using System;
using System.Threading.Tasks;
using Gekota.Core.Api.Model;

namespace Gekota.Core.Api
{
    internal interface IDanmakuServerApiClient : IDisposable
    {
        long GetUid();
        string? GetBuvid3();
        Task<BilibiliApiResponse<DanmuInfo>> GetDanmakuServerAsync(int roomid);
    }
}
