using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Proxy;

namespace AIHub.ToolWindows
{
    /// <summary>
    /// 代理控制面板（WPF，跟随 Visual Studio 主题）。
    /// </summary>
    public partial class ProxyPanel : UserControl
    {
        private const int MaxLogLines = 4000;
        private const int TrimThreshold = 6000;

        private readonly ProxyService _service = ProxyService.Instance;

        private ProxyConfig _working = new ProxyConfig();
        private string _selectedProvider = string.Empty;
        private bool _suppressEvents;
        private bool _subscribed;

        private List<ModelChoice> _pickerChoices = new List<ModelChoice>();
        private string _pickerProvider = string.Empty;
        private CancellationTokenSource? _fetchCancellation;

        public ProxyPanel()
        {
            ThemePalette.EnsureInitialized();
            InitializeComponent();

            ApplyTheme();

            ProtocolBox.ItemsSource = new[] { "OpenAI 兼容", "Anthropic" };
            ProtocolBox.SelectedIndex = 0;
            NewProviderProtocolBox.ItemsSource = new[] { "OpenAI 兼容", "Anthropic" };
            NewProviderProtocolBox.SelectedIndex = 0;

            _working = _service.Config.Clone();
            LoadFromConfig();

            StartStopButton.Click += (_, __) => OnStartStopClicked();
            SaveButton.Click += (_, __) => OnSaveClicked();
            RestartButton.Click += (_, __) => OnRestartClicked();

            // 被其它实例托管时按钮会禁用，仍需能显示原因。
            ToolTipService.SetShowOnDisabled(StartStopButton, true);
            ToolTipService.SetShowOnDisabled(RestartButton, true);

            CopyAddressButton.Click += (_, __) => OnCopyAddressClicked();
            OpenConfigButton.Click += (_, __) => OpenPath(ProxyPaths.RootDirectory);
            ClearLogButton.Click += (_, __) => OnClearLogClicked();
            OpenTodayLogButton.Click += (_, __) => OnOpenTodayLogClicked();
            DeleteLogsButton.Click += (_, __) => OnDeleteLogsClicked();
            OpenLogDirButton.Click += (_, __) => OnOpenLogDirClicked();
            BrowseLogDirButton.Click += (_, __) => OnBrowseLogDirClicked();
            ResetLogDirButton.Click += (_, __) => LogDirBox.Text = string.Empty;

            ProviderList.SelectionChanged += (_, __) => OnProviderChanged();
            ShowKeyCheck.Checked += (_, __) => ToggleKeyVisibility(true);
            ShowKeyCheck.Unchecked += (_, __) => ToggleKeyVisibility(false);
            PortBox.TextChanged += (_, __) => UpdateEndpointText();

            FetchModelsButton.Click += (_, __) => OnFetchModelsClicked();
            AddProviderButton.Click += (_, __) => OnAddProviderClicked();
            RemoveProviderButton.Click += (_, __) => OnRemoveProviderClicked();
            NewProviderOkButton.Click += (_, __) => OnNewProviderOkClicked();
            NewProviderCancelButton.Click += (_, __) => CloseAddProvider();
            PickerAllButton.Click += (_, __) => SetPickerSelection(true);
            PickerNoneButton.Click += (_, __) => SetPickerSelection(false);
            PickerInvertButton.Click += (_, __) => InvertPickerSelection();
            PickerAddButton.Click += (_, __) => OnPickerAddClicked();
            PickerCancelButton.Click += (_, __) => ClosePicker();
            PickerFilterBox.TextChanged += (_, __) => RefreshPickerList();
            PickerHideNonChatCheck.Checked += (_, __) => RefreshPickerList();
            PickerHideNonChatCheck.Unchecked += (_, __) => RefreshPickerList();
            PreviewKeyDown += OnPanelPreviewKeyDown;

            Loaded += (_, __) => Attach();
            Unloaded += (_, __) => Detach();
        }

        private void Attach()
        {
            if (_subscribed)
            {
                return;
            }

            _subscribed = true;
            _service.StateChanged += OnServerStateChanged;
            _service.ConfigReloaded += OnConfigReloaded;
            _service.Logger.LineLogged += OnLogLine;
            ThemePalette.Changed += OnThemeChanged;

            AppendLog(string.Join(Environment.NewLine, _service.Logger.GetRecentLines()), replace: true);
            UpdateStatus();
        }

