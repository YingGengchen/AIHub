using System;
using System.IO;
using System.Threading;
using Newtonsoft.Json;

namespace AIHub.Proxy
{
    /// <summary>
    /// 整个扩展共享的代理服务（配置、日志、本地 HTTP 服务）。
    /// 同一台机器上多个 Visual Studio 实例共用同一份配置，端口只由其中一个进程监听；
    /// 谁在监听由 <see cref="ProxyCoordinator"/> 仲裁，交接完全靠事件唤醒，没有定时轮询。
    /// </summary>
    internal sealed class ProxyService
    {
        private const int StartTimeoutMs = 2000;
        private const int StopTimeoutMs = 4000;

        private static readonly Lazy<ProxyService> LazyInstance =
            new Lazy<ProxyService>(() => new ProxyService(), LazyThreadSafetyMode.ExecutionAndPublication);

        private readonly object _lifeGate = new object();
        private readonly ManualResetEvent _stopRequested = new ManualResetEvent(false);
        private readonly ManualResetEvent _restartRequested = new ManualResetEvent(false);
        private readonly ManualResetEvent _startReported = new ManualResetEvent(false);
        private readonly AutoResetEvent _restartCompleted = new AutoResetEvent(false);

        private ProxyCoordinator? _coordinator;
        private int _coordinatorPort = -1;
        private Thread? _hostThread;
        private bool _startOk;
        private string? _startError;
        private volatile bool _stopEverything;
        private volatile bool _shuttingDown;

        /// <summary>用户期望代理处于运行状态（用于端口变更后自动按新端口重新仲裁）。</summary>
        private volatile bool _intentRunning;

        private int _autoStartApplied;
        private int _reloading;
        private DateTime _configStamp;
        private FileSystemWatcher? _configWatcher;
        private Timer? _configDebounce;

        private volatile ProxyConfig _config;
        private volatile ProxyHostInfo? _hostedBy;
        private volatile string? _lastError;

        private ProxyService()
        {
            Logger = new ProxyLogger();
            Server = new ProxyServer(Logger);
            Store = new ProxyConfigStore();

            _config = Store.Load(out var warning);
            if (!string.IsNullOrEmpty(warning))
            {
                Logger.Warn(warning!);
            }

            _configStamp = ReadConfigStamp();

            Logger.Configure(_config.LogEnabled, _config.LogDirectory);
            Server.ApplyConfig(_config);
            Server.StateChanged += RaiseStateChanged;
        }

        public static ProxyService Instance => LazyInstance.Value;

        public ProxyConfigStore Store { get; }

        public ProxyLogger Logger { get; }

        public ProxyServer Server { get; }

        public ProxyConfig Config => _config;

        /// <summary>最近一次启动失败的原因，成功启动或被其它实例接管后自动清除。</summary>
        public string? LastError
        {
            get => _lastError;
            private set => _lastError = value;
        }

        /// <summary>端口正被另一个 Visual Studio 实例的 AI Hub 代理占用时的信息。</summary>
        public ProxyHostInfo? HostedBy
        {
            get => _hostedBy;
            private set => _hostedBy = value;
        }

        /// <summary>本实例没有监听端口，但同一端口上已有另一个实例的代理在跑。</summary>
        public bool IsHostedByOtherInstance => HostedBy != null;

        /// <summary>代理状态变化（本实例启停、被其它实例接管、接管回来或同步停止）。</summary>
        public event Action? StateChanged;

        /// <summary>配置文件被其它 Visual Studio 实例改写并重新载入后触发。</summary>
        public event Action? ConfigReloaded;

