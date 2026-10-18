using Gekota.Core.SimpleWebhook;

namespace Gekota.Core.Event
{
    /// <summary>
    /// <see cref="EventType.StreamStarted"/>
    /// </summary>
    public sealed class StreamStartedEventArgs : RecordEventArgsBase
    {
        internal StreamStartedEventArgs(IRoom room) : base(room)
        {
        }
    }
}
