using System;
using System.IO;
using Newtonsoft.Json;
using Serilog;

#nullable enable
namespace Gekota.WPF.Services
{
    public static class BiliUploadSettings
    {
        public const string DefaultVideoTitleTemplate = "{title}";

        private static readonly ILogger logger = Log.ForContext(typeof(BiliUploadSettings));
        private static bool autoUploadEnabled;
        private static string videoTitleTemplate = DefaultVideoTitleTemplate;
        private static string FilePath => Path.Combine(AppContext.BaseDirectory, "BiliUploadSettings.json");

        public static bool AutoUploadEnabled
        {
            get => autoUploadEnabled;
            set
            {
                autoUploadEnabled = value;
                Save();
            }
        }

        /// <summary>
        /// 自动上传视频标题模板。占位符：{title} {streamer} {date} {time} {roomId}
        /// 默认 {title}，与当前录播文件夹标题一致。
        /// </summary>
        public static string VideoTitleTemplate
        {
            get => videoTitleTemplate;
            set
            {
                videoTitleTemplate = string.IsNullOrWhiteSpace(value)
                    ? DefaultVideoTitleTemplate
                    : value.Trim();
                Save();
            }
        }

        public static string FormatVideoTitle(string title, string streamer, int roomId, DateTimeOffset time)
        {
            var template = string.IsNullOrWhiteSpace(videoTitleTemplate)
                ? DefaultVideoTitleTemplate
                : videoTitleTemplate;
            var value = template
                .Replace("{title}", title ?? "")
                .Replace("{streamer}", streamer ?? "")
                .Replace("{roomId}", roomId.ToString())
                .Replace("{date}", time.ToString("yyyyMMdd"))
                .Replace("{time}", time.ToString("HHmmss"))
                .Trim();
            if (string.IsNullOrWhiteSpace(value)) value = (title ?? "").Trim();
            return value.Length > 80 ? value.Substring(0, 80) : value;
        }

        private static void Save()
        {
            try
            {
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(new Settings
                {
                    AutoUploadEnabled = autoUploadEnabled,
                    VideoTitleTemplate = videoTitleTemplate,
                }, Formatting.Indented));
            }
            catch (Exception ex) { logger.Warning(ex, "[上传] 保存设置失败"); }
        }

        static BiliUploadSettings()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var text = File.ReadAllText(FilePath);
                if (text.TrimStart().StartsWith("{", StringComparison.Ordinal))
                {
                    var settings = JsonConvert.DeserializeObject<Settings>(text);
                    if (settings != null)
                    {
                        autoUploadEnabled = settings.AutoUploadEnabled;
                        videoTitleTemplate = string.IsNullOrWhiteSpace(settings.VideoTitleTemplate)
                            ? DefaultVideoTitleTemplate
                            : settings.VideoTitleTemplate;
                    }
                }
                else
                {
                    // 兼容旧版只保存 true/false 的格式。
                    autoUploadEnabled = JsonConvert.DeserializeObject<bool>(text);
                }
            }
            catch (Exception ex) { logger.Warning(ex, "[上传] 读取设置失败"); }
        }

        private sealed class Settings
        {
            public bool AutoUploadEnabled { get; set; }
            public string VideoTitleTemplate { get; set; } = DefaultVideoTitleTemplate;
        }
    }
}
