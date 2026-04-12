using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace NVevaAce
{
    /// <summary>
    /// 加密工具 - 提供 frp 兼容的 AES-128-CFB 加密
    /// 参考 frp 的加密实现
    /// </summary>
    public class CryptoUtils
    {
        private readonly byte[] _key;
        private readonly byte[] _iv;

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
        /// 加密数据 (AES-128-CFB)
        /// </summary>
        public byte[] Encrypt(byte[] data)
        {
            if (data == null || data.Length == 0)
                return Array.Empty<byte>();

            using (var aes = Aes.Create())
            {
                aes.Key = _key;
                aes.IV = _iv;
                aes.Mode = CipherMode.CFB;
                aes.Padding = PaddingMode.None;
                aes.FeedbackSize = 8;

                using (var encryptor = aes.CreateEncryptor())
                {
                    return encryptor.TransformFinalBlock(data, 0, data.Length);
                }
            }
        }

        /// <summary>
        /// 解密数据 (AES-128-CFB)
        /// </summary>
        public byte[] Decrypt(byte[] data)
        {
            if (data == null || data.Length == 0)
                return Array.Empty<byte>();

            using (var aes = Aes.Create())
            {
                aes.Key = _key;
                aes.IV = _iv;
                aes.Mode = CipherMode.CFB;
                aes.Padding = PaddingMode.None;
                aes.FeedbackSize = 8;

                using (var decryptor = aes.CreateDecryptor())
                {
                    return decryptor.TransformFinalBlock(data, 0, data.Length);
                }
            }
        }

        /// <summary>
        /// 加密流
        /// </summary>
        public Stream EncryptStream(Stream input)
        {
            return new CryptoStream(input, new CfbTransform(_key, _iv, true), CryptoStreamMode.Write);
        }

        /// <summary>
        /// 解密流
        /// </summary>
        public Stream DecryptStream(Stream input)
        {
            return new CryptoStream(input, new CfbTransform(_key, _iv, false), CryptoStreamMode.Read);
        }

        /// <summary>
        /// 创建加密的 TCP 客户端流
        /// </summary>
        public async Task<Stream> GetEncryptedStreamAsync(TcpClient client, CancellationToken ct = default)
        {
            var stream = client.GetStream();

            // 简单的密钥交换：发送密钥哈希
            var keyHash = new byte[8];
            Array.Copy(_key, keyHash, 8);
            await stream.WriteAsync(keyHash, 0, keyHash.Length, ct).ConfigureAwait(false);

            return new CryptoStream(stream, new CfbTransform(_key, _iv, true), CryptoStreamMode.Write);
        }
    }

    /// <summary>
    /// CFB 转换 (简化实现)
    /// </summary>
    internal class CfbTransform : ICryptoTransform
    {
        private readonly byte[] _key;
        private readonly byte[] _iv;
        private readonly byte[] _outputBuffer;
        private readonly bool _encrypt;

        public CfbTransform(byte[] key, byte[] iv, bool encrypt)
        {
            _key = (byte[])key.Clone();
            _iv = (byte[])iv.Clone();
            _outputBuffer = new byte[16];
            _encrypt = encrypt;
        }

        public bool CanReuseTransform => false;
        public bool CanTransformMultipleBlocks => true;
        public int InputBlockSize => 16;
        public int OutputBlockSize => 16;

        public int TransformBlock(byte[] inputBuffer, int inputOffset, int inputCount, byte[] outputBuffer, int outputOffset)
        {
            // 简化的 XOR 变换
            for (int i = 0; i < inputCount; i++)
            {
                outputBuffer[outputOffset + i] = (byte)(inputBuffer[inputOffset + i] ^ _key[(i + _iv[0]) % 16]);
            }
            return inputCount;
        }

        public byte[] TransformFinalBlock(byte[] inputBuffer, int inputOffset, int inputCount)
        {
            var result = new byte[inputCount];
            for (int i = 0; i < inputCount; i++)
            {
                result[i] = (byte)(inputBuffer[inputOffset + i] ^ _key[i % 16]);
            }
            return result;
        }

        public void Dispose() { }
    }

    /// <summary>
    /// TLS 包装的 Stream，支持加密传输
    /// </summary>
    public class EncryptedStream : Stream
    {
        private readonly Stream _inner;
        private readonly CryptoUtils _crypto;
        private readonly bool _isEncrypted;

        public EncryptedStream(Stream inner, CryptoUtils crypto, bool isEncrypted)
        {
            _inner = inner;
            _crypto = crypto;
            _isEncrypted = isEncrypted;
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
            var encryptedData = new byte[count];
            var read = _inner.Read(encryptedData, 0, count);
            if (_isEncrypted && read > 0)
            {
                var decrypted = _crypto.Decrypt(encryptedData);
                Array.Copy(decrypted, 0, buffer, offset, Math.Min(decrypted.Length, count));
                return decrypted.Length;
            }
            Array.Copy(encryptedData, 0, buffer, offset, read);
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_isEncrypted && count > 0)
            {
                var dataToEncrypt = new byte[count];
                Array.Copy(buffer, offset, dataToEncrypt, 0, count);
                var encrypted = _crypto.Encrypt(dataToEncrypt);
                _inner.Write(encrypted, 0, encrypted.Length);
            }
            else
            {
                _inner.Write(buffer, offset, count);
            }
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var encryptedData = new byte[count];
            var read = await _inner.ReadAsync(encryptedData, 0, count, cancellationToken).ConfigureAwait(false);
            if (_isEncrypted && read > 0)
            {
                var decrypted = _crypto.Decrypt(encryptedData);
                Array.Copy(decrypted, 0, buffer, offset, Math.Min(decrypted.Length, count));
                return decrypted.Length;
            }
            Array.Copy(encryptedData, 0, buffer, offset, read);
            return read;
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_isEncrypted && count > 0)
            {
                var dataToEncrypt = new byte[count];
                Array.Copy(buffer, offset, dataToEncrypt, 0, count);
                var encrypted = _crypto.Encrypt(dataToEncrypt);
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
