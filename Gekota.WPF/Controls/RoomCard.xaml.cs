using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Gekota.Core;
using Gekota.WPF.Pages;
using Newtonsoft.Json.Linq;
using Serilog;

#nullable enable
namespace Gekota.WPF.Controls
{
    /// <summary>
    /// Interaction logic for RoomCard.xaml
    /// </summary>
    public partial class RoomCard : UserControl
    {
        private static readonly ILogger logger = Log.ForContext<RoomCard>();

        private static readonly HttpClient coverHttpClient;

        private string? lastCoverUrl;
        private DispatcherTimer? retryTimer;
        private int retryCount;
        private const int MaxRetry = 30; // 最多重试 30 次（约 60 秒）

        static RoomCard()
        {
            coverHttpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10),
            };
            coverHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
            coverHttpClient.DefaultRequestHeaders.Referrer = new Uri("https://www.bilibili.com/");
        }

        public RoomCard()
        {
            this.InitializeComponent();
            this.DataContextChanged += this.RoomCard_DataContextChanged;
            this.Loaded += this.RoomCard_Loaded;
            this.Unloaded += this.RoomCard_Unloaded;
        }

        public event EventHandler? DeleteRequested;

        public event EventHandler? ShowSettingsRequested;

        public event EventHandler<IRoom>? OpenRequested;

        private void CardRoot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (IsInteractiveElement(e.OriginalSource as DependencyObject)) return;
            if (this.DataContext is not IRoom room) return;

            logger.Debug("[卡片] 点击打开直播间 {RoomId}", room.RoomConfig.RoomId);
            e.Handled = true;
            this.OpenRequested?.Invoke(this, room);
        }

        private void CardRoot_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is not Border border) return;
            if (border.ActualWidth <= 0 || border.ActualHeight <= 0) return;

            // Border.CornerRadius 不会裁剪子元素（封面 Image 仍会画出方角），
            // 这里用圆角矩形 Clip 强制把整张卡片裁成圆角。
            border.Clip = new RectangleGeometry(
                new Rect(0, 0, border.ActualWidth, border.ActualHeight), 21, 21);
        }

        private static bool IsInteractiveElement(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is ButtonBase
                    || source is MenuItem
                    || source is Menu
                    || source is TextBoxBase
                    || source is ScrollBar
                    || source is Slider)
                {
                    return true;
                }

                source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
            }

            return false;
        }

        private void RoomCard_Loaded(object sender, RoutedEventArgs e)
        {
            this.StartCoverLoad();
        }

        private void RoomCard_Unloaded(object sender, RoutedEventArgs e)
        {
            try { this.retryTimer?.Stop(); } catch { }
        }

        private void RoomCard_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // 解除旧订阅
            if (e.OldValue is IRoom oldRoom)
            {
                oldRoom.ClipStarted -= this.Room_ClipStarted;
                oldRoom.ClipEnded -= this.Room_ClipEnded;
            }

            // 重置封面状态
            this.lastCoverUrl = null;
            this.CoverImage.Source = null;

            if (e.NewValue is IRoom newRoom)
            {
                newRoom.ClipStarted += this.Room_ClipStarted;
                newRoom.ClipEnded += this.Room_ClipEnded;
                this.UpdateClipIconState(newRoom);
                this.StartCoverLoad();
            }
        }

        private void Room_ClipStarted(object? sender, Gekota.Core.Event.ClipEventArgs e)
        {
            _ = this.Dispatcher.InvokeAsync(() =>
            {
                // 颜色由 XAML 绑定 IsClipping 自动更新
            });
        }

        private void Room_ClipEnded(object? sender, Gekota.Core.Event.ClipEventArgs e)
        {
            _ = this.Dispatcher.InvokeAsync(() =>
            {
                // 颜色由 XAML 绑定 IsClipping 自动更新
            });
        }

        private void UpdateClipIconState(IRoom room)
        {
            // 颜色由 XAML 绑定 IsClipping 自动更新
        }

        private void StartCoverLoad()
        {
            // 先尝试一次
            _ = this.TryLoadCoverAsync();

            // 启动重试定时器（针对 RawBilibiliApiJsonData 尚未填充的情况）
            this.retryCount = 0;
            this.retryTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            this.retryTimer.Tick -= this.RetryTimer_Tick;
            this.retryTimer.Tick += this.RetryTimer_Tick;
            this.retryTimer.Start();
        }

        private void RetryTimer_Tick(object? sender, EventArgs e)
        {
            // 已经加载成功 / 达到最大重试次数，停止
            if (this.lastCoverUrl != null || ++this.retryCount > MaxRetry)
            {
                this.retryTimer?.Stop();
                return;
            }

            _ = this.TryLoadCoverAsync();
        }

        private async Task TryLoadCoverAsync()
        {
            if (this.DataContext is not IRoom room) return;

            var raw = room.RawBilibiliApiJsonData;
            if (raw is null)
            {
                logger.Debug("[{RoomId}] RawBilibiliApiJsonData 为 null，等待重试", room.RoomConfig.RoomId);
                return;
            }

            var coverUrl = raw["room_info"]?["cover"]?.ToObject<string>();
            if (string.IsNullOrWhiteSpace(coverUrl))
            {
                logger.Debug("[{RoomId}] 封面 URL 为空，等待重试", room.RoomConfig.RoomId);
                return;
            }

            if (coverUrl == this.lastCoverUrl) return;
            this.lastCoverUrl = coverUrl;

            try
            {
                logger.Debug("[{RoomId}] 开始下载封面 {Url}", room.RoomConfig.RoomId, coverUrl);
                var bytes = await coverHttpClient.GetByteArrayAsync(coverUrl).ConfigureAwait(false);
                logger.Debug("[{RoomId}] 封面已下载 {Bytes} 字节", room.RoomConfig.RoomId, bytes.Length);

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
                    this.CoverImage.Source = image;
                    logger.Debug("[{RoomId}] 封面已设置到 CoverImage", room.RoomConfig.RoomId);
                });

                this.retryTimer?.Stop();
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "[{RoomId}] 加载封面失败 {Url}", room.RoomConfig.RoomId, coverUrl);
                this.lastCoverUrl = null; // 允许重试
            }
        }

        private void MenuItem_StartRecording_Click(object sender, RoutedEventArgs e) => (this.DataContext as IRoom)?.StartRecord();

        private void MenuItem_StopRecording_Click(object sender, RoutedEventArgs e) => (this.DataContext as IRoom)?.StopRecord();

