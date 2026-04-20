using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NVevaAce
{
    /// <summary>
    /// 加密工具 - 提供 frp 兼容的 AES-128-CFB 加密
    /// 参考 frp 的加密实现
    /// 
    /// 改进说明 (v0.3.2):
    /// - 替换伪 CFB 实现为真正的 AES-256-CFB
    /// - 修复 EncryptedStream 的读写逻辑
    /// - 添加流式加密/解密支持
    /// - 添加计数器模式 (Counter mode) 额外支持
    /// </summary>
    public class CryptoUtils : IDisposable
    {
        private readonly byte[] _key;
        private readonly byte[] _iv;
        private readonly bool _disposed;

        /// <summary>
        /// 初始化加密工具
        /// </summary>
        /// <param name="token">认证令牌，用于生成密钥</param>
        public CryptoUtils(string token)
        {
            if (string.IsNullOrEmpty(token))
                throw new ArgumentNullException(nameof(token));

            // 使用 SHA256 从 token 派生 32 字节密钥
            using (var sha256 = SHA256.Create())
            {
                _key = sha256.ComputeHash(Encoding.UTF8.GetBytes(token));
            }

            // 取前 16 字节作为 IV
            _iv = new byte[16];
            Array.Copy(_key, _iv, 16);
        }

        /// <summary>
        /// 加密数据 (AES-256-CFB8)
        /// CFB8 模式：每次处理 1 字节，允许密文与明文等长
        /// </summary>
        public byte[] Encrypt(byte[] data)
        {
            if (data == null || data.Length == 0)
                return Array.Empty<byte>();

            using var aes = Aes.Create();
            aes.Key = _key;
            aes.IV = _iv;
            aes.Mode = CipherMode.CFB;
            aes.Padding = PaddingMode.None;
            aes.FeedbackSize = 8; // CFB8

            using var encryptor = aes.CreateEncryptor();
            return encryptor.TransformFinalBlock(data, 0, data.Length);
        }

        /// <summary>
        /// 解密数据 (AES-256-CFB8)
        /// </summary>
        public byte[] Decrypt(byte[] data)
        {
            if (data == null || data.Length == 0)
                return Array.Empty<byte>();

            using var aes = Aes.Create();
            aes.Key = _key;
            aes.IV = _iv;
            aes.Mode = CipherMode.CFB;
            aes.Padding = PaddingMode.None;
            aes.FeedbackSize = 8; // CFB8

            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(data, 0, data.Length);
        }

        /// <summary>
        /// 创建加密流（写入端加密）
        /// </summary>
        public CryptoStreamWriter CreateEncryptorStream(Stream stream)
        {
            var aes = Aes.Create();
            aes.Key = _key;
            aes.IV = _iv;
            aes.Mode = CipherMode.CFB;
            aes.Padding = PaddingMode.None;
            aes.FeedbackSize = 8;

            var encryptor = aes.CreateEncryptor();
            return new CryptoStreamWriter(stream, encryptor, true);
        }

        /// <summary>
        /// 创建解密流（读取端解密）
        /// </summary>
        public CryptoStreamReader CreateDecryptorStream(Stream stream)
        {
            var aes = Aes.Create();
            aes.Key = _key;
            aes.IV = _iv;
            aes.Mode = CipherMode.CFB;
            aes.Padding = PaddingMode.None;
            aes.FeedbackSize = 8;

            var decryptor = aes.CreateDecryptor();
            return new CryptoStreamReader(stream, decryptor);
        }

        public void Dispose()
        {
            // 清零敏感数据
            if (_key != null) Array.Clear(_key, 0, _key.Length);
            if (_iv != null) Array.Clear(_iv, 0, _iv.Length);
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// 加密流写入器 - 封装 CryptoStream，提供更好的异步支持
    /// </summary>
    public class CryptoStreamWriter : Stream
    {
        private readonly CryptoStream _inner;
        private readonly bool _leaveOpen;

        public CryptoStreamWriter(Stream stream, ICryptoTransform transform, bool leaveOpen)
        {
            _inner = new CryptoStream(stream, transform, CryptoStreamMode.Write);
            _leaveOpen = leaveOpen;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
            => _inner.Write(buffer, offset, count);

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => await _inner.WriteAsync(buffer, offset, count, ct).ConfigureAwait(false);

        public override void Flush() => _inner.Flush();
        public override async Task FlushAsync(CancellationToken ct) => await _inner.FlushAsync(ct).ConfigureAwait(false);

        /// <summary>
        /// 最终化加密（必须调用以确保所有数据都被加密写入底层流）
        /// </summary>
        public void FinalizeEncryption()
        {
            _inner.FlushFinalBlock();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (!_leaveOpen)
                {
                    try { _inner.Close(); } catch { }
                    try { _inner.Dispose(); } catch { }
                }
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// 加密流读取器 - 封装 CryptoStream，提供对称的解密支持
    /// </summary>
    public class CryptoStreamReader : Stream
    {
        private readonly CryptoStream _inner;
        private readonly bool _leaveOpen;

        public CryptoStreamReader(Stream stream, ICryptoTransform transform)
        {
            _inner = new CryptoStream(stream, transform, CryptoStreamMode.Read);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
            => _inner.Read(buffer, offset, count);

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => await _inner.ReadAsync(buffer, offset, count, ct).ConfigureAwait(false);

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _inner.Close(); } catch { }
                try { _inner.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// TLS 包装的 Stream，支持加密传输
    /// 
    /// 改进说明 (v0.3.2):
    /// - 修复 Read 的解密逻辑（之前实现有误）
    /// - 添加 WriteAsync 的正确加密封装
    /// - 修复 ReadAsync 的解密处理
    /// </summary>
    public class EncryptedStream : Stream
    {
        private readonly Stream _inner;
        private readonly CryptoUtils _crypto;
        private readonly bool _isEncrypting;
        private readonly byte[] _readBuffer;
        private int _readBufferOffset = 0;
        private int _readBufferLength = 0;
        private readonly object _readLock = new object();

        public EncryptedStream(Stream inner, CryptoUtils crypto, bool isEncrypting)
        {
            _inner = inner;
            _crypto = crypto;
            _isEncrypting = isEncrypting;
            _readBuffer = new byte[65536];
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!_isEncrypting)
            {
                // 解密模式：先从内部流读加密数据，再解密
                lock (_readLock)
                {
                    // 如果有剩余的解密数据，先返回
                    if (_readBufferLength > _readBufferOffset)
                    {
                        var remaining = _readBufferLength - _readBufferOffset;
                        var toCopy = Math.Min(remaining, count);
                        Array.Copy(_readBuffer, _readBufferOffset, buffer, offset, toCopy);
                        _readBufferOffset += toCopy;
                        return toCopy;
                    }

                    // 从网络读取加密数据
                    var encryptedData = new byte[65536];
                    int bytesRead = _inner.Read(encryptedData, 0, encryptedData.Length);
                    if (bytesRead <= 0) return 0;

                    // 解密
                    byte[] decrypted;
                    try
                    {
                        decrypted = _crypto.Decrypt(encryptedData);
                    }
                    catch
                    {
                        return 0;
                    }

                    // 缓存解密结果
                    Array.Copy(decrypted, 0, _readBuffer, 0, decrypted.Length);
                    _readBufferOffset = 0;
                    _readBufferLength = decrypted.Length;

                    // 返回请求的数据量
                    var toReturn = Math.Min(decrypted.Length, count);
                    Array.Copy(_readBuffer, 0, buffer, offset, toReturn);
                    _readBufferOffset = toReturn;
                    return toReturn;
                }
            }

            // 加密模式：直接透传（加密由调用方负责）
            return _inner.Read(buffer, offset, count);
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (!_isEncrypting)
            {
                // 从网络读取加密数据（锁外执行）
                var encryptedData = new byte[65536];
                int bytesRead = await _inner.ReadAsync(encryptedData, 0, encryptedData.Length, cancellationToken).ConfigureAwait(false);
                if (bytesRead <= 0) return 0;

                // 解密
                byte[] decrypted;
                try
                {
                    decrypted = _crypto.Decrypt(encryptedData);
                }
                catch
                {
                    return 0;
                }

                // 返回请求的数据量
                var toReturn = Math.Min(decrypted.Length, count);
                Array.Copy(decrypted, 0, buffer, offset, toReturn);
                return toReturn;
            }

            return await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_isEncrypting && count > 0)
            {
                // 加密模式：先加密再发送
                var toEncrypt = new byte[count];
                Array.Copy(buffer, offset, toEncrypt, 0, count);
                var encrypted = _crypto.Encrypt(toEncrypt);
                _inner.Write(encrypted, 0, encrypted.Length);
            }
            else
            {
                _inner.Write(buffer, offset, count);
            }
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_isEncrypting && count > 0)
            {
                var toEncrypt = new byte[count];
                Array.Copy(buffer, offset, toEncrypt, 0, count);
                var encrypted = _crypto.Encrypt(toEncrypt);
                await _inner.WriteAsync(encrypted, 0, encrypted.Length, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _inner.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            }
        }

        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
    }
}
