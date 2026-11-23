using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

#nullable enable
namespace Gekota.WPF.Services
{
    /// <summary>
    /// 从萌娘百科条目里的“代表色 / 主题色 / 应援色”读取主播主题色。
    /// 使用移动站 HTML，不走被限制的 MediaWiki API。
    /// </summary>
    public static class MoegirlThemeService
    {
        private static readonly HttpClient http = CreateClient();
        private static readonly Dictionary<string, Color?> cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object gate = new();

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = true,
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Linux; Android 10) AppleWebKit/537.36 Chrome/132 Mobile Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
            return client;
        }

        public static async Task<Color?> FindColorAsync(string name, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            lock (gate)
            {
                if (cache.TryGetValue(name, out var cached)) return cached;
            }

            Color? result = null;
            try
            {
                var searchUrl = "https://mzh.moegirl.org.cn/index.php?search=" +
                                Uri.EscapeDataString(name);
                using var response = await http.GetAsync(searchUrl, cancellationToken).ConfigureAwait(false);
                var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                result = ExtractColor(html);

                if (result == null)
                {
                    foreach (var link in FindArticleLinks(html, name, 4))
                    {
                        try
                        {
                            using var page = await http.GetAsync("https://mzh.moegirl.org.cn" + link,
                                cancellationToken).ConfigureAwait(false);
                            var pageHtml = await page.Content.ReadAsStringAsync().ConfigureAwait(false);
                            result = ExtractColor(pageHtml);
                            if (result != null) break;
                        }
                        catch
                        {
                            // 换下一个候选条目
                        }
                    }
                }
            }
            catch
            {
                result = null;
            }

            lock (gate) cache[name] = result;
            return result;
        }

        private static IEnumerable<string> FindArticleLinks(string html, string name, int max)
        {
            var found = 0;
            foreach (Match match in Regex.Matches(html,
                         "href=\"(/[^\"#?]+)\"[^>]*>([^<]{0,80})</a>", RegexOptions.IgnoreCase))
            {
                var href = WebUtility.HtmlDecode(match.Groups[1].Value);
                var text = WebUtility.HtmlDecode(Regex.Replace(match.Groups[2].Value, "<[^>]+>", "")).Trim();
                if (!href.StartsWith("/", StringComparison.Ordinal)) continue;
                if (href.StartsWith("/Special:", StringComparison.OrdinalIgnoreCase) ||
                    href.StartsWith("/Category:", StringComparison.OrdinalIgnoreCase) ||
                    href.StartsWith("/Template:", StringComparison.OrdinalIgnoreCase) ||
                    href.StartsWith("/Help:", StringComparison.OrdinalIgnoreCase) ||
                    href.StartsWith("/萌娘百科:", StringComparison.Ordinal)) continue;
                if (text.Length == 0 || text.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;

                yield return href;
                if (++found >= max) yield break;
            }
        }

        private static Color? ExtractColor(string html)
        {
            foreach (var label in new[] { "代表色", "主题色", "应援色" })
            {
                var index = html.IndexOf(label, StringComparison.Ordinal);
                if (index < 0) continue;
                var length = Math.Min(2600, html.Length - index);
                var chunk = html.Substring(index, length);
                var match = Regex.Match(chunk,
                    "title=\"(#[0-9A-Fa-f]{6})\"", RegexOptions.IgnoreCase);
                if (!match.Success)
                    match = Regex.Match(chunk, "color\\s*[:=]\\s*(#[0-9A-Fa-f]{6})",
                        RegexOptions.IgnoreCase);
                if (!match.Success) continue;
                if (TryParseColor(match.Groups[1].Value, out var color)) return color;
            }
            return null;
        }

        private static bool TryParseColor(string hex, out Color color)
        {
            color = Colors.Transparent;
            if (hex.Length != 7 || hex[0] != '#') return false;
            if (!byte.TryParse(hex.Substring(1, 2), System.Globalization.NumberStyles.HexNumber,
                    null, out var r)) return false;
            if (!byte.TryParse(hex.Substring(3, 2), System.Globalization.NumberStyles.HexNumber,
                    null, out var g)) return false;
            if (!byte.TryParse(hex.Substring(5, 2), System.Globalization.NumberStyles.HexNumber,
                    null, out var b)) return false;
            color = Color.FromRgb(r, g, b);
            return true;
        }
    }
}
