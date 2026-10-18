using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gekota.WPF.Controls;
using Serilog;

#nullable enable
namespace Gekota.WPF.Services
{

    /// <summary>投稿时由用户在界面上覆盖的字段；为空则回退到自动推导。</summary>
    public sealed class UploadOverrides
    {
        public string? Title { get; set; }
        public string? Desc { get; set; }
        public string? Tags { get; set; }
        public int Tid { get; set; }          // 0 = 自动匹配
        public string? CoverPath { get; set; } // null = 自动抽帧
        public int Copyright { get; set; } = 2; // 1 自制 / 2 转载
        public string? Source { get; set; }
        public bool NoReprint { get; set; } = true;
        public bool CloseDanmu { get; set; }
        public bool CloseReply { get; set; }
    }

    public sealed class RecordingInfo
    {
        public string FlvPath { get; set; } = string.Empty;
        public long RoomId { get; set; }
        public string StreamerName { get; set; } = string.Empty;
        public string LiveTitle { get; set; } = string.Empty;
        public DateTimeOffset Date { get; set; } = DateTimeOffset.Now;
    }

    public sealed class BiliUploadOrchestrator
    {
        private static readonly ILogger logger = Log.ForContext<BiliUploadOrchestrator>();

        public async Task<string> UploadAsync(RecordingInfo info, string cookie, IProgress<string>? status, CancellationToken ct, UploadOverrides? overrides = null, IProgress<double>? fileProgress = null)
        {
            var dir = Path.GetDirectoryName(info.FlvPath) ?? string.Empty;
            var baseName = Path.GetFileNameWithoutExtension(info.FlvPath);

            var streamerFolder = Path.GetDirectoryName(dir) ?? dir;
            var fangzhuangPath = FindFangzhuang(streamerFolder);

            // 纯净版/弹幕版不限容器（mp4/mkv/flv/ts），避免压制容器与硬编码不一致
            var cleanPath = FindVariant(dir, baseName, "【纯净版】");
            var danmakuPath = FindVariant(dir, baseName, "【弹幕版】");
            var cleanExists = cleanPath != null;
            var danmakuExists = danmakuPath != null;

            if (!cleanExists && !danmakuExists && fangzhuangPath == null)
            {
                // 兜底：没有压制产物时，直接上传转封装后的原始文件
                var rawPath = FindVariant(dir, baseName, string.Empty);
                if (rawPath != null)
                {
                    logger.Information("[上传] 未找到压制产物，改为上传原始文件 {File}", rawPath);
                    cleanPath = rawPath;
                    cleanExists = true;
                }
                else
                {
                    throw new Exception("没有可上传的文件（纯净版/弹幕版/防撞/原始文件都不存在）");
                }
            }

            // 任一文件超 16GB → 整单跳过（不传任何分 P）
            CheckAllWithinLimit(cleanPath, danmakuPath, fangzhuangPath);

            var autoTitle = "【" + info.StreamerName + "】" + info.Date.ToString("yyyy年MM月dd日") + " " + info.LiveTitle;
            var title = string.IsNullOrWhiteSpace(overrides?.Title) ? autoTitle : overrides!.Title!;
            var desc = overrides?.Desc ?? ReadDescriptionTxt(streamerFolder);
            var rawTags = string.IsNullOrWhiteSpace(overrides?.Tags) ? StreamerTagPresetStore.Get(streamerFolder) : overrides!.Tags!;
            var tags = NormalizeTags(rawTags);
            if (string.IsNullOrWhiteSpace(tags))
                logger.Information("[上传] 未找到 tag.txt，标签留空（交由 B 站处理）");
            if (string.IsNullOrWhiteSpace(desc))
                logger.Information("[上传] 未找到 简介.txt，简介留空");
            var source = string.IsNullOrWhiteSpace(overrides?.Source) ? title + "【直播录像】" : overrides!.Source!;
            var copyright = overrides?.Copyright ?? 2;

            var svc = new BiliUploadService(cookie);

            // 封面：① 用户指定 → ② 直播文件夹里的截屏 → ③ 视频关键帧
            string? coverLocal;
            var coverIsAuto = false;
            if (!string.IsNullOrWhiteSpace(overrides?.CoverPath) && File.Exists(overrides!.CoverPath))
            {
                coverLocal = overrides.CoverPath;
            }
            else if (FindScreenshot(dir) is { } shot)
            {
                coverLocal = shot;
                logger.Information("[上传] 使用直播文件夹截屏作为封面 {File}", shot);
            }
            else
            {
                var coverSource = cleanExists ? cleanPath! : (danmakuExists ? danmakuPath! : fangzhuangPath!);
                coverLocal = await VideoCoverExtractor.ExtractKeyframeAsync(coverSource).ConfigureAwait(false);
                coverIsAuto = true;
            }

            logger.Information("[上传] 待上传：纯净版={Clean} | 弹幕版={Danmaku} | 防撞={Fangzhuang}",
                cleanPath ?? "(无)", danmakuPath ?? "(无)", fangzhuangPath ?? "(无)");

            var parts = new List<UploadedVideoPart>();
            if (cleanExists)
            {
                status?.Report("上传【纯净版】");
                var (fn, cid) = await svc.UploadVideoAsync(cleanPath!, fileProgress, ct).ConfigureAwait(false);
                parts.Add(new UploadedVideoPart { Filename = fn, Cid = cid, Title = "纯净版", Desc = string.Empty });
            }
            if (danmakuExists)
            {
                status?.Report("上传【弹幕版】");
                var (fn, cid) = await svc.UploadVideoAsync(danmakuPath!, fileProgress, ct).ConfigureAwait(false);
                parts.Add(new UploadedVideoPart { Filename = fn, Cid = cid, Title = "弹幕版", Desc = string.Empty });
            }
            if (fangzhuangPath != null)
            {
                status?.Report("上传【防撞】");
                var (fn, cid) = await svc.UploadVideoAsync(fangzhuangPath, fileProgress, ct).ConfigureAwait(false);
                parts.Add(new UploadedVideoPart { Filename = fn, Cid = cid, Title = "防撞", Desc = string.Empty });
            }

            string coverUrl = string.Empty;
            if (!string.IsNullOrEmpty(coverLocal))
            {
                status?.Report("上传封面");
                coverUrl = await svc.UploadCoverAsync(coverLocal!).ConfigureAwait(false);
            }

            status?.Report("匹配分区");
            var tid = (overrides?.Tid ?? 0) > 0 ? overrides!.Tid : await svc.PredictTidAsync(parts[0].Filename, title).ConfigureAwait(false);

            status?.Report("提交投稿");
            var payload = new BiliArchivePayload
            {
                Videos = parts, Cover = coverUrl, Title = title, Copyright = copyright, Tid = tid,
                Tag = tags!, Desc = desc, Source = source,
                NoReprint = overrides?.NoReprint ?? true,
                CloseDanmu = overrides?.CloseDanmu ?? false,
                CloseReply = overrides?.CloseReply ?? false,
            };
            var (aid, bvid) = await this.SubmitWithCaptchaAsync(svc, payload, cookie).ConfigureAwait(false);
            logger.Information("[上传] 投稿成功 aid={Aid} bvid={Bvid}", aid, bvid);

            // 仅删除自动抽取的封面，不删用户指定的封面图
            DeleteRecordingFiles(dir, baseName, coverIsAuto ? coverLocal : null);
            return bvid;
        }

