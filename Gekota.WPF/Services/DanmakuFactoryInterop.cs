using System;
using System.IO;
using System.Runtime.InteropServices;
using Serilog;

#nullable enable
namespace Gekota.WPF.Services
{
    /// <summary>
    /// DanmakuFactory 核心 DLL 的 P/Invoke 封装。
    /// danmakufactory.dll 导出 runCmd(const char*)，内部再调用 danmakufactory_core.dll 的 runCommand。
    /// </summary>
    internal static class DanmakuFactoryInterop
    {
        private static readonly ILogger logger = Log.ForContext(typeof(DanmakuFactoryInterop));
        private static readonly object cmdLock = new object();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        [DllImport("danmakufactory.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int runCmd([MarshalAs(UnmanagedType.LPUTF8Str)] string cmd);

        static DanmakuFactoryInterop()
        {
            try
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "lib", "danmakufactory");
                if (Directory.Exists(dir))
                    SetDllDirectory(dir);
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "[DanmakuFactory] SetDllDirectory 失败");
            }
        }

        /// <summary>执行一条 DanmakuFactory 命令，返回退出码（0 表示成功）。</summary>
        public static int RunCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                throw new ArgumentException("命令不能为空", nameof(command));

            logger.Debug("[DanmakuFactory] runCmd: {Command}", command);
            int code;
            lock (cmdLock) // runCmd 内部有全局状态，串行执行
            {
                code = runCmd(command);
            }
            logger.Debug("[DanmakuFactory] 退出码 {Code}", code);
            return code;
        }
    }
}
