using System.Collections.Concurrent;
using Ntilde.Controls;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Pty;
using Ntilde.Shell.Mux;
using Ntilde.Shell.Mux.Remote;
using Ntilde.Tests.Controls; // FakeTerminalSession, RecordingSessionFactory
using Ntilde.Tests.Shell.Mux.Remote;

namespace Ntilde.Tests.Shell.Mux;

/// <summary>
/// <see cref="MuxTerminalSessionFactory.CreatePersistent"/> for an SSH request whose profile persists
/// remote sessions (Phase 4 spec §7.5): it goes to that profile's remote daemon - a
/// <see cref="FakeRemoteHost"/> running the real proxy and daemon in-process - never to the local one.
/// </summary>
public sealed class MuxTerminalSessionFactoryRemoteTests : IDisposable
{
    private static readonly TimeSpan Patient = TimeSpan.FromSeconds(30);

    /// <summary>The reconnect loop's first wait at its longest: one second, jittered by 20%.</summary>
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(1.2);
    private readonly List<IDisposable> _owned = [];
    private readonly Dictionary<Guid, SshProfile> _profiles = [];
    private readonly SshProfile _profile = RemoteMuxConnectorTests.Profile();
    private readonly RecordingSessionFactory _fallback = new(new FakeTerminalSession());
    private readonly MuxConnectionHost _local;
    private readonly FakeRemoteHost _remote;
    private readonly MuxConnectionHosts _hosts;
    private readonly MuxTerminalSessionFactory _factory;

    public MuxTerminalSessionFactoryRemoteTests()
    {
        _profile.WorkingDirectory = "/srv/app";
        _profiles[_profile.Id] = _profile;
        _local = Own(new MuxConnectionHost(_ => throw new InvalidOperationException("a remote request never uses the local daemon"), "local", null));
        _remote = Own(new FakeRemoteHost());
        (_factory, _hosts) = Build(_remote);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Endpoint => $"ssh:{_profile.Id:N}";

    public void Dispose()
    {
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
    }

    private T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }

    /// <summary>The app's wiring with the remote transport swapped for <paramref name="remote"/>.</summary>
    private (MuxTerminalSessionFactory Factory, MuxConnectionHosts Hosts) Build(FakeRemoteHost remote)
    {
        MuxConnectionHosts hosts = Own(new MuxConnectionHosts(
            _local,
            id => RemoteMuxHostFactory.Create(id, Resolve, (_, _) => remote, log: null)));
        return (new MuxTerminalSessionFactory(hosts, _fallback, Resolve, log: null), hosts);
    }

    private SshProfile? Resolve(Guid id) => _profiles.GetValueOrDefault(id);

    /// <summary>
    /// Codex E1's wiring: the app's, with a clock the test moves (no reconnect before it says so), every host's log in
    /// <paramref name="log"/>, and <paramref name="chosen"/> as the id each remote spawn asks for.
    /// </summary>
    private (MuxTerminalSessionFactory Factory, MuxConnectionHosts Hosts, MuxConnectionHost Host) BuildWithClock(
        FakeMuxTimerScheduler clock, ConcurrentQueue<string> log, Guid chosen)
    {
        MuxConnectionHosts hosts = Own(new MuxConnectionHosts(
            _local,
            id => RemoteMuxHostFactory.Create(id, Resolve, (_, _) => _remote, log.Enqueue, userPrompts: null, scheduler: clock),
            log.Enqueue));
        var factory = new MuxTerminalSessionFactory(hosts, _fallback, Resolve, log.Enqueue) { NewRemoteSessionId = () => chosen };
        MuxConnectionHost host = hosts.GetOrCreate(MuxEndpointId.ForSsh(_profile.Id))!;
        Assert.NotNull(host.GetClient(Patient)); // connected before the spawn: the spawn is all that is in flight
        return (factory, hosts, host);
    }

    /// <summary>
    /// Runs <paramref name="request"/> through <paramref name="factory"/> and loses the spawn's reply: the daemon is held
    /// inside the spawn, the link goes silent, the daemon finishes - starting the shell, or failing when
    /// <paramref name="daemonFails"/> - and then the link drops, so the factory never hears back.
    /// </summary>
    private async Task<PersistentSessionResult> LoseTheSpawnReplyAsync(MuxTerminalSessionFactory factory, TerminalSessionRequest request, bool daemonFails = false)
    {
        ManualResetEventSlim gate = Own(new ManualResetEventSlim());
        _remote.Shells.CreateGate = gate;
        _remote.Shells.FailNext = daemonFails;
        Task<PersistentSessionResult> pending = Task.Run(() => factory.CreatePersistent(request), Ct);
        Assert.True(_remote.Shells.CreateEntered.Wait(Patient, Ct), "the daemon never got the spawn");

        _remote.StallLink(); // its reply will not get through
        gate.Set();
        await TestWait.UntilAsync(
            () => daemonFails ? !_remote.Shells.FailNext : _remote.Server.GetSessionIds().Count == 1,
            "the daemon handled the spawn", Patient);
        _remote.CutLink();   // and the link drops

        return await pending.WaitAsync(Patient, Ct);
    }

