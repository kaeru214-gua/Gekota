using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Gekota.WPF.Services;
using Microsoft.WindowsAPICodePack.Dialogs;
using Serilog;

#nullable enable
namespace Gekota.WPF.Pages
{
    public partial class ToolboxCompressPage
    {
        private static readonly ILogger logger = Log.ForContext<ToolboxCompressPage>();
        private static readonly string DesktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        private readonly VideoCompressOptions Options = VideoCompressSettings.Current;
        private readonly ObservableCollection<BitrateBar> Bars = new ObservableCollection<BitrateBar>();
        private double maxBitrate;
        private bool paused;

        private static CompressSession Session => CompressSession.Instance;

        public ToolboxCompressPage()
        {
            this.InitializeComponent();
            this.FileList.ItemsSource = Session.Items;
            this.ProgressFileList.ItemsSource = Session.Items;
            this.BitrateBars.ItemsSource = this.Bars;

            Session.PropertyChanged += this.OnSessionChanged;
            Session.Tick += this.OnSessionTick;

            this.Loaded += this.OnLoaded;
            this.Unloaded += this.OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            this.RefreshViewState();
            this.UpdateInputOutput();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            // 页面离开时不取消任何任务，只解除订阅避免泄漏
            Session.PropertyChanged -= this.OnSessionChanged;
            Session.Tick -= this.OnSessionTick;
        }

