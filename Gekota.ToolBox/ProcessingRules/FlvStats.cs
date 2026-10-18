using System.Collections.Generic;

namespace Gekota.ToolBox.ProcessingRules
{
    public class FlvStats
    {
        public int FrameCount { get; set; }
        public double FramePerSecond { get; set; }
        public Dictionary<int, int>? FrameDurations { get; set; }
    }
}
