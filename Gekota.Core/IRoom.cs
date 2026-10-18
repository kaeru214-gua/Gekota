using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Gekota.Core.Config.V3;
using Gekota.Core.Event;
using Newtonsoft.Json.Linq;

namespace Gekota.Core
{
    public interface IRoom : INotifyPropertyChanged, IDisposable
    {
        Guid ObjectId { get; }

        RoomConfig RoomConfig { get; }

        int ShortId { get; }
        string Name { get; }
        long Uid { get;}
        string Title { get; }
        string AreaNameParent { get; }
        string AreaNameChild { get; }

        JObject? RawBilibiliApiJsonData { get; }

        bool Recording { get; }
        bool Streaming { get; }
        bool DanmakuConnected { get; }
        bool AutoRecordForThisSession { get; }
        bool IsClipping { get; }

        /// <summary>当前录制文件完整路径（未录制时为 null）。</summary>
        string? CurrentRecordFilePath { get; }
        RoomStats Stats { get; }

        event EventHandler<RecordSessionStartedEventArgs>? RecordSessionStarted;
        event EventHandler<RecordSessionEndedEventArgs>? RecordSessionEnded;
        event EventHandler<RecordFileOpeningEventArgs>? RecordFileOpening;
        event EventHandler<RecordFileClosedEventArgs>? RecordFileClosed;
        event EventHandler<RecordingStatsEventArgs>? RecordingStats;
        event EventHandler<IOStatsEventArgs>? IOStats;
        event EventHandler<ClipEventArgs>? ClipStarted;
        event EventHandler<ClipEventArgs>? ClipEnded;

        void StartRecord();
        void StopRecord();
        void SplitOutput();
        Task RefreshRoomInfoAsync();

        void MarkNextRecordShouldUseRawMode();

        /// <summary>切换裁剪状态（开始 / 结束）。</summary>
        void ToggleClip();

        /// <summary>获取当前直播流地址（未录制时为 null）。</summary>
        Task<string?> GetLiveStreamUrlAsync();
    }
}
