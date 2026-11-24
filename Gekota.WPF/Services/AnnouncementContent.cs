namespace Gekota.WPF.Services
{
    /// <summary>
    /// 版本更新公告内容。公告页和启动弹窗共用这一份，发版时只改这里。
    /// 「Version」必须和 GekotaVersion.FullSemVer 保持一致，否则启动弹窗不会弹。
    /// </summary>
    public static class AnnouncementContent
    {
        /// <summary>本次更新对应的版本号。</summary>
        public const string Version = "1.2.2";

        /// <summary>更新主题（卡片标题后半段）。</summary>
        public const string Headline = "播放、录制、日程与上传流程完善";

        /// <summary>列表前的一句说明。</summary>
        public const string Intro = "本次更新主要集中在播放、录制、日程和上传流程的完善，以及部分 UI 的优化。";

        /// <summary>更新条目（每项自动加项目符号）。</summary>
        public static readonly string[] Items =
        {
            "新增播放模式 / 录制模式。",
            "播放器新增「弹幕不重合」和「特殊弹幕」，设置会持久保存。",
            "A-SOUL / 闪耀舞台可使用 ICS 日历，其他主播从动态、预约和微博获取日程。",
            "首页直播间可编辑标签和简介，自动用于上传。",
        };

        /// <summary>入口提示。</summary>
        public const string Footer = "感谢你的使用与反馈。";
    }
}
