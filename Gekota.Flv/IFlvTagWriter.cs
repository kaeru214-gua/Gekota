using System;
using System.Threading.Tasks;
using Gekota.Flv.Amf;

namespace Gekota.Flv
{
    public interface IFlvTagWriter : IDisposable
    {
        long FileSize { get; }
        object? State { get; }

        Task CreateNewFile();
        bool CloseCurrentFile();
        Task WriteTag(Tag tag);
        Task OverwriteMetadata(ScriptTagBody metadata);

        Task WriteAccompanyingTextLog(double lastTagDuration, string message);
    }
}
