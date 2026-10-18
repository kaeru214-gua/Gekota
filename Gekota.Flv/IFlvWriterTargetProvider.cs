using System.IO;

namespace Gekota.Flv
{
    public interface IFlvWriterTargetProvider
    {
        (Stream stream, object? state) CreateOutputStream();

        Stream CreateAccompanyingTextLogStream();
    }
}
