using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Daemon;
using Ntilde.Mux.Tests.Support;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.Storage;
using Ntilde.Services.Ssh;
using Ntilde.Shell.Mux;
using Ntilde.Shell.Mux.Remote;
using Ntilde.Tests.Shell.Mux.Remote;

namespace Ntilde.Tests.Shell.Mux;

/// <summary>
/// <c>ntilde mux ls --all</c> (Phase 5 spec §5, Task 27): this computer's sessions, then those of every SSH profile that keeps
/// its remote sessions. Each profile's host is a <see cref="FakeRemoteHost"/> - an in-memory daemon behind the real proxy -
/// reached through the connector the command itself builds (<see cref="RemoteMuxLister.CreateConnector"/>), and this computer's
/// daemon runs in-process under a temporary root. The SSH profiles are the test's own store: nothing here reads the real
/// profile store or the vault.
/// </summary>
public sealed class MuxCommandLsAllTests : IDisposable
{
    private static readonly TimeSpan Patient = TimeSpan.FromSeconds(30);

    /// <summary>The table's header with a HOST column as wide as "this computer", the widest host in these tests.</summary>
    private const string Header = "HOST           ID                                    STATE               ATTACHED  SIZE       TITLE";

    /// <summary>The JSON's property names: its root's, and a host's with its sessions or with its error instead.</summary>
    private static readonly string[] RootNames = ["endpoints"];
    private static readonly string[] ListedNames = ["endpoint", "host", "sessions"];
    private static readonly string[] UnreachableNames = ["endpoint", "host", "error"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "nmxa" + Guid.NewGuid().ToString("N")[..8]);
    private readonly TestProfileStore _profiles = new();
    private readonly Dictionary<Guid, FakeRemoteHost> _remotes = [];
    private readonly ConcurrentQueue<RemoteMuxTransportRequest> _requests = new();
    private readonly ConcurrentQueue<RemoteMuxConnector> _connectors = new();
    private readonly ConcurrentQueue<string> _log = new();
    private readonly List<IDisposable> _owned = [];
    private MuxDaemonHost? _localDaemon;

