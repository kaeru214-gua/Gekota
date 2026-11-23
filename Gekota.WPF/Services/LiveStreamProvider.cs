using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Serilog;

#nullable enable
namespace Gekota.WPF.Services
{
    /// <summary>
    /// 直播取流：优先使用较新的 getRoomPlayInfo（WBI 签名），
    /// 失败时回退到老 playUrl。只返回 FLV 地址，供本机代理播放。
    /// </summary>
    internal sealed class LiveStreamProvider
    {
        private static readonly ILogger logger = Log.ForContext<LiveStreamProvider>();
        private static readonly HttpClient http;
        private static readonly SemaphoreSlim wbiLock = new(1, 1);

        private static string? wbiMixinKey;
        private static DateTime wbiAt;

        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36 Edg/132.0.0.0";

        private const int DefaultQn = 10000;

        private static readonly int[] WbiMixin =
        {
            46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35,
            27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13,
            37, 48, 7, 16, 24, 55, 40, 61, 26, 17, 0, 1, 60, 51, 30, 4,
            22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36, 20, 34, 44, 52,
        };

        private readonly Dictionary<(int RoomId, int Qn), (DateTime At, LiveStreamInfo Info)> cache = new();
        private readonly Dictionary<int, (DateTime At, List<LiveQuality> Items)> qualityCache = new();

        static LiveStreamProvider()
        {
            var handler = new HttpClientHandler
            {
                UseCookies = false,
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            };
            http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        }

        public async Task<LiveStreamInfo> GetAsync(
            int roomId, int? qn = null, CancellationToken cancellationToken = default)
        {
            var requestedQn = qn is > 0 ? qn.Value : DefaultQn;
            lock (this.cache)
            {
                if (this.cache.TryGetValue((roomId, requestedQn), out var hit) &&
                    DateTime.UtcNow - hit.At < TimeSpan.FromSeconds(60))
                {
                    return hit.Info;
                }
            }

            var info = await this.FetchNewApiAsync(roomId, requestedQn, cancellationToken).ConfigureAwait(false)
                       ?? await this.FetchLegacyAsync(roomId, requestedQn, true, cancellationToken).ConfigureAwait(false);
            if (info == null)
                throw new InvalidOperationException("没有取到直播流地址（可能未开播或接口受限）");

            lock (this.cache) this.cache[(roomId, requestedQn)] = (DateTime.UtcNow, info);
            return info;
        }

        public async Task<IReadOnlyList<LiveQuality>> GetQualitiesAsync(
            int roomId, CancellationToken cancellationToken = default)
        {
            lock (this.qualityCache)
            {
                if (this.qualityCache.TryGetValue(roomId, out var hit) &&
                    DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(5))
                {
                    return hit.Items;
                }
            }

            try
            {
                var url = "https://api.live.bilibili.com/room/v1/Room/playUrl" +
                          $"?cid={roomId}&qn={DefaultQn}&platform=web&ptype=8";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                this.AddBiliHeaders(request, roomId);
                using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var jo = JObject.Parse(body);
                var list = new List<LiveQuality>();
                foreach (var q in jo.SelectToken("data.quality_description") as JArray ?? new JArray())
                {
                    var value = q.Value<int?>("qn") ?? 0;
                    var desc = q.Value<string>("desc") ?? "";
                    if (value > 0 && !string.IsNullOrWhiteSpace(desc))
                        list.Add(new LiveQuality(value, desc));
                }

                if (list.Count > 0)
                {
                    lock (this.qualityCache) this.qualityCache[roomId] = (DateTime.UtcNow, list);
                }
                return list;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.Debug(ex, "[直播取流] 获取画质列表失败");
                return Array.Empty<LiveQuality>();
            }
        }

