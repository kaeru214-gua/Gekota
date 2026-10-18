using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Serilog;

#nullable enable
namespace Gekota.WPF.Services
{
    public static class VideoCoverExtractor
    {
        private static readonly ILogger logger = Log.ForContext(typeof(VideoCoverExtractor));

        public static async Task<string?> ExtractKeyframeAsync(string videoPath, string? outputDir = null)
        {
            if (!File.Exists(videoPath)) return null;
            var dir = outputDir ?? Path.GetDirectoryName(videoPath) ?? string.Empty;
            var coverPath = Path.Combine(dir, "cover_" + Guid.NewGuid().ToString("N") + ".jpg");

            var ffmpeg = VideoCompressService.FfmpegPath;
            if (!File.Exists(ffmpeg)) { logger.Error("[封面] 找不到 FFmpeg: {Path}", ffmpeg); return null; }

            // 默认从录播 10 分钟之后选关键帧；不足 10 分钟则取中点
            double seekSec = 600;
            try
            {
                var info = MediaProbe.Probe(videoPath);
                if (info != null && info.DurationSec > 0 && info.DurationSec < seekSec)
                    seekSec = Math.Max(0, info.DurationSec * 0.5);
            }
            catch (Exception ex) { logger.Debug(ex, "[封面] 探测时长失败，使用默认 600s"); }

            var args = "-hide_banner -y -ss " + seekSec.ToString("0.#", CultureInfo.InvariantCulture)
                + " -i \"" + videoPath
                + "\" -vf \"select=eq(pict_type\\,I)\" -frames:v 1 -q:v 2 \"" + coverPath + "\"";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg, Arguments = args, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardError = true, WorkingDirectory = VideoCompressService.FfmpegDir,
                };
                using var proc = Process.Start(psi);
                if (proc == null) return null;
                await Task.Run(() => proc.WaitForExit()).ConfigureAwait(false);
                return File.Exists(coverPath) ? coverPath : null;
            }
            catch (Exception ex) { logger.Warning(ex, "[封面] 抽帧失败"); return null; }
        }
    }
}
