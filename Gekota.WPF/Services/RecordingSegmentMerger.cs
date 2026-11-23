using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Serilog;

#nullable enable
namespace Gekota.WPF.Services
{
    /// <summary>
    /// 同一场直播因 CDN 断流产生的多个 FLV，在最后一次写入结束后自动拼接。
    /// </summary>
    public static class RecordingSegmentMerger
    {
        private sealed class Segment
        {
            public string Path { get; set; } = "";
            public DateTime Open { get; set; }
            public DateTime Close { get; set; }
            public double Duration { get; set; }
            public string DanmakuPath => System.IO.Path.ChangeExtension(this.Path, ".xml");
        }

        private sealed class Batch
        {
            public string OutputDirectory { get; set; } = "";
            public Func<bool>? IsLive { get; set; }
            public Func<string, Task>? Process { get; set; }
            public List<Segment> Segments { get; } = new();
            public CancellationTokenSource Delay { get; set; } = new();
        }

        private static readonly ILogger Logger = Log.ForContext(typeof(RecordingSegmentMerger));
        private static readonly Dictionary<string, Batch> Batches = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object Gate = new();

        public static void Enqueue(string flvPath, DateTime open, DateTime close, double duration,
            string groupKey, Func<bool>? isLive, Func<string, Task> process)
        {
            if (!File.Exists(flvPath)) return;
            var directory = Path.GetDirectoryName(flvPath) ?? "";
            lock (Gate)
            {
                if (!Batches.TryGetValue(groupKey, out var batch))
                {
                    batch = new Batch { OutputDirectory = directory };
                    Batches[groupKey] = batch;
                }
                batch.IsLive = isLive;
                batch.Process = process;
                batch.Segments.RemoveAll(s => string.Equals(s.Path, flvPath, StringComparison.OrdinalIgnoreCase));
                batch.Segments.Add(new Segment
                {
                    Path = flvPath,
                    Open = open,
                    Close = close,
                    Duration = duration,
                });
                batch.Delay.Cancel();
                batch.Delay = new CancellationTokenSource();
                var token = batch.Delay.Token;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(20), token);
                        // 分段结束不代表直播结束：主播还在播时继续等，直到直播真正结束。
                        while (batch.IsLive?.Invoke() == true)
                            await Task.Delay(TimeSpan.FromSeconds(20), token);
                        await FlushAsync(groupKey, process);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { Logger.Warning(ex, "自动合并录制分段失败"); }
                });
            }
        }

        private static async Task FlushAsync(string groupKey, Func<string, Task>? process = null)
        {
            List<Segment> segments;
            string directory;
            Func<string, Task>? batchProcess;
            lock (Gate)
            {
                if (!Batches.TryGetValue(groupKey, out var batch)) return;
                Batches.Remove(groupKey);
                directory = batch.OutputDirectory;
                batchProcess = batch.Process;
                segments = batch.Segments.OrderBy(s => s.Open).ToList();
            }
            process ??= batchProcess;
            if (process == null) return;

            foreach (var group in GroupSegments(segments))
            {
                if (group.Count == 1)
                {
                    await process(group[0].Path);
                    continue;
                }

                var merged = Path.Combine(directory,
                    Path.GetFileNameWithoutExtension(group[0].Path) + "-merged-" + DateTime.Now.ToString("HHmmss") + ".flv");
                if (await ConcatAsync(group, merged))
                {
                    MergeDanmaku(group, merged);
                    Logger.Information("录制分段已合并：{Merged}", merged);
                    await process(merged);
                }
                else
                {
                    foreach (var segment in group)
                        await process(segment.Path);
                }
            }
        }

        public static void FlushRoom(int roomId)
        {
            List<string> keys;
            lock (Gate)
            {
                var prefix = "room-" + roomId + "|";
                keys = Batches.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
                foreach (var key in keys)
                {
                    if (Batches.TryGetValue(key, out var batch))
                        batch.Delay.Cancel();
                }
            }
            foreach (var key in keys)
                _ = Task.Run(() => FlushAsync(key));
        }

        private static void MergeDanmaku(IReadOnlyList<Segment> segments, string mergedFlv)
        {
            try
            {
                var docs = segments
                    .Select(s => (Segment: s, Path: s.DanmakuPath))
                    .Where(x => File.Exists(x.Path))
                    .ToList();
                if (docs.Count == 0) return;

                var outputPath = Path.ChangeExtension(mergedFlv, ".xml");
                XDocument output = XDocument.Load(docs[0].Path);
                var root = output.Root;
                if (root == null) return;

                double offset = 0;
                for (var i = 0; i < docs.Count; i++)
                {
                    var doc = i == 0 ? output : XDocument.Load(docs[i].Path);
                    var items = doc.Root?.Elements("d").ToList() ?? new List<XElement>();
                    foreach (var item in items)
                    {
                        var p = item.Attribute("p")?.Value;
                        if (string.IsNullOrWhiteSpace(p)) continue;
                        var parts = p.Split(',');
                        if (parts.Length == 0 ||
                            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var time))
                            continue;
                        parts[0] = (time + offset).ToString("0.000", CultureInfo.InvariantCulture);
                        item.SetAttributeValue("p", string.Join(",", parts));
                        if (i > 0) root.Add(new XElement(item));
                    }

                    offset += docs[i].Segment.Duration > 0
                        ? docs[i].Segment.Duration
                        : Math.Max(0, (docs[i].Segment.Close - docs[i].Segment.Open).TotalSeconds);
                }

                output.Save(outputPath);
                Logger.Information("弹幕分段已合并：{Xml}", outputPath);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "合并弹幕分段失败");
            }
        }

        private static IEnumerable<List<Segment>> GroupSegments(IReadOnlyList<Segment> segments)
        {
            var group = new List<Segment>();
            foreach (var segment in segments)
            {
                if (group.Count > 0 && segment.Open - group[group.Count - 1].Close > TimeSpan.FromMinutes(15))
                {
                    yield return group;
                    group = new List<Segment>();
                }
                group.Add(segment);
            }
            if (group.Count > 0) yield return group;
        }

        private static async Task<bool> ConcatAsync(IReadOnlyList<Segment> segments, string merged)
        {
            try
            {
                var listPath = Path.Combine(Path.GetDirectoryName(merged)!,
                    "concat-" + Guid.NewGuid().ToString("N") + ".txt");
                var lines = segments.Select(s => "file '" + s.Path.Replace("'", "'\\''") + "'");
                File.WriteAllLines(listPath, lines, new UTF8Encoding(false));
                var ffmpeg = Path.Combine(AppContext.BaseDirectory, "lib", "miniffmpeg");
                if (!File.Exists(ffmpeg)) return false;
                var psi = new ProcessStartInfo(ffmpeg)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    WorkingDirectory = Path.GetDirectoryName(ffmpeg)!,
                    Arguments = "-hide_banner -loglevel error -y -f concat -safe 0 -i \"" + listPath +
                                "\" -c copy \"" + merged + "\"",
                };
                using var process = Process.Start(psi);
                if (process == null) return false;
                await Task.Run(() => process.WaitForExit());
                try { File.Delete(listPath); } catch { }
                return process.ExitCode == 0 && File.Exists(merged) && new FileInfo(merged).Length > 0;
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "ffmpeg 合并分段失败");
                return false;
            }
        }
    }
}
