using System.Globalization;
using System.Text.Json;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Daemon;
using Ntilde.Mux.TextClient;

namespace Ntilde.Mux.Cli;

/// <summary>
/// The mux verbs <c>serve|ls|kill|kill-server|attach</c> (Phase 2 spec §5, §6) and, for the standalone
/// <c>ntilde-mux</c>, <c>proxy --stdio</c> and <c>--version</c> (Phase 4 spec §8.1), hosted by whichever
/// executable supplies a <see cref="MuxCliHost"/> (Phase 4 spec §6.2): the App's <c>ntilde mux</c>
/// and the standalone <c>ntilde-mux</c>. Exit codes: 0 success, 1 the operation failed (including "no
/// daemon running"), 2 the command line was wrong - an unknown verb, or one the host does not offer.
/// <c>attach</c> and <c>proxy</c> differ: see <see cref="Attach"/> and <see cref="MuxProxyExitCodes"/>.
/// <c>serve</c> exits <see cref="MuxServeHost.LockHeldExitCode"/> (3) when another daemon owns this
/// root's lock. Writes text only to the writers it is handed, never to the console itself; the one
/// exception is <c>proxy</c>, whose stdin and stdout are the raw console streams it pumps.
/// </summary>
public static class MuxCli
{
    // The verbs Execute dispatches. --version is a verb spelled as a flag: ntilde-mux --version.
    private static readonly (string Name, MuxCliVerbs Verb)[] VerbNames =
    [
        ("serve", MuxCliVerbs.Serve),
        ("ls", MuxCliVerbs.Ls),
        ("kill", MuxCliVerbs.Kill),
        ("kill-server", MuxCliVerbs.KillServer),
        ("attach", MuxCliVerbs.Attach),
        ("probe-console", MuxCliVerbs.ProbeConsole),
        ("proxy", MuxCliVerbs.Proxy),
        ("version", MuxCliVerbs.Version),
        ("--version", MuxCliVerbs.Version),
    ];

    // One usage line per verb, listed only when the host offers it. probe-console, a diagnostic, is
    // never listed. The App's set prints exactly the text it always has (its tests pin it).
    private static readonly (MuxCliVerbs Verb, string Line)[] UsageLines =
    [
        (MuxCliVerbs.Serve, "serve [--idle-exit-minutes N] [--foreground]"),
        (MuxCliVerbs.Ls, "ls [--json]"),
        (MuxCliVerbs.Kill, "kill <sessionId>"),
        (MuxCliVerbs.KillServer, "kill-server [--force]"),
        (MuxCliVerbs.Attach, "attach <sessionId|prefix> [--read-only]"),
        (MuxCliVerbs.Proxy, "proxy --stdio"),
        (MuxCliVerbs.Version, "--version [--json]"),
    ];

    /// <summary>Lines joined by the platform's newline, the same as the WriteLine that ends them.</summary>
    private static string Usage(MuxCliHost host)
    {
        var usage = new System.Text.StringBuilder("Usage:");
        foreach ((MuxCliVerbs verb, string line) in UsageLines)
        {
            if ((host.Verbs & verb) == verb) usage.Append(Environment.NewLine).Append("  ").Append(host.UsagePrefix).Append(' ').Append(line);
        }

        return usage.ToString();
    }

    // A raw literal, so it carries the source file's line endings: CRLF in every checkout
    // (.gitattributes). Normalised to the platform's newline here, so ntilde-mux on Linux prints \n,
    // the same as the WriteLine that ends it.
    private static string AttachUsage(MuxCliHost host) => $"""
        Usage: {host.UsagePrefix} attach <sessionId|prefix> [--read-only]

          Shows a multiplexer session in this terminal. The id (or a unique prefix of at least
          4 characters) comes from `{host.UsagePrefix} ls`. Detach with Ctrl+\ then d (Ctrl may stay held);
          Ctrl+\ Ctrl+\ sends a literal Ctrl+\. --read-only shows the session without sending
          input (a convenience, not a security boundary). Exit codes: 0 detached, 1 the session
          ended, 2 an error.
        """.ReplaceLineEndings();

    /// <summary>Test seam: the console `attach` draws on. Null = the real terminal (ConsoleSurfaces.Create).</summary>
    internal static Func<IConsoleSurface>? ConsoleFactoryForTest { get; set; }

    /// <summary>Whether <paramref name="verb"/> (any case) names a verb that <paramref name="verbs"/> offers.</summary>
    public static bool IsKnownVerb(string verb, MuxCliVerbs verbs)
    {
        ArgumentNullException.ThrowIfNull(verb);
        MuxCliVerbs named = Resolve(verb);
        return named != MuxCliVerbs.None && (verbs & named) == named;
    }

