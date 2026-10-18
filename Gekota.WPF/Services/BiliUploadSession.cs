using System;
using System.ComponentModel;
using System.Threading;
using System.Windows;

#nullable enable
namespace Gekota.WPF.Services
{
    /// <summary>自动上传的全局状态，供用户页显示进度与结果提醒。</summary>
    public sealed class BiliUploadSession : INotifyPropertyChanged
    {
        public static BiliUploadSession Instance { get; } = new BiliUploadSession();

        private readonly object sync = new object();
        private bool running;
        private bool done;
        private bool isError;
        private double percent;
        private string title = string.Empty;
        private string stage = string.Empty;
        private string result = string.Empty;
        private DateTime startedUtc;

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool IsRunning { get { lock (sync) return running; } }
        public bool IsDone { get { lock (sync) return done; } }
        public bool IsError { get { lock (sync) return isError; } }
        public double Percent { get { lock (sync) return percent; } }
        public string Title { get { lock (sync) return title; } }
        public string Stage { get { lock (sync) return stage; } }
        public string Result { get { lock (sync) return result; } }

        public void Start(string name)
        {
            lock (sync)
            {
                running = true; done = false; isError = false;
                percent = 0; title = name; stage = "准备上传..."; result = string.Empty;
                startedUtc = DateTime.UtcNow;
            }
            Notify();
        }

        public void ReportStage(string s)
        {
            lock (sync) stage = s;
            Notify();
        }

        public void ReportFilePercent(double p)
        {
            lock (sync) percent = Math.Max(0, Math.Min(100, p));
            Notify();
        }

        public void Complete(string bvid)
        {
            lock (sync)
            {
                running = false; done = true; isError = false;
                percent = 100; stage = "投稿成功"; result = bvid;
            }
            Notify();
        }

        public void Fail(string message)
        {
            lock (sync)
            {
                running = false; done = true; isError = true;
                stage = "上传失败"; result = message;
            }
            Notify();
        }

        public void Skip(string message)
        {
            lock (sync)
            {
                running = false; done = true; isError = false;
                stage = "已跳过"; result = message;
            }
            Notify();
        }

        public void Reset()
        {
            lock (sync)
            {
                running = false; done = false; isError = false;
                percent = 0; stage = string.Empty; result = string.Empty; title = string.Empty;
            }
            Notify();
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
            OnPropertyChanged(nameof(IsDone));
            OnPropertyChanged(nameof(IsError));
            OnPropertyChanged(nameof(Percent));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Stage));
            OnPropertyChanged(nameof(Result));
        }

        private void OnPropertyChanged(string n) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
