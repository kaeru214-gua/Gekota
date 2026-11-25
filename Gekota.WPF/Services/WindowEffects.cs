using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Serilog;

#nullable enable
namespace Gekota.WPF.Services
{
    /// <summary>
    /// Windows 10/11 原生毛玻璃（Acrylic）封装。
    /// 通过 undocumented 的 SetWindowCompositionAttribute 让系统对窗口背后的桌面做实时模糊，
    /// WPF 内容以半透明画刷叠在上面，得到接近 macOS 的材质；Win11 同时启用 DWM 圆角。
    /// </summary>
    public static class WindowEffects
    {
        private static readonly ILogger logger = Log.ForContext(typeof(WindowEffects));
        private static readonly bool IsWindows11OrGreater = Environment.OSVersion.Version.Build >= 22000;

        private const int WcaAccentPolicy = 19;
        private const int AccentDisabled = 0;
        private const int AccentEnableBlurBehind = 3;
        private const int AccentEnableAcrylicBlurBehind = 4;

        public static void Apply(Window window, bool acrylic, Color tint, bool dark)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                ApplyDarkMode(hwnd, dark);
                ApplyRoundedCorners(hwnd);
                RemoveWindowBorder(hwnd);

                if (!acrylic)
                {
                    // 关闭亚克力时必须完全禁用 accent，否则系统会在 WPF 内容上叠一层
                    // 不透明色，导致整个窗口看起来是纯白的。
                    Disable(window);
                    window.Background = new SolidColorBrush(Color.FromRgb(tint.R, tint.G, tint.B));
                    return;
                }

                var policy = new AccentPolicy
                {
                    AccentState = AccentEnableAcrylicBlurBehind,
                    AccentFlags = 2,
                    GradientColor = ToAbgr(tint),
                    AnimationId = 0,
                };

