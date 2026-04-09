using System;
using System.IO;
using System.Text;

namespace NVevaAce
{
    /// <summary>
    /// 日志级别
    /// </summary>
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warn = 2,
        Error = 3
    }

    /// <summary>
    /// 日志记录器 - 支持级别过滤和文件输出
    /// 参考 frp 的日志设计
    /// </summary>
    public class Logger : ILogger
    {
        private readonly string _logLevel;
        private readonly string? _logFile;
        private readonly bool _disableColor;
        private readonly object _lock = new object();
        private LogLevel _currentLevel;

        public Logger(string logLevel = "info", string? logFile = null, bool disableColor = false)
        {
            _logLevel = logLevel?.ToLower() ?? "info";
            _logFile = logFile;
            _disableColor = disableColor;

            _currentLevel = _logLevel switch
            {
                "debug" => LogLevel.Debug,
                "info" => LogLevel.Info,
                "warn" => LogLevel.Warn,
                "error" => LogLevel.Error,
                _ => LogLevel.Info
            };

            // 初始化日志文件
            if (!string.IsNullOrEmpty(_logFile))
            {
                try
                {
                    var dir = Path.GetDirectoryName(_logFile);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"创建日志目录失败：{ex.Message}");
                }
            }
        }

        public void Log(string message)
        {
            Log(LogLevel.Info, message);
        }

        public void Log(LogLevel level, string message)
        {
            if (level < _currentLevel) return;

            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var levelStr = level.ToString().ToUpper();
            var fullMessage = $"[{timestamp}] [{levelStr}] {message}";

            // 控制台输出
            WriteToConsole(fullMessage, level);

            // 文件输出
            if (!string.IsNullOrEmpty(_logFile))
            {
                WriteToFile(fullMessage);
            }
        }

        private void WriteToConsole(string message, LogLevel level)
        {
            if (_disableColor)
            {
                Console.WriteLine(message);
                return;
            }

            // 根据级别设置颜色
            var originalColor = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = level switch
                {
                    LogLevel.Debug => ConsoleColor.Gray,
                    LogLevel.Info => ConsoleColor.White,
                    LogLevel.Warn => ConsoleColor.Yellow,
                    LogLevel.Error => ConsoleColor.Red,
                    _ => ConsoleColor.White
                };

                Console.WriteLine(message);
            }
            finally
            {
                Console.ForegroundColor = originalColor;
            }
        }

        private void WriteToFile(string message)
        {
            try
            {
                lock (_lock)
                {
                    File.AppendAllText(_logFile, message + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"写入日志文件失败：{ex.Message}");
            }
        }

        public void Debug(string message) => Log(LogLevel.Debug, message);
        public void Info(string message) => Log(LogLevel.Info, message);
        public void Warn(string message) => Log(LogLevel.Warn, message);
        public void Error(string message) => Log(LogLevel.Error, message);
    }

    /// <summary>
    /// 简单的控制台日志记录器（用于兼容性）
    /// </summary>
    public class ConsoleLogger : ILogger
    {
        private readonly string _prefix;

        public ConsoleLogger(string prefix = "")
        {
            _prefix = prefix;
        }

        public void Log(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            Console.WriteLine($"[{timestamp}] {(_prefix.Length > 0 ? _prefix + " " : "")}{message}");
        }
    }
}
