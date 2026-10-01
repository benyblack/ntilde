using System.Text.Json;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Mux.TextClient;
using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Shell.Mux;

public sealed class MuxCommandTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nmxc" + Guid.NewGuid().ToString("N")[..8]);
    private MuxDaemonHost? _host;

    public void Dispose()
    {
        _host?.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

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

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        int code = MuxCommand.Execute(args, o, e, _root);
        return (code, o.ToString(), e.ToString());
    }

    [Fact] public void Dispatch_matches_only_mux() { Assert.True(MuxCommand.IsSupportedCliMode(["mux", "ls"])); Assert.False(MuxCommand.IsSupportedCliMode(["backup"])); Assert.False(MuxCommand.IsSupportedCliMode([])); }
    [Fact] public void Serve_is_recognised() { Assert.True(MuxCommand.IsServe(["mux", "serve"])); Assert.False(MuxCommand.IsServe(["mux", "ls"])); }
    [Fact] public void Unknown_verb_is_exit_2() => Assert.Equal(2, Run("mux", "frobnicate").Code);
    [Fact] public void Missing_verb_is_exit_2() => Assert.Equal(2, Run("mux").Code);

    [Fact]
    public void Ls_without_a_daemon_is_exit_1_with_a_message()
    {
        var (code, _, err) = Run("mux", "ls");
        Assert.Equal(1, code);
        Assert.Contains("No multiplexer is running", err);
    }

    [Fact]
    public async Task Ls_lists_the_running_session()
    {
        Guid id = await StartDaemonWithOneSessionAsync();
        var (code, output, _) = Run("mux", "ls");
        Assert.Equal(0, code);
        Assert.Contains(id.ToString(), output);
        Assert.Contains("running", output);
    }

    [Fact]
    public async Task Ls_json_is_a_ListSessionsResult()
    {
        Guid id = await StartDaemonWithOneSessionAsync();
        var (code, output, _) = Run("mux", "ls", "--json");
        Assert.Equal(0, code);
        ListSessionsResult r = JsonSerializer.Deserialize(output, MuxJsonContext.Default.ListSessionsResult)!;
        Assert.Contains(r.Sessions, s => s.SessionId == id);
    }

    [Fact]
    public async Task Kill_unknown_session_is_exit_1_and_known_is_0()
    {
        Guid id = await StartDaemonWithOneSessionAsync();
        Assert.Equal(1, Run("mux", "kill", Guid.NewGuid().ToString()).Code);
        Assert.Equal(2, Run("mux", "kill", "not-a-guid").Code);
        Assert.Equal(0, Run("mux", "kill", id.ToString()).Code);
    }

    [Fact]
    public async Task Kill_server_stops_the_daemon()
    {
        await StartDaemonWithOneSessionAsync();
        Assert.Equal(0, Run("mux", "kill-server").Code);
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
            });
            _host.Start();

            var (code, output, err) = Run("mux", "kill-server");

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
        var (code, output, err) = Run("mux", "kill-server");
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
    [InlineData(new[] { "mux", "serve" }, 10, false)]
    [InlineData(new[] { "mux", "serve", "--idle-exit-minutes", "0" }, 0, false)]
    [InlineData(new[] { "mux", "serve", "--foreground", "--idle-exit-minutes", "3" }, 3, true)]
    public void Serve_arguments_parse(string[] args, int minutes, bool foreground)
    {
        Assert.True(MuxCommand.TryParseServe(args, out MuxServeOptions o, out _));
        Assert.Equal(TimeSpan.FromMinutes(minutes), o.IdleExitAfter);
        Assert.Equal(foreground, o.Foreground);
    }

    [Theory]
    [InlineData("--idle-exit-minutes")]
    [InlineData("--idle-exit-minutes", "-1")]
    [InlineData("--bogus")]
    public void Bad_serve_arguments_are_rejected(params string[] extra)
    {
        Assert.False(MuxCommand.TryParseServe(["mux", "serve", .. extra], out _, out string? error));
        Assert.NotNull(error);
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
            var (code, output, err) = Run("mux", "kill-server");

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

    [Fact]
    public void Kill_server_force_terminates_a_verified_daemon_of_another_version()
    {
        using System.Diagnostics.Process standIn = StartForeignVersionDaemon();
        try
        {
            var (code, output, err) = Run("mux", "kill-server", "--force");

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

            int code = MuxCommand.KillByPid(path, force: true, o, e);

            Assert.Equal(1, code);
            Assert.False(standIn.HasExited, "an unverified process is never killed");
        }
        finally
        {
            try { standIn.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void Kill_server_rejects_unknown_options() => Assert.Equal(2, Run("mux", "kill-server", "--bogus").Code);

    [Fact]
    public void The_console_probe_verdict_needs_raw_applied_restored_and_a_stable_size()
    {
        const string raw = "speed 38400 baud; -icanon -isig -echo";
        const string cooked = "speed 38400 baud; icanon isig echo";

        Assert.True(MuxCommand.ProbeVerdict(raw, cooked, (80, 24), (80, 24), writeOk: null));
        Assert.False(MuxCommand.ProbeVerdict(cooked, cooked, (80, 24), (80, 24), writeOk: null));   // raw mode not applied
        Assert.False(MuxCommand.ProbeVerdict(raw, raw, (80, 24), (80, 24), writeOk: null));         // not restored
        Assert.False(MuxCommand.ProbeVerdict(raw, cooked, (80, 24), (1, 1), writeOk: null));        // the size broke while raw
        Assert.False(MuxCommand.ProbeVerdict(null, null, (120, 30), (120, 30), writeOk: false));    // Windows: the marker did not print as written
        Assert.True(MuxCommand.ProbeVerdict(null, null, (120, 30), (120, 30), writeOk: true));     // Windows: no stty, the write measured
    }

    [Fact]
    public void The_probe_verb_is_hidden_from_the_usage()
    {
        var (_, _, err) = Run("mux", "frobnicate");
        Assert.DoesNotContain("probe-console", err, StringComparison.Ordinal);
    }

    [Fact]
    public void Attach_is_recognised()
    {
        Assert.True(MuxCommand.IsAttach(["mux", "attach", "abcd"]));
        Assert.False(MuxCommand.IsAttach(["mux", "ls"]));
        Assert.False(MuxCommand.IsAttach(["backup"]));
    }

    [Theory]
    [InlineData("mux", "attach")]
    [InlineData("mux", "attach", "abcd", "efgh")]
    [InlineData("mux", "attach", "--bogus", "abcd")]
    public void Bad_attach_arguments_are_exit_2(params string[] args) => Assert.Equal(2, Run(args).Code);

    [Fact]
    public void Attach_without_a_daemon_is_exit_2()
    {
        var (code, _, err) = Run("mux", "attach", Guid.NewGuid().ToString());
        Assert.Equal(2, code);
        Assert.Contains("No multiplexer is running", err);
    }

    [Fact]
    public async Task Attach_to_an_unknown_session_is_exit_2()
    {
        await StartDaemonWithOneSessionAsync();
        var (code, _, err) = Run("mux", "attach", Guid.NewGuid().ToString());
        Assert.Equal(2, code);
        Assert.Contains("No session", err);
    }

    [Fact]
    public async Task Attach_by_prefix_renders_and_detaches_with_the_chord()
    {
        Guid id = await StartDaemonWithOneSessionAsync();
        using var console = new FakeConsoleSurface(80, 24);
        MuxCommand.ConsoleFactoryForTest = () => console;
        try
        {
            Task<(int Code, string Out, string Err)> run = Task.Run(() => Run("mux", "attach", id.ToString("N")[..8]), Ct);
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
            MuxCommand.ConsoleFactoryForTest = null;
        }
    }

    [Fact]
    public async Task Attach_from_inside_the_target_session_is_refused_before_any_console_is_touched()
    {
        // Drawing a session into its own terminal copies each frame into the screen it is copying:
        // a feedback loop that blacked out the pane in manual testing.
        Guid id = await StartDaemonWithOneSessionAsync();
        bool surfaceCreated = false;
        MuxCommand.ConsoleFactoryForTest = () =>
        {
            surfaceCreated = true;
            return new FakeConsoleSurface(80, 24);
        };
        string? previous = Environment.GetEnvironmentVariable(MuxServer.SessionEnvironmentVariable);
        Environment.SetEnvironmentVariable(MuxServer.SessionEnvironmentVariable, id.ToString("D"));
        try
        {
            var (code, _, err) = Run("mux", "attach", id.ToString("N")[..8]);

            Assert.Equal(2, code);
            Assert.Equal(
                $"mux: you are inside session {id.ToString("N")[..8]} already; attaching to it from itself would loop. Use another terminal." + Environment.NewLine,
                err);
            Assert.False(surfaceCreated);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MuxServer.SessionEnvironmentVariable, previous);
            MuxCommand.ConsoleFactoryForTest = null;
        }
    }

    [Fact]
    public async Task Attach_from_inside_a_different_session_proceeds()
    {
        Guid id = await StartDaemonWithOneSessionAsync();
        using var console = new FakeConsoleSurface(80, 24);
        MuxCommand.ConsoleFactoryForTest = () => console;
        string? previous = Environment.GetEnvironmentVariable(MuxServer.SessionEnvironmentVariable);
        Environment.SetEnvironmentVariable(MuxServer.SessionEnvironmentVariable, Guid.NewGuid().ToString("D"));
        try
        {
            Task<(int Code, string Out, string Err)> run = Task.Run(() => Run("mux", "attach", id.ToString()), Ct);
            await TestWait.UntilAsync(() => console.IsRaw, "the text client took the console");
            console.Type("\u001cd");

            var (code, _, err) = await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Equal(0, code);
            Assert.Equal(string.Empty, err);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MuxServer.SessionEnvironmentVariable, previous);
            MuxCommand.ConsoleFactoryForTest = null;
        }
    }

    [Fact]
    public void A_session_prefix_must_be_unique_and_at_least_4_characters()
    {
        var a = new SessionSummary { SessionId = new Guid("abcd0000-0000-0000-0000-000000000001") };
        var b = new SessionSummary { SessionId = new Guid("abcd0000-0000-0000-0000-000000000002") };

        Assert.True(MuxCommand.TryResolveSession("abcd0000000000000000000000000001", [a, b], out Guid exact, out _));
        Assert.Equal(a.SessionId, exact);
        Assert.False(MuxCommand.TryResolveSession("abcd", [a, b], out _, out string? ambiguous));
        Assert.Contains("matches 2", ambiguous);
        Assert.False(MuxCommand.TryResolveSession("abc", [a, b], out _, out string? tooShort));
        Assert.Contains("at least 4", tooShort);
        Assert.True(MuxCommand.TryResolveSession("abcd0000-0000-0000-0000-000000000002", [a, b], out Guid full, out _));
        Assert.Equal(b.SessionId, full);
    }

    [Theory]
    [InlineData(true, true, true)]     // the GUI exe, attached to a parent console: the only case that shares the keyboard
    [InlineData(true, false, false)]   // allocated its own console (Explorer), or Ntilde.Cli.exe
    [InlineData(false, true, false)]   // not Windows
    public void The_console_hint_is_printed_only_for_the_GUI_exe_on_a_parent_console(bool isWindows, bool attachedToParent, bool expected)
    {
        string? hint = MuxCommand.AttachConsoleHint(isWindows, attachedToParent, "abcd1234");

        Assert.Equal(expected, hint is not null);
        if (expected) Assert.Equal("mux: if keystrokes are lost, run via cmd /c ntilde mux attach abcd1234 (ignore if already under cmd /c)", hint);
    }

    [Fact]
    public void Attach_help_names_the_cmd_workaround()
    {
        var (code, output, _) = Run("mux", "attach", "--help");

        Assert.Equal(0, code);
        Assert.Contains("cmd /c ntilde mux attach <id>", output, StringComparison.Ordinal);
        Assert.Contains("Ctrl+\\ then d", output, StringComparison.Ordinal);
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

        await TestWait.UntilAsync(() => Run("mux", "ls").Out.Contains("running, detached", StringComparison.Ordinal), "ls marks it");
        Assert.Contains("\"detachedByUser\":true", Run("mux", "ls", "--json").Out, StringComparison.Ordinal);
    }
}
