using System;
using System.IO;
using Newtonsoft.Json;

#nullable enable
namespace Gekota.WPF.Services
{
    public class UserSession
    {
        public long Uid { get; set; }
        public string Username { get; set; } = string.Empty;
        public string AvatarUrl { get; set; } = string.Empty;
        public string Cookie { get; set; } = string.Empty;
        public DateTimeOffset LoginTime { get; set; } = DateTimeOffset.Now;
    }

    public static class UserSessionStore
    {
        private static readonly string FilePath;

        static UserSessionStore()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Gekota");
            try { Directory.CreateDirectory(dir); } catch { }
            FilePath = Path.Combine(dir, "user_session.json");
        }

        public static UserSession? Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                var json = File.ReadAllText(FilePath);
                return JsonConvert.DeserializeObject<UserSession>(json);
            }
            catch { return null; }
        }

        public static void Save(UserSession session)
        {
            try
            {
                var json = JsonConvert.SerializeObject(session, Formatting.Indented);
                File.WriteAllText(FilePath, json);
            }
            catch { }
        }

        public static void Clear()
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
        }
    }
}
