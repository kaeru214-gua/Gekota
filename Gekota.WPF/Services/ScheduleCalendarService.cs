using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Gekota.Core;

#nullable enable
namespace Gekota.WPF.Services
{
    public sealed class CalendarScheduleEvent
    {
        public string RoomName { get; set; } = "";
        public DateTime Start { get; set; }
        public string Title { get; set; } = "";
        public string Raw { get; set; } = "";
    }

    /// <summary>导入 A-SOUL / 闪耀舞台日历（ICS）。</summary>
    public static class ScheduleCalendarService
    {
        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Gekota", "schedule_calendar.txt");
        private static readonly HttpClient http = CreateHttpClient();

        public static string Url
        {
            get
            {
                try { return File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath).Trim() : ""; }
                catch { return ""; }
            }
        }

        public static void SaveUrl(string url)
        {
            try
            {
                var dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(ConfigPath, (url ?? "").Trim());
            }
            catch { }
        }

        public static async Task<IReadOnlyList<CalendarScheduleEvent>> GetAsync(DateTime start, DateTime end)
        {
            var url = Url;
            if (string.IsNullOrWhiteSpace(url)) return Array.Empty<CalendarScheduleEvent>();
            if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url.Substring("webcal://".Length);

            try
            {
                using var response = await http.GetAsync(url).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return Parse(text, start, end);
            }
            catch
            {
                return Array.Empty<CalendarScheduleEvent>();
            }
        }

        private static List<CalendarScheduleEvent> Parse(string ics, DateTime start, DateTime end)
        {
            var result = new List<CalendarScheduleEvent>();
            var lines = Unfold(ics);
            CalendarScheduleEvent? current = null;
            string summary = "", description = "", location = "";

            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd('\r', '\n');
                if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
                {
                    current = new CalendarScheduleEvent();
                    summary = description = location = "";
                    continue;
                }
                if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
                {
                    if (current != null && current.Start >= start && current.Start < end)
                    {
                        current.Raw = summary + "\n" + description + "\n" + location;
                        current.Title = string.IsNullOrWhiteSpace(current.Title) ? "直播" : current.Title;
                        result.Add(current);
                    }
                    current = null;
                    continue;
                }
                if (current == null) continue;

                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var keyPart = line.Substring(0, colon);
                var value = Unescape(line.Substring(colon + 1));
                var key = keyPart.Split(';')[0].ToUpperInvariant();

                switch (key)
                {
                    case "DTSTART":
                        if (TryParseDate(keyPart, value, out var dt)) current.Start = dt;
                        break;
                    case "SUMMARY":
                        summary = value;
                        current.Title = value;
                        break;
                    case "DESCRIPTION":
                        description = value;
                        break;
                    case "LOCATION":
                        location = value;
                        break;
                }
            }

            foreach (var item in result)
            {
                var text = item.Title + "\n" + item.Raw;
                var room = ResolveRoom(text);
                if (string.IsNullOrWhiteSpace(room)) continue;
                item.RoomName = room;
                item.Raw = text;
                item.Title = CleanTitle(item.Title, room);
            }
            return result.Where(x => !string.IsNullOrWhiteSpace(x.RoomName)).ToList();
        }

        private static string ResolveRoom(string text)
        {
            var asoulMembers = new[] { "贝拉", "嘉然", "乃琳" };
            var stageMembers = new[] { "心宜", "思诺" };
            if (asoulMembers.Count(n => text.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) >= 2)
                return "A-SOUL";
            if (stageMembers.Count(n => text.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) >= 2)
                return "闪耀舞台";
            var single = asoulMembers.Concat(stageMembers)
                .FirstOrDefault(n => text.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!string.IsNullOrEmpty(single)) return single;
            if (text.IndexOf("A-SOUL", StringComparison.OrdinalIgnoreCase) >= 0) return "A-SOUL";
            if (text.IndexOf("闪耀舞台", StringComparison.OrdinalIgnoreCase) >= 0) return "闪耀舞台";
            return "";
        }

        private static string CleanTitle(string title, string room)
        {
            var value = (title ?? "").Trim();
            if (string.IsNullOrWhiteSpace(value)) return "直播";
            return value;
        }

        private static bool TryParseDate(string keyPart, string value, out DateTime result)
        {
            result = default;
            var formats = new[]
            {
                "yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm",
                "yyyyMMdd"
            };
            if (value.EndsWith("Z", StringComparison.OrdinalIgnoreCase) &&
                DateTime.TryParseExact(value, formats[0], System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal, out var utc))
            {
                result = utc.ToLocalTime();
                return true;
            }
            if (DateTime.TryParseExact(value, formats, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out result))
                return true;
            if (DateTime.TryParse(value, out result)) return true;
            return false;
        }

        private static List<string> Unfold(string ics)
        {
            var result = new List<string>();
            foreach (var line in ics.Replace("\r\n", "\n").Split('\n'))
            {
                if ((line.StartsWith(" ", StringComparison.Ordinal) || line.StartsWith("\t", StringComparison.Ordinal))
                    && result.Count > 0)
                    result[result.Count - 1] += line.Substring(1);
                else
                    result.Add(line);
            }
            return result;
        }

        private static string Unescape(string value)
        {
            return value.Replace("\\n", "\n").Replace("\\N", "\n")
                .Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");
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
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/132.0.0.0 Safari/537.36");
            return client;
        }
    }
}
