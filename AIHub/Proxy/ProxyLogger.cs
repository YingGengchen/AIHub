using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AIHub.Proxy
{
    /// <summary>
    /// 线程安全的简易日志：内存环形缓冲（供面板显示）+ 按天滚动的文件日志。
    /// 只记录请求元数据，不记录消息内容。
    /// </summary>
    internal sealed class ProxyLogger
    {
        private const int MaxMemoryLines = 2000;
        private const long MaxFileBytes = 10L * 1024 * 1024;
        private const int RetentionDays = 14;

        private readonly object _gate = new object();
        private readonly Queue<string> _recent = new Queue<string>();

        private volatile bool _enabled = true;
        private string _logDirectory = ProxyPaths.LogDirectory;

        public ProxyLogger()
        {
            ProxyPaths.EnsureDirectories();
            CleanupOldLogs();
        }

        public event Action<string>? LineLogged;

        /// <summary>是否记录日志。关闭后既不写文件，也不进内存缓冲。</summary>
        public bool Enabled => _enabled;

        /// <summary>当前实际使用的日志目录。</summary>
        public string LogDirectory => _logDirectory;

        public string CurrentLogFile => Path.Combine(LogDirectory, $"proxy-{DateTime.Now:yyyy-MM-dd}.log");

        /// <summary>应用配置里的日志开关与目录。</summary>
        public void Configure(bool enabled, string? directory)
        {
            lock (_gate)
            {
                _enabled = enabled;
                _logDirectory = ResolveDirectory(directory);

                if (!enabled)
                {
                    _recent.Clear();
                }
            }

            if (enabled)
            {
                CleanupOldLogs();
            }
        }

        private static string ResolveDirectory(string? directory)
        {
            var normalized = ProxyConfig.NormalizeLogDirectory(directory);
            return normalized.Length == 0 ? ProxyPaths.LogDirectory : normalized;
        }

        /// <summary>删除当前日志目录下的全部日志文件，返回删除数量。</summary>
        public int DeleteAllLogs()
        {
            lock (_gate)
            {
                _recent.Clear();

                var removed = 0;
                try
                {
                    if (!Directory.Exists(LogDirectory))
                    {
                        return 0;
                    }

                    foreach (var file in Directory.EnumerateFiles(LogDirectory, "proxy-*.log*"))
                    {
                        try
                        {
                            File.Delete(file);
                            removed++;
                        }
                        catch (Exception)
                        {
                            // 被占用的文件跳过。
                        }
                    }
                }
                catch (Exception)
                {
                    // 目录不可用时忽略。
                }

                return removed;
            }
        }

        public void Info(string message) => Write("INFO", message);

        public void Warn(string message) => Write("WARN", message);

        public void Error(string message) => Write("ERROR", message);

        public string[] GetRecentLines()
        {
            lock (_gate)
            {
                return _recent.ToArray();
            }
        }

        public void ClearMemory()
        {
            lock (_gate)
            {
                _recent.Clear();
            }
        }

        private void Write(string level, string message)
        {
            if (!_enabled)
            {
                return;
            }

            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {level,-5}  {message}";

            lock (_gate)
            {
                _recent.Enqueue(line);
                while (_recent.Count > MaxMemoryLines)
                {
                    _recent.Dequeue();
                }

                try
                {
                    AppendToFile(line);
                }
                catch (Exception)
                {
                    // 日志写入失败不能影响转发主流程。
                }
            }

            try
            {
                LineLogged?.Invoke(line);
            }
            catch (Exception)
            {
                // 忽略界面订阅者异常。
            }
        }

        private void AppendToFile(string line)
        {
            Directory.CreateDirectory(LogDirectory);
            var path = CurrentLogFile;

            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxFileBytes)
            {
                Roll(path);
            }

            File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
        }

        private static void Roll(string path)
        {
            var first = path + ".1";
            var second = path + ".2";

            if (File.Exists(second))
            {
                File.Delete(second);
            }

            if (File.Exists(first))
            {
                File.Move(first, second);
            }

            if (File.Exists(path))
            {
                File.Move(path, first);
            }
        }

        private void CleanupOldLogs()
        {
            try
            {
                var cutoff = DateTime.Now.AddDays(-RetentionDays);
                foreach (var file in Directory.EnumerateFiles(LogDirectory, "proxy-*.log*"))
                {
                    try
                    {
                        if (File.GetLastWriteTime(file) < cutoff)
                        {
                            File.Delete(file);
                        }
                    }
                    catch (Exception)
                    {
                        // 单个文件清理失败可忽略。
                    }
                }
            }
            catch (Exception)
            {
                // 日志目录不可用时忽略。
            }
        }
    }
}
