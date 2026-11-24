using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Gekota.Core;

#nullable enable
namespace Gekota.WPF.Services
{
    public sealed class UploadMetadata
    {
        public string BracketLabel { get; set; } = "";
        public string Description { get; set; } = "";
        public string Tags { get; set; } = "";
    }

    /// <summary>
    /// 处理 A-SOUL / 双播 / 心宜思诺 的上传标题、简介和标签。
    /// 当前直播间的防撞文件由调用方单独检查。
    /// </summary>
    public static class UploadMetadataService
    {
        private static readonly string[] AsoulMembers = { "贝拉", "嘉然", "乃琳" };
        private static readonly string[] StageMembers = { "心宜", "思诺" };

        public static UploadMetadata? Resolve(IRoom room, string liveTitle, string streamerFolder)
        {
            var title = liveTitle ?? "";
            var current = (room.Name ?? "").Trim();
            var root = Path.GetDirectoryName(streamerFolder) ?? streamerFolder;

            List<string> members;
            string label;

            var asoul = Regex.Match(title, @"A-SOUL([^\s:：,，】\]]*)", RegexOptions.IgnoreCase);
            var stage = Regex.Match(title, @"心宜思诺([^\s:：,，】\]]*)");
            var duo = AsoulMembers.Where(n => title.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            if (asoul.Success)
            {
                members = AsoulMembers.ToList();
                label = "A-SOUL" + asoul.Groups[1].Value.Trim();
            }
            else if (stage.Success)
            {
                members = StageMembers.ToList();
                label = "心宜思诺" + stage.Groups[1].Value.Trim();
            }
            else if (duo.Count == 2 && AsoulMembers.Any(n =>
                         current.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                var owner = AsoulMembers.First(n =>
                    current.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
                var other = duo.First(n => !string.Equals(n, owner, StringComparison.OrdinalIgnoreCase));
                members = new List<string> { owner, other };
                label = owner + "&" + other;
            }
            else
            {
                return null;
            }

            var descriptions = new List<string>();
            var tags = new List<string>();
            foreach (var member in members)
            {
                var folder = FindStreamerFolder(root, member) ?? streamerFolder;
                var desc = ReadFile(folder, "简介.txt");
                if (!string.IsNullOrWhiteSpace(desc)) descriptions.Add(desc.Trim());
                var tag = ReadFile(folder, "tag.txt");
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    foreach (var part in tag.Split(new[] { ',', '，', ';', '；', '\r', '\n' },
                                 StringSplitOptions.RemoveEmptyEntries))
                    {
                        var value = part.Trim();
                        if (value.Length > 0 && !tags.Any(t =>
                                string.Equals(t, value, StringComparison.OrdinalIgnoreCase)))
                            tags.Add(value);
                    }
                }
            }

            return new UploadMetadata
            {
                BracketLabel = label,
                Description = string.Join("\n\n", descriptions.Distinct()),
                Tags = string.Join(", ", tags),
            };
        }

        private static string? FindStreamerFolder(string root, string member)
        {
            try
            {
                if (!Directory.Exists(root)) return null;
                return Directory.EnumerateDirectories(root, "*" + member + "*", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        private static string ReadFile(string folder, string name)
        {
            try
            {
                var path = Path.Combine(folder, name);
                return File.Exists(path) ? File.ReadAllText(path) : "";
            }
            catch { return ""; }
        }
    }
}
