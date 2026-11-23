using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CliWrap;
using CliWrap.Buffered;
using Gekota;
using Gekota.Core;
using Gekota.Core.Config;
using Gekota.DependencyInjection;
using Gekota.WPF.Controls;
using Gekota.WPF.Models;
using Gekota.WPF.Services;
using Microsoft.Extensions.DependencyInjection;
using ModernWpf;
using ModernWpf.Controls;
using ModernWpf.Media.Animation;
using Serilog;
using Path = System.IO.Path;

#nullable enable
namespace Gekota.WPF.Pages
{
    /// <summary>
    /// Interaction logic for RootPage.xaml
    /// </summary>
    public partial class RootPage : UserControl
    {
        private static readonly ILogger logger = Log.ForContext<RootPage>();

        internal static string? CommandArgumentRecorderPath = null;
        internal static bool CommandArgumentFirstRun = false;
        internal static bool CommandArgumentAskPath = false;
        internal static bool CommandArgumentHide = false;

        private readonly Dictionary<string, Type> PageMap = new Dictionary<string, Type>();
        private readonly Dictionary<string, object> pageCache = new Dictionary<string, object>();
        private static readonly Dictionary<string, string> ToolPageTitles = new Dictionary<string, string>
        {
            ["ToolboxAutoFixPage"] = "录播修复",
            ["ToolboxDanmakuMergerPage"] = "弹幕合并",
            ["ToolboxDanmakuConvertPage"] = "弹幕转换",
            ["ToolboxCompressPage"] = "视频压制",
            ["ToolboxRemuxPage"] = "转封装",
        };
        private readonly System.Collections.Generic.HashSet<Guid> subscribedRooms = new System.Collections.Generic.HashSet<Guid>();
        private readonly WorkDirectoryLoader workDirectoryLoader = new WorkDirectoryLoader();
        private readonly DispatcherTimer statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };

        internal static IServiceProvider? ServiceProvider { get; private set; }
        internal static RootPage? Current { get; private set; }        private ServiceProvider serviceProvider = null!;
        internal RootModel Model { get; private set; }

        internal static Action? SwitchToSettingsPage { get; private set; }

        public RootPage()
        {
            void AddType(Type t) => this.PageMap.Add(t.Name, t);
            AddType(typeof(RoomListPage));
            AddType(typeof(PlaybackPage));
            AddType(typeof(SchedulePage));
            AddType(typeof(DynamicsPage));
            AddType(typeof(SettingsPage));
            AddType(typeof(LogPage));
            AddType(typeof(AboutPage));
            AddType(typeof(AnnouncementPage));
            AddType(typeof(ToolboxAutoFixPage));
            AddType(typeof(ToolboxDanmakuMergerPage));
            AddType(typeof(ToolboxDanmakuConvertPage));
            AddType(typeof(ToolboxCompressPage));
            AddType(typeof(ToolboxRemuxPage));
            AddType(typeof(ToolboxHomePage));
            AddType(typeof(SkinPage));
            AddType(typeof(UserPage));

            Current = this;

            this.Model = new RootModel();
            this.DataContext = this.Model;

            this.InitializeComponent();
            CompressSession.Instance.PropertyChanged += (s, e) => this.RefreshCompressBar();

            var mw = Application.Current.MainWindow as NewMainWindow;
            if (mw is not null)
                mw.NativeBeforeWindowClose += this.RootPage_NativeBeforeWindowClose;

            Loaded += this.RootPage_Loaded;
            Loaded += (s, e) =>
            {
                this.StartStatusTimer();
                this.AnimateShellIn();
                this.ApplyAppearance();
                this.ScheduleUiSnapshot();
            };
            Unloaded += (s, e) => this.statusTimer.Stop();
            Gekota.WPF.Services.AppearanceManager.Changed += (s, e) => this.ApplyAppearance();

            this.UpdateUserIcon();

            try
            {
                var appearance = Gekota.WPF.Services.AppearanceManager.Current;
                if (!string.IsNullOrWhiteSpace(appearance.BackgroundImagePath) &&
                    File.Exists(appearance.BackgroundImagePath))
                    _ = BackgroundImageService.GetAsync(appearance.BackgroundImagePath);
            }
            catch { }

            SwitchToSettingsPage = () => this.NavigateToPage("SettingsPage");
        }

        private static void CloseMainWindowWithoutConfirm()
        {
            if (Application.Current?.MainWindow is NewMainWindow window)
                window.CloseWithoutConfirmAction();
        }

