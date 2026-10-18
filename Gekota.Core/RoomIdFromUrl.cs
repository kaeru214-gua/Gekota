using System.Text.RegularExpressions;

namespace Gekota.Core
{
    public static class RoomIdFromUrl
    {
        public static readonly Regex Regex = new Regex("""^(?:(?:https?:\/\/)?live\.bilibili\.com\/(?:blanc\/|h5\/)?)?(\d+)\/?(?:[#\?].*)?$""", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
    }
}
