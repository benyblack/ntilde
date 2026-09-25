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
        string? daemonizeProblem = options.Foreground ? null : Daemonize();

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
        // Reported only now: Daemonize runs before there is anywhere to write it.
        if (daemonizeProblem is not null) Log($"[MuxDaemon] {daemonizeProblem}");
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

        // No separate stderr echo: in --foreground, Log already writes every line to stderr.
        if (!TryStart(host, foregroundStderr: null, Log)) return 1;

        using PosixSignalRegistration term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; host.RequestStop("signal"); });
        using PosixSignalRegistration intr = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; host.RequestStop("signal"); });
        using PosixSignalRegistration? hup = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx => ctx.Cancel = true);

        string reason = host.Completion.GetAwaiter().GetResult();
        Log($"[MuxDaemon] exiting ({reason})");
        return 0;
    }

    /// <summary>
    /// Starts <paramref name="host"/>; a start failure is logged (and echoed to
    /// <paramref name="foregroundStderr"/> when watched) and returns false - the caller exits 1.
    /// </summary>
    internal static bool TryStart(MuxDaemonHost host, TextWriter? foregroundStderr, Action<string> log)
    {
        try
        {
            host.Start();
            return true;
        }
        catch (Exception ex) when (IsStartFailure(ex))
        {
            log($"[MuxDaemon] not starting: {ex.Message}");
            foregroundStderr?.WriteLine(ex.Message);
            return false;
        }
    }

    /// <summary>
    /// "Cannot serve here" failures: exit 1, not a crash. SocketException is a backstop - the Unix
    /// listener and the host already turn socket errors into IOException - because escaping it
    /// reaches Program.Main's catch, which writes the GUI's startup-error file and rethrows.
    /// </summary>
    internal static bool IsStartFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException;

    /// <returns>A problem worth logging once the log exists, or null.</returns>
    private static string? Daemonize()
    {
        if (OperatingSystem.IsWindows())
        {
            FreeConsole();
            return null;
        }

        // New session: no controlling terminal, not in the spawner's process group, so a terminal
        // hang-up or the GUI's group being signalled does not reach the daemon (or its shells).
        // A failure (EPERM: already a process-group leader) is not fatal - stdio is still
        // detached below - but the daemon then shares the spawner's session, so say so.
        string? problem = null;
        if (setsid() < 0)
        {
            problem = $"setsid() failed (errno {Marshal.GetLastPInvokeError()}); the daemon stays in its parent's session and may receive its hang-ups";
        }

        int devNull = open("/dev/null", 2 /* O_RDWR */);
        if (devNull >= 0)
        {
            _ = dup2(devNull, 0);
            _ = dup2(devNull, 1);
            _ = dup2(devNull, 2);
            if (devNull > 2) _ = close(devNull);
        }

        return problem;
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