    public void Dispose()
    {
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        foreach (FakeRemoteHost remote in _remotes.Values) remote.Dispose();
        _localDaemon?.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>This computer's daemon, under the test's root, with one session called <paramref name="title"/>.</summary>
    private async Task<Guid> StartLocalDaemonAsync(string title)
    {
        var server = new MuxServer(new ScriptedSessionFactory(), new MuxServerOptions { ForceConPtyFiltering = false });
        _localDaemon = new MuxDaemonHost(server, new MuxDaemonOptions
        {
            Endpoint = MuxDiscovery.GetDefaultEndpoint(_root),
            DescriptorPath = MuxDiscovery.GetDescriptorPath(_root),
            IdleExitAfter = TimeSpan.Zero,
        });
        _localDaemon.Start();
        using Stream s = Ntilde.Mux.Transport.MuxEndpointConnector.Connect(MuxDiscovery.GetDefaultEndpoint(_root), TimeSpan.FromSeconds(5));
        using MuxClient c = await MuxClient.ConnectAsync(s, null, Ct);
        return await c.SpawnAsync(new SpawnParams { Command = "scripted", Cols = 80, Rows = 24, Title = title }, Ct);
    }

    /// <summary>A profile <c>user@host</c> in the test's store, and the host it reaches.</summary>
    /// <param name="configure">Sets the profile up further (its backend, its jump hosts) before it is saved.</param>
    private (SshProfile Profile, FakeRemoteHost Host) AddProfile(string name, string host, bool keepsSessions = true, Action<SshProfile>? configure = null)
    {
        var profile = new SshProfile
        {
            Id = Guid.NewGuid(),
            Name = name,
            Host = host,
            User = "nova",
            MuxOptions = new SshMuxOptions { PersistRemoteSessions = keepsSessions },
        };
        configure?.Invoke(profile);
        _profiles.SaveProfile(profile);
        var remote = new FakeRemoteHost("nova@" + host);
        _remotes[profile.Id] = remote;
        return (profile, remote);
    }

    /// <summary>A session called <paramref name="title"/> on <paramref name="remote"/>'s daemon, spawned through a connection of its own.</summary>
    private async Task<Guid> SpawnOnAsync(SshProfile profile, FakeRemoteHost remote, string title)
    {
        var connector = new RemoteMuxConnector(profile, remote, "spawner", log: null);
        _owned.Add(connector);
        MuxClient client = await connector.ConnectAsync(interactive: true, Ct);
        _owned.Add(client);
        return await client.SpawnAsync(new SpawnParams { Command = "scripted", Cols = 100, Rows = 30, Title = title }, Ct);
    }

    /// <summary>The remote half the command is handed: the test's store, and each profile's connector as the command builds it.</summary>
    /// <param name="isWindows">The OS the command decides for; this one's when null.</param>
    /// <param name="releaseClient">How a listed host's client is let go; disposed when null.</param>
    private MuxLsAllRemotes Remotes(TimeSpan? perHost = null, bool? isWindows = null, Action<MuxClient>? releaseClient = null) => new(
        new SshConnectionService(_profiles),
        profile =>
        {
            RemoteMuxConnector connector = RemoteMuxLister.CreateConnector(
                profile,
                (p, request) =>
                {
                    _requests.Enqueue(request);
                    return _remotes[p.Id];
                },
                savedPassword: null,
                askPassRecords: null,
                Log);
            _connectors.Enqueue(connector);
            return connector;
        },
        perHost ?? Patient,
        Log,
        isWindows ?? OperatingSystem.IsWindows(),
        releaseClient);

    /// <summary>The command's and its connectors' log: kept, and shown to <see cref="_onLog"/> as it is written.</summary>
    private void Log(string line)
    {
        _log.Enqueue(line);
        _onLog?.Invoke(line);
    }

    /// <summary>Set by a test that acts on a log line as it is written (the connector's "connected", say).</summary>
    private Action<string>? _onLog;

    /// <summary>Runs <c>ntilde mux &lt;args&gt;</c> off the test's thread, and waits for it to finish.</summary>
    private async Task<(int Code, string Out, string Err)> RunAsync(MuxLsAllRemotes remotes, params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        int code = await Task.Run(() => MuxCommand.Execute(["mux", .. args], o, e, _root, _ => remotes), Ct).WaitAsync(Patient, Ct);
        return (code, o.ToString(), e.ToString());
    }

    private static string Row(string host, Guid id, string state, int attached, string size, string title) =>
        string.Format(CultureInfo.InvariantCulture, "{0,-13}  {1,-36}  {2,-18}  {3,8}  {4,-9}  {5}", host, id, state, attached, size, title);

    private static string Lines(params string[] lines) => string.Concat(lines.Select(l => l + Environment.NewLine));

    [Fact]
    public async Task Ls_json_without_all_is_byte_for_byte_what_it_was()
    {
        Guid id = await StartLocalDaemonAsync("t<&>'\"\u00e9\u001b[2J\u0007\u202e");
        var o = new StringWriter(); var e = new StringWriter();

        int code = MuxCommand.Execute(["mux", "ls", "--json"], o, e, _root);

        // Captured from `ntilde mux ls --json` at 862efa2, before ls --all existed: the same encoder, the same escapes.
        Assert.Equal(0, code);
        Assert.Equal(string.Empty, e.ToString());
        Assert.Equal(
            $$"""{"sessions":[{"sessionId":"{{id}}","title":"t\u003C\u0026\u003E\u0027\u0022\u00E9\u001B[2J\u0007\u202E","command":"scripted","arguments":"","cols":80,"rows":24,"running":true,"attachedClients":0,"faulted":false}]}"""
                + Environment.NewLine,
            o.ToString());
    }

    /// <summary>One table: a HOST column in front of ls's own, a line for an unreachable host, and exit 0 - an unreachable remote does not fail the command.</summary>
    [Fact]
    public async Task Text_lists_this_computer_then_each_remote_host_and_says_which_are_unreachable()
    {
        Guid local = await StartLocalDaemonAsync("local shell");
        (SshProfile alpha, FakeRemoteHost alphaHost) = AddProfile("alpha", "a-host");
        Guid remote = await SpawnOnAsync(alpha, alphaHost, "remote shell");
        (_, FakeRemoteHost betaHost) = AddProfile("beta", "b-host");
        betaHost.Script = FakeRemoteScript.NotInstalledDash;

        var (code, output, err) = await RunAsync(Remotes(), "ls", "--all");

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, err);
        Assert.Equal(
            Lines(
                Header,
                Row("this computer", local, "running", 0, "80x24", "local shell"),
                Row("nova@a-host", remote, "running", 0, "100x30", "remote shell"),
                "nova@b-host    unreachable: ntilde-mux is not installed there"),
            output);
    }

