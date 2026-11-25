using System;
using System.IO;
using Newtonsoft.Json;
using Serilog;

#nullable enable
namespace Gekota.WPF.Services
{
    /// <summary>录制素材归档设置：只保存转封装 MP4 与 ASS。</summary>
    public static class MaterialSettings
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(MaterialSettings));
        private static readonly object Gate = new object();

        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Gekota", "material_settings.json");

        public static bool Enabled { get; private set; }
        public static string SavePath { get; private set; } = "";

        static MaterialSettings()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var data = JsonConvert.DeserializeObject<Data>(File.ReadAllText(FilePath));
                if (data == null) return;
                Enabled = data.Enabled;
                SavePath = data.SavePath ?? "";
            }
            catch (Exception ex) { Logger.Warning(ex, "[素材] 读取设置失败"); }
        }

        public static void SetEnabled(bool enabled)
        {
            Enabled = enabled;
            Save();
        }

        public static void SetSavePath(string path)
        {
            SavePath = (path ?? "").Trim();
            Save();
        }

        private static void Save()
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    File.WriteAllText(FilePath, JsonConvert.SerializeObject(new Data
                    {
                        Enabled = Enabled,
                        SavePath = SavePath,
                    }, Formatting.Indented));
                }
            }
            catch (Exception ex) { Logger.Warning(ex, "[素材] 保存设置失败"); }
        }

        private sealed class Data
        {
            public bool Enabled { get; set; }
            public string SavePath { get; set; } = "";
        }
    }
}
