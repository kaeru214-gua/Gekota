using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

#nullable enable
namespace Gekota.WPF.Services
{
    /// <summary>媒体文件信息（通过 FFmpeg -i 解析）。</summary>
    public sealed class MediaInfo
    {
        public string Format { get; set; } = "";
        public double DurationSec { get; set; }
        public long SizeBytes { get; set; }
        public string VideoCodec { get; set; } = "";
        public string VideoProfile { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public double Fps { get; set; }
        public string AudioCodec { get; set; } = "";
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public string AudioBitrate { get; set; } = "";

        public string DurationText => MediaProbe.FormatDuration(DurationSec);
        public string SizeText => MediaProbe.FormatSize(SizeBytes);

        public string VideoText
        {
            get
            {
                if (string.IsNullOrEmpty(VideoCodec)) return "-";
                var s = VideoCodec;
                if (!string.IsNullOrEmpty(VideoProfile)) s += "(" + VideoProfile + ")";
                if (Width > 0) s += ", " + Width + "x" + Height;
                if (Fps > 0) s += ", " + Fps.ToString("0.##", CultureInfo.InvariantCulture) + "fps";
                return s;
            }
        }

        public string AudioText
        {
            get
            {
                if (string.IsNullOrEmpty(AudioCodec)) return "-";
                var s = AudioCodec;
                if (SampleRate > 0) s += ", " + SampleRate + "Hz";
                if (Channels > 0) s += ", " + Channels + "ch";
                if (!string.IsNullOrEmpty(AudioBitrate)) s += ", " + AudioBitrate;
                return s;
            }
        }
    }

    public static class MediaProbe
    {
        public static MediaInfo? Probe(string path)
        {
            try
            {
                var ff = VideoCompressService.FfmpegPath;
                if (!File.Exists(ff) || !File.Exists(path)) return null;

                var psi = new ProcessStartInfo
                {
                    FileName = ff,
                    Arguments = "-hide_banner -i \"" + path + "\"",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                };

                using var p = Process.Start(psi);
                if (p == null) return null;
                var err = p.StandardError.ReadToEnd();
                p.WaitForExit(8000);

                var info = new MediaInfo
                {
                    Format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
                    SizeBytes = new FileInfo(path).Length,
                };

                var md = Regex.Match(err, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
                if (md.Success)
                {
                    info.DurationSec = int.Parse(md.Groups[1].Value) * 3600
                        + int.Parse(md.Groups[2].Value) * 60
                        + double.Parse(md.Groups[3].Value, CultureInfo.InvariantCulture);
                }

                foreach (var line in err.Split('\n'))
                {
                    if (!line.Contains("Stream #")) continue;

                    if (line.Contains(": Video:"))
                    {
                        var mv = Regex.Match(line, @": Video:\s*([A-Za-z0-9_]+)(?:\s*\(([^)]*)\))?");
                        if (mv.Success)
                        {
                            info.VideoCodec = mv.Groups[1].Value;
                            info.VideoProfile = mv.Groups[2].Value;
                        }
                        var mr = Regex.Match(line, @"(\d{2,5})x(\d{2,5})");
                        if (mr.Success)
                        {
                            info.Width = int.Parse(mr.Groups[1].Value);
                            info.Height = int.Parse(mr.Groups[2].Value);
                        }
                        var mf = Regex.Match(line, @"([\d.]+)\s*fps");
                        if (mf.Success)
                            info.Fps = double.Parse(mf.Groups[1].Value, CultureInfo.InvariantCulture);
                    }
                    else if (line.Contains(": Audio:"))
                    {
                        var ma = Regex.Match(line, @": Audio:\s*([A-Za-z0-9_]+)");
                        if (ma.Success) info.AudioCodec = ma.Groups[1].Value;

                        var ms = Regex.Match(line, @"(\d+)\s*Hz");
                        if (ms.Success) info.SampleRate = int.Parse(ms.Groups[1].Value);

                        if (line.Contains("stereo")) info.Channels = 2;
                        else if (line.Contains("mono")) info.Channels = 1;
                        else
                        {
                            var mc = Regex.Match(line, @"(\d+)\s*channels");
                            if (mc.Success) info.Channels = int.Parse(mc.Groups[1].Value);
                        }

                        var mb = Regex.Match(line, @"(\d+)\s*kb/s");
                        if (mb.Success) info.AudioBitrate = mb.Groups[1].Value + "Kbps";
                    }
                }

                return info;
            }
            catch { return null; }
        }

        public static string FormatDuration(double seconds)
        {
            if (seconds <= 0) return "--:--";
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1
                ? string.Format("{0:0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                : string.Format("{0:00}:{1:00}", t.Minutes, t.Seconds);
        }

        public static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "-";
            var mb = bytes / 1048576.0;
            return mb >= 1
                ? mb.ToString("0.00", CultureInfo.InvariantCulture) + "MB"
                : (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + "KB";
        }
    }
}