        private void RootPage_NativeBeforeWindowClose(object sender, EventArgs e)
        {
            this.Model.Dispose();
            SingleInstance.Cleanup();
        }

#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void RootPage_Loaded(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100 // Avoid async void methods
        {
            try
            {
                if (Application.Current.MainWindow is NewMainWindow mainWindow)
                {
                    // 首次配置/选择工作目录期间置顶，避免被 Visual Studio 等窗口挡住。
                    mainWindow.Topmost = true;
                    mainWindow.Activate();
                }
            }
            catch (Exception) { }

            if (CommandArgumentFirstRun)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1000);
                    _ = this.Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)(() =>
                    {
                        MessageBox.Show(Window.GetWindow(this), @"Gekota 安装成功 
之后再运行请使用桌面或开始菜单里的快捷方式。
如需卸载 可在系统设置里操作。

Gekota Installed!
Please use the shortcut on the desktop or
in the start menu to launch.
You can uninstall me in system settings.", "安装成功 Installed", MessageBoxButton.OK, MessageBoxImage.Information);
                    }));
                });
            }

            var pathInfo = this.workDirectoryLoader.Read();
            // 工作目录尚未配置 且 没有从命令行传入路径 => 首次使用
            var firstUse = string.IsNullOrWhiteSpace(pathInfo.Path)
                           && string.IsNullOrWhiteSpace(CommandArgumentRecorderPath);
            var first_time = true;
            var from_argument = false;
            var error = WorkDirectorySelectorDialog.WorkDirectorySelectorDialogError.None;
            string path;

            while (true)
            {
                try
                {
                    if (first_time)
                    {
                        try
                        {
                            first_time = false;

                            if (!string.IsNullOrWhiteSpace(CommandArgumentRecorderPath))
                            {
                                logger.Debug("Using path from command argument");
                                path = Path.GetFullPath(CommandArgumentRecorderPath);
                                from_argument = true;
                            }
                            else if (pathInfo.SkipAsking && !CommandArgumentAskPath)
                            {
                                logger.Debug("Using path from path.json file");
                                path = pathInfo.Path;
                            }
                            else
                            {
                                continue;
                            }
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                    }
                    else
                    {
                        var lastdir = pathInfo.Path;

                        var dialog = new WorkDirectorySelectorDialog
                        {
                            Error = error,
                            Path = lastdir,
                            SkipAsking = pathInfo.SkipAsking,
                            Owner = Application.Current.MainWindow
                        };
                        var dialogResult = await dialog.ShowAndDisableMinimizeToTrayAsync();
                        switch (dialogResult)
                        {
                            case ContentDialogResult.Primary:
                                logger.Debug("Confirm path selected");
                                break;
                            case ContentDialogResult.Secondary:
                                logger.Debug("Toolbox mode selected");
                                if (Application.Current.MainWindow is NewMainWindow toolboxWindow)
                                    toolboxWindow.Topmost = false;
                                return;
                            case ContentDialogResult.None:
                            default:
                                logger.Debug("Exit selected");
                                CloseMainWindowWithoutConfirm();
                                return;
                        }

                        pathInfo.SkipAsking = dialog.SkipAsking;

                        try
                        { path = Path.GetFullPath(dialog.Path); }
                        catch (Exception)
                        {
                            error = WorkDirectorySelectorDialog.WorkDirectorySelectorDialogError.PathNotSupported;
                            continue;
                        }
                    }

                    var configFilePath = Path.Combine(path, "config.json");

                    if (!Directory.Exists(path))
                    {
                        error = WorkDirectorySelectorDialog.WorkDirectorySelectorDialogError.PathDoesNotExist;
                        continue;
                    }
                    else if (!Directory.EnumerateFiles(path).Any(x => Path.GetFileName(x) != "desktop.ini"))
                    {
                    }
                    else if (!File.Exists(configFilePath))
                    {
                        error = WorkDirectorySelectorDialog.WorkDirectorySelectorDialogError.PathContainsFiles;
                        continue;
                    }

                    try
                    {
                        if (!from_argument)
                        {
                            pathInfo.Path = path;
                            this.workDirectoryLoader.Write(pathInfo);
                        }
                    }
                    catch (Exception) { }

                    var config = ConfigParser.LoadFromDirectory(path);
                    if (config is null)
                    {
                        error = WorkDirectorySelectorDialog.WorkDirectorySelectorDialogError.FailedToLoadConfig;
                        continue;
                    }
                    config.Global.WorkDirectory = path;

                    if (!SingleInstance.CheckMutex(path))
                    {
                        CloseMainWindowWithoutConfirm();
                        return;
                    }

                    this.serviceProvider = this.BuildServiceProvider(config, logger);
                    ServiceProvider = this.serviceProvider;
                    var recorder = this.serviceProvider.GetRequiredService<IRecorder>();

                    this.Model.Recorder = recorder;
                    this.DockHome.IsEnabled = true;
                    this.DockPlayback.IsEnabled = true;
                    this.DockSettings.IsEnabled = true;
                    if (Application.Current.MainWindow is NewMainWindow mainWindow)
                    {
                        mainWindow.Topmost = false;
                        mainWindow.HideToTray = true;
                    }
                    NetworkChangeDetector.Enable();

                    recorder.StreamStarted += (sender, room) =>
                    {
                        if (!recorder.Config.Global.WpfNotifyStreamStart)
                            return;

                        _ = StreamStartedNotification.ShowAsync(room);
                    };

                    // 录制文件写入结束 → 自动转封装
                    recorder.RecordFileClosed += this.Recorder_RecordFileClosed;

                    // 裁剪结束 → 转封装为 MP4（用 HashSet 去重订阅）
                    void SubscribeClip(IRoom r)
                    {
                        lock (this.subscribedRooms)
                        {
                            if (this.subscribedRooms.Add(r.ObjectId))
                            {
                                r.ClipEnded += this.Room_ClipEnded;
                                r.PropertyChanged += this.Room_PropertyChanged;
                                logger.Debug("[{RoomId}] 已订阅 ClipEnded", r.RoomConfig.RoomId);
                            }
                        }
                    }

                    recorder.StreamStarted += (s, r) => SubscribeClip(r);
                    foreach (var r in recorder.Rooms)
                        SubscribeClip(r);

                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(150);
                        _ = this.Dispatcher.BeginInvoke(DispatcherPriority.Normal, method: new Action(() =>
                        {
                            if (firstUse)
                            {
                                this.NavigateToPage("AnnouncementPage");
                                // 首次使用直接展示了公告页，不再额外弹窗
                                AnnouncementState.MarkCurrentVersionShown();
                            }
                            else
                            {
                                this.NavigateToPage("RoomListPage");
                                _ = this.ShowUpdateNoticeOnceAsync();
                            }

                            if (CommandArgumentHide)
                                Application.Current.MainWindow.WindowState = WindowState.Minimized;
                        }));
                    });

                    this.UpdateUserIcon();
                    break;
                }
                catch (Exception ex)
                {
                    error = WorkDirectorySelectorDialog.WorkDirectorySelectorDialogError.UnknownError;
                    logger.Warning(ex, "选择工作目录时发生了未知错误");
                    continue;
                }
            }
        }

        /// <summary>新版本第一次启动时弹一次更新提示（每个版本只弹一次，记录在 AnnouncementState.json）。</summary>
        private async Task ShowUpdateNoticeOnceAsync()
        {
            try
            {
                if (!AnnouncementState.ShouldShowCurrentVersion()) return;

                await Task.Delay(300);

                var dialog = new UpdateNoticeDialog
                {
                    Owner = Application.Current.MainWindow,
                };
                await dialog.ShowAndDisableMinimizeToTrayAsync();

                // 弹完才记；中途关掉程序不会误记为已弹
                AnnouncementState.MarkCurrentVersionShown();
                logger.Information("[公告] 已展示 {Version} 更新提示", AnnouncementContent.Version);
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "[公告] 弹出更新提示失败");
            }
        }

        private ServiceProvider BuildServiceProvider(Core.Config.V3.ConfigV3 config, ILogger logger) => new ServiceCollection()
            .AddSingleton(logger)
            .AddRecorderConfig(config)
            .AddFlv()
            .AddRecorder()
            .BuildServiceProvider();

        // ============================================================
        // Dock 导航 / 窗口行为
        // ============================================================

        private void DockIcon_Activated(object? sender, EventArgs e)
        {
            if (sender is DockIcon { Tag: string tag })
                this.NavigateToPage(tag);
        }

        internal void NavigateToPage(string tag)
        {
            if (!this.PageMap.TryGetValue(tag, out var pageType))
            {
                logger.Debug("[导航] 未知页面 {Tag}", tag);
                return;
            }

            if (this.MainFrame.Content?.GetType() == pageType)
            {
                this.UpdateDockActive(tag);
                return;
            }

            if (this.MainFrame.Content?.GetType() == pageType)
            {
                this.UpdateDockActive(tag);
                return;
            }

            try
            {
                if (this.MainFrame.Content is PlaybackPage oldPlayback)
                    oldPlayback.SetWebViewVisible(false);

                if (!this.pageCache.TryGetValue(tag, out var page))
                {
                    page = Activator.CreateInstance(pageType);
                    if (page != null && tag != "PlaybackPage")
                        this.pageCache[tag] = page;
                }
                if (page == null) return;

                // 使用 Frame 自带导航承载缓存页面，不做手动 Content 交换。
                this.MainFrame.Navigate(page);
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "[导航] 打开页面 {Tag} 失败", tag);
            }
        }

        private void ShowPage(object page)
        {
            this.UpdateDockActive(page.GetType().Name);
            this.RefreshCompressBar();
            this.ApplyContentSheetLayout(page);
        }

        /// <summary>
        /// WebView2 是独立 HWND，会盖在 WPF 弹窗之上。打开 ContentDialog 时先把播放页 WebView 收起。
        /// </summary>
        internal void SetDialogOverlayMode(bool dialogOpen)
        {
            if (this.MainFrame.Content is PlaybackPage playback)
                playback.SetWebViewVisible(!dialogOpen);
        }

        internal void OpenPlayback(IRoom room)
        {
            logger.Debug("[导航] 打开实时播放，直播间 {RoomId}", room.RoomConfig.RoomId);
            PlaybackPage.RequestRoom(room);
            this.NavigateToPage("PlaybackPage");
        }

        private void UpdateDockActive(string? tag = null)
        {
            tag ??= this.MainFrame.Content?.GetType().Name;

            this.DockHome.IsActive = tag == "RoomListPage";
            this.DockPlayback.IsActive = tag == "PlaybackPage";
            this.DockSchedule.IsActive = tag == "SchedulePage";
            this.DockToolbox.IsActive = tag == "ToolboxHomePage" || (tag?.StartsWith("Toolbox", StringComparison.Ordinal) ?? false);
            this.DockUser.IsActive = tag == "UserPage";
            this.DockSettings.IsActive = tag == "SettingsPage";
            this.DockDynamics.IsActive = tag == "DynamicsPage";

            if (tag != null && ToolPageTitles.TryGetValue(tag, out var toolTitle))
            {
                this.ToolPageHeader.Visibility = Visibility.Visible;
                this.ToolPageTitleText.Text = toolTitle;
            }
            else
            {
                this.ToolPageHeader.Visibility = Visibility.Collapsed;
            }
        }

        private void ToolPageBack_Click(object sender, RoutedEventArgs e) => this.NavigateToPage("ToolboxHomePage");

        /// <summary>根据当前页面类型与压制状态，控制全局进度条的显隐与内容。</summary>
        private void RefreshCompressBar()
        {
            var page = this.MainFrame.Content;
            var hide = page is ToolboxCompressPage
                    || page is ToolboxHomePage
                    || page is SkinPage
                    || page is AnnouncementPage
                    || page is AboutPage
                    || page is LogPage;

            var s = CompressSession.Instance;
            var busy = s.IsBusy;

            this.GlobalCompressBar.Visibility = (!hide && busy) ? Visibility.Visible : Visibility.Collapsed;

            if (!hide && busy)
            {
                this.GcbCount.Text = s.CountText;
                this.GcbProgress.Value = s.OverallPercent;
                this.GcbTime.Text = s.TimeText;
            }
        }

        private void MainFrame_Navigated(object sender, System.Windows.Navigation.NavigationEventArgs e)
        {
            try
            {
                if (sender is ModernWpf.Controls.Frame frame)
                {
                    while (frame.BackStackDepth > 0)
                        frame.RemoveBackEntry();
                }
            }
            catch (Exception)
            {
            }

            this.UpdateDockActive();
            this.RefreshCompressBar();
            this.ApplyContentSheetLayout(e.Content);        }

        private void ApplyContentSheetLayout(object? content)
        {
            // 所有页面统一使用完全透明的内容层，避免切页时 ContentSheet 背景变化产生闪屏。
            // Dock 是 RootPage 的固定子元素，不随页面重建。
            this.MaximizeWindowButton.Visibility = Visibility.Visible;
            this.DockColumn.Width = new GridLength(136);
            this.Dock.Width = 88;
            this.ContentSheet.Margin = new Thickness(0);
            this.ContentSheet.CornerRadius = new CornerRadius(0);
            this.ContentSheet.Background = System.Windows.Media.Brushes.Transparent;
            this.DockShadow.Opacity = 0.24;
        }

        private void AnimatePageIn()
        {
            try
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                this.MainFrame.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease });

                if (this.MainFrame.RenderTransform is not TranslateTransform translate)
                {
                    translate = new TranslateTransform();
                    this.MainFrame.RenderTransform = translate;
                }
                translate.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(9, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
            }
            catch (Exception ex) { logger.Debug(ex, "[导航] 页面转场失败"); }
        }

        private void AnimateShellIn()
        {
            try
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                this.RootGrid.Opacity = 0;
                this.RootGrid.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
            }
            catch (Exception) { }
        }

        /// <summary>开发期视觉检查：设置 GEKOTA_UI_SNAPSHOT 环境变量后自动保存一张窗口渲染图。</summary>
        private void ScheduleUiSnapshot()
        {
            var path = Environment.GetEnvironmentVariable("GEKOTA_UI_SNAPSHOT");
            if (string.IsNullOrWhiteSpace(path)) return;

            var skin = Environment.GetEnvironmentVariable("GEKOTA_UI_SKIN");
            if (!string.IsNullOrWhiteSpace(skin))
            {
                try { AppearanceManager.SetThemeColor(skin); }
                catch (Exception ex) { logger.Debug(ex, "[快照] 设置皮肤失败"); }
            }

            if (Environment.GetEnvironmentVariable("GEKOTA_UI_DARK") == "1")
            {
                try { MacTheme.ToggleLightDark(); }
                catch (Exception ex) { logger.Debug(ex, "[快照] 切换深色失败"); }
            }

            var page = Environment.GetEnvironmentVariable("GEKOTA_UI_PAGE");
            var openRoom = Environment.GetEnvironmentVariable("GEKOTA_UI_OPEN_ROOM");
            if (!string.IsNullOrWhiteSpace(page) || !string.IsNullOrWhiteSpace(openRoom))
            {
                var navTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                navTimer.Tick += (s, e) =>
                {
                    navTimer.Stop();

                    if (int.TryParse(openRoom, out var roomId))
                    {
                        var room = this.Model.Recorder?.Rooms.FirstOrDefault(r => r.RoomConfig.RoomId == roomId || r.ShortId == roomId);
                        if (room != null)
                        {
                            this.OpenPlayback(room);
                            return;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(page))
                        this.NavigateToPage(page);
                };
                navTimer.Start();
            }

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(13) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                try
                {
                    this.SaveRootSnapshot(path);
                    this.SaveWindowSnapshot(path);
                    this.MaybeCaptureHomeMenu(path);
                }
                catch (Exception ex) { logger.Warning(ex, "[快照] 保存失败"); }
            };
            timer.Start();
        }

        private void MaybeCaptureHomeMenu(string basePath)
        {
            if (Environment.GetEnvironmentVariable("GEKOTA_UI_MENU") != "1") return;
            if (this.MainFrame.Content is not RoomListPage roomListPage) return;
            if (roomListPage.Content is not FrameworkElement homeRoot) return;
            if (homeRoot.ContextMenu is not ContextMenu menu) return;

            menu.PlacementTarget = homeRoot;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Center;
            menu.IsOpen = true;

            this.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                try
                {
                    var menuPath = Path.Combine(
                        Path.GetDirectoryName(basePath) ?? string.Empty,
                        Path.GetFileNameWithoutExtension(basePath) + ".menu.png");
                    this.SaveElementSnapshot(menu, menuPath);
                    menu.IsOpen = false;
                }
                catch (Exception ex) { logger.Debug(ex, "[快照] 渲染右键菜单失败"); }
            }));
        }

        private void SaveRootSnapshot(string path)
        {
            this.RootGrid.UpdateLayout();
            var width = (int)Math.Max(1, this.RootGrid.ActualWidth);
            var height = (int)Math.Max(1, this.RootGrid.ActualHeight);

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                dc.DrawRectangle(new VisualBrush(this.RootGrid)
                {
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top,
                }, null, new Rect(0, 0, width, height));
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            this.SaveBitmap(bitmap, path);

            logger.Information("[快照] 已保存 {Path}，RootGrid.Opacity={Opacity}，Size={Width}x{Height}",
                path, this.RootGrid.Opacity, width, height);
        }

        private void SaveWindowSnapshot(string basePath)
        {
            var window = Application.Current?.MainWindow;
            if (window == null) return;

            var root = VisualTreeHelper.GetChild(window, 0) as FrameworkElement;
            if (root == null) return;

            var path = Path.Combine(
                Path.GetDirectoryName(basePath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(basePath) + ".window.png");

            this.SaveElementSnapshot(root, path);
        }

        private void SaveElementSnapshot(FrameworkElement element, string path)
        {
            element.UpdateLayout();
            var width = (int)Math.Max(1, element.ActualWidth);
            var height = (int)Math.Max(1, element.ActualHeight);

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                dc.DrawRectangle(new VisualBrush(element)
                {
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top,
                }, null, new Rect(0, 0, width, height));
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            this.SaveBitmap(bitmap, path);
            logger.Information("[快照] 已保存菜单 {Path}，Size={Width}x{Height}", path, width, height);
        }

        private void SaveBitmap(BitmapSource bitmap, string path)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }

        internal void SetWindowMaximized(bool maximized)
        {
            this.ShellClip.CornerRadius = new CornerRadius(maximized ? 0 : 21);
            this.UpdateShellClip();
        }

        private void UpdateShellClip()
        {
            if (this.RootGrid.ActualWidth <= 0 || this.RootGrid.ActualHeight <= 0) return;

            var host = Window.GetWindow(this);
            var maximized = host != null && host.WindowState == WindowState.Maximized;
            var radius = maximized ? 0 : 21;
            this.ShellClip.CornerRadius = new CornerRadius(radius);
            if (host != null) host.Background = System.Windows.Media.Brushes.Transparent;

            var clip = new RectangleGeometry(
                new Rect(0, 0, this.RootGrid.ActualWidth, this.RootGrid.ActualHeight), radius, radius);

            // 背景图、底色和内容都在 RootGrid 里，必须一起裁，否则圆角处会被背景图填成方角。
            this.RootGrid.Clip = clip;
            this.ShellClip.Clip = clip;
        }

        private void ShellClip_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            this.UpdateShellClip();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            var window = Window.GetWindow(this);
            if (window == null) return;

            if (e.ClickCount == 2)
            {
                ToggleWindowMaximize(window);
                return;
            }

            try { window.DragMove(); }
            catch (Exception) { }
        }

        private static void ToggleWindowMaximize(Window window) =>
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void CloseWindowButton_Click(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();

        private void MinimizeWindowButton_Click(object sender, RoutedEventArgs e)
        {
            if (Window.GetWindow(this) is { } window)
                window.WindowState = WindowState.Minimized;
        }

        private void PlaybackFolderAction_Click(object sender, RoutedEventArgs e)
        {
            (this.MainFrame.Content as PlaybackPage)?.OpenRecordingFolder();
        }

        private void PlaybackCaptureAction_Click(object sender, RoutedEventArgs e)
        {
            (this.MainFrame.Content as PlaybackPage)?.CaptureCurrentFrame();
        }

        private void PlaybackClipAction_Click(object sender, RoutedEventArgs e)
        {
            (this.MainFrame.Content as PlaybackPage)?.ToggleCurrentClip();
        }

        private void MaximizeWindowButton_Click(object sender, RoutedEventArgs e)
        {
            if (Window.GetWindow(this) is { } window)
                ToggleWindowMaximize(window);
        }

        private void DockMenuHome_Click(object sender, RoutedEventArgs e) => this.NavigateToPage("RoomListPage");

        private void DockMenuSkin_Click(object sender, RoutedEventArgs e) => this.NavigateToPage("SettingsPage");

        private void DockMenuSettings_Click(object sender, RoutedEventArgs e) => this.NavigateToPage("SettingsPage");

        private void DockMenuTheme_Click(object sender, RoutedEventArgs e) => this.ToggleTheme();

        private void DockMenuQuit_Click(object sender, RoutedEventArgs e) =>
            (Application.Current.MainWindow as NewMainWindow)?.Close();

        private void StartStatusTimer()
        {
            if (this.statusTimer.IsEnabled) return;
            this.statusTimer.Tick += (s, e) => this.UpdateStatusPill();
            this.statusTimer.Start();
            this.UpdateStatusPill();
        }

        private void UpdateStatusPill()
        {
            try
            {
                var recorder = this.Model.Recorder;
                if (recorder == null)
                {
                    this.DockPlayback.Badge = string.Empty;
                    return;
                }

                var recording = recorder.Rooms.Count(r => r.Recording);
                this.DockPlayback.Badge = recording > 0 ? recording.ToString() : string.Empty;
            }
            catch (Exception ex) { logger.Debug(ex, "[状态] 刷新 Dock 角标失败"); }
        }

        internal void ToggleTheme()
        {
            MacTheme.ToggleLightDark();
            this.ApplyAppearance();
        }

        private void SwitchLightDarkTheme_Click(object sender, RoutedEventArgs e) => this.ToggleTheme();

        // ============================================================
        // 用户登录状态
        // ============================================================
        internal void UpdateUserIcon()
        {
            try
            {
                var session = Gekota.WPF.Services.UserSessionStore.Load();
                this.DockUser.Label = session is null ? "用户" : session.Username;
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "UpdateUserIcon 出错");
            }
        }

        // ============================================================
        // 录制结束 → 自动转封装为 MP4
        // ============================================================
        private void Recorder_RecordFileClosed(object? sender, Gekota.Core.Event.AggregatedRoomEventArgs<Gekota.Core.Event.RecordFileClosedEventArgs> e)
        {
            var flvPath = e?.Event?.FullPath;
            if (string.IsNullOrWhiteSpace(flvPath)) return;
            if (!flvPath!.EndsWith(".flv", StringComparison.OrdinalIgnoreCase)) return;

            var room = e?.Room;
            var openTime = e?.Event?.FileOpenTime ?? DateTimeOffset.Now;
            var closeTime = e?.Event?.FileCloseTime ?? DateTimeOffset.Now;
            // 用录制结束时的标题作为兜底；上传时房间可能已经换了新标题，
            // 直接用 room.Title 会和录播文件名对不上。
            var liveTitle = e?.Event?.Title ?? room?.Title ?? string.Empty;

            // 录播结束流水线（严格串行）：转封装 → 弹幕 → 压制 → 自动上传。
            // 压制不能再与转封装并行，否则会抢先占用源文件导致转封装失败。
            RecordingSegmentMerger.Enqueue(flvPath, openTime.LocalDateTime, closeTime.LocalDateTime,
                e?.Event?.Duration ?? 0,
                room != null ? "room-" + room.RoomConfig.RoomId + "|" + Path.GetDirectoryName(flvPath) : flvPath,
                room != null ? () => room.Streaming : null,
                async path =>
            {
                await this.RemuxToMp4Async(path).ConfigureAwait(false);
                this.CompressAfterRecordingAsync(path, room, openTime, liveTitle);
            });
        }

        private void ConvertDanmakuAsync(string flvPath)
        {
            try
            {
                if (!DanmakuConvertSettings.Current.AutoConvertEnabled)
                    return;

                var xmlPath = Path.ChangeExtension(flvPath, "xml");
                if (!File.Exists(xmlPath)) return;

                var assPath = Path.ChangeExtension(xmlPath, "ass");
                var code = DanmakuConverter.Convert(xmlPath, assPath);

                if (code == 0)
                    logger.Information("[弹幕转换] 完成 {Ass}", assPath);
                else
                    logger.Warning("[弹幕转换] 失败（退出码 {Code}）{Xml}", code, xmlPath);
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "[弹幕转换] 出错 {Flv}", flvPath);
            }
        }

        private void CompressAfterRecordingAsync(string flvPath, IRoom? room, DateTimeOffset openTime, string liveTitle)
        {
            try
            {
                // 1) 先转弹幕，生成 .ass
                this.ConvertDanmakuAsync(flvPath);

                var options = VideoCompressSettings.Current;
                var assPath = Path.ChangeExtension(flvPath, "ass");

                // 2) 自动压制（纯净版 + 弹幕版），统一走 CompressSession。
                // 压制用的 ffmpeg 不支持 FLV 里的 HEVC，但转封装后的 mp4 是标准 HEVC，
                // 所以优先压转封装出来的 mp4，没有 mp4 时再退回压源 flv。
                if (options.AutoCompressEnabled)
                {
                    var compressInput = flvPath;
                    var mp4Path = Path.ChangeExtension(flvPath, ".mp4");
                    if (File.Exists(mp4Path))
                        compressInput = mp4Path;

                    var session = CompressSession.Instance;
                    session.AddForAutoCompress(compressInput, null);                 // 纯净版
                    if (File.Exists(assPath))
                        session.AddForAutoCompress(compressInput, assPath);         // 弹幕版
                    session.Start(options);
                }

                // 3) 压制完成后自动上传（串行队列，逐个执行）
                if (BiliUploadSettings.AutoUploadEnabled && room != null)
                    BiliUploadQueue.Instance.Enqueue(() => this.AutoUploadAfterRecordingAsync(flvPath, room, openTime, liveTitle));
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "[压制] 自动压制出错 {Flv}", flvPath);
            }
        }

        private async Task AutoUploadAfterRecordingAsync(string flvPath, IRoom room, DateTimeOffset openTime, string eventTitle)
        {
            var ui = BiliUploadSession.Instance;
            try
            {
                await CompressSession.Instance.WaitUntilIdleAsync().ConfigureAwait(false);
                ui.Start(Path.GetFileName(flvPath));
                logger.Information("[上传] 开始自动上传 {Flv}", flvPath);

                var session = UserSessionStore.Load();
                if (session == null)
                {
                    ui.Fail("未登录，已跳过自动上传");
                    logger.Warning("[上传] 未登录，跳过自动上传 {Flv}", flvPath);
                    return;
                }

                // 标题优先用「录播所在标题文件夹」的名字（和手动上传、文件名一致）；
                // 取不到再用录制结束时记录的标题，避免用到房间当前的最新标题。
                var titleFolder = Path.GetDirectoryName(flvPath) ?? string.Empty;
                var folderTitle = Path.GetFileName(titleFolder);
                var uploadTitle = !string.IsNullOrWhiteSpace(folderTitle) ? folderTitle : eventTitle;

                var info = new RecordingInfo
                {
                    FlvPath = flvPath,
                    RoomId = room.RoomConfig.RoomId,
                    StreamerName = room.Name,
                    LiveTitle = (uploadTitle ?? string.Empty).Trim(),
                    Date = openTime,
                };

                var stage = new Progress<string>(s => ui.ReportStage(s));
                var filePct = new Progress<double>(p => ui.ReportFilePercent(p));

                var bvid = await new BiliUploadOrchestrator()
                    .UploadAsync(info, session.Cookie, stage, System.Threading.CancellationToken.None, null, filePct)
                    .ConfigureAwait(false);

                ui.Complete(bvid);
                logger.Information("[上传] 自动投稿成功 {Bvid}", bvid);
            }
            catch (VideoTooLargeException ex)
            {
                ui.Skip(ex.Message);
                logger.Warning("[上传] 超过 16GB，跳过 {Flv}：{Msg}", flvPath, ex.Message);
            }
            catch (BiliCaptchaRequiredException)
            {
                ui.Fail("投稿需要验证码，请到「用户中心 → B站上传」手动处理");
                logger.Warning("[上传] 自动投稿需要验证码，请到「用户中心 → B站上传」手动处理 {Flv}", flvPath);
            }
            catch (Exception ex)
            {
                ui.Fail(ex.Message);
                logger.Warning(ex, "[上传] 自动上传失败 {Flv}", flvPath);
            }
        }

        private void Room_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (!string.Equals(e.PropertyName, nameof(IRoom.Title), StringComparison.Ordinal)) return;
            if (sender is not IRoom room) return;
            // 标题变化会触发 SplitOutput；给旧文件一点关闭时间，然后立刻结束旧标题批次。
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000).ConfigureAwait(false);
                RecordingSegmentMerger.FlushRoom(room.RoomConfig.RoomId);
            });
        }

        private void Room_ClipEnded(object? sender, Gekota.Core.Event.ClipEventArgs e)
        {
            var flvPath = e?.ClipFilePath;
            if (string.IsNullOrWhiteSpace(flvPath)) return;

            logger.Information("[Clip] 裁剪已结束，开始转封装为 MP4: {Path}", flvPath);
            _ = Task.Run(() => this.RemuxToMp4Async(flvPath!));
        }

        private async Task RemuxToMp4Async(string flvPath)
        {
            try
            {
                if (!File.Exists(flvPath))
                {
                    logger.Debug("待转封装的 FLV 不存在：{Path}", flvPath);
                    return;
                }

                var ffmpegDir = Path.Combine(AppContext.BaseDirectory, "lib");
                var ffmpegPath = Path.Combine(ffmpegDir, "miniffmpeg");
                if (!File.Exists(ffmpegPath))
                {
                    logger.Debug("未找到 miniffmpeg：{Path}", ffmpegPath);
                    return;
                }

                var mp4Path = Path.ChangeExtension(flvPath, ".mp4");
                if (File.Exists(mp4Path))
                {
                    logger.Debug("MP4 已存在，跳过：{Path}", mp4Path);
                    return;
                }

                // 录制刚结束时源文件可能仍被占用（写入缓冲 / 杀毒扫描 / 其他读取方），
                // 失败后短退避重试几次，避免一次偶发占用就直接放弃转封装。
                const int MaxAttempts = 3;
                for (var attempt = 1; attempt <= MaxAttempts; attempt++)
                {
                    logger.Information("开始转封装 {Flv} -> {Mp4}（第 {Attempt}/{Max} 次）", flvPath, mp4Path, attempt, MaxAttempts);

                    var result = await Cli.Wrap(ffmpegPath)
                        .WithValidation(CommandResultValidation.None)
                        .WithWorkingDirectory(ffmpegDir)
                        .WithArguments(new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", flvPath, "-c", "copy", mp4Path })
                        .ExecuteBufferedAsync()
                        .ConfigureAwait(false);

                    if (result.ExitCode == 0)
                    {
                        logger.Information("转封装完成：{Mp4}", mp4Path);
                        return;
                    }

                    var stderr = (result.StandardError ?? string.Empty).Trim();
                    logger.Warning("转封装失败，退出码 {Code}，文件 {Flv}。stderr: {Stderr}",
                        result.ExitCode, flvPath, string.IsNullOrEmpty(stderr) ? "(空)" : stderr);

                    // 失败可能留下半成品，清掉后延时重试
                    try { if (File.Exists(mp4Path)) File.Delete(mp4Path); } catch { }
                    if (attempt < MaxAttempts)
                        await Task.Delay(TimeSpan.FromSeconds(attempt)).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "转封装时发生错误 {Flv}", flvPath);
            }
        }

        // ============================ 自定义外观 ============================

        private async void ApplyAppearance()
        {
            try
            {
                var s = Gekota.WPF.Services.AppearanceManager.Current;

                MacTheme.Apply();

                if (!string.IsNullOrWhiteSpace(s.BackgroundImagePath) && File.Exists(s.BackgroundImagePath))
                {
                    Application.Current.Resources["MacContentBrush"] = System.Windows.Media.Brushes.Transparent;
                    var image = await BackgroundImageService.GetAsync(s.BackgroundImagePath);
                    if (image != null) this.AppearanceBackgroundImage.Source = image;
                }
                else
                {
                    // 保留 XAML 里的内置默认背景，不因为设置路径失效而变成空白。
                }

                this.Background = System.Windows.Media.Brushes.Transparent;
                if (Window.GetWindow(this) is Window hostWindow)
                    hostWindow.Background = System.Windows.Media.Brushes.Transparent;

                MacTheme.ApplyWindowMaterial();
                this.Dispatcher.BeginInvoke(new Action(this.UpdateShellClip),
                    System.Windows.Threading.DispatcherPriority.Loaded);
                this.UpdateStatusPill();
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "[外观] 应用外观失败");
            }
        }


    }
}
