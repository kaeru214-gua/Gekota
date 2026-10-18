using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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

        private void MenuItem_ShowGlobalSettings_Click(object sender, RoutedEventArgs e)
        {
            if (RootPage.SwitchToSettingsPage is Action change)
            {
                change();
            }
        }
    }
}