        private async Task<LiveStreamInfo?> FetchNewApiAsync(
            int roomId, int requestedQn, CancellationToken cancellationToken)
        {
            try
            {
                var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["room_id"] = roomId.ToString(CultureInfo.InvariantCulture),
                    ["protocol"] = "0",
                    ["format"] = "0",
                    ["codec"] = "0",
                    ["qn"] = requestedQn.ToString(CultureInfo.InvariantCulture),
                    ["platform"] = "web",
                    ["ptype"] = "8",
                    ["web_location"] = "444.8",
                };

                var url = await this.SignWbiAsync(
                    "https://api.live.bilibili.com/xlive/web-room/v2/index/getRoomPlayInfo",
                    query,
                    cancellationToken).ConfigureAwait(false);

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                this.AddBiliHeaders(request, roomId);
                using var response = await http.SendAsync(
                    request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    logger.Debug("[直播取流] 新接口 HTTP {Status}", (int)response.StatusCode);
                    return null;
                }

                var jo = JObject.Parse(body);
                if (jo.Value<int?>("code") != 0)
                {
                    logger.Debug("[直播取流] 新接口 code={Code} {Message}",
                        jo.Value<int?>("code"), jo.Value<string>("message"));
                    return null;
                }

                var data = jo["data"] as JObject;
                if (data == null) return null;
                if (data.Value<int?>("live_status") is int liveStatus && liveStatus != 1)
                    return null;

                var streams = data.SelectToken("playurl_info.playurl.stream") as JArray;
                if (streams == null) return null;

                JToken? best = null;
                foreach (var stream in streams)
                {
                    foreach (var format in stream["format"] as JArray ?? new JArray())
                    {
                        var formatName = format.Value<string>("format_name") ?? "";
                        var protocolName = stream.Value<string>("protocol_name") ?? "";
                        if (formatName.Equals("flv", StringComparison.OrdinalIgnoreCase) &&
                            protocolName.Equals("http_stream", StringComparison.OrdinalIgnoreCase))
                        {
                            best = format;
                            break;
                        }
                    }
                    if (best != null) break;
                }

                if (best == null)
                {
                    // 新接口只给了 HLS；本项目先用老接口拿 FLV，避免引入 hls.js。
                    logger.Debug("[直播取流] 新接口没有 FLV，回退老接口");
                    return null;
                }

                var codec = (best["codec"] as JArray)?.FirstOrDefault();
                if (codec == null) return null;

                var baseUrl = codec.Value<string>("base_url") ?? "";
                var urlInfo = (codec["url_info"] as JArray)?.FirstOrDefault();
                var host = urlInfo?.Value<string>("host") ?? "";
                var extra = urlInfo?.Value<string>("extra") ?? "";

                string mediaUrl;
                if (baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    mediaUrl = baseUrl;
                else if (!string.IsNullOrWhiteSpace(host))
                    mediaUrl = host.TrimEnd('/') + "/" + baseUrl.TrimStart('/') + extra;
                else
                    return null;

                var qn = codec.Value<int?>("current_qn") ?? DefaultQn;
                logger.Information("[直播取流] 新接口成功 room={Room} qn={Qn}", roomId, qn);
                return new LiveStreamInfo(mediaUrl, roomId, qn);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.Debug(ex, "[直播取流] 新接口失败，准备回退老接口");
                return null;
            }
        }

        private async Task<LiveStreamInfo?> FetchLegacyAsync(
            int roomId, int requestedQn, bool includeCookie, CancellationToken cancellationToken)
        {
            try
            {
                var url = "https://api.live.bilibili.com/room/v1/Room/playUrl" +
                          $"?cid={roomId}&qn={requestedQn}&platform=web&ptype=8";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                this.AddBiliHeaders(request, roomId, includeCookie);
                using var response = await http.SendAsync(
                    request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Forbidden && includeCookie)
                {
                    logger.Debug("[直播取流] 老接口 412，尝试匿名重试");
                    return await this.FetchLegacyAsync(roomId, requestedQn, false, cancellationToken).ConfigureAwait(false);
                }

                var jo = JObject.Parse(body);
                if (jo.Value<int?>("code") != 0)
                {
                    logger.Debug("[直播取流] 老接口 code={Code} {Message}",
                        jo.Value<int?>("code"), jo.Value<string>("message"));
                    return null;
                }

                var media = jo.SelectToken("data.durl[0].url")?.Value<string>();
                if (string.IsNullOrWhiteSpace(media)) return null;

                var qn = jo.SelectToken("data.current_qn")?.Value<int?>() ?? DefaultQn;
                logger.Information("[直播取流] 老接口成功 room={Room} qn={Qn}", roomId, qn);
                return new LiveStreamInfo(media!, roomId, qn);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.Debug(ex, "[直播取流] 老接口失败");
                return null;
            }
        }

