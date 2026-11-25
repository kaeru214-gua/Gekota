using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace Gekota.Core
{
    /// <summary>
    /// 已跳过的预约场次。格式：主播名|yyyy-MM-dd HH:mm。
    /// 只影响该房间当天的自动录制，不影响监控室观看。
    /// </summary>
    public static class ScheduleSkipPolicy
    {
        private static readonly object Gate = new object();
        private static readonly HashSet<string> Keys = new HashSet<string>(StringComparer.Ordinal);
        private static bool loaded;

        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Gekota", "schedule_skips.json");

        public static void EnsureLoaded()
        {
            lock (Gate)
            {
                if (loaded) return;
                loaded = true;
                try
                {
                    if (File.Exists(DefaultPath))
                    {
                        var values = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(DefaultPath));
                        if (values != null)
                        {
                            Keys.Clear();
                            foreach (var value in values.Where(x => !string.IsNullOrWhiteSpace(x)))
                                Keys.Add(value);
                        }
                    }
                }
                catch
                {
                    // 读取失败时按未跳过处理，避免影响录制。
                }
            }
        }

        public static void Load(IEnumerable<string> values)
        {
            lock (Gate)
            {
                loaded = true;
                Keys.Clear();
                foreach (var value in values.Where(x => !string.IsNullOrWhiteSpace(x)))
                    Keys.Add(value);
            }
        }

        public static bool IsSkipped(string roomName, DateTime when)
        {
            EnsureLoaded();
            var date = when.ToString("yyyy-MM-dd");
            lock (Gate)
            {
                foreach (var key in Keys)
                {
                    var split = key.LastIndexOf('|');
                    if (split <= 0) continue;
                    var keyRoom = key.Substring(0, split);
                    var timeText = key.Substring(split + 1);
                    if (!timeText.StartsWith(date + " ", StringComparison.Ordinal)) continue;
                    if (!RoomMatches(keyRoom, roomName)) continue;
                    if (DateTime.TryParse(timeText, out var start) &&
                        Math.Abs((when - start).TotalMinutes) <= 90)
                        return true;
                }
            }
            return false;
        }

        private static bool RoomMatches(string keyRoom, string roomName)
        {
            var actual = roomName ?? "";
            if (string.Equals(keyRoom, actual, StringComparison.OrdinalIgnoreCase)) return true;
            // 日程里存的是「乃琳」，实际房间名可能是「乃琳Queen」。
            if (actual.IndexOf(keyRoom, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (string.Equals(keyRoom, "A-SOUL", StringComparison.OrdinalIgnoreCase))
                return new[] { "贝拉", "嘉然", "乃琳" }
                    .Any(n => actual.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
            if (string.Equals(keyRoom, "闪耀舞台", StringComparison.OrdinalIgnoreCase))
                return new[] { "心宜", "思诺" }
                    .Any(n => actual.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
            return false;
        }

        public static string Key(string roomName, DateTime start) =>
            (roomName ?? "") + "|" + start.ToString("yyyy-MM-dd HH:mm");
    }
}
