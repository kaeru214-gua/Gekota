using System;
using System.Linq;
using Gekota.Flv.Amf;
using Gekota.Flv.Pipeline.Actions;

namespace Gekota.Flv.Pipeline.Rules
{
    public class FfmpegDetectionRule : ISimpleProcessingRule
    {
        public bool EndTagDetected { get; private set; }
        public bool LavfEncoderDetected { get; private set; }

        public void Run(FlvProcessingContext context, Action next)
        {
            if (!this.EndTagDetected && context.Actions.Any(x => x is PipelineEndAction))
                this.EndTagDetected = true;

            if (!this.LavfEncoderDetected && context.Actions.Any(action =>
            {
                if (action is PipelineScriptAction scriptAction
                    && (scriptAction?.Tag?.ScriptData?.GetMetadataValue()?.TryGetValue("encoder", out var encoderValue) ?? false)
                    && encoderValue is ScriptDataString encoder)
                    return encoder.Value.StartsWith("Lavf", StringComparison.Ordinal);
                return false;
            }))
                this.LavfEncoderDetected = true;

            next();
        }
    }
}
