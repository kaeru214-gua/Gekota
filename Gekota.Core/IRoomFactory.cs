using Gekota.Core.Config.V3;

namespace Gekota.Core
{
    internal interface IRoomFactory
    {
        IRoom CreateRoom(RoomConfig roomConfig, int initDelayFactor);
    }
}
