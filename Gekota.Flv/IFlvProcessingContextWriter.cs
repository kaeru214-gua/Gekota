using System;
using System.Threading.Tasks;
using Gekota.Flv.Amf;
using Gekota.Flv.Pipeline;

namespace Gekota.Flv
{
    public interface IFlvProcessingContextWriter : IDisposable
    {
        Action<ScriptTagBody>? BeforeScriptTagWrite { get; set; }
        Action<ScriptTagBody>? BeforeScriptTagRewrite { get; set; }

        event EventHandler<FileClosedEventArgs> FileClosed;

        Task<int> WriteAsync(FlvProcessingContext context);
    }
}