    /// <summary>
    /// <c>{"endpoints":[…]}</c>, this computer first: each host's sessions as <c>ls --json</c> writes them, or its "error", in the
    /// command's own words. Exit 0 with a host unreachable.
    /// </summary>
    [Fact]
    public async Task Json_has_one_endpoint_per_host_with_its_sessions_or_its_error()
    {
        Guid local = await StartLocalDaemonAsync("local <shell> \u00e9");
        (SshProfile alpha, FakeRemoteHost alphaHost) = AddProfile("alpha", "a-host");
        Guid remote = await SpawnOnAsync(alpha, alphaHost, "remote shell");
        (SshProfile beta, FakeRemoteHost betaHost) = AddProfile("beta", "b-host");
        betaHost.Script = FakeRemoteScript.NotInstalledDash;
        string lsJson = LsJson();

        var (code, output, err) = await RunAsync(Remotes(), "ls", "--all", "--json");

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, err);
        Assert.EndsWith("}" + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', output.TrimEnd());   // one line, as ls --json
        using JsonDocument doc = JsonDocument.Parse(output);
        Assert.Equal(RootNames, Names(doc.RootElement));
        JsonElement[] endpoints = [.. doc.RootElement.GetProperty("endpoints").EnumerateArray()];
        Assert.Equal(3, endpoints.Length);

        Assert.Equal(ListedNames, Names(endpoints[0]));
        Assert.Equal("local", endpoints[0].GetProperty("endpoint").GetString());
        Assert.Equal("this computer", endpoints[0].GetProperty("host").GetString());
        // ls --json's own sessions, written the same way: the same names, the same escapes.
        using (JsonDocument ls = JsonDocument.Parse(lsJson))
        {
            Assert.Equal(ls.RootElement.GetProperty("sessions").GetRawText(), endpoints[0].GetProperty("sessions").GetRawText());
            Assert.Equal(local, Assert.Single(ls.RootElement.GetProperty("sessions").EnumerateArray()).GetProperty("sessionId").GetGuid());
        }

        Assert.Equal(ListedNames, Names(endpoints[1]));
        Assert.Equal("ssh:" + alpha.Id.ToString("N"), endpoints[1].GetProperty("endpoint").GetString());
        Assert.Equal("nova@a-host", endpoints[1].GetProperty("host").GetString());
        JsonElement remoteSession = Assert.Single(endpoints[1].GetProperty("sessions").EnumerateArray());
        Assert.Equal(remote, remoteSession.GetProperty("sessionId").GetGuid());
        Assert.Equal("remote shell", remoteSession.GetProperty("title").GetString());

        Assert.Equal(UnreachableNames, Names(endpoints[2]));
        Assert.Equal("ssh:" + beta.Id.ToString("N"), endpoints[2].GetProperty("endpoint").GetString());
        Assert.Equal("nova@b-host", endpoints[2].GetProperty("host").GetString());
        Assert.Equal("ntilde-mux is not installed there", endpoints[2].GetProperty("error").GetString());
    }

