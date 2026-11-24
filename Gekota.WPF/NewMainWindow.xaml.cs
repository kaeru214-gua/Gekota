using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Gekota.WPF.Controls;
using Gekota.WPF.Services;
using Hardcodet.Wpf.TaskbarNotification;
using ModernWpf.Controls;
using Serilog;
using WPFLocalizeExtension.Engine;
using WPFLocalizeExtension.Extensions;

#nullable enable
namespace Gekota.WPF
{
    /// <summary>
    /// Interaction logic for NewMainWindow.xaml
    /// </summary>
    public partial class NewMainWindow : Window
    {
        public string SoftwareVersion { get; }

        public NewMainWindow()
        {
            this.SoftwareVersion = GekotaVersion.FullSemVer;

            LocalizeDictionary.Instance.Culture = CultureInfo.CurrentUICulture;
#if DEBUG
            LocalizeDictionary.Instance.OutputMissingKeys = true;
            LocalizeDictionary.Instance.MissingKeyEvent += (object sender, MissingKeyEventArgs e) => MessageBox.Show("Missing: " + e.Key);
#endif

            this.InitializeComponent();
            this.ApplyResponsiveWindowSize();
            this.SizeChanged += (s, e) => this.ApplyUiScale();
            this.ApplyUiScale();

            SingleInstance.NotificationReceived += this.SingleInstance_NotificationReceived;
        }

        private void ApplyResponsiveWindowSize()
        {
            var work = SystemParameters.WorkArea;
            // 1080p 使用 1152×768；高分辨率按比例放大，4K 上限 1800×1200。
            var scale = Math.Max(1.0, Math.Min(work.Width / 1920.0, work.Height / 1080.0));
            var width = Math.Min(1800.0, 1152.0 * scale);
            var height = width * 2.0 / 3.0;
            if (height > 1200.0)
            {
                height = 1200.0;
                width = 1800.0;
            }
            if (width > work.Width * 0.84)
            {
                width = work.Width * 0.84;
                height = width * 2.0 / 3.0;
            }
            if (height > work.Height * 0.84)
            {
                height = work.Height * 0.84;
                width = height * 3.0 / 2.0;
            }
            this.Width = Math.Max(600.0, width);
            this.Height = Math.Max(400.0, height);
        }

        private void ApplyUiScale()
        {
            if (this.RootPage == null || this.ActualWidth <= 0 || this.ActualHeight <= 0) return;
            var scale = Math.Min(this.ActualWidth / 1152.0, this.ActualHeight / 768.0);
            if (scale <= 0 || double.IsNaN(scale)) return;
            this.RootPage.LayoutTransform = new ScaleTransform(scale, scale);
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            this.ApplyUiScale();
            WindowEffects.ApplyRoundedRegion(this);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            MacTheme.ApplyWindowMaterial();
            WindowEffects.ApplyRoundedRegion(this);

            if (PresentationSource.FromVisual(this) is HwndSource source)
                source.AddHook(this.WindowProc);
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            WindowEffects.ApplyRoundedRegion(this);
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            try { this.Activate(); }
            catch (Exception) { }
        }

        private void SingleInstance_NotificationReceived(object sender, EventArgs e) => this.SuperActivateAction();

        public event EventHandler? NativeBeforeWindowClose;

        internal Action<string, string, BalloonIcon>? ShowBalloonTipCallback { get; set; }

        internal void CloseWithoutConfirmAction()
        {
            this.CloseConfirmed = true;
            _ = this.Dispatcher.BeginInvoke(this.Close, DispatcherPriority.Normal);
        }

        internal void SuperActivateAction()
        {
            try
            {
                this.Show();
                this.WindowState = WindowState.Normal;
                this.Topmost = true;
                this.Activate();
                this.Topmost = false;
                this.Focus();
            }
            catch (Exception)
            { }
        }

        private bool notification_showed = false;
        public bool HideToTray { get; set; } = false;
        public bool HideToTrayBlockedByContentDialog { get; set; } = false;

