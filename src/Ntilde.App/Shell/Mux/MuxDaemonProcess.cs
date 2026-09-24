using System.Runtime.InteropServices;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Pty;
using Ntilde.Shell;

namespace Ntilde.Shell.Mux;

/// <summary>The body of <c>ntilde mux serve</c> (spec §5). Never initialises Avalonia.</summary>
internal static partial class MuxDaemonProcess
{
    public static int Run(MuxServeOptions options, TextWriter stderr)
    {
        // Controller ruling (Task 5 review): the launcher spawns us with our stdio redirected to
        // pipes whose parent ends it closes immediately, so a non-foreground `mux serve` must
        // reassign its own stdio (Windows: detach from the console; Unix: new session + /dev/null
        // over 0-2) as the VERY FIRST thing it does - before any logging, Console use, or anything
        // else that could write to the inherited handles. Doing this after any other work would
        // leave a window where those writes still target the parent's pipes.
        if (!options.Foreground) Daemonize();

        // A console-attached host takes ConPTY's passthrough path (lib.rs ~727-769); the daemon must
        // not, whatever console it inherited. Before the first spawn.
        if (OperatingSystem.IsWindows()) Environment.SetEnvironmentVariable("NTILDE_PTY_NO_PASSTHROUGH", "1");

        using var log = new RotatingFileLogWriter(Path.Combine(AppPaths.LogsDirectory, "mux.log"), 16L * 1024 * 1024, 8192);
        void Log(string message)
        {
            string line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z {message}";
            log.Write(line);
            if (options.Foreground) stderr.WriteLine(line);
        }

        // PtyLogger.Sink is Action<PtyLogLevel, string>, not Action<string> - the brief's plan
        // assumed the latter. Folding the level into the text keeps one Log helper for both the
        // PTY bridge and the daemon's own messages, same as Program.cs's GUI wiring
        // (`PtyLogger.Sink = static (level, message) => TerminalLogger.Log(ToLogLevel(level), message)`)
        // adapted to a plain string sink instead of TerminalLogger.
        PtyLogger.Sink = (level, message) => Log($"[{level}] {message}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log($"[MuxDaemon] unhandled: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => { Log($"[MuxDaemon] unobserved task: {e.Exception}"); e.SetObserved(); };

        var server = new MuxServer(DefaultTerminalSessionFactory.Instance, new MuxServerOptions { Log = Log });
        using var host = new MuxDaemonHost(server, new MuxDaemonOptions
        {
            Endpoint = MuxDiscovery.GetDefaultEndpoint(),
            DescriptorPath = MuxDiscovery.GetDescriptorPath(),
            IdleExitAfter = options.IdleExitAfter,
            Log = Log,
        });

        try
        {
            host.Start();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"[MuxDaemon] not starting: {ex.Message}");
            if (options.Foreground) stderr.WriteLine(ex.Message);
            return 1;
        }

        using PosixSignalRegistration term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; host.RequestStop("signal"); });
        using PosixSignalRegistration intr = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; host.RequestStop("signal"); });
        using PosixSignalRegistration? hup = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx => ctx.Cancel = true);

        string reason = host.Completion.GetAwaiter().GetResult();
        Log($"[MuxDaemon] exiting ({reason})");
        return 0;
    }

    private static void Daemonize()
    {
        if (OperatingSystem.IsWindows())
        {
            FreeConsole();
            return;
        }

        // New session: no controlling terminal, not in the spawner's process group, so a terminal
        // hang-up or the GUI's group being signalled does not reach the daemon (or its shells).
        _ = setsid();
        int devNull = open("/dev/null", 2 /* O_RDWR */);
        if (devNull >= 0)
        {
            _ = dup2(devNull, 0);
            _ = dup2(devNull, 1);
            _ = dup2(devNull, 2);
            if (devNull > 2) _ = close(devNull);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();

    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int dup2(int oldfd, int newfd);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
