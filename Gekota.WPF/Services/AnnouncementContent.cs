namespace Gekota.WPF.Services
{
    /// <summary>
    /// 版本更新公告内容。公告页和启动弹窗共用这一份，发版时只改这里。
    /// 「Version」必须和 GekotaVersion.FullSemVer 保持一致，否则启动弹窗不会弹。
    /// </summary>
    public static class AnnouncementContent
    {
        /// <summary>本次更新对应的版本号。</summary>
        public const string Version = "1.1.0";

        /// <summary>更新主题（卡片标题后半段）。</summary>
        public const string Headline = "弹幕转换";

        /// <summary>列表前的一句说明。</summary>
        public const string Intro = "特殊弹幕（SC / 礼物 / 上舰）现在可以单独调大小和透明度了：";

        /// <summary>更新条目（每项自动加项目符号）。</summary>
        public static readonly string[] Items =
        {
            "新增「小 / 中 / 大」三档框大小：框宽、框高、字号一起缩放，小是中的 2/3，大是中的 4/3，普通弹幕的字号不受影响。",
            "新增透明度开关和滑块：数值越大越透明（0% 完全不透明），不想挡住画面时可以把它调淡。",
            "特殊弹幕固定显示在画面左下角：横屏、竖屏各按自己的分辨率换算，不再挤在画面中间。",
            "修复：竖屏录播里 SC 框位置偏移、越叠越高的问题。",
        };

        /// <summary>入口提示。</summary>
        public const string Footer =
            "入口：工具箱 → 弹幕转换 → 特殊弹幕（SC / 礼物 / 上舰）。修改后会应用到之后转换的弹幕，已经转好的 ASS 需要重新转换。";
    }
}