        private void OnSessionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            this.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!this.IsLoaded) return;
                this.RefreshViewState();
            }));
        }

        /// <summary>压制中时强制显示压制视图。</summary>
        private void RefreshViewState()
        {
            var busy = Session.IsBusy;
            this.ListView.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
            this.ProgressView.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

            if (busy)
            {
                this.ProgressFileName.Text = Session.CurrentName;
                this.ProgressPercentText.Text = "[" + Session.CountText + "] "
                    + Session.OverallPercent.ToString("0.00", CultureInfo.InvariantCulture) + "%";
                this.AllProgress.Value = Session.OverallPercent;
            }
        }

        // ================= 添加 / 移除 =================

        private void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            var d = new CommonOpenFileDialog
            {
                Title = "选择视频（可同时选择字幕）",
                Multiselect = true,
                EnsureFileExists = true,
                EnsurePathExists = true,
                DefaultDirectory = DesktopPath,
            };
            d.Filters.Add(new CommonFileDialogFilter("媒体文件", "*.mp4;*.flv;*.mkv;*.ts;*.mov;*.ass;*.srt"));
            if (d.ShowDialog() != CommonFileDialogResult.Ok) return;

            foreach (var path in d.FileNames)
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".ass" || ext == ".srt") Session.AttachSubtitle(path);
                else Session.Add(path);
            }
            this.UpdateInputOutput();
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            if (Session.IsBusy) return;
            foreach (var item in this.FileList.SelectedItems.Cast<CompressFileItem>().ToList())
                Session.Items.Remove(item);
            this.UpdateInputOutput();
        }

        private void RemoveAll_Click(object sender, RoutedEventArgs e)
        {
            Session.RemoveAll();
            this.UpdateInputOutput();
        }

        private void Project_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("项目文件功能尚未实现。", "项目", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void QuickSettings_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Gekota.WPF.Controls.QuickSettingsDialog { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true)
            {
                foreach (var f in Session.Items) f.RaiseAll();
                this.UpdateInputOutput();
            }
        }

        private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) => this.UpdateInputOutput();

        private void UpdateInputOutput()
        {
            var item = this.FileList.SelectedItem as CompressFileItem
                       ?? (Session.Items.Count > 0 ? Session.Items[0] : null);

            if (item == null)
            {
                this.InputInfoText.Text = "";
                this.OutputInfoText.Text = "";
                return;
            }

            this.InputInfoText.Text = item.InputInfoText;
            this.OutputInfoText.Text = item.OutputInfoText;
        }

        // ================= 开始压制 =================

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            if (Session.IsBusy) return;
            if (Session.Items.Count == 0)
            {
                MessageBox.Show("没有待压制的文件。", "视频压制", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            this.Bars.Clear();
            this.maxBitrate = 0;
            this.paused = false;
            this.PauseButton.Content = "暂停";

            Session.Start(this.Options);
            this.RefreshViewState();
        }

        private void OnSessionTick(object? sender, CompressProgress p)
        {
            this.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!this.IsLoaded) return;
                this.UpdateProgressUi(p);
            }));
        }

        private void UpdateProgressUi(CompressProgress p)
        {
            var pct = Math.Min(100.0, Session.OverallPercent);
            this.ProgressPercentText.Text = "[" + Session.CountText + "] "
                + pct.ToString("0.00", CultureInfo.InvariantCulture) + "%";
            this.AllProgress.Value = pct;

            this.DetFrameText.Text = "帧: " + p.Frame + ", 比特率: " + (string.IsNullOrEmpty(p.Bitrate) ? "-" : p.Bitrate);
            this.DetPositionText.Text = MediaProbe.FormatDuration(p.OutTimeSec) + " / " + MediaProbe.FormatDuration(p.TotalDurationSec);
            this.DetSizeText.Text = MediaProbe.FormatSize(p.TotalSizeBytes);
            this.DetSpeedText.Text = p.Speed.ToString("0.00", CultureInfo.InvariantCulture)
                                   + "x (" + p.Fps.ToString("0", CultureInfo.InvariantCulture) + "fps)";

            // 当前文件：按「已编码时长 ÷ 速度」推算已耗时与剩余
            var speed = p.Speed > 0.01 ? p.Speed : 0.01;
            var usedSec = p.OutTimeSec / speed;
            var leftSec = Math.Max(0, (p.TotalDurationSec - p.OutTimeSec) / speed);
            this.DetElapsedText.Text = MediaProbe.FormatDuration(usedSec);
            this.DetRemainText.Text = p.TotalDurationSec > 0 ? MediaProbe.FormatDuration(leftSec) : "--:--";

            this.AllText.Text = "全部: " + Session.CountText + " ("
                + pct.ToString("0.00", CultureInfo.InvariantCulture) + "%) 速度: "
                + p.Speed.ToString("0.00", CultureInfo.InvariantCulture) + "x";
            // 整批任务的已用时长 / 预计总时长
            this.AllElapsedText.Text = Session.TimeText;
            this.AllRemainText.Text = "";

            if (TryParseBitrate(p.Bitrate, out var br) && br > 0)
            {
                if (br > this.maxBitrate) this.maxBitrate = br;
                this.MaxBitrateText.Text = "最大比特率: "
                    + this.maxBitrate.ToString("0.0", CultureInfo.InvariantCulture) + "Kbps";
                this.Bars.Add(new BitrateBar { Height = Math.Min(72.0, br / 20000.0 * 72.0) });
                while (this.Bars.Count > 60) this.Bars.RemoveAt(0);
            }
        }

        private static bool TryParseBitrate(string s, out double v)
        {
            v = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var digits = new string(s.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
            return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        // ================= 控制按钮 =================

        private void Pause_Click(object sender, RoutedEventArgs e)
        {
            this.paused = !this.paused;
            Session.SetPaused(this.paused);
            this.PauseButton.Content = this.paused ? "继续" : "暂停";
        }

        private void Stop_Click(object sender, RoutedEventArgs e) => Session.Stop();

        private void ForceStop_Click(object sender, RoutedEventArgs e) => Session.Stop();

        private void PauseAll_Click(object sender, RoutedEventArgs e) => this.Pause_Click(sender, e);

        private void ForceStopAll_Click(object sender, RoutedEventArgs e) => Session.Stop();

        private void StopAll_Click(object sender, RoutedEventArgs e) => Session.Stop();

        private void CloseProgress_Click(object sender, RoutedEventArgs e)
        {
            if (Session.IsBusy)
            {
                MessageBox.Show("压制尚未结束，请先「全部停止」或等待完成。", "视频压制",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            this.ListView.Visibility = Visibility.Visible;
            this.ProgressView.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>比特率柱状图的一根柱子。</summary>
    public sealed class BitrateBar
    {
        public double Height { get; set; }
    }
}
