using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Serilog;

#nullable enable
namespace Gekota.WPF.Services
{
    /// <summary>视频压制参数（完整对齐 ShanaEncoder 6.0 快速设置；分辨率自动匹配源文件）。</summary>
    public sealed class VideoCompressOptions
    {
        // ===== 容器 =====
        public string Container { get; set; } = "mp4";

        // ===== 视频编码 =====
        public string VideoCodec { get; set; } = "h264_nvenc";
        public string Profile { get; set; } = "baseline";        // none|baseline|main|high|high444p
        public string Level { get; set; } = "auto";              // auto|1.0 ... 6.2
        public string VideoPreset { get; set; } = "p4";          // default..p7
        public string VideoTune { get; set; } = "hq";            // hq|ll|ull|lossless

        // ===== 码率 =====
        public string RateMode { get; set; } = "bitrate";        // quality|quantizer|bitrate
        public int VideoBitrateKbps { get; set; } = 10000;
        public int Quality { get; set; } = 23;                   // 质量模式（NVENC: -cq / x264: -crf）
        public int Quantizer { get; set; } = 23;                 // 量化器模式（-qp）
        public string RateControl { get; set; } = "const";       // const|variable（-shanarcmode）

        // ===== 帧 =====
        public int Fps { get; set; } = 60;
        public string FpsMode { get; set; } = "cfr";             // cfr|vfr
        public int KeyframeInterval { get; set; } = 10;          // 关键帧（秒）

        // ===== 画面 =====
        public bool AutoResolution { get; set; } = true;         // true = 自动匹配源文件
        public int OutputWidth { get; set; } = 1920;
        public int OutputHeight { get; set; } = 1080;
        public string ScaleFilter { get; set; } = "bicubic";
        public bool HdrToSdr { get; set; } = true;

        // ===== 音频 =====
        public string AudioCodec { get; set; } = "libfdk_aac";
        public string AacProfile { get; set; } = "aac_low";      // aac_low|aac_he|aac_he_v2
        public int AudioBitrateKbps { get; set; } = 512;
        public int AudioChannels { get; set; } = 2;              // 0=原版
        public int SampleRate { get; set; } = 48000;

        // ===== 自动化 =====
        public bool AutoCompressEnabled { get; set; } = false;

        private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
        private static string Num(int v) => v.ToString(CultureInfo.InvariantCulture);
        private static string Esc(string s) => s.Replace('\\', '/').Replace(":", "\\:").Replace("'", "\\'");

