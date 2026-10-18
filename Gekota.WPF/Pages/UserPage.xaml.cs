using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Gekota.Core.Api.Http;
using Gekota.WPF.Services;
using QRCoder;
using Serilog;

#nullable enable
namespace Gekota.WPF.Pages
{
    public partial class UserPage
    {
        private static readonly ILogger logger = Log.ForContext<UserPage>();

        private readonly LoginApiClient loginApiClient = new();
        private CancellationTokenSource? qrPollingCts;
        private string? _streamerFolder;

        public UserPage()
        {
            this.InitializeComponent();
            this.InitializeTidComboBox();
            BiliUploadSession.Instance.PropertyChanged += (s, e) =>
                this.Dispatcher.BeginInvoke(new Action(this.RefreshAutoUploadStatus));
            BiliUploadQueue.Instance.PropertyChanged += (s, e) =>
                this.Dispatcher.BeginInvoke(new Action(this.RefreshAutoUploadStatus));
            this.Loaded += this.UserPage_Loaded;
            this.Unloaded += this.UserPage_Unloaded;
        }

        private void UserPage_Loaded(object sender, RoutedEventArgs e)
        {
            this.RefreshLoginState();
        }

        private void UserPage_Unloaded(object sender, RoutedEventArgs e)
        {
            this.StopQrPolling();
        }

        private void RefreshLoginState()
        {
            var session = UserSessionStore.Load();
            if (session is not null)
            {
                this.ShowLoggedIn(session);
                this.StopQrPolling();
            }
            else
            {
                this.ShowNotLoggedIn();
                _ = this.RefreshQrCodeAsync();
            }
        }

