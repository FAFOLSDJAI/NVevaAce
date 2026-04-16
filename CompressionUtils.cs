using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace NVevaAce
{
    /// <summary>
    /// 压缩工具类 - 支持 gzip/deflate 流压缩
    /// 参考 frp 的压缩实现
    /// </summary>
    public class CompressionUtils
    {
        private readonly ILogger _logger;
        private readonly bool _useCompression;

        public CompressionUtils(ILogger logger, bool useCompression = true)
        {
            _logger = logger;
            _useCompression = useCompression;
        }

        /// <summary>
        /// 创建压缩流（写入端压缩，读取端解压）
        /// </summary>
        public Stream CreateCompressedStream(Stream underlying, CompressionMode mode)
        {
            if (!_useCompression)
                return underlying;

            try
            {
                return mode == CompressionMode.Compress
                    ? new GZipStream(underlying, CompressionLevel.Fastest, leaveOpen: true)
                    : new GZipStream(underlying, mode, leaveOpen: true);
            }
            catch (Exception ex)
            {
                _logger.Log($"[压缩] 创建压缩流失败: {ex.Message}");
                return underlying;
            }
        }

        /// <summary>
        /// 压缩数据
        /// </summary>
        public byte[] Compress(byte[] data)
        {
            if (!_useCompression || data == null || data.Length == 0)
                return data;

            try
            {
                using var output = new MemoryStream();
                using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
                {
                    gzip.Write(data, 0, data.Length);
                }
                return output.ToArray();
            }
            catch (Exception ex)
            {
                _logger.Log($"[压缩] 压缩数据失败: {ex.Message}");
                return data;
            }
        }

        /// <summary>
        /// 解压数据
        /// </summary>
        public byte[] Decompress(byte[] data)
        {
            if (!_useCompression || data == null || data.Length == 0)
                return data;

            try
            {
                using var input = new MemoryStream(data);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                gzip.CopyTo(output);
                return output.ToArray();
            }
            catch (Exception ex)
            {
                _logger.Log($"[压缩] 解压数据失败: {ex.Message}");
                return data;
            }
        }

        /// <summary>
        /// 压缩流包装器 - 透明压缩传输
        /// </summary>
        public class CompressedStream : IDisposable
        {
            private readonly Stream _inner;
            private readonly GZipStream? _gzip;
            private readonly bool _isCompress;
            private readonly ILogger _logger;
            private bool _disposed;

            /// <summary>
            /// 创建压缩或解压流
            /// </summary>
            /// <param name="inner">底层流</param>
            /// <param name="isCompress">true=压缩写入，false=解压读取</param>
            public CompressedStream(Stream inner, bool isCompress, ILogger logger)
            {
                _inner = inner;
                _isCompress = isCompress;
                _logger = logger;

                if (isCompress)
                {
                    _gzip = new GZipStream(inner, CompressionLevel.Fastest, leaveOpen: true);
                }
                // 解压模式不在构造时创建gzip，因为需要先读magic bytes判断
            }

            public Stream OutputStream => _isCompress ? _gzip! : _inner;

            public Stream GetDecompressionStream()
            {
                if (_isCompress)
                    throw new InvalidOperationException("Cannot get decompression stream from compression stream");

                // 检测是否gzip格式 (magic: 0x1f 0x8b)
                var buffer = new byte[2];
                var pos = _inner.Position;
                _inner.Read(buffer, 0, 2);
                _inner.Position = pos;

                if (buffer[0] == 0x1f && buffer[1] == 0x8b)
                {
                    _logger.Log("[压缩] 检测到 GZip 格式数据");
                    return new GZipStream(_inner, CompressionMode.Decompress, leaveOpen: true);
                }

                _logger.Log("[压缩] 检测到普通数据（未压缩）");
                return _inner;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _gzip?.Dispose();
            }
        }
    }
}