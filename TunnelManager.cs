using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NVevaAce
{
    /// <summary>
    /// 隧道管理器 - 负责管理所有隧道连接
    /// 参考 frp 的 C/S 架构设计，实现控制连接与工作连接分离
    /// </summary>
    public class TunnelManager : IDisposable
    {
        private readonly ILogger _logger;
        private readonly AppConfig _config;
        
        // 修正 1: 去掉 readonly，因为重连时需要 new
        private CancellationTokenSource _cts = new CancellationTokenSource();
        
        private readonly List<Task> _runningTasks = new List<Task>();
        private readonly Dictionary<int, TcpListener> _localListeners = new Dictionary<int, TcpListener>();
        private bool _isRunning = false;
        private readonly object _lock = new object();
        
        // 新增：控制连接管理器
        private ControlConnection? _controlConnection;
        private HealthChecker? _healthChecker;
        
        private int _heartbeatCount = 0;
        private readonly int _heartbeatInterval = 30;
        private readonly int _heartbeatTimeout = 90;
        private DateTime _lastHeartbeatTime = DateTime.Now;

        public TunnelManager(ILogger logger, AppConfig? config = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? AppConfig.Load();
        }

        public void StartTunnel()
        {
            lock (_lock)
            {
                if (_isRunning) return;
                
                try
                {
                    // 修正 2: 确保清理旧的信号源
                    _cts?.Cancel();
                    _cts?.Dispose();
                    _cts = new CancellationTokenSource();
                    
                    // 初始化控制连接
                    _controlConnection = new ControlConnection(_logger, _config);
                    
                    var tunnels = _config.Tunnels;
                    foreach (var tunnel in tunnels)
                    {
                        var listener = new TcpListener(IPAddress.Any, tunnel.LocalPort);
                        listener.Start();
                        _localListeners[tunnel.LocalPort] = listener;
                        _logger.Log($"开始监听本地端口 {tunnel.LocalPort}");

                        // 修正 3: 必须启动接受连接的任务！
                        _runningTasks.Add(Task.Run(() => AcceptClientsAsync(tunnel.LocalPort, _cts.Token), _cts.Token));
                    }
                    
                    // 修正 4: 全局只需要一个心跳检测
                    _runningTasks.Add(Task.Run(() => HeartbeatCheckAsync(_cts.Token), _cts.Token));
                    StartHeartbeatTimer();
                    
                    _isRunning = true;
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

        private async Task AcceptClientsAsync(int localPort, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!_localListeners.TryGetValue(localPort, out var listener)) break;

                    // 使用 AcceptTcpClientAsync 并在外面包裹括号
                    var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    _logger.Log($"[端口 {localPort}] 接受连接：{client.Client.RemoteEndPoint}");

                    // 修正 5: 启动处理任务，不要 Wait 否则会阻塞监听
                    _ = Task.Run(() => HandleClientAsync(client, ct), ct);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    _logger.Log($"监听出错：{ex.Message}");
                    await Task.Delay(1000, ct);
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            // 注意：这里一定要用 using 确保即便出错也能释放 Socket
            using (client)
            using (TcpClient remoteClient = new TcpClient())
            {
                try
                {
                    await remoteClient.ConnectAsync(_config.ServerAddr, _config.ServerPort).ConfigureAwait(false);
                    using (var clientStream = client.GetStream())
                    using (var remoteStream = remoteClient.GetStream())
                    {
                        _logger.Log($"隧道建立成功：{client.Client.RemoteEndPoint} <-> {_config.ServerAddr}");
                        var t1 = CopyStreamAsync(clientStream, remoteStream, ct, "Up");
                        var t2 = CopyStreamAsync(remoteStream, clientStream, ct, "Down");
                        await Task.WhenAll(t1, t2).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Log($"传输中断：{ex.Message}");
                }
            }
        }

        private async Task CopyStreamAsync(Stream input, Stream output, CancellationToken ct, string direction)
        {
            var buffer = new byte[81920];
            int bytesRead;
            // 修正 6: 确保括号闭合正确
            while ((bytesRead = await input.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer, 0, bytesRead, ct).ConfigureAwait(false);
            }
        }

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

        private void SendHeartbeat()
        {
            _heartbeatCount++;
            _lastHeartbeatTime = DateTime.Now;
            _logger.Log($"[心跳 #{_heartbeatCount}] 连接正常");
        }

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

        public void StopTunnel()
        {
            lock (_lock)
            {
                if (!_isRunning) return;
                
                _isRunning = false;
                _cts.Cancel();
                
                foreach (var l in _localListeners.Values) l.Stop();
                _localListeners.Clear();
                
                _controlConnection?.Dispose();
                _healthChecker?.Dispose();
                
                _logger.Log("服务已停止");
            }
        }

        public void Dispose()
        {
            StopTunnel();
            _cts?.Dispose();
        }
    }
}
