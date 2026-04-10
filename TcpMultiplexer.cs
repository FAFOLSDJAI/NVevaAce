using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NVevaAce
{
    /// <summary>
    /// TCP 多路复用器 - 实现 TCP 连接复用，减少连接开销
    /// 参考 frp 的 TCP Stream Multiplexing 设计
    /// </summary>
    public class TcpMultiplexer : IDisposable
    {
        private readonly ILogger _logger;
        private readonly string _serverAddr;
        private readonly int _serverPort;
        private readonly TcpClient _controlConnection;
        private readonly ConcurrentDictionary<uint, MuxChannel> _channels = new ConcurrentDictionary<uint, MuxChannel>();
        private readonly SemaphoreSlim _channelSemaphore;
        private bool _isRunning = false;
        private bool _disposed = false;
        private uint _nextChannelId = 1;
        private readonly object _lock = new object();
        private const int MaxChannels = 256;

        // 帧类型
        private const byte DATA = 0x01;      // 数据帧
        private const byte WINDOW_UPDATE = 0x02; // 窗口更新
        private const byte GOAWAY = 0x03;    // 关闭连接

        public bool IsRunning => _isRunning;
        public int ActiveChannels => _channels.Count;

        public TcpMultiplexer(ILogger logger, string serverAddr, int serverPort)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serverAddr = serverAddr ?? throw new ArgumentNullException(nameof(serverAddr));
            _serverPort = serverPort;
            _channelSemaphore = new SemaphoreSlim(MaxChannels, MaxChannels);
            
            _controlConnection = new TcpClient();
            _controlConnection.NoDelay = true; // 禁用 Nagle 算法，降低延迟
        }

        /// <summary>
        /// 启动多路复用连接
        /// </summary>
        public async Task ConnectAsync(CancellationToken ct = default)
        {
            if (_isRunning) return;

            await _controlConnection.ConnectAsync(_serverAddr, _serverPort, ct).ConfigureAwait(false);
            _isRunning = true;
            
            // 启动帧读取循环
            Task.Run(() => ReadFramesAsync(ct), ct);
            
            _logger.Log($"[TCP Mux] 多路复用连接已建立: {_serverAddr}:{_serverPort}");
        }

        /// <summary>
        /// 创建新的复用通道
        /// </summary>
        public async Task<MuxChannel> OpenChannelAsync(CancellationToken ct = default)
        {
            if (!_isRunning)
                throw new InvalidOperationException("多路复用连接未建立");

            if (!await _channelSemaphore.WaitAsync(TimeSpan.FromSeconds(30), ct))
                throw new TimeoutException("等待通道资源超时");

            uint channelId;
            lock (_lock)
            {
                channelId = _nextChannelId++;
            }

            var channel = new MuxChannel(channelId, _controlConnection, _logger, () => 
            {
                _channels.TryRemove(channelId, out _);
                _channelSemaphore.Release();
            });

            _channels[channelId] = channel;

            // 发送通道打开帧
            await SendFrameAsync(channelId, new byte[] { 0x00 }, ct).ConfigureAwait(false);

            _logger.Log($"[TCP Mux] 打开通道 #{channelId}");
            return channel;
        }

        /// <summary>
        /// 读取帧循环
        /// </summary>
        private async Task ReadFramesAsync(CancellationToken ct)
        {
            try
            {
                var stream = _controlConnection.GetStream();
                var header = new byte[8]; // 4字节长度 + 2字节通道ID + 1字节类型 + 1字节标志

                while (!ct.IsCancellationRequested && _isRunning)
                {
                    var bytesRead = await stream.ReadAsync(header, 0, header.Length, ct).ConfigureAwait(false);
                    if (bytesRead == 0) break;

                    var length = BitConverter.ToUInt32(header, 0);
                    var channelId = BitConverter.ToUInt16(header, 4);
                    var type = header[6];
                    // var flags = header[7];

                    if (length > 0 && length < 65536)
                    {
                        var payload = new byte[length];
                        var totalRead = 0;
                        while (totalRead < length)
                        {
                            bytesRead = await stream.ReadAsync(payload, totalRead, (int)length - totalRead, ct).ConfigureAwait(false);
                            if (bytesRead == 0) break;
                            totalRead += bytesRead;
                        }

                        if (_channels.TryGetValue(channelId, out var channel))
                        {
                            channel.ReceiveData(payload);
                        }
                    }
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Log($"[TCP Mux] 读取帧异常：{ex.Message}");
            }
            finally
            {
                _isRunning = false;
                CloseAllChannels();
            }
        }

        /// <summary>
        /// 发送帧
        /// </summary>
        private async Task SendFrameAsync(uint channelId, byte[] payload, CancellationToken ct)
        {
            if (!_isRunning) return;

            var stream = _controlConnection.GetStream();
            var length = (uint)payload.Length;
            
            // 构建帧：4字节长度 + 2字节通道ID + 1字节类型 + 1字节标志 + 负载
            var frame = new byte[8 + payload.Length];
            BitConverter.GetBytes(length).CopyTo(frame, 0);
            BitConverter.GetBytes((ushort)channelId).CopyTo(frame, 4);
            frame[6] = DATA;
            frame[7] = 0; // flags

            if (payload.Length > 0)
                Buffer.BlockCopy(payload, 0, frame, 8, payload.Length);

            await stream.WriteAsync(frame, 0, frame.Length, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 关闭所有通道
        /// </summary>
        private void CloseAllChannels()
        {
            foreach (var channel in _channels.Values)
            {
                try { channel.Dispose(); } catch { }
            }
            _channels.Clear();
            _logger.Log("[TCP Mux] 所有通道已关闭");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            _isRunning = false;
            CloseAllChannels();
            _controlConnection?.Dispose();
            _channelSemaphore?.Dispose();
            
            _logger.Log("[TCP Mux] 多路复用器已销毁");
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// 多路复用通道 - 用于在单个TCP连接上传输多个独立的数据流
    /// </summary>
    public class MuxChannel : IDisposable
    {
        private readonly uint _channelId;
        private readonly TcpClient _connection;
        private readonly ILogger _logger;
        private readonly Action _onClose;
        private readonly BlockingCollection<byte[]> _receiveQueue = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>(), 100);
        private bool _isOpen = true;
        private bool _disposed = false;

        public uint ChannelId => _channelId;
        public bool IsOpen => _isOpen;

        public MuxChannel(uint channelId, TcpClient connection, ILogger logger, Action onClose)
        {
            _channelId = channelId;
            _connection = connection;
            _logger = logger;
            _onClose = onClose;
        }

        /// <summary>
        /// 接收数据
        /// </summary>
        public void ReceiveData(byte[] data)
        {
            if (!_isOpen) return;
            
            try
            {
                _receiveQueue.Add(data);
            }
            catch (Exception ex)
            {
                _logger.Log($"[MuxChannel#{_channelId}] 接收队列已满：{ex.Message}");
            }
        }

        /// <summary>
        /// 读取数据
        /// </summary>
        public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (!_isOpen) return 0;

            try
            {
                var data = _receiveQueue.Take(ct);
                var length = Math.Min(data.Length, count);
                Buffer.BlockCopy(data, 0, buffer, offset, length);
                return length;
            }
            catch (InvalidOperationException)
            {
                return 0; // 队列已关闭
            }
        }

        /// <summary>
        /// 发送数据
        /// </summary>
        public async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (!_isOpen) return;

            var payload = new byte[count];
            Buffer.BlockCopy(buffer, offset, payload, 0, count);
            
            // 这里需要通过 TcpMultiplexer 发送，暂时简化处理
            await Task.CompletedTask;
        }

        public void Close()
        {
            _isOpen = false;
            _receiveQueue.CompleteAdding();
            _onClose?.Invoke();
            _logger.Log($"[MuxChannel#{_channelId}] 已关闭");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Close();
            _receiveQueue.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}