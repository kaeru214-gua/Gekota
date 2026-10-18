using System.IO;

namespace Gekota.Flv
{
    public class DefaultMemoryStreamProvider : IMemoryStreamProvider
    {
        public MemoryStream CreateMemoryStream(string tag) => new MemoryStream();
    }
}
