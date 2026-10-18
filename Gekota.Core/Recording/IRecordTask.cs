using System;
using System.Threading.Tasks;
using Gekota.Core.Event;

namespace Gekota.Core.Recording
{
    internal interface IRecordTask
    {
        Guid SessionId { get; }

        int Qn { get; }

        /// <summary>此任务是否为裁剪任务（独立于主录制）。</summary>
        bool IsClipTask { get; set; }

        /// <summary>当前录制文件完整路径（含文件名），未开文件时为 null。</summary>
        string? CurrentFilePath { get; }

        bool IsClipping { get; }

        event EventHandler<IOStatsEventArgs>? IOStats;
        event EventHandler<RecordingStatsEventArgs>? RecordingStats;
        event EventHandler<RecordFileOpeningEventArgs>? RecordFileOpening;
        event EventHandler<RecordFileClosedEventArgs>? RecordFileClosed;
        event EventHandler? RecordSessionEnded;
        event EventHandler<ClipEventArgs>? ClipStarted;
        event EventHandler<ClipEventArgs>? ClipEnded;

        void SplitOutput();

        /// <summary>获取当前直播流地址（fresh，用于抓帧）。</summary>
        Task<string> GetCurrentStreamUrlAsync();
        Task StartAsync();
        void RequestStop();

        void StartClip();
        void EndClip();
    }
}
