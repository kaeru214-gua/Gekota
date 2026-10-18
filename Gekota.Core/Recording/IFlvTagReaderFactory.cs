using System.IO.Pipelines;
using Gekota.Flv;

namespace Gekota.Core.Recording
{
    internal interface IFlvTagReaderFactory
    {
        IFlvTagReader CreateFlvTagReader(PipeReader pipeReader);
    }
}
