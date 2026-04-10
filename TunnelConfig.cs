using System;
using System.Collections.Generic;

namespace NVevaAce
{
    /// <summary>
    /// Tunnel configuration - defines parameters for a single tunnel
    /// </summary>
    public class TunnelConfig
    {
        /// <summary>
        /// Local port to listen on
        /// </summary>
        public int LocalPort { get; set; }

        /// <summary>
        /// Remote port on the frp server
        /// </summary>
        public int RemotePort { get; set; }

        /// <summary>
        /// Protocol type: tcp, udp, http, https, stcp, xtcp
        /// </summary>
        public string Protocol { get; set; } = "tcp";

        /// <summary>
        /// Authentication token (overrides global config if set)
        /// </summary>
        public string AuthToken { get; set; } = "";

        /// <summary>
        /// Enable encryption
        /// </summary>
        public bool UseEncryption { get; set; } = false;

        /// <summary>
        /// Enable compression
        /// </summary>
        public bool UseCompression { get; set; } = false;

        /// <summary>
        /// Heartbeat timeout in seconds
        /// </summary>
        public int HeartbeatTimeout { get; set; } = 60;

        /// <summary>
        /// Connection pool size
        /// </summary>
        public int PoolCount { get; set; } = 1;

        /// <summary>
        /// Bandwidth limit in KB/s (0 = unlimited)
        /// </summary>
        public int BandwidthLimit { get; set; } = 0;

        /// <summary>
        /// Custom domain for HTTP/HTTPS tunnels
        /// </summary>
        public string? CustomDomain { get; set; }

        /// <summary>
        /// Subdomain for HTTP/HTTPS tunnels
        /// </summary>
        public string? SubDomain { get; set; }

        /// <summary>
        /// Health check path for HTTP tunnels
        /// </summary>
        public string? HealthCheckPath { get; set; }

        /// <summary>
        /// Health check interval in seconds
        /// </summary>
        public int HealthCheckInterval { get; set; } = 10;

        /// <summary>
        /// Load balance strategy: round_robin, least_connections, weighted_round_robin, random
        /// </summary>
        public string? LoadBalanceStrategy { get; set; } = "round_robin";

        /// <summary>
        /// Backend servers for load balancing
        /// </summary>
        public List<BackendConfig>? Backends { get; set; }

        public override string ToString()
        {
            return $"{Protocol?.ToUpper() ?? "TCP"} {LocalPort} -> {RemotePort}";
        }
    }

    /// <summary>
    /// Backend server configuration for load balancing
    /// </summary>
    public class BackendConfig
    {
        /// <summary>
        /// Backend server address
        /// </summary>
        public string Address { get; set; } = "127.0.0.1";

        /// <summary>
        /// Backend server port
        /// </summary>
        public int Port { get; set; }

        /// <summary>
        /// Backend weight for weighted load balancing
        /// </summary>
        public int Weight { get; set; } = 1;

        /// <summary>
        /// Backend is enabled
        /// </summary>
        public bool Enabled { get; set; } = true;
    }
}