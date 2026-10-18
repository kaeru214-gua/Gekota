using System.Threading.Tasks;
using System.Windows;
using ModernWpf.Controls;

namespace Gekota.WPF.Controls
{
    internal static class ContentDialogExtensions
    {
        internal static async Task<ContentDialogResult> ShowAndDisableMinimizeToTrayAsync(this ContentDialog contentDialog)
        {
            var mw = Application.Current?.MainWindow as NewMainWindow;
            if (mw == null)
                Serilog.Log.Warning("[对话框] MainWindow 不是 NewMainWindow，跳过最小化到托盘屏蔽");

            if (mw != null) mw.HideToTrayBlockedByContentDialog = true;
            try
            {
#pragma warning disable RS0030 // Do not used banned APIs
                return await contentDialog.ShowAsync();
#pragma warning restore RS0030 // Do not used banned APIs
            }
            finally
            {
                if (mw != null) mw.HideToTrayBlockedByContentDialog = false;
            }
        }
    }
}
