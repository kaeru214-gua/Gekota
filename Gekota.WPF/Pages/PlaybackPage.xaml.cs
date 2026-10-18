using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using IOPath = System.IO.Path;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Gekota.Core;
using Gekota.WPF.Services;
using Microsoft.Web.WebView2.Core;
using Serilog;

#nullable enable
namespace Gekota.WPF.Pages
{
    public partial class PlaybackPage
    {
        private static readonly ILogger logger = Log.ForContext<PlaybackPage>();
        private static readonly HttpClient coverHttpClient;

        static PlaybackPage()
        {
            coverHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            coverHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
            coverHttpClient.DefaultRequestHeaders.Referrer = new Uri("https://www.bilibili.com/");
        }

        private sealed class RoomCardUi
        {
            public RoomCardUi(Ellipse dot, TextBlock text, Border selection)
            {
                this.Dot = dot;
                this.Text = text;
                this.Selection = selection;
            }

            public Ellipse Dot { get; }
            public TextBlock Text { get; }
            public Border Selection { get; }
        }

        private IRoom? _selectedRoom;
        private IRoom? _clipWatchedRoom;
        private string? _currentUrl;
        private bool _webViewReady;

        private readonly DispatcherTimer _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        private readonly Dictionary<Guid, RoomCardUi> _cards = new Dictionary<Guid, RoomCardUi>();
        private readonly Dictionary<Guid, BitmapImage> _coverCache = new Dictionary<Guid, BitmapImage>();
        private List<Guid> _stripOrder = new List<Guid>();

        private IRecorder? Recorder => this.DataContext as IRecorder;

        public PlaybackPage()
        {
            this.InitializeComponent();
            this.DataContextChanged += (s, e) => this.SyncStrip();
            this.Loaded += this.OnLoaded;
            this.Unloaded += this.OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            this._refreshTimer.Tick += this.RefreshTimer_Tick;
            this._refreshTimer.Start();
            this.SyncStrip();
            this.AutoSelectFirst();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            this._refreshTimer.Stop();
            this._refreshTimer.Tick -= this.RefreshTimer_Tick;
            if (this._clipWatchedRoom != null)
                this._clipWatchedRoom.PropertyChanged -= this.OnRoomPropertyChanged;
            this._clipWatchedRoom = null;
        }

        // ============================ 状态 ============================

        private void SetStatus(string? text)
        {
            if (!this.Dispatcher.CheckAccess())
            {
                _ = this.Dispatcher.BeginInvoke(new Action(() => this.SetStatus(text)));
                return;
            }
            this.StatusOverlay.Text = text ?? string.Empty;
            this.StatusOverlay.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        // ============================ WebView2 + 登录 Cookie ============================

        private async Task EnsureWebViewAsync()
        {
            if (this._webViewReady) return;

            // ① 放开 Chromium 的自动播放限制（否则带声音的视频会被强制静音）
            var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
            var env = await CoreWebView2Environment.CreateAsync(null, null, options);
            await this.LiveWebView.EnsureCoreWebView2Async(env);

            // ② 取消 WebView 级别静音
            // 注意：CoreWebView2Settings 没有 IsMuted，静音由下方 JS 处理

            // ③ 每次导航完成后尝试把 <video> 取消静音
            this.LiveWebView.CoreWebView2.NavigationCompleted += this.OnNavigationCompleted;

            await this.InjectCookiesAsync();
            this._webViewReady = true;
        }

        private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _ = this.TryUnmuteAsync();
        }

        /// <summary>页面加载后多次尝试取消静音（B 站播放器会记住上次静音状态，需要覆盖）。</summary>
        private async Task TryUnmuteAsync()
        {
            const string js =
                "(function(){try{var v=document.querySelector('video');" +
                "if(v){v.muted=false;if(v.volume===0)v.volume=0.7;}}catch(e){}})()";

            for (var i = 0; i < 6; i++)
            {
                try { await this.LiveWebView.CoreWebView2.ExecuteScriptAsync(js); }
                catch { }
                await Task.Delay(1500);
            }
        }

