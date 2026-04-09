using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NVevaAce
{
    /// <summary>
    /// 控制连接管理器 - 负责与服务端的认证和心跳
    /// 参考 frp 的控制连接设计，实现认证、心跳和状态管理
    /// </summary>
    public class ControlConnection : IDisposable
    {
        private readonly ILogger _logger;
        private readonly AppConfig _config;
        private TcpClient? _controlConnection;
        private bool _isConnected = false;
        private bool _disposed = false;
        private CancellationTokenSource? _heartbeatCts;
        private readonly object _lock = new object();

        // 连接状态
        public bool IsConnected => _isConnected;
        public DateTime? LastHeartbeatTime { get; private set; }

        // 事件
        public event EventHandler<ConnectionEventArgs>? OnConnect;
        public event EventHandler<ConnectionEventArgs>? OnDisconnect;
        public event EventHandler<ConnectionEventArgs>? OnReconnect;

        public ControlConnection(ILogger logger, AppConfig config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            LastHeartbeatTime = DateTime.Now;
        }

        /// <summary>
        /// 连接到 frp 服务器并认证
        /// </summary>
        public async Task ConnectAsync(CancellationToken ct = default)
        {
            if (_isConnected)
            {
                _logger.Log("[控制连接] 已连接");
                return;
            }

            try
            {
                _logger.Log($"[控制连接] 准备连接到 {_config.ServerAddr}:{_config.ServerPort}");

                // 建立 TCP 连接
                _controlConnection = new TcpClient();
                _controlConnection.ReceiveTimeout = 30000;
                _controlConnection.SendTimeout = 30000;

                await _controlConnection.ConnectAsync(_config.ServerAddr, _config.ServerPort, ct)
                    .ConfigureAwait(false);

                _logger.Log("[控制连接] TCP 连接建立");

                // 发送认证信息
                await SendAuthAsync(ct).ConfigureAwait(false);

                // 启动心跳
                StartHeartbeat(ct);

                _isConnected = true;
                LastHeartbeatTime = DateTime.Now;

                _logger.Log("[控制连接] 认证成功");
                OnConnect?.Invoke(this, new ConnectionEventArgs { Status = "connected" });
            }
            catch (Exception ex)
            {
                _logger.Log($"[控制连接] 连接失败：{ex.Message}");
                _isConnected = false;
                throw;
            }
        }

        /// <summary>
        /// 发送认证信息到服务器
        /// </summary>
        private async Task SendAuthAsync(CancellationToken ct)
        {
            var authMessage = new
            {
                type = "auth",
                version = "0.2.0",
                token = _config.Token,
                timestamp = DateTimeOffset.Now.ToUnixTimeSeconds(),
                clientInfo = new
                {
                    platform = Environment.OSVersion.Platform,
                    version = "0.2.0",
                    protocol = _config.Protocol
                }
            };

            var json = JsonSerializer.Serialize(authMessage);
            var data = Encoding.UTF8.GetBytes(json + "\n");

            using (var stream = _controlConnection.GetStream())
            {
                await stream.WriteAsync(data, 0, data.Length, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);

                // 等待服务器响应
                var responseBuffer = new byte[4096];
                var responseLength = await stream.ReadAsync(data, 0, responseBuffer.Length, ct)
                    .ConfigureAwait(false);

                if (responseLength > 0)
                {
                    var responseStr = Encoding.UTF8.GetString(responseBuffer, 0, responseLength);
                    var response = JsonSerializer.Deserialize<AuthResponse>(responseStr);

                    if (response?.status != "ok")
                    {
                        throw new Exception($"认证失败：{response?.message ?? "未知错误"}");
                    }
                }
            }
        }

        /// <summary>
        /// 启动心跳
        /// </summary>
        private void StartHeartbeat(CancellationToken ct)
        {
            _heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            Task.Run(async () =>
            {
                while (!_heartbeatCts.Token.IsCancellationRequested && _isConnected)
                {
                    try
                    {
                        await Task.Delay(_config.HeartbeatInterval * 1000, _heartbeatCts.Token);
                        await SendHeartbeatAsync(_heartbeatCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        _logger.Log($"[心跳] 发送失败：{ex.Message}");
                        _isConnected = false;
                        OnDisconnect?.Invoke(this, new ConnectionEventArgs { Status = "disconnected" });
                        break;
                    }
                }
            }, ct);
        }

        /// <summary>
        /// 发送心跳
        /// </summary>
        private async Task SendHeartbeatAsync(CancellationToken ct)
        {
            if (!_isConnected || _controlConnection == null) return;

            try
            {
                var heartbeat = new
                {
                    type = "heartbeat",
                    timestamp = DateTimeOffset.Now.ToUnixTimeSeconds()
                };

                var json = JsonSerializer.Serialize(heartbeat);
                var data = Encoding.UTF8.GetBytes(json + "\n");

                using (var stream = _controlConnection.GetStream())
                {
                    await stream.WriteAsync(data, 0, data.Length, ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                }

                LastHeartbeatTime = DateTime.Now;
                _logger.Log($"[心跳] 正常 (延迟：{(DateTime.Now - LastHeartbeatTime.Value).TotalMilliseconds}ms)");
            }
            catch (Exception ex)
            {
                _logger.Log($"[心跳] 异常：{ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 检查工作连接（用于传输数据）
        /// </summary>
        public async Task<TcpClient> CreateWorkConnectionAsync(CancellationToken ct = default)
        {
            if (!_isConnected)
            {
                throw new InvalidOperationException("控制连接未建立");
            }

            var workClient = new TcpClient();
            await workClient.ConnectAsync(_config.ServerAddr, _config.ServerPort, ct)
                .ConfigureAwait(false);

            _logger.Log("[工作连接] 新建工作连接");
            return workClient;
        }

        /// <summary>
        /// 检查连接健康状态
        /// </summary>
        public bool CheckHealth()
        {
            if (!_isConnected || _controlConnection == null) return false;

            var timeout = DateTime.Now.AddSeconds(-_config.HeartbeatTimeout);
            return LastHeartbeatTime > timeout;
        }

        /// <summary>
        /// 重连控制连接
        /// </summary>
        public async Task ReconnectAsync(CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (_disposed) return;
            }

            _logger.Log("[控制连接] 开始重连...");
            OnDisconnect?.Invoke(this, new ConnectionEventArgs { Status = "reconnecting" });

            Disconnect();
            await ConnectAsync(ct);

            OnReconnect?.Invoke(this, new ConnectionEventArgs { Status = "reconnected" });
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        public void Disconnect()
        {
            _heartbeatCts?.Cancel();
            _controlConnection?.Close();
            _controlConnection?.Dispose();
            _controlConnection = null;
            _isConnected = false;
            _logger.Log("[控制连接] 已断开");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Disconnect();
            _heartbeatCts?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// 认证响应
    /// </summary>
    public class AuthResponse
    {
        public string? status { get; set; }
        public string? message { get; set; }
        public string? sessionId { get; set; }
    }

    /// <summary>
    /// 连接事件参数
    /// </summary>
    public class ConnectionEventArgs : EventArgs
    {
        public string Status { get; set; } = "";
    }
}
