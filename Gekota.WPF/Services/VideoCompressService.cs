using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Serilog;

#nullable enable
namespace Gekota.WPF.Services
{
    /// <summary>单次压制任务的实时进度（来自 FFmpeg -progress 输出）。</summary>
    public sealed class CompressProgress
    {
        public long Frame { get; set; }
        public double Fps { get; set; }
        public string Bitrate { get; set; } = "";
        public double Speed { get; set; }
        public double OutTimeSec { get; set; }
        public double TotalDurationSec { get; set; }
        public long TotalSizeBytes { get; set; }
        public double Percent { get; set; }

        public CompressProgress Clone() => (CompressProgress)this.MemberwiseClone();
    }

    /// <summary>用 ShanaEncoder 的 FFmpeg 执行单个压制任务。</summary>
    public static class VideoCompressService
    {
        private static readonly ILogger logger = Log.ForContext(typeof(VideoCompressService));

        public static string FfmpegDir => Path.Combine(AppContext.BaseDirectory, "lib", "shana");
        public static string FfmpegPath => Path.Combine(FfmpegDir, "ShanaEncoder.sha");

        private static Process? currentProcess;

        [System.Runtime.InteropServices.DllImport("ntdll.dll")]
        private static extern int NtSuspendProcess(IntPtr handle);

        [System.Runtime.InteropServices.DllImport("ntdll.dll")]
        private static extern int NtResumeProcess(IntPtr handle);

        public static bool IsRunning => currentProcess != null;

        /// <summary>暂停当前压制（挂起进程）。</summary>
        public static void PauseCurrent()
        {
            var p = currentProcess;
            if (p == null) return;
            try { NtSuspendProcess(p.Handle); } catch { }
        }

        /// <summary>恢复当前压制。</summary>
        public static void ResumeCurrent()
        {
            var p = currentProcess;
            if (p == null) return;
            try { NtResumeProcess(p.Handle); } catch { }
        }

        /// <summary>强制终止当前压制，并连同 ShanaEncoder 拉起的子进程一起结束。</summary>
        public static void KillCurrent()
        {
            var p = currentProcess;
            currentProcess = null;
            if (p != null) KillTree(p);
        }

        /// <summary>清理上次异常退出遗留的 ShanaEncoder 进程。</summary>
        public static void KillStaleProcesses()
        {
            var root = Path.GetFullPath(FfmpegDir).TrimEnd('\\') + "\\";
            foreach (var p in Process.GetProcessesByName("ShanaEncoder.sha"))
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path) &&
                        Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        KillTree(p);
                }
                catch { }
            }
        }

        private static void KillTree(Process p)
        {
            try
            {
                using var taskkill = Process.Start(new ProcessStartInfo("taskkill",
                    "/PID " + p.Id + " /T /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                taskkill?.WaitForExit(3000);
            }
            catch
            {
                try { p.Kill(); } catch { }
            }
        }

        /// <summary>应用退出时调用，避免 ShanaEncoder 留在后台。</summary>
        public static void Shutdown() => KillCurrent();

        public static Task<int> RunAsync(VideoCompressOptions opts, string input, string output, string? subtitle,
            Action<CompressProgress>? onProgress, CancellationToken ct = default)        {
            return Task.Run(() =>
            {
                if (!File.Exists(FfmpegPath))
                {
                    logger.Error("[压制] 找不到 FFmpeg: {Path}", FfmpegPath);
                    return -1;
                }

                // 先探测源文件尺寸：auto 模式要按横/竖屏选 4K 或 1080p 竖版
                var probe = MediaProbe.Probe(input);
                var srcW = probe?.Width ?? 0;
                var srcH = probe?.Height ?? 0;

                var psi = new ProcessStartInfo
                {
                    FileName = FfmpegPath,
                    Arguments = opts.BuildArguments(input, output, subtitle, srcW, srcH),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = FfmpegDir,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                using var proc = new Process { StartInfo = psi };
                double totalSec = 0;
                var prog = new CompressProgress();

                proc.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    var line = e.Data;
                    var eq = line.IndexOf('=');
                    if (eq <= 0) return;
                    var key = line.Substring(0, eq).Trim();
                    var val = line.Substring(eq + 1).Trim();

                    switch (key)
                    {
                        case "frame":
                            if (long.TryParse(val, out var frame)) prog.Frame = frame;
                            break;
                        case "fps":
                            if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps)) prog.Fps = fps;
                            break;
                        case "bitrate":
                            if (!string.Equals(val, "N/A", StringComparison.OrdinalIgnoreCase))
                                prog.Bitrate = val.Replace("kbits/s", "Kbps");
                            break;
                        case "total_size":
                            if (long.TryParse(val, out var size)) prog.TotalSizeBytes = size;
                            break;
                        case "out_time":
                            if (TimeSpan.TryParse(val, out var ts)) prog.OutTimeSec = ts.TotalSeconds;
                            break;
                        case "speed":
                            if (double.TryParse(val.TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out var sp)) prog.Speed = sp;
                            break;
                        case "progress":
                            prog.TotalDurationSec = totalSec;
                            prog.Percent = totalSec > 0 ? Math.Min(100.0, prog.OutTimeSec / totalSec * 100.0) : 0;
                            onProgress?.Invoke(prog.Clone());
                            break;
                    }
                };

                proc.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    var d = ParseDuration(e.Data);
                    if (d > 0) totalSec = d;
                    logger.Debug("[FFmpeg] {Line}", e.Data);
                };

                logger.Information("[压制] 开始 {Input} -> {Output}", input, output);

                proc.Start();
                currentProcess = proc;
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                try
                {
                    while (!proc.WaitForExit(500))
                    {
                        if (ct.IsCancellationRequested)
                        {
                            try { proc.Kill(); } catch { }
                            break;
                        }
                    }
                }
                finally
                {
                    currentProcess = null;
                }

                proc.CancelOutputRead();
                proc.CancelErrorRead();

                return proc.ExitCode;
            }, ct);
        }

        private static double ParseDuration(string line)
        {
            try
            {
                var idx = line.IndexOf("Duration:", StringComparison.Ordinal);
                if (idx < 0) return 0;
                var rest = line.Substring(idx + "Duration:".Length).Trim();
                var comma = rest.IndexOf(',');
                var dur = comma >= 0 ? rest.Substring(0, comma).Trim() : rest;
                if (TimeSpan.TryParse(dur, out var ts)) return ts.TotalSeconds;
            }
            catch { }
            return 0;
        }
    }
}
