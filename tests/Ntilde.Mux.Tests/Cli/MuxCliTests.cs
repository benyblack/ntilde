using System.Net.Sockets;
using System.Text.Json;
using Ntilde.Mux.Cli;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Daemon;
using Ntilde.Mux.Tests.Support;
using Ntilde.Pty;

namespace Ntilde.Mux.Tests.Cli;

/// <summary>
/// The mux verbs, moved from App.Tests' <c>MuxCommandTests</c> with the code they cover (Phase 4
/// spec §6.4). They run against a host shaped like the standalone <c>ntilde-mux</c>; the App adapter
/// keeps its own tests for what it supplies (dispatch, its usage text, the root override).
/// </summary>
public sealed class MuxCliTests : IDisposable
{
    private const string Prefix = "ntilde-mux";

    private const MuxCliVerbs Offered =
        MuxCliVerbs.Serve | MuxCliVerbs.Ls | MuxCliVerbs.Kill | MuxCliVerbs.KillServer | MuxCliVerbs.Attach | MuxCliVerbs.ProbeConsole;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nmxc" + Guid.NewGuid().ToString("N")[..8]);
    private MuxDaemonHost? _host;

    public void Dispose()
    {
        _host?.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private MuxCliHost Host(MuxCliVerbs verbs = Offered, ITerminalSessionFactory? sessions = null, Action? prepareForegroundConsole = null,
        bool attachedToParentConsole = false) => new()
        {
            Paths = new MuxPaths(_root),
            UsagePrefix = Prefix,
            ServeArguments = ["serve"],
            SessionFactory = () => sessions ?? new ScriptedSessionFactory(),
            Verbs = verbs,
            PrepareForegroundConsole = prepareForegroundConsole,
            AttachedToParentConsole = attachedToParentConsole,
        };

    private async Task<Guid> StartDaemonWithOneSessionAsync()
    {
        var server = new MuxServer(new ScriptedSessionFactory(), new MuxServerOptions { ForceConPtyFiltering = false });
        _host = new MuxDaemonHost(server, new MuxDaemonOptions
        {
            Endpoint = MuxDiscovery.GetDefaultEndpoint(_root),
            DescriptorPath = MuxDiscovery.GetDescriptorPath(_root),
            IdleExitAfter = TimeSpan.Zero,
        });
        _host.Start();
        using Stream s = Ntilde.Mux.Transport.MuxEndpointConnector.Connect(MuxDiscovery.GetDefaultEndpoint(_root), TimeSpan.FromSeconds(5));
        using MuxClient c = await MuxClient.ConnectAsync(s, null, Ct);
        return await MuxTestHost.SpawnAsync(c);
    }

    private (int Code, string Out, string Err) Run(params string[] verbArgs) => Run(Host(), verbArgs);

    private static (int Code, string Out, string Err) Run(MuxCliHost host, params string[] verbArgs)
    {
        var o = new StringWriter(); var e = new StringWriter();
        int code = MuxCli.Execute(verbArgs, o, e, host);
        return (code, o.ToString(), e.ToString());
    }

    [Fact] public void Unknown_verb_is_exit_2() => Assert.Equal(2, Run("frobnicate").Code);
    [Fact] public void Missing_verb_is_exit_2() => Assert.Equal(2, Run().Code);

    [Fact]
    public void The_usage_names_the_host()
    {
        var (_, _, err) = Run();

        Assert.StartsWith("Usage:\n  ntilde-mux serve [--idle-exit-minutes N] [--foreground]", err.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("ntilde mux", err, StringComparison.Ordinal);
    }

    /// <summary>
    /// The usage and the attach help are raw literals, which carry the source's CRLF in every checkout:
    /// what is printed must use the platform's newline throughout, as the WriteLine ending it does.
    /// </summary>
    [Fact]
    public void Usage_and_help_use_the_platform_newline()
    {
        string usage = Run().Err;
        string help = Run("attach", "--help").Out;

        Assert.Equal(usage.ReplaceLineEndings(), usage);
        Assert.Equal(help.ReplaceLineEndings(), help);
        Assert.Contains(Environment.NewLine + "  ntilde-mux ls [--json]" + Environment.NewLine, usage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ls", MuxCliVerbs.Ls, true)]
    [InlineData("LS", MuxCliVerbs.Ls, true)]               // verbs match in any case, as they always have
    [InlineData("kill-server", Offered, true)]
    [InlineData("probe-console", MuxCliVerbs.ProbeConsole, true)]
    [InlineData("kill", MuxCliVerbs.KillServer, false)]   // a verb the host does not offer
    [InlineData("frobnicate", Offered, false)]
    [InlineData("proxy", MuxCliVerbs.Proxy, true)]
    [InlineData("proxy", Offered, false)]                 // the App's ntilde mux does not offer it
    [InlineData("version", MuxCliVerbs.Version, true)]
    [InlineData("--version", MuxCliVerbs.Version, true)]  // ntilde-mux --version: the flag is the verb
    [InlineData("--version", Offered, false)]
    public void Known_verbs_are_the_implemented_ones_the_host_offers(string verb, MuxCliVerbs verbs, bool known) =>
        Assert.Equal(known, MuxCli.IsKnownVerb(verb, verbs));

    [Fact]
    public async Task A_verb_the_host_does_not_offer_prints_the_usage_and_exits_2()
    {
        await StartDaemonWithOneSessionAsync();

        var (code, output, err) = Run(Host(MuxCliVerbs.Ls), "kill-server");

        Assert.Equal(2, code);
        Assert.Equal(string.Empty, output);
        Assert.Equal(Run(Host(MuxCliVerbs.Ls)).Err, err);   // that host's usage, which names only ls
        Assert.DoesNotContain("kill-server", err, StringComparison.Ordinal);
        Assert.Equal(0, Run(Host(MuxCliVerbs.Ls), "ls").Code);   // the daemon was left alone
    }

    /// <summary>
    /// Phase 4 spec §6.2: the usage lists what the host offers. ntilde-mux adds proxy and --version;
    /// the App's set prints exactly what it always has (its own tests pin that text); probe-console,
    /// a diagnostic, is never listed.
    /// </summary>
    [Fact]
    public void The_usage_lists_only_the_verbs_the_host_offers()
    {
        string standalone = Run(Host(Offered | MuxCliVerbs.Proxy | MuxCliVerbs.Version)).Err.ReplaceLineEndings("\n");
        string appSet = Run(Host(Offered)).Err.ReplaceLineEndings("\n");

        Assert.Equal("""
            Usage:
              ntilde-mux serve [--idle-exit-minutes N] [--foreground]
              ntilde-mux ls [--json]
              ntilde-mux kill <sessionId>
              ntilde-mux kill-server [--force]
              ntilde-mux attach <sessionId|prefix> [--read-only]
              ntilde-mux proxy --stdio
              ntilde-mux --version [--json]

            """.ReplaceLineEndings("\n"), standalone);
        Assert.DoesNotContain("proxy", appSet, StringComparison.Ordinal);
        Assert.DoesNotContain("--version", appSet, StringComparison.Ordinal);
        Assert.DoesNotContain("probe-console", standalone, StringComparison.Ordinal);
    }

    [Fact]
    public void Ls_without_a_daemon_is_exit_1_with_a_message()
    {
        var (code, _, err) = Run("ls");
        Assert.Equal(1, code);
        Assert.Contains("No multiplexer is running", err);
    }

    [Fact]
    public async Task Ls_lists_the_running_session()
    {
        Guid id = await StartDaemonWithOneSessionAsync();
        var (code, output, _) = Run("ls");
        Assert.Equal(0, code);
        Assert.Contains(id.ToString(), output);
        Assert.Contains("running", output);
    }

    [Fact]
    public async Task Ls_json_is_a_ListSessionsResult()
    {
        Guid id = await StartDaemonWithOneSessionAsync();
        var (code, output, _) = Run("ls", "--json");
        Assert.Equal(0, code);
        ListSessionsResult r = JsonSerializer.Deserialize(output, MuxJsonContext.Default.ListSessionsResult)!;
        Assert.Contains(r.Sessions, s => s.SessionId == id);
    }

    [Fact]
    public async Task Kill_unknown_session_is_exit_1_and_known_is_0()
    {
        Guid id = await StartDaemonWithOneSessionAsync();
        Assert.Equal(1, Run("kill", Guid.NewGuid().ToString()).Code);
        Assert.Equal(2, Run("kill", "not-a-guid").Code);
        Assert.Equal(0, Run("kill", id.ToString()).Code);
    }

    [Fact]
    public async Task Kill_server_stops_the_daemon()
    {
        await StartDaemonWithOneSessionAsync();
        Assert.Equal(0, Run("kill-server").Code);
        Assert.Equal("shutdown", await _host!.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    /// <summary>
    /// Final-fix item 6: the daemon answered <c>shutdown</c> but its process is still alive after
    /// the 5 s wait - kill-server must say so and fail, not report success. The in-process daemon
    /// advertises a separate long-running child process as its pid, which never exits on its own.
    /// </summary>
    [Fact]
    public void Kill_server_fails_when_the_daemon_process_outlives_the_wait()
    {
        using System.Diagnostics.Process stand_in = StartLongRunningProcess();
        try
        {
            var server = new MuxServer(new ScriptedSessionFactory(), new MuxServerOptions { ForceConPtyFiltering = false });
            _host = new MuxDaemonHost(server, new MuxDaemonOptions
            {
                Endpoint = MuxDiscovery.GetDefaultEndpoint(_root),
                DescriptorPath = MuxDiscovery.GetDescriptorPath(_root),
                IdleExitAfter = TimeSpan.Zero,
                Pid = stand_in.Id,
                ProcessName = stand_in.ProcessName,
                StartToken = MuxDiscovery.GetProcessStartToken(stand_in),
            });
            _host.Start();

            var (code, output, err) = Run("kill-server");

            Assert.Equal(1, code);
            Assert.Contains("Multiplexer did not stop within 5 s.", err);
            Assert.DoesNotContain("Multiplexer stopped.", output);
        }
        finally
        {
            try { stand_in.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void Kill_server_success_prints_stopped_and_exits_0()
    {
        StartDaemonWithOneSessionAsync().GetAwaiter().GetResult();
        var (code, output, err) = Run("kill-server");
        Assert.Equal(0, code);
        Assert.Contains("Multiplexer stopped.", output);
        Assert.Equal(string.Empty, err);
        // PR #489 CI: kill-server's own polling used to block the daemon's descriptor delete on
        // Windows (sharing violation), leaving it behind so the 5 s wait timed out (~3% of runs).
        Assert.False(File.Exists(MuxDiscovery.GetDescriptorPath(_root)));
    }

    private static System.Diagnostics.Process StartLongRunningProcess()
    {
        var psi = OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo("ping", "-n 60 127.0.0.1")
            : new System.Diagnostics.ProcessStartInfo("sleep", "60");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        return System.Diagnostics.Process.Start(psi)!;
    }

    [Theory]
    [InlineData(new[] { "serve" }, 10, false)]
    [InlineData(new[] { "serve", "--idle-exit-minutes", "0" }, 0, false)]
    [InlineData(new[] { "serve", "--foreground", "--idle-exit-minutes", "3" }, 3, true)]
    public void Serve_arguments_parse(string[] verbArgs, int minutes, bool foreground)
    {
        Assert.True(MuxCli.TryParseServe(verbArgs, out MuxServeOptions o, out _));
        Assert.Equal(TimeSpan.FromMinutes(minutes), o.IdleExitAfter);
        Assert.Equal(foreground, o.Foreground);
    }

    [Theory]
    [InlineData("--idle-exit-minutes")]
    [InlineData("--idle-exit-minutes", "-1")]
    [InlineData("--bogus")]
    public void Bad_serve_arguments_are_rejected(params string[] extra)
    {
        Assert.False(MuxCli.TryParseServe(["serve", .. extra], out _, out string? error));
        Assert.NotNull(error);
    }

    /// <summary>
    /// What serve takes from its host (Phase 4 spec §6.2): the root it advertises under, the shells it
    /// spawns, and, for a watched --foreground run, the console binding - while the caller's own
    /// writer keeps the log.
    /// </summary>
    [Fact]
    public async Task Serve_in_the_foreground_binds_the_hosts_console_and_spawns_its_shells_at_its_root()
    {
        // serve runs in this process: keep the PTY log sink and ConPTY's passthrough switch it sets
        // from leaking into other tests.
        Action<PtyLogLevel, string>? ptySink = PtyLogger.Sink;
        string? noPassthrough = Environment.GetEnvironmentVariable("NTILDE_PTY_NO_PASSTHROUGH");
        var shells = new ScriptedSessionFactory();
        int prepared = 0;
        MuxCliHost host = Host(sessions: shells, prepareForegroundConsole: () => Interlocked.Increment(ref prepared));
        var serveLog = new StringWriter();
        TextWriter serveErr = TextWriter.Synchronized(serveLog);
        Task<int> serve = Task.Run(() => MuxCli.Execute(["serve", "--foreground", "--idle-exit-minutes", "0"], TextWriter.Null, serveErr, host), Ct);
        try
        {
            await TestWait.UntilAsync(() => File.Exists(MuxDiscovery.GetDescriptorPath(_root)) || serve.IsCompleted, "serve advertised itself at the host's root");
            Assert.False(serve.IsCompleted, $"serve exited early: {serveLog}");
            Assert.Equal(1, Volatile.Read(ref prepared));

            using (Stream s = Ntilde.Mux.Transport.MuxEndpointConnector.Connect(MuxDiscovery.GetDefaultEndpoint(_root), TimeSpan.FromSeconds(5)))
            using (MuxClient c = await MuxClient.ConnectAsync(s, null, Ct))
            {
                await MuxTestHost.SpawnAsync(c);
            }

            Assert.Single(shells.Requests);
            Assert.Equal(0, Run(host, "kill-server").Code);
            Assert.Equal(0, await serve.WaitAsync(TimeSpan.FromSeconds(10), Ct));
            Assert.Contains("[MuxDaemon] exiting", serveLog.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (!serve.IsCompleted) Run(host, "kill-server");
            PtyLogger.Sink = ptySink;
            Environment.SetEnvironmentVariable("NTILDE_PTY_NO_PASSTHROUGH", noPassthrough);
        }
    }

    /// <summary>A daemon of another protocol version: the handshake fails, only the pid can stop it.</summary>
    private System.Diagnostics.Process StartForeignVersionDaemon()
    {
        System.Diagnostics.Process standIn = StartLongRunningProcess();
        var server = new MuxServer(new ScriptedSessionFactory(), new MuxServerOptions
        {
            MinProtocolVersion = 99,
            MaxProtocolVersion = 99,
            ForceConPtyFiltering = false,
        });
        _host = new MuxDaemonHost(server, new MuxDaemonOptions
        {
            Endpoint = MuxDiscovery.GetDefaultEndpoint(_root),
            DescriptorPath = MuxDiscovery.GetDescriptorPath(_root),
            IdleExitAfter = TimeSpan.Zero,
            Pid = standIn.Id,
            ProcessName = standIn.ProcessName,
            StartToken = MuxDiscovery.GetProcessStartToken(standIn),
        });
        _host.Start();
        return standIn;
    }

    [Fact]
    public void Kill_server_against_another_protocol_version_names_the_pid_and_needs_force()
    {
        using System.Diagnostics.Process standIn = StartForeignVersionDaemon();
        try
        {
            var (code, output, err) = Run("kill-server");

            Assert.Equal(1, code);
            Assert.Contains($"pid {standIn.Id}", err);
            Assert.Contains("--force", err);
            Assert.DoesNotContain("terminated", output);
            Assert.False(standIn.HasExited);
        }
        finally
        {
            try { standIn.Kill(); } catch (InvalidOperationException) { }
        }
    }

    /// <summary>The hint names the host's own command (`ntilde-mux ...` on a remote host), not the App's `ntilde mux ...`.</summary>
    [Fact]
    public void A_version_mismatch_hint_names_the_hosts_own_kill_server_command()
    {
        using System.Diagnostics.Process standIn = StartForeignVersionDaemon();
        try
        {
            var (code, _, err) = Run("ls");

            Assert.Equal(1, code);
            Assert.Contains("ntilde-mux kill-server --force", err, StringComparison.Ordinal);
            Assert.DoesNotContain("ntilde mux", err, StringComparison.Ordinal);
        }
        finally
        {
            try { standIn.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void Kill_server_force_terminates_a_verified_daemon_of_another_version()
    {
        using System.Diagnostics.Process standIn = StartForeignVersionDaemon();
        try
        {
            var (code, output, err) = Run("kill-server", "--force");

            Assert.Equal(0, code);
            Assert.Contains($"pid {standIn.Id}", output);
            Assert.True(standIn.WaitForExit(10_000), "the daemon process was terminated");
            Assert.False(File.Exists(MuxDiscovery.GetDescriptorPath(_root)), "the stale descriptor is gone");
            Assert.Equal(string.Empty, err);
        }
        finally
        {
            try { standIn.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void The_pid_fallback_refuses_a_process_whose_name_does_not_match()
    {
        using System.Diagnostics.Process standIn = StartLongRunningProcess();
        try
        {
            string path = MuxDiscovery.GetDescriptorPath(_root);
            MuxDiscovery.WriteDescriptor(path, new MuxEndpointDescriptor
            {
                MinVersion = 99,
                MaxVersion = 99,
                Endpoint = MuxDiscovery.GetDefaultEndpoint(_root),
                Pid = standIn.Id,
                ProcessName = "not-the-daemon",   // a recycled pid
            });
            var o = new StringWriter();
            var e = new StringWriter();

            int code = MuxCli.KillByPid(path, force: true, o, e);

            Assert.Equal(1, code);
            Assert.False(standIn.HasExited, "an unverified process is never killed");
        }
        finally
        {
            try { standIn.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void Force_refuses_a_pid_whose_start_time_changed()
    {
        using System.Diagnostics.Process standIn = StartLongRunningProcess();
        try
        {
            string path = MuxDiscovery.GetDescriptorPath(_root);
            MuxDiscovery.WriteDescriptor(path, new MuxEndpointDescriptor
            {
                MinVersion = 99,
                MaxVersion = 99,
                Endpoint = MuxDiscovery.GetDefaultEndpoint(_root),
                Pid = standIn.Id,
                ProcessName = standIn.ProcessName,
                // a recycled pid: one tick off on Linux (exact compare), 5 s off elsewhere (1 s tolerance)
                StartTime = MuxDiscovery.GetProcessStartToken(standIn)!.Value + (OperatingSystem.IsLinux() ? 1 : TimeSpan.FromSeconds(5).Ticks),
            });
            var o = new StringWriter();
            var e = new StringWriter();

            int code = MuxCli.KillByPid(path, force: true, o, e);

            Assert.Equal(1, code);
            Assert.Contains("is no longer the multiplexer", e.ToString());
            Assert.False(standIn.HasExited, "a process started at another time is never killed");
        }
        finally
        {
            try { standIn.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void Kill_server_rejects_unknown_options() => Assert.Equal(2, Run("kill-server", "--bogus").Code);

    // ---- PR #489 review 2, item 3 (moved from App.Tests' MuxReview2Tests): a SocketException must end a verb with exit 1, not crash it.

    [Fact]
    public void A_socket_exception_is_a_reportable_verb_failure()
    {
        Assert.True(MuxCli.IsReportableFailure(new SocketException(98)));
        Assert.True(MuxCli.IsReportableFailure(new IOException("x")));
        Assert.False(MuxCli.IsReportableFailure(new InvalidOperationException("a bug")));
    }

    [Fact]
    public void The_console_probe_verdict_needs_raw_applied_restored_and_a_stable_size()
    {
        const string raw = "speed 38400 baud; -icanon -isig -echo";
        const string cooked = "speed 38400 baud; icanon isig echo";

        Assert.True(MuxCli.ProbeVerdict(raw, cooked, (80, 24), (80, 24), writeOk: null));
        Assert.False(MuxCli.ProbeVerdict(cooked, cooked, (80, 24), (80, 24), writeOk: null));   // raw mode not applied
        Assert.False(MuxCli.ProbeVerdict(raw, raw, (80, 24), (80, 24), writeOk: null));         // not restored
        Assert.False(MuxCli.ProbeVerdict(raw, cooked, (80, 24), (1, 1), writeOk: null));        // the size broke while raw
        Assert.False(MuxCli.ProbeVerdict(null, null, (120, 30), (120, 30), writeOk: false));    // Windows: the marker did not print as written
        Assert.True(MuxCli.ProbeVerdict(null, null, (120, 30), (120, 30), writeOk: true));     // Windows: no stty, the write measured
    }

    [Fact]
    public void The_console_probe_reads_the_Windows_input_mode_back_instead_of_assuming_raw()
    {
        // DescribeInputMode's words: line input is icanon, processed input is isig.
        const string raw = "input=0x03E0 -icanon -isig -echo vtinput";
        const string noVt = "input=0x01E0 -icanon -isig -echo -vtinput";
        const string cooked = "input=0x0992 icanon -isig -echo -vtinput";

        Assert.True(MuxCli.ProbeVerdict(raw, null, (120, 30), (120, 30), writeOk: true));
        Assert.False(MuxCli.ProbeVerdict(noVt, null, (120, 30), (120, 30), writeOk: true));   // keys would not arrive as VT
        Assert.False(MuxCli.ProbeVerdict(cooked, null, (120, 30), (120, 30), writeOk: true)); // line mode holds the chord until Enter
        Assert.False(MuxCli.ProbeVerdict(raw, MuxCli.RestoreMismatch, (120, 30), (120, 30), writeOk: true));
    }

    [Fact]
    public void The_probe_verb_is_hidden_from_the_usage()
    {
        var (_, _, err) = Run("frobnicate");
        Assert.DoesNotContain("probe-console", err, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("attach")]
    [InlineData("attach", "abcd", "efgh")]
    [InlineData("attach", "--bogus", "abcd")]
    public void Bad_attach_arguments_are_exit_2(params string[] verbArgs) => Assert.Equal(2, Run(verbArgs).Code);

    [Fact]
    public void Attach_without_a_daemon_is_exit_2()
    {
        var (code, _, err) = Run("attach", Guid.NewGuid().ToString());
        Assert.Equal(2, code);
        Assert.Contains("No multiplexer is running", err);
    }

    [Fact]
    public async Task Attach_to_an_unknown_session_is_exit_2()
    {
        await StartDaemonWithOneSessionAsync();
        var (code, _, err) = Run("attach", Guid.NewGuid().ToString());
        Assert.Equal(2, code);
        Assert.Contains("No session", err);
    }

    [Fact]
    public async Task Attach_by_prefix_renders_and_detaches_with_the_chord()
    {
        Guid id = await StartDaemonWithOneSessionAsync();
        using var console = new FakeConsoleSurface(80, 24);
        MuxCli.ConsoleFactoryForTest = () => console;
        try
        {
            Task<(int Code, string Out, string Err)> run = Task.Run(() => Run("attach", id.ToString("N")[..8]), Ct);
            await TestWait.UntilAsync(() => console.IsRaw, "the text client took the console");
            console.Type("\u001cd");

            var (code, _, err) = await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Equal(0, code);
            Assert.Equal(string.Empty, err);
            Assert.Contains($"[detached from {id}]", console.Output, StringComparison.Ordinal);
            Assert.False(console.IsRaw);
        }
        finally
        {
            MuxCli.ConsoleFactoryForTest = null;
        }
    }

    [Fact]
    public async Task Attach_from_inside_the_target_session_is_refused_before_any_console_is_touched()
    {
        // Drawing a session into its own terminal copies each frame into the screen it is copying:
        // a feedback loop that blacked out the pane in manual testing.
        Guid id = await StartDaemonWithOneSessionAsync();
        bool surfaceCreated = false;
        MuxCli.ConsoleFactoryForTest = () =>
        {
            surfaceCreated = true;
            return new FakeConsoleSurface(80, 24);
        };
        string? previous = Environment.GetEnvironmentVariable(MuxServer.SessionEnvironmentVariable);
        Environment.SetEnvironmentVariable(MuxServer.SessionEnvironmentVariable, id.ToString("D"));
        try
        {
            var (code, _, err) = Run("attach", id.ToString("N")[..8]);

            Assert.Equal(2, code);
            Assert.Equal(
                $"mux: you are inside session {id.ToString("N")[..8]} already; attaching to it from itself would loop. Use another terminal." + Environment.NewLine,
                err);
            Assert.False(surfaceCreated);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MuxServer.SessionEnvironmentVariable, previous);
            MuxCli.ConsoleFactoryForTest = null;
        }
    }

    [Fact]
    public async Task Attach_from_inside_a_different_session_proceeds()
    {
        Guid id = await StartDaemonWithOneSessionAsync();
        using var console = new FakeConsoleSurface(80, 24);
        MuxCli.ConsoleFactoryForTest = () => console;
        string? previous = Environment.GetEnvironmentVariable(MuxServer.SessionEnvironmentVariable);
        Environment.SetEnvironmentVariable(MuxServer.SessionEnvironmentVariable, Guid.NewGuid().ToString("D"));
        try
        {
            Task<(int Code, string Out, string Err)> run = Task.Run(() => Run("attach", id.ToString()), Ct);
            await TestWait.UntilAsync(() => console.IsRaw, "the text client took the console");
            console.Type("\u001cd");

            var (code, _, err) = await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Equal(0, code);
            Assert.Equal(string.Empty, err);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MuxServer.SessionEnvironmentVariable, previous);
            MuxCli.ConsoleFactoryForTest = null;
        }
    }

    [Fact]
    public void A_session_prefix_must_be_unique_and_at_least_4_characters()
    {
        var a = new SessionSummary { SessionId = new Guid("abcd0000-0000-0000-0000-000000000001") };
        var b = new SessionSummary { SessionId = new Guid("abcd0000-0000-0000-0000-000000000002") };

        Assert.True(MuxCli.TryResolveSession("abcd0000000000000000000000000001", [a, b], out Guid exact, out _));
        Assert.Equal(a.SessionId, exact);
        Assert.False(MuxCli.TryResolveSession("abcd", [a, b], out _, out string? ambiguous));
        Assert.Contains("matches 2", ambiguous);
        Assert.False(MuxCli.TryResolveSession("abc", [a, b], out _, out string? tooShort));
        Assert.Contains("at least 4", tooShort);
        Assert.True(MuxCli.TryResolveSession("abcd0000-0000-0000-0000-000000000002", [a, b], out Guid full, out _));
        Assert.Equal(b.SessionId, full);
    }

    /// <summary>
    /// Phase 3 printed a <c>cmd /c</c> hint before raw mode when the GUI exe attached to a parent console.
    /// ntilde.com ended that (Phase 4 spec §11.4): attaching from a parent console writes nothing to stderr.
    /// </summary>
    [Fact]
    public async Task Attach_from_a_parent_console_prints_no_hint()
    {
        Guid id = await StartDaemonWithOneSessionAsync();
        using var console = new FakeConsoleSurface(80, 24);
        MuxCli.ConsoleFactoryForTest = () => console;
        try
        {
            MuxCliHost host = Host(attachedToParentConsole: true);
            Task<(int Code, string Out, string Err)> run = Task.Run(() => Run(host, "attach", id.ToString("N")[..8]), Ct);
            await TestWait.UntilAsync(() => console.IsRaw, "the text client took the console");
            console.Type("\u001cd");

            var (code, _, err) = await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Equal(0, code);
            Assert.Equal(string.Empty, err);
        }
        finally
        {
            MuxCli.ConsoleFactoryForTest = null;
        }
    }

    [Fact]
    public void Attach_usage_no_longer_mentions_cmd_c()
    {
        var (code, help, _) = Run("attach", "--help");
        var (_, _, usage) = Run("attach");

        Assert.Equal(0, code);
        Assert.DoesNotContain("cmd /c", help, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cmd /c", usage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ctrl+\\ then d (Ctrl may stay held)", help, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ls_marks_user_detached_sessions()
    {
        Guid id = await StartDaemonWithOneSessionAsync();
        using Stream s = Ntilde.Mux.Transport.MuxEndpointConnector.Connect(MuxDiscovery.GetDefaultEndpoint(_root), TimeSpan.FromSeconds(5));
        using MuxClient c = await MuxClient.ConnectAsync(s, null, Ct);
        MuxClientSession session = c.OpenSession(id, "scripted");
        await session.AttachAsync(0, MuxTestHost.DefaultPresentation, Ct);

        session.Detach(userDetached: true);

        await TestWait.UntilAsync(() => Run("ls").Out.Contains("running, detached", StringComparison.Ordinal), "ls marks it");
        Assert.Contains("\"detachedByUser\":true", Run("ls", "--json").Out, StringComparison.Ordinal);
    }
}