        private async Task<(long aid, string bvid)> SubmitWithCaptchaAsync(BiliUploadService svc, BiliArchivePayload payload, string cookie)
        {
            while (true)
            {
                try
                {
                    return await svc.SubmitArchiveAsync(payload).ConfigureAwait(false);
                }
                catch (BiliCaptchaRequiredException ex)
                {
                    var url = ex.Response["data"]?["url"]?.ToString() ?? ex.Response["url"]?.ToString();
                    if (string.IsNullOrWhiteSpace(url))
                        throw new Exception("投稿需要验证码，但未返回验证码地址，请到网页版手动投稿");

                    var resultCookie = await ShowCaptchaAsync(url!).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(resultCookie))
                        throw new Exception("验证码未完成，已取消投稿");

                    cookie = MergeCookies(cookie, resultCookie!);
                    svc = new BiliUploadService(cookie);
                }
            }
        }

        private static async Task<string?> ShowCaptchaAsync(string url)
        {
            var app = System.Windows.Application.Current;
            if (app == null) return null;

            var op = app.Dispatcher.InvokeAsync(new Func<Task<string?>>(async () =>
            {
                var dialog = new CaptchaDialog { Owner = app.MainWindow };
                var ok = await dialog.ShowCaptchaAsync(url).ConfigureAwait(false);
                return ok ? dialog.ResultCookie : null;
            }));
            return await op.Task.Unwrap().ConfigureAwait(false);
        }

