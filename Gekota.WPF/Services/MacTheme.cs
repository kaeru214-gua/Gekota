using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Gekota.WPF.Services;
using ModernWpf;
using Serilog;

#nullable enable
namespace Gekota.WPF.Services
{
    /// <summary>
    /// macOS 风格主题控制器：跟随浅色/深色与皮肤色，统一更新全局画刷、ModernWpf 强调色、
    /// 窗口亚克力材质和 Dock 图标颜色。
    /// </summary>
    public static class MacTheme
    {
        private static readonly ILogger logger = Log.ForContext(typeof(MacTheme));

        public static bool IsDark { get; private set; }
        public static Color Accent { get; private set; } = Color.FromRgb(0x0A, 0x84, 0xFF);
        public static Color SkinColor { get; private set; } = Colors.White;
        public static bool IsDefaultWhiteSkin => SkinColor.A == 0 || IsNearWhite(SkinColor);
        public static event EventHandler? Changed;

        public static void Apply()
        {
            try
            {
                var app = Application.Current;
                if (app == null) return;

                IsDark = ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Dark;

                var skin = ParseColor(AppearanceManager.Current.ThemeColorHex, Colors.Transparent);
                SkinColor = skin;
                var isDefaultSkin = skin.A == 0 || IsNearWhite(skin);
                Accent = isDefaultSkin ? Color.FromRgb(0x0A, 0x84, 0xFF) : skin;

                // 让 ModernWpf 的 ToggleSwitch / Flyout 等控件跟随皮肤色。
                try { ThemeManager.Current.AccentColor = Accent; }
                catch (Exception ex) { logger.Debug(ex, "[主题] 设置 ModernWpf 强调色失败"); }

                var text = IsDark ? Color.FromRgb(0xF5, 0xF5, 0xF7) : Color.FromRgb(0x1D, 0x1D, 0x1F);
                var textSecondary = IsDark ? Color.FromRgb(0xA1, 0xA1, 0xA6) : Color.FromRgb(0x6E, 0x6E, 0x73);
                var textTertiary = IsDark ? Color.FromRgb(0x8E, 0x8E, 0x93) : Color.FromRgb(0x8E, 0x8E, 0x93);

                // 全局字体跟随 Windows「消息字体」设置。
                var systemFont = SystemFonts.MessageFontFamily;
                app.Resources["MacFontFamily"] = systemFont;
                app.Resources["MacDisplayFontFamily"] = systemFont;
                app.Resources["MacMonoFontFamily"] = systemFont;
                app.Resources["ContentControlThemeFontFamily"] = systemFont;

                Set("MacTextPrimaryBrush", text);
                Set("MacTextSecondaryBrush", textSecondary);
                Set("MacTextTertiaryBrush", textTertiary);                Set("MacAccentBrush", Accent);
                Set("MacAccentSoftBrush", Color.FromArgb(0x26, Accent.R, Accent.G, Accent.B));
                Set("MacOnAccentBrush", ContrastForeground(Accent));
                Set("MacDangerBrush", Color.FromRgb(0xFF, 0x45, 0x3A));
                Set("MacSuccessBrush", Color.FromRgb(0x30, 0xD1, 0x58));
                Set("MacWarningBrush", Color.FromRgb(0xFF, 0x9F, 0x0A));

                var acrylic = AppearanceManager.Current.UseAcrylic;
                Set("MacContentBrush", IsDark ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0xF2, 0xF3, 0xF7));
                Set("MacCardBrush", IsDark
                    ? Color.FromArgb((byte)(acrylic ? 0xC7 : 0xEE), 0x2C, 0x2C, 0x2E)
                    : Color.FromArgb((byte)(acrylic ? 0xC7 : 0xEE), 0xFF, 0xFF, 0xFF));
                Set("MacFieldBrush", IsDark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
                Set("MacControlBrush", IsDark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
                Set("MacControlHoverBrush", IsDark ? Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
                Set("MacBorderBrush", IsDark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0x00, 0x00, 0x00));
                Set("MacStrongBorderBrush", IsDark ? Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x3D, 0x00, 0x00, 0x00));
                Set("MacSeparatorBrush", IsDark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
                Set("MacHoverBrush", IsDark ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0F, 0x00, 0x00, 0x00));
                Set("MacPressedBrush", IsDark ? Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0x00, 0x00, 0x00));
                Set("MacMenuBrush", IsDark ? Color.FromArgb(0xF7, 0x1E, 0x1E, 0x20) : Color.FromArgb(0xF7, 0xFF, 0xFF, 0xFF));
                Set("MacMenuHoverBrush", IsDark ? Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x12, 0x00, 0x00, 0x00));
                Set("MacTrackBrush", IsDark ? Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x2E, 0x00, 0x00, 0x00));
                Set("MacDisabledBrush", IsDark ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));

                // Dock：默认透明皮肤跟随深浅色；其他主题色也使用半透明底，保留 Dock 的玻璃感。
                var dockIconBackground = isDefaultSkin
                    ? (IsDark ? Color.FromArgb(0x8C, 0x62, 0x62, 0x68) : Color.FromArgb(0xBA, 0x5E, 0x5E, 0x66))
                    : Color.FromArgb(0xA6, skin.R, skin.G, skin.B);
                var dockIconForeground = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
                Set("MacDockIconBackgroundBrush", dockIconBackground);
                Set("MacDockIconForegroundBrush", dockIconForeground);
                Set("MacDockIconHoverBrush", IsDark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
                Set("MacDockIndicatorBrush", IsDark ? Color.FromRgb(0xF5, 0xF5, 0xF7) : Color.FromRgb(0x1D, 0x1D, 0x1F));

                Set("MacDockBrush", IsDark
                    ? Color.FromArgb((byte)(acrylic ? 0x80 : 0xCC), 0x2C, 0x2C, 0x2E)
                    : Color.FromArgb((byte)(acrylic ? 0x80 : 0xD9), 0xE2, 0xE2, 0xE7));

                SetGradient("MacCardGlassBrush", IsDark
                    ? new[] { (Color.FromArgb(0xD1, 0x2C, 0x2C, 0x2E), 0d), (Color.FromArgb(0xA8, 0x2C, 0x2C, 0x2E), 1d) }
                    : new[] { (Color.FromArgb(0xD1, 0xFF, 0xFF, 0xFF), 0d), (Color.FromArgb(0xA8, 0xFF, 0xFF, 0xFF), 1d) });
                SetGradient("MacAddCardBrush", IsDark
                    ? new[] { (Color.FromArgb(0xF0, 0x30, 0x30, 0x33), 0d), (Color.FromArgb(0xEA, 0x28, 0x28, 0x2B), 1d) }
                    : new[] { (Color.FromArgb(0xFF, 0xF8, 0xF9, 0xFB), 0d), (Color.FromArgb(0xFF, 0xF2, 0xF4, 0xF7), 1d) });
                SetGradient("MacDockSheenBrush", new[]
                {
                    (Color.FromArgb(IsDark ? (byte)0x1F : (byte)0x59, 0xFF, 0xFF, 0xFF), 0d),
                    (Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.55d),
                });

                Set("MacWindowTintBrush", IsDark
                    ? Color.FromArgb(0xB3, 0x1C, 0x1C, 0x1E)
                    : Color.FromArgb(0xB3, 0xF3, 0xF3, 0xF5));
                Set("MacWindowBaseBrush", IsDark
                    ? Color.FromRgb(0x1C, 0x1C, 0x1E)
                    : Color.FromRgb(0xF2, 0xF3, 0xF7));
                Set("MacChromeBrush", IsDark
                    ? Color.FromArgb(0xCC, 0x1C, 0x1C, 0x1E)
                    : Color.FromArgb(0xCC, 0xF7, 0xF7, 0xF9));

                ApplyWindowMaterial();
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "[主题] 应用 macOS 主题失败");
            }

            try { Changed?.Invoke(null, EventArgs.Empty); }
            catch (Exception ex) { logger.Warning(ex, "[主题] 通知刷新失败"); }
        }

        /// <summary>窗口句柄就绪后（或窗口材质开关变化时）重新应用亚克力。</summary>
        public static void ApplyWindowMaterial()
        {
            var window = Application.Current?.MainWindow;
            if (window == null) return;

            if (Environment.GetEnvironmentVariable("GEKOTA_SAFE_MODE") == "1")
            {
                WindowEffects.Disable(window);
                window.Background = new SolidColorBrush(IsDark
                    ? Color.FromRgb(0x1C, 0x1C, 0x1E)
                    : Color.FromRgb(0xF3, 0xF3, 0xF5));
                return;
            }

            // 有自定义背景图时，窗口本身由背景图铺满，不再使用系统 accent。
            // accent 会把透明圆角处涂成黑块，同时也会和 WPF 内容争抢合成。
            var backgroundPath = AppearanceManager.Current.BackgroundImagePath;
            if (!string.IsNullOrWhiteSpace(backgroundPath) && File.Exists(backgroundPath))
            {
                WindowEffects.Disable(window);
                window.Background = System.Windows.Media.Brushes.Transparent;
                return;
            }

            var tint = IsDark
                ? Color.FromArgb(0x01, 0x1C, 0x1C, 0x1E)
                : Color.FromArgb(0x01, 0xF3, 0xF3, 0xF5);
            WindowEffects.Apply(window, AppearanceManager.Current.UseAcrylic, tint, IsDark);
        }

        public static void ToggleLightDark()
        {
            AppearanceManager.SetDarkMode(!IsDark);
        }

        public static Color ParseColor(string? hex, Color fallback)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(hex))
                    return (Color)ColorConverter.ConvertFromString(hex);
            }
            catch { }
            return fallback;
        }

        public static bool IsNearWhite(Color c) => c.R > 245 && c.G > 245 && c.B > 245;

        public static Color ContrastForeground(Color background)
        {
            var lum = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255d;
            return lum > 0.66 ? Color.FromRgb(0x1D, 0x1D, 0x1F) : Colors.White;
        }

        private static void Set(string key, Color color)
        {
            Application.Current.Resources[key] = new SolidColorBrush(color);
        }

        private static void SetGradient(string key, (Color Color, double Offset)[] stops)
        {
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            foreach (var (color, offset) in stops)
                brush.GradientStops.Add(new GradientStop(color, offset));
            brush.Freeze();
            Application.Current.Resources[key] = brush;
        }
    }
}
