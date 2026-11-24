using System;
using System.IO;
using Gekota.Core;
using Newtonsoft.Json;

#nullable enable
namespace Gekota.WPF.Services
{
    /// <summary>全局播放/录制模式。默认播放模式，录制模式才启用自动录制。</summary>
    public static class RecordingModeSettings
    {
        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Gekota", "recording_mode.json");

        public static bool AutoRecordEnabled { get; private set; }
        public static bool PlaybackModeEnabled { get; private set; } = true;

        static RecordingModeSettings()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var value = JsonConvert.DeserializeObject<RecordingModeConfig>(File.ReadAllText(ConfigPath));
                    if (value != null)
                    {
                        AutoRecordEnabled = value.AutoRecordEnabled;
                        PlaybackModeEnabled = value.PlaybackModeEnabled;
                    }
                }
            }
            catch
            {
                AutoRecordEnabled = false;
                PlaybackModeEnabled = true;
            }
        }

        public static void SetPlaybackMode(bool enabled, IRecorder? recorder)
        {
            PlaybackModeEnabled = enabled;
            if (enabled) AutoRecordEnabled = false;
            Save();
            ApplyTo(recorder);
        }

        public static void SetRecordingMode(bool enabled, IRecorder? recorder)
        {
            PlaybackModeEnabled = !enabled;
            AutoRecordEnabled = enabled;
            Save();
            ApplyTo(recorder);
        }

        private static void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(new RecordingModeConfig
                {
                    AutoRecordEnabled = AutoRecordEnabled,
                    PlaybackModeEnabled = PlaybackModeEnabled,
                }, Formatting.Indented));
            }
            catch { }
        }

        public static void ApplyTo(IRecorder? recorder)
        {
            if (recorder == null) return;
            var autoRecord = !PlaybackModeEnabled && AutoRecordEnabled;
            foreach (var room in recorder.Rooms)
                room.RoomConfig.AutoRecord = autoRecord;
            try { recorder.SaveConfig(); } catch { }
        }

        private sealed class RecordingModeConfig
        {
            public bool AutoRecordEnabled { get; set; }
            public bool PlaybackModeEnabled { get; set; } = true;
        }
    }
}
