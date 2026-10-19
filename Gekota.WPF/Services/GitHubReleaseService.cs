using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Serilog;

#nullable enable
namespace Gekota.WPF.Services
{
    public sealed class GitHubReleaseInfo
    {
        public string TagName { get; set; } = "";
        public string Name { get; set; } = "";
        public string Body { get; set; } = "";
        public string HtmlUrl { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
    }

    public static class GitHubReleaseService
    {
        // TODO：改成你的 GitHub 用户名 / 仓库名
        public const string Owner = "kaeru214-gua";
        public const string Repo = "Gekota";

        private static readonly ILogger logger = Log.ForContext(typeof(GitHubReleaseService));
        private static readonly HttpClient client;

        static GitHubReleaseService()
        {
            client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Gekota/" + GekotaVersion.FullSemVer);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        /// <summary>获取最新 Release（含版本号、发布说明 body、下载链接）。</summary>
        public static async Task<GitHubReleaseInfo?> GetLatestAsync()
        {
            try
            {
                var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
                var json = await client.GetStringAsync(url).ConfigureAwait(false);
                var jo = JObject.Parse(json);

                var info = new GitHubReleaseInfo
                {
                    TagName = jo["tag_name"]?.ToString() ?? "",
                    Name = jo["name"]?.ToString() ?? "",
                    Body = jo["body"]?.ToString() ?? "",
                    HtmlUrl = jo["html_url"]?.ToString() ?? "",
                };

                if (jo["assets"] is JArray assets)
                {
                    foreach (var a in assets)
                    {
                        var name = a["name"]?.ToString() ?? "";
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            info.DownloadUrl = a["browser_download_url"]?.ToString() ?? "";
                            break;
                        }
                    }
                }
                return info;
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "[更新] 检查 GitHub Releases 失败");
                return null;
            }
        }

        public static string NormalizeTag(string? tag) => (tag ?? "").TrimStart('v', 'V').Trim();

        /// <summary>三段式版本比较：latest > current 返回 true。</summary>
        public static bool IsNewer(string? latest, string current)
        {
            if (string.IsNullOrWhiteSpace(latest)) return false;
            var a = NormalizeTag(latest).Split('.');
            var b = (current ?? "").Split('.');
            for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                var x = i < a.Length && int.TryParse(a[i], out var v1) ? v1 : 0;
                var y = i < b.Length && int.TryParse(b[i], out var v2) ? v2 : 0;
                if (x != y) return x > y;
            }
            return false;
        }
    }
}