        private void Detach()
        {
            if (!_subscribed)
            {
                return;
            }

            _subscribed = false;
            _service.StateChanged -= OnServerStateChanged;
            _service.ConfigReloaded -= OnConfigReloaded;
            _service.Logger.LineLogged -= OnLogLine;
            ThemePalette.Changed -= OnThemeChanged;
        }

        private void OnThemeChanged()
        {
            RunOnUi(ApplyTheme);
        }

        private void RunOnUi(Action action)
        {
            if (Dispatcher.CheckAccess())
            {
                action();
                return;
            }

            // 事件来自代理后台线程，切回 UI 线程再更新控件。
#pragma warning disable VSTHRD001, VSTHRD110
            Dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
#pragma warning restore VSTHRD001, VSTHRD110
        }

        #region 主题

        private void ApplyTheme()
        {
            SetBrush("AhPanel", ThemePalette.Background);
            SetBrush("AhSurface", ThemePalette.Surface);
            SetBrush("AhSurfaceHover", ThemePalette.SurfaceHover);
            SetBrush("AhBorder", ThemePalette.Border);
            SetBrush("AhText", ThemePalette.Foreground);
            SetBrush("AhMuted", ThemePalette.Muted);
            SetBrush("AhInput", ThemePalette.InputBackground);
            SetBrush("AhAccent", ThemePalette.Accent);
            SetBrush("AhOnAccent", ThemePalette.OnAccent);
            SetBrush("AhSuccess", ThemePalette.Success);
            SetBrush("AhDanger", ThemePalette.Danger);
            SetBrush("AhSelection", ThemePalette.Blend(ThemePalette.Background, ThemePalette.Accent, 0.32));

            UpdateStatus();
        }

        private void SetBrush(string key, Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Resources[key] = brush;
        }

        #endregion

        #region 配置 <-> 界面

        private void LoadFromConfig()
        {
            _suppressEvents = true;
            try
            {
                PortBox.Text = _working.Port.ToString(CultureInfo.InvariantCulture);
                ContextBox.Text = _working.ContextLength.ToString(CultureInfo.InvariantCulture);
                AutoStartCheck.IsChecked = _working.AutoStart;
                LogEnabledCheck.IsChecked = _working.LogEnabled;
                LogDirBox.Text = _working.LogDirectory;
                ModelsBox.Text = string.Join(Environment.NewLine, _working.Models);

                var selected = _working.DefaultProvider;
                if (string.IsNullOrEmpty(selected) || !_working.Providers.ContainsKey(selected))
                {
                    selected = _working.Providers.Keys.FirstOrDefault() ?? "deepseek";
                }

                RefreshProviderList(selected);
            }
            finally
            {
                _suppressEvents = false;
            }

            UpdateEndpointText();
            UpdateStatus();
            AppendLog(string.Join(Environment.NewLine, _service.Logger.GetRecentLines()), replace: true);
            UpdateLogState();
        }

        private void RefreshProviderList(string? selectName)
        {
            ProviderList.Items.Clear();

            foreach (var name in _working.Providers.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                ProviderList.Items.Add(new ProviderEntry(name));
            }

            var target = ProviderList.Items
                .OfType<ProviderEntry>()
                .FirstOrDefault(e => string.Equals(e.Name, selectName, StringComparison.OrdinalIgnoreCase))
                ?? ProviderList.Items.OfType<ProviderEntry>().FirstOrDefault();

            ProviderList.SelectedItem = target;
            _selectedProvider = target?.Name ?? string.Empty;
            LoadProviderFields();
            UpdateRemoveProviderState();
        }

        private void UpdateRemoveProviderState()
        {
            RemoveProviderButton.IsEnabled = ProviderList.SelectedItem is ProviderEntry { IsCustom: true };
        }

        private void OnProviderChanged()
        {
            if (_suppressEvents || ProviderList.SelectedItem is not ProviderEntry entry)
            {
                return;
            }

            CommitProviderFields();
            _selectedProvider = entry.Name;
            LoadProviderFields();
            UpdateEndpointText();
            UpdateRemoveProviderState();
        }

