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
    /// 改进版本：集成连接池、健康检查、代理协议、TCP多路复用、带宽限制、负载均衡
    /// </summary>
    public class TunnelManager : IDisposable
    {
        private readonly ILogger _logger;
        private readonly AppConfig _config;
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly List<Task> _runningTasks = new List<Task>();
        private readonly Dictionary<int, TcpListener> _localListeners = new Dictionary<int, TcpListener>();
        private bool _isRunning = false;
        private readonly object _lock = new object();

        // 连接池
        private ConnectionPool? _connectionPool;

        // TCP 多路复用器 (新增)
        private TcpMultiplexer? _tcpMultiplexer;

        // 带宽限制器 (新增)
        private readonly Dictionary<int, BandwidthLimiter> _bandwidthLimiters = new Dictionary<int, BandwidthLimiter>();

        // 负载均衡器 (新增)
        private readonly Dictionary<int, LoadBalancer> _loadBalancers = new Dictionary<int, LoadBalancer>();

        // 健康检查器
        private readonly Dictionary<int, HealthChecker> _healthCheckers = new Dictionary<int, HealthChecker>();

        // 代理协议处理器
        private ProxyProtocolHandler? _protocolHandler;

        // 心跳统计
        private int _heartbeatCount = 0;
        private DateTime _lastHeartbeatTime = DateTime.Now;
        private const int HeartbeatInterval = 30;
        private const int HeartbeatTimeout = 90;

        // 连接统计
        private int _totalConnections = 0;
        private int _activeConnections = 0;
        private readonly object _statsLock = new object();

        public int ActiveConnections => _activeConnections;
        public int TotalConnections => _totalConnections;

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
                    // 清理旧资源
                    _cts?.Cancel();
                    _cts?.Dispose();
                    _cts = new CancellationTokenSource();

                    // 初始化 TCP 多路复用 (如果启用)
                    if (_config.TcpMux)
                    {
                        _tcpMultiplexer = new TcpMultiplexer(_logger, _config.ServerAddr, _config.ServerPort);
                        _logger.Log($"[改进] TCP 多路复用已启用");
                    }

                    // 初始化连接池
                    if (_config.PoolCount > 0)
                    {
                        _connectionPool = new ConnectionPool(
                            _logger,
                            _config.ServerAddr,
                            _config.ServerPort,
                            _config.PoolCount);
                        _logger.Log($"[改进] 连接池已初始化，大小: {_config.PoolCount}");
                    }

                    // 初始化代理协议处理器
                    _protocolHandler = new ProxyProtocolHandler(_logger, _config);

                    // 启动隧道
                    var tunnels = _config.Tunnels;
                    foreach (var tunnel in tunnels)
                    {
                        // 初始化带宽限制器
                        if (tunnel.BandwidthLimit > 0)
                        {
                            _bandwidthLimiters[tunnel.LocalPort] = new BandwidthLimiter(_logger, tunnel.BandwidthLimit);
                        }

                        // 初始化负载均衡器 (如果配置了多个后端)
                        if (tunnel.Backends != null && tunnel.Backends.Count > 1)
                        {
                            var lb = new LoadBalancer(_logger, ParseLoadBalanceStrategy(tunnel.LoadBalanceStrategy));
                            foreach (var backend in tunnel.Backends)
                            {
                                lb.AddBackend(backend.Address, backend.Port, backend.Weight);
                            }
                            _loadBalancers[tunnel.LocalPort] = lb;
                        }

                        StartLocalListener(tunnel);
                        StartHealthChecker(tunnel);
                    }

                    // 启动心跳
                    StartHeartbeatTimer();

                    // 启动统计报告
                    _runningTasks.Add(Task.Run(() => StatsReportAsync(_cts.Token), _cts.Token));

                    _isRunning = true;
                    _logger.Log($"✓ 内网穿透已启动，共 {tunnels.Count} 个隧道");
                }
                catch (Exception ex)
                {
                    _logger.Log($"启动隧道失败：{ex.Message}");
                    StopTunnel();
                    throw;
                }
            }
        }

        private LoadBalanceStrategy ParseLoadBalanceStrategy(string? strategy)
        {
            return strategy?.ToLower() switch
            {
                "least_connections" => LoadBalanceStrategy.LeastConnections,
                "weighted_round_robin" => LoadBalanceStrategy.WeightedRoundRobin,
                "random" => LoadBalanceStrategy.Random,
                _ => LoadBalanceStrategy.RoundRobin
            };
        }

        private void StartLocalListener(TunnelConfig tunnel)
        {
            var listener = new TcpListener(IPAddress.Any, tunnel.LocalPort);
            listener.Start();
            _localListeners[tunnel.LocalPort] = listener;

            // 禁用 Nagle 算法，降低延迟
            listener.Server.NoDelay = true;

            _logger.Log($"开始监听本地端口 {tunnel.LocalPort} -> 远程 {tunnel.RemotePort}");

            // 接受客户端连接
            _runningTasks.Add(Task.Run(() => AcceptClientsAsync(tunnel, _cts.Token), _cts.Token));
        }

        private void StartHealthChecker(TunnelConfig tunnel)
        {
            var checker = new HealthChecker(_logger, tunnel, "127.0.0.1", tunnel.LocalPort);
            checker.OnHealthChanged += OnHealthChanged;
            _healthCheckers[tunnel.LocalPort] = checker;
            checker.StartHealthCheck(tunnel.HealthCheckInterval > 0 ? tunnel.HealthCheckInterval : 10);
            _logger.Log($"[改进] 健康检查已启动: 端口 {tunnel.LocalPort}, 间隔: {tunnel.HealthCheckInterval}s");
        }

        private void OnHealthChanged(object? sender, HealthChangedEventArgs e)
        {
            _logger.Log($"[健康检查] 状态变更: {(e.IsHealthy ? "健康" : "不健康")} - {e.Timestamp:HH:mm:ss}");
        }

        private async Task AcceptClientsAsync(TunnelConfig tunnel, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!_localListeners.TryGetValue(tunnel.LocalPort, out var listener)) break;

                    var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    
                    // 禁用 Nagle 算法
                    client.NoDelay = true;

                    lock (_statsLock)
                    {
                        _totalConnections++;
                        _activeConnections++;
                    }

                    _logger.Log($"[端口 {tunnel.LocalPort}] 接受连接：{client.Client.RemoteEndPoint} (总计: {_totalConnections})");
                    
                    _ = Task.Run(() => HandleClientAsync(client, tunnel, ct), ct);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    _logger.Log($"监听出错：{ex.Message}");
                    await Task.Delay(1000, ct);
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, TunnelConfig tunnel, CancellationToken ct)
        {
            TcpClient? remoteClient = null;
            bool usedPool = false;

            try
            {
                // 确定目标地址
                string targetAddr = _config.ServerAddr;
                int targetPort = _config.ServerPort;

                // 如果配置了负载均衡，使用负载均衡选择后端
                if (_loadBalancers.TryGetValue(tunnel.LocalPort, out var lb))
                {
                    var backend = lb.SelectBackend();
                    if (backend != null)
                    {
                        targetAddr = backend.Address;
                        targetPort = backend.Port;
                        lb.IncrementConnections(targetAddr, targetPort);
                    }
                }

                // 尝试使用连接池
                if (_connectionPool != null)
                {
                    try
                    {
                        remoteClient = await _connectionPool.AcquireAsync(ct).ConfigureAwait(false);
                        usedPool = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.Log($"[连接池] 获取连接失败，使用直连: {ex.Message}");
                        remoteClient = new TcpClient();
                    }
                }
                else
                {
                    remoteClient = new TcpClient();
                }

                // 禁用 Nagle 算法
                remoteClient.NoDelay = true;

                await remoteClient.ConnectAsync(targetAddr, targetPort).ConfigureAwait(false);

                using (var clientStream = client.GetStream())
                using (var remoteStream = remoteClient.GetStream())
                {
                    _logger.Log($"[隧道] {client.Client.RemoteEndPoint} -> {targetAddr}:{targetPort}");

                    // 获取带宽限制器
                    BandwidthLimiter? limiter = null;
                    _bandwidthLimiters.TryGetValue(tunnel.LocalPort, out limiter);

                    var t1 = CopyStreamAsync(clientStream, remoteStream, ct, "↑", limiter);
                    var t2 = CopyStreamAsync(remoteStream, clientStream, ct, "↓", limiter);

                    await Task.WhenAll(t1, t2).ConfigureAwait(false);
                }

                // 归还连接到池
                if (usedPool && remoteClient?.Connected == true)
                {
                    _connectionPool?.Release(remoteClient);
                }

                // 减少负载均衡计数
                if (_loadBalancers.TryGetValue(tunnel.LocalPort, out var lb2))
                {
                    lb2.DecrementConnections(targetAddr, targetPort);
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"传输中断：{ex.Message}");
            }
            finally
            {
                lock (_statsLock)
                {
                    _activeConnections = Math.Max(0, _activeConnections - 1);
                }
            }
        }

        private async Task CopyStreamAsync(Stream input, Stream output, CancellationToken ct, string direction, BandwidthLimiter? limiter)
        {
            var buffer = new byte[65536];
            int bytesRead;

            while ((bytesRead = await input.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
            {
                // 应用带宽限制
                if (limiter != null)
                {
                    await limiter.WaitForTokensAsync(bytesRead, ct).ConfigureAwait(false);
                }

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
                        await Task.Delay(HeartbeatInterval * 1000, _cts.Token);
                        SendHeartbeat();
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
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

            var muxStatus = _tcpMultiplexer != null ? $", TCP Mux: {_tcpMultiplexer.ActiveChannels} 通道" : "";
            _logger.Log($"[心跳 #{_heartbeatCount}] 连接正常 | 活动连接: {_activeConnections}{muxStatus}");
        }

        private async Task StatsReportAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(60 * 1000, ct); // 每分钟报告一次

                    var poolSize = _connectionPool?.GetPoolSize() ?? 0;
                    var stats = _connectionPool?.GetStats();

                    // 带宽统计
                    var bwStats = "";
                    foreach (var kvp in _bandwidthLimiters)
                    {
                        var bw = kvp.Value.GetStats();
                        bwStats += $"\n  端口 {kvp.Key}: {bw}";
                    }

                    _logger.Log($"[统计] 活动连接: {_activeConnections}, 总计: {_totalConnections}, " +
                        $"连接池: {poolSize}, 创建数: {stats?.CreatedCount ?? 0}{bwStats}");
                }
                catch (OperationCanceledException)
                {
                    break;
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

                foreach (var l in _localListeners.Values)
                {
                    try { l.Stop(); } catch { }
                }
                _localListeners.Clear();

                foreach (var hc in _healthCheckers.Values)
                {
                    try { hc.Dispose(); } catch { }
                }
                _healthCheckers.Clear();

                foreach (var bw in _bandwidthLimiters.Values)
                {
                    try { bw.Dispose(); } catch { }
                }
                _bandwidthLimiters.Clear();

                foreach (var lb in _loadBalancers.Values)
                {
                    try { lb.Dispose(); } catch { }
                }
                _loadBalancers.Clear();

                _connectionPool?.Dispose();
                _tcpMultiplexer?.Dispose();

                _logger.Log("服务已停止");
            }
        }

        /// <summary>
        /// 重新加载配置
        /// </summary>
        public void ReloadConfig()
        {
            _logger.Log("开始重新加载配置...");
            StopTunnel();
            _config.Reload();
            StartTunnel();
            _logger.Log("配置已重新加载");
        }

        public void Dispose()
        {
            StopTunnel();
            _cts?.Dispose();
        }
    }
}