        public void InitializeAutoStart()
        {
            if (Interlocked.Exchange(ref _autoStartApplied, 1) != 0)
            {
                return;
            }

            EnsureConfigWatcher();

            if (!_config.AutoStart)
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
            _config = config;
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

        /// <summary>
        /// 启动代理。若端口已被另一个实例的 AI Hub 占用，则转为「托管」状态，
        /// 对方释放端口的瞬间自动接管，不会抛异常。
        /// </summary>
        public void Start()
        {
            _intentRunning = true;

            var host = ProxyServer.ProbeHost(_config.Port);
            if (host != null)
            {
                LaunchHostThread();
                AdoptExistingHost(host);
                return;
            }

            LaunchHostThread();

            if (!_startReported.WaitOne(StartTimeoutMs))
            {
                var other = ProxyServer.ProbeHost(_config.Port);
                if (other != null)
                {
                    AdoptExistingHost(other);
                    return;
                }

                _intentRunning = false;
                throw new InvalidOperationException($"端口 {_config.Port} 启动超时，可能已被其它程序占用。");
            }

            if (!_startOk && _startError != null)
            {
                _intentRunning = false;
                throw new InvalidOperationException(_startError);
            }
        }

        /// <summary>停止代理。任一实例停止后，其它实例也同步停止并保持停止。</summary>
        public void Stop()
        {
            _intentRunning = false;
            StopCore(true);
        }

        /// <summary>重启代理。宿主身份不变，因此不会出现被其它实例抢走端口的空档。</summary>
        public void Restart()
        {
            _intentRunning = true;

            var thread = _hostThread;
            if (thread == null || !Server.IsRunning || _coordinatorPort != _config.Port)
            {
                // 端口改过（或本来没在跑）：协调器要跟着换端口，只能完整启停一遍。
                StopCore(false);
                Start();
                return;
            }

            _restartCompleted.Reset();
            _restartRequested.Set();

            if (!_restartCompleted.WaitOne(StopTimeoutMs))
            {
                Logger.Warn("代理重启超时，改为重新启动。");
                StopCore(false);
                Start();
                return;
            }

            if (!Server.IsRunning && !string.IsNullOrEmpty(LastError))
            {
                throw new InvalidOperationException(LastError!);
            }
        }

        /// <summary>Visual Studio 退出时调用：交出端口，让其它实例立刻接管。</summary>
        public void Shutdown()
        {
            _shuttingDown = true;
            _intentRunning = false;
            DisposeConfigWatcher();
            StopCore(false);

            lock (_lifeGate)
            {
                _coordinator?.Dispose();
                _coordinator = null;
            }
        }

        #region 宿主线程

        private void LaunchHostThread()
        {
            lock (_lifeGate)
            {
                if (_shuttingDown)
                {
                    return;
                }

                StopThreadLocked();

                if (_coordinator == null || _coordinatorPort != _config.Port)
                {
                    _coordinator?.Dispose();
                    _coordinator = new ProxyCoordinator(_config.Port);
                    _coordinatorPort = _config.Port;
                }

                _coordinator.ClearStopSignal();

                _stopEverything = false;
                _startOk = false;
                _startError = null;
                _startReported.Reset();
                _stopRequested.Reset();
                _restartRequested.Reset();

                var thread = new Thread(HostLoop)
                {
                    IsBackground = true,
                    Name = "AIHub.Proxy.Host",
                };

                _hostThread = thread;
                thread.Start();
            }
        }

        /// <summary>调用方必须持有 <see cref="_lifeGate"/>。</summary>
        private void StopThreadLocked()
        {
            var thread = _hostThread;
            if (thread == null)
            {
                return;
            }

            _hostThread = null;

            if (thread == Thread.CurrentThread)
            {
                return;
            }

            _stopRequested.Set();
            if (!thread.Join(StopTimeoutMs))
            {
                Logger.Warn("代理线程未在预期时间内退出。");
            }
        }

        private void HostLoop()
        {
            var coordinator = _coordinator;
            if (coordinator == null)
            {
                return;
            }

            try
            {
                if (!coordinator.WaitForHostship(_stopRequested))
                {
                    ReportStart(false, null);

                    var stoppedBySelf = _stopRequested.WaitOne(0);
                    if (!stoppedBySelf && !_shuttingDown)
                    {
                        // 另一个实例点了「停止代理」，本实例同步停止。
                        HostedBy = null;
                        LastError = null;
                        Logger.Info("另一个 Visual Studio 实例停止了代理，本实例同步停止。");
                        RaiseStateChanged();
                    }

                    return;
                }

                if (coordinator.Port != _config.Port)
                {
                    // 排队等端口的这段时间里端口被改了，交回所有权并按新端口重新仲裁。
                    coordinator.Release(false);
                    QueueRelaunch();
                    return;
                }

                var error = TryStartServer();
                ReportStart(error == null, error);

                if (error != null)
                {
                    coordinator.Release(_stopEverything);
                    RaiseStateChanged();
                    return;
                }

                HostedBy = null;
                LastError = null;
                RaiseStateChanged();

                var waits = new WaitHandle[] { _stopRequested, _restartRequested, coordinator.StopSignal };

                while (true)
                {
                    var index = WaitHandle.WaitAny(waits);
                    if (index != 1)
                    {
                        // 本地停止，或别的实例广播了停止。
                        break;
                    }

                    // _restartRequested 是手工重置事件，必须清掉，否则会一直重启下去。
                    _restartRequested.Reset();

                    RestartServer();
                    if (!Server.IsRunning)
                    {
                        break;
                    }
                }

                Server.Stop();
                coordinator.Release(_stopEverything);
                RaiseStateChanged();
            }
            catch (Exception ex)
            {
                try
                {
                    Logger.Error("代理线程异常：" + ex.Message);
                }
                catch (Exception)
                {
                    // 忽略日志异常。
                }
            }
        }

        private string? TryStartServer()
        {
            try
            {
                Server.Start(_config);
                return null;
            }
            catch (ProxyAlreadyHostedException ex)
            {
                AdoptExistingHost(ex.Host);
                return ex.Message;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private void RestartServer()
        {
            try
            {
                Server.Stop();

                var error = TryStartServer();
                if (error == null)
                {
                    LastError = null;
                    Logger.Info("代理已重启。");
                }
                else
                {
                    LastError = error;
                    Logger.Error("重启代理失败：" + error);
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Logger.Error("重启代理失败：" + ex.Message);
            }
            finally
            {
                RaiseStateChanged();
                _restartCompleted.Set();
            }
        }

        private void ReportStart(bool ok, string? error)
        {
            _startOk = ok;
            _startError = error;
            _startReported.Set();
        }

        private void StopCore(bool stopEverything)
        {
            lock (_lifeGate)
            {
                _stopEverything = stopEverything;
                var coordinator = _coordinator;
                StopThreadLocked();

                if (stopEverything)
                {
                    // 本实例可能只是待命者，真正的宿主也要一起停下来。
                    coordinator?.SignalStop();
                }
            }

            HostedBy = null;
            LastError = null;
            RaiseStateChanged();
        }

        private void AdoptExistingHost(ProxyHostInfo host)
        {
            HostedBy = host;
            LastError = null;
            Logger.Info($"端口 {host.Port} 已有 AI Hub 代理在运行（{host.Describe()}），本实例转为待命，对方释放端口后立即接管。");
            RaiseStateChanged();
        }

        /// <summary>
        /// 端口被改掉后，在后台线程按新端口重新仲裁一次。
        /// 不能在宿主线程里直接做，否则会和自己抢 <see cref="_lifeGate"/>。
        /// </summary>
        private void QueueRelaunch()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (!_intentRunning || _shuttingDown)
                    {
                        return;
                    }

                    lock (_lifeGate)
                    {
                        StopThreadLocked();
                    }

                    Start();
                }
                catch (Exception ex)
                {
                    Logger.Error("按新端口重新启动代理失败：" + ex.Message);
                }
            });
        }