    private static TaskCompletionSource ClosedSignal(MuxConnectionHost host, Action? atClose = null)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Closed += _ =>
        {
            atClose?.Invoke();
            closed.TrySetResult();
        };
        return closed;
    }

    /// <summary>
    /// What an SSH pane asks for. Its command, directory and environment are the GUI host's (the
    /// shell-integration bootstrap's variables among them): none of them means anything to the remote.
    /// </summary>
    private TerminalSessionRequest Ssh(Guid? existing = null, bool reattachAfterDrop = false) => new(
        Command: "ssh",
        Arguments: "-l nova",
        StartingDirectory: @"C:\Users\nova",
        Cols: 100,
        Rows: 30,
        EnvironmentOverrides: new Dictionary<string, string> { ["NTILDE_SHELL_INTEGRATION_BOOTSTRAP"] = @"C:\Users\nova\bootstrap.ps1" },
        SkipPowerShellPostLaunchInit: true,
        Ssh: new SshSessionDescriptor(_profile.Id, 0, null, false),
        ExistingMuxSessionId: existing,
        ReattachAfterDrop: reattachAfterDrop);

    /// <summary>Another client of the same remote daemon (another window, or a dead twin of ours) that holds <paramref name="id"/>.</summary>
    private async Task HoldElsewhereAsync(FakeRemoteHost remote, Guid id)
    {
        RemoteMuxConnector other = Own(new RemoteMuxConnector(_profile, remote, "another-window", log: null));
        MuxClient client = Own(await other.ConnectAsync(Ct));
        await MuxTestHost.AttachPaneAsync(client, id);
    }

    [Fact]
    public void New_persisted_ssh_tab_spawns_on_the_remote_daemon()
    {
        TerminalSessionRequest request = Ssh();
        Assert.True(_factory.RoutesRemote(request));

        PersistentSessionResult r = _factory.CreatePersistent(request);

        Assert.Equal(PersistentSessionOutcome.Spawned, r.Outcome);
        Assert.Equal(Endpoint, r.Endpoint);
        Assert.Equal("nova@fake-host", r.HostDisplayName);
        Assert.Null(r.RemoteFailure);
        var session = Assert.IsType<MuxClientSession>(r.Session);
        Assert.False(session.IsAttached);
        Assert.Equal((string.Empty, (string?)null), (session.ShellCommand, session.ShellArguments));
        Assert.Contains(session.Id, _remote.Server.GetSessionIds());

        // Spec §3: an empty command is the remote daemon's default login shell, started in the
        // profile's working directory. None of the GUI host's environment is sent: the daemon's own
        // session variable is all the shell gets.
        TerminalSessionRequest spawned = Assert.Single(_remote.Shells.Requests);
        Assert.Equal(
            (string.Empty, string.Empty, "/srv/app", 100, 30, false),
            (spawned.Command, spawned.Arguments, spawned.StartingDirectory, spawned.Cols, spawned.Rows, spawned.SkipPowerShellPostLaunchInit));
        Assert.Equal(new[] { MuxServer.SessionEnvironmentVariable }, spawned.EnvironmentOverrides!.Keys);
        Assert.True(_remote.Server.TryGetSession(session.Id, out HeadlessTerminalSession? onDaemon));
        Assert.Equal(_profile.Name, onDaemon.Title);

        Assert.Null(_fallback.LastRequest);
        Assert.Equal(0, _local.ConnectAttempts);
    }

    [Fact]
    public void Profile_without_the_flag_is_NotPersistent()
    {
        _profile.MuxOptions.PersistRemoteSessions = false;
        TerminalSessionRequest request = Ssh(Guid.NewGuid());

        Assert.False(_factory.RoutesRemote(request));
        PersistentSessionResult r = _factory.CreatePersistent(request);

        Assert.Equal(PersistentSessionOutcome.NotPersistent, r.Outcome);
        Assert.IsType<FakeTerminalSession>(r.Session);
        Assert.Same(request, _fallback.LastRequest);
        Assert.Equal(((string?)null, (string?)null), (r.Endpoint, r.HostDisplayName));
        Assert.Equal(0, _remote.StartCount);
        Assert.Null(_hosts.TryGet(MuxEndpointId.ForSsh(_profile.Id)));
    }

    /// <summary>Spec §7.6: a restored id whose profile is gone opens plain SSH.</summary>
    [Fact]
    public void A_profile_that_is_gone_is_NotPersistent()
    {
        _profiles.Clear();
        TerminalSessionRequest request = Ssh(Guid.NewGuid());

        Assert.False(_factory.RoutesRemote(request));
        PersistentSessionResult r = _factory.CreatePersistent(request);

        Assert.Equal(PersistentSessionOutcome.NotPersistent, r.Outcome);
        Assert.Same(request, _fallback.LastRequest);
        Assert.Equal(0, _remote.StartCount);
    }

    /// <summary>A restore reopens the session exclusively, as locally (spec §7.6).</summary>
    [Fact]
    public void Restore_by_id_attaches_IfUnattached()
    {
        var first = Assert.IsType<MuxClientSession>(_factory.CreatePersistent(Ssh()).Session);
        Guid id = first.Id;
        first.Dispose(); // detach, as a pane does on Reconnect

        PersistentSessionResult r = _factory.CreatePersistent(Ssh(id));

        Assert.Equal(PersistentSessionOutcome.Reattached, r.Outcome);
        var mine = Assert.IsType<MuxClientSession>(r.Session);
        Assert.Equal((id, MuxAttachMode.IfUnattached), (mine.Id, mine.AttachMode));
        Assert.Equal((Endpoint, "nova@fake-host"), (r.Endpoint, r.HostDisplayName));
        Assert.Single(_remote.Server.GetSessionIds());
        Assert.Single(_remote.Shells.Requests);
    }

    /// <summary>
    /// The local command check would take a remote session that names a program (one the remote CLI
    /// started, say) for a foreign one and leave it behind: a remote restore never compares commands.
    /// </summary>
    [Fact]
    public async Task A_remote_restore_never_compares_commands()
    {
        MuxClient cli = Own(await Own(new RemoteMuxConnector(_profile, _remote, "remote-cli", log: null)).ConnectAsync(Ct));
        Guid id = await MuxTestHost.SpawnAsync(cli); // Command = "scripted"; the pane's request says "ssh"

        PersistentSessionResult r = _factory.CreatePersistent(Ssh(id));

        Assert.Equal(PersistentSessionOutcome.Reattached, r.Outcome);
        Assert.Equal(id, Assert.IsType<MuxClientSession>(r.Session).Id);
        Assert.Single(_remote.Server.GetSessionIds());
    }

    [Fact]
    public void A_restore_whose_session_is_gone_starts_a_fresh_remote_shell_and_says_PreviousLost()
    {
        PersistentSessionResult r = _factory.CreatePersistent(Ssh(Guid.NewGuid()));

        Assert.Equal(PersistentSessionOutcome.PreviousLost, r.Outcome);
        Assert.Contains(Assert.IsType<MuxClientSession>(r.Session).Id, _remote.Server.GetSessionIds());
        Assert.Equal(string.Empty, Assert.Single(_remote.Shells.Requests).Command);
        Assert.Equal(Endpoint, r.Endpoint);
    }

    /// <summary>
    /// Spec §7.4: after a drop the pane reattaches Shared, because its own dead connection - which a
    /// Phase 3 daemon does not evict - may still hold the session.
    /// </summary>
    [Fact]
    public async Task ReattachAfterDrop_attaches_Shared()
    {
        var first = Assert.IsType<MuxClientSession>(_factory.CreatePersistent(Ssh()).Session);
        Guid id = first.Id;
        first.Dispose();
        await HoldElsewhereAsync(_remote, id);

        PersistentSessionResult r = _factory.CreatePersistent(Ssh(id, reattachAfterDrop: true));

        Assert.Equal(PersistentSessionOutcome.Reattached, r.Outcome);
        var mine = Assert.IsType<MuxClientSession>(r.Session);
        Assert.Equal((id, MuxAttachMode.Shared), (mine.Id, mine.AttachMode));
        Assert.Equal(Endpoint, r.Endpoint);
        await Task.Run(() => mine.AttachAsync(0, MuxTestHost.DefaultPresentation, Ct), Ct).WaitAsync(Patient, Ct);
        Assert.True(_remote.Server.TryGetSession(id, out HeadlessTerminalSession? onDaemon));
        await TestWait.UntilAsync(() => onDaemon.AttachedClients == 2, "both clients attached");
    }

    [Fact]
    public void ReattachAfterDrop_to_a_session_that_ended_starts_fresh_and_says_PreviousLost()
    {
        PersistentSessionResult r = _factory.CreatePersistent(Ssh(Guid.NewGuid(), reattachAfterDrop: true));

        Assert.Equal(PersistentSessionOutcome.PreviousLost, r.Outcome);
        var fresh = Assert.IsType<MuxClientSession>(r.Session);
        Assert.Contains(fresh.Id, _remote.Server.GetSessionIds());
        Assert.Equal("/srv/app", Assert.Single(_remote.Shells.Requests).StartingDirectory);
    }

    /// <summary>A Phase 3 (v1) remote daemon has no exclusive attach: the factory's own check keeps a held session where it is.</summary>
    [Fact]
    public async Task On_v1_a_restore_held_elsewhere_starts_a_new_remote_shell()
    {
        FakeRemoteHost v1 = Own(new FakeRemoteHost(serverOptions: new MuxServerOptions { ForceConPtyFiltering = false, MaxProtocolVersion = 1 }));
        (MuxTerminalSessionFactory factory, _) = Build(v1);
        var first = Assert.IsType<MuxClientSession>(factory.CreatePersistent(Ssh()).Session);
        Guid theirs = first.Id;
        first.Dispose();
        await HoldElsewhereAsync(v1, theirs);

        PersistentSessionResult r = factory.CreatePersistent(Ssh(theirs));

        Assert.Equal(PersistentSessionOutcome.AttachedElsewhere, r.Outcome);
        Guid mine = Assert.IsType<MuxClientSession>(r.Session).Id;
        Assert.NotEqual(theirs, mine);
        Assert.Equal(2, v1.Server.GetSessionIds().Count);
    }

    /// <summary>Spec §7.5: a new tab whose remote has no ntilde-mux still opens - as plain SSH - and the toast offers the install.</summary>
    [Fact]
    public void Not_installed_falls_back_to_plain_ssh_with_an_install_action()
    {
        _remote.Script = FakeRemoteScript.NotInstalledDash;
        TerminalSessionRequest request = Ssh();

        PersistentSessionResult r = _factory.CreatePersistent(request);

        Assert.Equal(PersistentSessionOutcome.Unavailable, r.Outcome);
        Assert.IsType<FakeTerminalSession>(r.Session);
        Assert.Same(request, _fallback.LastRequest);
        Assert.Equal(RemoteFailureKind.NotInstalled, r.RemoteFailure!.Kind);
        Assert.Equal(r.RemoteFailure.Reason, r.Detail);
        Assert.Equal((Endpoint, "nova@fake-host"), (r.Endpoint, r.HostDisplayName));

        Guid? opened = null;
        PersistenceNoticeAction action = Assert.IsType<PersistenceNoticeAction>(
            PersistenceNoticeAction.ForRemoteFailure(r.RemoteFailure, _profile.Id, r.HostDisplayName!, id => opened = id));
        Assert.Equal("Install ntilde-mux on nova@fake-host\u2026", action.Label);
        action.Run();
        Assert.Equal(_profile.Id, opened);
    }

    /// <summary>Spec §7.5: a reopen keeps its id and starts nothing - the shell may still be on that daemon - and the toast offers the update.</summary>
    [Fact]
    public void Version_mismatch_with_an_id_is_DaemonUnreachable()
    {
        FakeRemoteHost newer = Own(new FakeRemoteHost(serverOptions: new MuxServerOptions { ForceConPtyFiltering = false, MinProtocolVersion = 3, MaxProtocolVersion = 4 }));
        (MuxTerminalSessionFactory factory, _) = Build(newer);

        PersistentSessionResult r = factory.CreatePersistent(Ssh(Guid.NewGuid()));

        Assert.Equal(PersistentSessionOutcome.DaemonUnreachable, r.Outcome);
        Assert.Null(r.Session);
        Assert.Null(_fallback.LastRequest);
        Assert.Equal(RemoteFailureKind.VersionMismatch, r.RemoteFailure!.Kind);
        Assert.False(r.VersionMismatch); // the local daemon's kill-server hint does not apply to a remote one
        Assert.Equal((Endpoint, "nova@fake-host"), (r.Endpoint, r.HostDisplayName));
        Assert.Equal("Update ntilde-mux on nova@fake-host\u2026", PersistenceNoticeAction.ForRemoteFailure(r.RemoteFailure, _profile.Id, r.HostDisplayName!, _ => { })?.Label);
    }

    /// <summary>The brief's rule: only a missing or outdated ntilde-mux has an action - installing fixes nothing else.</summary>
    [Theory]
    [InlineData(nameof(RemoteFailureKind.NotInstalled), "Install ntilde-mux on nova@fake-host\u2026")]
    [InlineData(nameof(RemoteFailureKind.VersionMismatch), "Update ntilde-mux on nova@fake-host\u2026")]
    [InlineData(nameof(RemoteFailureKind.Unsupported), null)]
    [InlineData(nameof(RemoteFailureKind.SshFailed), null)]
    [InlineData(nameof(RemoteFailureKind.ProxyFailed), null)]
    [InlineData(nameof(RemoteFailureKind.NeedsUser), null)]
    public void Only_a_missing_or_outdated_ntilde_mux_offers_an_action(string kind, string? label)
    {
        var failure = new RemoteMuxFailure(Enum.Parse<RemoteFailureKind>(kind), "why");

        Assert.Equal(label, PersistenceNoticeAction.ForRemoteFailure(failure, _profile.Id, "nova@fake-host", _ => { })?.Label);
        Assert.Null(PersistenceNoticeAction.ForRemoteFailure(failure, _profile.Id, "nova@fake-host", openInstall: null)); // no install flow to open
        Assert.Null(PersistenceNoticeAction.ForRemoteFailure(null, _profile.Id, "nova@fake-host", _ => { }));
    }

    /// <summary>
    /// Controller ruling over spec §7.5: when SSH itself failed, a plain SSH session would only fail - or
    /// prompt - again. A new tab gets no session, and the pane offers a retry.
    /// </summary>
    [Fact]
    public void A_new_tab_whose_ssh_connect_fails_shows_a_retry_not_plain_ssh()
    {
        _remote.Script = FakeRemoteScript.ConnectionRefused;

        PersistentSessionResult r = _factory.CreatePersistent(Ssh());

        Assert.Equal(PersistentSessionOutcome.DaemonUnreachable, r.Outcome);
        Assert.Null(r.Session);
        Assert.Null(_fallback.LastRequest);
        Assert.Equal(RemoteFailureKind.SshFailed, r.RemoteFailure!.Kind);
        Assert.Equal(r.RemoteFailure.Reason, r.Detail);
        Assert.Equal((Endpoint, "nova@fake-host"), (r.Endpoint, r.HostDisplayName));
        Assert.Null(PersistenceNoticeAction.ForRemoteFailure(r.RemoteFailure, _profile.Id, r.HostDisplayName!, _ => { }));
    }

    /// <summary>An unclassified failure - the connect did not finish in time - is treated as an SSH failure: no plain SSH.</summary>
    [Fact]
    public void A_new_tab_whose_connect_does_not_finish_shows_a_retry_not_plain_ssh()
    {
        _remote.Script = FakeRemoteScript.Silent; // a prompt nobody answers
        var factory = new MuxTerminalSessionFactory(_hosts, _fallback, Resolve, log: null) { ConnectTimeout = TimeSpan.FromMilliseconds(300) };

        PersistentSessionResult r = factory.CreatePersistent(Ssh());

        Assert.Equal(PersistentSessionOutcome.DaemonUnreachable, r.Outcome);
        Assert.Null(r.Session);
        Assert.Null(r.RemoteFailure);
        Assert.Null(_fallback.LastRequest);
        Assert.Equal((Endpoint, "nova@fake-host"), (r.Endpoint, r.HostDisplayName));
        Assert.NotNull(r.Detail);
    }

    /// <summary>
    /// Task 20's ruling: signing in needed an answer nobody gave (no window to ask through here). Like an
    /// SSH failure, a plain SSH session would only ask again: no session, and the pane offers a retry.
    /// </summary>
    [Fact]
    public void A_new_tab_whose_sign_in_needs_the_user_shows_a_retry_not_plain_ssh()
    {
        MuxConnectionHosts hosts = Own(new MuxConnectionHosts(_local, id => RemoteMuxHostFactory.Create(id, Resolve, (_, request) =>
        {
            _remote.OnStart = _ =>
            {
                try
                {
                    request.Prompts.HandleAsync(RemoteMuxConnectorTests.PasswordPrompt, CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (RemoteMuxPromptAbortedException ex)
                {
                    // As the native channel does: the session closes without answering.
                    _remote.Script = FakeRemoteScript.NativeFailure($"the native session failed: {ex.Message}");
                }
            };
            return _remote;
        }, log: null)));
        var factory = new MuxTerminalSessionFactory(hosts, _fallback, Resolve, log: null);

        PersistentSessionResult r = factory.CreatePersistent(Ssh());

        Assert.Equal(PersistentSessionOutcome.DaemonUnreachable, r.Outcome);
        Assert.Null(r.Session);
        Assert.Null(_fallback.LastRequest);
        Assert.Equal(RemoteFailureKind.NeedsUser, r.RemoteFailure!.Kind);
        Assert.Equal((Endpoint, "nova@fake-host"), (r.Endpoint, r.HostDisplayName));
    }

    /// <summary>
    /// Task 20's ruling: an attempt that does not finish is unclassified even after an earlier one failed
    /// classified - a blackholed host must not be reported as the earlier attempt's missing binary.
    /// </summary>
    [Fact]
    public void A_new_tab_whose_connect_does_not_finish_after_a_classified_failure_shows_a_retry()
    {
        _remote.Script = FakeRemoteScript.NotInstalledDash;
        Assert.Equal(PersistentSessionOutcome.Unavailable, _factory.CreatePersistent(Ssh()).Outcome);
        _remote.Script = FakeRemoteScript.Silent;
        var factory = new MuxTerminalSessionFactory(_hosts, _fallback, Resolve, log: null) { ConnectTimeout = TimeSpan.FromMilliseconds(300) };

        PersistentSessionResult r = factory.CreatePersistent(Ssh());

        Assert.Equal(PersistentSessionOutcome.DaemonUnreachable, r.Outcome);
        Assert.Null(r.Session);
        Assert.Null(r.RemoteFailure);
    }

    [Fact]
    public void A_new_tab_whose_connection_cannot_be_set_up_shows_a_retry_not_plain_ssh()
    {
        MuxConnectionHosts hosts = Own(new MuxConnectionHosts(_local, _ => throw new InvalidOperationException("no transport for this profile")));
        var factory = new MuxTerminalSessionFactory(hosts, _fallback, Resolve, log: null);

        PersistentSessionResult r = factory.CreatePersistent(Ssh());

        Assert.Equal(PersistentSessionOutcome.DaemonUnreachable, r.Outcome);
        Assert.Null(r.Session);
        Assert.Null(r.RemoteFailure);
        Assert.Null(_fallback.LastRequest);
        Assert.Equal((Endpoint, "nova@fake-host", "no transport for this profile"), (r.Endpoint, r.HostDisplayName, r.Detail));
    }

    /// <summary>The SSH link works and only ntilde-mux does not: a new tab still opens, as plain SSH, with the notice.</summary>
    [Theory]
    [InlineData(nameof(RemoteFailureKind.Unsupported))]
    [InlineData(nameof(RemoteFailureKind.VersionMismatch))]
    [InlineData(nameof(RemoteFailureKind.ProxyFailed))]
    public void When_ssh_works_but_ntilde_mux_does_not_a_new_tab_falls_back_to_plain_ssh(string kind)
    {
        MuxTerminalSessionFactory factory = _factory;
        switch (Enum.Parse<RemoteFailureKind>(kind))
        {
            case RemoteFailureKind.Unsupported:
                _remote.Script = new FakeRemoteScript(Stderr: "sh: 1: /home/nova/.local/share/ntilde/bin/ntilde-mux: Exec format error\n", ExitCode: 126);
                break;
            case RemoteFailureKind.ProxyFailed:
                _remote.Script = new FakeRemoteScript(Stderr: "mux: The multiplexer did not come up within 10 s.\n", ExitCode: 1);
                break;
            default:
                (factory, _) = Build(Own(new FakeRemoteHost(serverOptions: new MuxServerOptions { ForceConPtyFiltering = false, MinProtocolVersion = 3, MaxProtocolVersion = 4 })));
                break;
        }

        TerminalSessionRequest request = Ssh();
        PersistentSessionResult r = factory.CreatePersistent(request);

        Assert.Equal(PersistentSessionOutcome.Unavailable, r.Outcome);
        Assert.IsType<FakeTerminalSession>(r.Session);
        Assert.Same(request, _fallback.LastRequest);
        Assert.Equal(kind, r.RemoteFailure!.Kind.ToString());
        Assert.Equal((Endpoint, "nova@fake-host"), (r.Endpoint, r.HostDisplayName));
    }

    [Fact]
    public void A_remote_that_cannot_be_reached_on_a_reopen_keeps_its_id_and_names_its_endpoint()
    {
        _remote.Script = FakeRemoteScript.ConnectionRefused;

        PersistentSessionResult r = _factory.CreatePersistent(Ssh(Guid.NewGuid()));

        Assert.Equal(PersistentSessionOutcome.DaemonUnreachable, r.Outcome);
        Assert.Null(r.Session);
        Assert.Equal(RemoteFailureKind.SshFailed, r.RemoteFailure!.Kind);
        Assert.Equal((Endpoint, "nova@fake-host"), (r.Endpoint, r.HostDisplayName));
        Assert.Null(_fallback.LastRequest);
    }

    /// <summary>
    /// Codex E1: the daemon starts the shell, but the link drops before its reply arrives. Nothing adopts a remote
    /// session, so that shell would run on with no tab to close it. The tab still gets plain SSH, as before; the
    /// session the factory named is ended once the host is connected again, and a release of the endpoint - the tab
    /// uses nothing on it now - waits for that kill (final review F1).
    /// </summary>
    [Fact]
    public async Task A_spawn_whose_reply_is_lost_falls_back_to_plain_ssh_and_its_shell_is_ended_once_connected()
    {
        var clock = new FakeMuxTimerScheduler();
        var log = new ConcurrentQueue<string>();
        Guid chosen = Guid.NewGuid();
        (MuxTerminalSessionFactory factory, MuxConnectionHosts hosts, MuxConnectionHost host) = BuildWithClock(clock, log, chosen);
        ManualResetEventSlim linkBack = Own(new ManualResetEventSlim());
        _remote.OnStart = ct => linkBack.Wait(ct); // no new link until the test says so
        TerminalSessionRequest request = Ssh();

        PersistentSessionResult r = await LoseTheSpawnReplyAsync(factory, request);

        Assert.Equal(PersistentSessionOutcome.Unavailable, r.Outcome);
        Assert.IsType<FakeTerminalSession>(r.Session);
        Assert.Same(request, _fallback.LastRequest);
        Assert.Equal((Endpoint, "nova@fake-host"), (r.Endpoint, r.HostDisplayName));
        Assert.Contains(log, l => l.Contains($"nova@fake-host: a spawn's reply was lost; session {chosen} will be ended once connected", StringComparison.Ordinal));

        bool? endedAtClose = null;
        TaskCompletionSource closed = ClosedSignal(host, () => endedAtClose = !_remote.Server.GetSessionIds().Contains(chosen));
        hosts.Release(MuxEndpointId.ForSsh(_profile.Id)); // the window's release pass: no pane uses the endpoint now

        Assert.False(host.IsClosed); // the kill has yet to go out, and the release waits for it
        Assert.Equal(new[] { chosen }, _remote.Server.GetSessionIds()); // the shell runs on, under the id the factory chose
        linkBack.Set();
        clock.Advance(FirstRetry); // the loop's first retry, unless the kill's own attempt got in first
        await closed.Task.WaitAsync(Patient, Ct);
        Assert.True(endedAtClose);
        Assert.Empty(_remote.Server.GetSessionIds());
    }

    /// <summary>
    /// Codex E1: when the daemon never started the shell (it failed, and that reply was lost too), the kill finds no
    /// such session. The host logs it once and moves on: no retry, and the release is not held up.
    /// </summary>
    [Fact]
    public async Task The_kill_of_a_lost_spawn_the_daemon_never_ran_is_logged_and_dropped()
    {
        var clock = new FakeMuxTimerScheduler();
        var log = new ConcurrentQueue<string>();
        Guid chosen = Guid.NewGuid();
        (MuxTerminalSessionFactory factory, MuxConnectionHosts hosts, MuxConnectionHost host) = BuildWithClock(clock, log, chosen);
        ManualResetEventSlim linkBack = Own(new ManualResetEventSlim());
        _remote.OnStart = ct => linkBack.Wait(ct);

        PersistentSessionResult r = await LoseTheSpawnReplyAsync(factory, Ssh(), daemonFails: true);

        Assert.Equal(PersistentSessionOutcome.Unavailable, r.Outcome);
        Assert.IsType<FakeTerminalSession>(r.Session);
        Assert.Empty(_remote.Server.GetSessionIds());
        Assert.Contains(log, l => l.Contains($"a spawn's reply was lost; session {chosen} will be ended once connected", StringComparison.Ordinal));

        TaskCompletionSource closed = ClosedSignal(host);
        hosts.Release(MuxEndpointId.ForSsh(_profile.Id));
        Assert.False(host.IsClosed);
        linkBack.Set();
        clock.Advance(FirstRetry);
        await closed.Task.WaitAsync(Patient, Ct);

        Assert.Single(log, l => l.Contains($"killing session {chosen} failed", StringComparison.Ordinal));
        Assert.Equal(2, _remote.StartCount); // the first connect, and the one reconnect that carried the kill
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(2, _remote.StartCount);
    }

    /// <summary>
    /// Codex E1: the daemon answered with an error, so it started nothing - no kill is queued. When the refusal is that
    /// the id is taken, the session holding it is someone else's and must not be killed.
    /// </summary>
    [Theory]
    [InlineData(MuxErrorCodes.SpawnFailed)]
    [InlineData(MuxErrorCodes.SessionExists)]
    public async Task A_spawn_the_daemon_refuses_queues_no_kill(string refusal)
    {
        var clock = new FakeMuxTimerScheduler();
        var log = new ConcurrentQueue<string>();
        Guid chosen = Guid.NewGuid();
        (MuxTerminalSessionFactory factory, MuxConnectionHosts hosts, MuxConnectionHost host) = BuildWithClock(clock, log, chosen);
        HeadlessTerminalSession? theirs = null;
        if (refusal == MuxErrorCodes.SessionExists)
        {
            // Another window's session already runs under that id (contrived: a new Guid never collides).
            MuxClient other = Own(await Own(new RemoteMuxConnector(_profile, _remote, "another-window", log: null)).ConnectAsync(Ct));
            Assert.Equal(chosen, await other.SpawnAsync(new SpawnParams { Command = "scripted", Cols = 80, Rows = 24 }, chosen, Ct));
            await MuxTestHost.AttachPaneAsync(other, chosen);
            Assert.True(_remote.Server.TryGetSession(chosen, out theirs));
            await TestWait.UntilAsync(() => theirs.AttachedClients == 1, "the other window attached");
        }
        else
        {
            _remote.Shells.FailNext = true;
        }

        int starts = _remote.StartCount;
        PersistentSessionResult r = factory.CreatePersistent(Ssh());

        Assert.Equal(PersistentSessionOutcome.Unavailable, r.Outcome);
        Assert.IsType<FakeTerminalSession>(r.Session);

        TaskCompletionSource closed = ClosedSignal(host);
        hosts.Release(MuxEndpointId.ForSsh(_profile.Id)); // a release waits for every kill to settle: none was sent
        await closed.Task.WaitAsync(Patient, Ct);
        Assert.DoesNotContain(log, l => l.Contains("will be ended", StringComparison.Ordinal) || l.Contains("killing session", StringComparison.Ordinal));
        Assert.Equal(starts, _remote.StartCount); // and no connect to deliver one
        if (theirs is not null)
        {
            Assert.Equal(new[] { chosen }, _remote.Server.GetSessionIds());
            Assert.False(theirs.IsExited);
            Assert.Equal(1, theirs.AttachedClients);
        }
    }

    /// <summary>
    /// Codex E1: the reply arrived, but the session never reached a pane - here the open fails, because this client
    /// already has that id open. The session runs on the daemon with nobody to close it, so it is ended.
    /// </summary>
    [Fact]
    public async Task A_remote_session_no_pane_took_is_ended()
    {
        var clock = new FakeMuxTimerScheduler();
        var log = new ConcurrentQueue<string>();
        Guid chosen = Guid.NewGuid();
        (MuxTerminalSessionFactory factory, _, MuxConnectionHost host) = BuildWithClock(clock, log, chosen);
        Own(host.GetClient(Patient)!.OpenSession(chosen)); // the factory's open of that id will be refused

        PersistentSessionResult r = factory.CreatePersistent(Ssh());

        Assert.Equal(PersistentSessionOutcome.Unavailable, r.Outcome);
        Assert.IsType<FakeTerminalSession>(r.Session);
        Assert.Contains(log, l => l.Contains($"nova@fake-host: session {chosen} was started but no pane took it; it will be ended once connected", StringComparison.Ordinal));
        ScriptedTerminalSession shell = Assert.Single(_remote.Shells.Sessions);
        await TestWait.UntilAsync(() => !_remote.Server.GetSessionIds().Contains(chosen) && shell.Disposed, "the kill reached the daemon", Patient);
    }
}
