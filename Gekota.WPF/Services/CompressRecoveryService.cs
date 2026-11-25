using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

#nullable enable
namespace Gekota.WPF.Services
{
    /// <summary>扫描录制目录，找出缺少纯净版/弹幕版产物的待压制任务。</summary>
    public static class CompressRecoveryService
    {
        public static IReadOnlyList<(string Input, string? Subtitle)> FindUnfinished(
            string workDirectory, TimeSpan maxAge)
        {
            var result = new List<(string, string?)>();
            if (string.IsNullOrWhiteSpace(workDirectory) || !Directory.Exists(workDirectory))
                return result;

            var cutoff = DateTime.Now - maxAge;
            foreach (var streamerDir in Directory.EnumerateDirectories(workDirectory))
            {
                try
                {
                    if (Directory.GetLastWriteTime(streamerDir) < cutoff) continue;
                    foreach (var titleDir in Directory.EnumerateDirectories(streamerDir))
                    {
                        if (Directory.GetLastWriteTime(titleDir) < cutoff) continue;
                        var flvs = Directory.EnumerateFiles(titleDir, "*.flv")
                            .Where(p => Path.GetFileName(p).IndexOf("-merged-", StringComparison.OrdinalIgnoreCase) < 0)
                            .ToList();
                        var merged = Directory.EnumerateFiles(titleDir, "*-merged-*.flv")
                            .OrderByDescending(File.GetLastWriteTime)
                            .FirstOrDefault();
                        if (merged == null && flvs.Count > 1) continue; // 等自动合并先处理

                        var basePath = merged ?? flvs.FirstOrDefault();
                        if (basePath == null) continue;
                        var mp4Path = Path.ChangeExtension(basePath, ".mp4");
                        if (File.Exists(mp4Path)) basePath = mp4Path;
                        // 仍在写入的录制文件不要抢先压制。
                        if (File.GetLastWriteTime(basePath) > DateTime.Now.AddMinutes(-1)) continue;

                        var dir = Path.GetDirectoryName(basePath) ?? titleDir;
                        var baseName = Path.GetFileNameWithoutExtension(basePath);
                        var container = VideoCompressSettings.Current.Container;
                        if (string.IsNullOrWhiteSpace(container)) container = "mp4";
                        var clean = Path.Combine(dir, baseName + "【纯净版】." + container);
                        var danmaku = Path.Combine(dir, baseName + "【弹幕版】." + container);
                        var ass = Path.ChangeExtension(basePath, ".ass");

                        if (!IsComplete(clean))
                            result.Add((basePath, null));
                        if (!IsComplete(danmaku) && File.Exists(ass))
                            result.Add((basePath, ass));
                    }
                }
                catch { }
            }
            return result;
        }

        private static bool IsComplete(string path)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length <= 0) return false;
                return (MediaProbe.Probe(path)?.DurationSec ?? 0) > 0;
            }
            catch { return false; }
        }
    }
}
