using System;
using System.IO;
using Serilog;

#nullable enable
namespace Gekota.WPF.Services
{
    /// <summary>每个主播一个标签预设，保存在主播主文件夹下的「标签预设.txt」。</summary>
    public static class StreamerTagPresetStore
    {
        private static readonly ILogger logger = Log.ForContext(typeof(StreamerTagPresetStore));

        private const string PresetFileName = "tag.txt";
        private const string LegacyPresetFileName = "标签预设.txt";

        /// <summary>读取某主播文件夹的标签预设，无则返回 null（兼容旧的「标签预设.txt」）。</summary>
        public static string? Get(string streamerFolder)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(streamerFolder)) return null;
                var path = Path.Combine(streamerFolder, PresetFileName);
                if (!File.Exists(path))
                {
                    var legacy = Path.Combine(streamerFolder, LegacyPresetFileName);
                    if (File.Exists(legacy)) path = legacy;
                    else return null;
                }
                var tags = File.ReadAllText(path).Trim();
                return string.IsNullOrWhiteSpace(tags) ? null : tags;
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "[标签预设] 读取失败");
                return null;
            }
        }

        /// <summary>把标签预设写入主播文件夹。</summary>
        public static void Set(string streamerFolder, string tags)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(streamerFolder)) return;
                File.WriteAllText(Path.Combine(streamerFolder, PresetFileName), tags.Trim());
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "[标签预设] 保存失败");
            }
        }
    }
}
