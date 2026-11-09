using System;
using System.IO;
using System.Threading;

namespace AIHub.Proxy
{
    /// <summary>
    /// 整个扩展共享的代理服务（配置、日志、本地 HTTP 服务）。
    /// 同一台机器上多个 Visual Studio 实例共用同一份配置，端口只由其中一个进程监听。
    /// </summary>
    internal sealed class ProxyService
    {
        private static readonly Lazy<ProxyService> LazyInstance =
            new Lazy<ProxyService>(() => new ProxyService(), LazyThreadSafetyMode.ExecutionAndPublication);

        private readonly object _watchGate = new object();

        private int _autoStartApplied;
        private int _watchdogBusy;
        private Timer? _watchdog;
        private DateTime _configStamp;

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

            _configStamp = ReadConfigStamp();

            Logger.Configure(Config.LogEnabled, Config.LogDirectory);
            Server.ApplyConfig(Config);
            Server.StateChanged += RaiseStateChanged;
        }

        public static ProxyService Instance => LazyInstance.Value;

        public ProxyConfigStore Store { get; }

        public ProxyLogger Logger { get; }

        public ProxyServer Server { get; }

        public ProxyConfig Config { get; private set; }

        /// <summary>最近一次启动失败的原因，成功启动或被其它实例接管后自动清除。</summary>
        public string? LastError { get; private set; }

        /// <summary>端口正被另一个 Visual Studio 实例的 AI Hub 代理占用时的信息。</summary>
        public ProxyHostInfo? HostedBy { get; private set; }

        /// <summary>本实例没有监听端口，但同一端口上已有另一个实例的代理在跑。</summary>
        public bool IsHostedByOtherInstance => HostedBy != null;

        /// <summary>代理状态变化（本实例启停，或被其它实例接管、接管回来）。</summary>
        public event Action? StateChanged;

        /// <summary>配置文件被其它 Visual Studio 实例改写并重新载入后触发。</summary>
        public event Action? ConfigReloaded;

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

            // 先探测：已有实例在跑就直接复用，不报错。
            var host = ProxyServer.ProbeHost(Config.Port);
            if (host != null)
            {
                AdoptExistingHost(host);
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
            _configStamp = ReadConfigStamp();
            ApplyConfig(config);
            Logger.Info("配置已保存：" + Store.ConfigFile);
        }

        public void Start()
        {
            try
            {
                Server.Start(Config);
                HostedBy = null;
                LastError = null;
            }
            catch (ProxyAlreadyHostedException ex)
            {
                AdoptExistingHost(ex.Host);
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

        /// <summary>Visual Studio 退出时调用。</summary>
        public void Shutdown()
        {
            lock (_watchGate)
            {
                _watchdog?.Dispose();
                _watchdog = null;
            }

            Server.Stop();
        }

        private void AdoptExistingHost(ProxyHostInfo host)
        {
            HostedBy = host;
            LastError = null;
            Logger.Info($"端口 {host.Port} 已有 AI Hub 代理在运行（{host.Describe()}），本实例直接复用，不再重复监听。");
            ArmWatchdog();
            RaiseStateChanged();
        }

        private void ArmWatchdog()
        {
            lock (_watchGate)
            {
                if (_watchdog != null)
                {
                    return;
                }

                // 每 5 秒检查一次：托管者退出后自动接管，配置被其它实例改动后自动重载。
                _watchdog = new Timer(_ => WatchdogTick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            }
        }

        private void WatchdogTick()
        {
            if (Interlocked.Exchange(ref _watchdogBusy, 1) != 0)
            {
                return;
            }

            try
            {
                if (Server.IsRunning)
                {
                    if (HostedBy != null)
                    {
                        HostedBy = null;
                        RaiseStateChanged();
                    }

                    ReloadConfigIfChanged();
                    return;
                }

                var host = ProxyServer.ProbeHost(Config.Port);
                if (host != null)
                {
                    if (HostedBy == null || HostedBy.ProcessId != host.ProcessId)
                    {
                        HostedBy = host;
                        LastError = null;
                        RaiseStateChanged();
                    }

                    return;
                }

                if (HostedBy == null)
                {
                    return;
                }

                // 原来的托管者已经退出，本实例接管端口。
                HostedBy = null;
                Logger.Info("原有 AI Hub 代理实例已退出，本实例接管端口。");

                try
                {
                    Server.Start(Config);
                    LastError = null;
                    _configStamp = ReadConfigStamp();
                }
                catch (ProxyAlreadyHostedException ex)
                {
                    HostedBy = ex.Host;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Logger.Error("接管代理失败：" + ex.Message);
                }

                RaiseStateChanged();
            }
            catch (Exception)
            {
                // 看门狗自身异常不能影响 Visual Studio。
            }
            finally
            {
                Interlocked.Exchange(ref _watchdogBusy, 0);
            }
        }

        private void ReloadConfigIfChanged()
        {
            var stamp = ReadConfigStamp();
            if (stamp == DateTime.MinValue || stamp == _configStamp)
            {
                return;
            }

            _configStamp = stamp;

            var config = Store.Load(out var warning);
            if (!string.IsNullOrEmpty(warning))
            {
                Logger.Warn(warning!);
                return;
            }

            ApplyConfig(config);
            Logger.Info("检测到配置文件被其它实例修改，已重新载入。");

            try
            {
                ConfigReloaded?.Invoke();
            }
            catch (Exception)
            {
                // 忽略界面订阅者异常。
            }

            RaiseStateChanged();
        }

        private DateTime ReadConfigStamp()
        {
            try
            {
                var info = new FileInfo(Store.ConfigFile);
                return info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
            }
            catch (Exception)
            {
                return DateTime.MinValue;
            }
        }

        private void RaiseStateChanged()
        {
            try
            {
                StateChanged?.Invoke();
            }
            catch (Exception)
            {
                // 忽略界面订阅者异常。
            }
        }
    }
}
