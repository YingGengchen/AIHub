using System;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace AIHub.ToolWindows
{
    /// <summary>
    /// 把 Visual Studio 当前主题色解析成面板用的一组配色，主题切换时自动刷新。
    /// </summary>
    internal static class ThemePalette
    {
        private static bool _hooked;

        public static event Action? Changed;

        public static bool IsDark { get; private set; }

        public static Color Background { get; private set; }

        public static Color Foreground { get; private set; }

        public static Color Surface { get; private set; }

        public static Color SurfaceHover { get; private set; }

        public static Color Border { get; private set; }

        public static Color Muted { get; private set; }

        public static Color InputBackground { get; private set; }

        public static Color Accent { get; private set; }

        public static Color OnAccent { get; private set; }

        public static Color Success { get; private set; }

        public static Color Danger { get; private set; }

        public static void EnsureInitialized()
        {
            Refresh();

            if (_hooked)
            {
                return;
            }

            _hooked = true;
            try
            {
                VSColorTheme.ThemeChanged += _ => RefreshAndNotify();
            }
            catch (Exception)
            {
                // 老版本 Shell 没有主题事件时忽略，配色仍然可用。
            }
        }

        private static void RefreshAndNotify()
        {
            Refresh();
            Changed?.Invoke();
        }

        private static void Refresh()
        {
            var background = Get(EnvironmentColors.ToolWindowBackgroundColorKey, FromArgb(0x25, 0x25, 0x26));
            IsDark = Luminance(background) < 0.5;

            Background = background;
            Foreground = Get(EnvironmentColors.ToolWindowTextColorKey, IsDark ? FromArgb(0xF1, 0xF1, 0xF1) : FromArgb(0x1E, 0x1E, 0x1E));

            // 卡片比背景略亮/略暗一点，形成层次但不会太跳。
            Surface = Blend(Background, Foreground, IsDark ? 0.055 : 0.035);
            SurfaceHover = Blend(Background, Foreground, IsDark ? 0.11 : 0.07);
            Border = Blend(Background, Foreground, IsDark ? 0.16 : 0.14);
            Muted = Blend(Background, Foreground, 0.55);

            InputBackground = Get(EnvironmentColors.ComboBoxBackgroundColorKey, Blend(Background, Foreground, IsDark ? 0.09 : 0.02));

            Accent = Get(EnvironmentColors.AccentMediumColorKey, FromArgb(0x0E, 0x63, 0x9C));
            if (Luminance(Accent) < 0.12)
            {
                Accent = Blend(Accent, Colors.White, 0.25);
            }

            OnAccent = Luminance(Accent) > 0.6 ? FromArgb(0x1E, 0x1E, 0x1E) : Colors.White;
            Success = IsDark ? FromArgb(0x4E, 0xC9, 0x7A) : FromArgb(0x1A, 0x7F, 0x37);
            Danger = IsDark ? FromArgb(0xF8, 0x51, 0x49) : FromArgb(0xCF, 0x22, 0x2E);
        }

        private static Color Get(ThemeResourceKey key, Color fallback)
        {
            try
            {
                var color = VSColorTheme.GetThemedColor(key);
                return color.A == 0 ? fallback : FromArgb(color.R, color.G, color.B);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static Color FromArgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

        public static Color Blend(Color from, Color to, double amount)
        {
            var t = Math.Max(0, Math.Min(1, amount));
            return Color.FromRgb(
                (byte)Math.Round(from.R + ((to.R - from.R) * t)),
                (byte)Math.Round(from.G + ((to.G - from.G) * t)),
                (byte)Math.Round(from.B + ((to.B - from.B) * t)));
        }

        private static double Luminance(Color color)
        {
            return ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255.0;
        }
    }
}
