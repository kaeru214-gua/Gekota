using System;
using System.IO;
using Newtonsoft.Json;
using Serilog;

#nullable enable
namespace Gekota.WPF.Services
{
    public static class BiliUploadSettings
    {
        private static readonly ILogger logger = Log.ForContext(typeof(BiliUploadSettings));
        private static bool autoUploadEnabled = false;
        private static string FilePath => Path.Combine(AppContext.BaseDirectory, "BiliUploadSettings.json");

        public static bool AutoUploadEnabled
        {
            get => autoUploadEnabled;
            set
            {
                autoUploadEnabled = value;
                try { File.WriteAllText(FilePath, JsonConvert.SerializeObject(value)); }
                catch (Exception ex) { logger.Warning(ex, "[上传] 保存设置失败"); }
            }
        }

        static BiliUploadSettings()
        {
            try
            {
                if (File.Exists(FilePath))
                    autoUploadEnabled = JsonConvert.DeserializeObject<bool>(File.ReadAllText(FilePath));
            }
            catch (Exception ex) { logger.Warning(ex, "[上传] 读取设置失败"); }
        }
    }
}
