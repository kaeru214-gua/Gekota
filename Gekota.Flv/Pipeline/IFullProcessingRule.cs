namespace Gekota.Flv.Pipeline
{
    public interface IFullProcessingRule : IProcessingRule
    {
        void Run(FlvProcessingContext context, ProcessingDelegate next);
    }
}