        private void LoadProviderFields()
        {
            if (!_working.Providers.TryGetValue(_selectedProvider, out var setting) || setting == null)
            {
                BaseUrlBox.Text = string.Empty;
                SetApiKey(string.Empty);
                ProtocolBox.SelectedIndex = 0;
                RetriesBox.Text = "0";
                FallbackBox.Text = string.Empty;
                return;
            }

            BaseUrlBox.Text = setting.BaseUrl ?? string.Empty;
            SetApiKey(setting.ApiKey ?? string.Empty);
            ProtocolBox.SelectedIndex = setting.IsAnthropic ? 1 : 0;
            RetriesBox.Text = Math.Max(0, setting.Retries).ToString(CultureInfo.InvariantCulture);
            FallbackBox.Text = setting.Fallback ?? string.Empty;
        }

        private void CommitProviderFields()
        {
            if (string.IsNullOrEmpty(_selectedProvider))
            {
                return;
            }

            if (!_working.Providers.TryGetValue(_selectedProvider, out var setting) || setting == null)
            {
                setting = new ProviderSetting();
                _working.Providers[_selectedProvider] = setting;
            }

            setting.BaseUrl = BaseUrlBox.Text.Trim();
            setting.ApiKey = GetApiKey().Trim();
            setting.Protocol = ProtocolBox.SelectedIndex == 1
                ? ProviderSetting.ProtocolAnthropic
                : ProviderSetting.ProtocolOpenAi;

            setting.Retries = int.TryParse(RetriesBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var retries)
                ? Math.Max(0, Math.Min(5, retries))
                : 0;

            setting.Fallback = FallbackBox.Text.Trim();
            _working.DefaultProvider = _selectedProvider;
        }

        private string GetApiKey()
        {
            return ShowKeyCheck.IsChecked == true ? ApiKeyPlainBox.Text : ApiKeyBox.Password;
        }

        private void SetApiKey(string value)
        {
            ApiKeyBox.Password = value;
            ApiKeyPlainBox.Text = value;
        }

