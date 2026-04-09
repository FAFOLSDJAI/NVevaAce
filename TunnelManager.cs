using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Authentication;
using System.IO.Compression;

namespace NVevaAce
{
    /// <summary>
    /// 隧道管理器 - 负责管理所有隧道连接
    /// 参考 frp 的 C/S 架构设计
    /// </summary>
    public class TunnelManager : IDisposable
    {
        private readonly ILogger _logger;
        private readonly AppConfig _config;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly List<Task> _runningTasks = new List<Task>();
        private readonly Dictionary<int, TcpListener> _localListeners = new Dictionary<int, TcpListener>();
        private readonly ConcurrentDictionary<string, TcpClient> _clientPool = new ConcurrentDictionary<string, TcpClient>();
        
        private bool _isRunning = false;
        private readonly object _lock = new object();
        private TcpListener? _localListener;
        private int _heartbeatCount = 0;
        private readonly int _heartbeatInterval = 30; // 秒
        private readonly int _heartbeatTimeout = 90;  // 秒
        private DateTime _lastHeartbeatTime = DateTime.Now;

        public TunnelManager(ILogger logger, AppConfig? config = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? AppConfig.Load();
            InitializePool();
        }

        /// <summary>
        /// 初始化连接池
        /// </summary>
        private void InitializePool()
        {
            var poolSize = _config.PoolCount;
            _logger.Log($"初始化连接池，大小：{poolSize}");
        }

        /// <summary>
        /// 从配置文件加载隧道配置
        /// </summary>
        private List<TunnelConfig> LoadTunnelsFromConfig()
        {
            return _config.Tunnels;
        }

        /// <summary>
        /// 启动隧道服务
        /// </summary>
        public void StartTunnel()
        {
            lock (_lock)
            {
                if (_isRunning)
                {
                    _logger.Log("隧道已在运行");
                    return;
                }

                try
                {
                    _cts.Dispose();
                    _cts = new CancellationTokenSource();
                    
                    var tunnels = LoadTunnelsFromConfig();
                    
                    foreach (var tunnel in tunnels)
                    {
                        var listener = new TcpListener(IPAddress.Any, tunnel.LocalPort);
                        listener.Start();
                        _localListeners[tunnel.LocalPort] = listener;
                        _localListener = listener;
                        _logger.Log($"开始监听本地端口 {tunnel.LocalPort}");
                        
                        // 启动心跳检测
                        var heartbeatTask = Task.Run(() => HeartbeatCheckAsync(_cts.Token), _cts.Token);
                        _runningTasks.Add(heartbeatTask);
                    }
                    
                    _isRunning = true;
                    StartHeartbeatTimer();
                    _logger.Log($"内网穿透已启动，共 {tunnels.Count} 个隧道");
                }
                catch (Exception ex)
                {
                    _logger.Log($"启动隧道失败：{ex.Message}");
                    StopTunnel();
                    throw;
                }
            }
        }