#pragma warning disable VSTHRD110 // Observe result of async calls
        private void MenuItem_RefreshInfo_Click(object sender, RoutedEventArgs e) => (this.DataContext as IRoom)?.RefreshRoomInfoAsync();
#pragma warning restore VSTHRD110 // Observe result of async calls

        private void MenuItem_StartMonitor_Click(object sender, RoutedEventArgs e)
        {
            if (this.DataContext is IRoom room)
                room.RoomConfig.AutoRecord = true;
        }

        private void MenuItem_StopMonitor_Click(object sender, RoutedEventArgs e)
        {
            if (this.DataContext is IRoom room)
                room.RoomConfig.AutoRecord = false;
        }

        private void MenuItem_DeleteRoom_Click(object sender, RoutedEventArgs e) => this.DeleteRequested?.Invoke(this.DataContext, EventArgs.Empty);

        private void MenuItem_ShowSettings_Click(object sender, RoutedEventArgs e) => this.ShowSettingsRequested?.Invoke(this.DataContext, EventArgs.Empty);

        private void Button_Split_Click(object sender, RoutedEventArgs e)
        {
            if (this.DataContext is IRoom room)
                room.ToggleClip();
        }

        private void MenuItem_OpenInBrowser_Click(object sender, RoutedEventArgs e)
        {
            if (this.DataContext is IRoom r && r is not null)
            {
                try
                {
                    System.Diagnostics.Process.Start("https://live.bilibili.com/" + r.RoomConfig.RoomId);
                }
                catch (Exception) { }
            }
        }

        private void MenuItem_EditTags_Click(object sender, RoutedEventArgs e)
        {
            this.EditProfile("标签设置", "每个标签用 ; 隔开，例如：A-SOUL;贝拉;直播", "tag.txt");
        }

        private void MenuItem_EditDescription_Click(object sender, RoutedEventArgs e)
        {
            this.EditProfile("编辑简介", "这段文字会作为自动上传的视频简介。", "简介.txt");
        }

        private void EditProfile(string title, string hint, string fileName)
        {
            if (this.DataContext is not IRoom room) return;
            try
            {
                var folder = GetStreamerFolder(room);
                if (string.IsNullOrWhiteSpace(folder)) return;
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, fileName);
                var initial = File.Exists(path) ? File.ReadAllText(path) : "";
                var dialog = new ProfileEditDialog(title, hint, initial)
                {
                    Owner = Window.GetWindow(this),
                };
                if (dialog.ShowDialog() != true) return;
                File.WriteAllText(path, dialog.Result, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "[主播资料] 保存失败 {File}", fileName);
                MessageBox.Show("保存失败：" + ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static string? GetStreamerFolder(IRoom room)
        {
            var work = RootPage.Current?.Model.Recorder?.Config.Global.WorkDirectory;
            if (string.IsNullOrWhiteSpace(work)) return null;
            var direct = Path.Combine(work, room.RoomConfig.RoomId + "-" + room.Name);
            if (Directory.Exists(direct)) return direct;
            try
            {
                return Directory.EnumerateDirectories(work, room.RoomConfig.RoomId + "-*",
                    SearchOption.TopDirectoryOnly).FirstOrDefault() ?? direct;
            }
            catch
            {
                return direct;
            }
        }

        private void MenuItem_ShowGlobalSettings_Click(object sender, RoutedEventArgs e)
        {
            if (RootPage.SwitchToSettingsPage is Action change)
            {
                change();
            }
        }
    }
}
