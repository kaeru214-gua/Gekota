using System.Collections.Generic;
using Gekota.Flv.Pipeline.Actions;

namespace Gekota.Flv.Grouping.Rules
{
    public class ScriptGroupingRule : IGroupingRule
    {
        public bool CanStartWith(Tag tag) => tag.IsScript();

        public bool CanAppendWith(Tag tag, List<Tag> tags) => false;

        public PipelineAction CreatePipelineAction(List<Tag> tags) => new PipelineScriptAction(tags[0]);
    }
}
