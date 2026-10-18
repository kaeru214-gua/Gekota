using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CliWrap;
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
        private readonly System.Collections.Generic.HashSet<Guid> subscribedRooms = new System.Collections.Generic.HashSet<Guid>();
        private readonly WorkDirectoryLoader workDirectoryLoader = new WorkDirectoryLoader();
        private readonly NavigationTransitionInfo transitionInfo = new DrillInNavigationTransitionInfo();

        internal static IServiceProvider? ServiceProvider { get; private set; }
        private ServiceProvider serviceProvider = null!;
        internal RootModel Model { get; private set; }

        internal static Action? SwitchToSettingsPage { get; private set; }

        public RootPage()
        {
            void AddType(Type t) => this.PageMap.Add(t.Name, t);
            AddType(typeof(RoomListPage));
            AddType(typeof(PlaybackPage));
            AddType(typeof(SettingsPage));
            AddType(typeof(LogPage));
            AddType(typeof(AboutPage));
            AddType(typeof(AnnouncementPage));
            AddType(typeof(ToolboxAutoFixPage));
            AddType(typeof(ToolboxDanmakuMergerPage));
            AddType(typeof(ToolboxDanmakuConvertPage));
            AddType(typeof(ToolboxCompressPage));
            AddType(typeof(ToolboxRemuxPage));
            AddType(typeof(UserPage));

            this.Model = new RootModel();
            this.DataContext = this.Model;

            this.InitializeComponent();
            CompressSession.Instance.PropertyChanged += (s, e) => this.RefreshCompressBar();

#if DEBUG
            this.DebugBuildIcon.Visibility = Visibility.Visible;
#endif

            var mw = Application.Current.MainWindow as NewMainWindow;
            if (mw is not null)
                mw.NativeBeforeWindowClose += this.RootPage_NativeBeforeWindowClose;

            Loaded += this.RootPage_Loaded;
            Loaded += (s, e) => this.ApplyAppearance();
            Gekota.WPF.Services.AppearanceManager.Changed += (s, e) => this.ApplyAppearance();

            this.UpdateUserIcon();

            SwitchToSettingsPage = () =>
            {
                this.SettingsPageNavigationViewItem.IsSelected = true;
            };
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
                                return;
                            case ContentDialogResult.None:
                            default:
                                logger.Debug("Exit selected");
                                (Application.Current.MainWindow as NewMainWindow)!.CloseWithoutConfirmAction();
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
                        (Application.Current.MainWindow as NewMainWindow)!.CloseWithoutConfirmAction();
                        return;
                    }

                    this.serviceProvider = this.BuildServiceProvider(config, logger);
                    ServiceProvider = this.serviceProvider;
                    var recorder = this.serviceProvider.GetRequiredService<IRecorder>();

                    this.Model.Recorder = recorder;
                    this.RoomListPageNavigationViewItem.IsEnabled = true;
                    this.SettingsPageNavigationViewItem.IsEnabled = true;
                    (Application.Current.MainWindow as NewMainWindow)!.HideToTray = true;
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
                                this.AnnouncementPageNavigationViewItem.IsSelected = true;
                            else
                                this.RoomListPageNavigationViewItem.IsSelected = true;

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

        private ServiceProvider BuildServiceProvider(Core.Config.V3.ConfigV3 config, ILogger logger) => new ServiceCollection()
            .AddSingleton(logger)
            .AddRecorderConfig(config)
            .AddFlv()
            .AddRecorder()
            .BuildServiceProvider();

        private void NavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.IsSettingsSelected)
            {
                this.MainFrame.Navigate(typeof(SettingsPage), null, this.transitionInfo);
            }
            else
            {
                var selectedItem = (NavigationViewItem)args.SelectedItem;
                var selectedItemTag = (string)selectedItem.Tag;
                if (selectedItemTag.StartsWith("http"))
                {
                    try
                    {
                        this.MainFrame.Navigate(new Uri(selectedItemTag), null, this.transitionInfo);
                    }
                    catch (Exception)
                    {
                    }
                }
                else if (this.PageMap.ContainsKey(selectedItemTag))
                {
                    var pageType = this.PageMap[selectedItemTag];
                    this.MainFrame.Navigate(pageType, null, this.transitionInfo);
                }
            }
        }

        /// <summary>根据当前页面类型与压制状态，控制全局进度条的显隐与内容。</summary>
        private void RefreshCompressBar()
        {
            var page = this.MainFrame.Content;
            var hide = page is ToolboxCompressPage
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
                if (sender is not ModernWpf.Controls.Frame frame) return;

                while (frame.BackStackDepth > 0)
                {
                    frame.RemoveBackEntry();
                }
            }
            catch (Exception)
            {
            }

            this.RefreshCompressBar();
        }

        private void SwitchLightDarkTheme_Click(object sender, RoutedEventArgs e)
        {
            if (this.Dispatcher.CheckAccess())
                changeTheme();
            else
                _ = this.Dispatcher.BeginInvoke(changeTheme);

            static void changeTheme()
            {
                ThemeManager.Current.ApplicationTheme =
                    ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Dark
                    ? ApplicationTheme.Light
                    : ApplicationTheme.Dark;
            };
        }

        // ============================================================
        // 用户登录状态
        // ============================================================
        internal void UpdateUserIcon()
        {
            try
            {
                var session = Gekota.WPF.Services.UserSessionStore.Load();
                if (session is not null)
                {
                    this.UserNavigationItemDefault.Visibility = Visibility.Collapsed;
                    this.UserNavigationItemLoggedIn.Visibility = Visibility.Visible;
                    this.UserNavigationItemLoggedIn.Content = session.Username;

                    _ = this.LoadUserAvatarIconAsync(session.AvatarUrl);
                }
                else
                {
                    this.UserNavigationItemDefault.Visibility = Visibility.Visible;
                    this.UserNavigationItemLoggedIn.Visibility = Visibility.Collapsed;
                    this.UserNavigationItemLoggedIn.Content = "用户";
                    this.UserAvatarIcon.UriSource = null;
                }
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "UpdateUserIcon 出错");
            }
        }

        private async Task LoadUserAvatarIconAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                using var client = new System.Net.Http.HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
                client.DefaultRequestHeaders.Referrer = new Uri("https://www.bilibili.com/");
                var bytes = await client.GetByteArrayAsync(url).ConfigureAwait(false);

                var tempFile = Path.Combine(
                    Path.GetTempPath(),
                    "Gekota_avatar_" + Guid.NewGuid().ToString("N") + ".png");
                File.WriteAllBytes(tempFile, bytes);

                await this.Dispatcher.InvokeAsync(() =>
                {
                    this.UserAvatarIcon.UriSource = new Uri(tempFile, UriKind.Absolute);
                });
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "加载导航栏用户头像失败");
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

            _ = Task.Run(() => this.RemuxToMp4Async(flvPath));

            // 录播结束：先弹幕转换，再自动压制（纯净版 -> 弹幕版）
            _ = Task.Run(() => this.CompressAfterRecordingAsync(flvPath, e?.Room, e?.Event?.FileOpenTime ?? DateTimeOffset.Now));
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

        private void CompressAfterRecordingAsync(string flvPath, IRoom? room, DateTimeOffset openTime)
        {
            try
            {
                // 1) 先转弹幕，生成 .ass
                this.ConvertDanmakuAsync(flvPath);

                var options = VideoCompressSettings.Current;
                var assPath = Path.ChangeExtension(flvPath, "ass");

                // 2) 自动压制（纯净版 + 弹幕版），统一走 CompressSession
                if (options.AutoCompressEnabled)
                {
                    var session = CompressSession.Instance;
                    session.AddForAutoCompress(flvPath, null);                 // 纯净版
                    if (File.Exists(assPath))
                        session.AddForAutoCompress(flvPath, assPath);         // 弹幕版
                    session.Start(options);
                }

                // 3) 压制完成后自动上传（串行队列，逐个执行）
                if (BiliUploadSettings.AutoUploadEnabled && room != null)
                    BiliUploadQueue.Instance.Enqueue(() => this.AutoUploadAfterRecordingAsync(flvPath, room, openTime));
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "[压制] 自动压制出错 {Flv}", flvPath);
            }
        }

        private async Task AutoUploadAfterRecordingAsync(string flvPath, IRoom room, DateTimeOffset openTime)
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

                var info = new RecordingInfo
                {
                    FlvPath = flvPath,
                    RoomId = room.RoomConfig.RoomId,
                    StreamerName = room.Name,
                    LiveTitle = room.Title,
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

                logger.Information("开始转封装 {Flv} -> {Mp4}", flvPath, mp4Path);

                var result = await Cli.Wrap(ffmpegPath)
                    .WithValidation(CommandResultValidation.None)
                    .WithWorkingDirectory(ffmpegDir)
                    .WithArguments(new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", flvPath, "-c", "copy", mp4Path })
                    .ExecuteAsync()
                    .ConfigureAwait(false);

                if (result.ExitCode == 0)
                    logger.Information("转封装完成：{Mp4}", mp4Path);
                else
                    logger.Warning("转封装失败，退出码 {Code}，文件 {Flv}", result.ExitCode, flvPath);
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "转封装时发生错误 {Flv}", flvPath);
            }
        }

        // ============================ 自定义外观 ============================

        private void ApplyAppearance()
        {
            try
            {
                var s = Gekota.WPF.Services.AppearanceManager.Current;

                // 主题色 -> 侧边栏 pane 背景（同时写入本页资源，优先级更高）
                var brush = Gekota.WPF.Services.AppearanceManager.CreateBrush(s.ThemeColorHex);
                this.Resources["NavigationViewDefaultPaneBackground"] = brush;
                this.Resources["NavigationViewExpandedPaneBackground"] = brush;

                // 背景图 + 白色遮罩
                if (!string.IsNullOrWhiteSpace(s.BackgroundImagePath) && File.Exists(s.BackgroundImagePath))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(s.BackgroundImagePath, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();

                    this.AppearanceBackgroundImage.Source = bmp;
                    this.AppearanceWhiteMask.Opacity = s.MaskOpacity;
                    this.Background = System.Windows.Media.Brushes.Transparent;
                }
                else
                {
                    this.AppearanceBackgroundImage.Source = null;
                    this.SetResourceReference(BackgroundProperty, "SystemControlPageBackgroundAltHighBrush");
                }
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "[外观] 应用外观失败");
            }
        }


    }
}
