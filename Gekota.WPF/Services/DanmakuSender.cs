using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Serilog;

#nullable enable
namespace Gekota.WPF.Services
{
    /// <summary>
    /// 发送直播弹幕：B 站发送走 HTTP 表单，不是 WebSocket。
    /// 需要 SESSDATA（身份）和 bili_jct（CSRF）。
    /// </summary>
    internal sealed class DanmakuSender
    {
        private static readonly ILogger logger = Log.ForContext<DanmakuSender>();
        private static readonly HttpClient http;

        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36 Edg/132.0.0.0";

        public const int MaxLength = 30;

        static DanmakuSender()
        {
            var handler = new HttpClientHandler
            {
                UseCookies = false,
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            };
            http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        }

        public bool IsReady
        {
            get
            {
                var (sess, jct) = ReadCredentials();
                return !string.IsNullOrWhiteSpace(sess) && !string.IsNullOrWhiteSpace(jct);
            }
        }

        public async Task<DanmakuSendResult> SendAsync(
            int roomId, string text, CancellationToken cancellationToken = default)
        {
            text = (text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text))
                return new DanmakuSendResult(false, -1, "弹幕内容不能为空");
            if (text.Length > MaxLength)
                return new DanmakuSendResult(false, -1, $"弹幕最长 {MaxLength} 个字");

            var (sess, jct) = ReadCredentials();
            if (string.IsNullOrWhiteSpace(sess) || string.IsNullOrWhiteSpace(jct))
                return new DanmakuSendResult(false, -1, "请先在「用户」页扫码登录");

            try
            {
                var body = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["bubble"] = "0",
                    ["msg"] = text,
                    ["color"] = "16777215",
                    ["mode"] = "1",
                    ["fontsize"] = "25",
                    ["roomid"] = roomId.ToString(CultureInfo.InvariantCulture),
                    ["rnd"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                    ["csrf"] = jct,
                    ["csrf_token"] = jct,
                });

                using var request = new HttpRequestMessage(
                    HttpMethod.Post, "https://api.live.bilibili.com/msg/send")
                {
                    Content = body,
                };
                request.Headers.Referrer = new Uri("https://live.bilibili.com/" + roomId);
                request.Headers.TryAddWithoutValidation("Origin", "https://live.bilibili.com");
                request.Headers.TryAddWithoutValidation("Cookie", $"SESSDATA={sess}; bili_jct={jct}");

                using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var jo = JObject.Parse(raw);
                var code = jo.Value<int?>("code") ?? -1;
                var message = jo.Value<string>("message") ?? "";

                if (code == 0)
                {
                    logger.Information("[弹幕] 发送成功 room={Room} len={Len}", roomId, text.Length);
                    return new DanmakuSendResult(true, 0, "");
                }

                logger.Warning("[弹幕] 发送失败 room={Room} code={Code} message={Message}",
                    roomId, code, message);
                return new DanmakuSendResult(false, code, message);
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "[弹幕] 发送请求失败");
                return new DanmakuSendResult(false, -1, ex.Message);
            }
        }

        private static (string sess, string jct) ReadCredentials()
        {
            var cookie = UserSessionStore.Load()?.Cookie ?? "";
            string sess = "";
            string jct = "";

            foreach (var part in cookie.Split(';'))
            {
                var idx = part.IndexOf('=');
                if (idx <= 0) continue;
                var name = part.Substring(0, idx).Trim();
                var value = part.Substring(idx + 1).Trim();
                if (name.Equals("SESSDATA", StringComparison.OrdinalIgnoreCase))
                    sess = value;
                else if (name.Equals("bili_jct", StringComparison.OrdinalIgnoreCase))
                    jct = value;
            }

            return (sess, jct);
        }
    }

    internal sealed class DanmakuSendResult
    {
        public DanmakuSendResult(bool ok, int code, string message)
        {
            this.Ok = ok;
            this.Code = code;
            this.Message = message;
        }

        public bool Ok { get; }
        public int Code { get; }
        public string Message { get; }
    }
}