        private void ShowLoggedIn(UserSession session)
        {
            this.NotLoggedInPanel.Visibility = Visibility.Collapsed;
            this.LoggedInPanel.Visibility = Visibility.Visible;

            this.UsernameText.Text = session.Username;
            this.UidText.Text = "UID: " + session.Uid;
            _ = this.LoadAvatarAsync(session.AvatarUrl);

            this.UpdateRootPageIcon();

            this.AutoUploadBox.IsChecked = BiliUploadSettings.AutoUploadEnabled;
            this.ManualPanel.Visibility = BiliUploadSettings.AutoUploadEnabled ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ShowNotLoggedIn()
        {
            this.NotLoggedInPanel.Visibility = Visibility.Visible;
            this.LoggedInPanel.Visibility = Visibility.Collapsed;
        }

        private async Task LoadAvatarAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
                client.DefaultRequestHeaders.Referrer = new Uri("https://www.bilibili.com/");
                var bytes = await client.GetByteArrayAsync(url).ConfigureAwait(false);

                await this.Dispatcher.InvokeAsync(() =>
                {
                    var image = new BitmapImage();
                    using (var ms = new MemoryStream(bytes))
                    {
                        image.BeginInit();
                        image.CacheOption = BitmapCacheOption.OnLoad;
                        image.StreamSource = ms;
                        image.EndInit();
                    }
                    image.Freeze();
                    this.AvatarImage.Source = image;
                });
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "加载用户头像失败");
            }
        }

        private async Task RefreshQrCodeAsync()
        {
            this.StopQrPolling();
            this.QrLoading.IsActive = true;
            this.QrLoading.Visibility = Visibility.Visible;
            this.QrExpiredOverlay.Visibility = Visibility.Collapsed;
            this.QrStatusText.Text = "正在加载二维码...";

            try
            {
                var (url, key) = await this.loginApiClient.GenerateQrCodeAsync().ConfigureAwait(true);
                this.RenderQrCode(url);

                this.QrLoading.IsActive = false;
                this.QrLoading.Visibility = Visibility.Collapsed;
                this.QrStatusText.Text = "请使用哔哩哔哩手机客户端扫码";

                this.StartQrPolling(key);
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "生成二维码失败");
                this.QrLoading.IsActive = false;
                this.QrLoading.Visibility = Visibility.Collapsed;
                this.QrStatusText.Text = "生成二维码失败：" + ex.Message;
            }
        }

        private void RenderQrCode(string url)
        {
            var generator = new QRCodeGenerator();
            var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
            var png = new PngByteQRCode(data);
            var bytes = png.GetGraphic(10);

            var image = new BitmapImage();
            using (var ms = new MemoryStream(bytes))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = ms;
                image.EndInit();
            }
            image.Freeze();
            this.QrImage.Source = image;
        }

        private void StartQrPolling(string key)
        {
            this.qrPollingCts = new CancellationTokenSource();
            var token = this.qrPollingCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        await Task.Delay(2000, token).ConfigureAwait(false);
                        if (token.IsCancellationRequested) return;

                        var result = await this.loginApiClient.PollQrCodeAsync(key).ConfigureAwait(false);

                        await this.Dispatcher.InvokeAsync(() =>
                        {
                            if (result.IsWaiting)
                            {
                                this.QrStatusText.Text = "等待扫码";
                            }
                            else if (result.IsScanned)
                            {
                                this.QrStatusText.Text = "已扫码，请在手机上确认";
                            }
                        });

                        if (result.IsExpired)
                        {
                            await this.Dispatcher.InvokeAsync(() =>
                            {
                                this.QrStatusText.Text = "二维码已失效";
                                this.QrExpiredOverlay.Visibility = Visibility.Visible;
                            });
                            return;
                        }

                        if (result.IsSuccess)
                        {
                            await this.HandleLoginSuccessAsync(result.Cookie).ConfigureAwait(false);
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    logger.Warning(ex, "轮询二维码状态失败");
                }
            }, token);
        }

        private async Task HandleLoginSuccessAsync(string cookie)
        {
            try
            {
                var userInfo = await this.loginApiClient.GetUserInfoAsync(cookie).ConfigureAwait(false);

                var session = new UserSession
                {
                    Uid = userInfo.Uid,
                    Username = userInfo.Username,
                    AvatarUrl = userInfo.AvatarUrl,
                    Cookie = cookie,
                    LoginTime = DateTimeOffset.Now,
                };
                UserSessionStore.Save(session);
                this.ApplySessionToConfig(session);

                await this.Dispatcher.InvokeAsync(() =>
                {
                    this.ShowLoggedIn(session);
                });
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "登录失败");
                await this.Dispatcher.InvokeAsync(() =>
                {
                    this.QrStatusText.Text = "登录失败：" + ex.Message;
                });
            }
        }

        private void StopQrPolling()
        {
            try { this.qrPollingCts?.Cancel(); } catch { }
            try { this.qrPollingCts?.Dispose(); } catch { }
            this.qrPollingCts = null;
        }

        private void RefreshQr_Click(object sender, RoutedEventArgs e)
        {
            _ = this.RefreshQrCodeAsync();
        }

        private void Logout_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("确定要退出登录吗？", "退出登录",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
                return;

            UserSessionStore.Clear();
            this.ClearSessionFromConfig();
            this.AvatarImage.Source = null;
            this.ShowNotLoggedIn();
            _ = this.RefreshQrCodeAsync();
            this.UpdateRootPageIcon();
        }

        private void UpdateRootPageIcon()
        {
            try
            {
                if (Application.Current.MainWindow is NewMainWindow mw && mw.RootPage is RootPage root)
                {
                    root.UpdateUserIcon();
                }
            }
            catch { }
        }

        // ============================================================
        // 登录成功后应用会话到 GlobalConfig
        // ============================================================
        private void ApplySessionToConfig(UserSession session)
        {
            try
            {
                var config = RootPage.ServiceProvider?.GetService(typeof(Gekota.Core.Config.V3.GlobalConfig)) as Gekota.Core.Config.V3.GlobalConfig;
                var recorder = RootPage.ServiceProvider?.GetService(typeof(Gekota.Core.IRecorder)) as Gekota.Core.IRecorder;
                if (config is null) return;

                config.Cookie = session.Cookie;
                config.RecordingQuality = "10000"; // 1080p 原画(高帧率)
                recorder?.SaveConfig();

                logger.Information("登录后已自动设置 Cookie 与画质为 1080p 原画(高帧率)");
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "应用登录会话到配置失败");
            }
        }

        private void ClearSessionFromConfig()
        {
            try
            {
                var config = RootPage.ServiceProvider?.GetService(typeof(Gekota.Core.Config.V3.GlobalConfig)) as Gekota.Core.Config.V3.GlobalConfig;
                var recorder = RootPage.ServiceProvider?.GetService(typeof(Gekota.Core.IRecorder)) as Gekota.Core.IRecorder;
                if (config is null) return;

                config.Cookie = string.Empty;
                config.RecordingQuality = "250"; // 退回 720p 超清
                recorder?.SaveConfig();

                logger.Information("已清除登录 Cookie，画质退回 720p 超清");
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "清除登录会话时出错");
            }
        }

        // ============================ 自动上传状态 ============================

        private void RefreshAutoUploadStatus()
        {
            try
            {
                var s = BiliUploadSession.Instance;
                this.AutoUploadStatusPanel.Visibility = this.AutoUploadBox.IsChecked == true
                    ? Visibility.Visible : Visibility.Collapsed;

                this.AutoUploadTitleText.Text = string.IsNullOrEmpty(s.Title) ? "（等待录播结束）" : s.Title;
                var q = BiliUploadQueue.Instance;
                if (!s.IsRunning && q.PendingCount > 0)
                    this.AutoUploadStageText.Text = "排队中（前面还有 " + q.PendingCount + " 个任务）";
                else
                    this.AutoUploadStageText.Text = string.IsNullOrEmpty(s.Stage) ? "空闲" : s.Stage;
                this.AutoUploadProgress.Value = s.Percent;
                this.AutoUploadResultText.Text = s.Result;
                this.AutoUploadResultText.Foreground = s.IsError
                    ? System.Windows.Media.Brushes.Red
                    : System.Windows.Media.Brushes.ForestGreen;
            }
            catch { }
        }

        private void ClearUploadStatus_Click(object sender, RoutedEventArgs e)
        {
            BiliUploadSession.Instance.Reset();
            this.RefreshAutoUploadStatus();
        }

        // ============================ B站上传 ============================

        private sealed class TidOption
        {
            public string DisplayName { get; set; } = string.Empty;
            public int Tid { get; set; }
            public override string ToString() => this.DisplayName;
        }

        private static readonly TidOption[] TidOptions = new[]
        {
            new TidOption { DisplayName = "自动匹配（推荐）", Tid = 0 },
            new TidOption { DisplayName = "娱乐", Tid = 5 },
            new TidOption { DisplayName = "音乐", Tid = 3 },
            new TidOption { DisplayName = "单机游戏", Tid = 17 },
            new TidOption { DisplayName = "网络游戏", Tid = 20 },
            new TidOption { DisplayName = "手机游戏", Tid = 21 },
            new TidOption { DisplayName = "生活", Tid = 160 },
        };

        private string? _coverPath;

        private void InitializeTidComboBox()
        {
            this.TidComboBox.ItemsSource = TidOptions;
            this.TidComboBox.SelectedIndex = 0;
        }

        private void AutoUploadBox_Changed(object sender, RoutedEventArgs e)
        {
            var auto = this.AutoUploadBox.IsChecked == true;
            this.ManualPanel.Visibility = auto ? Visibility.Collapsed : Visibility.Visible;
            this.AutoUploadStatusPanel.Visibility = auto ? Visibility.Visible : Visibility.Collapsed;
            this.RefreshAutoUploadStatus();
            BiliUploadSettings.AutoUploadEnabled = auto;
        }

        private void PickRecording_Click(object sender, RoutedEventArgs e)
        {
            var d = new Microsoft.Win32.OpenFileDialog { Title = "选择录播文件", Filter = "录播文件|*.flv;*.mp4" };
            if (d.ShowDialog() == true)
            {
                this.PickedFileText.Text = d.FileName;
                this.TryAutoFillInfo(d.FileName);
                this.UpdateUploadPreview();
                _ = this.LoadCoverPreviewAsync(d.FileName);
            }
        }

        private async Task LoadCoverPreviewAsync(string videoPath)
        {
            try
            {
                // 优先用直播文件夹里的截屏
                var titleFolder = Path.GetDirectoryName(videoPath) ?? string.Empty;
                var shot = FindScreenshotLocal(titleFolder);
                if (shot != null)
                {
                    this._coverPath = shot;
                    this.ShowCoverPreview(shot);
                    return;
                }

                var cover = await VideoCoverExtractor.ExtractKeyframeAsync(videoPath).ConfigureAwait(true);
                if (cover != null)
                {
                    this._coverPath = null; // 自动抽帧
                    this.ShowCoverPreview(cover);
                }
            }
            catch (Exception ex) { logger.Debug(ex, "[上传] 封面预览失败"); }
        }

        private void PickCover_Click(object sender, RoutedEventArgs e)
        {
            var d = new Microsoft.Win32.OpenFileDialog { Title = "选择封面", Filter = "图片|*.jpg;*.jpeg;*.png;*.webp" };
            if (d.ShowDialog() == true)
            {
                this._coverPath = d.FileName;
                this.ShowCoverPreview(d.FileName);
            }
        }

        private void ShowCoverPreview(string path)
        {
            try { this.CoverPreview.Source = new BitmapImage(new Uri(path)); }
            catch { this.CoverPreview.Source = null; }
        }

        private void TryAutoFillInfo(string path)
        {
            try
            {
                var titleFolder = Path.GetDirectoryName(path);
                var streamerFolder = titleFolder == null ? null : Path.GetDirectoryName(titleFolder);
                this._streamerFolder = streamerFolder;

                var streamerName = string.Empty;
                if (streamerFolder != null)
                {
                    var folderName = Path.GetFileName(streamerFolder);
                    var dash = folderName.IndexOf('-');
                    if (dash > 0) streamerName = folderName.Substring(dash + 1);
                }
                var liveTitle = titleFolder == null ? string.Empty : Path.GetFileName(titleFolder);
                var date = File.GetLastWriteTime(path).ToString("yyyy年MM月dd日");
                this.TitleBox.Text = "【" + streamerName + "】" + date + " " + liveTitle;

                var tags = streamerFolder == null ? null : StreamerTagPresetStore.Get(streamerFolder);
                if (!string.IsNullOrEmpty(tags)) this.TagsBox.Text = tags;

                var desc = streamerFolder == null ? string.Empty : ReadDescTxtLocal(streamerFolder);
                if (!string.IsNullOrEmpty(desc)) this.DescBox.Text = desc;
            }
            catch { }
        }

        private void TitleBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            this.TitleCountText.Text = (this.TitleBox.Text?.Length ?? 0) + "/80";
        }

        /// <summary>显示实际会上传的文件（与 BiliUploadOrchestrator 取文件逻辑一致）。</summary>
        private void UpdateUploadPreview()
        {
            try
            {
                var flv = this.PickedFileText.Text;
                if (string.IsNullOrWhiteSpace(flv))
                {
                    this.UploadFilesText.Text = "（未选择文件）";
                    return;
                }

                var dir = Path.GetDirectoryName(flv) ?? string.Empty;
                var baseName = Path.GetFileNameWithoutExtension(flv);
                var clean = Path.Combine(dir, baseName + "【纯净版】.mp4");
                var danmaku = Path.Combine(dir, baseName + "【弹幕版】.mp4");
                var streamerDir = Path.GetDirectoryName(dir) ?? dir;
                var fangzhuang = FindFangzhuangLocal(streamerDir);

                var sb = new System.Text.StringBuilder();
                sb.AppendLine((File.Exists(clean) ? "✔ 纯净版  " : "✘ 纯净版  ") + Path.GetFileName(clean));
                sb.AppendLine((File.Exists(danmaku) ? "✔ 弹幕版  " : "✘ 弹幕版  ") + Path.GetFileName(danmaku));
                sb.AppendLine(fangzhuang != null
                    ? "✔ 防撞    " + Path.GetFileName(fangzhuang)
                    : "✘ 防撞    （主播目录下未找到）");
                this.UploadFilesText.Text = sb.ToString().TrimEnd();
            }
            catch { }
        }

        private static string? FindFangzhuangLocal(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return null;
                foreach (var f in Directory.EnumerateFiles(folder, "*.mp4", SearchOption.TopDirectoryOnly))
                    if (Path.GetFileName(f).Contains("防撞")) return f;
            }
            catch { }
            return null;
        }

        private static string ReadDescTxtLocal(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return string.Empty;
                var path = Path.Combine(folder, "简介.txt");
                return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
            }
            catch { }
            return string.Empty;
        }

        private static string? FindScreenshotLocal(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return null;
                string? best = null;
                var bestTime = DateTime.MinValue;
                foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".webp") continue;
                    var name = Path.GetFileName(f);
                    if (name.IndexOf("screenshot", StringComparison.OrdinalIgnoreCase) < 0
                        && !name.StartsWith("截图", StringComparison.Ordinal)) continue;
                    var t = File.GetLastWriteTimeUtc(f);
                    if (t > bestTime) { bestTime = t; best = f; }
                }
                return best;
            }
            catch { }
            return null;
        }

        private void Copyright_Changed(object sender, RoutedEventArgs e)
        {
            if (this.SourcePanel == null) return;
            this.SourcePanel.Visibility = this.CopyrightZhuanzaiRadio.IsChecked == true
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SaveTags_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(this._streamerFolder))
            {
                MessageBox.Show("请先选择录播文件以确定主播文件夹", "标签预设", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            StreamerTagPresetStore.Set(this._streamerFolder!, this.TagsBox.Text.Trim());
            MessageBox.Show("标签预设已保存到主播文件夹", "标签预设", MessageBoxButton.OK, MessageBoxImage.Information);
        }

#pragma warning disable VSTHRD100
        private async void ManualUpload_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            try
            {
                var flv = this.PickedFileText.Text;
                if (string.IsNullOrWhiteSpace(flv) || !File.Exists(flv))
                {
                    MessageBox.Show("请先选择录播文件", "B站上传", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var overrides = new UploadOverrides
                {
                    Title = this.TitleBox.Text.Trim(),
                    Desc = this.DescBox.Text,
                    Tags = this.TagsBox.Text.Trim(),
                    Tid = (this.TidComboBox.SelectedItem as TidOption)?.Tid ?? 0,
                    CoverPath = this._coverPath,
                    Copyright = this.CopyrightZizhiRadio.IsChecked == true ? 1 : 2,
                    Source = this.SourceBox.Text.Trim(),
                    NoReprint = this.NoReprintCheckBox.IsChecked == true,
                    CloseDanmu = this.CloseDanmuCheckBox.IsChecked == true,
                    CloseReply = this.CloseReplyCheckBox.IsChecked == true,
                };

                var info = new RecordingInfo { FlvPath = flv };
                await this.RunUploadAsync(info, overrides);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "[上传] 手动上传出错");
                this.UploadStatusText.Text = "上传出错: " + ex.Message;
            }
        }

        private async Task RunUploadAsync(RecordingInfo info, UploadOverrides? overrides = null)
        {
            var session = UserSessionStore.Load();
            if (session == null) { MessageBox.Show("请先登录B站", "B站上传", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            this.UploadProgress.Visibility = Visibility.Visible;
            this.UploadProgress.Value = 0;
            this.UploadStatusText.Text = "准备上传...";
            var progress = new Progress<string>(s => this.UploadStatusText.Text = s);
            try
            {
                var bvid = await new BiliUploadOrchestrator().UploadAsync(info, session.Cookie, progress, CancellationToken.None, overrides);
                this.UploadProgress.Value = 100;
                this.UploadStatusText.Text = "投稿成功: " + bvid;
                MessageBox.Show("投稿成功: " + bvid, "B站上传", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (BiliCaptchaRequiredException)
            {
                this.UploadStatusText.Text = "投稿需要验证码";
            }
            catch (Exception ex)
            {
                logger.Error(ex, "[上传] 上传失败");
                this.UploadStatusText.Text = "上传失败: " + ex.Message;
                MessageBox.Show("上传失败: " + ex.Message, "B站上传", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { this.UploadProgress.Visibility = Visibility.Collapsed; }
        }

    }
}
