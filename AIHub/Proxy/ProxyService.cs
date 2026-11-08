using System;
using System.Threading;

namespace AIHub.Proxy
{
    /// <summary>
    /// 整个扩展共享的代理服务（配置、日志、本地 HTTP 服务）。
    /// </summary>
    internal sealed class ProxyService
    {
        private static readonly Lazy<ProxyService> LazyInstance =
            new Lazy<ProxyService>(() => new ProxyService(), LazyThreadSafetyMode.ExecutionAndPublication);

        private int _autoStartApplied;

        private ProxyService()
        {
            Logger = new ProxyLogger();
            Server = new ProxyServer(Logger);
            Store = new ProxyConfigStore();

            Config = Store.Load(out var warning);
            if (!string.IsNullOrEmpty(warning))
            {
                Logger.Warn(warning!);
            }

            Logger.Configure(Config.LogEnabled, Config.LogDirectory);
            Server.ApplyConfig(Config);
        }

        public static ProxyService Instance => LazyInstance.Value;

        public ProxyConfigStore Store { get; }

        public ProxyLogger Logger { get; }

        public ProxyServer Server { get; }

        public ProxyConfig Config { get; private set; }

        /// <summary>最近一次启动失败的原因，成功启动后自动清除。</summary>
        public string? LastError { get; private set; }

        public void InitializeAutoStart()
        {
            if (Interlocked.Exchange(ref _autoStartApplied, 1) != 0)
            {
                return;
            }

            if (!Config.AutoStart)
            {
                Logger.Info("已关闭随 Visual Studio 自动启动。");
                return;
            }

            try
            {
                Start();
            }
            catch (Exception ex)
            {
                Logger.Error("自动启动代理失败：" + ex.Message);
            }
        }

        public void ApplyConfig(ProxyConfig config)
        {
            Config = config;
            Logger.Configure(config.LogEnabled, config.LogDirectory);
            Server.ApplyConfig(config);
        }

        public void SaveAndApply(ProxyConfig config)
        {
            Store.Save(config);
            ApplyConfig(config);
            Logger.Info("配置已保存：" + Store.ConfigFile);
        }

        public void Start()
        {
            try
            {
                Server.Start(Config);
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                throw;
            }
        }

        public void Stop()
        {
            Server.Stop();
        }
    }
}
