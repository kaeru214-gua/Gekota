namespace Gekota.WPF.Services
{
    /// <summary>
    /// 版本更新公告内容。公告页和启动弹窗共用这一份，发版时只改这里。
    /// 「Version」必须和 GekotaVersion.FullSemVer 保持一致，否则启动弹窗不会弹。
    /// </summary>
    public static class AnnouncementContent
    {
        /// <summary>本次更新对应的版本号。</summary>
        public const string Version = "1.2.3";

        /// <summary>更新主题（卡片标题后半段）。</summary>
        public const string Headline = "修复已知问题并优化 UI";

        /// <summary>列表前的一句说明。</summary>
        public const string Intro = "本次更新：";

        /// <summary>更新条目（每项自动加项目符号）。</summary>
        public static readonly string[] Items =
        {
            "修复了部分已知错误。",
            "优化了 UI 表现。",
        };

        /// <summary>入口提示。</summary>
        public const string Footer = "感谢你的使用与反馈。";
    }
}
