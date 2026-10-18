using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Serilog;

#nullable enable
namespace Gekota.WPF.Services
{
    /// <summary>
    /// DanmakuFactory 转换选项。默认值 = DF defaultConfig（main.c:53-90）。
    /// 唯一例外：Density 默认 -1（不重合）。
    /// </summary>
    public sealed class DanmakuConvertOptions
    {
        // ---- 画面 ----
        public int ResolutionX { get; set; } = 1920;      // -r
        public int ResolutionY { get; set; } = 1080;
        public double DisplayArea { get; set; } = 0.75;  // --displayarea（默认屏幕 75%）
        public double ScrollArea { get; set; } = 0.75;   // --scrollarea

        /// <summary>把顶部/底部固定弹幕改为滚动显示（特殊弹幕不受影响）。</summary>
        public bool ForceScroll { get; set; } = true;

        // ---- 时间 ----
        public double ScrollTime { get; set; } = 12.0;   // -s
        public double FixTime { get; set; } = 5.0;       // -f
        public double TimeShift { get; set; } = 0.0;     // -t

        // ---- 密度 / 间距 ----
        public int Density { get; set; } = -1;           // -d（唯一例外：默认不重合）
        public int LineSpacing { get; set; } = 0;        // --line-spacing
        public int TopMargin { get; set; } = 0;          // --top-margin
        public int BottomMargin { get; set; } = 0;       // --bottom-margin

        // ---- 字体 ----
        public int FontSize { get; set; } = 38;          // -S
        public bool FontSizeStrict { get; set; }         // --font-size-strict（无值开关）
        public bool FontSizeNorm { get; set; }           // --font-size-norm（无值开关）
        public string FontName { get; set; } = "Microsoft YaHei"; // -N
        public int Opacity { get; set; } = 180;          // -O
        public double Outline { get; set; } = 0;         // -L
        public double OutlineBlur { get; set; } = 0;     // --outline-blur
        public int OutlineOpacity { get; set; } = 255;   // --outline-opacity
        public double Shadow { get; set; } = 1;          // -D
        public bool Bold { get; set; }                   // -B（有值布尔）

        // ---- 显示 ----
        public bool SaveBlockedPart { get; set; } = true; // --saveblocked
        public bool ShowUserNames { get; set; }           // --showusernames
        public bool ShowMsgBox { get; set; } = true;      // --showmsgbox

        // ---- 消息框 ----
        public int MsgBoxFontSize { get; set; } = 38;    // --msgboxfontsize
        public int MsgBoxWidth { get; set; } = 500;      // --msgboxsize WxH
        public int MsgBoxHeight { get; set; } = 1080;
        public int MsgBoxX { get; set; } = 20;           // --msgboxpos XxY
        public int MsgBoxY { get; set; } = 0;
        public double MsgBoxDuration { get; set; } = 0;  // --msgboxduration
        public double GiftMinPrice { get; set; } = 0;    // --giftminprice

        // ---- 屏蔽模式 ----
        public bool BlockR2L { get; set; }
        public bool BlockL2R { get; set; }
        public bool BlockTop { get; set; }
        public bool BlockBottom { get; set; }
        public bool BlockSpecial { get; set; }
        public bool BlockColor { get; set; }
        public bool BlockRepeat { get; set; }

        // ---- 统计模式 ----
        public bool StatTable { get; set; }
        public bool StatHistogram { get; set; }

        // ---- 黑名单（可选）----
        public string BlacklistPath { get; set; } = string.Empty;
        public bool BlacklistRegex { get; set; }

        // ---- 自动化 ----
        public bool AutoConvertEnabled { get; set; } = false;
        /// <summary>配置版本：2 = 显示范围 75% + 固定弹幕转滚动。</summary>
        public int Version { get; set; }
        /// <summary>自动探测录播文件分辨率作为 -r（竖屏视频必须开启）。</summary>
        public bool AutoResolution { get; set; } = true;

        private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
        private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
        private static string B(bool b) => b ? "true" : "false";

        private static readonly Dictionary<string, (int W, int H)?> resolutionCache = new();

        private static string FfmpegPath =>
            Path.Combine(AppContext.BaseDirectory, "lib", "shana", "ShanaEncoder.sha");

        /// <summary>
        /// 解析本次转换使用的分辨率：依次探测同目录录播文件，全部失败才回退手动值。
        /// .flv 优先——录播刚结束时它是完整源文件，而 .mp4 可能还在转封装中（不完整）。
        /// </summary>
        private (int W, int H) ResolveResolution(string xmlPath)
        {
            if (this.AutoResolution)
            {
                foreach (var ext in new[] { ".flv", ".mp4", ".mkv", ".ts", ".mov" })
                {
                    var p = Path.ChangeExtension(xmlPath, ext);
                    if (!File.Exists(p)) continue;
                    if (ProbeResolution(p) is { } r) return r;
                }
            }
            return (this.ResolutionX, this.ResolutionY);
        }

