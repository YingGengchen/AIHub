using System;

namespace AIHub.Proxy
{
    /// <summary>
    /// 已经占用端口的 AI Hub 代理实例信息（来自另一个 Visual Studio 进程）。
    /// </summary>
    internal sealed class ProxyHostInfo
    {
        public ProxyHostInfo(int port, int processId, string version)
        {
            Port = port;
            ProcessId = processId;
            Version = version ?? string.Empty;
        }

        public int Port { get; }

        public int ProcessId { get; }

        public string Version { get; }

        public string Describe()
        {
            return ProcessId > 0
                ? $"PID {ProcessId}"
                : "另一个 Visual Studio 实例";
        }
    }

    /// <summary>
    /// 端口已被另一个 AI Hub 代理实例占用（不是启动失败，而是可以共用）。
    /// </summary>
    internal sealed class ProxyAlreadyHostedException : InvalidOperationException
    {
        public ProxyAlreadyHostedException(ProxyHostInfo host)
            : base($"端口 {host.Port} 已由{host.Describe()}的 AI Hub 代理占用。")
        {
            Host = host;
        }

        public ProxyHostInfo Host { get; }
    }
}
