using System;
using System.Threading;
using System.Threading.Tasks;
using Gekota.Flv.Pipeline.Actions;

namespace Gekota.Flv
{
    public interface ITagGroupReader : IDisposable
    {
        Task<PipelineAction?> ReadGroupAsync(CancellationToken token);
    }
}
