using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

#nullable enable
namespace Gekota.WPF.Services
{
    /// <summary>
    /// 在后台线程解码窗口背景图，避免 UI 线程同步读取大图。
    /// </summary>
    public static class BackgroundImageService
    {
        private static readonly ConcurrentDictionary<string, BitmapImage> Cache =
            new ConcurrentDictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);

        private static readonly object Gate = new object();
        private static Task<BitmapImage?>? pending;
        private static string? pendingPath;

        public static Task<BitmapImage?> GetAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return Task.FromResult<BitmapImage?>(null);
            if (Cache.TryGetValue(path, out var cached)) return Task.FromResult<BitmapImage?>(cached);

            lock (Gate)
            {
                if (pending != null && string.Equals(pendingPath, path, StringComparison.OrdinalIgnoreCase))
                    return pending;

                pendingPath = path;
                pending = Task.Run(() => Decode(path));
                return pending;
            }
        }

        private static BitmapImage? Decode(string path)
        {
            BitmapImage? result = null;
            Exception? error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    if (!File.Exists(path)) return;
                    using var stream = File.OpenRead(path);
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bitmap.DecodePixelWidth = 2560;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    result = bitmap;
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            thread.Join();
            if (error != null || result == null) return null;
            Cache[path] = result;
            return result;
        }
    }
}
