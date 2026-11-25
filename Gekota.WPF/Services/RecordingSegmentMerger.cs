using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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

        public sealed class ExistingMergeResult
        {
            public string MergedPath { get; set; } = "";
            public string TitleFolder { get; set; } = "";
            public DateTime Open { get; set; }
        }

        /// <summary>
        /// 启动时扫描最近的录制文件夹，把断流重连留下的多个 FLV 补合并。
        /// 已经存在 -merged- 文件的目录会跳过，避免重复处理。
        /// </summary>
        public static async Task<IReadOnlyList<ExistingMergeResult>> MergeExistingRecentAsync(
            string root, TimeSpan maxAge)
        {
            var results = new List<ExistingMergeResult>();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return results;
            var cutoff = DateTime.Now - maxAge;
            foreach (var streamerDir in Directory.EnumerateDirectories(root))
            {
                try
                {
                    if (Directory.GetLastWriteTime(streamerDir) < cutoff) continue;
                    foreach (var titleDir in Directory.EnumerateDirectories(streamerDir))
                    {
                        if (Directory.GetLastWriteTime(titleDir) < cutoff) continue;
                        var files = Directory.EnumerateFiles(titleDir, "*.flv", SearchOption.TopDirectoryOnly)
                            .Where(p => Path.GetFileName(p).IndexOf("-merged-", StringComparison.OrdinalIgnoreCase) < 0)
                            .Select(p => new { Path = p, Open = ParseFileOpenTime(p) })
                            .Where(x => x.Open != null)
                            .OrderBy(x => x.Open)
                            .ToList();
                        var existingMerged = Directory.EnumerateFiles(titleDir, "*-merged-*.flv")
                            .OrderByDescending(File.GetLastWriteTime)
                            .FirstOrDefault();                        if (existingMerged != null)
                        {
                            if (GetFlvDurationSeconds(existingMerged) > 0)
                            {
                                var existingSegments = files.Select(x => new Segment
                                {
                                    Path = x.Path,
                                    Open = x.Open!.Value,
                                    Close = x.Open!.Value,
                                    Duration = GetFlvDurationSeconds(x.Path),
                                }).ToList();
                                DeleteOriginalSegments(existingSegments, existingMerged);
                            }
                            continue;
                        }
                        if (files.Count < 2) continue;

                        var segments = files.Select(x => new Segment
                        {
                            Path = x.Path,
                            Open = x.Open!.Value,
                            Close = x.Open!.Value,
                            Duration = GetFlvDurationSeconds(x.Path),
                        }).ToList();

                        foreach (var group in GroupSegments(segments))
                        {
                            if (group.Count < 2) continue;
                            var merged = Path.Combine(titleDir,
                                Path.GetFileNameWithoutExtension(group[0].Path) + "-merged-" +
                                DateTime.Now.ToString("HHmmss") + ".flv");
                            if (File.Exists(merged)) continue;
                            if (!await ConcatAsync(group, merged).ConfigureAwait(false)) continue;
                            MergeDanmaku(group, merged);
                            if (GetFlvDurationSeconds(merged) <= 0)
                            {
                                Logger.Warning("[合并] 合并结果校验失败，保留原分段：{Merged}", merged);
                                continue;
                            }
                            DeleteOriginalSegments(group, merged);
                            Logger.Information("[合并] 已补合并历史分段：{Merged}", merged);
                            results.Add(new ExistingMergeResult
                            {
                                MergedPath = merged,
                                TitleFolder = titleDir,
                                Open = group[0].Open,
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "[合并] 扫描历史分段失败：{Dir}", streamerDir);
                }
            }
            return results;
        }

        private static DateTime? ParseFileOpenTime(string path)
        {
            var match = Regex.Match(Path.GetFileName(path), @"-(\d{8})-(\d{6})-");
            if (!match.Success) return null;
            return DateTime.TryParseExact(match.Groups[1].Value + match.Groups[2].Value,
                "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                ? time
                : null;
        }

        private static double GetFlvDurationSeconds(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                var header = new byte[13];
                if (stream.Read(header, 0, 13) != 13) return 0;
                var tag = new byte[11];
                double max = 0;
                while (stream.Position + 11 <= stream.Length)
                {
                    if (stream.Read(tag, 0, 11) != 11) break;
                    var size = (tag[1] << 16) | (tag[2] << 8) | tag[3];
                    if (stream.Position + size + 4 > stream.Length) break;
                    var ts = (tag[4] << 16) | (tag[5] << 8) | tag[6] | (tag[7] << 24);
                    if (ts > max) max = ts;
                    stream.Seek(size + 4, SeekOrigin.Current);
                }
                return max / 1000.0;
            }
            catch { return 0; }
        }

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
                Logger.Information("[合并] 已收到分段 {Path}，当前批次 {Count} 段", flvPath, batch.Segments.Count);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(20), token);
                        // 分段结束不代表直播结束：主播还在播时继续等，最多等 3 分钟，
                        // 避免直播状态异常时批次永远不落盘。
                        var deadline = DateTime.UtcNow.AddMinutes(3);
                        while (batch.IsLive?.Invoke() == true && DateTime.UtcNow < deadline)
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
            Logger.Information("[合并] 开始处理 {Count} 个分段", segments.Count);

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
                    if (GetFlvDurationSeconds(merged) <= 0)
                    {
                        Logger.Warning("[合并] 合并结果校验失败，保留原分段：{Merged}", merged);
                        foreach (var segment in group)
                            await process(segment.Path);
                        continue;
                    }
                    DeleteOriginalSegments(group, merged);
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

        /// <summary>合并结果校验通过后，删除未合并的分段视频和弹幕，避免重复占用空间。</summary>
        private static void DeleteOriginalSegments(IReadOnlyList<Segment> segments, string mergedFlv)
        {
            var mergedXml = Path.ChangeExtension(mergedFlv, ".xml");
            var hasMergedXml = File.Exists(mergedXml) && new FileInfo(mergedXml).Length > 0;
            foreach (var segment in segments)
            {
                foreach (var ext in new[] { ".flv", ".mp4", ".xml", ".ass" })
                {
                    if (ext == ".xml" && !hasMergedXml) continue;
                    var path = Path.ChangeExtension(segment.Path, ext);
                    if (string.Equals(path, mergedFlv, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(path, mergedXml, StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        if (File.Exists(path)) File.Delete(path);
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug(ex, "[合并] 删除旧分段失败：{Path}", path);
                    }
                }
            }
        }

        /// <summary>
        /// 直接拼接各分段的 ASS，避免重新从 XML 转换时因换行/过滤导致条数变少。
        /// </summary>
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
                // miniffmpeg 是精简构建，不包含 concat demuxer，不能直接 -f concat。
                // FLV 的 tag 结构固定，这里按 tag 拼接并重写时间戳，避免依赖外部完整 ffmpeg。
                await Task.Run(() => ConcatFlv(segments, merged)).ConfigureAwait(false);
                return File.Exists(merged) && new FileInfo(merged).Length > 0;
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "FLV 分段合并失败");
                try { File.Delete(merged); } catch { }
                return false;
            }
        }

        private static void ConcatFlv(IReadOnlyList<Segment> segments, string outputPath)
        {
            using var output = File.Create(outputPath);
            long offsetMs = 0;
            for (var index = 0; index < segments.Count; index++)
            {
                var segment = segments[index];
                using var input = File.OpenRead(segment.Path);
                if (input.Length < 13) throw new InvalidDataException("FLV 文件过短");
                var header = new byte[9];
                if (input.Read(header, 0, header.Length) != header.Length ||
                    header[0] != (byte)'F' || header[1] != (byte)'L' || header[2] != (byte)'V')
                    throw new InvalidDataException("不是有效的 FLV 文件");

                if (index == 0)
                {
                    output.Write(header, 0, header.Length);
                    var previous0 = new byte[4];
                    if (input.Read(previous0, 0, 4) != 4) throw new InvalidDataException("FLV 头不完整");
                    output.Write(previous0, 0, 4);
                }
                else
                {
                    // 跳过 header + PreviousTagSize0。
                    input.Seek(13, SeekOrigin.Begin);
                }

                var tagHeader = new byte[11];
                var dataBuffer = new byte[64 * 1024];
                double maxTimestamp = 0;
                while (input.Position + 11 <= input.Length)
                {
                    if (input.Read(tagHeader, 0, 11) != 11) break;
                    var tagType = tagHeader[0];
                    var dataSize = (tagHeader[1] << 16) | (tagHeader[2] << 8) | tagHeader[3];
                    if (dataSize < 0 || input.Position + dataSize + 4 > input.Length) break;

                    var timestamp = (tagHeader[4] << 16) | (tagHeader[5] << 8) | tagHeader[6] |
                                    (tagHeader[7] << 24);
                    maxTimestamp = Math.Max(maxTimestamp, timestamp);

                    // 后续分段重复的脚本元数据会覆盖时长，跳过。
                    if (!(index > 0 && tagType == 18))
                    {
                        var adjusted = (long)Math.Round((double)(timestamp + offsetMs));
                        adjusted = Math.Max(0, Math.Min(0xFFFFFFFFL, adjusted));
                        tagHeader[4] = (byte)((adjusted >> 16) & 0xFF);
                        tagHeader[5] = (byte)((adjusted >> 8) & 0xFF);
                        tagHeader[6] = (byte)(adjusted & 0xFF);
                        tagHeader[7] = (byte)((adjusted >> 24) & 0xFF);
                        output.Write(tagHeader, 0, 11);

                        var remaining = dataSize;
                        while (remaining > 0)
                        {
                            var read = input.Read(dataBuffer, 0, Math.Min(dataBuffer.Length, remaining));
                            if (read <= 0) throw new EndOfStreamException("FLV tag 数据不完整");
                            output.Write(dataBuffer, 0, read);
                            remaining -= read;
                        }
                    }
                    else
                    {
                        input.Seek(dataSize, SeekOrigin.Current);
                    }

                    var previous = new byte[4];
                    if (input.Read(previous, 0, 4) != 4) break;
                    if (!(index > 0 && tagType == 18))
                        output.Write(previous, 0, 4);
                }

                var durationMs = segment.Duration > 0
                    ? segment.Duration * 1000
                    : Math.Max(0, maxTimestamp);
                offsetMs += (long)Math.Round((double)durationMs);
            }
        }
    }
}
