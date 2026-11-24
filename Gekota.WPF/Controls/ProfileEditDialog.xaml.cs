using System.Windows;

#nullable enable
namespace Gekota.WPF.Controls
{
    public partial class ProfileEditDialog : Window
    {
        public ProfileEditDialog(string title, string hint, string text)
        {
            this.InitializeComponent();
            this.TitleText.Text = title;
            this.HintText.Text = hint;
            this.Editor.Text = text ?? "";
            this.Editor.SelectAll();
            this.Editor.Focus();
        }

        public string Result { get; private set; } = "";

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            this.Result = this.Editor.Text.Trim();
            this.DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
        }
    }
}
