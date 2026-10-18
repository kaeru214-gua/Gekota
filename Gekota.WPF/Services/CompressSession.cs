using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Serilog;

#nullable enable
namespace Gekota.WPF.Services
{
    /// <summary>统一压制会话（手动 + 自动），独立于页面运行，切换页面不中断。</summary>
    public sealed class CompressSession : INotifyPropertyChanged
    {
        public static CompressSession Instance { get; } = new CompressSession();

        private static readonly ILogger logger = Log.ForContext(typeof(CompressSession));

        private readonly object sync = new object();
        private readonly List<TaskCompletionSource<bool>> idleWaiters = new List<TaskCompletionSource<bool>>();

        private CancellationTokenSource? cts;
        private bool running;
        private int total;
        private int done;
        private double currentPercent;
        private DateTime startedUtc;
        private CompressProgress progress = new CompressProgress();
        private string currentName = "";

        /// <summary>待压制文件列表（跨页面保留）。</summary>
        public ObservableCollection<CompressFileItem> Items { get; } = new ObservableCollection<CompressFileItem>();

        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler<CompressProgress>? Tick;

        public bool IsBusy { get { lock (sync) return running; } }
        public CompressProgress Progress { get { lock (sync) return progress; } }
        public string CurrentName { get { lock (sync) return currentName; } }

        public string CountText
        {
            get { lock (sync) return total == 0 ? "" : Math.Min(done + 1, total) + "/" + total; }
        }

        public double OverallPercent
        {
            get
            {
                lock (sync)
                {
                    if (total == 0) return 0;
                    return (done + currentPercent / 100.0) / total * 100.0;
                }
            }
        }

        public string TimeText
        {
            get
            {
                lock (sync)
                {
                    if (!running) return "";
                    var elapsed = DateTime.UtcNow - startedUtc;
                    var pct = total == 0 ? 0 : (done + currentPercent / 100.0) / total * 100.0;
                    var totalSec = pct > 0 ? elapsed.TotalSeconds * 100.0 / pct : 0;
                    return "已压制 " + Fmt(elapsed.TotalSeconds)
                         + " / 预计 " + (pct > 0 ? Fmt(totalSec) : "--:--");
                }
            }
        }

        // ================= 添加 =================

        /// <summary>手动添加视频（自动挂同名字幕）。</summary>
        public void Add(string path)
        {
            if (!File.Exists(path)) return;
            if (this.Items.Any(x => string.Equals(x.FilePath, path, StringComparison.OrdinalIgnoreCase))) return;

            var item = new CompressFileItem(path);
            foreach (var ext in new[] { ".ass", ".srt" })
            {
                var sub = Path.ChangeExtension(path, ext);
                if (File.Exists(sub))
                {
                    item.SubtitlePath = sub;
                    item.SubtitleText = ext.TrimStart('.').ToUpperInvariant();
                    break;
                }
            }
            this.AddItem(item);
        }

        /// <summary>自动压制：显式指定字幕（null = 纯净版）。</summary>
        public void AddForAutoCompress(string videoPath, string? subtitlePath)
        {
            if (!File.Exists(videoPath)) return;
            var item = new CompressFileItem(videoPath);
            if (!string.IsNullOrWhiteSpace(subtitlePath) && File.Exists(subtitlePath))
            {
                item.SubtitlePath = subtitlePath;
                item.SubtitleText = Path.GetExtension(subtitlePath).TrimStart('.').ToUpperInvariant();
            }
            this.AddItem(item);
        }

        public void AttachSubtitle(string subtitlePath)
        {
            var ext = Path.GetExtension(subtitlePath).TrimStart('.').ToUpperInvariant();
            foreach (var it in this.Items)
            {
                if (string.Equals(Path.GetFileNameWithoutExtension(it.FilePath),
                                  Path.GetFileNameWithoutExtension(subtitlePath),
                                  StringComparison.OrdinalIgnoreCase))
                {
                    it.SubtitlePath = subtitlePath;
                    it.SubtitleText = ext;
                    return;
                }
            }
        }

        public void RemoveAll() { if (!this.IsBusy) this.Items.Clear(); }

        private void AddItem(CompressFileItem item)
        {
            var app = Application.Current;
            if (app != null && !app.Dispatcher.CheckAccess())
                app.Dispatcher.Invoke(() => { lock (sync) this.Items.Add(item); });
            else
                lock (sync) this.Items.Add(item);
            Notify();
        }

        // ================= 启动 / 运行 =================