    /// <summary>
    /// Two hosts that never answer are each cut at the wait the command is given, while this computer and the host that
    /// answers still print. They are connected to at once: each one's start waits at a barrier for the other's, which a
    /// listing in turn would never reach before the first was cut. The cut stops each hung channel: nothing of it runs on.
    /// </summary>
    [Fact]
    public async Task A_host_that_hangs_is_cut_at_the_timeout_and_the_others_still_print()
    {
        TimeSpan perHost = TimeSpan.FromSeconds(4);
        Guid local = await StartLocalDaemonAsync("local shell");
        (SshProfile alpha, FakeRemoteHost alphaHost) = AddProfile("alpha", "a-host");
        Guid remote = await SpawnOnAsync(alpha, alphaHost, "remote shell");
        (_, FakeRemoteHost betaHost) = AddProfile("beta", "b-host");
        betaHost.Script = FakeRemoteScript.Silent;
        (_, FakeRemoteHost gammaHost) = AddProfile("gamma", "c-host");
        gammaHost.Script = FakeRemoteScript.Silent;
        using var bothStarting = new Barrier(2);
        int metAtTheBarrier = 0;
        void MeetTheOther(CancellationToken ct)
        {
            if (bothStarting.SignalAndWait(Patient, ct)) Interlocked.Increment(ref metAtTheBarrier);
        }

        betaHost.OnStart = MeetTheOther;
        gammaHost.OnStart = MeetTheOther;

        var clock = Stopwatch.StartNew();
        var (code, output, _) = await RunAsync(Remotes(perHost), "ls", "--all");
        TimeSpan took = clock.Elapsed;

        Assert.Equal(0, code);
        Assert.Equal(
            Lines(
                Header,
                Row("this computer", local, "running", 0, "80x24", "local shell"),
                Row("nova@a-host", remote, "running", 0, "100x30", "remote shell"),
                "nova@b-host    unreachable: it did not answer in time",
                "nova@c-host    unreachable: it did not answer in time"),
            output);
        Assert.Equal(2, Volatile.Read(ref metAtTheBarrier));   // both were starting at once, before either was cut
        Assert.True(Assert.Single(betaHost.Channels).AbortCount >= 1, "the hung channel was stopped by the cut");
        Assert.True(Assert.Single(gammaHost.Channels).AbortCount >= 1, "the hung channel was stopped by the cut");
        Assert.True(took < perHost * 3, $"took {took}: a guard against a hang, not a measure of the parallelism");
    }

    /// <summary>
    /// A host that goes silent after its greeting - the listing is asked for and never answered - is cut, and its channel is
    /// killed in the cut, not ended gracefully: on a dead link a graceful end waits out its grace, past the command's exit,
    /// and its ssh would outlive the command.
    /// </summary>
    [Fact]
    public async Task A_host_that_stalls_after_its_greeting_is_cut_and_its_channel_killed()
    {
        TimeSpan perHost = TimeSpan.FromSeconds(2);
        Guid local = await StartLocalDaemonAsync("local shell");
        (_, FakeRemoteHost alphaHost) = AddProfile("alpha", "a-host");
        // The connector logs "connected" once the daemon said hello, before the listing is asked for: the link stalls there.
        _onLog = line =>
        {
            if (line.Contains("nova@a-host: connected (", StringComparison.Ordinal)) alphaHost.StallLink();
        };

        var clock = Stopwatch.StartNew();
        var (code, output, _) = await RunAsync(Remotes(perHost), "ls", "--all");
        TimeSpan took = clock.Elapsed;

        Assert.Equal(0, code);
        Assert.Equal(
            Lines(
                Header,
                Row("this computer", local, "running", 0, "80x24", "local shell"),
                "nova@a-host    unreachable: it did not answer in time"),
            output);
        FakeRemoteChannel channel = Assert.Single(alphaHost.Channels);
        Assert.True(channel.AbortCount >= 1, "the cut killed the channel; it was not left to end gracefully");
        Assert.Null(await channel.Completion.WaitAsync(Patient, Ct));   // killed: no exit status
        Assert.True(took < perHost * 3, $"took {took}");
    }

    /// <summary>
    /// A host whose sessions arrived in time is listed, however long letting its connection go takes afterwards: the
    /// sessions are taken out first, and the release happens outside the host's wait.
    /// </summary>
    [Fact]
    public async Task A_host_listed_in_time_is_listed_however_slow_its_release()
    {
        TimeSpan perHost = TimeSpan.FromSeconds(2);
        Guid local = await StartLocalDaemonAsync("local shell");
        (SshProfile alpha, FakeRemoteHost alphaHost) = AddProfile("alpha", "a-host");
        Guid remote = await SpawnOnAsync(alpha, alphaHost, "remote shell");
        using var releasing = new ManualResetEventSlim();
        using var mayRelease = new ManualResetEventSlim();
        void SlowRelease(MuxClient client)
        {
            releasing.Set();
            mayRelease.Wait(Patient, CancellationToken.None);
            client.Dispose();
        }

        try
        {
            var (code, output, _) = await RunAsync(Remotes(perHost, releaseClient: SlowRelease), "ls", "--all");

            Assert.Equal(0, code);
            Assert.Equal(
                Lines(
                    Header,
                    Row("this computer", local, "running", 0, "80x24", "local shell"),
                    Row("nova@a-host", remote, "running", 0, "100x30", "remote shell")),
                output);
            // The command has returned the host's rows while that release cannot finish before the finally below.
            Assert.True(releasing.Wait(Patient, Ct), "the listed host's client was let go");
        }
        finally
        {
            mayRelease.Set();
        }
    }

