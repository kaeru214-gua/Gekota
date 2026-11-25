using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Serilog;

#nullable enable
namespace Gekota.WPF.Services
{
    /// <summary>压制队列落盘，程序异常退出后下次启动继续压制。</summary>
    public static class CompressResumeStore
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(CompressResumeStore));
        private static readonly object Gate = new object();

        private sealed class Entry
        {
            public string Input { get; set; } = "";
            public string? Subtitle { get; set; }
        }

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Gekota", "compress_pending.json");

        public static IReadOnlyList<(string Input, string? Subtitle)> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return Array.Empty<(string, string?)>();
                var list = JsonConvert.DeserializeObject<List<Entry>>(File.ReadAllText(FilePath))
                           ?? new List<Entry>();
                // 同一录制可能同时记录了 flv 和转封装后的 mp4，优先保留 mp4，避免重复压制。
                return list.Where(x => File.Exists(x.Input))
                    .Select(x => new Entry
                    {
                        Input = PreferMp4(x.Input),
                        Subtitle = x.Subtitle,
                    })
                    .GroupBy(x => NormalizedKey(x.Input, x.Subtitle), StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderBy(x => x.Input.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                        .First())
                    .Select(x => (x.Input, x.Subtitle)).ToList();
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "[压制] 读取断点任务失败");
                return Array.Empty<(string, string?)>();
            }
        }

        private static string NormalizedKey(string input, string? subtitle)
            => Path.Combine(Path.GetDirectoryName(input) ?? "",
                   Path.GetFileNameWithoutExtension(input)).ToLowerInvariant() +
               "|" + (subtitle ?? "").ToLowerInvariant();

        private static string PreferMp4(string input)
        {
            if (!input.EndsWith(".flv", StringComparison.OrdinalIgnoreCase)) return input;
            var mp4 = Path.ChangeExtension(input, ".mp4");
            return File.Exists(mp4) ? mp4 : input;
        }

        public static void Add(string input, string? subtitle)
        {
            lock (Gate)
            {
                var list = ReadRaw();
                if (list.Any(x => string.Equals(x.Input, input, StringComparison.OrdinalIgnoreCase) &&
                                  string.Equals(x.Subtitle, subtitle, StringComparison.OrdinalIgnoreCase)))
                    return;
                list.Add(new Entry { Input = input, Subtitle = subtitle });
                WriteRaw(list);
            }
        }

        public static void Remove(string input, string? subtitle)
        {
            lock (Gate)
            {
                var list = ReadRaw();
                list.RemoveAll(x => string.Equals(x.Input, input, StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(x.Subtitle, subtitle, StringComparison.OrdinalIgnoreCase));
                WriteRaw(list);
            }
        }

        private static List<Entry> ReadRaw()
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<Entry>();
                return JsonConvert.DeserializeObject<List<Entry>>(File.ReadAllText(FilePath))
                       ?? new List<Entry>();
            }
            catch { return new List<Entry>(); }
        }

        private static void WriteRaw(List<Entry> list)
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(list, Formatting.Indented));
            }
            catch (Exception ex) { Logger.Warning(ex, "[压制] 保存断点任务失败"); }
        }
    }
}
