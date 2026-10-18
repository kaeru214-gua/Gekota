using Gekota.Flv;

namespace Gekota.Core.Recording
{
    internal interface ITagGroupReaderFactory
    {
        ITagGroupReader CreateTagGroupReader(IFlvTagReader flvTagReader);
    }
}