    /// <summary>
    /// Ruling (fix round 1): on Linux and macOS a jump host's ssh does not inherit batch mode, and with askpass refused its
    /// prompt falls back to the terminal <c>ls --all</c> runs in, which a cut could leave with echo off. So there an OpenSSH
    /// profile that goes through a jump host or a proxy - its jump hosts, or <c>-J</c>, <c>ProxyJump</c> or
    /// <c>ProxyCommand</c> in its extra arguments - is reported, not connected to: no transport is built for it.
    /// </summary>
    [Theory]
    [InlineData(true, "")]
    [InlineData(false, "-J ops@bastion")]
    [InlineData(false, "-o ProxyCommand=nc-to-bastion")]
    public async Task Off_Windows_an_OpenSSH_profile_through_a_jump_host_or_proxy_is_reported_not_connected(bool jumpHops, string extraArgs)
    {
        Guid local = await StartLocalDaemonAsync("local shell");
        (SshProfile alpha, FakeRemoteHost alphaHost) = AddProfile("alpha", "a-host", configure: p =>
        {
            p.BackendKind = SshBackendKind.OpenSsh;
            if (jumpHops) p.JumpHops = [new SshJumpHop { Host = "bastion", User = "ops" }];
            p.ExtraSshArgs = extraArgs;
        });

        var (code, output, _) = await RunAsync(Remotes(isWindows: false), "ls", "--all");
        var (jsonCode, json, _) = await RunAsync(Remotes(isWindows: false), "ls", "--all", "--json");

        Assert.Equal(0, code);
        Assert.Equal(
            Lines(
                Header,
                Row("this computer", local, "running", 0, "80x24", "local shell"),
                "nova@a-host    unreachable: it goes through a jump host, which ls --all does not sign in through"),
            output);
        Assert.Equal(0, jsonCode);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement[] endpoints = [.. doc.RootElement.GetProperty("endpoints").EnumerateArray()];
        Assert.Equal(2, endpoints.Length);
        Assert.Equal("ssh:" + alpha.Id.ToString("N"), endpoints[1].GetProperty("endpoint").GetString());
        Assert.Equal("it goes through a jump host, which ls --all does not sign in through", endpoints[1].GetProperty("error").GetString());
        Assert.Empty(_connectors);
        Assert.Empty(_requests);
        Assert.Equal(0, alphaHost.StartCount);
    }

