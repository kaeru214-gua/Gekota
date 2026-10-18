using System;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

#nullable enable
namespace Gekota.WPF.Controls
{
    public partial class CaptchaDialog : Window
    {
        public CaptchaDialog()
        {
            this.InitializeComponent();
        }

        public string? ResultCookie { get; private set; }

        public async Task<bool> ShowCaptchaAsync(string url)
        {
            try
            {
                await this.WebView.EnsureCoreWebView2Async();
                this.WebView.CoreWebView2.Navigate(url);
            }
            catch (Exception ex)
            {
                MessageBox.Show("验证码窗口初始化失败（可能缺少 WebView2 运行时）。\n" + ex.Message,
                    "验证码", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            return this.ShowDialog() == true;
        }

        private async void Done_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var cm = this.WebView.CoreWebView2.CookieManager;
                var cookies = await cm.GetCookiesAsync("https://www.bilibili.com");
                var sb = new StringBuilder();
                foreach (var c in cookies)
                {
                    if (sb.Length > 0) sb.Append("; ");
                    sb.Append(c.Name).Append("=").Append(c.Value);
                }
                this.ResultCookie = sb.ToString();
            }
            catch { }

            this.DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
        }
    }
}
