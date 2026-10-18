using System;
using System.IO;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Serilog;
using Serilog.Core;
using Serilog.Exceptions;
using Serilog.Formatting.Compact;

#nullable enable
namespace Gekota.WPF
{
    internal static class Program
    {
        internal static readonly LoggingLevelSwitch levelSwitchGlobal;
        internal static readonly LoggingLevelSwitch levelSwitchConsole;
        internal static readonly Logger logger;

#if DEBUG
        internal static readonly bool DebugMode = System.Diagnostics.Debugger.IsAttached;
#else
        internal static readonly bool DebugMode = false;
#endif

        static Program()
        {
            AttachConsole(-1);
            levelSwitchGlobal = new LoggingLevelSwitch(Serilog.Events.LogEventLevel.Debug);
            if (DebugMode)
                levelSwitchGlobal.MinimumLevel = Serilog.Events.LogEventLevel.Verbose;
            levelSwitchConsole = new LoggingLevelSwitch(Serilog.Events.LogEventLevel.Error);
            logger = BuildLogger();
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            Log.Logger = logger;
            ServicePointManager.Expect100Continue = false;
        }

        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                logger.Debug("Starting, Version: {Version}, CurrentDirectory: {CurrentDirectory}, CommandLine: {CommandLine}",
                             GekotaVersion.InformationalVersion,
                             Environment.CurrentDirectory,
                             Environment.CommandLine);

                string? path = null;
                var askPath = false;
                var hide = false;
                for (var i = 0; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "run":
                            break;
                        case "--ask-path": askPath = true; break;
                        case "--hide": hide = true; break;
                        default:
                            if (!args[i].StartsWith("-") && path is null)
                                path = args[i];
                            break;
                    }
                }

                Pages.RootPage.CommandArgumentRecorderPath = path;
                Pages.RootPage.CommandArgumentAskPath = askPath;
                Pages.RootPage.CommandArgumentHide = hide;
                Pages.RootPage.CommandArgumentFirstRun = false;

                SleepBlocker.Start();

                var app = new App();
                app.InitializeComponent();
                app.DispatcherUnhandledException += App_DispatcherUnhandledException;
                return app.Run();
            }
            finally
            {
                StreamStartedNotification.Cleanup();
                logger.Dispose();
            }
        }

        private static class SleepBlocker
        {
            internal static void Start()
            {
                var t = new Thread(EntryPoint)
                {
                    Name = "SystemSleepBlocker",
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal
                };
                t.Start();
            }

            [DllImport("kernel32", CharSet = CharSet.Auto, SetLastError = true)]
            private static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

            [Flags]
            private enum EXECUTION_STATE : uint
            {
                ES_AWAYMODE_REQUIRED = 0x00000040,
                ES_CONTINUOUS = 0x80000000,
                ES_DISPLAY_REQUIRED = 0x00000002,
                ES_SYSTEM_REQUIRED = 0x00000001
            }

            private static void EntryPoint()
            {
                try
                {
                    while (true)
                    {
                        try
                        {
                            _ = SetThreadExecutionState(EXECUTION_STATE.ES_SYSTEM_REQUIRED | EXECUTION_STATE.ES_CONTINUOUS);
                        }
                        catch (Exception) { }
                        Thread.Sleep(30 * 1000);
                    }
                }
                catch (Exception) { }
            }
        }

        private static Logger BuildLogger()
        {
            var logFilePath = Environment.GetEnvironmentVariable("GEKOTA_LOG_FILE_PATH");
            if (string.IsNullOrWhiteSpace(logFilePath))
                logFilePath = Path.Combine(AppContext.BaseDirectory, "logs", "gekota.txt");

            return new LoggerConfiguration()
                .MinimumLevel.ControlledBy(levelSwitchGlobal)
                .Enrich.WithProcessId()
                .Enrich.WithThreadId()
                .Enrich.WithThreadName()
                .Enrich.WithExceptionDetails()
                .Destructure.AsScalar<IPAddress>()
                .WriteTo.Console(levelSwitch: levelSwitchConsole)
#if DEBUG
                .WriteTo.Debug()
                .WriteTo.Async(l => l.Sink<WpfLogEventSink>(Serilog.Events.LogEventLevel.Debug))
#else
                .WriteTo.Async(l => l.Sink<WpfLogEventSink>(Serilog.Events.LogEventLevel.Information))
#endif
                .WriteTo.Async(l => l.File(new CompactJsonFormatter(), logFilePath, shared: true,
                    rollingInterval: RollingInterval.Day, rollOnFileSizeLimit: true))
                .CreateLogger();
        }

        [DllImport("kernel32")]
        private static extern bool AttachConsole(int pid);

        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
                logger.Fatal(ex, "Unhandled exception from AppDomain.UnhandledException");
        }

        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        private static void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e) =>
            logger.Error(e.Exception, "Unobserved exception from TaskScheduler.UnobservedTaskException");

        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) =>
            logger.Fatal(e.Exception, "Unhandled exception from Application.DispatcherUnhandledException");
    }
}
