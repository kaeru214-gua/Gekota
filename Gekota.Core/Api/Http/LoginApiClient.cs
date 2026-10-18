using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

#nullable enable
namespace Gekota.Core.Api.Http
{
    /// <summary>
    /// Bilibili 扫码登录相关 API。
    /// </summary>
    public class LoginApiClient
    {
        private const string QrGenerateUrl = "https://passport.bilibili.com/x/passport-login/web/qrcode/generate";
        private const string QrPollUrl = "https://passport.bilibili.com/x/passport-login/web/qrcode/poll";
        private const string NavUrl = "https://api.bilibili.com/x/web-interface/nav";

        private readonly HttpClient client;

        public LoginApiClient()
        {
            var handler = new HttpClientHandler
            {
                UseCookies = false,
                UseDefaultCredentials = false,
                AllowAutoRedirect = true,
            };
            this.client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(15),
            };
            this.client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36 Edg/132.0.0.0");
            this.client.DefaultRequestHeaders.Referrer = new Uri("https://www.bilibili.com/");
            this.client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
            this.client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9");
        }

        public async Task<(string url, string qrcodeKey)> GenerateQrCodeAsync()
        {
            var resp = await this.client.GetStringAsync(QrGenerateUrl).ConfigureAwait(false);
            var jo = JObject.Parse(resp);
            if (jo["code"]?.ToObject<int>() != 0)
                throw new Exception("生成二维码失败: " + resp);
            var data = jo["data"] ?? throw new Exception("响应缺少 data");
            var url = data["url"]?.ToString() ?? throw new Exception("二维码 URL 缺失");
            var key = data["qrcode_key"]?.ToString() ?? throw new Exception("二维码 key 缺失");
            return (url, key);
        }

        public async Task<QrCodePollResult> PollQrCodeAsync(string qrcodeKey)
        {
            var url = QrPollUrl + "?qrcode_key=" + Uri.EscapeDataString(qrcodeKey);
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            var resp = await this.client.SendAsync(req).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            var cookies = new List<string>();
            if (resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var c in setCookies)
                {
                    var semi = c.IndexOf(';');
                    if (semi > 0)
                        cookies.Add(c.Substring(0, semi));
                }
            }

            var jo = JObject.Parse(body);
            var data = jo["data"];
            var code = data?["code"]?.ToObject<int>() ?? -1;
            var message = data?["message"]?.ToString() ?? "";
            var url2 = data?["url"]?.ToString();

            var cookieString = string.Join("; ", cookies);
            return new QrCodePollResult(code, message, cookieString, url2);
        }

        public async Task<UserInfoResult> GetUserInfoAsync(string cookie)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, NavUrl);
            req.Headers.Add("Cookie", cookie);
            var resp = await this.client.SendAsync(req).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var jo = JObject.Parse(body);
            if (jo["code"]?.ToObject<int>() != 0)
                throw new Exception("获取用户信息失败: " + body);
            var data = jo["data"];
            var isLogin = data?["isLogin"]?.ToObject<bool>() ?? false;
            if (!isLogin)
                throw new Exception("Cookie 无效或未登录");
            var uid = data?["mid"]?.ToObject<long>() ?? 0;
            var uname = data?["uname"]?.ToString() ?? "";
            var face = data?["face"]?.ToString() ?? "";
            return new UserInfoResult(uid, uname, face);
        }
    }

    public class QrCodePollResult
    {
        public QrCodePollResult(int code, string message, string cookie, string? redirectUrl)
        {
            this.Code = code;
            this.Message = message;
            this.Cookie = cookie;
            this.RedirectUrl = redirectUrl;
        }

        public int Code { get; }
        public string Message { get; }
        public string Cookie { get; }
        public string? RedirectUrl { get; }

        public bool IsSuccess => this.Code == 0;
        public bool IsExpired => this.Code == 86038;
        public bool IsScanned => this.Code == 86090;
        public bool IsWaiting => this.Code == 86101;
    }

    public class UserInfoResult
    {
        public UserInfoResult(long uid, string username, string avatarUrl)
        {
            this.Uid = uid;
            this.Username = username;
            this.AvatarUrl = avatarUrl;
        }

        public long Uid { get; }
        public string Username { get; }
        public string AvatarUrl { get; }
    }
}
