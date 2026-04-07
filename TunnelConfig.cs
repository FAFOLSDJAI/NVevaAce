using System;

namespace NVevaAce
{
    public class TunnelConfig
    {
        public int LocalPort { get; set; }
        public int RemotePort { get; set; }
        public string Protocol { get; set; } = "tcp";
        public string AuthToken { get; set; } = "";
        public bool UseEncryption { get; set; } = false;
        public int HeartbeatTimeout { get; set; } = 60;
        public int PoolCount { get; set; } = 1;
    }
}