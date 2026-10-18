using Gekota.Flv;

namespace Gekota.Core.Recording
{
    internal interface IFlvProcessingContextWriterFactory
    {
        IFlvProcessingContextWriter CreateWriter(IFlvWriterTargetProvider targetProvider);
    }
}
