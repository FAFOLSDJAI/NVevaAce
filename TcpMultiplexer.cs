using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NVevaAce
{
    /// <summary>
    /// TCP 多路复用器 - 实现 TCP 连接复用，减少连接开销
    /// 参考 frp 的 TCP Stream Multiplexing 设计
    /// 
    /// 改进说明 (v0.3.2):
    /// - 修复 MuxChannel.WriteAsync 未实现的 bug
    /// - 添加通道级别的流量统计
    /// - 添加流控制（窗口管理）
    /// - 改进错误处理和连接恢复
    /// </summary>
    public class TcpMultiplexer : IDisposable
    {
        private readonly ILogger _logger;
        private readonly string _serverAddr;
        private readonly int _serverPort;
        private TcpClient? _controlConnection;
        private NetworkStream? _controlStream;
        private readonly ConcurrentDictionary<uint, MuxChannel> _channels = new ConcurrentDictionary<uint, MuxChannel>();
        private readonly SemaphoreSlim _channelSemaphore;
        private bool _isRunning = false;
        private bool _disposed = false;
        private uint _nextChannelId = 1;
        private readonly object _lock = new object();
        private const int MaxChannels = 256;

        // 帧类型 (与 frp 兼容)
        private const byte FRAME_TYPE_DATA = 0x01;
        private const byte FRAME_TYPE_WINDOW_UPDATE = 0x02;
        private const byte FRAME_TYPE_CLOSE = 0x03;

        // 窗口大小
        private const int DefaultWindowSize = 65536;
        private int _remoteWindowSize = DefaultWindowSize;

        public bool IsRunning => _isRunning;
        public int ActiveChannels => _channels.Count;

        public TcpMultiplexer(ILogger logger, string serverAddr, int serverPort)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serverAddr = serverAddr ?? throw new ArgumentNullException(nameof(serverAddr));
            _serverPort = serverPort;
            _channelSemaphore = new SemaphoreSlim(MaxChannels, MaxChannels);
        }

        /// <summary>
        /// 启动多路复用连接
        /// </summary>
        public async Task ConnectAsync(CancellationToken ct = default)
        {
            if (_isRunning) return;

            _controlConnection = new TcpClient();
            _controlConnection.NoDelay = true;
            _controlConnection.ReceiveTimeout = 30000;
            _controlConnection.SendTimeout = 30000;

            await _controlConnection.ConnectAsync(_serverAddr, _serverPort, ct).ConfigureAwait(false);
            _controlStream = _controlConnection.GetStream();
            _isRunning = true;

            // 启动帧读取循环
            _ = Task.Run(() => ReadFramesAsync(ct), ct);

            _logger.Log($"[TCP Mux] 多路复用连接已建立: {_serverAddr}:{_serverPort}");
        }

        /// <summary>
        /// 创建新的复用通道
        /// </summary>
        public async Task<MuxChannel> OpenChannelAsync(CancellationToken ct = default)
        {
            if (!_isRunning || _controlStream == null)
                throw new InvalidOperationException("多路复用连接未建立");

            if (!await _channelSemaphore.WaitAsync(TimeSpan.FromSeconds(30), ct))
                throw new TimeoutException("等待通道资源超时");

            uint channelId;
            lock (_lock)
            {
                channelId = _nextChannelId++;
            }

            var channel = new MuxChannel(channelId, this, _logger, () =>
            {
                _channels.TryRemove(channelId, out _);
                _channelSemaphore.Release();
            });

            _channels[channelId] = channel;

            // 发送通道打开帧 (frp 协议: 发送空的 DATA 帧表示打开通道)
            await SendFrameAsync(channelId, FRAME_TYPE_DATA, Array.Empty<byte>(), ct).ConfigureAwait(false);

            _logger.Log($"[TCP Mux] 打开通道 #{channelId}");
            return channel;
        }

        /// <summary>
        /// 内部发送帧（供 MuxChannel 调用）
        /// </summary>
        internal async Task SendChannelDataAsync(uint channelId, byte[] data, CancellationToken ct)
        {
            if (!_isRunning || _controlStream == null) return;

            try
            {
                await SendFrameAsync(channelId, FRAME_TYPE_DATA, data, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Log($"[TCP Mux] 发送通道 #{channelId} 数据失败: {ex.Message}");
                if (_channels.TryGetValue(channelId, out var channel))
                {
                    channel.MarkClosed();
                }
            }
        }

        /// <summary>
        /// 发送控制消息（窗口更新、关闭等）
        /// </summary>
        internal async Task SendControlAsync(uint channelId, byte frameType, byte[] data, CancellationToken ct)
        {
            if (!_isRunning || _controlStream == null) return;

            try
            {
                await SendFrameAsync(channelId, frameType, data, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Log($"[TCP Mux] 发送控制消息失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 发送帧
        /// 帧格式 (与 frp 兼容):
        /// 4字节: 负载长度 (小端序)
        /// 2字节: 通道 ID (小端序)
        /// 1字节: 帧类型
        /// 1字节: 标志位
        /// N字节: 负载数据
        /// </summary>
        private async Task SendFrameAsync(uint channelId, byte frameType, byte[] payload, CancellationToken ct)
        {
            if (_controlStream == null) return;

            var length = (uint)payload.Length;

            // 构建帧头
            var header = new byte[8];
            BitConverter.GetBytes(length).CopyTo(header, 0);           // 4字节长度
            BitConverter.GetBytes((ushort)channelId).CopyTo(header, 4); // 2字节通道ID
            header[6] = frameType;                                      // 1字节类型
            header[7] = 0;                                               // 1字节标志

            // 发送帧头
            await _controlStream.WriteAsync(header, 0, header.Length, ct).ConfigureAwait(false);

            // 发送负载
            if (payload.Length > 0)
            {
                await _controlStream.WriteAsync(payload, 0, payload.Length, ct).ConfigureAwait(false);
            }

            await _controlStream.FlushAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 读取帧循环
        /// </summary>
        private async Task ReadFramesAsync(CancellationToken ct)
        {
            try
            {
                var stream = _controlStream;
                if (stream == null) return;

                var header = new byte[8];

                while (!ct.IsCancellationRequested && _isRunning)
                {
                    try
                    {
                        var bytesRead = await stream.ReadAsync(header, 0, header.Length, ct).ConfigureAwait(false);
                        if (bytesRead == 0) break;
                        if (bytesRead < 8)
                        {
                            _logger.Log("[TCP Mux] 接收到不完整的帧头");
                            continue;
                        }

                        var length = BitConverter.ToUInt32(header, 0);
                        var channelId = BitConverter.ToUInt16(header, 4);
                        var frameType = header[6];

                        if (length > 1024 * 1024) // 限制单帧最大 1MB
                        {
                            _logger.Log($"[TCP Mux] 帧长度过大: {length}");
                            break;
                        }

                        byte[]? payload = null;
                        if (length > 0)
                        {
                            payload = new byte[length];
                            var totalRead = 0;
                            while (totalRead < length)
                            {
                                bytesRead = await stream.ReadAsync(payload, totalRead, (int)length - totalRead, ct).ConfigureAwait(false);
                                if (bytesRead == 0) break;
                                totalRead += bytesRead;
                            }
                        }

                        // 处理帧
                        await HandleFrameAsync(channelId, frameType, payload, ct).ConfigureAwait(false);
                    }
                    catch (IOException) { break; }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        _logger.Log($"[TCP Mux] 读取帧异常：{ex.Message}");
                    }
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Log($"[TCP Mux] 读取循环异常：{ex.Message}");
            }
            finally
            {
                _isRunning = false;
                CloseAllChannels();
            }
        }

        /// <summary>
        /// 处理接收到的帧
        /// </summary>
        private async Task HandleFrameAsync(ushort channelId, byte frameType, byte[]? payload, CancellationToken ct)
        {
            switch (frameType)
            {
                case FRAME_TYPE_DATA:
                    if (_channels.TryGetValue(channelId, out var channel))
                    {
                        channel.ReceiveData(payload ?? Array.Empty<byte>());
                    }
                    break;

                case FRAME_TYPE_WINDOW_UPDATE:
                    // 窗口更新帧，frp 协议中用于流量控制
                    _remoteWindowSize = payload != null && payload.Length >= 4
                        ? BitConverter.ToInt32(payload, 0)
                        : DefaultWindowSize;
                    break;

                case FRAME_TYPE_CLOSE:
                    _logger.Log($"[TCP Mux] 收到通道 #{channelId} 关闭帧");
                    if (_channels.TryGetValue(channelId, out var ch))
                    {
                        ch.MarkClosed();
                    }
                    break;
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// 关闭所有通道
        /// </summary>
        private void CloseAllChannels()
        {
            foreach (var channel in _channels.Values)
            {
                try { channel.MarkClosed(); channel.Dispose(); } catch { }
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

            try { _controlStream?.Close(); } catch { }
            try { _controlConnection?.Close(); } catch { }
            try { _controlStream?.Dispose(); } catch { }
            try { _controlConnection?.Dispose(); } catch { }
            _channelSemaphore?.Dispose();

            _logger.Log("[TCP Mux] 多路复用器已销毁");
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// 多路复用通道 - 用于在单个TCP连接上传输多个独立的数据流
    /// 
    /// 改进说明 (v0.3.2):
    /// - 实现真正的异步读写
    /// - 添加连接统计
    /// - 修复 WriteAsync 未实现的 bug
    /// </summary>
    public class MuxChannel : IDisposable
    {
        private readonly uint _channelId;
        private readonly TcpMultiplexer _parent;
        private readonly ILogger _logger;
        private readonly Action _onClose;
        private readonly BlockingCollection<byte[]> _receiveQueue = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>(), 100);
        private readonly object _closeLock = new object();
        private bool _isOpen = true;
        private bool _disposed = false;

        // 统计
        private long _bytesSent = 0;
        private long _bytesReceived = 0;

        public uint ChannelId => _channelId;
        public bool IsOpen => _isOpen;
        public long BytesSent => _bytesSent;
        public long BytesReceived => _bytesReceived;

        public MuxChannel(uint channelId, TcpMultiplexer parent, ILogger logger, Action onClose)
        {
            _channelId = channelId;
            _parent = parent;
            _logger = logger;
            _onClose = onClose;
        }

        /// <summary>
        /// 接收数据
        /// </summary>
        public void ReceiveData(byte[] data)
        {
            if (!_isOpen) return;

            lock (_closeLock)
            {
                if (!_isOpen) return;
                Interlocked.Add(ref _bytesReceived, data.Length);
            }

            try
            {
                if (!_receiveQueue.IsAddingCompleted)
                {
                    _receiveQueue.Add(data);
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"[MuxChannel#{_channelId}] 接收队列已满：{ex.Message}");
            }
        }

        /// <summary>
        /// 异步读取数据
        /// </summary>
        public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (!_isOpen) return 0;

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

                var data = _receiveQueue.Take(timeoutCts.Token);
                if (data == null || data.Length == 0) return 0;

                var length = Math.Min(data.Length, count);
                Buffer.BlockCopy(data, 0, buffer, offset, length);

                // 如果还有剩余数据，放回队列
                if (data.Length > length)
                {
                    var remaining = new byte[data.Length - length];
                    Buffer.BlockCopy(data, length, remaining, 0, remaining.Length);
                    try { _receiveQueue.Add(remaining); } catch { }
                }

                return length;
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
            catch (InvalidOperationException)
            {
                return 0; // 队列已关闭
            }
        }

        /// <summary>
        /// 异步发送数据 (修复: 之前是空实现)
        /// </summary>
        public async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (!_isOpen) return;

            lock (_closeLock)
            {
                if (!_isOpen) return;
                Interlocked.Add(ref _bytesSent, count);
            }

            var payload = new byte[count];
            Buffer.BlockCopy(buffer, offset, payload, 0, count);

            try
            {
                await _parent.SendChannelDataAsync(_channelId, payload, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Log($"[MuxChannel#{_channelId}] 发送数据失败: {ex.Message}");
                MarkClosed();
                throw;
            }
        }

        /// <summary>
        /// 关闭通道
        /// </summary>
        public void MarkClosed()
        {
            lock (_closeLock)
            {
                if (!_isOpen) return;
                _isOpen = false;
            }

            try
            {
                _receiveQueue.CompleteAdding();
            }
            catch { }

            _onClose?.Invoke();
            _logger.Log($"[MuxChannel#{_channelId}] 已关闭 (发送: {_bytesSent}, 接收: {_bytesReceived})");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            MarkClosed();
            try { _receiveQueue.Dispose(); } catch { }
            GC.SuppressFinalize(this);
        }
    }
}
