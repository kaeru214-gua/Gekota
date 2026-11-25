using System;
using System.IO;
using System.Linq;
using Serilog;

#nullable enable
namespace Gekota.WPF.Services
{
    /// <summary>把转封装 MP4 和 ASS 复制到「素材路径\主播-场次」。</summary>
    public static class MaterialArchiveService
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(MaterialArchiveService));

        public static void Save(string flvPath, string roomName)
        {
            if (!MaterialSettings.Enabled || string.IsNullOrWhiteSpace(MaterialSettings.SavePath))
                return;

            var mp4 = Path.ChangeExtension(flvPath, ".mp4");
            var ass = Path.ChangeExtension(flvPath, ".ass");
            if (!File.Exists(mp4)) return;

            try
            {
                var title = Path.GetFileName(Path.GetDirectoryName(flvPath) ?? "");
                var name = Sanitize((roomName ?? "未知主播") + "-" + title);
                var destination = Path.Combine(MaterialSettings.SavePath, name);
                Directory.CreateDirectory(destination);
                File.Copy(mp4, Path.Combine(destination, Path.GetFileName(mp4)), true);
                if (File.Exists(ass))
                    File.Copy(ass, Path.Combine(destination, Path.GetFileName(ass)), true);
                Logger.Information("[素材] 已保存 MP4/ASS：{Path}", destination);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "[素材] 保存失败：{Flv}", flvPath);
            }
        }

        private static string Sanitize(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string((value ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
            return string.IsNullOrWhiteSpace(clean) ? "未命名场次" : clean;
        }
    }
}