        private List<string> BuildTokens(string input, string output, string? subtitlePath)
        {
            var vf = new List<string>();

            // 分辨率：自动匹配源文件（宽高规整为偶数，H.264 要求）
            if (AutoResolution)
                vf.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2:flags=" + ScaleFilter);
            else
                vf.Add("scale=" + Num(OutputWidth) + ":" + Num(OutputHeight) + ":flags=" + ScaleFilter);

            // 注：标准 FFmpeg 无 shanatonemap 滤镜；B 站源为 SDR，无需色调映射

            if (!string.IsNullOrWhiteSpace(subtitlePath))
                vf.Add("subtitles='" + Esc(subtitlePath!) + "'");

            vf.Add("setsar=1/1");

            var a = new List<string>
            {
                "-hide_banner", "-y",
                "-hwaccel", "auto",
                "-i", input,
                "-vf", string.Join(",", vf),
                "-af", "aresample=" + Num(SampleRate) + ":resampler=soxr",
                "-c:v", VideoCodec,
            };

            if (!string.IsNullOrWhiteSpace(Profile)) { a.Add("-profile:v"); a.Add(Profile); }
            if (!string.IsNullOrWhiteSpace(Level)) { a.Add("-level:v"); a.Add(Level); }
            if (!string.IsNullOrWhiteSpace(VideoPreset)) { a.Add("-preset:v"); a.Add(VideoPreset); }
            if (!string.IsNullOrWhiteSpace(VideoTune)) { a.Add("-tune:v"); a.Add(VideoTune); }

            // 码率模式：质量 / 量化器 / 比特率
            switch ((RateMode ?? "bitrate").ToLowerInvariant())
            {
                case "quality":
                    // 质量模式：NVENC 用 CQ（-rc vbr -cq），x264/x265 用 CRF
                    if (VideoCodec.IndexOf("nvenc", StringComparison.OrdinalIgnoreCase) >= 0)
                    { a.Add("-rc:v"); a.Add("vbr"); a.Add("-cq:v"); a.Add(Num(Quality)); }
                    else
                    { a.Add("-crf:v"); a.Add(Num(Quality)); }
                    break;
                case "quantizer":
                    // 量化器模式：恒定 QP
                    if (VideoCodec.IndexOf("nvenc", StringComparison.OrdinalIgnoreCase) >= 0)
                    { a.Add("-rc:v"); a.Add("constqp"); a.Add("-qp:v"); a.Add(Num(Quantizer)); }
                    else
                    { a.Add("-qp:v"); a.Add(Num(Quantizer)); }
                    break;
                default:
                    a.Add("-b:v"); a.Add(Num(VideoBitrateKbps) + "k");
                    // 比特率模式：CBR / VBR（NVENC 用 -rc）
                    if (VideoCodec.IndexOf("nvenc", StringComparison.OrdinalIgnoreCase) >= 0
                        && !string.IsNullOrWhiteSpace(RateControl))
                    {
                        a.Add("-rc:v");
                        a.Add(string.Equals(RateControl, "const", StringComparison.OrdinalIgnoreCase) ? "cbr" : "vbr");
                    }
                    break;
            }

            if (Fps > 0) { a.Add("-r:v"); a.Add(Num(Fps)); }
            if (!string.IsNullOrWhiteSpace(FpsMode)) { a.Add("-fps_mode"); a.Add(FpsMode); }

            // 关键帧间隔：N 秒 → 标准 -g（帧数 = 秒 × 帧率）
            if (KeyframeInterval > 0)
            {
                var fps = Fps > 0 ? Fps : 60;
                a.Add("-g:v"); a.Add(Num(KeyframeInterval * fps));
            }

            a.Add("-c:a"); a.Add(AudioCodec);
            if (!string.IsNullOrWhiteSpace(AacProfile)) { a.Add("-profile:a"); a.Add(AacProfile); }
            if (AudioChannels > 0) { a.Add("-ac"); a.Add(Num(AudioChannels)); }
            a.Add("-b:a"); a.Add(Num(AudioBitrateKbps) + "k");
            a.Add("-ar"); a.Add(Num(SampleRate));

            a.Add("-f"); a.Add(string.IsNullOrWhiteSpace(Container) ? "mp4" : Container);
            a.Add("-sn"); a.Add("-map_metadata"); a.Add("-1"); a.Add("-map_chapters"); a.Add("-1");
            a.Add("-progress"); a.Add("pipe:1");
            a.Add("-nostats");
            a.Add(output);
            return a;
        }

        public string BuildArguments(string input, string output, string? subtitlePath)
        {
            var tokens = BuildTokens(input, output, subtitlePath);
            var sb = new StringBuilder();
            foreach (var t in tokens)
            {
                if (sb.Length > 0) sb.Append(' ');
                if (t.IndexOfAny(new[] { ' ', '"', '\t' }) >= 0) sb.Append(Quote(t));
                else sb.Append(t);
            }
            return sb.ToString();
        }
    }

    /// <summary>压制设置持久化（页面与自动压制共用）。</summary>
    public static class VideoCompressSettings
    {
        private static readonly ILogger logger = Log.ForContext(typeof(VideoCompressSettings));
        private static readonly object sync = new object();
        private static VideoCompressOptions? current;

        private static string ConfigPath =>
            Path.Combine(AppContext.BaseDirectory, "VideoCompressSettings.json");

        public static VideoCompressOptions Current
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

        private static VideoCompressOptions Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var o = JsonConvert.DeserializeObject<VideoCompressOptions>(File.ReadAllText(ConfigPath));
                    if (o != null) return o;
                }
            }
            catch (Exception ex) { logger.Warning(ex, "[压制] 读取设置失败，使用默认值"); }
            return new VideoCompressOptions();
        }

        public static void Save()
        {
            try
            {
                lock (sync)
                {
                    File.WriteAllText(ConfigPath,
                        JsonConvert.SerializeObject(current ?? new VideoCompressOptions(), Formatting.Indented));
                }
            }
            catch (Exception ex) { logger.Warning(ex, "[压制] 保存设置失败"); }
        }
    }
}
