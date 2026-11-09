using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AIHub.Proxy;
using AIHub.ToolWindows;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace AIHub
{
    /// <summary>
    /// This is the class that implements the package exposed by this assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The minimum requirement for a class to be considered a valid package for Visual Studio
    /// is to implement the IVsPackage interface and register itself with the shell.
    /// This package uses the helper classes defined inside the Managed Package Framework (MPF)
    /// to do it: it derives from the Package class that provides the implementation of the
    /// IVsPackage interface and uses the registration attributes defined in the framework to
    /// register itself and its components with the shell. These attributes tell the pkgdef creation
    /// utility what data to put into .pkgdef file.
    /// </para>
    /// <para>
    /// To get loaded into VS, the package must be referred by &lt;Asset Type="Microsoft.VisualStudio.VsPackage" ...&gt; in .vsixmanifest file.
    /// </para>
    /// </remarks>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("AI Hub 代理", "Copilot Chat 用上任意云端或本地大模型", "1.1")]
    [ProvideAutoLoad("ADFC4E64-0397-11D1-9F4E-00A0C911004F", PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad("F1536EF8-92EC-443C-9ED7-FDADF150DA82", PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(ProxyToolWindow), Style = VsDockStyle.Tabbed, Window = "3ae79031-e1bc-11d0-8f78-00a0c9110057")]
    [Guid(AIHubPackage.PackageGuidString)]
    public sealed class AIHubPackage : AsyncPackage
    {
        /// <summary>
        /// AIHubPackage GUID string.
        /// </summary>
        public const string PackageGuidString = "04096e46-d5fb-4bdc-873c-08f579b92205";

        public static readonly Guid CommandSet = new Guid("7f6b2f0a-8a0e-4b63-9a3f-0c7d5e6a1b21");

        public const int OpenProxyPanelCommandId = 0x0100;

        #region Package Members

        /// <summary>
        /// Initialization of the package; this method is called right after the package is sited, so this is the place
        /// where you can put all the initialization code that rely on services provided by VisualStudio.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to monitor for initialization cancellation, which can occur when VS is shutting down.</param>
        /// <param name="progress">A provider for progress updates.</param>
        /// <returns>A task representing the async work of package initialization, or an already completed task if there is none. Do not return null from this method.</returns>
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            {
                var commandId = new CommandID(CommandSet, OpenProxyPanelCommandId);
                var command = new OleMenuCommand(
                    (_, __) => _ = JoinableTaskFactory.RunAsync(ShowToolWindowAsync),
                    commandId);
                commandService.AddCommand(command);
            }

            // 代理启动不依赖 UI 线程，放到后台线程避免拖慢 VS 启动。
            await TaskScheduler.Default;

            ProxyService.Instance.InitializeAutoStart();

            // 诊断用：设置 AIHUB_AUTO_OPEN_PANEL=1 时启动后自动打开面板。
            if (Environment.GetEnvironmentVariable("AIHUB_AUTO_OPEN_PANEL") == "1")
            {
                // 必须等包初始化结束后再创建工具窗口，否则会阻塞 Shell 启动。
#pragma warning disable VSTHRD110
                _ = JoinableTaskFactory.RunAsync(async () =>
                {
                    await Task.Delay(2000).ConfigureAwait(false);
                    await ShowToolWindowAsync();
                });
#pragma warning restore VSTHRD110
            }
        }

        private async Task ShowToolWindowAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                var window = FindToolWindow(typeof(ProxyToolWindow), 0, true);
                if (window?.Frame == null)
                {
                    throw new NotSupportedException("无法创建 AI Hub 代理窗口。");
                }

                var frame = (IVsWindowFrame)window.Frame;
                Marshal.ThrowExceptionForHR(frame.Show());
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(
                    "打开 AI Hub 代理面板失败：" + Environment.NewLine + Environment.NewLine + ex.Message,
                    "AI Hub 代理",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);
                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    ProxyService.Instance.Shutdown();
                }
                catch (Exception)
                {
                    // 退出时停止代理失败可忽略。
                }
            }

            base.Dispose(disposing);
        }

        #endregion
    }
}
