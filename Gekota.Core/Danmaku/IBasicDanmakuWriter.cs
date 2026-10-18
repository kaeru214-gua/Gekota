using System;
using System.Threading.Tasks;
using Gekota.Core.Api.Danmaku;

namespace Gekota.Core.Danmaku
{
    internal interface IBasicDanmakuWriter : IDisposable
    {
        void Disable();
        void EnableWithPath(string path, IRoom room);
        Task WriteAsync(DanmakuModel danmakuModel);
    }
}
