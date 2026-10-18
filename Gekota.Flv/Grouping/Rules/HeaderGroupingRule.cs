using System.Collections.Generic;
using Gekota.Flv.Pipeline.Actions;

namespace Gekota.Flv.Grouping.Rules
{
    public class HeaderGroupingRule : IGroupingRule
    {
        public bool CanStartWith(Tag tag) => tag.IsHeader();

        public bool CanAppendWith(Tag tag, List<Tag> tags) => tag.IsHeader();

        public PipelineAction CreatePipelineAction(List<Tag> tags) => new PipelineHeaderAction(tags);
    }
}
