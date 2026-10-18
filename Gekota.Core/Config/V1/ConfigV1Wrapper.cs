using Newtonsoft.Json;

namespace Gekota.Core.Config.V1
{
    internal sealed class ConfigV1Wrapper : ConfigBase
    {
        /// <summary>
        /// Config Data String
        /// </summary>
        [JsonProperty("data")]
        public string? Data { get; set; }
    }
}
