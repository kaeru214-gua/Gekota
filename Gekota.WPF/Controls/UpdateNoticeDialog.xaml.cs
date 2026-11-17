using Gekota.WPF.Services;

#nullable enable
namespace Gekota.WPF.Controls
{
    /// <summary>新版本启动时弹出一次的更新提示（内容来自 <see cref="AnnouncementContent"/>）。</summary>
    public partial class UpdateNoticeDialog
    {
        public UpdateNoticeDialog()
        {
            this.InitializeComponent();

            this.HeaderText.Text = "v" + AnnouncementContent.Version + " · " + AnnouncementContent.Headline;
            this.IntroText.Text = AnnouncementContent.Intro;
            this.ItemsControl.ItemsSource = AnnouncementContent.Items;
            this.FooterText.Text = AnnouncementContent.Footer;
        }
    }
}