        /// <summary>把已登录用户的 Cookie 注入 WebView2，让内嵌直播间处于登录状态。</summary>
        private async Task InjectCookiesAsync()
        {
            try
            {
                var session = UserSessionStore.Load();
                if (session == null || string.IsNullOrWhiteSpace(session.Cookie))
                {
                    logger.Information("[实时播放] 未登录，跳过 Cookie 注入");
                    return;
                }

                var cm = this.LiveWebView.CoreWebView2.CookieManager;
                var count = 0;
                foreach (var part in session.Cookie.Split(';'))
                {
                    var idx = part.IndexOf('=');
                    if (idx <= 0) continue;
                    var name = part.Substring(0, idx).Trim();
                    var value = part.Substring(idx + 1).Trim();
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    try
                    {
                        var cookie = cm.CreateCookie(name, value, ".bilibili.com", "/");
                        cm.AddOrUpdateCookie(cookie);
                        count++;
                    }
                    catch { }
                }

                logger.Information("[实时播放] 已注入 {Count} 个登录 Cookie", count);
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "[实时播放] 注入 Cookie 失败");
            }
        }

        // ============================ 房间卡片条 ============================

        private void SyncStrip()
        {
            try
            {
                var rec = this.Recorder;
                if (rec == null)
                {
                    if (this._stripOrder.Count > 0)
                    {
                        this._stripOrder.Clear();
                        this._cards.Clear();
                        this.RoomStrip.Children.Clear();
                    }
                    return;
                }

                var ids = new List<Guid>();
                foreach (var r in rec.Rooms) ids.Add(r.ObjectId);

                var changed = ids.Count != this._stripOrder.Count;
                if (!changed)
                {
                    for (var i = 0; i < ids.Count; i++)
                        if (ids[i] != this._stripOrder[i]) { changed = true; break; }
                }

                if (changed)
                {
                    this._stripOrder = ids;
                    this._cards.Clear();
                    this.RoomStrip.Children.Clear();
                    foreach (var r in rec.Rooms)
                        this.RoomStrip.Children.Add(this.BuildRoomButton(r));
                }
                else
                {
                    foreach (var r in rec.Rooms) this.UpdateCardVisual(r);
                }

                this.HighlightSelection();
            }
            catch (Exception ex) { logger.Warning(ex, "[实时播放] 刷新卡片条失败"); }
        }

