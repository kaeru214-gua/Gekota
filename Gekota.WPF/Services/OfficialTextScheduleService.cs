using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media;
using Newtonsoft.Json.Linq;

#nullable enable
namespace Gekota.WPF.Services
{
    public sealed class OfficialTextEvent
    {
        public string RoomName { get; set; } = "";
        public DateTime Start { get; set; }
        public string Title { get; set; } = "";
        public string Source { get; set; } = "";
        public bool Cancelled { get; set; }
    }

    /// <summary>
    /// 只请求两个官方账号的动态正文，不做图片识别。
    /// </summary>
    public static class OfficialTextScheduleService
    {
        private const long ZhijiangUid = 3493085336046382;
        private const long AsoulUid = 703007996;
        private static readonly string[] Names = { "贝拉", "嘉然", "乃琳", "心宜", "思诺" };
        private static List<OfficialTextEvent>? cache;
        private static DateTime cacheAt;

        public static async Task<IReadOnlyList<OfficialTextEvent>> GetAsync()
        {
            if (cache != null && DateTime.Now - cacheAt < TimeSpan.FromMinutes(10)) return cache;

            var all = new List<OfficialTextEvent>();
            var asoul = await FetchAsync(AsoulUid, "A-SOUL_Official");
            var zhijiang = await FetchAsync(ZhijiangUid, "枝江娱乐官方");
            var asoulNames = new HashSet<string>(StringComparer.Ordinal) { "贝拉", "嘉然", "乃琳" };
            all.AddRange(asoul.Where(e => asoulNames.Contains(e.RoomName)));
            all.AddRange(zhijiang);

            var asoulDays = new HashSet<string>(
                all.Where(e => e.Source == "A-SOUL_Official")
                    .Select(e => e.RoomName + "|" + e.Start.Date.ToString("yyyy-MM-dd")),
                StringComparer.Ordinal);
            all.RemoveAll(e => e.Source != "A-SOUL_Official" &&
                asoulDays.Contains(e.RoomName + "|" + e.Start.Date.ToString("yyyy-MM-dd")));

            cache = all
                .GroupBy(e => (e.RoomName, e.Start.Date, e.Start.TimeOfDay))
                .Select(g => g.OrderByDescending(e => e.Source == "A-SOUL_Official").First())
                .OrderBy(e => e.Start)
                .ToList();
            cacheAt = DateTime.Now;
            return cache;
        }

        private static async Task<List<OfficialTextEvent>> FetchAsync(long uid, string source)
        {
            var result = new List<OfficialTextEvent>();
            try
            {
                var cookie = UserSessionStore.Load()?.Cookie ?? "";
                var url = "https://api.bilibili.com/x/polymer/web-dynamic/v1/feed/space" +
                          $"?host_mid={uid}&timezone_offset=-480&platform=web" +
                          "&features=itemOpusStyle,listOnlyfans,opusBigCover,onlyfansVote,decorationCard,onlyfansAssetsV2,ugcDelete,onlyfansQaCard";
                var body = await FetchWithCurlAsync(url, cookie, uid);
                var json = JObject.Parse(body);
                if (json.Value<int?>("code") != 0) return result;

                foreach (var item in json.SelectToken("data.items") as JArray ?? new JArray())
                {
                    var desc = item.SelectToken("modules.module_dynamic.desc") as JObject;
                    var opus = item.SelectToken("modules.module_dynamic.major.opus") as JObject;
                    var text = desc?.Value<string>("text")
                               ?? opus?.SelectToken("summary.text")?.Value<string>()
                               ?? "";
                    ParseText(text, source, result);
                }
            }
            catch { }
            return result;
        }

        private static void ParseText(string text, string source, List<OfficialTextEvent> events)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var date = Regex.Match(line, @"(?:(\d{1,2})月(\d{1,2})日|(\d{1,2})\.(\d{1,2}))");
                if (!date.Success) continue;
                var month = int.TryParse(date.Groups[1].Value, out var m) ? m :
                    int.TryParse(date.Groups[3].Value, out var m2) ? m2 : 0;
                var day = int.TryParse(date.Groups[2].Value, out var d) ? d :
                    int.TryParse(date.Groups[4].Value, out var d2) ? d2 : 0;
                if (month < 1 || month > 12 || day < 1 || day > 31) continue;

                var room = Names.FirstOrDefault(n =>
                    line.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
                if (room == null) continue;

                var titleMatch = Regex.Match(line, @"[【\[]([^】\]]+)[】\]]");
                var title = titleMatch.Success ? titleMatch.Groups[1].Value : "直播";
                var time = Regex.Match(line, @"(\d{1,2})[：:](\d{2})");
                var hour = time.Success && int.TryParse(time.Groups[1].Value, out var h) ? h : 20;
                var minute = time.Success && int.TryParse(time.Groups[2].Value, out var mi) ? mi : 0;
                var cancelled = line.Contains("取消") || line.Contains("停播");
                try
                {
                    events.Add(new OfficialTextEvent
                    {
                        RoomName = room,
                        Start = new DateTime(DateTime.Today.Year, month, day, hour, minute, 0),
                        Title = title,
                        Source = source,
                        Cancelled = cancelled,
                    });
                }
                catch { }
            }
        }

        private static async Task<string> FetchWithCurlAsync(string url, string cookie, long uid)
        {
            try
            {
                var psi = new ProcessStartInfo("curl.exe")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    Arguments = "-s -L --max-time 20 -A \"Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                                "AppleWebKit/537.36 Chrome/132.0.0.0 Safari/537.36\" " +
                                "-H \"Referer: https://space.bilibili.com/" + uid + "\" " +
                                (string.IsNullOrWhiteSpace(cookie) ? "" : "-H \"Cookie: " + cookie.Replace("\"", "") + "\" ") +
                                "\"" + url + "\"",
                };
                using var process = Process.Start(psi);
                if (process == null) return "";
                var text = await process.StandardOutput.ReadToEndAsync();
                process.WaitForExit(22000);
                return text;
            }
            catch
            {
                return "";
            }
        }
    }
}
