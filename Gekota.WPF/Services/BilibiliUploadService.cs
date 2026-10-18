using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

#nullable enable
namespace Gekota.WPF.Services
{
    public sealed class UploadedVideoPart
    {
        public string Filename { get; set; } = string.Empty;
        public long Cid { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Desc { get; set; } = string.Empty;
    }

    public sealed class BiliArchivePayload
    {
        public List<UploadedVideoPart> Videos { get; set; } = new List<UploadedVideoPart>();
        public string Cover { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public int Copyright { get; set; } = 2;
        public int Tid { get; set; }
        public string Tag { get; set; } = string.Empty;
        public string Desc { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Dynamic { get; set; } = string.Empty;
        public bool NoReprint { get; set; } = true;
        public bool CloseDanmu { get; set; }
        public bool CloseReply { get; set; }
    }

    public sealed class BiliCaptchaRequiredException : Exception
    {
        public BiliCaptchaRequiredException(JObject response) : base("投稿需要验证码") { this.Response = response; }
        public JObject Response { get; }
    }

    /// <summary>视频超过 B 站 16GB 单文件上限。</summary>
    public sealed class VideoTooLargeException : Exception
    {
        public string FilePath { get; }
        public VideoTooLargeException(string filePath, long size)
            : base("视频超过 16GB 限制: " + Path.GetFileName(filePath)
                   + "（" + (size / 1024.0 / 1024 / 1024).ToString("0.00") + "GB）")
        {
            this.FilePath = filePath;
        }
    }

    /// <summary>一次 upos 上传的上下文（供断点续传持久化）。</summary>
    internal sealed class UploadContext
    {
        public long Size { get; set; }
        public string Auth { get; set; } = string.Empty;
        public long BizId { get; set; }
        public long ChunkSize { get; set; }
        public string UploadUrl { get; set; } = string.Empty;
        public string UploadId { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public int ChunkCount { get; set; }
        public Dictionary<int, string> Etags { get; set; } = new Dictionary<int, string>();
    }

    public sealed class BiliUploadService
    {
        private static readonly ILogger logger = Log.ForContext<BiliUploadService>();
        private const int MaxConcurrentChunks = 4;
        private const int MaxAttempts = 3;
        public const long MaxVideoBytes = 16L * 1024 * 1024 * 1024; // B 站单文件上限 16GB

        private readonly HttpClient client;
        private readonly string csrf;

        public BiliUploadService(string cookie)
        {
            this.csrf = ExtractCookieValue(cookie, "bili_jct") ?? string.Empty;
            var handler = new HttpClientHandler { UseCookies = false, UseDefaultCredentials = false, AllowAutoRedirect = true };
            this.client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
            this.client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36 Edg/132.0.0.0");
            this.client.DefaultRequestHeaders.Referrer = new Uri("https://member.bilibili.com/");
            this.client.DefaultRequestHeaders.Add("Cookie", cookie);
        }

        public bool HasCsrf => !string.IsNullOrWhiteSpace(this.csrf);

        private static string? ExtractCookieValue(string cookie, string key)
        {
            foreach (var part in cookie.Split(';'))
            {
                var kv = part.Trim().Split('=');
                if (kv.Length >= 2 && kv[0].Trim() == key) return kv[1].Trim();
            }
            return null;
        }

        // ==================== 通用重试 ====================

        private async Task<T> RetryAsync<T>(Func<Task<T>> action, string what, CancellationToken ct, int max = MaxAttempts)
        {
            Exception? last = null;
            for (var attempt = 1; attempt <= max; attempt++)
            {
                try { return await action().ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt == max) break;
                    logger.Warning("[上传] {What} 第 {Attempt}/{Max} 次失败，重试: {Msg}",
                        what, attempt, max, ex.Message);
                    await Task.Delay(TimeSpan.FromSeconds(attempt), ct).ConfigureAwait(false);
                }
            }
            throw last!;
        }

        // ==================== 上传一个视频 ====================

        public async Task<(string filename, long cid)> UploadVideoAsync(string filePath, IProgress<double>? progress, CancellationToken ct)
        {
            var name = Path.GetFileName(filePath);
            var size = new FileInfo(filePath).Length;

            // B 站单文件上限 16GB，超过直接抛专用异常
            if (size > MaxVideoBytes)
                throw new VideoTooLargeException(filePath, size);

            // ① 断点续传：复用已保存的上下文
            var ctx = ResumeStore.Load(filePath);
            if (ctx != null && ctx.Size == size && ctx.Etags.Count > 0)
            {
                logger.Information("[上传] 断点续传 {File}，已传 {Done}/{Total} 分片",
                    name, ctx.Etags.Count, ctx.ChunkCount);
            }
            else
            {
                ctx = await this.InitUploadAsync(name, size, ct).ConfigureAwait(false);
                ctx.Size = size;
                ResumeStore.Save(filePath, ctx);
            }

            var chunks = ctx.ChunkCount;
            var etags = new Dictionary<int, string>(ctx.Etags);
            var doneCount = etags.Count;
            var lockObj = new object();
            var sem = new SemaphoreSlim(MaxConcurrentChunks);
            var tasks = new List<Task>();

            using var fs = File.OpenRead(filePath);
            for (var chunk = 0; chunk < chunks; chunk++)
            {
                var partNumber = chunk + 1;
                if (etags.ContainsKey(partNumber)) continue;

                await sem.WaitAsync(ct).ConfigureAwait(false);
                var ch = chunk;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        byte[] buffer;
                        int read;
                        lock (lockObj)
                        {
                            var start = (long)ch * ctx.ChunkSize;
                            var len = (int)Math.Min(ctx.ChunkSize, size - start);
                            fs.Seek(start, SeekOrigin.Begin);
                            buffer = new byte[len];
                            read = fs.Read(buffer, 0, len);
                        }

                        var startPos = (long)ch * ctx.ChunkSize;
                        var putUrl = ctx.UploadUrl
                            + "?partNumber=" + partNumber + "&uploadId=" + Uri.EscapeDataString(ctx.UploadId)
                            + "&chunk=" + ch + "&chunks=" + chunks + "&size=" + read
                            + "&start=" + startPos + "&end=" + (startPos + read) + "&total=" + size;
                        var etag = await this.PutChunkAsync(putUrl, ctx.Auth, buffer, read, ct).ConfigureAwait(false);

                        lock (lockObj)
                        {
                            etags[partNumber] = etag;
                            ctx.Etags = new Dictionary<int, string>(etags);
                            ResumeStore.Save(filePath, ctx);
                        }
                        var done = Interlocked.Increment(ref doneCount);
                        progress?.Report(done * 100.0 / chunks);
                    }
                    finally { sem.Release(); }
                }, ct));
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);

