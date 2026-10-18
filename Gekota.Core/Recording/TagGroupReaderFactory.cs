using Gekota.Flv;
using Gekota.Flv.Grouping;

namespace Gekota.Core.Recording
{
    internal class TagGroupReaderFactory : ITagGroupReaderFactory
    {
        public ITagGroupReader CreateTagGroupReader(IFlvTagReader flvTagReader) =>
            new TagGroupReader(flvTagReader);
    }
}
