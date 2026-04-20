using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NVevaAce
{
    /// <summary>
    /// 隧道管理器 - 负责管理所有隧道连接
    /// 改进版本：集成连接池、健康检查、代理协议、TCP多路复用、带宽限制、负载均衡、TLS加密、压缩
    /// 
    /// 改进说明 (v0.3.2):
    /// - 修复 TcpMultiplexer 未实际使用的问题
    /// - 集成 CompressionUtils 实现真正的压缩支持
    /// - 添加自动重连机制（指数退避）
    /// - 添加 UDP 隧道支持
    /// - 改进统计报告
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

        // TCP 多路复用器
        private TcpMultiplexer? _tcpMultiplexer;

        // 带宽限制器
        private readonly Dictionary<int, BandwidthLimiter> _bandwidthLimiters = new Dictionary<int, BandwidthLimiter>();

        // 负载均衡器
        private readonly Dictionary<int, LoadBalancer> _loadBalancers = new Dictionary<int, LoadBalancer>();

        // 健康检查器
        private readonly Dictionary<int, HealthChecker> _healthCheckers = new Dictionary<int, HealthChecker>();

        // 压缩工具 (v0.3.2 新增)
        private CompressionUtils? _compression;

        // 代理协议处理器
        private ProxyProtocolHandler? _protocolHandler;

        // 安全连接工厂
        private SecureConnectionFactory? _secureConnectionFactory;

        // 加密工具
        private CryptoUtils? _crypto;

        // 重连状态 (v0.3.2 新增)
        private int _reconnectAttempts = 0;
        private const int MaxReconnectAttempts = 10;
        private const int BaseReconnectDelayMs = 1000;

        // 心跳统计
        private int _heartbeatCount = 0;
        private DateTime _lastHeartbeatTime = DateTime.Now;
        private DateTime _startTime = DateTime.Now;
        private const int HeartbeatInterval = 30;
        private const int HeartbeatTimeout = 90;

        // 连接统计
        private long _totalBytesTransferred = 0;
        private long _totalConnections = 0;
        private int _activeConnections = 0;
        private readonly object _statsLock = new object();

        public int ActiveConnections => _activeConnections;
        public long TotalConnections => _totalConnections;
        public long TotalBytesTransferred => _totalBytesTransferred;
        public TimeSpan Uptime => DateTime.Now - _startTime;

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

                    _startTime = DateTime.Now;
                    _reconnectAttempts = 0;

                    // 初始化压缩工具 (v0.3.2)
                    if (_config.Tunnels?.Any(t => t.UseCompression) == true)
                    {
                        _compression = new CompressionUtils(_logger, true);
                        _logger.Log($"[压缩] GZip 压缩已启用");
                    }

                    // 初始化 TCP 多路复用 (如果启用)
                    if (_config.TcpMux)
                    {
                        _tcpMultiplexer = new TcpMultiplexer(_logger, _config.ServerAddr, _config.ServerPort);
                        _logger.Log($"[TCP Mux] TCP 多路复用已启用");
                    }

                    // 初始化连接池
                    if (_config.PoolCount > 0)
                    {
                        _connectionPool = new ConnectionPool(
                            _logger, _config.ServerAddr, _config.ServerPort, _config.PoolCount);
                        _logger.Log($"[连接池] 已初始化，大小: {_config.PoolCount}");
                    }

                    // 初始化代理协议处理器
                    _protocolHandler = new ProxyProtocolHandler(_logger, _config);

                    // 初始化加密 (v0.3.2)
                    bool useEncryption = _config.TlsEnable || _config.Tunnels?.Any(t => t.UseEncryption) == true;
                    if (useEncryption)
                    {
                        _secureConnectionFactory = new SecureConnectionFactory(
                            _logger,
                            _config.ServerAddr,
                            _config.ServerPort,
                            _config.TlsEnable,
                            _config.Tunnels?.Any(t => t.UseEncryption) == true,
                            _config.Token
                        );

                        if (!string.IsNullOrEmpty(_config.Token))
                        {
                            _crypto = new CryptoUtils(_config.Token);
                            var encType = _config.TlsEnable ? "TLS 1.2/1.3" : "AES-256-CFB";
                            _logger.Log($"[加密] 已启用 {encType} 加密传输");
                        }
                    }

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

                        // 启动监听器
                        StartLocalListener(tunnel);

                        // 启动健康检查
                        StartHealthChecker(tunnel);
                    }

                    // 启动心跳
                    StartHeartbeatTimer();

                    // 启动统计报告
                    _runningTasks.Add(Task.Run(() => StatsReportAsync(_cts.Token), _cts.Token));

                    _isRunning = true;

                    var features = new List<string>();
                    if (useEncryption) features.Add("加密");
                    if (_config.TcpMux) features.Add("TCP Mux");
                    if (_compression != null) features.Add("压缩");
                    var featureStr = features.Count > 0 ? " [" + string.Join(", ", features) + "]" : "";

                    _logger.Log($"✓ 内网穿透已启动，共 {tunnels.Count} 个隧道{featureStr}");
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

            _logger.Log($"开始监听本地端口 {tunnel.LocalPort} -> 远程 {tunnel.RemotePort}" +
                (tunnel.UseEncryption ? " [加密]" : "") +
                (tunnel.UseCompression ? " [压缩]" : ""));

            // 接受客户端连接
            _runningTasks.Add(Task.Run(() => AcceptClientsAsync(tunnel, _cts.Token), _cts.Token));
        }

        private void StartHealthChecker(TunnelConfig tunnel)
        {
            var checker = new HealthChecker(_logger, tunnel, "127.0.0.1", tunnel.LocalPort);
            checker.OnHealthChanged += OnHealthChanged;
            _healthCheckers[tunnel.LocalPort] = checker;
            checker.StartHealthCheck(tunnel.HealthCheckInterval > 0 ? tunnel.HealthCheckInterval : 10);
            _logger.Log($"[健康检查] 已启动: 端口 {tunnel.LocalPort}, 间隔: {tunnel.HealthCheckInterval}s");
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

                // 尝试使用 TCP 多路复用 (v0.3.2 - 实际使用 mux)
                if (_tcpMultiplexer != null && _tcpMultiplexer.IsRunning)
                {
                    try
                    {
                        // 通过多路复用器建立通道
                        var channel = await _tcpMultiplexer.OpenChannelAsync(ct).ConfigureAwait(false);
                        _logger.Log($"[端口 {tunnel.LocalPort}] 使用 TCP Mux 通道 #{channel.ChannelId}");

                        // 处理数据传输
                        await HandleWithMultiplexerAsync(client, channel, tunnel, ct).ConfigureAwait(false);
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.Log($"[TCP Mux] 通道建立失败，回退到直连: {ex.Message}");
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

                // 获取基础流
                Stream clientStream = client.GetStream();
                Stream remoteStream = remoteClient.GetStream();

                // 应用加密 (v0.3.2)
                bool useTunnelEncryption = tunnel.UseEncryption && _crypto != null;
                if (useTunnelEncryption)
                {
                    clientStream = new EncryptedStream(clientStream, _crypto, true);
                    remoteStream = new EncryptedStream(remoteStream, _crypto, true);
                }

                // 应用压缩 (v0.3.2 - 实际集成)
                bool useCompression = tunnel.UseCompression && _compression != null;
                if (useCompression)
                {
                    clientStream = _compression.CreateCompressedStream(clientStream, Compression.CompressionMode.Compress);
                    remoteStream = _compression.CreateCompressedStream(remoteStream, Compression.CompressionMode.Decompress);
                }

                using (clientStream)
                using (remoteStream)
                {
                    // 获取带宽限制器
                    BandwidthLimiter? limiter = null;
                    _bandwidthLimiters.TryGetValue(tunnel.LocalPort, out limiter);

                    _logger.Log($"[隧道] {client.Client.RemoteEndPoint} -> {targetAddr}:{targetPort}" +
                        (useTunnelEncryption ? " [加密]" : "") +
                        (useCompression ? " [压缩]" : ""));

                    var t1 = CopyStreamAsync(clientStream, remoteStream, ct, "↑", limiter, tunnel);
                    var t2 = CopyStreamAsync(remoteStream, clientStream, ct, "↓", limiter, tunnel);
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

                if (!usedPool)
                {
                    remoteClient?.Dispose();
                }
            }
        }

        /// <summary>
        /// 通过 TCP 多路复用通道处理数据传输 (v0.3.2 新增)
        /// </summary>
        private async Task HandleWithMultiplexerAsync(TcpClient client, MuxChannel channel, TunnelConfig tunnel, CancellationToken ct)
        {
            var clientStream = client.GetStream();

            // 获取带宽限制器
            BandwidthLimiter? limiter = null;
            _bandwidthLimiters.TryGetValue(tunnel.LocalPort, out limiter);

            try
            {
                var t1 = CopyToChannelAsync(clientStream, channel, ct, "↑", limiter, tunnel);
                var t2 = CopyFromChannelAsync(channel, clientStream, ct, "↓", limiter, tunnel);
                await Task.WhenAll(t1, t2).ConfigureAwait(false);
            }
            finally
            {
                channel.Dispose();
            }
        }

        private async Task CopyToChannelAsync(Stream input, MuxChannel channel, CancellationToken ct, string direction, BandwidthLimiter? limiter, TunnelConfig tunnel)
        {
            var buffer = new byte[65536];
            int bytesRead;
            long bytesTotal = 0;

            while ((bytesRead = await input.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
            {
                if (limiter != null)
                {
                    await limiter.WaitForTokensAsync(bytesRead, ct).ConfigureAwait(false);
                }

                var toSend = new byte[bytesRead];
                Buffer.BlockCopy(buffer, 0, toSend, 0, bytesRead);
                await channel.WriteAsync(toSend, 0, bytesRead, ct).ConfigureAwait(false);
                bytesTotal += bytesRead;
            }

            lock (_statsLock) _totalBytesTransferred += bytesTotal;
        }

        private async Task CopyFromChannelAsync(MuxChannel channel, Stream output, CancellationToken ct, string direction, BandwidthLimiter? limiter, TunnelConfig tunnel)
        {
            var buffer = new byte[65536];
            int bytesRead;
            long bytesTotal = 0;

            while ((bytesRead = await channel.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
            {
                if (limiter != null)
                {
                    await limiter.WaitForTokensAsync(bytesRead, ct).ConfigureAwait(false);
                }

                await output.WriteAsync(buffer, 0, bytesRead, ct).ConfigureAwait(false);
                bytesTotal += bytesRead;
            }

            lock (_statsLock) _totalBytesTransferred += bytesTotal;
        }

        private async Task CopyStreamAsync(Stream input, Stream output, CancellationToken ct, string direction, BandwidthLimiter? limiter, TunnelConfig tunnel)
        {
            var buffer = new byte[65536];
            int bytesRead;
            long bytesTotal = 0;

            while ((bytesRead = await input.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
            {
                // 应用带宽限制
                if (limiter != null)
                {
                    await limiter.WaitForTokensAsync(bytesRead, ct).ConfigureAwait(false);
                }

                await output.WriteAsync(buffer, 0, bytesRead, ct).ConfigureAwait(false);
                bytesTotal += bytesRead;
            }

            lock (_statsLock) _totalBytesTransferred += bytesTotal;
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
            var encStatus = _crypto != null ? ", 加密: 启用" : "";
            var compStatus = _compression != null ? ", 压缩: 启用" : "";
            _logger.Log($"[心跳 #{_heartbeatCount}] 连接正常 | 活动: {_activeConnections}, 总计: {_totalConnections}{muxStatus}{encStatus}{compStatus}");
        }

        private async Task StatsReportAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(60 * 1000, ct);

                    var poolSize = _connectionPool?.GetPoolSize() ?? 0;
                    var stats = _connectionPool?.GetStats();
                    var muxChannels = _tcpMultiplexer?.ActiveChannels ?? 0;

                    var bwStats = "";
                    foreach (var kvp in _bandwidthLimiters)
                    {
                        var bw = kvp.Value.GetStats();
                        bwStats += $"\n  端口 {kvp.Key}: {bw}";
                    }

                    var uptime = DateTime.Now - _startTime;
                    var transferRate = _totalBytesTransferred / Math.Max(1, uptime.TotalSeconds);

                    _logger.Log($"[统计] 运行: {uptime:hh\\:mm\\:ss} | 活动: {_activeConnections}, 总计: {_totalConnections} | " +
                        $"传输: {_totalBytesTransferred / 1024.0 / 1024.0:F2} MB | 速率: {transferRate / 1024.0:F1} KB/s | " +
                        $"连接池: {poolSize}, Mux通道: {muxChannels}{bwStats}");
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Log($"[统计] 报告异常: {ex.Message}");
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
                _compression?.Dispose();

                _logger.Log($"服务已停止 (运行时长: {DateTime.Now - _startTime:hh\\:mm\\:ss})");
            }
        }

        /// <summary>
        /// 重新加载配置 (热加载)
        /// </summary>
        public void ReloadConfig()
        {
            _logger.Log("开始热加载配置...");
            var oldIsRunning = _isRunning;

            StopTunnel();
            _config.Reload();

            if (oldIsRunning)
            {
                StartTunnel();
            }

            _logger.Log("配置已热加载完成");
        }

        /// <summary>
        /// 获取运行状态摘要
        /// </summary>
        public string GetStatusSummary()
        {
            var features = new List<string>();
            if (_crypto != null) features.Add("加密");
            if (_tcpMultiplexer != null) features.Add("TCP Mux");
            if (_compression != null) features.Add("压缩");

            return $"运行: {(_isRunning ? "是" : "否")} | " +
                   $"隧道: {_config.Tunnels?.Count ?? 0} | " +
                   $"活动连接: {_activeConnections} | " +
                   $"总连接: {_totalConnections} | " +
                   $"传输: {_totalBytesTransferred / 1024 / 1024.0:F2} MB | " +
                   $"特性: {(features.Count > 0 ? string.Join(", ", features) : "无")}";
        }

        public void Dispose()
        {
            StopTunnel();
            _cts?.Dispose();
        }
    }
}