        /// <summary>调用 FFmpeg -i 读取 Video 流的分辨率，结果缓存。</summary>
        private static (int W, int H)? ProbeResolution(string videoPath)
        {
            if (resolutionCache.TryGetValue(videoPath, out var cached)) return cached;

            (int W, int H)? result = null;
            try
            {
                var ff = FfmpegPath;
                if (!File.Exists(ff)) return null;

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ff,
                    Arguments = "-hide_banner -i \"" + videoPath + "\"",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return null;
                var err = proc.StandardError.ReadToEnd();
                proc.WaitForExit(5000);

                foreach (var line in err.Split('\n'))
                {
                    if (!line.Contains("Video:")) continue;
                    var m = System.Text.RegularExpressions.Regex.Match(line, @"(\d{2,5})x(\d{2,5})");
                    if (!m.Success) continue;
                    result = (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                              int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
                    break;
                }
            }
            catch { result = null; }

            resolutionCache[videoPath] = result;
            return result;
        }

        public string BuildCommand(string input, string output, string? probePath = null)
        {
            var (rx, ry) = this.ResolveResolution(probePath ?? input);
            var parts = new List<string>
            {
                "-i", Quote(input),
                "-o", Quote(output),
                "--force",
                "--ignore-warnings",

                "-r", rx + "x" + ry,
                "--displayarea", F(DisplayArea),
                "--scrollarea", F(ScrollArea),
                "-s", F(ScrollTime),
                "-f", F(FixTime),
                "-d", I(Density),
                "--line-spacing", I(LineSpacing),
                "--top-margin", I(TopMargin),
                "--bottom-margin", I(BottomMargin),
                "-S", I(FontSize),
                "-N", Quote(FontName),
                "-O", I(Opacity),
                "-L", F(Outline),
                "--outline-blur", F(OutlineBlur),
                "--outline-opacity", I(OutlineOpacity),
                "-D", F(Shadow),
                "--msgboxfontsize", I(MsgBoxFontSize > 0 ? MsgBoxFontSize : 38),
                "--msgboxsize", MsgBoxWidth + "x" + MsgBoxHeight,
                "--msgboxpos", MsgBoxX + "x" + MsgBoxY,
                "--msgboxduration", F(MsgBoxDuration),
                "--giftminprice", F(GiftMinPrice),
                "--saveblocked", B(SaveBlockedPart),
                "--showusernames", B(ShowUserNames),
                "--showmsgbox", B(ShowMsgBox),
            };

            if (FontSizeStrict) parts.Add("--font-size-strict");
            if (FontSizeNorm) parts.Add("--font-size-norm");
            if (Bold) { parts.Add("-B"); parts.Add("true"); }
            if (Math.Abs(TimeShift) > 0.0001) { parts.Add("-t"); parts.Add(F(TimeShift)); }

            var blocks = new List<string>();
            if (BlockR2L) blocks.Add("r2l");
            if (BlockL2R) blocks.Add("l2r");
            if (BlockTop) blocks.Add("top");
            if (BlockBottom) blocks.Add("bottom");
            if (BlockSpecial) blocks.Add("special");
            if (BlockColor) blocks.Add("color");
            if (BlockRepeat) blocks.Add("repeat");
            if (blocks.Count > 0) { parts.Add("-b"); parts.Add(string.Join("-", blocks)); }

            var stats = new List<string>();
            if (StatTable) stats.Add("table");
            if (StatHistogram) stats.Add("histogram");
            if (stats.Count > 0) { parts.Add("--statmode"); parts.Add(string.Join("-", stats)); }

            if (!string.IsNullOrWhiteSpace(BlacklistPath))
            {
                parts.Add("--blacklist"); parts.Add(Quote(BlacklistPath));
                if (BlacklistRegex) { parts.Add("--blacklist-regex"); parts.Add("true"); }
            }

            return string.Join(" ", parts);
        }
    }

    public static class DanmakuConvertSettings
    {
        private static readonly ILogger logger = Log.ForContext(typeof(DanmakuConvertSettings));
        private static readonly object sync = new object();
        private static DanmakuConvertOptions? current;

        private static string ConfigPath =>
            Path.Combine(AppContext.BaseDirectory, "DanmakuConvertSettings.json");

        public static DanmakuConvertOptions Current
        {
            get
            {
                lock (sync)
                {
                    if (current == null) current = Load();
                    return current;
                }
            }
        }

        private static DanmakuConvertOptions Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var o = JsonConvert.DeserializeObject<DanmakuConvertOptions>(File.ReadAllText(ConfigPath));
                    if (o != null)
                    {
                        if (o.Version < 2)
                        {
                            o.DisplayArea = 0.75;
                            o.ScrollArea = 0.75;
                            o.ForceScroll = true;
                            o.Version = 2;
                            try
                            {
                                File.WriteAllText(ConfigPath,
                                    JsonConvert.SerializeObject(o, Formatting.Indented));
                            }
                            catch { }
                        }
                        return o;
                    }
                }
            }
            catch (Exception ex) { logger.Warning(ex, "[DanmakuFactory] 读取转换设置失败"); }
            return new DanmakuConvertOptions();
        }

        public static void Save()
        {
            try
            {
                lock (sync)
                {
                    var o = current ?? new DanmakuConvertOptions();
                    o.Version = 2;
                    File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(o, Formatting.Indented));
                }
            }
            catch (Exception ex) { logger.Warning(ex, "[DanmakuFactory] 保存转换设置失败"); }
        }
    }
}
