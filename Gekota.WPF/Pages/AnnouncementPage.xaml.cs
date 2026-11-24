using System.Windows;
using Gekota.WPF.Controls;

#nullable enable
namespace Gekota.WPF.Pages
{
    /// <summary>设置页「声明」。</summary>
    public partial class AnnouncementPage
    {
        public AnnouncementPage()
        {
            this.InitializeComponent();
            this.VersionText.Text = "v" + GekotaVersion.FullSemVer;
        }

        private void Tutorial_Click(object sender, RoutedEventArgs e)
        {
            new TutorialDialog { Owner = Window.GetWindow(this) }.ShowDialog();
        }
    }
}