        private void Window_StateChanged(object sender, EventArgs e)
        {
            WindowEffects.ApplyRoundedRegion(this);
            (this.RootPage as Pages.RootPage)?.SetWindowMaximized(this.WindowState == WindowState.Maximized);

            if (this.HideToTray && !this.HideToTrayBlockedByContentDialog && this.WindowState == WindowState.Minimized)
            {
                this.Hide();
                if (!this.notification_showed)
                {
                    this.notification_showed = true;
                    var title = LocExtension.GetLocalizedValue<string>("Gekota.WPF:Strings:TaskbarIconControl_Title");
                    var body = LocExtension.GetLocalizedValue<string>("Gekota.WPF:Strings:TaskbarIconControl_MinimizedNotification");
                    this.ShowBalloonTipCallback?.Invoke(title, body, BalloonIcon.Info);
                }
            }
        }

        // 无边框 + AllowsTransparency 的窗口最大化时会盖住任务栏，
        // 通过 WM_GETMINMAXINFO 把最大化尺寸限制在当前显示器的工作区。
        private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_GETMINMAXINFO = 0x0024;
            const int WM_NCHITTEST = 0x0084;
            const int WM_SIZE = 0x0005;
            const int WM_SIZING = 0x0214;
            const int HTLEFT = 10;
            const int HTRIGHT = 11;
            const int HTTOP = 12;
            const int HTTOPLEFT = 13;
            const int HTTOPRIGHT = 14;
            const int HTBOTTOM = 15;
            const int HTBOTTOMLEFT = 16;
            const int HTBOTTOMRIGHT = 17;

            if (msg == WM_GETMINMAXINFO)
            {
                var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                var dpi = VisualTreeHelper.GetDpi(this);
                info.ptMinTrackSize.X = (int)Math.Round(600 * dpi.DpiScaleX);
                info.ptMinTrackSize.Y = (int)Math.Round(400 * dpi.DpiScaleY);

                var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref monitorInfo))
                {
                    var work = monitorInfo.rcWork;
                    var full = monitorInfo.rcMonitor;
                    info.ptMaxPosition.X = work.Left - full.Left;
                    info.ptMaxPosition.Y = work.Top - full.Top;
                    info.ptMaxSize.X = work.Right - work.Left;
                    info.ptMaxSize.Y = work.Bottom - work.Top;
                    Marshal.StructureToPtr(info, lParam, true);
                    handled = true;
                }
            }
            else if (msg == WM_SIZING && this.WindowState == WindowState.Normal)
            {
                var rect = Marshal.PtrToStructure<RECT>(lParam);
                var edge = wParam.ToInt32();
                var width = rect.Right - rect.Left;
                var height = rect.Bottom - rect.Top;
                if (width <= 0 || height <= 0) return IntPtr.Zero;

                var dpi = VisualTreeHelper.GetDpi(this);
                var minWidth = (int)Math.Round(600 * dpi.DpiScaleX);
                var minHeight = (int)Math.Round(400 * dpi.DpiScaleY);

                if (edge == 3 || edge == 6)
                {
                    height = Math.Max(minHeight, height);
                    width = Math.Max(minWidth, (int)Math.Round(height * 3.0 / 2.0));
                    height = (int)Math.Round(width * 2.0 / 3.0);
                    rect.Right = rect.Left + width;
                    if (edge == 3)
                        rect.Top = rect.Bottom - height;
                    else
                        rect.Bottom = rect.Top + height;
                }
                else
                {
                    width = Math.Max(minWidth, width);
                    height = (int)Math.Round(width * 2.0 / 3.0);
                    if (height < minHeight)
                    {
                        height = minHeight;
                        width = (int)Math.Round(height * 3.0 / 2.0);
                    }

                    if (edge == 1 || edge == 4 || edge == 7)
                        rect.Left = rect.Right - width;
                    else
                        rect.Right = rect.Left + width;

                    if (edge == 4 || edge == 5)
                        rect.Top = rect.Bottom - height;
                    else
                        rect.Bottom = rect.Top + height;
                }

                Marshal.StructureToPtr(rect, lParam, true);
                handled = true;
            }
            else if (msg == WM_NCHITTEST && this.WindowState == WindowState.Normal)
            {
                // 去掉 WindowChrome 后自己实现 8px 边缘缩放，避免 WindowChrome
                // 与分层窗口组合时在圆角外画出黑色方块。
                long lp = lParam.ToInt64();
                int screenX = (short)(lp & 0xFFFF);
                int screenY = (short)((lp >> 16) & 0xFFFF);
                var p = this.PointFromScreen(new Point(screenX, screenY));
                const double edge = 8;

                bool left = p.X >= 0 && p.X <= edge;
                bool right = p.X >= this.ActualWidth - edge && p.X <= this.ActualWidth;
                bool top = p.Y >= 0 && p.Y <= edge;
                bool bottom = p.Y >= this.ActualHeight - edge && p.Y <= this.ActualHeight;

                if (left || right || top || bottom)
                {
                    handled = true;
                    if (top && left) return (IntPtr)HTTOPLEFT;
                    if (top && right) return (IntPtr)HTTOPRIGHT;
                    if (bottom && left) return (IntPtr)HTBOTTOMLEFT;
                    if (bottom && right) return (IntPtr)HTBOTTOMRIGHT;
                    if (left) return (IntPtr)HTLEFT;
                    if (right) return (IntPtr)HTRIGHT;
                    if (top) return (IntPtr)HTTOP;
                    return (IntPtr)HTBOTTOM;
                }
            }

            else if (msg == WM_SIZE)
            {
                // 窗口尺寸变化后重新设置区域，否则缩小时区域会停留在旧尺寸，圆角消失。
                this.Dispatcher.BeginInvoke(
                    new Action(() => WindowEffects.ApplyRoundedRegion(this)),
                    DispatcherPriority.Background);
            }

            return IntPtr.Zero;
        }

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        #region Confirm Close Window

        private bool CloseConfirmed = false;

        private readonly SemaphoreSlim CloseWindowSemaphoreSlim = new SemaphoreSlim(1, 1);

        public bool PromptCloseConfirm { get; set; } = true;

