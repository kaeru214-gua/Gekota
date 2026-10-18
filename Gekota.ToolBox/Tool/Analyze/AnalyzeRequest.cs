using Gekota.Flv.Pipeline;

namespace Gekota.ToolBox.Tool.Analyze
{
    public class AnalyzeRequest : ICommandRequest<AnalyzeResponse>
    {
        public string Input { get; set; } = string.Empty;

        public ProcessingPipelineSettings? PipelineSettings { get; set; }
    }
}