        /// <summary>
        /// 启动心跳定时器
        /// </summary>
        private void StartHeartbeatTimer()
        {
            Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(_heartbeatInterval * 1000, _cts.Token);
                        SendHeartbeat();
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        _logger.Log($"心跳检测异常：{ex.Message}");
                    }
                }
            }, _cts.Token);
        }

        /// <summary>
        /// 发送心跳
        /// </summary>
        private void SendHeartbeat()
        {
            _heartbeatCount++;
            _lastHeartbeatTime = DateTime.Now;
            _logger.Log($"[心跳 #{_heartbeatCount}] 连接正常");
        }

        /// <summary>
        /// 心跳检测
        /// </summary>
        private async Task HeartbeatCheckAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, ct);
                    var timeout = DateTime.Now.AddSeconds(-_heartbeatTimeout);
                    
                    if (_lastHeartbeatTime < timeout)
                    {
                        _logger.Log("心跳超时，尝试重新连接...");
                        // 实现重连逻辑
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.Log($"心跳检测异常：{ex.Message}");
                }
            }
        }

        /// <summary>
        /// 停止隧道服务
        /// </summary>
        public void StopTunnel()
        {
            lock (_lock)
            {
                if (!_isRunning) return;

                _isRunning = false;
                _logger.Log("正在停止内网穿透...");

                try
                {
                    _cts.Cancel();
                    
                    foreach (var listener in _localListeners.Values)
                    {
                        listener.Stop();
                    }
                    
                    Task.WhenAll(_runningTasks.ToArray()).Wait(TimeSpan.FromSeconds(5));
                    
                    _localListeners.Clear();
                    _runningTasks.Clear();
                    _logger.Log("内网穿透已停止");
                }
                catch (Exception ex)
                {
                    _logger.Log($"停止隧道时出错：{ex.Message}");
                }
                finally
                {
                    _cts.Dispose();
                }
            }
        }

        /// <summary>
        /// 接受客户端连接
        /// </summary>
        private async Task AcceptClientsAsync(int localPort, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    TcpClient client = null;
                    try
                    {
                        if (!_localListeners.ContainsKey(localPort))
                            break;
                            
                        client = await _localListeners[localPort].AcceptTcpClientAsync().ConfigureAwait(false);
                        
                        if (!ct.IsCancellationRequested)
                        {
                            _logger.Log($"接受客户端连接：{client.Client.RemoteEndPoint}");
                            var handleTask = Task.Run(() => HandleClientAsync(client, ct), ct);
                            _runningTasks.Add(handleTask);
                        }
                    }
                    catch (SocketException) when (ct.IsCancellationRequested) { break; }
                    catch (ObjectDisposedException) when (ct.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        if (!ct.IsCancellationRequested)
                            _logger.Log($"接受连接时出错：{ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    _logger.Log($"接受连接循环异常：{ex.Message}");
            }
        }

        /// <summary>
        /// 处理客户端连接
        /// </summary>
        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            TcpClient remoteClient = null;
            NetworkStream clientStream = null;
            NetworkStream remoteStream = null;

            try
            {
                string remoteHost = _config.ServerAddr;
                int remotePort = _config.ServerPort;

                remoteClient = new TcpClient();
                await remoteClient.ConnectAsync(remoteHost, remotePort).ConfigureAwait(false);
                
                clientStream = client.GetStream();
                remoteStream = remoteClient.GetStream();
                
                _logger.Log($"建立隧道：{client.Client.RemoteEndPoint} <-> {remoteHost}:{remotePort}");

                var clientToRemote = CopyStreamAsync(clientStream, remoteStream, ct, "客户端->远程");
                var remoteToClient = CopyStreamAsync(remoteStream, clientStream, ct, "远程->客户端");
                
                await Task.WhenAll(clientToRemote, remoteToClient).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    _logger.Log($"处理客户端时出错：{ex.Message}");
            }
            finally
            {
                clientStream?.Dispose();
                remoteStream?.Dispose();
                client?.Dispose();
                remoteClient?.Dispose();
                _logger.Log($"连接已关闭：{client?.Client?.RemoteEndPoint}");
            }
        }

        /// <summary>
        /// 双向数据复制
        /// </summary>
        private async Task CopyStreamAsync(Stream input, Stream output, CancellationToken ct, string direction)
        {
            try
            {
                var buffer = new byte[81920];
                int bytesRead;
                
                while ((bytesRead = await input.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                {
                    if (ct.IsCancellationRequested) break;
                    
                    await output.WriteAsync(buffer, 0, bytesRead, ct).ConfigureAwait(false);
                    await output.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (IOException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    _logger.Log($"{direction} 数据传输错误：{ex.Message}");
            }
        }

        public void Dispose()
        {
            StopTunnel();
            _cts.Dispose();
        }
    }

    /// <summary>
    /// 日志接口
    /// </summary>
    public interface ILogger
    {
        void Log(string message);
    }
}