        private Button BuildRoomButton(IRoom room)
        {
            var btn = new Button
            {
                Width = 150,
                Height = 96,
                Margin = new Thickness(4, 4, 4, 4),
                Background = new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0)),
                BorderBrush = (Brush)FindResource("SystemControlBackgroundAccentBrush"),
                BorderThickness = new Thickness(1, 1, 1, 1),
                Cursor = System.Windows.Input.Cursors.Hand,
                Focusable = false,
                Tag = room,
            };
            btn.Click += this.RoomShortcut_Click;

            var grid = new Grid();
            var cover = new Image { Stretch = Stretch.UniformToFill, Opacity = 0.85 };
            grid.Children.Add(cover);

            var statusDot = new Ellipse
            {
                Width = 8, Height = 8, Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var statusText = new TextBlock
            {
                Foreground = Brushes.White, FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(statusDot);
            sp.Children.Add(statusText);

            var statusBorder = new Border
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
                Padding = new Thickness(6, 2, 6, 2),
                Child = sp,
            };
            grid.Children.Add(statusBorder);

            var selBorder = new Border
            {
                BorderThickness = new Thickness(2, 2, 2, 2),
                BorderBrush = Brushes.Transparent,
                CornerRadius = new CornerRadius(3),
                IsHitTestVisible = false,
            };
            grid.Children.Add(selBorder);

            btn.Content = grid;
            this._cards[room.ObjectId] = new RoomCardUi(statusDot, statusText, selBorder);

            this.LoadCover(room, cover);
            this.UpdateCardVisual(room);
            return btn;
        }

        private void UpdateCardVisual(IRoom room)
        {
            if (!this._cards.TryGetValue(room.ObjectId, out var ui)) return;
            ui.Dot.Fill = room.Recording ? Brushes.Red : room.Streaming ? Brushes.LimeGreen : Brushes.Gray;
            ui.Text.Text = room.Recording ? "录制中" : room.Streaming ? "直播中" : room.RoomConfig.RoomId.ToString();
        }

        private void LoadCover(IRoom room, Image image)
        {
            if (this._coverCache.TryGetValue(room.ObjectId, out var cached)) { image.Source = cached; return; }
            _ = this.LoadCoverAsync(room, image);
        }

        private async Task LoadCoverAsync(IRoom room, Image image)
        {
            try
            {
                var coverUrl = room.RawBilibiliApiJsonData?["room_info"]?["cover"]?.ToObject<string>();
                if (string.IsNullOrWhiteSpace(coverUrl)) return;

                var bytes = await coverHttpClient.GetByteArrayAsync(coverUrl).ConfigureAwait(false);
                await this.Dispatcher.InvokeAsync(() =>
                {
                    using var ms = new MemoryStream(bytes);
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    this._coverCache[room.ObjectId] = bmp;
                    image.Source = bmp;
                });
            }
            catch (Exception ex) { logger.Debug(ex, "[实时播放] 封面加载失败"); }
        }

        private void RefreshTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                this.SyncStrip();
                this.UpdateClipIcon();
            }
            catch (Exception ex) { logger.Warning(ex, "[实时播放] 定时刷新异常"); }
        }

        // ============================ 选择 / 导航 ============================

        private void AutoSelectFirst()
        {
            var rec = this.Recorder;
            if (rec == null || rec.Rooms.Count == 0) { this.SetStatus("还没有添加房间"); return; }

            IRoom? target = null;
            foreach (var r in rec.Rooms) { if (r.Recording) { target = r; break; } }
            if (target == null) foreach (var r in rec.Rooms) { if (r.Streaming) { target = r; break; } }
            if (target == null) target = rec.Rooms[0];

            this.SelectRoom(target);
        }

        private void RoomShortcut_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is IRoom room) this.SelectRoom(room);
        }

        private void SelectRoom(IRoom room)
        {
            if (this._clipWatchedRoom != null && !ReferenceEquals(this._clipWatchedRoom, room))
                this._clipWatchedRoom.PropertyChanged -= this.OnRoomPropertyChanged;
            this._clipWatchedRoom = room;
            room.PropertyChanged += this.OnRoomPropertyChanged;

            this._selectedRoom = room;
            this.HighlightSelection();
            this.UpdateClipIcon();
            this.NavigateToLive(room);
        }

        /// <summary>内嵌打开 B 站直播间网页（已注入登录 Cookie）。</summary>
#pragma warning disable VSTHRD100
        private async void NavigateToLive(IRoom room)
