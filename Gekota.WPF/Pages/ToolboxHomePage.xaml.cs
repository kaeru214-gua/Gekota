using System.Windows;
using System.Windows.Input;

#nullable enable
namespace Gekota.WPF.Pages
{
    /// <summary>工具箱入口页（Launchpad 风格）。</summary>
    public partial class ToolboxHomePage
    {
        public ToolboxHomePage()
        {
            this.InitializeComponent();
        }

        private void ToolCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string tag }) return;
            e.Handled = true;
            RootPage.Current?.NavigateToPage(tag);
        }
    }
}