#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
#pragma warning restore VSTHRD100 // Avoid async void methods
        {
            Serilog.Log.Information("[退出] Closing: prompt={P} confirmed={C}", this.PromptCloseConfirm, this.CloseConfirmed);
            if (this.PromptCloseConfirm && !this.CloseConfirmed)
            {
                e.Cancel = true;

                // 已经有对话框（例如首次启动的“选择工作目录”）正在显示时，
                // 不能再弹关闭确认框，否则 ModernWpf 会抛 "Only a single ContentDialog
                // can be open at any time"，窗口会卡住。直接关闭即可。
                if (ContentDialogExtensions.IsAnyDialogOpen)
                {
                    Serilog.Log.Information("[退出] 检测到已打开的对话框，跳过关闭确认并退出");
                    this.CloseConfirmed = true;
                    _ = this.Dispatcher.BeginInvoke(this.Close, DispatcherPriority.Normal);
                    return;
                }

                if (await this.CloseWindowSemaphoreSlim.WaitAsync(0))
                {
                    try
                    {
                        var dialog = new CloseWindowConfirmDialog { Owner = Application.Current.MainWindow };
                        this.Topmost = false;
                        await dialog.ShowAndDisableMinimizeToTrayAsync();
                        if (dialog.Confirmed)
                        {
                            this.CloseConfirmed = true;
                            _ = this.Dispatcher.BeginInvoke(this.Close, DispatcherPriority.Normal);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Warning(ex, "[退出] 关闭确认框失败，直接退出");
                        this.CloseConfirmed = true;
                        _ = this.Dispatcher.BeginInvoke(this.Close, DispatcherPriority.Normal);
                        return;
                    }
                    finally
                    {
                        this.CloseWindowSemaphoreSlim.Release();
                    }
                }
            }
            else
            {
                SingleInstance.NotificationReceived -= this.SingleInstance_NotificationReceived;
                Log.Logger.ForContext<NewMainWindow>().Debug("Window Closing");
                NativeBeforeWindowClose?.Invoke(this, EventArgs.Empty);
                return;
            }
        }

        #endregion
    }
}