    private static MuxCliVerbs Resolve(string verb)
    {
        foreach ((string name, MuxCliVerbs v) in VerbNames)
        {
            if (string.Equals(verb, name, StringComparison.OrdinalIgnoreCase)) return v;
        }

        return MuxCliVerbs.None;
    }

    /// <param name="verbArgs">The verb and its arguments: what follows <c>ntilde mux</c> or <c>ntilde-mux</c>.</param>
    public static int Execute(string[] verbArgs, TextWriter stdout, TextWriter stderr, MuxCliHost host)
    {
        ArgumentNullException.ThrowIfNull(verbArgs);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(host);
        if (verbArgs.Length < 1 || !IsKnownVerb(verbArgs[0], host.Verbs)) return Fail(stderr, Usage(host));

        string descriptorPath = host.Paths.DescriptorPath;
        try
        {
            return Resolve(verbArgs[0]) switch
            {
                MuxCliVerbs.Serve => Serve(verbArgs, stderr, host),
                MuxCliVerbs.Ls => List(verbArgs, stdout, stderr, descriptorPath, host),
                MuxCliVerbs.Kill => Kill(verbArgs, stdout, stderr, descriptorPath, host),
                MuxCliVerbs.KillServer => KillServer(verbArgs, stdout, stderr, descriptorPath, host),
                MuxCliVerbs.Attach => Attach(verbArgs, stdout, stderr, descriptorPath, host),
                MuxCliVerbs.ProbeConsole => ProbeConsole(verbArgs, stdout, stderr, host),
                MuxCliVerbs.Proxy => Proxy(verbArgs, stderr, host),
                MuxCliVerbs.Version => Version(verbArgs, stdout, stderr, host),
                _ => Fail(stderr, Usage(host)),
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
    /// not an IOException, and one escaping here reached the executable's top-level catch (the GUI's
    /// Program.Main writes its startup-error file and rethrows) - a crash for what is an "endpoint
    /// unusable" error.
    /// </summary>
    internal static bool IsReportableFailure(Exception ex) =>
        ex is IOException or TimeoutException or MuxProtocolException or UnauthorizedAccessException or MuxUnavailableException
            or System.Net.Sockets.SocketException;

    /// <param name="verbArgs"><c>serve</c> and its options.</param>
    internal static bool TryParseServe(string[] verbArgs, out MuxServeOptions options, out string? error)
    {
        TimeSpan idle = TimeSpan.FromMinutes(10);
        bool foreground = false;
        options = new MuxServeOptions(idle, foreground);
        int i = 1;
        while (i < verbArgs.Length)
        {
            string arg = verbArgs[i++];
            switch (arg)
            {
                case "--foreground":
                    foreground = true;
                    break;
                case "--idle-exit-minutes":
                    // The value is the next argument: consumed here, so the loop resumes after it.
                    if (i >= verbArgs.Length || !int.TryParse(verbArgs[i++], NumberStyles.None, CultureInfo.InvariantCulture, out int m))
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

    private static int Serve(string[] verbArgs, TextWriter stderr, MuxCliHost host)
    {
        if (!TryParseServe(verbArgs, out MuxServeOptions options, out string? error)) return Fail(stderr, error + Environment.NewLine + Usage(host));

        // The executable binds no console for serve (a daemon must not attach to the launching
        // console), so a --foreground run, which is meant to be watched, binds it here - otherwise
        // its output is invisible on Windows (the GUI's exe is a WinExe, with no console of its own).
        if (options.Foreground && host.PrepareForegroundConsole is { } prepare)
        {
            // Rebinding replaces Console.Error: follow it only when that is what we were handed
            // (a caller's own writer, e.g. a test's, is kept).
            bool wasConsoleError = ReferenceEquals(stderr, Console.Error);
            prepare();
            if (wasConsoleError) stderr = Console.Error;
        }

        return MuxServeHost.Run(options, host.Paths, host.SessionFactory(), stderr);
    }

    /// <summary>
    /// <c>proxy --stdio</c> (Phase 4 spec §8.1): sshd's exec channel to this host's daemon, spawned on
    /// demand. Its stdout is the channel, so everything it says - the usage, the launcher's log - goes
    /// to stderr. The raw standard streams, not Console.In/Out: bytes, unbuffered and undecoded. They
    /// are not disposed here: the pump may still be blocked reading stdin, and the process exit closes
    /// both. On Unix, once the daemon side ended, the proxy ends fds 1 and 2 themselves before it waits for the
    /// daemon's process (<see cref="UnixChannelStdio"/>, residual R1): disposing a console stream only closes its
    /// copy of the descriptor.
    /// </summary>
    private static int Proxy(string[] verbArgs, TextWriter stderr, MuxCliHost host)
    {
        if (verbArgs.Length != 2 || !string.Equals(verbArgs[1], "--stdio", StringComparison.Ordinal)) return Fail(stderr, Usage(host));

        void Log(string line) => stderr.WriteLine($"[ntilde-mux] {line}");
        return MuxProxyCommand.Run(Console.OpenStandardInput(), Console.OpenStandardOutput(), stderr,
            ct => MuxDaemonLauncher.CreateDefault(Log, host.ServeArguments, host.Paths, KillServerCommand(host)).EnsureEndpointStreamAsync(ct),
            endStdio: OperatingSystem.IsWindows() ? null : UnixChannelStdio.EndStdoutAndStderr);
    }

    /// <summary><c>--version [--json]</c>: one line, plain or <see cref="MuxVersionInfo"/> as JSON.</summary>
    private static int Version(string[] verbArgs, TextWriter stdout, TextWriter stderr, MuxCliHost host)
    {
        bool json = false;
        foreach (string arg in verbArgs.Skip(1))
        {
            if (arg != "--json") return Fail(stderr, Usage(host));
            json = true;
        }

        MuxVersionInfo info = MuxVersionInfo.Current(host.Version);
        stdout.WriteLine(json ? JsonSerializer.Serialize(info, MuxCliJsonContext.Default.MuxVersionInfo) : info.ToDisplayString());
        return 0;
    }

    /// <summary>The command a failure's hint tells the user to run: this host's own, not the App's.</summary>
    private static string KillServerCommand(MuxCliHost host) => $"{host.UsagePrefix} kill-server --force";

    private static MuxClient? Connect(string descriptorPath, TextWriter stderr, MuxCliHost host)
    {
        var launcher = new MuxDaemonLauncher(descriptorPath, new NoSpawn(), new MuxClientOptions { ClientKind = "ntilde-cli" },
            killServerCommand: KillServerCommand(host));
        MuxClient? client = launcher.TryConnectExistingAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (client is null) stderr.WriteLine("No multiplexer is running.");
        return client;
    }

    private sealed class NoSpawn : IMuxDaemonSpawner { public void Spawn() => throw new InvalidOperationException("CLI verbs never start a daemon."); }

    private static int List(string[] verbArgs, TextWriter stdout, TextWriter stderr, string descriptorPath, MuxCliHost host)
    {
        bool json = verbArgs.Skip(1).Any(a => a == "--json");
        if (verbArgs.Skip(1).Any(a => a != "--json")) return Fail(stderr, Usage(host));
        using MuxClient? client = Connect(descriptorPath, stderr, host);
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

    private static int Kill(string[] verbArgs, TextWriter stdout, TextWriter stderr, string descriptorPath, MuxCliHost host)
    {
        if (verbArgs.Length != 2 || !Guid.TryParse(verbArgs[1], out Guid id)) return Fail(stderr, Usage(host));
        using MuxClient? client = Connect(descriptorPath, stderr, host);
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

    private static int KillServer(string[] verbArgs, TextWriter stdout, TextWriter stderr, string descriptorPath, MuxCliHost host)
    {
        bool force = false;
        foreach (string arg in verbArgs.Skip(1))
        {
            if (arg != "--force") return Fail(stderr, Usage(host));
            force = true;
        }

        MuxDiscovery.TryReadDescriptor(descriptorPath, out MuxEndpointDescriptor? d);
        try
        {
            using MuxClient? client = Connect(descriptorPath, stderr, host);
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
        // Name-only liveness here: the start time is checked below, on the Process object that is
        // about to be killed, so a recycled pid gets the specific "no longer the multiplexer" answer.
        if (!MuxDiscovery.TryReadDescriptor(descriptorPath, out MuxEndpointDescriptor? d)
            || !MuxDiscovery.IsProcessAlive(d.Pid, d.ProcessName))
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

            // The name alone does not tell a recycled pid from the daemon (another ntilde, say):
            // the start time recorded in the descriptor does.
            if (!MuxDiscovery.StartTimeMatches(process, d.StartTime))
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
    private static int Attach(string[] verbArgs, TextWriter stdout, TextWriter stderr, string descriptorPath, MuxCliHost host)
    {
        if (verbArgs.Skip(1).Any(a => a is "--help" or "-h"))
        {
            stdout.WriteLine(AttachUsage(host));
            return 0;
        }

        string? target = null;
        bool readOnly = false;
        foreach (string arg in verbArgs.Skip(1))
        {
            if (arg == "--read-only") readOnly = true;
            else if (target is null && !arg.StartsWith('-')) target = arg;
            else return Fail(stderr, Usage(host));
        }

        if (target is null) return Fail(stderr, Usage(host));

        MuxClient? client;
        try
        {
            var launcher = new MuxDaemonLauncher(descriptorPath, new NoSpawn(), new MuxClientOptions { ClientKind = "ntilde-attach" },
                killServerCommand: KillServerCommand(host));
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
                stderr.WriteLine($"mux: the running multiplexer is too old for --read-only; run '{host.UsagePrefix} kill-server' to replace it.");
                return 2;
            }

            IConsoleSurface console;
            try
            {
                console = (ConsoleFactoryForTest ?? ConsoleSurfaces.Create)();
            }
            catch (ConsoleUnavailableException ex)
            {
                stderr.WriteLine($"mux: {ex.Message}");
                return 2;
            }

            using (console)
            using (var textClient = new TextClientSession(client, id, console,
                new TextClientOptions { ReadOnly = readOnly, HandleSignals = ConsoleFactoryForTest is null }))
            {
                return textClient.Run(stderr);
            }
        }
    }

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

    /// <summary>In a probe's restored state: the Windows input mode after the restore differs from before raw mode.</summary>
    internal const string RestoreMismatch = "restore-mismatch";

    private static readonly TimeSpan ProbeKeysTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Hidden diagnostic: raw mode in and out, and the size before and while raw. Exit 0 when raw mode
    /// takes effect, is restored, and the size reads the same in both modes. On Windows the input mode
    /// is read back, never assumed. <c>--keys</c> then echoes what each read returns, through the
    /// attach's own chord, until Ctrl+\ then d (exit 0) or 20 s without it (exit 1).
    /// </summary>
    private static int ProbeConsole(string[] verbArgs, TextWriter stdout, TextWriter stderr, MuxCliHost host)
    {
        bool keys = verbArgs.Length == 2 && string.Equals(verbArgs[1], "--keys", StringComparison.Ordinal);
        if (verbArgs.Length > 1 && !keys) return Fail(stderr, $"usage: {host.UsagePrefix} probe-console [--keys]");

        IConsoleSurface surface;
        try
        {
            surface = ConsoleSurfaces.Create();
        }
        catch (ConsoleUnavailableException ex)
        {
            stderr.WriteLine($"mux: {ex.Message}");
            return 2;
        }

        using (surface)
        {
            (int Cols, int Rows) before = surface.Size;
            (int Cols, int Rows) inRaw;
            string? rawState = null;
            string? restoredState = null;
            bool? writeOk = null;
            bool? chord = null;
            string writeNote = string.Empty;
            uint? modeBefore = OperatingSystem.IsWindows() ? ((WindowsConsoleSurface)surface).InputMode : null;
            surface.EnterRawMode();
            try
            {
                inRaw = surface.Size;
                if (!OperatingSystem.IsWindows())
                {
                    rawState = SttyState();
                }
                else
                {
                    var windows = (WindowsConsoleSurface)surface;
                    rawState = windows.InputMode is uint mode ? WindowsConsoleSurface.DescribeInputMode(mode) : "input mode unreadable";
                    writeOk = ProbeWindowsWrite(windows, out writeNote);
                }

                if (keys) chord = ProbeKeys(surface);
            }
            finally
            {
                surface.RestoreMode();
            }

            if (!OperatingSystem.IsWindows()) restoredState = SttyState();
            else if (((WindowsConsoleSurface)surface).InputMode is var after && after != modeBefore)
            {
                restoredState = $"{RestoreMismatch} {DescribeMode(modeBefore)} -> {DescribeMode(after)}";
            }

            string writeVerdict = writeOk == true ? "ok" : "FAILED";
            string write = writeOk is null ? string.Empty : $"; write {writeVerdict} ({writeNote})";
            string windowsMode = OperatingSystem.IsWindows() ? $" ({rawState})" : string.Empty;
            string chordVerdict = chord == true ? "chord detected" : $"NO chord within {ProbeKeysTimeout.TotalSeconds:0} s";
            string keysNote = chord is null ? string.Empty : $"; keys {chordVerdict}";
            // Non-zero: something sharing this console switched it out of raw mode while the probe ran.
            if (chord is not null && OperatingSystem.IsWindows()) keysNote += $"; raw mode put back {((WindowsConsoleSurface)surface).RawModeReasserts} time(s)";
            stdout.WriteLine($"size before raw {before.Cols}x{before.Rows}, while raw {inRaw.Cols}x{inRaw.Rows} ({(ProbeSizeStable(before, inRaw) ? "stable" : "UNSTABLE")}); "
                + $"raw mode {(ProbeRawApplied(rawState) ? "ok" : "NOT applied")}{windowsMode}; restore {(ProbeRestored(restoredState) ? "ok" : "FAILED")}{write}{keysNote}");
            return ProbeVerdict(rawState, restoredState, before, inRaw, writeOk) && chord != false ? 0 : 1;
        }
    }

    /// <summary>
    /// Windows: a marker through the surface's own Write must advance the cursor by exactly its length.
    /// It pins the UTF-16 marshalling of WriteConsoleW: marshalled as Ansi, a `ref char` goes through a
    /// one-byte temporary and the console prints a different number of cells.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool ProbeWindowsWrite(WindowsConsoleSurface surface, out string note)
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

    /// <summary>
    /// The keystrokes as the attach sees them: one line per read with each char in hex, fed through the
    /// same <see cref="DetachChord"/>. True when the chord completed in time.
    /// </summary>
    private static bool ProbeKeys(IConsoleSurface surface)
    {
        surface.Write($"\r\nprobe-console: press keys; Ctrl+\\ then d ends it ({ProbeKeysTimeout.TotalSeconds:0} s)\r\n");
        using var detected = new ManualResetEventSlim(false);
        var reader = new Thread(() =>
        {
            var chord = new DetachChord();
            var ignored = new System.Text.StringBuilder();
            char[] buffer = new char[256];
            try
            {
                while (true)
                {
                    int n = surface.Read(buffer);
                    if (n <= 0) return;
                    surface.Write($"read {n}: {string.Join(' ', buffer.Take(n).Select(c => ((int)c).ToString("X2", CultureInfo.InvariantCulture)))}\r\n");
                    if (chord.Feed(buffer.AsSpan(0, n), ignored))
                    {
                        detected.Set();
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // the console went away: the timeout reports it
            }
        })
        { IsBackground = true, Name = "MuxProbeKeys" };
        reader.Start();

        // A read still blocked at the timeout is abandoned: the thread is background and the process ends.
        return detected.Wait(ProbeKeysTimeout);
    }

    /// <summary>stty's flags, or <see cref="WindowsConsoleSurface.DescribeInputMode"/>'s on Windows, which adds vtinput.</summary>
    internal static bool ProbeRawApplied(string? rawState) =>
        rawState is null || (rawState.Contains("-icanon", StringComparison.Ordinal) && rawState.Contains("-isig", StringComparison.Ordinal)
            && !rawState.Contains("-vtinput", StringComparison.Ordinal));

    internal static bool ProbeRestored(string? restoredState) =>
        restoredState is null || (!restoredState.Contains("-icanon", StringComparison.Ordinal) && !restoredState.Contains(RestoreMismatch, StringComparison.Ordinal));

    private static string DescribeMode(uint? mode) => mode is uint m ? $"0x{m:X4}" : "unreadable";

    internal static bool ProbeSizeStable((int Cols, int Rows) before, (int Cols, int Rows) inRaw) => before == inRaw && before.Cols > 1;

    /// <summary>The exit code's rule, built from the same helpers the printed line uses. <paramref name="writeOk"/>: null = not measured (Unix).</summary>
    internal static bool ProbeVerdict(string? rawState, string? restoredState, (int Cols, int Rows) before, (int Cols, int Rows) inRaw, bool? writeOk) =>
        ProbeRawApplied(rawState) && ProbeRestored(restoredState) && ProbeSizeStable(before, inRaw) && writeOk != false;

    /// <summary>`stty -a` on our own terminal: the child inherits stdin, which is the TTY.</summary>
    private static string SttyState()
    {
        // Absolute path, never an implicit PATH search (csharpsquid:S4036). Missing stty reads as
        // "raw mode NOT applied", a failed probe, rather than the Win32Exception a bare name threw.
        string? stty = Ntilde.Pty.ShellHelper.ResolveSystemTool("stty");
        if (stty is null) return "stty not found";
        var psi = new System.Diagnostics.ProcessStartInfo(stty, "-a") { UseShellExecute = false, RedirectStandardOutput = true };
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
