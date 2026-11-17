using Gekota.WPF.Services;

#nullable enable
namespace Gekota.WPF.Pages
{
    /// <summary>
    /// AnnouncementPage（公告 / 版本更新 / 使用指南）。
    /// 更新内容统一来自 <see cref="AnnouncementContent"/>，和启动弹窗共用同一份文案。
    /// </summary>
    public partial class AnnouncementPage
    {
        public AnnouncementPage()
        {
            this.InitializeComponent();

            // 版本号统一取 GekotaVersion，避免公告页写死后忘记改
            this.VersionText.Text = "v" + GekotaVersion.FullSemVer;

            this.UpdateHeaderText.Text = "v" + AnnouncementContent.Version + " · " + AnnouncementContent.Headline;
            this.UpdateIntroText.Text = AnnouncementContent.Intro;
            this.UpdateItemsControl.ItemsSource = AnnouncementContent.Items;
            this.UpdateFooterText.Text = AnnouncementContent.Footer;
        }
    }
}
