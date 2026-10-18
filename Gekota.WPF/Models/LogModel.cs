using System.Collections.ObjectModel;

namespace Gekota.WPF.Models
{
    internal class LogModel : ReadOnlyObservableCollection<WpfLogEventSink.LogModel>
    {
        public LogModel() : base(WpfLogEventSink.Logs)
        {
        }
    }
}
