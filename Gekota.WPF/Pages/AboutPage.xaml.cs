using System;

namespace Gekota.WPF.Pages
{
    /// <summary>
    /// Interaction logic for AboutPage.xaml
    /// </summary>
    public partial class AboutPage
    {
        public AboutPage()
        {
            this.InitializeComponent();

            // 版权年份动态显示为当前年份
            this.CopyrightTextBlock.Text = $" © {DateTime.Now.Year} Gekota";
        }
    }
}
