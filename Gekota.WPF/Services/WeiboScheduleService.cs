using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Gekota.Core;
using Newtonsoft.Json.Linq;

#nullable enable
namespace Gekota.WPF.Services
{
    public sealed class WeiboScheduleEvent
    {
        public string RoomName { get; set; } = "";
        public DateTime Start { get; set; }
        public string Title { get; set; } = "";
        public string Raw { get; set; } = "";
    }

    /// <summary>从微博公开动态中尽力提取直播日程。</summary>
    public static class WeiboScheduleService
    {
        private static readonly HttpClient http = CreateHttpClient();

        public static async Task<IReadOnlyList<WeiboScheduleEvent>> GetAsync(
            IEnumerable<IRoom> rooms, DateTime start, DateTime end)
        {
            var result = new List<WeiboScheduleEvent>();
            foreach (var room in rooms)
            {
                var name = (room.Name ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (name.IndexOf("A-SOUL", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("枝江", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("贝拉", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("嘉然", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("乃琳", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("心宜", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("思诺", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                try
                {
                    var url = "https://m.weibo.cn/api/container/getIndex?containerid=100103type%3D1%26q%3D" +
                              Uri.EscapeDataString(name) + "&page_type=searchall";
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Referrer = new Uri("https://m.weibo.cn/");
                    using var response = await http.SendAsync(request).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) continue;
                    var json = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    foreach (var blog in EnumerateBlogs(json))
                    {
                        var userName = blog.SelectToken("user.screen_name")?.Value<string>() ?? "";
                        if (!AccountMatches(name, userName)) continue;
                        var text = StripHtml(blog.Value<string>("text") ?? "");
                        if (text.Length == 0) continue;
                        foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (!TryParseStart(line, out var dt) || dt < start || dt >= end) continue;
                            var titleMatch = Regex.Match(line, @"[【\[]([^】\]]+)[】\]]");
                            result.Add(new WeiboScheduleEvent
                            {
                                RoomName = name,
                                Start = dt,
                                Title = titleMatch.Success ? titleMatch.Groups[1].Value : "直播",
                                Raw = line,
                            });
                        }
                    }
                }
                catch
                {
                    // 微博风控/无公开搜索时忽略，不影响 B 站日程。
                }
            }
            return result;
        }

        private static IEnumerable<JToken> EnumerateBlogs(JToken root)
        {
            var cards = root.SelectToken("data.cards") as JArray;
            if (cards == null) yield break;
            foreach (var card in cards)
            {
                var mblog = card.SelectToken("mblog");
                if (mblog != null) yield return mblog;
                var group = card.SelectToken("card_group") as JArray;
                if (group == null) continue;
                foreach (var item in group)
                {
                    var itemBlog = item.SelectToken("mblog");
                    if (itemBlog != null) yield return itemBlog;
                }
            }
        }

        private static bool AccountMatches(string roomName, string userName)
        {
            if (string.IsNullOrWhiteSpace(userName)) return false;
            return userName.IndexOf(roomName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   roomName.IndexOf(userName, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string StripHtml(string html)
        {
            return Regex.Replace(html ?? "", "<[^>]+>", " ")
                .Replace("&nbsp;", " ").Replace("&amp;", "&")
                .Replace("&lt;", "<").Replace("&gt;", ">")
                .Trim();
        }

        private static bool TryParseStart(string line, out DateTime start)
        {
            start = default;
            if (string.IsNullOrWhiteSpace(line)) return false;
            var date = Regex.Match(line, @"(?:(\d{1,2})月(\d{1,2})日|(\d{1,2})[-/](\d{1,2}))");
            var relative = Regex.Match(line, @"(今天|明天|后天)");
            if (!date.Success && !relative.Success) return false;
            var time = Regex.Match(line, @"(\d{1,2})[：:](\d{2})");
            var hour = time.Success && int.TryParse(time.Groups[1].Value, out var h) ? h : 20;
            var minute = time.Success && int.TryParse(time.Groups[2].Value, out var m) ? m : 0;
            if (hour > 23 || minute > 59) return false;
            try
            {
                if (relative.Success)
                {
                    var offset = relative.Groups[1].Value == "明天" ? 1 :
                        relative.Groups[1].Value == "后天" ? 2 : 0;
                    start = DateTime.Today.AddDays(offset).AddHours(hour).AddMinutes(minute);
                    return true;
                }
                var month = int.TryParse(date.Groups[1].Value, out var mm) ? mm :
                    int.TryParse(date.Groups[3].Value, out var mm2) ? mm2 : 0;
                var day = int.TryParse(date.Groups[2].Value, out var dd) ? dd :
                    int.TryParse(date.Groups[4].Value, out var dd2) ? dd2 : 0;
                if (month < 1 || month > 12 || day < 1 || day > 31) return false;
                start = new DateTime(DateTime.Today.Year, month, day, hour, minute, 0);
                if (start < DateTime.Today.AddMonths(-6)) start = start.AddYears(1);
                else if (start > DateTime.Today.AddMonths(6)) start = start.AddYears(-1);
                return true;
            }
            catch { return false; }
        }

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                UseCookies = false,
                UseProxy = false,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                         System.Net.DecompressionMethods.Deflate,
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 " +
                "Version/17.0 Mobile/15E148 Safari/604.1");
            return client;
        }
    }
}
