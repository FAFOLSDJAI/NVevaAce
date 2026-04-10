using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace NVevaAce
{
    /// <summary>
    /// 带宽限制器 - 实现基于令牌桶的流量控制
    /// 参考 frp 的带宽限制设计
    /// </summary>
    public class BandwidthLimiter : IDisposable
    {
        private readonly ILogger _logger;
        private readonly long _rateBytesPerSecond; // 字节/秒
        private readonly long _burstBytes;          // 突发容量
        private long _tokens;
        private readonly object _lock = new object();
        private DateTime _lastRefillTime;
        private bool _disposed = false;

        // 统计
        private long _totalBytesPassed = 0;
        private long _totalBytesLimited = 0;
        
        public long RateBytesPerSecond => _rateBytesPerSecond;
        public long TotalBytesPassed => _totalBytesPassed;
        public long TotalBytesLimited => _totalBytesLimited;

        public BandwidthLimiter(ILogger logger, int rateKBps = 0)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            
            // rateKBps = 0 表示不限制
            _rateBytesPerSecond = rateKBps > 0 ? rateKBps * 1024 : long.MaxValue;
            _burstBytes = _rateBytesPerSecond; // 突发容量等于速率
            
            _lastRefillTime = DateTime.Now;
            _tokens = _burstBytes;

            if (rateKBps > 0)
            {
                _logger.Log($"[带宽限制] 已启用，限制: {rateKBps} KB/s");
            }
            else
            {
                _logger.Log("[带宽限制] 未启用（无限制）");
            }
        }

        /// <summary>
        /// 等待并获取传输数据的令牌
        /// </summary>
        public async Task WaitForTokensAsync(int bytes, CancellationToken ct = default)
        {
            if (_rateBytesPerSecond == long.MaxValue) return; // 无限制

            while (true)
            {
                RefillTokens();
                
                lock (_lock)
                {
                    if (_tokens >= bytes)
                    {
                        _tokens -= bytes;
                        _totalBytesPassed += bytes;
                        return;
                    }
                }

                // 计算需要等待的时间
                var needed = bytes - _tokens;
                var waitMs = (int)((needed * 1000) / _rateBytesPerSecond);
                
                _totalBytesLimited += needed;
                
                try
                {
                    await Task.Delay(Math.Max(1, waitMs), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }
        }

        /// <summary>
        /// 补充令牌
        /// </summary>
        private void RefillTokens()
        {
            var now = DateTime.Now;
            var elapsed = (now - _lastRefillTime).TotalSeconds;
            
            if (elapsed > 0)
            {
                lock (_lock)
                {
                    var tokensToAdd = (long)(_rateBytesPerSecond * elapsed);
                    _tokens = Math.Min(_burstBytes, _tokens + tokensToAdd);
                }
                _lastRefillTime = now;
            }
        }

        /// <summary>
        /// 获取带宽使用统计
        /// </summary>
        public BandwidthStats GetStats()
        {
            RefillTokens();
            return new BandwidthStats
            {
                RateKBps = _rateBytesPerSecond / 1024,
                AvailableBytes = _tokens,
                TotalBytesPassed = _totalBytesPassed,
                TotalBytesLimited = _totalBytesLimited
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            _logger.Log($"[带宽限制] 已销毁，通过: {_totalBytesPassed} bytes, 限制: {_totalBytesLimited} bytes");
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// 带宽统计信息
    /// </summary>
    public class BandwidthStats
    {
        public long RateKBps { get; set; }
        public long AvailableBytes { get; set; }
        public long TotalBytesPassed { get; set; }
        public long TotalBytesLimited { get; set; }

        public override string ToString()
        {
            return $"带宽统计：限制={RateKBps}KB/s, 可用={AvailableBytes}, 通过={TotalBytesPassed}, 限流={TotalBytesLimited}";
        }
    }

    /// <summary>
    /// 负载均衡策略
    /// </summary>
    public enum LoadBalanceStrategy
    {
        /// <summary>轮询</summary>
        RoundRobin,
        /// <summary>最少连接</summary>
        LeastConnections,
        /// <summary>加权轮询</summary>
        WeightedRoundRobin,
        /// <summary>随机</summary>
        Random
    }

    /// <summary>
    /// 后端服务器
    /// </summary>
    public class Backend
    {
        public string Address { get; set; } = "";
        public int Port { get; set; }
        public int Weight { get; set; } = 1;
        public bool IsHealthy { get; set; } = true;
        public int ActiveConnections { get; set; } = 0;
        public DateTime LastCheck { get; set; } = DateTime.Now;

        public string Endpoint => $"{Address}:{Port}";
    }

    /// <summary>
    /// 负载均衡器 - 在多个后端服务器之间分发请求
    /// 参考 frp 的负载均衡设计
    /// </summary>
    public class LoadBalancer : IDisposable
    {
        private readonly ILogger _logger;
        private readonly LoadBalanceStrategy _strategy;
        private readonly ConcurrentDictionary<string, Backend> _backends = new ConcurrentDictionary<string, Backend>();
        private readonly object _lock = new object();
        private int _roundRobinIndex = 0;
        private readonly Random _random = new Random();
        private bool _disposed = false;

        public LoadBalancer(ILogger logger, LoadBalanceStrategy strategy = LoadBalanceStrategy.RoundRobin)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _strategy = strategy;
            _logger.Log($"[负载均衡] 初始化，策略: {strategy}");
        }

        /// <summary>
        /// 添加后端服务器
        /// </summary>
        public void AddBackend(string address, int port, int weight = 1)
        {
            var key = $"{address}:{port}";
            var backend = new Backend
            {
                Address = address,
                Port = port,
                Weight = weight,
                IsHealthy = true
            };

            _backends[key] = backend;
            _logger.Log($"[负载均衡] 添加后端: {key} (权重: {weight})");
        }

        /// <summary>
        /// 移除后端服务器
        /// </summary>
        public void RemoveBackend(string address, int port)
        {
            var key = $"{address}:{port}";
            if (_backends.TryRemove(key, out _))
            {
                _logger.Log($"[负载均衡] 移除后端: {key}");
            }
        }

        /// <summary>
        /// 更新后端健康状态
        /// </summary>
        public void UpdateBackendHealth(string address, int port, bool isHealthy)
        {
            var key = $"{address}:{port}";
            if (_backends.TryGetValue(key, out var backend))
            {
                backend.IsHealthy = isHealthy;
                backend.LastCheck = DateTime.Now;
                _logger.Log($"[负载均衡] 后端 {key} 健康状态: {(isHealthy ? "健康" : "不健康")}");
            }
        }

        /// <summary>
        /// 增加后端连接计数
        /// </summary>
        public void IncrementConnections(string address, int port)
        {
            var key = $"{address}:{port}";
            if (_backends.TryGetValue(key, out var backend))
            {
                Interlocked.Increment(ref backend.ActiveConnections);
            }
        }

        /// <summary>
        /// 减少后端连接计数
        /// </summary>
        public void DecrementConnections(string address, int port)
        {
            var key = $"{address}:{port}";
            if (_backends.TryGetValue(key, out var backend))
            {
                Interlocked.Decrement(ref backend.ActiveConnections);
            }
        }

        /// <summary>
        /// 选择一个后端服务器
        /// </summary>
        public Backend? SelectBackend()
        {
            var healthyBackends = _backends.Values.Where(b => b.IsHealthy).ToList();
            
            if (healthyBackends.Count == 0)
            {
                _logger.Log("[负载均衡] 无健康的后端服务器");
                return null;
            }

            Backend? selected = null;

            switch (_strategy)
            {
                case LoadBalanceStrategy.RoundRobin:
                    selected = SelectRoundRobin(healthyBackends);
                    break;
                case LoadBalanceStrategy.LeastConnections:
                    selected = SelectLeastConnections(healthyBackends);
                    break;
                case LoadBalanceStrategy.WeightedRoundRobin:
                    selected = SelectWeightedRoundRobin(healthyBackends);
                    break;
                case LoadBalanceStrategy.Random:
                    selected = healthyBackends[_random.Next(healthyBackends.Count)];
                    break;
            }

            if (selected != null)
            {
                _logger.Log($"[负载均衡] 选择后端: {selected.Endpoint} (策略: {_strategy})");
            }

            return selected;
        }

        private Backend? SelectRoundRobin(System.Collections.Generic.List<Backend> backends)
        {
            lock (_lock)
            {
                var index = _roundRobinIndex++ % backends.Count;
                return backends[index];
            }
        }

        private Backend? SelectLeastConnections(System.Collections.Generic.List<Backend> backends)
        {
            return backends.OrderBy(b => b.ActiveConnections).First();
        }

        private Backend? SelectWeightedRoundRobin(System.Collections.Generic.List<Backend> backends)
        {
            var totalWeight = backends.Sum(b => b.Weight);
            var randomWeight = _random.Next(totalWeight);
            
            int currentWeight = 0;
            foreach (var backend in backends)
            {
                currentWeight += backend.Weight;
                if (randomWeight < currentWeight)
                {
                    return backend;
                }
            }
            
            return backends[0];
        }

        /// <summary>
        /// 获取所有后端状态
        /// </summary>
        public System.Collections.Generic.List<Backend> GetAllBackends()
        {
            return _backends.Values.ToList();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _backends.Clear();
            _logger.Log("[负载均衡] 已销毁");
            GC.SuppressFinalize(this);
        }
    }
}