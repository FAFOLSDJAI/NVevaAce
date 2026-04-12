using System;
using System.IO;
using System.Text.Json;

namespace NVevaAce
{
    /// <summary>
    /// Application configuration loaded from appsettings.json
    /// </summary>
    public class AppConfig
    {
        public string ServerAddr { get; set; } = "your-server.com";
        public int ServerPort { get; set; } = 7000;
        public string Token { get; set; } = "your-auth-token";
        public string Protocol { get; set; } = "tcp";
        /// <summary>
    /// 启用 TLS 加密传输
    /// </summary>
    public bool TlsEnable { get; set; } = false;

    /// <summary>
    /// TLS 证书路径
    /// </summary>
    public string? TlsCertPath { get; set; }

    /// <summary>
    /// TLS 证书密码
    /// </summary>
    public string? TlsCertPassword { get; set; }
        public int PoolCount { get; set; } = 5;
        public bool TcpMux { get; set; } = true;
        public int HeartbeatInterval { get; set; } = 30;
        public int HeartbeatTimeout { get; set; } = 90;
        public List<TunnelConfig>? Tunnels { get; set; }

        private static string ConfigPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load config: {ex.Message}");
            }
            return new AppConfig();
        }

        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save config: {ex.Message}");
            }
        }

        public void Reload()
        {
            Load();
        }
    }
}