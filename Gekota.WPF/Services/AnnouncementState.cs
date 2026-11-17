using System;
using System.IO;
using Newtonsoft.Json;
using Serilog;

#nullable enable
namespace Gekota.WPF.Services
{
    /// <summary>
    /// 记录「版本更新弹窗」已经给哪个版本弹过，保证每个版本只弹一次。
    /// 状态存放在程序目录下的 AnnouncementState.json。
    /// </summary>
    public static class AnnouncementState
    {
        private static readonly ILogger logger = Log.ForContext(typeof(AnnouncementState));
        private static readonly object sync = new object();

        private sealed class State
        {
            public string LastShownVersion { get; set; } = string.Empty;
        }

        private static string ConfigPath =>
            Path.Combine(AppContext.BaseDirectory, "AnnouncementState.json");

        /// <summary>当前版本是否还没弹过（且公告内容确实是这个版本的）。</summary>
        public static bool ShouldShowCurrentVersion()
        {
            try
            {
                var current = GekotaVersion.FullSemVer;
                if (!string.Equals(AnnouncementContent.Version, current, StringComparison.Ordinal))
                {
                    logger.Warning("[公告] 公告内容版本 {Content} 与程序版本 {Current} 不一致，不弹更新提示；发版时记得同步 AnnouncementContent.Version",
                        AnnouncementContent.Version, current);
                    return false;
                }

                return !string.Equals(Load().LastShownVersion, current, StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "[公告] 读取弹窗记录失败");
                return false;
            }
        }

        /// <summary>把当前版本记为「已弹过」。</summary>
        public static void MarkCurrentVersionShown()
        {
            try
            {
                lock (sync)
                {
                    var state = new State { LastShownVersion = GekotaVersion.FullSemVer };
                    File.WriteAllText(ConfigPath,
                        JsonConvert.SerializeObject(state, Formatting.Indented));
                }
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "[公告] 写入弹窗记录失败");
            }
        }

        private static State Load()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return new State();
                return JsonConvert.DeserializeObject<State>(File.ReadAllText(ConfigPath)) ?? new State();
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "[公告] 解析弹窗记录失败，按未弹过处理");
                return new State();
            }
        }
    }
}
