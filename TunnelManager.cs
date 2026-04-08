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
    public class TunnelManager : IDisposable
    {
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly List<Task> _runningTasks = new List<Task>();
        private TcpListener _localListener;
        private bool _isRunning = false;
        private readonly object _lock = new object();
        private string _remoteHost;
        private int _remotePort;
        private string _protocol;
        private string _authToken;
        private bool _useEncryption;
        private string _logLevel;
        private string _httpHost;
        private string _httpPath;
        private int _heartbeatTimeout;
        private int _poolCount;
        private string _user;
        private string _token;
        private bool _disableLogColor;
        private readonly ConcurrentDictionary<string, TcpClient> _clientPool = new ConcurrentDictionary<string, TcpClient>();
        private readonly List<TunnelConfig> _tunnels = new List<TunnelConfig>();

        public TunnelManager(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            LoadConfiguration();
        }

        private void LoadConfiguration()
        {
            try
            {
                if (!File.Exists("appsettings.json"))
                {
                    _logger.Log("appsettings.json not found, using default values");
                    return;
                }

                var json = File.ReadAllText("appsettings.json");
                var config = SimpleJson.DeserializeObject(json);
                var dict = (System.Collections.Generic.IDictionary<string, object>)config;

                // 读取基本配置
                if (dict.TryGetValue("RemoteHost", out var remoteHost))
                    _remoteHost = remoteHost?.ToString() ?? "tunnel.example.com";

                if (dict.TryGetValue("RemotePort", out var remotePort))
                    _remotePort = remotePort != null ? Convert.ToInt32(remotePort) : 443;

                if (dict.TryGetValue("Protocol", out var protocol))
                    _protocol = protocol?.ToString() ?? "tcp";

                if (dict.TryGetValue("AuthToken", out var authToken))
                    _authToken = authToken?.ToString() ?? "";

                if (dict.TryGetValue("UseEncryption", out var useEncryption))
                    _useEncryption = useEncryption != null && Convert.ToBoolean(useEncryption);

                if (dict.TryGetValue("LogLevel", out var logLevel))
                    _logLevel = logLevel?.ToString() ?? "Info";

                if (dict.TryGetValue("HttpHost", out var httpHost))
                    _httpHost = httpHost?.ToString() ?? "";

                if (dict.TryGetValue("HttpPath", out var httpPath))
                    _httpPath = httpPath?.ToString() ?? "/";

                if (dict.TryGetValue("HeartbeatTimeout", out var heartbeatTimeout))
                    _heartbeatTimeout = heartbeatTimeout != null ? Convert.ToInt32(heartbeatTimeout) : 60;

                if (dict.TryGetValue("PoolCount", out var poolCount))
                    _poolCount = poolCount != null ? Convert.ToInt32(poolCount) : 5;

                if (dict.TryGetValue("User", out var user))
                    _user = user?.ToString() ?? "";

                if (dict.TryGetValue("Token", out var token))
                    _token = token?.ToString() ?? "";

                if (dict.TryGetValue("DisableLogColor", out var disableLogColor))
                    _disableLogColor = disableLogColor != null && Convert.ToBoolean(disableLogColor);

                _logger.Log("Configuration loaded successfully");
            }
            catch (Exception ex)
            {
                _logger.Log($"Failed to load configuration: {ex.Message}");
                // 设置默认�?
                _remoteHost = "tunnel.example.com";
                _remotePort = 443;
                _protocol = "tcp";
                _authToken = "";
                _useEncryption = false;
                _logLevel = "Info";
                _httpHost = "";
                _httpPath = "/";
                _heartbeatTimeout = 60;
                _poolCount = 5;
                _user = "";
                _token = "";
                _disableLogColor = false;
            }
        }

        public void StartTunnel(int localPort)
        {
            lock (_lock)
            {
                if (_isRunning)
                {
                    _logger.Log("隧道已在运行�?);
                    return;
                }

                try
                {
                    _cts.Dispose();
                    _cts = new CancellationTokenSource();

                    _localListener = new TcpListener(IPAddress.Any, localPort);
                    _localListener.Start();

                    _isRunning = true;
                    _logger.Log($"开始监听本地端�?{localPort}");

                    // 启接受连接的任务
                    var acceptTask = Task.Run(() => AcceptClientsAsync(_cts.Token), _cts.Token);
                    _runningTasks.Add(acceptTask);

                    _logger.Log($"内网穿透已启动: 本地端口 {localPort} -> {remoteHost}:{remotePort}");
                }
                catch (Exception ex)
                {
                    _logger.Log($"启动隧道失败: {ex.Message}");
                    StopTunnel();
                    throw;
                }
            }
        }

        public void StopTunnel()
        {
            lock (_lock)
            {
                if (!_isRunning)
                {
                    return;
                }

                _isRunning = false;
                _logger.Log("正在停止内网穿�?..");

                try
                {
                    // 取消所有操�?
                    _cts.Cancel();

                    // 停止监听
                    _localListener?.Stop();

                    // 等待所有任务完成（最�?秒）
                    Task.WhenAll(_runningTasks.ToArray()).Wait(TimeSpan.FromSeconds(5));

                    // 清理资源
                    _localListener = null;
                    _runningTasks.Clear();

                    _logger.Log("内网穿透已停止");
                }
                catch (Exception ex)
                {
                    _logger.Log($"停止隧道时出�? {ex.Message}");
                }
                finally
                {
                    _cts.Dispose();
                }
            }
        }

        private async Task AcceptClientsAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    TcpClient client = null;
                    try
                    {
                        client = await _localListener.AcceptTcpClientAsync().ConfigureAwait(false);
                        if (!ct.IsCancellationRequested)
                        {
                            _logger.Log($"接受客户端连�? {client.Client.RemoteEndPoint}");
                            // 为每个连接创建处理任�?
                            var handleTask = Task.Run(() => HandleClientAsync(client, ct), ct);
                            _runningTasks.Add(handleTask);
                        }
                    }
                    catch (SocketException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (ObjectDisposedException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (!ct.IsCancellationRequested)
                        {
                            _logger.Log($"接受连接时出�? {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    _logger.Log($"接受连接循环异常: {ex.Message}");
                }
            }
            finally
            {
                // 从运行任务列表中移除接受任务
                lock (_lock)
                {
                    _runningTasks.RemoveAll(t => t.IsCompleted);
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            TcpClient remoteClient = null;
            NetworkStream clientStream = null;
            NetworkStream remoteStream = null;

            try
            {
                // 读取配置
                var config = System.IO.File.ReadAllText("appsettings.json");
                dynamic configObj = SimpleJson.DeserializeObject(config);
                string remoteHost = configObj.RemoteHost;
                int remotePort = (int)configObj.RemotePort;

                // 连接到远程服务器
                remoteClient = new TcpClient();
                await remoteClient.ConnectAsync(remoteHost, remotePort).ConfigureAwait(false);

                clientStream = client.GetStream();
                remoteStream = remoteClient.GetStream();

                _logger.Log($"建立隧道: {client.Client.RemoteEndPoint} <-> {remoteHost}:{remotePort}");

                // 双向数据传输
                var clientToRemote = CopyStreamAsync(clientStream, remoteStream, ct, "客户�?-> 远程");
                var remoteToClient = CopyStreamAsync(remoteStream, clientStream, ct, "远程 -> 客户�?);

                await Task.WhenAll(clientToRemote, remoteToClient).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 正常取消
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    _logger.Log($"处理客户端时出错: {ex.Message}");
                }
            }
            finally
            {
                // 安全关闭所有流和连�?
                clientStream?.Dispose();
                remoteStream?.Dispose();
                client?.Dispose();
                remoteClient?.Dispose();

                _logger.Log($"连接已关�? {client?.Client?.RemoteEndPoint}");
            }
        }

        private async Task CopyStreamAsync(Stream input, Stream output, CancellationToken ct, string direction)
        {
            try
            {
                var buffer = new byte[81920]; // 80KB buffer
                int bytesRead;

                while ((bytesRead = await input.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                {
                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    await output.WriteAsync(buffer, 0, bytesRead, ct).ConfigureAwait(false);
                    await output.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 正常取消
            }
            catch (IOException) when (ct.IsCancellationRequested)
            {
                // 连接可能已被对方关闭
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    _logger.Log($"{direction} 数据传输错误: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            StopTunnel();
            _cts.Dispose();
        }

        // 隧道配置�?
        public class TunnelConfig
        {
            public int LocalPort { get; set; }
            public int RemotePort { get; set; }
            public string Protocol { get; set; } = "tcp";
            public string AuthToken { get; set; } = "";
            public bool UseEncryption { get; set; } = false;
            public int HeartbeatTimeout { get; set; } = 60;
            public int PoolCount { get; set; } = 1;
        }

        // 简单的JSON解析器（避免额外依赖�?
        private static class SimpleJson
        {
            public static object DeserializeObject(string json)
            {
                // 非常简单的实现，只处理我们需要的键值对
                var result = new System.Dynamic.ExpandoObject();
                var dict = (System.Collections.Generic.IDictionary<string, object>)result;

                // 移除空白和大括号
                json = json.Trim();
                if (json.StartsWith("{") && json.EndsWith("}"))
                {
                    json = json.Substring(1, json.Length - 2);
                }

                // 分割键值对
                var pairs = json.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var pair in pairs)
                {
                    var keyValue = pair.Split(new[] { ':' }, 2);
                    if (keyValue.Length == 2)
                    {
                        var key = keyValue[0].Trim().Trim('"', ' ');
                        var value = keyValue[1].Trim();

                        // 处理数�?
                        if (int.TryParse(value, out int intValue))
                        {
                            dict[key] = intValue;
                        }
                        else if (value.StartsWith("\"") && value.EndsWith("\""))
                        {
                            dict[key] = value.Trim('"');
                        }
                        else
                        {
                            dict[key] = value;
                        }
                    }
                }

                return result;
            }
        }
    }

    public interface ILogger
    {
        void Log(string message);
    }
}