        private static string MergeCookies(string original, string extra)
        {
            var dict = new Dictionary<string, string>();
            foreach (var part in (original + "; " + extra).Split(';'))
            {
                var kv = part.Trim().Split('=');
                if (kv.Length >= 2 && !string.IsNullOrWhiteSpace(kv[0]))
                    dict[kv[0].Trim()] = kv[1].Trim();
            }
            return string.Join("; ", dict.Select(p => p.Key + "=" + p.Value));
        }

        /// <summary>
        /// 标签规范化：B 站 tag 字段只认英文逗号分隔。
        /// 把 ; ； 、 | 换行 等统一换成逗号，去空、去重，最多 10 个。
        /// </summary>
        private static string NormalizeTags(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            var parts = raw!.Split(
                new[] { ';', '；', ',', '，', '、', '|', '/', '\n', '\r', '\t' },
                StringSplitOptions.RemoveEmptyEntries);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length == 0 || !seen.Add(t)) continue;
                list.Add(t);
                if (list.Count >= 10) break;
            }
            return string.Join(",", list);
        }

        /// <summary>任一文件超过 16GB 就抛 VideoTooLargeException（整单跳过）。</summary>
        private static void CheckAllWithinLimit(params string?[] paths)
        {
            foreach (var p in paths)
            {
                if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) continue;
                var len = new FileInfo(p).Length;
                if (len > BiliUploadService.MaxVideoBytes)
                    throw new VideoTooLargeException(p!, len);
            }
        }

        /// <summary>按后缀找同目录下任意容器的文件（mp4/mkv/flv/ts）。</summary>
        private static string? FindVariant(string dir, string baseName, string suffix)
        {
            foreach (var ext in new[] { ".mp4", ".mkv", ".flv", ".ts" })
            {
                var p = Path.Combine(dir, baseName + suffix + ext);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        private static string? FindFangzhuang(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return null;
                foreach (var f in Directory.EnumerateFiles(folder, "*.mp4", SearchOption.TopDirectoryOnly))
                    if (Path.GetFileName(f).Contains("防撞")) return f;
            }
            catch (Exception ex) { logger.Debug(ex, "[上传] 查找防撞文件失败"); }
            return null;
        }

        private const string DescriptionFileName = "简介.txt";

        private static string ReadDescriptionTxt(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return string.Empty;
                var path = Path.Combine(folder, DescriptionFileName);
                return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
            }
            catch (Exception ex) { logger.Debug(ex, "[上传] 读取简介失败"); }
            return string.Empty;
        }

        /// <summary>在直播文件夹里找最新的截图（文件名含 screenshot 或 以“截图”开头）。</summary>
        private static string? FindScreenshot(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return null;
                string? best = null;
                var bestTime = DateTime.MinValue;
                foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".webp") continue;
                    var name = Path.GetFileName(f);
                    if (name.IndexOf("screenshot", StringComparison.OrdinalIgnoreCase) < 0
                        && !name.StartsWith("截图", StringComparison.Ordinal)) continue;
                    var t = File.GetLastWriteTimeUtc(f);
                    if (t > bestTime) { bestTime = t; best = f; }
                }
                return best;
            }
            catch (Exception ex) { logger.Debug(ex, "[上传] 查找截屏失败"); }
            return null;
        }

        private static void DeleteRecordingFiles(string dir, string baseName, string? coverPath)
        {
            var files = new[]
            {
                Path.Combine(dir, baseName + ".flv"),
                Path.Combine(dir, baseName + ".xml"),
                Path.Combine(dir, baseName + ".ass"),
                Path.Combine(dir, baseName + ".mp4"),
                Path.Combine(dir, baseName + "【纯净版】.mp4"),
                Path.Combine(dir, baseName + "【弹幕版】.mp4"),
                coverPath ?? string.Empty,
            };
            foreach (var f in files)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(f) && File.Exists(f))
                    {
                        File.Delete(f);
                        logger.Information("[上传] 已删除 {File}", f);
                    }
                }
                catch (Exception ex) { logger.Warning(ex, "[上传] 删除失败 {File}", f); }
            }
        }
    }
}
