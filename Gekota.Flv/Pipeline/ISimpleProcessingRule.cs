using System;

namespace Gekota.Flv.Pipeline
{
    public interface ISimpleProcessingRule : IProcessingRule
    {
        void Run(FlvProcessingContext context, Action next);
    }
}