        #endregion

        #region 配置文件热重载

        private void EnsureConfigWatcher()
        {
            lock (_lifeGate)
            {
                if (_configWatcher != null || _shuttingDown)
                {
                    return;
                }

                try
                {
                    var path = Store.ConfigFile;
                    var directory = Path.GetDirectoryName(path);
                    if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                    {
                        return;
                    }

                    var watcher = new FileSystemWatcher(directory, Path.GetFileName(path))
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                        IncludeSubdirectories = false,
                    };

                    watcher.Changed += OnConfigFileChanged;
                    watcher.Created += OnConfigFileChanged;
                    watcher.Renamed += OnConfigFileChanged;
                    watcher.EnableRaisingEvents = true;
                    _configWatcher = watcher;
                }
                catch (Exception ex)
                {
                    Logger.Warn("无法监视配置文件变化：" + ex.Message);
                }
            }
        }

        private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
        {
            lock (_lifeGate)
            {
                if (_shuttingDown)
                {
                    return;
                }

                // 一次保存会触发多个事件，用一次性定时器合并成一次重载。
                if (_configDebounce == null)
                {
                    _configDebounce = new Timer(_ => ReloadConfigFromDisk(), null, Timeout.Infinite, Timeout.Infinite);
                }

                try
                {
                    _configDebounce.Change(400, Timeout.Infinite);
                }
                catch (Exception)
                {
                    // 忽略定时器已释放的情况。
                }
            }
        }

        private void ReloadConfigFromDisk()
        {
            if (Interlocked.Exchange(ref _reloading, 1) != 0)
            {
                return;
            }

            try
            {
                if (_shuttingDown)
                {
                    return;
                }

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

                if (SameConfig(config, _config))
                {
                    return;
                }

                var previousPort = _config.Port;
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

                if (previousPort != config.Port)
                {
                    Logger.Info($"代理端口已由 {previousPort} 改为 {config.Port}。");
                    QueueRelaunch();
                }
            }
            catch (Exception)
            {
                // 热重载失败不能影响 Visual Studio。
            }
            finally
            {
                Interlocked.Exchange(ref _reloading, 0);
            }
        }

        private void DisposeConfigWatcher()
        {
            lock (_lifeGate)
            {
                try
                {
                    _configWatcher?.Dispose();
                }
                catch (Exception)
                {
                    // 忽略。
                }

                _configWatcher = null;

                try
                {
                    _configDebounce?.Dispose();
                }
                catch (Exception)
                {
                    // 忽略。
                }

                _configDebounce = null;
            }
        }

        private static bool SameConfig(ProxyConfig left, ProxyConfig right)
        {
            try
            {
                return string.Equals(
                    JsonConvert.SerializeObject(left),
                    JsonConvert.SerializeObject(right),
                    StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
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

        #endregion

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
