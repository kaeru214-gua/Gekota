using System.IO;

namespace Gekota.Flv
{
    public interface IMemoryStreamProvider
    {
        MemoryStream CreateMemoryStream(string tag);
    }
}
