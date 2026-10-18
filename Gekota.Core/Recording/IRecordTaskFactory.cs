using Gekota.Core.Config;

namespace Gekota.Core.Recording
{
    internal interface IRecordTaskFactory
    {
        IRecordTask CreateRecordTask(IRoom room, RecordMode? recordModeOverride = null);
    }
}
