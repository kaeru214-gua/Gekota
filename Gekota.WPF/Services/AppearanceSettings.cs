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
        /// <summary>默认主题色（贝拉Kira）。</summary>
        public const string DefaultThemeColorHex = "#DB7D74";

        /// <summary>默认背景图的原始位置（首次启动时复制到程序目录）。</summary>
        public const string DefaultBackgroundSourcePath =
            @"C:\Users\91054\Pictures\A-SOUL\[SHANA]【A-SOUL贝拉】2026年09月30日【3D】超级大负翁！-00.58.23.272.jpg";

        public string ThemeColorHex { get; set; } = DefaultThemeColorHex;
        public string BackgroundImagePath { get; set; } = string.Empty;
        public double MaskOpacity { get; set; } = 0.25;   // 0.25 - 1.0

        /// <summary>11 个预设主题色（参考配色图）。</summary>
        public static readonly (string Name, string Hex, string Rgb)[] Presets = new[]
        {
            ("A-SOUL团体", "#FC966E", "R:252 G:150 B:110"),
            ("贝拉Kira",   "#DB7D74", "R:219 G:125 B:116"),
            ("乃琳Queen",  "#576690", "R:87 G:104 B:144"),
            ("嘉然Diana",  "#E799B0", "R:231 G:153 B:176"),
            ("琳嘉女孩",   "#A892B8", "R:168 G:146 B:184"),
            ("超级嘉贝",   "#DE9B9E", "R:222 G:155 B:158"),
            ("乃贝",       "#A5828D", "R:165 G:130 B:141"),
            ("闪耀舞台",   "#83BDF9", "R:131 G:189 B:249"),
            ("思诺Gladys", "#7252C0", "R:114 G:82 B:192"),
            ("心宜Fiona",  "#C93773", "R:201 G:55 B:115"),
            ("枝江娱乐",   "#50FFB3", "R:80 G:255 B:179"),
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
                if (File.Exists(DefaultBackgroundPath)) return DefaultBackgroundPath;

                var src = AppearanceSettings.DefaultBackgroundSourcePath;
                if (!string.IsNullOrWhiteSpace(src) && File.Exists(src))
                {
                    File.Copy(src, DefaultBackgroundPath, overwrite: true);
                    logger.Information("[外观] 已复制默认背景图 -> {Path}", DefaultBackgroundPath);
                    return DefaultBackgroundPath;
                }

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

            // 背景图为空或仍指向"原始路径" -> 换成程序目录里的副本
            var copied = EnsureDefaultBackground();
            if (!string.IsNullOrWhiteSpace(copied) &&
                (string.IsNullOrWhiteSpace(o.BackgroundImagePath) ||
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

        /// <summary>把主题色应用到侧边栏（ModernWpf NavigationView 的 pane 背景）并通知刷新。</summary>
        public static void Apply()
        {
            try
            {
                var app = Application.Current;
                if (app != null)
                {
                    var brush = CreateBrush(Current.ThemeColorHex);
                    app.Resources["NavigationViewDefaultPaneBackground"] = brush;
                    app.Resources["NavigationViewExpandedPaneBackground"] = brush;
                }
            }
            catch (Exception ex) { logger.Warning(ex, "[外观] 应用主题色失败"); }

            try { Changed?.Invoke(null, EventArgs.Empty); }
            catch (Exception ex) { logger.Warning(ex, "[外观] 通知刷新失败"); }
        }

        public static void SetThemeColor(string hex) { Current.ThemeColorHex = hex; Save(); Apply(); }

        public static void SetBackground(string path)
        {
            Current.BackgroundImagePath = path;
            Save();
            Apply();
        }

        public static void SetMaskOpacity(double v)
        {
            Current.MaskOpacity = Math.Max(0.25, Math.Min(1.0, v));
            Save();
            Apply();
        }
    }
}
