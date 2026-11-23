using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Gekota.Core;
using Gekota.WPF.Services;
using LibVLCSharp.Shared;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using Serilog;

#nullable enable
namespace Gekota.WPF.Controls
{
    /// <summary>LibVLC 原生播放窗口：视频由 LibVLC 解码，聊天/Danmaku 由 WPF 绘制。</summary>
    public partial class NativeLiveWindow : Window
    {
        private static readonly ILogger logger = Log.ForContext<NativeLiveWindow>();
        private static readonly Random random = new();
        private readonly IRoom room;
        private readonly LiveStreamProvider provider = new();
        private readonly DanmakuSender sender = new();
        private readonly ObservableCollection<ChatEntry> chat = new();
        private readonly CancellationTokenSource cts = new();

        private LibVLC? libVlc;
        private VlcMediaPlayer? player;
        private bool ready;
        private int requestedQn;
        private bool suppressQuality;
        private DateTime lastSendAt = DateTime.MinValue;

        public NativeLiveWindow(IRoom room)
        {
            this.room = room;
            this.InitializeComponent();
            this.Title = "Gekota 原生播放 - " + room.Name;
            this.TitleText.Text = room.Name;
            this.OverlayTitle.Text = room.Name;
            this.ChatList.ItemsSource = this.chat;
            this.Loaded += this.Window_Loaded;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (this.ready) return;
            this.ready = true;

            try
            {
                LibVLCSharp.Shared.Core.Initialize();
                this.libVlc = new LibVLC("--no-video-title-show", "--quiet");
                this.player = new VlcMediaPlayer(this.libVlc);
                this.Video.MediaPlayer = this.player;
                this.player.Playing += (s, a) => this.SetStatus("直播中");
                this.player.EncounteredError += (s, a) =>
                {
                    this.SetStatus("播放失败，正在重试…");
                    _ = this.RetryAsync();
                };

                this.room.DanmakuReceived += this.Room_DanmakuReceived;
                this.ChatState.Text = this.room.DanmakuConnected ? "已连接" : "连接中…";

                await this.LoadQualitiesAsync();
                await this.PlayAsync(0);
                this.SendState.Text = this.sender.IsReady ? "已登录，可发送弹幕" : "未登录，无法发送弹幕";
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "[原生播放] 初始化失败");
                this.SetStatus("LibVLC 初始化失败：" + ex.Message);
            }
        }

