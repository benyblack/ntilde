using System.Globalization;
using System.Text.Json;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;

namespace Ntilde.Shell.Mux;

internal sealed record MuxServeOptions(TimeSpan IdleExitAfter, bool Foreground);

/// <summary>
/// <c>ntilde mux serve|ls|kill|kill-server|attach</c> (spec §5, §6). Exit codes: 0 success, 1 the
/// operation failed (including "no daemon running"), 2 the command line was wrong. <c>attach</c>
/// differs: see <see cref="Attach"/>.
/// </summary>
public static class MuxCommand
{
    private const string Usage = """
        Usage:
          ntilde mux serve [--idle-exit-minutes N] [--foreground]
          ntilde mux ls [--json]
          ntilde mux kill <sessionId>
          ntilde mux kill-server [--force]
          ntilde mux attach <sessionId|prefix> [--read-only]
        """;

    private const string AttachUsage = """
        Usage: ntilde mux attach <sessionId|prefix> [--read-only]

          Shows a multiplexer session in this terminal. The id (or a unique prefix of at least
          4 characters) comes from `ntilde mux ls`. Detach with Ctrl+\ then d; Ctrl+\ Ctrl+\ sends
          a literal Ctrl+\. --read-only shows the session without sending input (a convenience,
          not a security boundary). Exit codes: 0 detached, 1 the session ended, 2 an error.

          Windows: from PowerShell, or any prompt that does not wait for GUI programs, run
            cmd /c ntilde mux attach <id>
          so the prompt does not compete for your keystrokes.
        """;

    /// <summary>Set by Program.cs: PrepareInteractive attached this (GUI) process to its parent's console.</summary>
    internal static bool AttachedToParentConsole { get; set; }

    /// <summary>Test seam: the console `mux attach` draws on. Null = the real terminal (ConsoleSurfaces.Create).</summary>
    internal static Func<Ntilde.Mux.TextClient.IConsoleSurface>? ConsoleFactoryForTest { get; set; }

