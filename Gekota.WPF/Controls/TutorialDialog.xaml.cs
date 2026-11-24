using System.Windows;

#nullable enable
namespace Gekota.WPF.Controls
{
    public partial class TutorialDialog : Window
    {
        public TutorialDialog()
        {
            this.InitializeComponent();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true;
        }
    }
}