        private void AddBiliHeaders(HttpRequestMessage request, int roomId, bool includeCookie = true)
        {
            request.Headers.Referrer = new Uri("https://live.bilibili.com/" + roomId);
            request.Headers.TryAddWithoutValidation("Origin", "https://live.bilibili.com");
            if (!includeCookie) return;
            var cookie = UserSessionStore.Load()?.Cookie;
            if (!string.IsNullOrWhiteSpace(cookie))
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        private async Task<string> SignWbiAsync(
            string baseUrl, SortedDictionary<string, string> query, CancellationToken cancellationToken)
        {
            var key = await this.GetWbiKeyAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(key))
                return baseUrl + "?" + BuildQuery(query);

            query["wts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var q = BuildQuery(query);
            var wRid = Md5Hex(q + key);
            return baseUrl + "?" + q + "&w_rid=" + wRid;
        }

        private async Task<string?> GetWbiKeyAsync(CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(wbiMixinKey) &&
                DateTime.UtcNow - wbiAt < TimeSpan.FromHours(6))
                return wbiMixinKey;

            await wbiLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!string.IsNullOrWhiteSpace(wbiMixinKey) &&
                    DateTime.UtcNow - wbiAt < TimeSpan.FromHours(6))
                    return wbiMixinKey;

                using var request = new HttpRequestMessage(
                    HttpMethod.Get, "https://api.bilibili.com/x/web-interface/nav");
                request.Headers.Referrer = new Uri("https://www.bilibili.com/");
                var cookie = UserSessionStore.Load()?.Cookie;
                if (!string.IsNullOrWhiteSpace(cookie))
                    request.Headers.TryAddWithoutValidation("Cookie", cookie);

                using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var jo = JObject.Parse(body);
                var img = jo.SelectToken("data.wbi_img.img_url")?.Value<string>() ?? "";
                var sub = jo.SelectToken("data.wbi_img.sub_url")?.Value<string>() ?? "";
                var raw = FileName(img) + FileName(sub);
                if (raw.Length < 64) return null;

                var mixin = new string(WbiMixin.Where(i => i < raw.Length).Select(i => raw[i]).ToArray());
                wbiMixinKey = mixin.Substring(0, Math.Min(32, mixin.Length));
                wbiAt = DateTime.UtcNow;
                return wbiMixinKey;
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "[直播取流] 获取 WBI 密钥失败");
                return null;
            }
            finally
            {
                wbiLock.Release();
            }
        }

        private static string FileName(string url)
        {
            var name = url.Split('/').LastOrDefault() ?? "";
            var dot = name.IndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : name;
        }

        private static string BuildQuery(IEnumerable<KeyValuePair<string, string>> query) =>
            string.Join("&", query
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));

        private static string Md5Hex(string text)
        {
            using var md5 = MD5.Create();
            return string.Concat(md5.ComputeHash(Encoding.UTF8.GetBytes(text))
                .Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }
    }

    internal sealed class LiveStreamInfo
    {
        public LiveStreamInfo(string url, int roomId, int qn)
        {
            this.Url = url;
            this.RoomId = roomId;
            this.Qn = qn;
        }

        public string Url { get; }
        public int RoomId { get; }
        public int Qn { get; }
    }

    internal sealed class LiveQuality
    {
        public LiveQuality(int qn, string desc)
        {
            this.Qn = qn;
            this.Desc = desc;
        }

        public int Qn { get; }
        public string Desc { get; }
    }
}
