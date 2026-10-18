using System;
using System.Threading.Tasks;
using Gekota.Core.Api.Danmaku;
using Gekota.Core.Api.Http;
using Xunit;

namespace Gekota.Core.UnitTests.Danmaku
{
    public class ManualTests
    {
        [Fact(Skip = "skip")]
        public async Task TestAsync()
        {
            var client = new DanmakuClient(new HttpApiClient(null!), null!);

            client.StatusChanged += this.Client_StatusChanged;
            client.DanmakuReceived += this.Client_DanmakuReceived;

            await Task.Yield();

            throw new NotImplementedException();
            // await client.ConnectAsync().ConfigureAwait(false);

            // await Task.Delay(TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        }

        private void Client_DanmakuReceived(object? sender, DanmakuReceivedEventArgs e)
        {
        }

        private void Client_StatusChanged(object? sender, StatusChangedEventArgs e)
        {
        }
    }
}
