using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NVevaAce
{
    /// <summary>
    /// 连接池 - 管理预连接的 TCP 连接
    /// 参考 frp 的连接池设计，提高连接复用性
    /// </summary>
    public class ConnectionPool : IDisposable
    {
        private readonly ILogger _logger;
        private readonly string _serverAddr;
        private readonly int _serverPort;
        private readonly int _poolSize;
        private readonly ConcurrentQueue<TcpClient> _pool = new ConcurrentQueue<TcpClient>();
        private readonly SemaphoreSlim _poolSemaphore;
        private bool _disposed = false;
        private int _createdCount = 0;
        private int _inUseCount = 0;

        public ConnectionPool(ILogger logger, string serverAddr, int serverPort, int poolSize)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serverAddr = serverAddr ?? throw new ArgumentNullException(nameof(serverAddr));
            _serverPort = poolSize > 0 ? poolSize : 1;
            _poolSize = poolSize;
            _poolSemaphore = new SemaphoreSlim(poolSize, poolSize);
            
            _logger.Log($"连接池初始化：服务器 {_serverAddr}:{_serverPort}, 大小：{_poolSize}");
        }

        /// <summary>
        /// 从池中获取连接
        /// </summary>
        public async Task<TcpClient> AcquireAsync(CancellationToken ct = default)
        {
            if (await _poolSemaphore.WaitAsync(TimeSpan.FromSeconds(30), ct) == false)
            {
                throw new TimeoutException("等待连接池资源超时");
            }

            Interlocked.Increment(ref _inUseCount);
            
            if (_pool.TryDequeue(out var client))
            {
                _logger.Log($"从连接池获取连接 (池中剩余：{GetPoolSize()})");
                return client;
            }

            // 创建新连接
            client = new TcpClient();
            await client.ConnectAsync(_serverAddr, _serverPort, ct).ConfigureAwait(false);
            
            Interlocked.Increment(ref _createdCount);
            _logger.Log($"创建新连接到 {_serverAddr}:{_serverPort} (总创建：{_createdCount})");
            
            return client;
        }

        /// <summary>
        /// 归还连接到池中
        /// </summary>
        public void Release(TcpClient client)
        {
            if (client == null) return;
            
            if (client.Connected)
            {
                _pool.Enqueue(client);
                _logger.Log($"归还连接到池 (池中数量：{GetPoolSize()})");
            }
            else
            {
                client.Dispose();
                _logger.Log("连接已断开，直接销毁");
            }
            
            Interlocked.Decrement(ref _inUseCount);
            _poolSemaphore.Release();
        }

        /// <summary>
        /// 预填充连接池
        /// </summary>
        public async Task PreFillAsync(int count, CancellationToken ct = default)
        {
            var tasks = new List<Task>();
            
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var client = new TcpClient();
                    await client.ConnectAsync(_serverAddr, _serverPort, ct).ConfigureAwait(false);
                    _pool.Enqueue(client);
                    _logger.Log($"预填充连接 #{i + 1}/{count}");
                }
                catch (Exception ex)
                {
                    _logger.Log($"预填充连接失败：{ex.Message}");
                }
            }
            
            _logger.Log($"连接池预填充完成，池中数量：{GetPoolSize()}");
        }

        /// <summary>
        /// 清空连接池
        /// </summary>
        public void Clear()
        {
            while (_pool.TryDequeue(out var client))
            {
                client?.Dispose();
            }
            _logger.Log("连接池已清空");
        }

        /// <summary>
        /// 获取池中连接数量
        /// </summary>
        public int GetPoolSize()
        {
            return _pool.Count;
        }

        /// <summary>
        /// 获取使用统计
        /// </summary>
        public PoolStats GetStats()
        {
            return new PoolStats
            {
                PoolSize = GetPoolSize(),
                InUseCount = _inUseCount,
                CreatedCount = _createdCount,
                MaxSize = _poolSize
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            
            Clear();
            _poolSemaphore?.Dispose();
            _disposed = true;
            _logger.Log("连接池已销毁");
        }
    }

    /// <summary>
    /// 连接池统计信息
    /// </summary>
    public class PoolStats
    {
        public int PoolSize { get; set; }
        public int InUseCount { get; set; }
        public int CreatedCount { get; set; }
        public int MaxSize { get; set; }

        public override string ToString()
        {
            return $"连接池统计：池中={PoolSize}, 使用中={InUseCount}, 已创建={CreatedCount}, 最大={MaxSize}";
        }
    }
}