            // ② 结束上传
            var endUrl = ctx.UploadUrl + "?output=json&name=" + Uri.EscapeDataString(name)
                + "&profile=" + Uri.EscapeDataString("ugcfx/bup")
                + "&uploadId=" + Uri.EscapeDataString(ctx.UploadId) + "&biz_id=" + ctx.BizId;
            var endParts = new JArray();
            for (var i = 1; i <= chunks; i++)
            {
                if (etags.TryGetValue(i, out var e))
                    endParts.Add(new JObject { ["partNumber"] = i, ["eTag"] = e });
            }
            var endBody = new JObject { ["parts"] = endParts };
            await this.PostJsonAsync(endUrl, ctx.Auth, endBody, ct).ConfigureAwait(false);

            ResumeStore.Delete(filePath);
            return (ctx.Key, ctx.BizId);
        }

        private async Task<UploadContext> InitUploadAsync(string name, long size, CancellationToken ct)
        {
            var preUrl = "https://member.bilibili.com/preupload?name=" + Uri.EscapeDataString(name)
                + "&r=upos&profile=" + Uri.EscapeDataString("ugcfx/bup") + "&size=" + size;
            var pre = await this.GetJsonAsync(preUrl, null, ct).ConfigureAwait(false);
            if ((pre["OK"]?.ToObject<int>() ?? 0) != 1) throw new Exception("预上传失败: " + pre.ToString(Formatting.None));

            var auth = pre["auth"]?.ToString() ?? throw new Exception("预上传缺少 auth");
            var bizId = pre["biz_id"]?.ToObject<long>() ?? 0;
            var chunkSize = pre["chunk_size"]?.ToObject<long>() ?? 10485760;
            var endpoint = (pre["endpoint"]?.ToString() ?? string.Empty).TrimEnd('/');
            var uposUri = pre["upos_uri"]?.ToString() ?? string.Empty;

            var uposPath = uposUri.Replace("upos://", string.Empty);

            // endpoint 可能是 //host、https://host 或裸 host，三种都要拼对
            string uploadUrl;
            if (endpoint.StartsWith("//"))
                uploadUrl = "https:" + endpoint + "/" + uposPath;
            else if (endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                  || endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                uploadUrl = endpoint + "/" + uposPath;
            else
                uploadUrl = "https://" + endpoint + "/" + uposPath;

            var key = Path.GetFileNameWithoutExtension(uposPath);

            // upos 分片初始化必须用 POST，参数是 ?uploads（不带等号）
            var metaUrl = uploadUrl + "?uploads&output=json&profile=" + Uri.EscapeDataString("ugcfx/bup")
                + "&filesize=" + size + "&partsize=" + chunkSize + "&biz_id=" + bizId;

            var meta = await this.PostEmptyAsync(metaUrl, auth, ct).ConfigureAwait(false);
            var uploadId = meta["upload_id"]?.ToString();
            if (string.IsNullOrEmpty(uploadId))
            {
                // 兜底：个别 CDN 只接受 GET
                meta = await this.GetJsonAsync(metaUrl, auth, ct).ConfigureAwait(false);
                uploadId = meta["upload_id"]?.ToString();
            }
            if (string.IsNullOrEmpty(uploadId))
                throw new Exception("上传元数据缺少 upload_id，响应: " + meta.ToString(Formatting.None));

            var chunks = (int)((size + chunkSize - 1) / chunkSize);
            return new UploadContext
            {
                Auth = auth, BizId = bizId, ChunkSize = chunkSize,
                UploadUrl = uploadUrl, UploadId = uploadId!, Key = key, ChunkCount = chunks,
            };
        }

        // ==================== 封面 / 分区 / 投稿 ====================

        public async Task<string> UploadCoverAsync(string imagePath)
        {
            return await this.RetryAsync(async () =>
            {
                var dataUri = ToCoverDataUri(imagePath);
                var url = "https://member.bilibili.com/x/vu/web/cover/up?csrf=" + Uri.EscapeDataString(this.csrf);
                // 不能用 FormUrlEncodedContent：它内部用 Uri.EscapeDataString 编码字段值，
                // 超过 65519 字符会抛「无效的 URI: URI 字符串太长」。这里手工编码。
                var body = "cover=" + UrlEncodeLong(dataUri);
                using var content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
                var jo = await this.PostFormAsync(url, content, CancellationToken.None).ConfigureAwait(false);
                if ((jo["code"]?.ToObject<int>() ?? -1) != 0) throw new Exception("上传封面失败: " + jo.ToString(Formatting.None));
                return jo["data"]?["url"]?.ToString() ?? throw new Exception("封面 URL 缺失");
            }, "上传封面", CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>把封面等比缩到最长边 1280 并压成 JPEG，返回 data URI（避免超大 base64）。</summary>
        private static string ToCoverDataUri(string imagePath)
        {
            const int MaxEdge = 1280;

            using var fs = File.OpenRead(imagePath);
            using var src = System.Drawing.Image.FromStream(fs);

            var scale = Math.Min(1.0, Math.Min((double)MaxEdge / src.Width, (double)MaxEdge / src.Height));
            var w = Math.Max(1, (int)(src.Width * scale));
            var h = Math.Max(1, (int)(src.Height * scale));

            using var bmp = new System.Drawing.Bitmap(w, h);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, w, h);
            }

            using var ms = new MemoryStream();
            var codec = GetJpegCodec();
            if (codec != null)
            {
                using var eps = new System.Drawing.Imaging.EncoderParameters(1);
                eps.Param[0] = new System.Drawing.Imaging.EncoderParameter(
                    System.Drawing.Imaging.Encoder.Quality, 85L);
                bmp.Save(ms, codec, eps);
            }
            else
            {
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
            }

            var b64 = Convert.ToBase64String(ms.ToArray());
            logger.Information("[上传] 封面 {File} -> {W}x{H}, base64 {Kb}KB",
                Path.GetFileName(imagePath), w, h, b64.Length * 3 / 4 / 1024);
            return "data:image/jpeg;base64," + b64;
        }

        private static System.Drawing.Imaging.ImageCodecInfo? GetJpegCodec()
        {
            foreach (var c in System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders())
                if (c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid) return c;
            return null;
        }

        public async Task<int> PredictTidAsync(string filename, string title)
        {
            return await this.RetryAsync(async () =>
            {
                var url = "https://member.bilibili.com/x/vupre/web/archive/types/predict?csrf=" + Uri.EscapeDataString(this.csrf);
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("filename", filename ?? string.Empty),
                    new KeyValuePair<string, string>("title", title ?? string.Empty),
                });
                var jo = await this.PostFormAsync(url, content, CancellationToken.None).ConfigureAwait(false);
                if ((jo["code"]?.ToObject<int>() ?? -1) != 0) throw new Exception("预测分区失败: " + jo.ToString(Formatting.None));
                var arr = jo["data"] as JArray;
                if (arr == null || arr.Count == 0) return 0;
                return arr[0]?["id"]?.ToObject<int>() ?? 0;
            }, "预测分区", CancellationToken.None).ConfigureAwait(false);
        }

        public async Task<(long aid, string bvid)> SubmitArchiveAsync(BiliArchivePayload p)
        {
            Exception? last = null;
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try { return await this.SubmitArchiveCoreAsync(p).ConfigureAwait(false); }
                catch (BiliCaptchaRequiredException) { throw; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt == MaxAttempts) break;
                    logger.Warning("[上传] 投稿第 {Attempt}/{Max} 次失败，重试: {Msg}", attempt, MaxAttempts, ex.Message);
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2)).ConfigureAwait(false);
                }
            }
            throw last!;
        }

        private async Task<(long aid, string bvid)> SubmitArchiveCoreAsync(BiliArchivePayload p)
        {
            var ts = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            var url = "https://member.bilibili.com/x/vu/web/add/v3?ts=" + ts + "&csrf=" + Uri.EscapeDataString(this.csrf);

            var videos = new JArray();
            foreach (var v in p.Videos)
            {
                videos.Add(new JObject
                {
                    ["filename"] = v.Filename, ["title"] = v.Title, ["desc"] = v.Desc, ["cid"] = v.Cid,
                });
            }

            var body = new JObject
            {
                ["videos"] = videos, ["cover"] = p.Cover, ["title"] = p.Title, ["copyright"] = p.Copyright,
                ["tid"] = p.Tid, ["tag"] = p.Tag, ["desc_format_id"] = 9999, ["desc"] = p.Desc,
                ["source"] = p.Source, ["recreate"] = -1, ["dynamic"] = p.Dynamic, ["interactive"] = 0,
                ["act_reserve_create"] = 0, ["no_disturbance"] = 0, ["no_reprint"] = p.NoReprint ? 1 : 0,
                ["subtitle"] = new JObject { ["open"] = 0, ["lan"] = string.Empty },
                ["dolby"] = 0, ["lossless_music"] = 0, ["up_selection_reply"] = false,
                ["up_close_reply"] = p.CloseReply, ["up_close_danmu"] = p.CloseDanmu, ["web_os"] = 3, ["csrf"] = this.csrf,
            };

            var content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
            var resp = await this.client.PostAsync(url, content).ConfigureAwait(false);
            var respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var jo = JObject.Parse(respBody);
            var code = jo["code"]?.ToObject<int>() ?? -1;

            if (code == -210 || code == -352 || jo["data"]?["gt"] != null) throw new BiliCaptchaRequiredException(jo);
            if (code != 0) throw new Exception("投稿失败(code " + code + "): " + (jo["message"]?.ToString() ?? respBody));

            var aid = jo["data"]?["aid"]?.ToObject<long>() ?? 0;
            var bvid = jo["data"]?["bvid"]?.ToString() ?? string.Empty;
            return (aid, bvid);
        }

        // ==================== HTTP 基础方法（带重试） ====================

        /// <summary>无 body 的 POST（upos 分片初始化用）。</summary>
        private async Task<JObject> PostEmptyAsync(string url, string? uposAuth, CancellationToken ct)
        {
            return await this.RetryAsync(async () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                if (uposAuth != null) req.Headers.TryAddWithoutValidation("X-Upos-Auth", uposAuth);
                using var resp = await this.client.SendAsync(req, ct).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JObject.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            }, "POST(empty) " + url, ct).ConfigureAwait(false);
        }

        private async Task<JObject> GetJsonAsync(string url, string? uposAuth, CancellationToken ct)
        {
            return await this.RetryAsync(async () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (uposAuth != null) req.Headers.TryAddWithoutValidation("X-Upos-Auth", uposAuth);
                using var resp = await this.client.SendAsync(req, ct).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JObject.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            }, "GET " + (url.Length > 80 ? url.Substring(0, 80) : url), ct).ConfigureAwait(false);
        }

        private async Task<JObject> PostJsonAsync(string url, string? uposAuth, JObject body, CancellationToken ct)
        {
            return await this.RetryAsync(async () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                if (uposAuth != null) req.Headers.TryAddWithoutValidation("X-Upos-Auth", uposAuth);
                req.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using var resp = await this.client.SendAsync(req, ct).ConfigureAwait(false);
                var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JObject.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            }, "POST " + url, ct).ConfigureAwait(false);
        }

        /// <summary>分段安全的长字符串 form 编码（避免 Uri.EscapeDataString 的长度限制）。</summary>
        private static string UrlEncodeLong(string s)
        {
            var sb = new StringBuilder(s.Length + s.Length / 4);
            foreach (var c in s)
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                    || c == '-' || c == '_' || c == '.' || c == '~')
                {
                    sb.Append(c);
                }
                else
                {
                    sb.Append('%');
                    sb.Append(((int)c).ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            return sb.ToString();
        }

        private async Task<JObject> PostFormAsync(string url, HttpContent content, CancellationToken ct)
        {
            return await this.RetryAsync(async () =>
            {
                using var resp = await this.client.PostAsync(url, content, ct).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JObject.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            }, "POST " + url, ct).ConfigureAwait(false);
        }

        private async Task<string> PutChunkAsync(string url, string auth, byte[] buffer, int length, CancellationToken ct)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Put, url);
                    req.Headers.TryAddWithoutValidation("X-Upos-Auth", auth);
                    req.Content = new ByteArrayContent(buffer, 0, length);
                    req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    using var resp = await this.client.SendAsync(req, ct).ConfigureAwait(false);
                    var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (text.Contains("MULTIPART_PUT_SUCCESS"))
                        return (resp.Headers.ETag?.Tag ?? string.Empty).Trim('"');
                    throw new Exception("分片上传失败: " + text);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (attempt < MaxAttempts)
                {
                    logger.Warning("[上传] 分片第 {Attempt}/{Max} 次失败，重试: {Msg}", attempt, MaxAttempts, ex.Message);
                    await Task.Delay(TimeSpan.FromSeconds(attempt), ct).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>断点续传状态存储（侧车 JSON）。</summary>
    internal static class ResumeStore
    {
        private static string StatePath(string filePath) => filePath + ".gekota.upload.json";

        public static UploadContext? Load(string filePath)
        {
            try
            {
                var p = StatePath(filePath);
                if (!File.Exists(p)) return null;
                return JsonConvert.DeserializeObject<UploadContext>(File.ReadAllText(p));
            }
            catch (Exception ex)
            {
                Log.ForContext(typeof(ResumeStore)).Debug(ex, "[上传] 读取续传状态失败");
                return null;
            }
        }

        public static void Save(string filePath, UploadContext ctx)
        {
            try { File.WriteAllText(StatePath(filePath), JsonConvert.SerializeObject(ctx, Formatting.Indented)); }
            catch (Exception ex) { Log.ForContext(typeof(ResumeStore)).Debug(ex, "[上传] 保存续传状态失败"); }
        }

        public static void Delete(string filePath)
        {
            try { var p = StatePath(filePath); if (File.Exists(p)) File.Delete(p); }
            catch { }
        }
    }
}