        /// <summary>确保循环运行；已在运行时，新加入的项会被自动拾取。</summary>
        public void Start(VideoCompressOptions options)
        {
            lock (sync)
            {
                if (running) return;
                if (this.Items.All(x => x.StatusText == "完成" || x.StatusText == "失败")) return;

                running = true;
                total = this.Items.Count;
                done = this.Items.Count(x => x.StatusText == "完成" || x.StatusText == "失败");
                currentPercent = 0;
                startedUtc = DateTime.UtcNow;
                cts = new CancellationTokenSource();
                var token = cts.Token;
                _ = Task.Run(() => this.LoopAsync(options, token));
            }
            Notify();
        }

        private async Task LoopAsync(VideoCompressOptions options, CancellationToken ct)
        {
            try
            {
                // 本轮已拾取的项。SetStatus 是异步派发的，刚完成项的 StatusText 尚未更新，
                // 缺少此集合时循环会立刻把同一项再拾取一次（重复压制、进度超 100%、结束延迟）。
                var processed = new HashSet<CompressFileItem>();

                while (!ct.IsCancellationRequested)
                {
                    CompressFileItem? item;
                    lock (sync)
                    {
                        item = this.Items.FirstOrDefault(x => !processed.Contains(x)
                                                              && x.StatusText != "完成"
                                                              && x.StatusText != "失败");
                        if (item == null) break;
                        total = Math.Max(total, this.Items.Count);
                        currentPercent = 0;
                        currentName = item.Name;
                    }
                    processed.Add(item);

                    this.SetStatus(item, "压制中");

                    var dir = Path.GetDirectoryName(item.FilePath) ?? "";
                    var baseName = Path.GetFileNameWithoutExtension(item.FilePath);
                    var suffix = string.IsNullOrEmpty(item.SubtitlePath) ? "【纯净版】" : "【弹幕版】";
                    var ext = string.IsNullOrWhiteSpace(options.Container) ? "mp4" : options.Container;
                    var output = Path.Combine(dir, baseName + suffix + "." + ext);

                    var code = await VideoCompressService.RunAsync(
                        options, item.FilePath, output, item.SubtitlePath,
                        p =>
                        {
                            lock (sync) { progress = p; currentPercent = p.Percent; }
                            this.Notify();
                            this.Tick?.Invoke(this, p);
                        }, ct).ConfigureAwait(false);

                    lock (sync) { done++; currentPercent = 100; }
                    this.SetStatus(item, code == 0 ? "完成" : "失败");
                    logger.Information("[压制] {Result} {Output}", code == 0 ? "完成" : "失败", output);
                    this.Notify();
                }
            }
            catch (Exception ex) { logger.Warning(ex, "[压制] 会话出错"); }
            finally
            {
                lock (sync) { running = false; cts?.Dispose(); cts = null; }
                this.Notify();
                this.CompleteIdleWaiters();
            }
        }

        public void SetPaused(bool paused)
        {
            if (paused) VideoCompressService.PauseCurrent();
            else VideoCompressService.ResumeCurrent();
        }

        public void Stop()
        {
            try { VideoCompressService.KillCurrent(); } catch { }
            lock (sync) cts?.Cancel();
        }

        // ================= 等待 / 通知 =================

        public Task WaitUntilIdleAsync()
        {
            lock (sync)
            {
                if (!running) return Task.CompletedTask;
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                idleWaiters.Add(tcs);
                return tcs.Task;
            }
        }

        private void CompleteIdleWaiters()
        {
            TaskCompletionSource<bool>[] waiters;
            lock (sync) { waiters = idleWaiters.ToArray(); idleWaiters.Clear(); }
            foreach (var w in waiters) w.TrySetResult(true);
        }

        private void SetStatus(CompressFileItem item, string status)
        {
            var app = Application.Current;
            if (app != null && !app.Dispatcher.CheckAccess())
                app.Dispatcher.BeginInvoke(new Action(() => item.StatusText = status));
            else
                item.StatusText = status;
        }

        private void Notify()
        {
            var app = Application.Current;
            if (app != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(new Action(Notify));
                return;
            }
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(CountText));
            OnPropertyChanged(nameof(OverallPercent));
            OnPropertyChanged(nameof(TimeText));
            OnPropertyChanged(nameof(CurrentName));
            OnPropertyChanged(nameof(Progress));
        }

        private static string Fmt(double sec)
        {
            if (sec <= 0) return "--:--";
            var t = TimeSpan.FromSeconds(sec);
            return t.TotalHours >= 1
                ? string.Format("{0:0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                : string.Format("{0:00}:{1:00}", t.Minutes, t.Seconds);
        }

        private void OnPropertyChanged(string n) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
