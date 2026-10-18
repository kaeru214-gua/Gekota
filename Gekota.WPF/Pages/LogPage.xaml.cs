namespace Gekota.WPF.Pages
{
    /// <summary>
    /// Interaction logic for LogPage.xaml
    /// </summary>
    public partial class LogPage
    {
        public LogPage()
        {
            this.InitializeComponent();
            this.VersionTextBlock.Text = " " + GekotaVersion.FullSemVer;
            this.VersionTextBlock.ToolTip = GekotaVersion.InformationalVersion;
        }
    }
}
