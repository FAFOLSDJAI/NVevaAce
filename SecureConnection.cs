using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace NVevaAce
{
    /// <summary>
    /// 安全连接管理器 - 支持 TLS 加密传输
    /// 参考 frp 的 TLS 实现
    /// </summary>
    public class SecureConnection : IDisposable
    {
        private readonly ILogger _logger;
        private readonly string _serverAddr;
        private readonly int _serverPort;
        private readonly bool _tlsEnable;
        private readonly bool _useEncryption;
        private readonly CryptoUtils? _crypto;
        private TcpClient? _tcpClient;
        private Stream? _stream;
        private bool _disposed;

        public Stream? Stream => _stream;
        public bool IsConnected => _tcpClient?.Connected ?? false;

        public SecureConnection(ILogger logger, string serverAddr, int serverPort, 
            bool tlsEnable, bool useEncryption, string token)
        {
            _logger = logger;
            _serverAddr = serverAddr;
            _serverPort = serverPort;
            _tlsEnable = tlsEnable;
            _useEncryption = useEncryption;

            if (useEncryption && !string.IsNullOrEmpty(token))
            {
                _crypto = new CryptoUtils(token);
            }
        }

        /// <summary>
        /// 建立安全连接
        /// </summary>
        public async Task<bool> ConnectAsync(CancellationToken ct = default)
        {
            try
            {
                _tcpClient = new TcpClient();
                _tcpClient.NoDelay = true;
                
                await _tcpClient.ConnectAsync(_serverAddr, _serverPort, ct).ConfigureAwait(false);
                
                if (_tlsEnable)
                {
                    // 使用 TLS 加密
                    var sslStream = new SslStream(
                        _tcpClient.GetStream(),
                        false,
                        (sender, certificate, chain, errors) => true // 接受所有证书
                    );
                    
                    await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = _serverAddr,
                        EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12
                    }).ConfigureAwait(false);
                    
                    _stream = sslStream;
                    _logger.Log("[TLS] 已建立 TLS 加密连接");
                }
                else if (_useEncryption && _crypto != null)
                {
                    // 使用自定义加密
                    _stream = new EncryptedStream(_tcpClient.GetStream(), _crypto, true);
                    _logger.Log("[加密] 已建立 AES-128-CFB 加密连接");
                }
                else
                {
                    _stream = _tcpClient.GetStream();
                    _logger.Log("[连接] 已建立普通 TCP 连接");
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.Log($"[连接] 建立连接失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 关闭连接
        /// </summary>
        public void Close()
        {
            try
            {
                _stream?.Close();
                _tcpClient?.Close();
                _logger.Log("[连接] 已关闭连接");
            }
            catch (Exception ex)
            {
                _logger.Log($"[连接] 关闭连接时出错: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            Close();
            _stream?.Dispose();
            _tcpClient?.Dispose();
            _disposed = true;
        }
    }

    /// <summary>
    /// 安全连接工厂
    /// </summary>
    public class SecureConnectionFactory
    {
        private readonly ILogger _logger;
        private readonly string _serverAddr;
        private readonly int _serverPort;
        private readonly bool _tlsEnable;
        private readonly bool _useEncryption;
        private readonly string _token;

        public SecureConnectionFactory(ILogger logger, string serverAddr, int serverPort,
            bool tlsEnable, bool useEncryption, string token)
        {
            _logger = logger;
            _serverAddr = serverAddr;
            _serverPort = serverPort;
            _tlsEnable = tlsEnable;
            _useEncryption = useEncryption;
            _token = token;
        }

        /// <summary>
        /// 创建新的安全连接
        /// </summary>
        public SecureConnection Create()
        {
            return new SecureConnection(_logger, _serverAddr, _serverPort, _tlsEnable, _useEncryption, _token);
        }

        /// <summary>
        /// 创建加密的 NetworkStream
        /// </summary>
        public async Task<Stream> CreateEncryptedStreamAsync(TcpClient client, CancellationToken ct = default)
        {
            if (_tlsEnable)
            {
                var sslStream = new SslStream(
                    client.GetStream(),
                    false,
                    (sender, certificate, chain, errors) => true
                );
                
                await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = _serverAddr,
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12
                }).ConfigureAwait(false);
                
                return sslStream;
            }
            else if (_useEncryption && !string.IsNullOrEmpty(_token))
            {
                var crypto = new CryptoUtils(_token);
                return new EncryptedStream(client.GetStream(), crypto, true);
            }
            
            return client.GetStream();
        }
    }
}