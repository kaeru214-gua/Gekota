using Gekota.Flv.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace Gekota.DependencyInjection
{
    public static class DependencyInjectionExtensions
    {
        public static IServiceCollection AddFlv(this IServiceCollection services) => services
            .AddTransient<IProcessingPipelineBuilder, ProcessingPipelineBuilder>()
            ;
    }
}
