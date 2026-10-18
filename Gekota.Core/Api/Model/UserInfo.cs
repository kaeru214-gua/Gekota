using Newtonsoft.Json;

namespace Gekota.Core.Api.Model
{
    internal class UserInfo
    {
        [JsonProperty("info")]
        public InfoClass? Info { get; set; }

        public class InfoClass
        {
            [JsonProperty("uname")]
            public string Name { get; set; } = string.Empty;
        }
    }
}
