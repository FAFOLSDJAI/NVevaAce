using System;

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

        public override string ToString()
        {
            return $"{Protocol?.ToUpper() ?? "TCP"} {LocalPort} -> {RemotePort}";
        }
    }
}