    public static bool IsSupportedCliMode(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Length > 0 && string.Equals(args[0], "mux", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsServe(string[] args) =>
        IsSupportedCliMode(args) && args.Length > 1 && string.Equals(args[1], "serve", StringComparison.OrdinalIgnoreCase);

    public static bool IsAttach(string[] args) =>
        IsSupportedCliMode(args) && args.Length > 1 && string.Equals(args[1], "attach", StringComparison.OrdinalIgnoreCase);

    /// <param name="rootOverride">Test seam: app-data root. Null uses <see cref="MuxDiscovery.GetRootDirectory"/>.</param>
    public static int Execute(string[] args, TextWriter stdout, TextWriter stderr, string? rootOverride = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        if (args.Length < 2) return Fail(stderr, Usage);

        string descriptorPath = MuxDiscovery.GetDescriptorPath(rootOverride ?? MuxDiscovery.GetRootDirectory());
        try
        {
            return args[1].ToLowerInvariant() switch
            {
                "serve" => Serve(args, stderr),
                "ls" => List(args, stdout, stderr, descriptorPath),
                "kill" => Kill(args, stdout, stderr, descriptorPath),
                "kill-server" => KillServer(args, stdout, stderr, descriptorPath),
                "attach" => Attach(args, stdout, stderr, descriptorPath),
                "probe-console" => ProbeConsole(stdout, stderr),
                _ => Fail(stderr, Usage),
            };
        }
        catch (Exception ex) when (IsReportableFailure(ex))
        {
            stderr.WriteLine($"mux: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Failures a verb reports as "mux: ..." with exit 1. SocketException is listed on its own: it is
    /// not an IOException, and one escaping here reached Program.Main's catch, which writes the GUI's
    /// startup-error file and rethrows - a crash for what is an "endpoint unusable" error.
    /// </summary>
    internal static bool IsReportableFailure(Exception ex) =>
        ex is IOException or TimeoutException or MuxProtocolException or UnauthorizedAccessException or MuxUnavailableException
            or System.Net.Sockets.SocketException;

    internal static bool TryParseServe(string[] args, out MuxServeOptions options, out string? error)
    {
        TimeSpan idle = TimeSpan.FromMinutes(10);
        bool foreground = false;
        options = new MuxServeOptions(idle, foreground);
        int i = 2;
        while (i < args.Length)
        {
            string arg = args[i++];
            switch (arg)
            {
                case "--foreground":
                    foreground = true;
                    break;
                case "--idle-exit-minutes":
                    // The value is the next argument: consumed here, so the loop resumes after it.
                    if (i >= args.Length || !int.TryParse(args[i++], NumberStyles.None, CultureInfo.InvariantCulture, out int m))
                    {
                        error = "--idle-exit-minutes needs a non-negative whole number.";
                        return false;
                    }

                    idle = TimeSpan.FromMinutes(m);
                    break;
                default:
                    error = $"Unknown option '{arg}'.";
                    return false;
            }
        }

        options = new MuxServeOptions(idle, foreground);
        error = null;
        return true;
    }

    private static int Serve(string[] args, TextWriter stderr)
    {
        if (!TryParseServe(args, out MuxServeOptions options, out string? error)) return Fail(stderr, error + Environment.NewLine + Usage);

        // Program.cs skips CliConsoleBindings.Prepare for serve (a daemon must not attach to the
        // launching console), so a --foreground run, which is meant to be watched, binds it here -
        // otherwise its output is invisible on Windows (a WinExe has no console of its own).
        if (options.Foreground)
        {
            // Rebinding replaces Console.Error: follow it only when that is what we were handed
            // (a caller's own writer, e.g. a test's, is kept).
            bool wasConsoleError = ReferenceEquals(stderr, Console.Error);
            CliConsoleBindings.Prepare();
            if (wasConsoleError) stderr = Console.Error;
        }

        return MuxDaemonProcess.Run(options, stderr);
    }

    private static MuxClient? Connect(string descriptorPath, TextWriter stderr)
    {
        var launcher = new MuxDaemonLauncher(descriptorPath, new NoSpawn(), new MuxClientOptions { ClientKind = "ntilde-cli" });
        MuxClient? client = launcher.TryConnectExistingAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (client is null) stderr.WriteLine("No multiplexer is running.");
        return client;
    }

    private sealed class NoSpawn : IMuxDaemonSpawner { public void Spawn() => throw new InvalidOperationException("CLI verbs never start a daemon."); }

    private static int List(string[] args, TextWriter stdout, TextWriter stderr, string descriptorPath)
    {
        bool json = args.Skip(2).Any(a => a == "--json");
        if (args.Skip(2).Any(a => a != "--json")) return Fail(stderr, Usage);
        using MuxClient? client = Connect(descriptorPath, stderr);
        if (client is null) return 1;

        IReadOnlyList<SessionSummary> sessions = client.ListSessionsAsync().GetAwaiter().GetResult();
        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new ListSessionsResult { Sessions = sessions }, MuxJsonContext.Default.ListSessionsResult));
            return 0;
        }

        if (sessions.Count == 0)
        {
            stdout.WriteLine("No sessions.");
            return 0;
        }

        stdout.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-36}  {1,-18}  {2,8}  {3,-9}  {4}", "ID", "STATE", "ATTACHED", "SIZE", "TITLE"));
        foreach (SessionSummary s in sessions)
        {
            string state = DescribeState(s);
            stdout.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-36}  {1,-18}  {2,8}  {3,-9}  {4}",
                s.SessionId, state, s.AttachedClients, $"{s.Cols}x{s.Rows}", s.Title));
        }

        return 0;
    }

    private static string DescribeState(SessionSummary s)
    {
        if (s.Faulted) return "faulted";
        if (!s.Running) return $"exited {s.ExitCode}";
        // Deliberately detached (spec §7.7): the GUI leaves these alone, so ls is where they are found.
        return s.DetachedByUser ? "running, detached" : "running";
    }

    private static int Kill(string[] args, TextWriter stdout, TextWriter stderr, string descriptorPath)
    {
        if (args.Length != 3 || !Guid.TryParse(args[2], out Guid id)) return Fail(stderr, Usage);
        using MuxClient? client = Connect(descriptorPath, stderr);
        if (client is null) return 1;
        try
        {
            client.KillAsync(id).GetAwaiter().GetResult();
        }
        catch (MuxProtocolException ex) when (ex.Code == MuxErrorCodes.UnknownSession)
        {
            stderr.WriteLine($"No session {id}.");
            return 1;
        }

        stdout.WriteLine($"Killed {id}.");
        return 0;
    }

    private static int KillServer(string[] args, TextWriter stdout, TextWriter stderr, string descriptorPath)
    {
        bool force = false;
        foreach (string arg in args.Skip(2))
        {
            if (arg != "--force") return Fail(stderr, Usage);
            force = true;
        }

        MuxDiscovery.TryReadDescriptor(descriptorPath, out MuxEndpointDescriptor? d);
        try
        {
            using MuxClient? client = Connect(descriptorPath, stderr);
            if (client is null) return 1;
            client.ShutdownServerAsync().GetAwaiter().GetResult();
        }
        catch (MuxUnavailableException ex) when (ex.VersionMismatch)
        {
            // It cannot be asked to stop: it does not speak our protocol (PR #489 follow-up).
            return KillByPid(descriptorPath, force, stdout, stderr);
        }

        // Wait for it to really be gone, so "kill-server && start" cannot race the old daemon.
        if (!WaitForExitOrReport(descriptorPath, d, stderr)) return 1;

        stdout.WriteLine("Multiplexer stopped.");
        return 0;
    }

    /// <summary>Shared by <see cref="KillServer"/> and <see cref="KillByPid"/>: report and fail if the daemon is still there after 5 s.</summary>
    private static bool WaitForExitOrReport(string descriptorPath, MuxEndpointDescriptor? before, TextWriter stderr)
    {
        if (MuxDaemonExit.WaitForExit(descriptorPath, before, TimeSpan.FromSeconds(5), Environment.ProcessId)) return true;
        stderr.WriteLine("Multiplexer did not stop within 5 s.");
        return false;
    }

    /// <summary>
    /// The version-mismatch fallback: terminate the daemon named by the descriptor, but only after
    /// verifying that its pid is alive under the recorded process name (a recycled pid is never
    /// killed), and only with <paramref name="force"/>. Never this process.
    /// </summary>
    internal static int KillByPid(string descriptorPath, bool force, TextWriter stdout, TextWriter stderr)
    {
        if (!MuxDiscovery.TryReadLiveDescriptor(descriptorPath, out MuxEndpointDescriptor? d))
        {
            stderr.WriteLine("The multiplexer speaks a different protocol version, and its process could not be verified (pid and process name); nothing was terminated.");
            return 1;
        }

        if (d.Pid == Environment.ProcessId)
        {
            stderr.WriteLine("Refusing to terminate this process.");
            return 1;
        }

        if (!force)
        {
            stderr.WriteLine($"The running multiplexer (pid {d.Pid}) speaks a different protocol version. Re-run with --force to terminate it (this ends its sessions).");
            return 1;
        }

        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(d.Pid);
            // The live check above and this Kill are not atomic: the pid could have exited and been
            // recycled for an unrelated process in between. Re-verify on this exact Process object,
            // right before killing it, so that window never kills the wrong process.
            if (!string.Equals(process.ProcessName, d.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                stderr.WriteLine($"pid {d.Pid} is no longer the multiplexer; nothing was terminated.");
                return 1;
            }

            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            stderr.WriteLine($"Could not terminate pid {d.Pid}: {ex.Message}");
            return 1;
        }

        if (!WaitForExitOrReport(descriptorPath, d, stderr)) return 1;

        MuxDiscovery.DeleteDescriptorIfOwned(descriptorPath, d.Pid);
        stdout.WriteLine($"Multiplexer (pid {d.Pid}) terminated.");
        return 0;
    }

    /// <summary>
    /// The text client (spec §6). Exit codes: 0 detached, 1 the session ended, 2 usage or any
    /// connection problem (including "no multiplexer running", unlike the other verbs).
    /// </summary>
    private static int Attach(string[] args, TextWriter stdout, TextWriter stderr, string descriptorPath)
    {
        if (args.Skip(2).Any(a => a is "--help" or "-h"))
        {
            stdout.WriteLine(AttachUsage);
            return 0;
        }

        string? target = null;
        bool readOnly = false;
        foreach (string arg in args.Skip(2))
        {
            if (arg == "--read-only") readOnly = true;
            else if (target is null && !arg.StartsWith('-')) target = arg;
            else return Fail(stderr, Usage);
        }

        if (target is null) return Fail(stderr, Usage);

        MuxClient? client;
        try
        {
            var launcher = new MuxDaemonLauncher(descriptorPath, new NoSpawn(), new MuxClientOptions { ClientKind = "ntilde-attach" });
            client = launcher.TryConnectExistingAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (IsReportableFailure(ex))
        {
            stderr.WriteLine($"mux: {ex.Message}");
            return 2;
        }

        if (client is null)
        {
            stderr.WriteLine("No multiplexer is running.");
            return 2;
        }

        using (client)
        {
            Guid id;
            try
            {
                if (!TryResolveSession(target, client.ListSessionsAsync().GetAwaiter().GetResult(), out id, out string? why))
                {
                    stderr.WriteLine($"mux: {why}");
                    return 2;
                }
            }
            catch (Exception ex) when (IsReportableFailure(ex))
            {
                stderr.WriteLine($"mux: {ex.Message}");
                return 2;
            }

            // The daemon names each shell's session in its environment. Drawing a session into its
            // own terminal copies every frame into the screen being copied: a loop, not a view.
            if (IsEnclosingSession(id, Environment.GetEnvironmentVariable(MuxServer.SessionEnvironmentVariable)))
            {
                stderr.WriteLine($"mux: you are inside session {id.ToString("N")[..8]} already; attaching to it from itself would loop. Use another terminal.");
                return 2;
            }

            if (readOnly && client.ProtocolVersion < MuxProtocol.SessionEventsVersion)
            {
                stderr.WriteLine("mux: the running multiplexer is too old for --read-only; run 'ntilde mux kill-server' to replace it.");
                return 2;
            }

            // Before the surface exists, so before raw mode: the line must read as a normal line.
            if (AttachConsoleHint(OperatingSystem.IsWindows(), AttachedToParentConsole, target) is { } hint) stderr.WriteLine(hint);

            Ntilde.Mux.TextClient.IConsoleSurface console;
            try
            {
                console = (ConsoleFactoryForTest ?? Ntilde.Mux.TextClient.ConsoleSurfaces.Create)();
            }
            catch (Ntilde.Mux.TextClient.ConsoleUnavailableException ex)
            {
                stderr.WriteLine($"mux: {ex.Message}");
                return 2;
            }

            using (console)
            using (var textClient = new Ntilde.Mux.TextClient.TextClientSession(client, id, console,
                new Ntilde.Mux.TextClient.TextClientOptions { ReadOnly = readOnly, HandleSignals = ConsoleFactoryForTest is null }))
            {
                return textClient.Run(stderr);
            }
        }
    }

    /// <summary>
    /// The review's decision 1: the GUI exe on a parent console shares the keyboard with a prompt that
    /// does not wait for it. One line on stderr, before raw mode. Null when it does not apply.
    /// </summary>
    internal static string? AttachConsoleHint(bool isWindows, bool attachedToParentConsole, string target) =>
        isWindows && attachedToParentConsole ? $"mux: if keystrokes are lost, run via cmd /c ntilde mux attach {target} (ignore if already under cmd /c)" : null;

    private static bool IsEnclosingSession(Guid target, string? enclosing) =>
        Guid.TryParse(enclosing, out Guid inside) && inside == target;

    /// <summary>A full id, or a unique prefix of at least 4 hex characters (dashes ignored).</summary>
    internal static bool TryResolveSession(string target, IReadOnlyList<SessionSummary> sessions, out Guid id, out string? why)
    {
        if (Guid.TryParse(target, out id))
        {
            Guid wanted = id;
            if (sessions.Any(s => s.SessionId == wanted))
            {
                why = null;
                return true;
            }

            why = $"No session {id}.";
            return false;
        }

        string prefix = target.Replace("-", string.Empty, StringComparison.Ordinal);
        id = Guid.Empty;
        if (prefix.Length < 4)
        {
            why = "A session id prefix needs at least 4 characters.";
            return false;
        }

        SessionSummary[] matches = sessions.Where(s => s.SessionId.ToString("N").StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 1)
        {
            id = matches[0].SessionId;
            why = null;
            return true;
        }

        why = matches.Length == 0 ? $"No session starts with '{target}'." : $"'{target}' matches {matches.Length} sessions; give more of the id.";
        return false;
    }

    /// <summary>
    /// Hidden diagnostic: raw mode in and out, and the size before and while raw. Exit 0 when raw mode
    /// takes effect, is restored, and the size reads the same in both modes.
    /// </summary>
    private static int ProbeConsole(TextWriter stdout, TextWriter stderr)
    {
        Ntilde.Mux.TextClient.IConsoleSurface surface;
        try
        {
            surface = Ntilde.Mux.TextClient.ConsoleSurfaces.Create();
        }
        catch (Ntilde.Mux.TextClient.ConsoleUnavailableException ex)
        {
            stderr.WriteLine($"mux: {ex.Message}");
            return 2;
        }

        using (surface)
        {
            (int Cols, int Rows) before = surface.Size;
            (int Cols, int Rows) inRaw;
            string? rawState = null;
            bool? writeOk = null;
            string writeNote = string.Empty;
            surface.EnterRawMode();
            try
            {
                inRaw = surface.Size;
                if (!OperatingSystem.IsWindows()) rawState = SttyState();
                else writeOk = ProbeWindowsWrite((Ntilde.Mux.TextClient.WindowsConsoleSurface)surface, out writeNote);
            }
            finally
            {
                surface.RestoreMode();
            }

            string? restoredState = OperatingSystem.IsWindows() ? null : SttyState();
            string write = writeOk is null ? string.Empty : $"; write {(writeOk.Value ? "ok" : "FAILED")} ({writeNote})";
            stdout.WriteLine($"size before raw {before.Cols}x{before.Rows}, while raw {inRaw.Cols}x{inRaw.Rows} ({(ProbeSizeStable(before, inRaw) ? "stable" : "UNSTABLE")}); "
                + $"raw mode {(ProbeRawApplied(rawState) ? "ok" : "NOT applied")}; restore {(ProbeRestored(restoredState) ? "ok" : "FAILED")}{write}");
            return ProbeVerdict(rawState, restoredState, before, inRaw, writeOk) ? 0 : 1;
        }
    }

    /// <summary>
    /// Windows: a marker through the surface's own Write must advance the cursor by exactly its length.
    /// It pins the UTF-16 marshalling of WriteConsoleW: marshalled as Ansi, a `ref char` goes through a
    /// one-byte temporary and the console prints a different number of cells.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool ProbeWindowsWrite(Ntilde.Mux.TextClient.WindowsConsoleSurface surface, out string note)
    {
        const string Marker = "mux probe-console \u00f1\u00df\u00e9: ";
        surface.Write("\r");
        (int Col, int Row)? from = surface.CursorPosition;
        surface.Write(Marker);
        (int Col, int Row)? to = surface.CursorPosition;
        if (from is not { } a || to is not { } b)
        {
            note = "cursor unreadable";
            return false;
        }

        note = $"cursor advanced {b.Col - a.Col} of {Marker.Length}";
        return b.Row == a.Row && b.Col - a.Col == Marker.Length;
    }

    internal static bool ProbeRawApplied(string? rawState) =>
        rawState is null || (rawState.Contains("-icanon", StringComparison.Ordinal) && rawState.Contains("-isig", StringComparison.Ordinal));

    internal static bool ProbeRestored(string? restoredState) => restoredState is null || !restoredState.Contains("-icanon", StringComparison.Ordinal);

    internal static bool ProbeSizeStable((int Cols, int Rows) before, (int Cols, int Rows) inRaw) => before == inRaw && before.Cols > 1;

    /// <summary>The exit code's rule, built from the same helpers the printed line uses. <paramref name="writeOk"/>: null = not measured (Unix).</summary>
    internal static bool ProbeVerdict(string? rawState, string? restoredState, (int Cols, int Rows) before, (int Cols, int Rows) inRaw, bool? writeOk) =>
        ProbeRawApplied(rawState) && ProbeRestored(restoredState) && ProbeSizeStable(before, inRaw) && writeOk != false;

    /// <summary>`stty -a` on our own terminal: the child inherits stdin, which is the TTY.</summary>
    private static string SttyState()
    {
        var psi = new System.Diagnostics.ProcessStartInfo("stty", "-a") { UseShellExecute = false, RedirectStandardOutput = true };
        using var p = System.Diagnostics.Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(5000);
        return output;
    }

    private static int Fail(TextWriter stderr, string message)
    {
        stderr.WriteLine(message);
        return 2;
    }
}
