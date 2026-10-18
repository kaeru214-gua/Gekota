using System.Collections.Generic;
using Gekota.Flv.Pipeline.Actions;

namespace Gekota.Flv
{
    public interface IGroupingRule
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="tags">Current Tags</param>
        /// <returns></returns>
        bool CanStartWith(Tag tag);

        /// <summary>
        /// 
        /// </summary>
        /// <param name="tag">Tag not yet added to the list</param>
        /// <param name="tags">List of tags</param>
        /// <returns></returns>
        bool CanAppendWith(Tag tag, List<Tag> tags);

        PipelineAction CreatePipelineAction(List<Tag> tags);
    }
}
