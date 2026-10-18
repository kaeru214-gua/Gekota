using System;

namespace Gekota.Flv
{
    [Flags]
    public enum TagFlag : int
    {
        None = 0,
        Header = 1 << 0,
        Keyframe = 1 << 1,
        End = 1 << 2,
    }
}