        private void ToggleKeyVisibility(bool show)
        {
            if (show)
            {
                ApiKeyPlainBox.Text = ApiKeyBox.Password;
                ApiKeyBox.Visibility = Visibility.Collapsed;
                ApiKeyPlainBox.Visibility = Visibility.Visible;
                ApiKeyPlainBox.Focus();
                ApiKeyPlainBox.CaretIndex = ApiKeyPlainBox.Text.Length;
            }
            else
            {
                ApiKeyBox.Password = ApiKeyPlainBox.Text;
                ApiKeyPlainBox.Visibility = Visibility.Collapsed;
                ApiKeyBox.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// 把界面上的值写回 <see cref="_working"/>，失败时返回 false 并给出中文原因。
        /// </summary>
        private bool TryApplyUiToWorking(out string error)
        {
            error = string.Empty;

            if (!int.TryParse(PortBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ||
                port < 1 || port > 65535)
            {
                error = "端口必须是 1 - 65535 之间的整数。";
                return false;
            }

            if (!int.TryParse(ContextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var contextLength) ||
                contextLength < 1024)
            {
                error = "上下文长度必须是不小于 1024 的整数。";
                return false;
            }

            var models = (ModelsBox.Text ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(m => m.Trim())
                .Where(m => m.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (models.Count == 0)
            {
                error = "至少需要一个模型，格式为 提供方/模型。";
                return false;
            }

            foreach (var model in models)
            {
                var slash = model.IndexOf('/');
                if (slash <= 0 || slash == model.Length - 1)
                {
                    error = $"模型“{model}”格式不正确，应为 提供方/模型。";
                    return false;
                }

                var prefix = model.Substring(0, slash);
                if (!_working.Providers.Keys.Any(k => string.Equals(k, prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    error = $"模型“{model}”使用了未知的提供方前缀“{prefix}”。";
                    return false;
                }
            }

            var baseUrl = BaseUrlBox.Text.Trim();
            if (baseUrl.Length == 0)
            {
                error = "Base URL 不能为空。";
                return false;
            }

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                error = "Base URL 必须是 http:// 或 https:// 开头的完整地址。";
                return false;
            }

            var logDirectory = (LogDirBox.Text ?? string.Empty).Trim();
            if (logDirectory.Length > 0 && !Path.IsPathRooted(logDirectory))
            {
                error = "日志目录必须是完整路径，例如 D:\\Logs\\AIHub；留空表示使用默认目录。";
                return false;
            }

            CommitProviderFields();

            _working.Port = port;
            _working.ContextLength = contextLength;
            _working.AutoStart = AutoStartCheck.IsChecked == true;
            _working.LogEnabled = LogEnabledCheck.IsChecked == true;
            _working.LogDirectory = logDirectory;
            _working.Models = models;
            _working.Normalize();

            return true;
        }

        #endregion

        #region 操作

        private void OnStartStopClicked()
        {
            if (_service.Server.IsRunning)
            {
                try
                {
                    _service.Stop();
                }
                catch (Exception ex)
                {
                    ShowError("停止代理失败：" + ex.Message);
                }

                return;
            }

            if (_service.IsHostedByOtherInstance)
            {
                // 另一个实例在托管：先确认它还活着，否则本实例接管。
                var host = ProxyServer.ProbeHost(_service.Config.Port);
                if (host != null)
                {
                    ShowInfo(
                        $"端口 {host.Port} 已由{host.Describe()}的 AI Hub 代理托管，无需重复启动。" +
                        Environment.NewLine + Environment.NewLine +
                        "关闭那个 Visual Studio 实例后，本实例会立刻接管端口，不用重新启动。");
                    return;
                }
            }

            if (!TryApplyUiToWorking(out var error))
            {
                ShowError(error);
                return;
            }

            try
            {
                _service.SaveAndApply(_working);

                if (_service.IsHostedByOtherInstance)
                {
                    UpdateStatus();
                    AppendLog($"端口 {_working.Port} 仍由其它实例托管，未重复启动。");
                    return;
                }

                _service.Start();

                if (_service.IsHostedByOtherInstance)
                {
                    UpdateStatus();
                    AppendLog($"端口 {_working.Port} 已有其它实例的代理在运行，已复用该实例。");
                    return;
                }

                UpdateLogState();
                AppendLog($"代理已启动：http://localhost:{_working.Port}");
            }
            catch (Exception ex)
            {
                ShowError("启动代理失败：" + ex.Message);
            }
        }

        private void OnSaveClicked()
        {
            if (!TryApplyUiToWorking(out var error))
            {
                ShowError(error);
                return;
            }

            try
            {
                _service.SaveAndApply(_working);
                UpdateStatus();
                UpdateLogState();
                AppendLog("配置已保存并应用。");
            }
            catch (Exception ex)
            {
                ShowError("保存配置失败：" + ex.Message);
            }
        }

        private void OnRestartClicked()
        {
            if (_service.IsHostedByOtherInstance)
            {
                var host = ProxyServer.ProbeHost(_service.Config.Port);
                if (host != null)
                {
                    ShowInfo(
                        $"端口 {host.Port} 已由{host.Describe()}的 AI Hub 代理托管，本实例无法重启它。" +
                        Environment.NewLine + Environment.NewLine +
                        "请在那个 Visual Studio 实例中点击“重启代理”。");
                    return;
                }
            }

            if (!TryApplyUiToWorking(out var error))
            {
                ShowError(error);
                return;
            }

            try
            {
                _service.SaveAndApply(_working);
                _service.Restart();

                if (_service.IsHostedByOtherInstance)
                {
                    UpdateStatus();
                    AppendLog($"端口 {_working.Port} 已有其它实例的代理在运行，已复用该实例。");
                    return;
                }

                UpdateLogState();
                AppendLog($"代理已重启：http://localhost:{_working.Port}");
            }
            catch (Exception ex)
            {
                ShowError("重启代理失败：" + ex.Message);
            }
        }

        private void OnCopyAddressClicked()
        {
            var address = EndpointText.Text;
            try
            {
                Clipboard.SetText(address);
                AppendLog("已复制地址：" + address);
            }
            catch (Exception ex)
            {
                ShowError("复制失败：" + ex.Message);
            }
        }

        private void OnClearLogClicked()
        {
            LogBox.Clear();
            _service.Logger.ClearMemory();
        }

        private void OnOpenTodayLogClicked()
        {
            var file = _service.Logger.CurrentLogFile;
            if (!File.Exists(file))
            {
                ShowInfo(_service.Logger.Enabled
                    ? "今天的日志文件还不存在。"
                    : "日志记录已关闭，不会生成日志文件。");
                return;
            }

            OpenPath(file);
        }

        private void OnDeleteLogsClicked()
        {
            var directory = _service.Logger.LogDirectory;

            var answer = MessageBox.Show(
                $"确定删除日志目录下的全部日志文件吗？\r\n\r\n{directory}",
                "AI Hub 代理",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            var removed = _service.Logger.DeleteAllLogs();
            LogBox.Clear();
            UpdateLogState();
            ShowInfo(removed == 0 ? "没有可删除的日志文件。" : $"已删除 {removed} 个日志文件。");
        }

        private void OnBrowseLogDirClicked()
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "选择日志保存位置（留空使用默认目录）";
                dialog.ShowNewFolderButton = true;

                var current = ProxyConfig.NormalizeLogDirectory(LogDirBox.Text);
                if (current.Length == 0)
                {
                    current = _service.Logger.LogDirectory;
                }

                if (Directory.Exists(current))
                {
                    dialog.SelectedPath = current;
                }

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    LogDirBox.Text = dialog.SelectedPath;
                }
            }
        }

        private void OnOpenLogDirClicked()
        {
            var directory = _service.Logger.LogDirectory;

            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                ShowError("日志目录不可用：" + ex.Message);
                return;
            }

            OpenPath(directory);
        }

        /// <summary>日志开关/目录变化后刷新界面提示。</summary>
        private void UpdateLogState()
        {
            var enabled = _service.Logger.Enabled;
            ClearLogButton.IsEnabled = enabled;
            OpenTodayLogButton.IsEnabled = enabled;

            if (!enabled)
            {
                LogBox.Text = "日志记录已关闭。取消“记录日志”后保存即可重新开启。";
            }
        }

        private void OpenPath(string path)
        {
            try
            {
                var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (Directory.Exists(path) || File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                    return;
                }

                ShowInfo("路径还不存在：" + path);
            }
            catch (Exception ex)
            {
                ShowError("打开路径失败：" + ex.Message);
            }
        }

        #endregion

        #region 自定义提供方

        private void OnAddProviderClicked()
        {
            NewProviderNameBox.Text = string.Empty;
            NewProviderUrlBox.Text = "https://";
            NewProviderKeyBox.Password = string.Empty;
            NewProviderProtocolBox.SelectedIndex = 0;
            HideNewProviderError();

            AddProviderOverlay.Visibility = Visibility.Visible;
            NewProviderNameBox.Focus();
            NewProviderNameBox.CaretIndex = NewProviderNameBox.Text.Length;
        }

        private void CloseAddProvider()
        {
            AddProviderOverlay.Visibility = Visibility.Collapsed;
            HideNewProviderError();
        }

        private void ShowNewProviderError(string message)
        {
            NewProviderErrorText.Text = message;
            NewProviderErrorText.Visibility = Visibility.Visible;
        }

        private void HideNewProviderError()
        {
            NewProviderErrorText.Text = string.Empty;
            NewProviderErrorText.Visibility = Visibility.Collapsed;
        }

        private void OnNewProviderOkClicked()
        {
            var name = (NewProviderNameBox.Text ?? string.Empty).Trim();
            var baseUrl = (NewProviderUrlBox.Text ?? string.Empty).Trim();
            var apiKey = NewProviderKeyBox.Password.Trim();

            if (name.Length == 0)
            {
                ShowNewProviderError("请填写名称，它会作为模型前缀使用。");
                return;
            }

            if (name.IndexOf('/') >= 0)
            {
                ShowNewProviderError("名称不能包含斜杠 /，因为模型名用斜杠分隔提供方与模型。");
                return;
            }

            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9._\-]{0,31}$"))
            {
                ShowNewProviderError("名称只能用字母、数字、点、下划线、短横线，且以字母或数字开头（最多 32 字符）。");
                return;
            }

            if (_working.Providers.ContainsKey(name))
            {
                ShowNewProviderError($"提供方“{name}”已存在，请换一个名称。");
                return;
            }

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                ShowNewProviderError("Base URL 必须是 http:// 或 https:// 开头的完整地址。");
                return;
            }

            // 先把当前提供方正在编辑的内容写回，避免切走时丢失。
            CommitProviderFields();

            _working.Providers[name] = new ProviderSetting
            {
                BaseUrl = baseUrl,
                ApiKey = apiKey,
                Protocol = NewProviderProtocolBox.SelectedIndex == 1
                    ? ProviderSetting.ProtocolAnthropic
                    : ProviderSetting.ProtocolOpenAi,
            };
            _working.DefaultProvider = name;

            CloseAddProvider();

            _suppressEvents = true;
            try
            {
                RefreshProviderList(name);
            }
            finally
            {
                _suppressEvents = false;
            }

            AppendLog($"已新增自定义提供方 {name}（{baseUrl}）。点“拉取可用模型”或“保存并应用”继续。");
        }

        private void OnRemoveProviderClicked()
        {
            if (ProviderList.SelectedItem is not ProviderEntry entry || !entry.IsCustom)
            {
                return;
            }

            var answer = MessageBox.Show(
                $"确定删除自定义提供方“{entry.Name}”吗？{Environment.NewLine}模型列表中属于它的条目也会被移除。",
                "AI Hub 代理",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            _working.Providers.Remove(entry.Name);

            var prefix = entry.Name + "/";
            var remaining = (_working.Models ?? new List<string>())
                .Where(m => !m.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var removedModels = (_working.Models?.Count ?? 0) - remaining.Count;
            _working.Models = remaining;
            ModelsBox.Text = string.Join(Environment.NewLine, remaining);

            var fallback = _working.Providers.Keys.FirstOrDefault() ?? "deepseek";
            if (string.Equals(_working.DefaultProvider, entry.Name, StringComparison.OrdinalIgnoreCase))
            {
                _working.DefaultProvider = fallback;
            }

            _suppressEvents = true;
            try
            {
                RefreshProviderList(_working.DefaultProvider);
            }
            finally
            {
                _suppressEvents = false;
            }

            AppendLog($"已删除自定义提供方 {entry.Name}，同时移除 {removedModels} 个模型条目。");
        }

        #endregion

        #region 拉取模型

        // 事件处理器；内部已捕获所有异常，不会让进程崩溃。
#pragma warning disable VSTHRD100
        private async void OnFetchModelsClicked()
#pragma warning restore VSTHRD100
        {
            CommitProviderFields();

            if (!_working.Providers.TryGetValue(_selectedProvider, out var setting) || setting == null)
            {
                ShowError("请先选择一个提供方。");
                return;
            }

            if (!Uri.TryCreate(setting.BaseUrl, UriKind.Absolute, out _))
            {
                ShowError("请先填写有效的 Base URL，例如 https://api.deepseek.com。");
                return;
            }

            if (string.IsNullOrWhiteSpace(setting.ApiKey))
            {
                ShowError("请先填写该提供方的 API Key，拉取模型需要它。");
                return;
            }

            var providerName = _selectedProvider;
            FetchModelsButton.IsEnabled = false;
            FetchModelsButton.Content = "拉取中...";

            _fetchCancellation?.Cancel();
            _fetchCancellation = new CancellationTokenSource();

            try
            {
                var models = await ModelCatalog
                    .FetchAsync(setting, _fetchCancellation.Token)
                    .ConfigureAwait(true);

                AppendLog($"已从 {providerName} 拉取到 {models.Count} 个模型。");
                ShowPicker(providerName, models);
            }
            catch (OperationCanceledException)
            {
                // 用户重新发起拉取，忽略本次结果。
            }
            catch (Exception ex)
            {
                AppendLog($"拉取模型失败（{providerName}）：{ex.Message}");
                ShowError("拉取模型失败：" + Environment.NewLine + Environment.NewLine + ex.Message);
            }
            finally
            {
                FetchModelsButton.IsEnabled = true;
                FetchModelsButton.Content = "拉取可用模型";
            }
        }

        private void ShowPicker(string providerName, IReadOnlyList<string> models)
        {
            _pickerProvider = providerName;

            var existing = new HashSet<string>(
                (_working.Models ?? new List<string>())
                    .Where(m => m.StartsWith(providerName + "/", StringComparison.OrdinalIgnoreCase))
                    .Select(m => m.Substring(providerName.Length + 1)),
                StringComparer.OrdinalIgnoreCase);

            _pickerChoices = models
                .Select(id => new ModelChoice(id, existing.Contains(id)))
                .ToList();

            foreach (var choice in _pickerChoices)
            {
                choice.PropertyChanged += (_, __) => UpdatePickerCount();
            }

            PickerProviderText.Text = $"{providerName} · 共 {_pickerChoices.Count} 个模型，勾选后加入模型列表";
            PickerFilterBox.Text = string.Empty;
            PickerHideNonChatCheck.IsChecked = true;

            RefreshPickerList();

            PickerOverlay.Visibility = Visibility.Visible;
            PickerFilterBox.Focus();
        }

        private void ClosePicker()
        {
            PickerOverlay.Visibility = Visibility.Collapsed;
            PickerList.ItemsSource = null;
            _pickerChoices = new List<ModelChoice>();
            _pickerProvider = string.Empty;
        }

        private void RefreshPickerList()
        {
            var keyword = (PickerFilterBox.Text ?? string.Empty).Trim();
            var hideNonChat = PickerHideNonChatCheck.IsChecked == true;

            var visible = _pickerChoices
                .Where(c => keyword.Length == 0 || c.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(c => !hideNonChat || !ModelChoice.LooksNonChat(c.Name))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            PickerList.ItemsSource = visible;
            PickerEmptyText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdatePickerCount();
        }

        private void UpdatePickerCount()
        {
            var selected = _pickerChoices.Count(c => c.IsSelected);
            PickerCountText.Text = $"已选 {selected} 个";
            PickerAddButton.IsEnabled = selected > 0;
        }

        private List<ModelChoice> VisiblePickerChoices()
        {
            return (PickerList.ItemsSource as IEnumerable<ModelChoice>)?.ToList() ?? new List<ModelChoice>();
        }

        private void SetPickerSelection(bool selected)
        {
            // 只作用于当前筛选后可见的项，避免"已选 N 个"把看不见的项也算进去。
            foreach (var choice in VisiblePickerChoices())
            {
                choice.IsSelected = selected;
            }

            UpdatePickerCount();
        }

        private void InvertPickerSelection()
        {
            foreach (var choice in VisiblePickerChoices())
            {
                choice.IsSelected = !choice.IsSelected;
            }

            UpdatePickerCount();
        }

        private void OnPickerChoiceToggled(object sender, RoutedEventArgs e)
        {
            UpdatePickerCount();
        }

        private void OnPickerAddClicked()
        {
            // 用拉取时的提供方，避免等待期间切换提供方导致前缀错位。
            var providerName = _pickerProvider;
            var chosen = _pickerChoices
                .Where(c => c.IsSelected)
                .Select(c => providerName + "/" + c.Name)
                .ToList();

            if (chosen.Count == 0)
            {
                ShowInfo("请至少勾选一个模型。");
                return;
            }

            var merged = (_working.Models ?? new List<string>()).ToList();
            foreach (var model in chosen)
            {
                if (!merged.Any(m => string.Equals(m, model, StringComparison.OrdinalIgnoreCase)))
                {
                    merged.Add(model);
                }
            }

            _working.Models = merged;
            ModelsBox.Text = string.Join(Environment.NewLine, merged);

            ClosePicker();
            AppendLog($"已添加 {chosen.Count} 个模型（{providerName}），记得点“保存并应用”。");
        }

        private void OnPanelPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape)
            {
                return;
            }

            if (AddProviderOverlay.Visibility == Visibility.Visible)
            {
                CloseAddProvider();
                e.Handled = true;
                return;
            }

            if (PickerOverlay.Visibility == Visibility.Visible)
            {
                ClosePicker();
                e.Handled = true;
            }
        }

        #endregion

        #region 事件

        private void OnServerStateChanged()
        {
            RunOnUi(UpdateStatus);
        }

        private void OnConfigReloaded()
        {
            RunOnUi(() =>
            {
                _working = _service.Config.Clone();
                LoadFromConfig();
                AppendLog("其它实例修改了配置，已重新载入。");
            });
        }

        private void OnLogLine(string line)
        {
            RunOnUi(() => AppendLog(line));
        }

        private void AppendLog(string text, bool replace = false)
        {
            if (string.IsNullOrEmpty(text))
            {
                if (replace)
                {
                    LogBox.Clear();
                }

                return;
            }

            if (replace)
            {
                LogBox.Text = text;
            }
            else
            {
                if (LogBox.Text.Length > 0)
                {
                    LogBox.AppendText(Environment.NewLine);
                }

                LogBox.AppendText(text);
            }

            var lineCount = LogBox.Text.Count(c => c == '\n') + 1;
            if (lineCount > TrimThreshold)
            {
                var lines = LogBox.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                if (lines.Length > MaxLogLines)
                {
                    LogBox.Text = string.Join(Environment.NewLine, lines.Skip(lines.Length - MaxLogLines));
                }
            }

            LogBox.ScrollToEnd();
        }

        private void UpdateEndpointText()
        {
            var port = int.TryParse(PortBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : _working.Port;

            EndpointText.Text = $"http://localhost:{port}";
        }

        private void UpdateStatus()
        {
            var running = _service.Server.IsRunning;
            var host = running ? null : _service.HostedBy;
            var error = _service.LastError;

            var accent = running
                ? ThemePalette.Success
                : host != null
                    ? ThemePalette.Accent
                    : ThemePalette.Danger;

            StatusDot.Fill = Brush(accent);
            StatusPill.Background = Brush(ThemePalette.Blend(ThemePalette.Background, accent, 0.18));

            if (running)
            {
                StatusText.Text = "运行中";
                StatusText.ToolTip = null;
                StartStopButton.Content = "停止代理";
                StartStopButton.Style = (Style)Resources["BaseButton"];
                StartStopButton.IsEnabled = true;
                StartStopButton.ToolTip = null;
                RestartButton.IsEnabled = true;
                RestartButton.ToolTip = null;
            }
            else if (host != null)
            {
                var tip = $"端口 {host.Port} 由{host.Describe()}的 AI Hub 代理托管，无需在本实例重复启动。"
                    + Environment.NewLine + "那个实例退出后，本实例会立刻接管端口，不用重新启动。";

                StatusText.Text = "其它实例托管";
                StatusText.ToolTip = tip;
                StartStopButton.Content = "启动代理";
                StartStopButton.Style = (Style)Resources["PrimaryButton"];
                StartStopButton.IsEnabled = false;
                StartStopButton.ToolTip = tip;
                RestartButton.IsEnabled = false;
                RestartButton.ToolTip = tip;
            }
            else if (!string.IsNullOrEmpty(error))
            {
                StatusText.Text = "启动失败";
                StatusText.ToolTip = error;
                StartStopButton.Content = "启动代理";
                StartStopButton.Style = (Style)Resources["PrimaryButton"];
                StartStopButton.IsEnabled = true;
                StartStopButton.ToolTip = null;
                RestartButton.IsEnabled = true;
                RestartButton.ToolTip = null;
            }
            else
            {
                StatusText.Text = "已停止";
                StatusText.ToolTip = null;
                StartStopButton.Content = "启动代理";
                StartStopButton.Style = (Style)Resources["PrimaryButton"];
                StartStopButton.IsEnabled = true;
                StartStopButton.ToolTip = null;
                RestartButton.IsEnabled = true;
                RestartButton.ToolTip = null;
            }

            PortBox.IsEnabled = !running && host == null;
            UpdateEndpointText();
        }

        private static Brush Brush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static void ShowError(string message)
        {
            MessageBox.Show(message, "AI Hub 代理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private static void ShowInfo(string message)
        {
            MessageBox.Show(message, "AI Hub 代理", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        #endregion
    }
}
