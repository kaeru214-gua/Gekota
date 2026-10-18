using System;

namespace Gekota.Flv
{
    public class FileClosedEventArgs : EventArgs
    {
        public long FileSize { get; set; }

        public double Duration { get; set; }

        public object? State { get; set; }
    }
}
