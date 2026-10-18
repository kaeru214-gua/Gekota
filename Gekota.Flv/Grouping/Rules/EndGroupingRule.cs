using System.Collections.Generic;
using Gekota.Flv.Pipeline.Actions;

namespace Gekota.Flv.Grouping.Rules
{
    public class EndGroupingRule : IGroupingRule
    {
        public bool CanStartWith(Tag tag) => tag.IsEnd();

        public bool CanAppendWith(Tag tag, List<Tag> tags) => false;

        public PipelineAction CreatePipelineAction(List<Tag> tags) => new PipelineEndAction(tags[0]);
    }
}
