using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Serilog;

#nullable enable
namespace Gekota.WPF.Services
{
    /// <summary>自动上传队列：串行执行，任务逐个处理，跳过/失败自动下一个。</summary>
    public sealed class BiliUploadQueue : INotifyPropertyChanged
    {
        public static BiliUploadQueue Instance { get; } = new BiliUploadQueue();

        private static readonly ILogger logger = Log.ForContext(typeof(BiliUploadQueue));

        private readonly ConcurrentQueue<Func<Task>> jobs = new ConcurrentQueue<Func<Task>>();
        private int running;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>是否有任务正在执行。</summary>
        public bool IsRunning => Volatile.Read(ref running) != 0;

        /// <summary>排队中（不含正在执行的）任务数。</summary>
        public int PendingCount => jobs.Count;

        /// <summary>加入一个任务；若队列空闲会立即开始。</summary>
        public void Enqueue(Func<Task> job)
        {
            jobs.Enqueue(job);
            Notify();
            _ = ProcessAsync();
        }

        private async Task ProcessAsync()
        {
            // 只允许一个 worker
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return;

            try
            {
                while (jobs.TryDequeue(out var job))
                {
                    try
                    {
                        await job().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // 单个任务异常不能拖垮队列
                        logger.Warning(ex, "[上传] 队列任务异常");
                    }
                    Notify();
                }
            }
            finally
            {
                running = 0;
                Notify();
                // 竞态兜底：处理期间又有人入队
                if (!jobs.IsEmpty)
                    _ = ProcessAsync();
            }
        }

        private void Notify()
        {
            var app = Application.Current;
            if (app != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(new Action(Notify));
                return;
            }
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(PendingCount));
        }

        private void OnPropertyChanged(string n) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
