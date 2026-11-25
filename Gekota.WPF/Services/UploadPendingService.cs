using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

#nullable enable
namespace Gekota.WPF.Services
{
    /// <summary>扫描本地已压制但未成功投稿的录播。</summary>
    public static class UploadPendingService
    {
        public sealed class Entry
        {
            public string BasePath { get; set; } = "";
            public string TitleFolder { get; set; } = "";
            public string Title { get; set; } = "";
            public long RoomId { get; set; }
        }

        public static IReadOnlyList<Entry> Find(string workDirectory, TimeSpan maxAge,
            TimeSpan? minAge = null)
        {
            var result = new List<Entry>();
            if (string.IsNullOrWhiteSpace(workDirectory) || !Directory.Exists(workDirectory))
                return result;
            var cutoff = DateTime.Now - maxAge;
            var youngest = minAge ?? TimeSpan.FromMinutes(10);
            foreach (var streamerDir in Directory.EnumerateDirectories(workDirectory))
            {
                try
                {
                    if (Directory.GetLastWriteTime(streamerDir) < cutoff) continue;
                    var folderName = Path.GetFileName(streamerDir);
                    if (!long.TryParse(folderName.Split('-')[0], out var roomId)) continue;
                    foreach (var titleDir in Directory.EnumerateDirectories(streamerDir))
                    {
                        if (Directory.GetLastWriteTime(titleDir) < cutoff) continue;
                        var candidates = Directory.EnumerateFiles(titleDir)
                            .Where(p =>
                            {
                                var n = Path.GetFileName(p);
                                return n.IndexOf("【纯净版】", StringComparison.Ordinal) >= 0 ||
                                       n.IndexOf("【弹幕版】", StringComparison.Ordinal) >= 0;
                            })
                            .ToList();
                        if (candidates.Count == 0) continue;

                        var first = candidates[0];
                        var baseName = Path.GetFileName(first);
                        var suffixIndex = baseName.IndexOf("【纯净版】", StringComparison.Ordinal);
                        if (suffixIndex < 0) suffixIndex = baseName.IndexOf("【弹幕版】", StringComparison.Ordinal);
                        if (suffixIndex <= 0) continue;
                        baseName = baseName.Substring(0, suffixIndex);

                        var basePath = Path.Combine(titleDir, baseName + ".flv");
                        if (!File.Exists(basePath)) basePath = Path.Combine(titleDir, baseName + ".mp4");
                        if (!File.Exists(basePath)) continue;
                        if (DateTime.Now - File.GetLastWriteTime(first) < youngest) continue;
                        if (UploadHistoryStore.Contains(roomId, titleDir, baseName)) continue;
                        if (!BiliUploadOrchestrator.HasFangzhuang(basePath)) continue;
                        result.Add(new Entry
                        {
                            BasePath = basePath,
                            TitleFolder = titleDir,
                            Title = Path.GetFileName(titleDir),
                            RoomId = roomId,
                        });
                    }
                }
                catch { }
            }
            return result;
        }
    }
}
