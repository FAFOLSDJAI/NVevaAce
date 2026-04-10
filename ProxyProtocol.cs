using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NVevaAce
{
    /// <summary>
    /// 代理协议 - 实现与 frp 服务器的握手机制
    /// 参考 frp 的 protocol 设计，支持多种代理类型
    /// </summary>
    public enum ProxyType
    {
        /// <summary>TCP 代理</summary>
        TCP,
        /// <summary>UDP 代理</summary>
        UDP,
        /// <summary>HTTP 代理</summary>
        HTTP,
        /// <summary>HTTPS 代理</summary>
        HTTPS,
        /// <summary>STCP - 安全 TCP，需要密钥</summary>
        STCP,
        /// <summary>XTCP - P2P 直连</summary>
        XTCP
    }

    /// <summary>
    /// 代理协议消息类型
    /// </summary>
    public enum ProxyMessageType : byte
    {
        /// <summary>请求新代理</summary>
        NewProxy = 1,
        /// <summary>关闭代理</summary>
        CloseProxy = 2,
        /// <summary>心跳</summary>
        Ping = 3,
        /// <summary>心跳响应</summary>
        Pong = 4,
        /// <summary>工作连接</summary>
        NewWorkConn = 5,
        /// <summary>UDP 消息</summary>
        UdpPacket = 6
    }

    /// <summary>
    /// 代理协议消息
    /// </summary>
    public class ProxyMessage
    {
        public ProxyMessageType Type { get; set; }
        public string? ProxyName { get; set; }
        public string? Token { get; set; }
        public byte[]? Payload { get; set; }

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write((byte)Type);
                
                // 写入代理名称
                var nameBytes = Encoding.UTF8.GetBytes(ProxyName ?? "");
                writer.Write(nameBytes.Length);
                writer.Write(nameBytes);

                // 写入 Token
                var tokenBytes = Encoding.UTF8.GetBytes(Token ?? "");
                writer.Write(tokenBytes.Length);
                writer.Write(tokenBytes);

                // 写入负载
                if (Payload != null && Payload.Length > 0)
                {
                    writer.Write(Payload.Length);
                    writer.Write(Payload);
                }
                else
                {
                    writer.Write(0);
                }

                return ms.ToArray();
            }
        }

        public static ProxyMessage? Deserialize(byte[] data)
        {
            if (data.Length < 3) return null;

            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                var msg = new ProxyMessage
                {
                    Type = (ProxyMessageType)reader.ReadByte()
                };

                // 读取代理名称
                var nameLen = reader.ReadInt32();
                if (nameLen > 0)
                    msg.ProxyName = Encoding.UTF8.GetString(reader.ReadBytes(nameLen));

                // 读取 Token
                var tokenLen = reader.ReadInt32();
                if (tokenLen > 0)
                    msg.Token = Encoding.UTF8.GetString(reader.ReadBytes(tokenLen));

                // 读取负载
                var payloadLen = reader.ReadInt32();
                if (payloadLen > 0)
                    msg.Payload = reader.ReadBytes(payloadLen);

                return msg;
            }
        }
    }

    /// <summary>
    /// 代理协议处理器 - 负责与 frps 服务器通信
    /// </summary>
    public class ProxyProtocolHandler
    {
        private readonly ILogger _logger;
        private readonly AppConfig _config;
        private const int HEADER_SIZE = 4;

        public ProxyProtocolHandler(ILogger logger, AppConfig config)
        {
            _logger = logger;
            _config = config;
        }

        /// <summary>
        /// 发送代理注册请求
        /// </summary>
        public async Task<bool> RegisterProxyAsync(TcpClient client, TunnelConfig tunnel, CancellationToken ct = default)
        {
            try
            {
                var stream = client.GetStream();
                var message = new ProxyMessage
                {
                    Type = ProxyMessageType.NewProxy,
                    ProxyName = $"tunnel_{tunnel.LocalPort}_{tunnel.RemotePort}",
                    Token = tunnel.AuthToken ?? _config.Token,
                    Payload = SerializeTunnelConfig(tunnel)
                };

                var data = message.Serialize();
                var lengthPrefix = BitConverter.GetBytes(data.Length);
                
                await stream.WriteAsync(lengthPrefix, 0, lengthPrefix.Length, ct).ConfigureAwait(false);
                await stream.WriteAsync(data, 0, data.Length, ct).ConfigureAwait(false);

                _logger.Log($"[协议] 已发送代理注册请求: {tunnel.LocalPort} -> {tunnel.RemotePort}");

                // 等待服务器响应
                var response = await ReadMessageAsync(stream, ct).ConfigureAwait(false);
                if (response != null)
                {
                    _logger.Log($"[协议] 服务器响应: {(byte)response.Type}");
                    return response.Type == ProxyMessageType.Pong;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.Log($"[协议] 注册代理失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 发送心跳
        /// </summary>
        public async Task SendPingAsync(TcpClient client, CancellationToken ct = default)
        {
            try
            {
                var stream = client.GetStream();
                var message = new ProxyMessage
                {
                    Type = ProxyMessageType.Ping,
                    Token = _config.Token
                };

                var data = message.Serialize();
                var lengthPrefix = BitConverter.GetBytes(data.Length);
                
                await stream.WriteAsync(lengthPrefix, 0, lengthPrefix.Length, ct).ConfigureAwait(false);
                await stream.WriteAsync(data, 0, data.Length, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Log($"[协议] 发送心跳失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 读取消息
        /// </summary>
        private async Task<ProxyMessage?> ReadMessageAsync(NetworkStream stream, CancellationToken ct)
        {
            var lengthBuffer = new byte[HEADER_SIZE];
            var bytesRead = await stream.ReadAsync(lengthBuffer, 0, HEADER_SIZE, ct).ConfigureAwait(false);
            
            if (bytesRead < HEADER_SIZE) return null;
            
            var length = BitConverter.ToInt32(lengthBuffer, 0);
            if (length <= 0 || length > 1024 * 1024) return null; // 限制最大1MB

            var data = new byte[length];
            var totalRead = 0;
            while (totalRead < length)
            {
                bytesRead = await stream.ReadAsync(data, totalRead, length - totalRead, ct).ConfigureAwait(false);
                if (bytesRead <= 0) return null;
                totalRead += bytesRead;
            }

            return ProxyMessage.Deserialize(data);
        }

        /// <summary>
        /// 序列化隧道配置为负载
        /// </summary>
        private byte[] SerializeTunnelConfig(TunnelConfig config)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(config.LocalPort);
                writer.Write(config.RemotePort);
                writer.Write(config.Protocol ?? "tcp");
                writer.Write(config.UseEncryption);
                writer.Write(config.UseCompression);
                writer.Write(config.BandwidthLimit);
                return ms.ToArray();
            }
        }
    }
}