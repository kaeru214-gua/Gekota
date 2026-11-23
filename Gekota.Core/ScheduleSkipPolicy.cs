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
            var prefix = (roomName ?? "") + "|" + when.ToString("yyyy-MM-dd");
            lock (Gate)
                return Keys.Any(k => k.StartsWith(prefix + " ", StringComparison.Ordinal));
        }

        public static string Key(string roomName, DateTime start) =>
            (roomName ?? "") + "|" + start.ToString("yyyy-MM-dd HH:mm");
    }
}
