using System;

namespace Gekota.Core.Event
{
    public interface IRecordSessionEventArgs
    {
        Guid SessionId { get; }
    }
}
