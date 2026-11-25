using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Serilog;

#nullable enable
namespace Gekota.WPF.Services
{
    /// <summary>已成功投稿的本地录制，避免启动扫描时重复上传。</summary>
    public static class UploadHistoryStore
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(UploadHistoryStore));
        private static readonly object Gate = new object();

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Gekota", "uploaded_history.json");

        public static string Key(long roomId, string directory, string baseName)
            => roomId + "|" + Path.GetFullPath(directory).ToLowerInvariant() + "|" + baseName.ToLowerInvariant();

        public static bool Contains(long roomId, string directory, string baseName)
        {
            try
            {
                lock (Gate)
                    return LoadRaw().Contains(Key(roomId, directory, baseName), StringComparer.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static void Mark(long roomId, string directory, string baseName)
        {
            try
            {
                lock (Gate)
                {
                    var list = LoadRaw();
                    var key = Key(roomId, directory, baseName);
                    if (!list.Contains(key, StringComparer.OrdinalIgnoreCase))
                    {
                        list.Add(key);
                        var dir = Path.GetDirectoryName(FilePath);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        File.WriteAllText(FilePath, JsonConvert.SerializeObject(list, Formatting.Indented));
                    }
                }
            }
            catch (Exception ex) { Logger.Warning(ex, "[上传] 保存上传历史失败"); }
        }

        private static List<string> LoadRaw()
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<string>();
                return JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(FilePath))
                       ?? new List<string>();
            }
            catch { return new List<string>(); }
        }
    }
}
