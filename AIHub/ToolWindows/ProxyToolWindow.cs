using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;

namespace AIHub.ToolWindows
{
    /// <summary>
    /// 承载代理控制面板的工具窗口。
    /// </summary>
    public sealed class ProxyToolWindow : ToolWindowPane
    {
        public ProxyToolWindow() : base(null)
        {
            Caption = "AI Hub 代理";

            try
            {
                Content = new ProxyPanel();
            }
            catch (Exception ex)
            {
                Content = CreateErrorContent(ex);
            }
        }

        // 面板构建失败时把异常显示出来，避免 VS 里只看到一个空白窗口。
        private static FrameworkElement CreateErrorContent(Exception exception)
        {
            var host = new Grid { Margin = new Thickness(16) };
            host.Children.Add(new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"),
                Text = "AI Hub 代理面板加载失败：" + Environment.NewLine + Environment.NewLine + exception,
            });

            return host;
        }
    }
}