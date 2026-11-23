using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

#nullable enable
namespace Gekota.WPF.Services
{
    /// <summary>
    /// 只绑定 127.0.0.1 的极简播放服务：
    /// - 本地播放器静态页；
    /// - /stream 代理 FLV（带 Referer，避免 CDN 403）；
    /// - /api/send 发送弹幕（SESSDATA + bili_jct）。
    /// 用 TcpListener 而不是 HttpListener，避免非管理员下的 URLACL 问题。
    /// </summary>
    internal sealed class LivePlaybackServer : IDisposable
    {
        private static readonly ILogger logger = Log.ForContext<LivePlaybackServer>();
        private static readonly HttpClient streamHttp;
        private static readonly string WebRoot;

        private readonly LiveStreamProvider provider;
        private readonly DanmakuSender sender;
        private readonly CancellationTokenSource cts = new();
        private TcpListener? listener;
        private Task? acceptTask;
        private readonly object sendLock = new();
        private readonly object coverLock = new();
        private readonly Dictionary<string, byte[]> coverCache = new(StringComparer.OrdinalIgnoreCase);
        private DateTime lastSendAt = DateTime.MinValue;

        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36 Edg/132.0.0.0";

        static LivePlaybackServer()
        {
            var handler = new HttpClientHandler
            {
                UseCookies = false,
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            };
            streamHttp = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            streamHttp.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            streamHttp.DefaultRequestHeaders.Accept.ParseAdd("*/*");

            WebRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "LivePlayer");
        }

        public LivePlaybackServer(LiveStreamProvider provider, DanmakuSender sender)
        {
            this.provider = provider;
            this.sender = sender;
        }

        public int Port { get; private set; }
        public int CurrentRoomId { get; set; }
        public int CurrentQn { get; set; }
        public string CurrentTitle { get; set; } = "";
        public string MonitorBackgroundPath { get; set; } = "";
        public IReadOnlyList<MonitorRoomInfo> MonitorRooms { get; set; } = Array.Empty<MonitorRoomInfo>();

        public string BaseUrl => $"http://127.0.0.1:{this.Port}";

        public void Start()
        {
            if (this.listener != null) return;
            this.listener = new TcpListener(IPAddress.Loopback, 0);
            this.listener.Start();
            this.Port = ((IPEndPoint)this.listener.LocalEndpoint).Port;
            this.acceptTask = Task.Run(this.AcceptLoopAsync);
            logger.Information("[直播播放] 本机服务已启动 {Url}", this.BaseUrl);
        }

        public void Stop()
        {
            try { this.cts.Cancel(); } catch { }
            try { this.listener?.Stop(); } catch { }
            this.listener = null;
            this.acceptTask = null;
        }

        public void Dispose()
        {
            this.Stop();
            this.cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!this.cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await this.listener!.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }
                catch (Exception ex)
                {
                    logger.Debug(ex, "[直播播放] accept 失败");
                    continue;
                }

