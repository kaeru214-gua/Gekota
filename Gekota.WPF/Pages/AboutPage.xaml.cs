using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Gekota.WPF.Services;

namespace Gekota.WPF.Pages
{
    public partial class AboutPage
    {
        private GitHubReleaseInfo? _latest;

        public AboutPage()
        {
            this.InitializeComponent();
            this.CopyrightTextBlock.Text = $" © {DateTime.Now.Year} Gekota";
        }

        private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            this.UpdateStatusText.Text = "正在检查更新...";
            var info = await GitHubReleaseService.GetLatestAsync();
            if (info == null)
            {
                this.UpdateStatusText.Text = "检查失败（网络或仓库不可用）";
                return;
            }
            this._latest = info;

            if (!GitHubReleaseService.IsNewer(info.TagName, GekotaVersion.FullSemVer))
            {
                this.UpdateStatusText.Text = "已是最新版本（" + GekotaVersion.FullSemVer + "）";
                return;
            }

            var body = string.IsNullOrWhiteSpace(info.Body) ? "" : info.Body;
            var r = MessageBox.Show(
                "发现新版本 " + info.TagName + "\n\n" + body + "\n\n是否立即下载并更新？",
                "检查更新", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (r != MessageBoxResult.Yes)
            {
                this.UpdateStatusText.Text = "发现新版本 " + info.TagName + "，点击查看更新内容";
                return;
            }

            if (string.IsNullOrWhiteSpace(info.DownloadUrl))
            {
                this.UpdateStatusText.Text = "没有可下载的安装包，请手动到 Releases 更新";
                return;
            }

            this.UpdateStatusText.Text = "正在下载更新，请稍候...";
            var ok = await SelfUpdater.DownloadAndApplyAsync(info.DownloadUrl);
            if (!ok)
                this.UpdateStatusText.Text = "更新失败，请稍后重试或手动更新";
            // 成功时 SelfUpdater 已调用 Application.Shutdown，程序会直接退出
        }

        private void UpdateStatusText_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (this._latest == null || string.IsNullOrWhiteSpace(this._latest.HtmlUrl)) return;
            try { Process.Start(new ProcessStartInfo(this._latest.HtmlUrl) { UseShellExecute = true }); }
            catch { }
        }
    }
}