    /// <summary>A native profile's prompts go to its handler, never to a terminal: with jump hosts it is connected to anywhere.</summary>
    [Fact]
    public async Task Off_Windows_a_native_profile_through_a_jump_host_is_still_connected()
    {
        await StartLocalDaemonAsync("local shell");
        (SshProfile alpha, FakeRemoteHost alphaHost) = AddProfile("alpha", "a-host", configure: p =>
        {
            p.BackendKind = SshBackendKind.Native;
            p.JumpHops = [new SshJumpHop { Host = "bastion", User = "ops" }];
        });
        Guid remote = await SpawnOnAsync(alpha, alphaHost, "remote shell");

        var (code, output, _) = await RunAsync(Remotes(isWindows: false), "ls", "--all");

        Assert.Equal(0, code);
        Assert.Contains(Row("nova@a-host", remote, "running", 0, "100x30", "remote shell") + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.Single(_requests);
    }

    /// <summary>On Windows an OpenSSH profile with jump hosts is connected to, as before: the ruling is for Linux and macOS.</summary>
    [Fact]
    public async Task On_Windows_an_OpenSSH_profile_through_a_jump_host_is_still_connected()
    {
        await StartLocalDaemonAsync("local shell");
        (SshProfile alpha, FakeRemoteHost alphaHost) = AddProfile("alpha", "a-host", configure: p =>
        {
            p.BackendKind = SshBackendKind.OpenSsh;
            p.JumpHops = [new SshJumpHop { Host = "bastion", User = "ops" }];
        });
        Guid remote = await SpawnOnAsync(alpha, alphaHost, "remote shell");

        var (code, output, _) = await RunAsync(Remotes(isWindows: true), "ls", "--all");

        Assert.Equal(0, code);
        Assert.Contains(Row("nova@a-host", remote, "running", 0, "100x30", "remote shell") + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.Single(_requests);
    }

    /// <summary>
    /// A host stuck where a cancel cannot reach - its ssh start blocked, deaf to the cut - is still given up on, a short grace
    /// after the cut, and the command returns.
    /// </summary>
    [Fact]
    public async Task A_host_deaf_to_the_cut_is_still_given_up_on()
    {
        TimeSpan perHost = TimeSpan.FromSeconds(1);
        Guid local = await StartLocalDaemonAsync("local shell");
        (_, FakeRemoteHost stuckHost) = AddProfile("alpha", "a-host");
        using var release = new ManualResetEventSlim();
        stuckHost.OnStart = _ => release.Wait(Patient, CancellationToken.None);   // deaf to its token
        try
        {
            var clock = Stopwatch.StartNew();
            var (code, output, _) = await RunAsync(Remotes(perHost), "ls", "--all");
            TimeSpan took = clock.Elapsed;

            Assert.Equal(0, code);
            Assert.Equal(
                Lines(
                    Header,
                    Row("this computer", local, "running", 0, "80x24", "local shell"),
                    "nova@a-host    unreachable: it did not answer in time"),
                output);
            Assert.Equal(1, stuckHost.StartCount);
            Assert.InRange(took, perHost, perHost + TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.Set();
        }
    }

    /// <summary>Only profiles that keep their sessions are connected to; with none, the command says so after this computer's.</summary>
    [Fact]
    public async Task With_no_profile_keeping_sessions_it_lists_this_computer_and_says_so()
    {
        Guid local = await StartLocalDaemonAsync("local shell");
        AddProfile("plain", "a-host", keepsSessions: false);

        var (code, output, err) = await RunAsync(Remotes(), "ls", "--all");
        var (jsonCode, json, _) = await RunAsync(Remotes(), "ls", "--json", "--all");

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, err);
        Assert.Equal(
            Lines(
                Header,
                Row("this computer", local, "running", 0, "80x24", "local shell"),
                "No remote hosts keep sessions."),
            output);
        Assert.Equal(0, jsonCode);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement only = Assert.Single(doc.RootElement.GetProperty("endpoints").EnumerateArray());
        Assert.Equal("local", only.GetProperty("endpoint").GetString());
        Assert.Empty(_connectors);   // the plain profile was never connected to
        Assert.Empty(_requests);
    }

    /// <summary>
    /// ls's exit code for this computer's part: 1 when no daemon runs here, as plain ls. The remote hosts are listed all the
    /// same, and this computer's line says why it has none.
    /// </summary>
    [Fact]
    public async Task Without_a_daemon_here_it_still_lists_the_remote_hosts_and_exits_1()
    {
        (SshProfile alpha, FakeRemoteHost alphaHost) = AddProfile("alpha", "a-host");
        Guid remote = await SpawnOnAsync(alpha, alphaHost, "remote shell");

        var (code, output, err) = await RunAsync(Remotes(), "ls", "--all");
        var (jsonCode, json, jsonErr) = await RunAsync(Remotes(), "ls", "--all", "--json");

        Assert.Equal(1, code);
        Assert.Equal(string.Empty, err);
        Assert.Equal(
            Lines(
                Header,
                "this computer  unreachable: no multiplexer is running",
                Row("nova@a-host", remote, "running", 0, "100x30", "remote shell")),
            output);
        Assert.Equal(1, jsonCode);
        Assert.Equal(string.Empty, jsonErr);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement[] endpoints = [.. doc.RootElement.GetProperty("endpoints").EnumerateArray()];
        Assert.Equal(2, endpoints.Length);
        Assert.Equal(UnreachableNames, Names(endpoints[0]));
        Assert.Equal("no multiplexer is running", endpoints[0].GetProperty("error").GetString());
        Assert.Equal(remote, Assert.Single(endpoints[1].GetProperty("sessions").EnumerateArray()).GetProperty("sessionId").GetGuid());
        Assert.False(File.Exists(MuxDiscovery.GetDescriptorPath(_root)));   // ls never starts a daemon here
    }

    /// <summary>
    /// Ruling P2: a title another machine reports reaches the user's terminal without its control, bidi and format characters,
    /// and capped; this computer's is cleaned the same way (a local shell's OSC title can carry the same).
    /// </summary>
    [Fact]
    public async Task Titles_print_without_control_or_bidi_characters()
    {
        Guid local = await StartLocalDaemonAsync("local\u001b]0;x\u0007 shell");
        (SshProfile alpha, FakeRemoteHost alphaHost) = AddProfile("alpha", "a-host");
        Guid remote = await SpawnOnAsync(alpha, alphaHost, "re\u001b[2Jmote\u0007 \u202eshell");
        Guid longOne = await SpawnOnAsync(alpha, alphaHost, new string('x', RemoteOutputText.MaxLength + 50));

        var (code, output, _) = await RunAsync(Remotes(), "ls", "--all");

        Assert.Equal(0, code);
        Assert.DoesNotContain('\u001b', output);
        Assert.DoesNotContain('\u0007', output);
        Assert.DoesNotContain('\u202e', output);
        Assert.Contains(Row("this computer", local, "running", 0, "80x24", "local]0;x shell") + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.Contains(Row("nova@a-host", remote, "running", 0, "100x30", "re[2Jmote shell") + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.Contains(Row("nova@a-host", longOne, "running", 0, "100x30", new string('x', RemoteOutputText.MaxLength) + "\u2026") + Environment.NewLine, output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The command's connections never prompt (every attempt automatic, no window's prompts), and each says hello with a
    /// client instance id of its own: the daemon evicts only a connection that has the same one.
    /// </summary>
    [Fact]
    public async Task Each_connect_is_automatic_with_a_client_instance_id_of_its_own()
    {
        await StartLocalDaemonAsync("local shell");
        AddProfile("alpha", "a-host");
        AddProfile("beta", "b-host");

        var (code, _, _) = await RunAsync(Remotes(), "ls", "--all");

        Assert.Equal(0, code);
        Assert.Equal(2, _requests.Count);
        Assert.DoesNotContain(_requests, r => r.Interactive);
        Assert.Equal(2, _connectors.Count);
        Assert.Equal(2, _connectors.Select(c => c.ClientInstanceId).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>A window's connection to a remote daemon, attached to a session there, is still connected and attached afterwards.</summary>
    [Fact]
    public async Task A_window_attached_to_the_remote_daemon_stays_attached()
    {
        await StartLocalDaemonAsync("local shell");
        (SshProfile alpha, FakeRemoteHost alphaHost) = AddProfile("alpha", "a-host");
        var window = new RemoteMuxConnector(alpha, alphaHost, Guid.NewGuid().ToString("N"), log: null);
        _owned.Add(window);
        MuxClient windowClient = await window.ConnectAsync(interactive: true, Ct);
        _owned.Add(windowClient);
        Guid id = await MuxTestHost.SpawnAsync(windowClient);
        await MuxTestHost.AttachPaneAsync(windowClient, id);

        var (code, output, _) = await RunAsync(Remotes(), "ls", "--all");

        Assert.Equal(0, code);
        Assert.Contains(Row("nova@a-host", id, "running", 1, "80x24", "test") + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.True(windowClient.IsConnected);
        SessionSummary after = Assert.Single(await windowClient.ListSessionsAsync(Ct));
        Assert.Equal(1, after.AttachedClients);
    }

    /// <summary>What plain <c>ntilde mux ls --json</c> prints under the test's root.</summary>
    private string LsJson()
    {
        var o = new StringWriter();
        Assert.Equal(0, MuxCommand.Execute(["mux", "ls", "--json"], o, new StringWriter(), _root));
        return o.ToString();
    }

    private static string[] Names(JsonElement element) => [.. element.EnumerateObject().Select(p => p.Name)];

    /// <summary>An SSH profile store in memory: the real one is never read.</summary>
    private sealed class TestProfileStore : ISshProfileStore
    {
        private readonly ConcurrentDictionary<Guid, SshProfile> _profiles = new();

        public IReadOnlyList<SshProfile> GetProfiles() => [.. _profiles.Values];

        public SshProfile? GetProfile(Guid profileId) => _profiles.TryGetValue(profileId, out SshProfile? profile) ? profile : null;

        public void SaveProfile(SshProfile profile) => _profiles[profile.Id] = profile;

        public bool DeleteProfile(Guid profileId) => _profiles.TryRemove(profileId, out _);
    }
}