                _ = Task.Run(() => this.HandleClientAsync(client));
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                try
                {
                    var request = await ReadRequestAsync(stream).ConfigureAwait(false);
                    if (request == null) return;

                    var path = request.Path;
                    if (path.Equals("/api/info", StringComparison.OrdinalIgnoreCase))
                    {
                        IReadOnlyList<LiveQuality> qualities = Array.Empty<LiveQuality>();
                        try
                        {
                            qualities = await this.provider.GetQualitiesAsync(this.CurrentRoomId, this.cts.Token)
                                .ConfigureAwait(false);
                        }
                        catch { }

                        await WriteJsonAsync(stream, 200, new
                        {
                            room = this.CurrentRoomId,
                            title = this.CurrentTitle,
                            qn = this.CurrentQn,
                            qualities = qualities.Select(q => new { qn = q.Qn, desc = q.Desc }).ToArray(),
                            logged = this.sender.IsReady,
                            canSend = this.sender.IsReady,
                        }).ConfigureAwait(false);
                        return;
                    }

                    if (path.Equals("/api/monitor/rooms", StringComparison.OrdinalIgnoreCase))
                    {
                        var rooms = this.MonitorRooms ?? Array.Empty<MonitorRoomInfo>();
                        await WriteJsonAsync(stream, 200, new
                        {
                            rooms = rooms.Select(r => new
                            {
                                room = r.RoomId,
                                name = r.Name,
                                cover = r.CoverUrl,
                                live = r.Live,
                            }).ToArray(),
                        }).ConfigureAwait(false);
                        return;
                    }

                    if (path.Equals("/monitor-bg", StringComparison.OrdinalIgnoreCase))
                    {
                        await this.ServeMonitorBackgroundAsync(stream).ConfigureAwait(false);
                        return;
                    }

                    if (path.Equals("/cover", StringComparison.OrdinalIgnoreCase))
                    {
                        await this.ProxyCoverAsync(stream, request.Query).ConfigureAwait(false);
                        return;
                    }

                    if (path.Equals("/api/send", StringComparison.OrdinalIgnoreCase))
                    {
                        await this.HandleSendAsync(stream, request).ConfigureAwait(false);
                        return;
                    }

                    if (path.Equals("/stream", StringComparison.OrdinalIgnoreCase))
                    {
                        await this.ProxyStreamAsync(stream, request.Query).ConfigureAwait(false);
                        return;
                    }

                    await this.ServeStaticAsync(stream, path).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.Debug(ex, "[直播播放] 请求处理失败");
                }
            }
        }

        private async Task HandleSendAsync(NetworkStream stream, HttpRequest request)
        {
            if (!request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(stream, 405, new { ok = false, message = "Method Not Allowed" }).ConfigureAwait(false);
                return;
            }

            string text = "";
            try
            {
                var jo = JObject.Parse(Encoding.UTF8.GetString(request.Body));
                text = jo.Value<string>("text") ?? "";
            }
            catch { }

            if (this.CurrentRoomId <= 0)
            {
                await WriteJsonAsync(stream, 400, new { ok = false, message = "没有选中房间" }).ConfigureAwait(false);
                return;
            }

            bool tooFast;
            lock (this.sendLock)
            {
                tooFast = DateTime.UtcNow - this.lastSendAt < TimeSpan.FromSeconds(2);
                if (!tooFast) this.lastSendAt = DateTime.UtcNow;
            }

            if (tooFast)
            {
                await WriteJsonAsync(stream, 429, new
                {
                    ok = false,
                    message = "发送太快了，请等 2 秒再试",
                }).ConfigureAwait(false);
                return;
            }

            var result = await this.sender.SendAsync(this.CurrentRoomId, text, this.cts.Token).ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, new
            {
                ok = result.Ok,
                code = result.Code,
                message = result.Message,
            }).ConfigureAwait(false);
        }

        private async Task ProxyStreamAsync(NetworkStream client, string query)
        {
            var qn = ParseIntQuery(query, "qn");
            var roomId = ParseIntQuery(query, "room");
            if (roomId <= 0) roomId = this.CurrentRoomId;

            LiveStreamInfo info;
            try
            {
                info = await this.provider.GetAsync(roomId, qn > 0 ? qn : (int?)null, this.cts.Token)
                    .ConfigureAwait(false);
                if (roomId == this.CurrentRoomId) this.CurrentQn = info.Qn;
            }
            catch (Exception ex)
            {
                await WriteJsonAsync(client, 502, new
                {
                    ok = false,
                    message = ex.Message,
                }).ConfigureAwait(false);
                return;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, info.Url);
            request.Headers.Referrer = new Uri("https://live.bilibili.com/" + roomId);
            request.Headers.TryAddWithoutValidation("Origin", "https://live.bilibili.com");
            request.Headers.TryAddWithoutValidation("Accept", "*/*");

            using var upstream = await streamHttp.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, this.cts.Token).ConfigureAwait(false);
            if (!upstream.IsSuccessStatusCode)
            {
                logger.Warning("[直播播放] 上游取流失败 {Status} host={Host}",
                    (int)upstream.StatusCode, new Uri(info.Url).Host);
                await WriteJsonAsync(client, 502, new
                {
                    ok = false,
                    message = "直播源 HTTP " + (int)upstream.StatusCode,
                }).ConfigureAwait(false);
                return;
            }

            var head = "HTTP/1.1 200 OK\r\n" +
                       "Content-Type: video/x-flv\r\n" +
                       "Cache-Control: no-store\r\n" +
                       "Access-Control-Allow-Origin: *\r\n" +
                       "Connection: close\r\n\r\n";
            var headBytes = Encoding.ASCII.GetBytes(head);
            await client.WriteAsync(headBytes, 0, headBytes.Length, this.cts.Token).ConfigureAwait(false);

            using var upstreamStream = await upstream.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            try
            {
                while (!this.cts.IsCancellationRequested)
                {
                    var read = await upstreamStream.ReadAsync(buffer, 0, buffer.Length, this.cts.Token)
                        .ConfigureAwait(false);
                    if (read <= 0) break;
                    await client.WriteAsync(buffer, 0, read, this.cts.Token).ConfigureAwait(false);
                    await client.FlushAsync(this.cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
        }

        private async Task ServeMonitorBackgroundAsync(NetworkStream client)
        {
            try
            {
                var path = this.MonitorBackgroundPath;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    await WriteJsonAsync(client, 404, new { ok = false }).ConfigureAwait(false);
                    return;
                }

                var bytes = File.ReadAllBytes(path);
                var mime = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png"
                    : path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ? "image/webp"
                    : path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ? "image/gif"
                    : "image/jpeg";
                var head = "HTTP/1.1 200 OK\r\n" +
                           "Content-Type: " + mime + "\r\n" +
                           "Content-Length: " + bytes.Length + "\r\n" +
                           "Cache-Control: public, max-age=3600\r\n" +
                           "Connection: close\r\n\r\n";
                var headBytes = Encoding.ASCII.GetBytes(head);
                await client.WriteAsync(headBytes, 0, headBytes.Length, this.cts.Token).ConfigureAwait(false);
                await client.WriteAsync(bytes, 0, bytes.Length, this.cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "[直播播放] 监控室背景读取失败");
                await WriteJsonAsync(client, 404, new { ok = false }).ConfigureAwait(false);
            }
        }

        private async Task ProxyCoverAsync(NetworkStream client, string query)
        {
            var raw = ParseStringQuery(query, "url");
            if (string.IsNullOrWhiteSpace(raw) ||
                !Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                await WriteJsonAsync(client, 400, new { ok = false, message = "无效封面" }).ConfigureAwait(false);
                return;
            }

            if (!uri.Host.EndsWith("hdslb.com", StringComparison.OrdinalIgnoreCase) &&
                !uri.Host.EndsWith("biliimg.com", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(client, 400, new { ok = false, message = "封面域名不受支持" }).ConfigureAwait(false);
                return;
            }

            byte[]? bytes;
            lock (this.coverLock) this.coverCache.TryGetValue(uri.AbsoluteUri, out bytes);
            if (bytes == null)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    request.Headers.Referrer = new Uri("https://live.bilibili.com/");
                    request.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/*,*/*;q=0.8");
                    using var response = await streamHttp.SendAsync(
                        request, HttpCompletionOption.ResponseContentRead, this.cts.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        await WriteJsonAsync(client, 502, new { ok = false, message = "封面 HTTP " + (int)response.StatusCode })
                            .ConfigureAwait(false);
                        return;
                    }
                    bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (bytes.Length > 5 * 1024 * 1024)
                    {
                        await WriteJsonAsync(client, 502, new { ok = false, message = "封面过大" }).ConfigureAwait(false);
                        return;
                    }
                    lock (this.coverLock)
                    {
                        if (this.coverCache.Count > 200) this.coverCache.Clear();
                        this.coverCache[uri.AbsoluteUri] = bytes;
                    }
                }
                catch (Exception ex)
                {
                    logger.Debug(ex, "[直播播放] 封面代理失败");
                    await WriteJsonAsync(client, 502, new { ok = false, message = "封面加载失败" }).ConfigureAwait(false);
                    return;
                }
            }

            var mime = uri.AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
            var head = "HTTP/1.1 200 OK\r\n" +
                       "Content-Type: " + mime + "\r\n" +
                       "Content-Length: " + bytes.Length + "\r\n" +
                       "Cache-Control: public, max-age=3600\r\n" +
                       "Connection: close\r\n\r\n";
            var headBytes = Encoding.ASCII.GetBytes(head);
            await client.WriteAsync(headBytes, 0, headBytes.Length, this.cts.Token).ConfigureAwait(false);
            await client.WriteAsync(bytes, 0, bytes.Length, this.cts.Token).ConfigureAwait(false);
        }

        private async Task ServeStaticAsync(NetworkStream stream, string path)
        {
            if (path == "/") path = "/index.html";
            var name = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(name) || name.Contains(".."))
            {
                await WriteJsonAsync(stream, 404, new { error = "not found" }).ConfigureAwait(false);
                return;
            }

            var file = Path.Combine(WebRoot, name);
            if (!File.Exists(file))
            {
                await WriteJsonAsync(stream, 404, new { error = "not found" }).ConfigureAwait(false);
                return;
            }

            var bytes = File.ReadAllBytes(file);
            var mime = name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ? "text/html; charset=utf-8"
                : name.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ? "text/css; charset=utf-8"
                : name.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? "application/javascript; charset=utf-8"
                : name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png"
                : "application/octet-stream";

            var head = "HTTP/1.1 200 OK\r\n" +
                       "Content-Type: " + mime + "\r\n" +
                       "Content-Length: " + bytes.Length + "\r\n" +
                       "Cache-Control: no-store\r\n" +
                       "Connection: close\r\n\r\n";
            var headBytes = Encoding.ASCII.GetBytes(head);
            await stream.WriteAsync(headBytes, 0, headBytes.Length).ConfigureAwait(false);
            await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        }

        private static async Task WriteJsonAsync(NetworkStream stream, int status, object body)
        {
            var json = JsonConvert.SerializeObject(body);
            var bytes = Encoding.UTF8.GetBytes(json);
            var reason = status == 200 ? "OK" : status == 400 ? "Bad Request" : status == 405 ? "Method Not Allowed" : status == 429 ? "Too Many Requests" : "Bad Gateway";
            var head = $"HTTP/1.1 {status} {reason}\r\n" +
                       "Content-Type: application/json; charset=utf-8\r\n" +
                       $"Content-Length: {bytes.Length}\r\n" +
                       "Cache-Control: no-store\r\n" +
                       "Connection: close\r\n\r\n";
            var headBytes = Encoding.ASCII.GetBytes(head);
            await stream.WriteAsync(headBytes, 0, headBytes.Length).ConfigureAwait(false);
            await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        }

        private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream)
        {
            var buffer = new List<byte>();
            var temp = new byte[4096];
            var headerEnd = -1;
            while (buffer.Count < 64 * 1024)
            {
                var read = await stream.ReadAsync(temp, 0, temp.Length).ConfigureAwait(false);
                if (read <= 0) return null;
                for (var i = 0; i < read; i++) buffer.Add(temp[i]);
                var text = Encoding.ASCII.GetString(buffer.ToArray());
                headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd >= 0) break;
            }

            if (headerEnd < 0) return null;
            var headerText = Encoding.ASCII.GetString(buffer.Take(headerEnd).ToArray());
            var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return null;
            var first = lines[0].Split(' ');
            if (first.Length < 2) return null;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var idx = line.IndexOf(':');
                if (idx <= 0) continue;
                headers[line.Substring(0, idx).Trim()] = line.Substring(idx + 1).Trim();
            }

            var contentLength = headers.TryGetValue("Content-Length", out var cl) &&
                                int.TryParse(cl, out var n) ? n : 0;
            var body = new byte[contentLength];
            var already = buffer.Count - headerEnd - 4;
            var copied = Math.Min(already, contentLength);
            if (copied > 0)
                Array.Copy(buffer.ToArray(), headerEnd + 4, body, 0, copied);

            var remaining = contentLength - copied;
            var offset = copied;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(body, offset, remaining).ConfigureAwait(false);
                if (read <= 0) break;
                offset += read;
                remaining -= read;
            }

            return new HttpRequest
            {
                Method = first[0],
                Path = first[1].Split('?')[0],
                Query = first[1].Contains('?') ? first[1].Substring(first[1].IndexOf('?') + 1) : "",
                Body = body,
            };
        }

        private static int ParseIntQuery(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return 0;
            foreach (var part in query.Split('&'))
            {
                var idx = part.IndexOf('=');
                if (idx <= 0) continue;
                if (!part.Substring(0, idx).Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                return int.TryParse(part.Substring(idx + 1), out var value) ? value : 0;
            }
            return 0;
        }

        private static string ParseStringQuery(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return "";
            foreach (var part in query.Split('&'))
            {
                var idx = part.IndexOf('=');
                if (idx <= 0) continue;
                if (!part.Substring(0, idx).Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                return Uri.UnescapeDataString(part.Substring(idx + 1).Replace("+", " "));
            }
            return "";
        }

        private sealed class HttpRequest
        {
            public string Method { get; set; } = "";
            public string Path { get; set; } = "";
            public string Query { get; set; } = "";
            public byte[] Body { get; set; } = Array.Empty<byte>();
        }
    }

    internal sealed class MonitorRoomInfo
    {
        public MonitorRoomInfo(int roomId, string name, string coverUrl, bool live)
        {
            this.RoomId = roomId;
            this.Name = name;
            this.CoverUrl = coverUrl;
            this.Live = live;
        }

        public int RoomId { get; }
        public string Name { get; }
        public string CoverUrl { get; }
        public bool Live { get; }
    }
}
