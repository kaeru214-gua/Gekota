using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Gekota.WPF.Pages;
using ModernWpf.Controls;

namespace Gekota.WPF.Controls
{
    internal static class ContentDialogExtensions
    {
        private static int openDialogCount;

        /// <summary>当前是否有 ContentDialog 正在显示。ModernWpf 同一时间只允许一个。</summary>
        internal static bool IsAnyDialogOpen => Volatile.Read(ref openDialogCount) > 0;

        internal static async Task<ContentDialogResult> ShowAndDisableMinimizeToTrayAsync(this ContentDialog contentDialog)
        {
            var mw = Application.Current?.MainWindow as NewMainWindow;
            if (mw == null)
                Serilog.Log.Warning("[对话框] MainWindow 不是 NewMainWindow，跳过最小化到托盘屏蔽");

            if (mw != null) mw.HideToTrayBlockedByContentDialog = true;
            RootPage.Current?.SetDialogOverlayMode(true);
            Interlocked.Increment(ref openDialogCount);
            try
            {
#pragma warning disable RS0030 // Do not used banned APIs
                return await contentDialog.ShowAsync();
#pragma warning restore RS0030 // Do not used banned APIs
            }
            finally
            {
                RootPage.Current?.SetDialogOverlayMode(false);
                Interlocked.Decrement(ref openDialogCount);
                if (mw != null) mw.HideToTrayBlockedByContentDialog = false;
            }
        }
    }
}
