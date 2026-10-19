using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Serilog;

#nullable enable
namespace Gekota.WPF.Services
{
    /// <summary>完整自更新：下载 Setup.exe → 批处理静默安装 → 自动重启。</summary>
    public static class SelfUpdater
    {
        private static readonly ILogger logger = Log.ForContext(typeof(SelfUpdater));

        public static async Task<bool> DownloadAndApplyAsync(string setupUrl)
        {
            try
            {
                var tempRoot = Path.Combine(Path.GetTempPath(), "GekotaUpdate");
                Directory.CreateDirectory(tempRoot);

                var setupPath = Path.Combine(tempRoot, "Gekota-Setup.exe");

                // ① 下载安装包
                using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
                using (var resp = await client.GetAsync(setupUrl).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    using var fs = File.Create(setupPath);
                    await resp.Content.CopyToAsync(fs).ConfigureAwait(false);
                }

                // ② 写批处理：等程序退出 → 静默安装 → 重启
                var bat = Path.Combine(tempRoot, "update.bat");
                File.WriteAllText(bat, BuildBatch(), Encoding.ASCII);

                var appDir = AppContext.BaseDirectory;
                var psi = new ProcessStartInfo
                {
                    FileName = bat,
                    Arguments = "\"" + setupPath + "\" \"" + appDir + "\"",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                Process.Start(psi);

                Application.Current.Shutdown();
                return true;
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "[更新] 下载/安装失败");
                return false;
            }
        }

        private static string BuildBatch()
        {
            // %~1 = Setup.exe 路径，%~2 = 程序目录
            return "@echo off\r\n"
                 + "title Gekota Updater\r\n"
                 + "timeout /t 3 /nobreak >nul\r\n"
                 + "\"%~1\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL\r\n"
                 + "timeout /t 2 /nobreak >nul\r\n"
                 + "start \"\" \"%~2\\Gekota.WPF.exe\"\r\n"
                 + "exit\r\n";
        }
    }
}
