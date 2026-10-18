using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Gekota.Core.Config;

namespace Gekota.Core.Api.Danmaku
{
    internal interface IDanmakuTransport : IDisposable
    {
        Task<PipeReader> ConnectAsync(string host, int port, AllowedAddressFamily allowedAddressFamily, CancellationToken cancellationToken);
        Task SendAsync(byte[] buffer, int offset, int count);
    }
}