#pragma warning restore VSTHRD100
        {
            var url = "https://live.bilibili.com/" + room.RoomConfig.RoomId;
            if (this._currentUrl == url) return;
            this._currentUrl = url;

            try
            {
                await this.EnsureWebViewAsync();
                this.LiveWebView.CoreWebView2.Navigate(url);
                this.SetStatus(null);
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "[实时播放] 加载直播间网页失败");
                this.SetStatus("加载直播间网页失败：" + ex.Message);
            }
        }

        // ============================ 剪刀 ============================

        private void OnRoomPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(IRoom.IsClipping) || string.IsNullOrEmpty(e.PropertyName))
                this.UpdateClipIcon();
        }

        private void UpdateClipIcon()
        {
            var clipping = this._selectedRoom != null && this._selectedRoom.IsClipping;
            this.ClipIcon.Foreground = clipping ? Brushes.Red : Brushes.White;
        }

        private void HighlightSelection()
        {
            try
            {
                foreach (var kv in this._cards)
                {
                    var isSel = this._selectedRoom != null && kv.Key == this._selectedRoom.ObjectId;
                    kv.Value.Selection.BorderBrush = isSel
                        ? (Brush)FindResource("SystemControlBackgroundAccentBrush")
                        : Brushes.Transparent;
                }
            }
            catch (Exception ex) { logger.Debug(ex, "[实时播放] 更新选中高亮失败"); }
        }


        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dir = this.ResolveCurrentRecordingFolder();
                if (string.IsNullOrWhiteSpace(dir))
                {
                    logger.Warning("[实时播放] 打开文件夹失败：无当前录制文件");
                    return;
                }

                System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
                logger.Information("[实时播放] 打开录制文件夹 {Dir}", dir);
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "[实时播放] 打开文件夹失败");
            }
        }

        /// <summary>当前录制文件所在目录；未录制时回退到录播根目录。</summary>
        private string? ResolveCurrentRecordingFolder()
        {
            var cur = this._selectedRoom?.CurrentRecordFilePath;
            if (!string.IsNullOrWhiteSpace(cur))
            {
                var dir = IOPath.GetDirectoryName(cur);
                if (!string.IsNullOrWhiteSpace(dir)) return dir;
            }

            try
            {
                if (this.DataContext is Gekota.Core.IRecorder rec)
                {
                    var d = rec.Config.Global.WorkDirectory;
                    if (!string.IsNullOrWhiteSpace(d)) return d;
                }
            }
            catch { }
            return null;
        }

        private const string StreamUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36 Edg/132.0.0.0";

        private async void CaptureButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var file = this.ResolveScreenshotPath();

                // ① 优先：从直播流抓纯净帧（无播放器水印，需正在录制）
                var room = this._selectedRoom;
                if (room != null)
                {
                    string? url = null;
                    try { url = await room.GetLiveStreamUrlAsync(); }
                    catch (Exception ex) { logger.Debug(ex, "[实时播放] 获取直播流地址失败"); }

                    if (!string.IsNullOrWhiteSpace(url) && await this.GrabStreamFrameAsync(url!, file))
                    {
                        logger.Information("[实时播放] 纯净截图已保存 {File}", file);
                        return;
                    }
                }

                // ② 回退：WebView2 截图（可能含播放器水印）
                await this.CaptureWebViewFrameAsync(file);
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "[实时播放] 拍照失败");
            }
        }

        /// <summary>截图保存路径：优先当前录制文件目录（切片同目录），否则回退录播根目录。</summary>
        private string ResolveScreenshotPath()
        {
            var cur = this._selectedRoom?.CurrentRecordFilePath;
            if (!string.IsNullOrWhiteSpace(cur))
            {
                var dir = IOPath.GetDirectoryName(cur);
                var baseName = IOPath.GetFileNameWithoutExtension(cur);
                if (!string.IsNullOrWhiteSpace(dir) && !string.IsNullOrWhiteSpace(baseName))
                    return IOPath.Combine(dir, baseName + "-screenshot-" + DateTime.Now.ToString("HHmmss") + ".png");
            }

            return IOPath.Combine(this.ResolveRecordingFolder(), this.BuildScreenshotName() + ".png");
        }

        private string ResolveRecordingFolder()
        {
            try
            {
                if (this.DataContext is Gekota.Core.IRecorder rec)
                {
                    var dir = rec.Config.Global.WorkDirectory;
                    if (!string.IsNullOrWhiteSpace(dir)) return dir!;
                }
            }
            catch { }
            return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        }

        private string BuildScreenshotName()
        {
            var roomName = this._selectedRoom?.Name ?? "live";
            var invalid = IOPath.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder();
            foreach (var c in roomName)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return sb.ToString() + "_截图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        }

        /// <summary>用 FFmpeg 从直播流抓一帧（纯净，无播放器水印）。</summary>
        private async Task<bool> GrabStreamFrameAsync(string url, string file)
        {
            try
            {
                var ff = Gekota.WPF.Services.VideoCompressService.FfmpegPath;
                if (!File.Exists(ff)) return false;

                var args = "-y -hide_banner -loglevel error -nostats "
                         + "-user_agent \"" + StreamUserAgent + "\" "
                         + "-referer \"https://live.bilibili.com/\" "
                         + "-rw_timeout 10000000 "
                         + "-i \"" + url + "\" -frames:v 1 -q:v 2 \"" + file + "\"";

                var dir = IOPath.GetDirectoryName(file);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

                return await Task.Run(() =>
                {
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = ff,
                            Arguments = args,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardError = true,
                        };
                        using var p = System.Diagnostics.Process.Start(psi);
                        if (p == null) return false;
                        p.StandardError.ReadToEnd();
                        if (!p.WaitForExit(15000))
                        {
                            try { p.Kill(); } catch { }
                            return false;
                        }
                        return p.ExitCode == 0 && File.Exists(file) && new FileInfo(file).Length > 0;
                    }
                    catch { return false; }
                });
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "[实时播放] 直播流抓帧失败");
                return false;
            }
        }

        /// <summary>WebView2 整页截图并裁剪到 video（回退方案，可能含水印）。</summary>
        private async Task CaptureWebViewFrameAsync(string file)
        {
            try
            {
                if (this.LiveWebView.CoreWebView2 == null)
                {
                    logger.Warning("[实时播放] 拍照失败：直播页未就绪");
                    return;
                }

                var raw = "null";
                try
                {
                    const string js =
                        "(function(){var v=document.querySelector('video');if(!v)return 'null';" +
                        "var r=v.getBoundingClientRect();" +
                        "return r.x+','+r.y+','+r.width+','+r.height+','+" +
                        "window.innerWidth+','+window.innerHeight;})()";
                    raw = (await this.LiveWebView.CoreWebView2.ExecuteScriptAsync(js)).Trim();
                }
                catch { }

                using var ms = new MemoryStream();
                await this.LiveWebView.CoreWebView2.CapturePreviewAsync(
                    CoreWebView2CapturePreviewImageFormat.Png, ms);
                ms.Position = 0;
                using var src = new System.Drawing.Bitmap(ms);

                System.Drawing.Bitmap result = src;
                try
                {
                    var rect = raw;
                    if (rect != "null" && rect.Length >= 2 && rect[0] == '"' && rect[rect.Length - 1] == '"')
                        rect = rect.Substring(1, rect.Length - 2);

                    var parts = rect.Split(',');
                    if (parts.Length >= 6
                        && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var cx)
                        && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var cy)
                        && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var cw)
                        && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var ch)
                        && double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var vw)
                        && double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var vh)
                        && cw > 1 && ch > 1 && vw > 1 && vh > 1)
                    {
                        // 截图物理像素 ÷ CSS 视口尺寸 = DPI × 缩放比例
                        var scaleX = src.Width / vw;
                        var scaleY = src.Height / vh;
                        var x = Math.Max(0, (int)(cx * scaleX));
                        var y = Math.Max(0, (int)(cy * scaleY));
                        var w = Math.Min((int)(cw * scaleX), src.Width - x);
                        var h = Math.Min((int)(ch * scaleY), src.Height - y);
                        if (w > 0 && h > 0)
                            result = src.Clone(new System.Drawing.Rectangle(x, y, w, h), src.PixelFormat);
                    }
                }
                catch (Exception ex) { logger.Debug(ex, "[实时播放] 裁剪失败，保存整页"); }

                var dir = IOPath.GetDirectoryName(file);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                result.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                if (!ReferenceEquals(result, src)) result.Dispose();

                logger.Information("[实时播放] 截图已保存 {File}", file);
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "[实时播放] 截图失败");
            }
        }

        private void ClipButton_Click(object sender, RoutedEventArgs e)
        {
            this._selectedRoom?.ToggleClip();
        }
    }
}
