using System;
using System.Threading;
using System.Threading.Tasks;
using Gekota.Core.Api.Danmaku;
using Gekota.Core.Config;

namespace Gekota.Core.Api
{
    internal interface IDanmakuClient : IDisposable
    {
        bool Connected { get; }

        event EventHandler<StatusChangedEventArgs>? StatusChanged;
        event EventHandler<DanmakuReceivedEventArgs>? DanmakuReceived;

        Func<string, string?>? BeforeHandshake { get; set; }

        Task ConnectAsync(int roomid, DanmakuTransportMode transportMode, string? bindAddress, AllowedAddressFamily allowedAddressFamily, CancellationToken cancellationToken);
        Task DisconnectAsync();
    }
}
