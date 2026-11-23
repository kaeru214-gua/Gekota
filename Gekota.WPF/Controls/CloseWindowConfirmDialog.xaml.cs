using System.Windows;

namespace Gekota.WPF.Controls
{
    /// <summary>
    /// Interaction logic for CloseWindowConfirmDialog.xaml
    /// </summary>
    public partial class CloseWindowConfirmDialog
    {
        public CloseWindowConfirmDialog()
        {
            this.InitializeComponent();
        }

        public bool Confirmed { get; private set; }

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            this.Confirmed = true;
            this.Hide();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.Confirmed = false;
            this.Hide();
        }
    }
}
