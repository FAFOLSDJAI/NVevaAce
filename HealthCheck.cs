using System;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NVevaAce
{
    /// <summary>
    /// 服务健康检查器 - 检查后端服务是否可用
    /// 参考 frp 的健康检查机制
    /// </summary>
    public class HealthChecker : IDisposable
    {
        private readonly ILogger _logger;
        private readonly TunnelConfig _tunnelConfig;
        private readonly string _serverAddr;
        private readonly int _serverPort;
        private bool _isHealthy = false;
        private bool _disposed = false;
        private CancellationTokenSource? _checkCts;
        private readonly HttpClient _httpClient = new HttpClient();

        public bool IsHealthy => _isHealthy;
        public int ConsecutiveFailures { get; private set; }
        public DateTime? LastCheckTime { get; private set; }
        public DateTime? LastSuccessTime { get; private set; }

        public event EventHandler<HealthChangedEventArgs>? OnHealthChanged;

        public HealthChecker(ILogger logger, TunnelConfig tunnelConfig, string serverAddr, int serverPort)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _tunnelConfig = tunnelConfig ?? throw new ArgumentNullException(nameof(tunnelConfig));
            _serverAddr = serverAddr ?? throw new ArgumentNullException(nameof(serverAddr));
            _serverPort = serverPort;
            _httpClient.Timeout = TimeSpan.FromSeconds(5);
        }

        /// <summary>
        /// 启动健康检查
        /// </summary>
        public void StartHealthCheck(int intervalSeconds = 10)
        {
            _checkCts = new CancellationTokenSource();

            Task.Run(async () =>
            {
                while (!_checkCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(intervalSeconds * 1000, _checkCts.Token);
                        await CheckHealthAsync(_checkCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        _logger.Log($"[健康检查] 检查异常：{ex.Message}");
                    }
                }
            }, _checkCts.Token);
        }

        /// <summary>
        /// 执行健康检查
        /// </summary>
        private async Task CheckHealthAsync(CancellationToken ct)
        {
            LastCheckTime = DateTime.Now;
            bool wasHealthy = _isHealthy;

            try
            {
                switch (_tunnelConfig.Protocol?.ToLower())
                {
                    case "http":
                    case "https":
                        await CheckHttpHealthAsync(ct).ConfigureAwait(false);
                        break;
                    case "tcp":
                    default:
                        await CheckTcpHealthAsync(ct).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex)
            {
                _isHealthy = false;
                ConsecutiveFailures++;
                _logger.Log($"[健康检查] 服务不健康：{ex.Message}");
            }

            // 状态变化时触发事件
            if (_isHealthy != wasHealthy)
            {
                OnHealthChanged?.Invoke(this, new HealthChangedEventArgs
                {
                    IsHealthy = _isHealthy,
                    Timestamp = DateTime.Now
                });

                if (_isHealthy)
                {
                    LastSuccessTime = DateTime.Now;
                    ConsecutiveFailures = 0;
                    _logger.Log("[健康检查] 服务恢复健康");
                }
            }
        }

        /// <summary>
        /// TCP 健康检查
        /// </summary>
        private async Task CheckTcpHealthAsync(CancellationToken ct)
        {
            using (var client = new TcpClient())
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;

                await client.ConnectAsync(_serverAddr, _serverPort, ct)
                    .ConfigureAwait(false);

                _isHealthy = client.Connected;
            }
        }

        /// <summary>
        /// HTTP 健康检查
        /// </summary>
        private async Task CheckHttpHealthAsync(CancellationToken ct)
        {
            var url = $"{_tunnelConfig.Protocol}://{_serverAddr}:{_serverPort}{_tunnelConfig.HealthCheckPath ?? "/"}";

            var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
            _isHealthy = response.IsSuccessStatusCode;

            if (!_isHealthy)
            {
                _logger.Log($"[健康检查] HTTP 检查失败：{(int)response.StatusCode} {response.StatusCode}");
            }
        }

        /// <summary>
        /// 获取健康状态摘要
        /// </summary>
        public string GetStatus()
        {
            return $"健康状态：{(_isHealthy ? "健康" : "不健康")}, " +
                   $"连续失败：{ConsecutiveFailures}, " +
                   $"最后检查：{LastCheckTime?.ToString("HH:mm:ss") ?? "从未"}, " +
                   $"最后成功：{LastSuccessTime?.ToString("HH:mm:ss") ?? "从未"}";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _checkCts?.Cancel();
            _checkCts?.Dispose();
            _httpClient.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// 健康变化事件参数
    /// </summary>
    public class HealthChangedEventArgs : EventArgs
    {
        public bool IsHealthy { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