        private async Task LoadQualitiesAsync()
        {
            try
            {
                var list = await this.provider.GetQualitiesAsync(this.room.RoomConfig.RoomId, this.cts.Token)
                    .ConfigureAwait(false);
                this.Dispatcher.Invoke(() =>
                {
                    this.suppressQuality = true;
                    this.QualityBox.Items.Clear();
                    this.QualityBox.Items.Add(new QualityChoice(0, "自动"));
                    foreach (var q in list) this.QualityBox.Items.Add(new QualityChoice(q.Qn, q.Desc));
                    this.QualityBox.SelectedIndex = 0;
                    this.suppressQuality = false;
                });
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "[原生播放] 获取画质失败");
            }
        }

        private async Task PlayAsync(int qn)
        {
            if (this.player == null || this.libVlc == null) return;
            this.requestedQn = qn;
            this.SetStatus("连接中…");

            var info = await this.provider.GetAsync(
                this.room.RoomConfig.RoomId, qn > 0 ? qn : (int?)null, this.cts.Token).ConfigureAwait(false);

            this.Dispatcher.Invoke(() =>
            {
                if (this.player == null || this.libVlc == null) return;
                this.player.Stop();
                var media = new Media(this.libVlc, new Uri(info.Url));
                media.AddOption(":http-referrer=https://live.bilibili.com/" + this.room.RoomConfig.RoomId);
                media.AddOption(":http-user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/132.0.0.0 Safari/537.36");
                media.AddOption(":network-caching=800");
                media.AddOption(":live-caching=800");
                this.player.Play(media);
                this.SetStatus("直播中");
            });
        }

        private async Task RetryAsync()
        {
            await Task.Delay(6000, this.cts.Token).ConfigureAwait(false);
            if (this.cts.IsCancellationRequested) return;
            try { await this.PlayAsync(this.requestedQn).ConfigureAwait(false); }
            catch (Exception ex) { logger.Debug(ex, "[原生播放] 重试失败"); }
        }

        private void SetStatus(string text)
        {
            if (!this.Dispatcher.CheckAccess())
            {
                _ = this.Dispatcher.BeginInvoke(new Action(() => this.SetStatus(text)));
                return;
            }
            this.StatusText.Text = text;
        }

        private void Room_DanmakuReceived(object? sender, RoomDanmakuEventArgs e)
        {
            _ = this.Dispatcher.BeginInvoke(new Action(() =>
            {
                this.chat.Add(new ChatEntry(e.UserName, e.Text));
                while (this.chat.Count > 120) this.chat.RemoveAt(0);
                this.ChatList.ScrollIntoView(this.chat[this.chat.Count - 1]);
                this.FlyDanmaku(e.Text);
            }));
        }

        private void FlyDanmaku(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || this.DanmakuCanvas.ActualWidth <= 0) return;

            var block = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 4,
                    ShadowDepth = 1,
                    Opacity = 0.9,
                },
            };
            this.DanmakuCanvas.Children.Add(block);
            block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var width = Math.Max(1, block.DesiredSize.Width);
            var height = this.DanmakuCanvas.ActualHeight;
            Canvas.SetTop(block, 8 + random.NextDouble() * Math.Max(1, height * 0.6));
            var transform = new TranslateTransform(this.DanmakuCanvas.ActualWidth, 0);
            block.RenderTransform = transform;

            var animation = new DoubleAnimation(
                this.DanmakuCanvas.ActualWidth,
                -width - 20,
                TimeSpan.FromSeconds(9 + random.NextDouble() * 2));
            animation.Completed += (s, e) => this.DanmakuCanvas.Children.Remove(block);
            transform.BeginAnimation(TranslateTransform.XProperty, animation);
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            await this.SendAsync();
        }

        private async void ChatInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await this.SendAsync();
            }
        }

        private async Task SendAsync()
        {
            var text = (this.ChatInput.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text)) return;
            if (DateTime.UtcNow - this.lastSendAt < TimeSpan.FromSeconds(2.5))
            {
                this.SendState.Text = "发送太快了，请稍等…";
                return;
            }

            this.lastSendAt = DateTime.UtcNow;
            this.SendButton.IsEnabled = false;
            this.SendState.Text = "发送中…";
            try
            {
                var result = await this.sender.SendAsync(
                    this.room.RoomConfig.RoomId, text, this.cts.Token).ConfigureAwait(false);
                this.Dispatcher.Invoke(() =>
                {
                    if (result.Ok)
                    {
                        this.ChatInput.Text = "";
                        this.chat.Add(new ChatEntry("我", text));
                        this.FlyDanmaku(text);
                        this.SendState.Text = "已发送 ✓";
                    }
                    else
                    {
                        this.SendState.Text = result.Message;
                    }
                });
            }
            catch (Exception ex)
            {
                this.SendState.Text = "发送失败：" + ex.Message;
            }
            finally
            {
                await Task.Delay(2500).ConfigureAwait(false);
                this.Dispatcher.Invoke(() => this.SendButton.IsEnabled = true);
            }
        }

        private async void QualityBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (this.suppressQuality || this.QualityBox.SelectedItem is not QualityChoice choice) return;
            try { await this.PlayAsync(choice.Qn).ConfigureAwait(false); }
            catch (Exception ex) { logger.Debug(ex, "[原生播放] 切换画质失败"); }
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.player == null) return;
            if (this.player.IsPlaying) this.player.Pause();
            else this.player.Play();
        }

        private void FullscreenButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = this.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            try { this.room.DanmakuReceived -= this.Room_DanmakuReceived; } catch { }
            try { this.cts.Cancel(); } catch { }
            try { this.player?.Stop(); } catch { }
            try { this.player?.Dispose(); } catch { }
            try { this.libVlc?.Dispose(); } catch { }
            this.player = null;
            this.libVlc = null;
        }

        private sealed class QualityChoice
        {
            public QualityChoice(int qn, string desc)
            {
                this.Qn = qn;
                this.Desc = desc;
            }

            public int Qn { get; }
            public string Desc { get; }

            public override string ToString() => this.Desc;
        }

        public sealed class ChatEntry
        {
            public ChatEntry(string user, string text)
            {
                this.User = user;
                this.Text = text;
            }

            public string User { get; }
            public string Text { get; }
        }
    }
}
