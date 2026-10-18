using System;
using Microsoft.Extensions.DependencyInjection;

namespace Gekota.Flv.Pipeline
{
    public interface IProcessingPipelineBuilder
    {
        IServiceCollection ServiceCollection { get; }

        IProcessingPipelineBuilder AddRule(Func<ProcessingDelegate, IServiceProvider, ProcessingDelegate> rule);

        ProcessingDelegate Build();
    }
}
