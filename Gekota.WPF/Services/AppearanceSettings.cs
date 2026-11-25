using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Newtonsoft.Json;
using Serilog;

#nullable enable
namespace Gekota.WPF.Services
{
    /// <summary>自定义外观设置。</summary>
    public sealed class AppearanceSettings
    {
        /// <summary>默认主题色：白色（macOS Dock 默认白底黑色图标）。</summary>
        public const string DefaultThemeColorHex = "Transparent";

        /// <summary>
        /// 默认背景图的原始位置（相对于程序目录；首次启动时复制到根目录）。
        /// </summary>
        public const string DefaultBackgroundSourcePath = @"Assets\default_background.jpg";

        public string ThemeColorHex { get; set; } = DefaultThemeColorHex;
        public string BackgroundImagePath { get; set; } = string.Empty;
        public bool UseAcrylic { get; set; } = true;      // 窗口亚克力材质
        public bool DarkMode { get; set; }

        /// <summary>12 个预设主题色：白色（默认） + 11 个参考配色。</summary>
        public static readonly (string Name, string Hex, string Rgb)[] Presets = new[]
        {
            ("默认（透明）", "Transparent", "跟随深浅色"),
            ("A-SOUL团体", "#FC966E", "R:252 G:150 B:110"),
            ("贝拉Kira",   "#DB7D74", "R:219 G:125 B:116"),
            ("乃琳Queen",  "#576690", "R:87 G:104 B:144"),
            ("嘉然Diana",  "#E799B0", "R:231 G:153 B:176"),
            ("闪耀舞台",   "#83BDF9", "R:131 G:189 B:249"),
            ("思诺Gladys", "#7252C0", "R:114 G:82 B:192"),
            ("心宜Fiona",  "#C93773", "R:201 G:55 B:115"),
            ("闪耀舞台",   "#50FFB3", "R:80 G:255 B:179"),
        };
    }

    /// <summary>外观管理器：读取/保存、复制默认背景图、应用主题色、通知刷新。</summary>
    public static class AppearanceManager
    {
        private static readonly ILogger logger = Log.ForContext(typeof(AppearanceManager));
        private static readonly object sync = new object();

        public static AppearanceSettings Current { get; private set; } = Load();

        public static event EventHandler? Changed;

        private static string FilePath => Path.Combine(AppContext.BaseDirectory, "AppearanceSettings.json");

        /// <summary>复制到程序目录后的默认背景图路径。</summary>
        public static string DefaultBackgroundPath =>
            Path.Combine(AppContext.BaseDirectory, "default_background.jpg");

        /// <summary>确保默认背景图已复制到程序目录，返回可用路径（失败返回空）。</summary>
        public static string EnsureDefaultBackground()
        {
            try
            {
                var src = AppearanceSettings.DefaultBackgroundSourcePath;
                if (!string.IsNullOrWhiteSpace(src) && !Path.IsPathRooted(src))
                    src = Path.Combine(AppContext.BaseDirectory, src);

                if (!string.IsNullOrWhiteSpace(src) && File.Exists(src))
                {
                    var needsCopy = !File.Exists(DefaultBackgroundPath) ||
                                    File.GetLastWriteTimeUtc(src) > File.GetLastWriteTimeUtc(DefaultBackgroundPath);
                    if (needsCopy)
                    {
                        File.Copy(src, DefaultBackgroundPath, overwrite: true);
                        logger.Information("[外观] 已复制默认背景图 -> {Path}", DefaultBackgroundPath);
                    }
                    return DefaultBackgroundPath;
                }

                if (File.Exists(DefaultBackgroundPath)) return DefaultBackgroundPath;
                if (!string.IsNullOrWhiteSpace(src))
                    logger.Warning("[外观] 默认背景图源文件不存在: {Src}", src);
            }
            catch (Exception ex) { logger.Warning(ex, "[外观] 复制默认背景图失败"); }
            return string.Empty;
        }

        private static AppearanceSettings Load()
        {
            AppearanceSettings? o = null;
            try
            {
                if (File.Exists(FilePath))
                    o = JsonConvert.DeserializeObject<AppearanceSettings>(File.ReadAllText(FilePath));
            }
            catch (Exception ex) { logger.Warning(ex, "[外观] 读取设置失败"); }
            o ??= new AppearanceSettings();

            // 背景图为空、路径失效或仍指向"原始路径" -> 使用程序目录里的默认背景。
            var copied = EnsureDefaultBackground();
            if (!string.IsNullOrWhiteSpace(copied) &&
                (string.IsNullOrWhiteSpace(o.BackgroundImagePath) ||
                 !File.Exists(o.BackgroundImagePath) ||
                 string.Equals(o.BackgroundImagePath, AppearanceSettings.DefaultBackgroundSourcePath,
                     StringComparison.OrdinalIgnoreCase)))
            {
                o.BackgroundImagePath = copied;
                try { File.WriteAllText(FilePath, JsonConvert.SerializeObject(o, Formatting.Indented)); }
                catch (Exception ex) { logger.Warning(ex, "[外观] 迁移背景图路径失败"); }
            }

            return o;
        }

        public static void Save()
        {
            try
            {
                lock (sync)
                    File.WriteAllText(FilePath, JsonConvert.SerializeObject(Current, Formatting.Indented));
            }
            catch (Exception ex) { logger.Warning(ex, "[外观] 保存设置失败"); }
        }

        public static SolidColorBrush CreateBrush(string hex)
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                var b = new SolidColorBrush(c);
                b.Freeze();
                return b;
            }
            catch { return new SolidColorBrush(Colors.Orange); }
        }

        /// <summary>把主题色应用到 Dock / 强调色 / 窗口材质并通知刷新。</summary>
        public static void Apply()
        {
            try
            {
                var hex = Current.ThemeColorHex;
                var brush = CreateBrush(hex);
                var app = Application.Current;
                if (app != null)
                {
                    app.Resources["NavigationViewDefaultPaneBackground"] = brush;
                    app.Resources["NavigationViewExpandedPaneBackground"] = brush;
                }
            }
            catch (Exception ex) { logger.Warning(ex, "[外观] 应用主题色失败"); }

            MacTheme.Apply();

            try { Changed?.Invoke(null, EventArgs.Empty); }
            catch (Exception ex) { logger.Warning(ex, "[外观] 通知刷新失败"); }
        }

        public static void SetThemeColor(string hex) { Current.ThemeColorHex = hex; Save(); Apply(); }

        public static void SetDarkMode(bool dark)
        {
            Current.DarkMode = dark;
            Save();
            ModernWpf.ThemeManager.Current.ApplicationTheme = dark ? ModernWpf.ApplicationTheme.Dark : ModernWpf.ApplicationTheme.Light;
            Apply();
        }

        public static void SetUseAcrylic(bool enabled)
        {
            Current.UseAcrylic = enabled;
            Save();
            Apply();
        }

        public static void SetBackground(string path)
        {
            Current.BackgroundImagePath = path;
            Save();
            Apply();
        }
    }
}
