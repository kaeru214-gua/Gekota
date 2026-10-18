using System;

namespace Gekota.Core.Event
{
    public sealed class ClipEventArgs : EventArgs
    {
        public ClipEventArgs(string clipFilePath, DateTimeOffset startTime, DateTimeOffset? endTime = null)
        {
            this.ClipFilePath = clipFilePath ?? throw new ArgumentNullException(nameof(clipFilePath));
            this.StartTime = startTime;
            this.EndTime = endTime;
        }

        /// <summary>裁剪文件的完整路径（FLV）。</summary>
        public string ClipFilePath { get; }

        /// <summary>裁剪开始时间。</summary>
        public DateTimeOffset StartTime { get; }

        /// <summary>裁剪结束时间（裁剪进行中为 null）。</summary>
        public DateTimeOffset? EndTime { get; }
    }
}