                var size = Marshal.SizeOf<AccentPolicy>();
                var ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(policy, ptr, false);
                    var data = new WindowCompositionAttributeData
                    {
                        Attribute = WcaAccentPolicy,
                        Data = ptr,
                        SizeOfData = size,
                    };
                    var result = SetWindowCompositionAttribute(hwnd, ref data);
                    if (result == 0)
                    {
                        logger.Debug("[窗口] SetWindowCompositionAttribute 返回 0，亚克力可能不可用，使用纯色回退");
                        window.Background = new SolidColorBrush(Color.FromRgb(tint.R, tint.G, tint.B));
                    }
                    else
                    {
                        logger.Debug("[窗口] 亚克力材质已应用，Acrylic={Acrylic}", acrylic);
                        if (window.Background is SolidColorBrush)
                            window.Background = System.Windows.Media.Brushes.Transparent;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "[窗口] 应用亚克力材质失败");
            }
        }

        public static void Disable(Window window)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;
                RemoveWindowBorder(hwnd);

                var policy = new AccentPolicy { AccentState = AccentDisabled };
                var size = Marshal.SizeOf<AccentPolicy>();
                var ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(policy, ptr, false);
                    var data = new WindowCompositionAttributeData
                    {
                        Attribute = WcaAccentPolicy,
                        Data = ptr,
                        SizeOfData = size,
                    };
                    SetWindowCompositionAttribute(hwnd, ref data);
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
            catch (Exception ex) { logger.Debug(ex, "[窗口] 关闭亚克力材质失败"); }
        }

        private static void ApplyDarkMode(IntPtr hwnd, bool dark)
        {
            try
            {
                var value = dark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int));
            }
            catch { }
        }

        /// <summary>
        /// Win10 没有 DWM 圆角，用窗口区域裁出 21px 圆角；Win11 使用系统圆角。
        /// 分层窗口同样需要这个区域，否则圆角外的透明区域会被系统画成黑块。
        /// </summary>
        public static void ApplyRoundedRegion(Window window)
        {
            // 分层窗口（AllowsTransparency=True）由 WPF 的圆角 Clip 输出 alpha 圆角；
            // SetWindowRgn 对分层窗口不生效或会留下硬边，这里不再使用。
            if (window.AllowsTransparency) return;
            if (Environment.GetEnvironmentVariable("GEKOTA_SAFE_MODE") == "1") return;
            if (IsWindows11OrGreater) return;
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                if (window.WindowState == WindowState.Maximized)
                {
                    SetWindowRgn(hwnd, IntPtr.Zero, true);
                    return;
                }

                if (window.ActualWidth < 1 || window.ActualHeight < 1) return;

                var dpi = VisualTreeHelper.GetDpi(window);
                if (!GetWindowRect(hwnd, out var wr)) return;
                var width = wr.Right - wr.Left;
                var height = wr.Bottom - wr.Top;
                // CreateRoundRectRgn 的后两个参数是椭圆直径，不是半径；
                // 半径 21 DIP 需要传 42 DIP（乘 DPI）才能和 Dock 的 21px 圆角一致。
                var diameter = Math.Max(16, (int)Math.Round(42 * dpi.DpiScaleX));

                var region = CreateRoundRectRgn(0, 0, width, height, diameter, diameter);
                if (region == IntPtr.Zero) return;

                if (SetWindowRgn(hwnd, region, true) == 0)
                    DeleteObject(region);
            }
            catch (Exception ex) { logger.Debug(ex, "[窗口] 应用圆角区域失败"); }
        }

        private static void ApplyRoundedCorners(IntPtr hwnd)
        {
            if (!IsWindows11OrGreater) return;
            try
            {
                // DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2
                var preference = 2;
                DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int));
                RemoveWindowBorder(hwnd);
            }
            catch { }
        }

        /// <summary>去掉 Win11 DWM 在无边框窗口外沿绘制的一圈浅色边。</summary>
        /// <summary>窗口显示后再次去掉原生外框，防止 WPF 重新恢复 thick frame。</summary>
        public static void RemoveBorder(Window window)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd != IntPtr.Zero) RemoveWindowBorder(hwnd);
            }
            catch { }
        }

        private static void RemoveWindowBorder(IntPtr hwnd)
        {
            try
            {
                if (IsWindows11OrGreater)
                {
                    // DWMWA_BORDER_COLOR = 34, DWMWA_COLOR_NONE = 0xFFFFFFFE
                    var none = unchecked((int)0xFFFFFFFE);
                    DwmSetWindowAttribute(hwnd, 34, ref none, sizeof(int));
                }

                // 关闭非客户区渲染，Windows 10 上这会去掉窗口外沿的浅色描边/阴影。
                var ncPolicy = 1; // DWMNCRP_DISABLED
                DwmSetWindowAttribute(hwnd, 2, ref ncPolicy, sizeof(int));

                // Windows 10/11 无边框窗口仍可能带原生 thick frame，
                // 会在四周画出一圈浅灰边，直接去掉。
                const int GWL_STYLE = -16;
                const int WS_THICKFRAME = 0x00040000;
                const int WS_BORDER = 0x00800000;
                const int WS_DLGFRAME = 0x00400000;
                var style = GetWindowLong(hwnd, GWL_STYLE);
                style &= ~(WS_THICKFRAME | WS_BORDER | WS_DLGFRAME);
                SetWindowLong(hwnd, GWL_STYLE, style);

                const int GWL_EXSTYLE = -20;
                const int WS_EX_DLGMODALFRAME = 0x00000001;
                const int WS_EX_WINDOWEDGE = 0x00000100;
                const int WS_EX_CLIENTEDGE = 0x00000200;
                var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                exStyle &= ~(WS_EX_DLGMODALFRAME | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE);
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
            }
            catch { }
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private static int ToAbgr(Color c) => (c.A << 24) | (c.B << 16) | (c.G << 8) | c.R;

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);
    }
}
