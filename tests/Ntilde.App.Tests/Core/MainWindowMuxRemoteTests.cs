using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Models;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.Storage;
using Ntilde.Pty;
using Ntilde.Services.Ssh;
using Ntilde.Shell;
using Ntilde.Shell.Mux;
using Ntilde.Shell.Mux.Remote;
using Ntilde.Tests.Controls; // FakeTerminalSession, RecordingSessionFactory
using Ntilde.Tests.Shell.Mux;
using Ntilde.Tests.Shell.Mux.Remote;
using static Ntilde.Tests.Core.WindowToast;

namespace Ntilde.Tests.Core;

/// <summary>
/// The window's side of persisted remote panes (Phase 4 spec §5, §7.6, §8.4): a restored session file
/// whose SSH panes name sessions on their profile's remote daemon - a <see cref="FakeRemoteHost"/> on a clock
/// the test advances - reattaches them over one connection, a remote tab closed while its link is down still
/// ends its shell (a shared one detaches instead: Phase 5 Task 26), the window's lists of "its" daemon sessions mean the
/// local daemon only, and the remote-files sidebar is not offered on such a tab.
/// </summary>
/// <remarks>
/// <see cref="TestAppDataRoot"/> is taken for its lifetime: the test writes the session file and the SSH
/// profile store there, and a closing window saves its session there (see MainWindowMuxLifecycleTests).
/// </remarks>
public sealed class MainWindowMuxRemoteTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    private static readonly TimeSpan Patient = TimeSpan.FromSeconds(30);

    /// <summary>The reconnect loop's first wait at its longest: one second, jittered by 20%.</summary>
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(1.2);

    private readonly MuxTestHost _localMux = new();
    private readonly FakeRemoteHost _remote = new();
    private readonly FakeMuxTimerScheduler _clock = new();
    private readonly SshProfile _sshProfile = RemoteMuxConnectorTests.Profile();
    private MuxConnectionHost? _local;
    private MuxConnectionHosts? _hosts;

    public MainWindowMuxRemoteTests()
    {
        if (File.Exists(AppPaths.SessionFilePath)) File.Delete(AppPaths.SessionFilePath);
        // Native: the remote-files sidebar would be offered on a plain SSH tab of this profile.
        _sshProfile.BackendKind = SshBackendKind.Native;
        // The restore resolves an SSH leaf's profile from the store (SessionManager.TryResolvePaneProfile).
        new JsonSshProfileStore().SaveProfile(_sshProfile);
    }

    public void Dispose()
    {
        TestMainWindowFactory.DisposeCreatedWindows();
        // The window's hosts, remote ones included: closing a remote pane above can start a host's connect for its kill.
        _hosts?.Dispose();
        _local?.Dispose();
        foreach (MuxClient client in _otherClients) client.Dispose();
        foreach (FakeRemoteHost other in _otherRemotes) other.Dispose();
        _remote.Dispose();
        _localMux.Dispose();
        new JsonSshProfileStore().DeleteProfile(_sshProfile.Id);
        if (_twin is { } twin) new JsonSshProfileStore().DeleteProfile(twin.Profile.Id);
    }

    /// <summary>
    /// A second profile and its host, set by a test before <see cref="CreateWindow"/>: the window resolves it and connects
    /// to its host as for <see cref="_sshProfile"/>.
    /// </summary>
    private (SshProfile Profile, FakeRemoteHost Host)? _twin;

    /// <summary>Every remote host's request wait (its policy's <c>RpcTimeout</c>), set by a test before <see cref="CreateWindow"/>; the policy's own when null.</summary>
    private TimeSpan? _remoteRpcTimeout;

    private string Endpoint => MuxEndpointId.ForSsh(_sshProfile.Id).ToString();

    private volatile bool _profileGone; // the profile is deleted: every lookup misses it from then on

    /// <summary>What each remote attempt's transport was asked for, in order: an automatic attempt is not interactive.</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<RemoteMuxTransportRequest> _transportRequests = new();

    private SshProfile? Resolve(Guid id) =>
        id == _sshProfile.Id && !_profileGone ? _sshProfile
        : _twin is { } twin && id == twin.Profile.Id ? twin.Profile
        : null;

    private int _uiThread;
    private int _factoryCallsOffUi; // CreatePersistent resolves the profile once, first: one per remote factory call

    /// <summary>The factory's own lookup, counting the calls made off the UI thread (the panes' remote factory calls).</summary>
    private SshProfile? FactoryResolve(Guid id)
    {
        if (Environment.CurrentManagedThreadId != Volatile.Read(ref _uiThread))
        {
            Interlocked.Increment(ref _factoryCallsOffUi);
            if (Interlocked.Exchange(ref _stopPersistingInTheNextFactoryCall, 0) == 1) _sshProfile.MuxOptions.PersistRemoteSessions = false;
        }

        return Resolve(id);
    }

    /// <summary>1: the next remote factory call finds the profile no longer keeping its sessions (it was routed while it did).</summary>
    private int _stopPersistingInTheNextFactoryCall;

    /// <summary>The app's wiring over the test's daemons: the local one in memory, the remote one behind <see cref="_remote"/>.</summary>
    /// <param name="bootTimeUtc">The boot (or logon) the window's startup restore sees (spec R2); the real one when null.</param>
    /// <param name="beforeShow">Runs on the built window before it is shown, when no pane has spawned yet.</param>
    /// <param name="remote">The profile's host; <see cref="_remote"/> when null.</param>
    /// <param name="settings">The window's settings; the designer's (SessionPersistence Off) when null.</param>
    /// <param name="build">Builds the window from its services (a subclass overriding its dialogs); a plain one when null.</param>
    private MainWindow CreateWindow(DateTime? bootTimeUtc = null, Action<MainWindow>? beforeShow = null, FakeRemoteHost? remote = null, TerminalSettings? settings = null, Func<AppServiceBundle, MainWindow>? build = null)
    {
        Volatile.Write(ref _uiThread, Environment.CurrentManagedThreadId);
        _local = new MuxConnectionHost(ct => MuxClient.ConnectAsync(_localMux.Listener.Connect(), null, ct), "test", null)
        {
            DisposeFlushTimeout = TimeSpan.FromSeconds(1),
        };
        string? typed = _typedPassword;
        var hosts = _hosts = new MuxConnectionHosts(_local, id => RemoteMuxHostFactory.Create(id, Resolve, (profile, request) =>
        {
            _transportRequests.Enqueue(request);
            if (typed is not null && request.Interactive) AskTheTargetsPassword(profile, request);
            return _twin is { } twin && profile.Id == twin.Profile.Id ? twin.Host : remote ?? _remote;
        }, log: null, userPrompts: typed is null ? null : new TypingUser(typed), scheduler: _clock, rpcTimeout: _remoteRpcTimeout));
        var factory = new MuxTerminalSessionFactory(hosts, new RecordingSessionFactory(new FakeTerminalSession()), FactoryResolve, null);
        AppServiceBundle services = AppServices.BuildForDesigner() with
        {
            CommandAssist = TestCommandAssistServices.Instance,
            SessionFactory = factory,
        };
        if (settings is not null)
        {
            services = services with { Settings = settings };
        }
        if (bootTimeUtc is { } boot)
        {
            services = services with { SessionsCannotPredateUtc = () => boot };
        }

        MainWindow window = build is null ? TestMainWindowFactory.Create(services) : TestMainWindowFactory.Track(build(services));
        beforeShow?.Invoke(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>
    /// Set by a test before <see cref="CreateWindow"/>: the password the user types when a user's attempt meets the target's
    /// password prompt (Phase 5 Task 28); with none, no prompt is raised and the window has no prompts to ask.
    /// </summary>
    private string? _typedPassword;

    /// <summary>
    /// The target's password prompt as the native exec transport raises it before the proxy runs: answered by the attempt's
    /// prompts (<see cref="RemoteMuxTransportRequest.Prompts"/>), which ask the window's handler - here a user typing
    /// <see cref="_typedPassword"/>. Off the UI thread, inside the connector's start.
    /// </summary>
    private static void AskTheTargetsPassword(SshProfile profile, RemoteMuxTransportRequest request) =>
        request.Prompts.HandleAsync(new Ntilde.Platform.Ssh.Interactions.SshInteractionRequest
        {
            Kind = Ntilde.Platform.Ssh.Interactions.SshInteractionKind.Password,
            Prompt = "Password:",
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            ProfileUser = profile.User,
            ProfileHost = profile.Host,
            AllowVaultPasswordReuse = true,
            RememberPasswordInVault = true,
            Host = profile.Host,
            Port = profile.Port > 0 ? profile.Port : 22,
            User = profile.User,
        }, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>A user at the window's password dialog who types the same password every time.</summary>
    private sealed class TypingUser(string password) : Ntilde.Platform.Ssh.Interactions.ISshInteractionHandler
    {
        public Task<Ntilde.Platform.Ssh.Interactions.SshInteractionResponse> HandleAsync(Ntilde.Platform.Ssh.Interactions.SshInteractionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(request.Kind == Ntilde.Platform.Ssh.Interactions.SshInteractionKind.Password
                ? Ntilde.Platform.Ssh.Interactions.SshInteractionResponse.FromSecret(password)
                : Ntilde.Platform.Ssh.Interactions.SshInteractionResponse.Cancel());
    }

    /// <summary>A window whose transfer dialog and local file pickers answer at once, and whose transfers are only recorded.</summary>
    private sealed class TransferWindow(AppServiceBundle services) : MainWindow(services)
    {
        public List<TransferDialogRequest> Dialogs { get; } = [];

        public List<TransferJob> Queued { get; } = [];

        /// <summary>What happens while the transfer dialog or a file picker is open (the user edits the profile, say); nothing when null.</summary>
        public Action? WhileTheDialogIsOpen { get; set; }

        internal override Task<TransferDialogResult?> ShowTransferDialogAsync(TransferDialogRequest request)
        {
            Dialogs.Add(request);
            WhileTheDialogIsOpen?.Invoke();
            return Task.FromResult<TransferDialogResult?>(TransferDialogResult.CreateConfirmed(localPath: @"D:\transfers\a.txt", remotePath: "/srv/a.txt"));
        }

        /// <summary>How many local file pickers were shown.</summary>
        public int Pickers { get; private set; }

        internal override Task<string?> PickLocalDownloadFilePathAsync(string suggestedFileName)
        {
            Pickers++;
            WhileTheDialogIsOpen?.Invoke();
            return Task.FromResult<string?>(@"D:\transfers\" + suggestedFileName);
        }

        internal override void EnqueueTransferJob(TransferJob job) => Queued.Add(job);
    }

    /// <summary>A session file with one tab: these leaves side by side.</summary>
    private static void SaveSession(params PaneNode[] leaves)
    {
        PaneNode root = leaves.Length == 1
            ? leaves[0]
            : new PaneNode { Type = NodeType.Split, SplitOrientation = 0, Children = [.. leaves] };
        SaveTabs(root);
    }

    /// <summary>A session file with one tab per root, the first selected: the others restore in the background, unshown.</summary>
    private static void SaveTabs(params PaneNode[] roots)
    {
        var session = new NtildeSession { ActiveTabIndex = 0 };
        foreach (PaneNode root in roots) session.Tabs.Add(new TabSession { Title = $"tab {session.Tabs.Count}", Root = root });
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.SessionFilePath)!);
        File.WriteAllText(AppPaths.SessionFilePath, JsonSerializer.Serialize(session, SessionSerializationContext.Default.NtildeSession));
    }

    private static PaneNode LocalLeaf() => new() { Type = NodeType.Leaf, Command = "scripted" };

    private MuxConnectionHost? RemoteHostOf(MainWindow window) => window.MuxHosts!.TryGet(MuxEndpointId.ForSsh(_sshProfile.Id));

    private static Task<bool> Close(MainWindow window, TerminalPane pane) =>
        (Task<bool>)typeof(MainWindow).GetMethod("ClosePaneAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [pane, true])!;

    /// <summary>Waits until <paramref name="id"/> is gone from the remote daemon, moving the clock on whenever the host's loop is waiting.</summary>
    private void KillLands(MuxConnectionHost? host, Guid id, string because) => PumpUntil(() =>
    {
        // Null once released (final review F1): with nothing left to deliver, the host is closed.
        if (host?.IsReconnecting == true) _clock.Advance(FirstRetry);
        _remote.Server.ReapExitedSessions(TimeSpan.Zero);
        return !_remote.Server.GetSessionIds().Contains(id);
    }, because);

    /// <summary>An SSH pane of the persisted profile, as the window saved it: reopening <paramref name="id"/> on the remote daemon.</summary>
    private PaneNode RemoteLeaf(Guid id) => new()
    {
        Type = NodeType.Leaf,
        SshProfileId = _sshProfile.Id.ToString(),
        MuxSessionId = id.ToString("D"),
        MuxEndpoint = Endpoint,
    };

    /// <summary>Shells already running on the remote daemon, started without an exec channel (another machine's client, say).</summary>
    /// <param name="remote">The host whose daemon runs them; <see cref="_remote"/> when null.</param>
    private Guid[] SpawnOnRemote(int count, FakeRemoteHost? remote = null) => Task.Run(async () =>
    {
        (Stream stream, _) = await (remote ?? _remote).ConnectDaemonAsync(TestContext.Current.CancellationToken);
        using MuxClient client = await MuxClient.ConnectAsync(stream, null, TestContext.Current.CancellationToken);
        var ids = new Guid[count];
        for (int i = 0; i < count; i++) ids[i] = await MuxTestHost.SpawnAsync(client);
        return ids;
    }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    private static IReadOnlyList<TerminalPane> AllPanes(MainWindow w) => w.AllPanesForTest();

    private List<TerminalPane> RemotePanes(MainWindow w) => AllPanes(w).Where(p => p.MuxEndpoint == Endpoint).ToList();

    private static void PumpUntil(Func<bool> condition, string because, int ms = 20_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Dispatcher.UIThread.RunJobs();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > ms) Assert.Fail($"Timed out: {because}");
            Thread.Sleep(10);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static string Text(TerminalPane pane) => MuxTestText.VisibleText(pane.Buffer!);

    /// <summary>
    /// The window's own persistent factory (not a test's) routes an SSH request by the SSH profile store, read
    /// at each spawn through the window's service (Task 19's note: the factory is built before that service).
    /// </summary>
    [AvaloniaFact]
    public void The_windows_factory_routes_by_the_ssh_profile_store()
    {
        MainWindow window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            CommandAssist = TestCommandAssistServices.Instance,
            SessionFactory = new RecordingSessionFactory(new FakeTerminalSession()),
        });
        window.MuxHostFactory = () => new MuxConnectionHost(ct => MuxClient.ConnectAsync(_localMux.Listener.Connect(), null, ct), "test", null);
        ((TerminalSettings)typeof(MainWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!)
            .SessionPersistence = SessionPersistenceMode.KeepOnClose;
        typeof(MainWindow).GetMethod("ApplySessionPersistenceSetting", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
        var factory = Assert.IsType<MuxTerminalSessionFactory>(AllPanes(window)[0].SessionFactory);
        var request = new TerminalSessionRequest("ssh", string.Empty, string.Empty, 80, 24, null, false, new SshSessionDescriptor(_sshProfile.Id, 0, null, false));

        Assert.True(factory.RoutesRemote(request));
        Assert.Equal("nova@fake-host", factory.RemoteHostDisplayName(request));

        _sshProfile.MuxOptions.PersistRemoteSessions = false;
        new JsonSshProfileStore().SaveProfile(_sshProfile);
        Assert.False(factory.RoutesRemote(request)); // read again at the next spawn, not captured once
    }

    /// <summary>
    /// Codex4 F, through the window's own wiring (its remote host factory and per-attempt transport, not a test's): a new
    /// remote tab on a native profile while native SSH is off in the window's settings starts no native session and says
    /// why. The host's one attempt was refused by the switch - its failure is the refusal, not a native connect's.
    /// </summary>
    [AvaloniaFact]
    public void A_new_native_remote_tab_is_refused_by_the_windows_own_transport_while_native_ssh_is_off()
    {
        MainWindow window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            CommandAssist = TestCommandAssistServices.Instance,
            SessionFactory = new RecordingSessionFactory(new FakeTerminalSession()),
        });
        window.MuxHostFactory = () => _local = new MuxConnectionHost(ct => MuxClient.ConnectAsync(_localMux.Listener.Connect(), null, ct), "test", null);
        var settings = (TerminalSettings)typeof(MainWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        settings.SessionPersistence = SessionPersistenceMode.KeepOnClose;
        settings.ExperimentalNativeSshEnabled = false;
        typeof(MainWindow).GetMethod("ApplySessionPersistenceSetting", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        typeof(MainWindow).GetMethod("AddTab", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(
            window,
            [new TerminalProfile { Id = _sshProfile.Id, Name = _sshProfile.Name, Type = ConnectionType.SSH }, Ntilde.Platform.Ssh.Launch.SshDiagnosticsLevel.None]);

        PumpUntil(() => RemotePanes(window).Any(p => Text(p).Contains(TerminalPane.RemoteUnreachableBanner("nova@fake-host"), StringComparison.Ordinal)), "the new tab gave up for now");
        TerminalPane pane = RemotePanes(window).Single();
        PumpUntil(() => Text(pane).Contains("[Native SSH is disabled globally.", StringComparison.Ordinal), "the pane says why");
        Assert.Null(pane.Session);
        Assert.Null(pane.MuxSessionIdToRestore);
        MuxConnectionHost host = RemoteHostOf(window)!;
        RemoteMuxFailure failure = Assert.IsType<RemoteMuxUnavailableException>(host.LastFailure).Failure;
        Assert.Equal(
            (RemoteFailureKind.NeedsUser, Ntilde.Platform.Ssh.Sessions.SshSessionFactory.NativeSshDisabledMessage),
            (failure.Kind, failure.Reason));
        Assert.Equal(1, host.ConnectAttempts);
    }

    /// <summary>Review Focus 4: several panes of one profile restoring at startup share one connection, and so one set of prompts.</summary>
    [AvaloniaFact]
    public void Restoring_three_panes_of_one_profile_starts_one_exec()
    {
        Guid[] ids = SpawnOnRemote(3);
        SaveSession(RemoteLeaf(ids[0]), RemoteLeaf(ids[1]), RemoteLeaf(ids[2]));
        // The first exec start is held until all three factory calls are under way, so the later two meet the first
        // one's connect in flight (an SSH prompt being answered, in real life) and must join it, not start their own.
        bool overlapped = false;
        _remote.OnStart = _ => overlapped |= SpinWait.SpinUntil(() => Volatile.Read(ref _factoryCallsOffUi) >= 3, Patient);

        MainWindow window = CreateWindow();

        PumpUntil(() => RemotePanes(window).Count(p => p.Session is MuxClientSession { IsAttached: true }) == 3, "all three panes reattached");
        Assert.True(overlapped, "the three factory calls overlapped the held connect");
        Assert.Equal(3, Volatile.Read(ref _factoryCallsOffUi));
        Assert.Equal(1, _remote.StartCount);
        Assert.Equal(ids.Order(), RemotePanes(window).Select(p => p.Session!.Id).Order());
        Assert.All(RemotePanes(window), p => Assert.Equal(MuxAttachMode.IfUnattached, ((MuxClientSession)p.Session!).AttachMode));
    }

    /// <summary>
    /// Spec R2 is about the local daemon. A remote daemon outlives a local reboot, so a remote session found gone after
    /// one is still news; the local pane beside it, whose session the reboot ended, starts its fresh shell quietly.
    /// </summary>
    [AvaloniaFact]
    public void After_a_reboot_only_the_remote_panes_lost_session_is_announced()
    {
        DateTime boot = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        Guid remoteGone = Guid.NewGuid(), localGone = Guid.NewGuid();
        var localLeaf = new PaneNode { Type = NodeType.Leaf, Command = "scripted", MuxSessionId = localGone.ToString("D"), MuxEndpoint = MuxEndpointId.Local.ToString() };
        SaveSession(RemoteLeaf(remoteGone), localLeaf);
        File.SetLastWriteTimeUtc(AppPaths.SessionFilePath, boot - TimeSpan.FromMinutes(5));
        var lost = new List<TerminalPane>();

        MainWindow window = CreateWindow(bootTimeUtc: boot, beforeShow: w =>
        {
            TerminalPane remote = Assert.Single(RemotePanes(w));
            TerminalPane local = Assert.Single(AllPanes(w), p => p != remote);
            Assert.False(remote.MuxQuietPreviousLost);
            Assert.True(local.MuxQuietPreviousLost);
            foreach (TerminalPane p in AllPanes(w))
                p.PersistenceNotice += (pane, title, _, _) => { if (title == TerminalPane.MuxPreviousLostNoticeTitle) lost.Add(pane); };
        });

        PumpUntil(() => AllPanes(window).Count(p => p.Session is MuxClientSession { IsAttached: true }) == 2, "both panes started fresh shells");
        List<Guid> fresh = AllPanes(window).Select(p => p.Session!.Id).ToList();
        Assert.DoesNotContain(remoteGone, fresh);
        Assert.DoesNotContain(localGone, fresh);
        // Each pane raises its notice in the callback that then asks for the session save: once the file names both, both have spoken.
        PumpUntil(() => File.ReadAllText(AppPaths.SessionFilePath) is var saved && fresh.All(id => saved.Contains(id.ToString("D"))), "the session file names both new sessions");
        Assert.Equal(RemotePanes(window), lost);
    }

    /// <summary>Review Focus 1: the user meant to end that shell; the kill waits for the link, it is not dropped.</summary>
    [AvaloniaFact]
    public void Closing_a_disconnected_remote_pane_queues_the_kill()
    {
        Guid[] ids = SpawnOnRemote(2);
        SaveSession(RemoteLeaf(ids[0]), RemoteLeaf(ids[1]));
        MainWindow window = CreateWindow();
        PumpUntil(() => RemotePanes(window).Count(p => p.Session is MuxClientSession { IsAttached: true }) == 2, "both panes reattached");
        TerminalPane closing = RemotePanes(window).Single(p => p.Session!.Id == ids[0]);
        TerminalPane staying = RemotePanes(window).Single(p => p.Session!.Id == ids[1]);
        var stayingSession = (MuxClientSession)staying.Session!;

        _remote.CutLink();
        PumpUntil(() => Text(closing).Contains(TerminalPane.RemoteReconnectingBanner("nova@fake-host"), StringComparison.Ordinal), "the link is down");
        var close = (Task<bool>)typeof(MainWindow).GetMethod("ClosePaneAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [closing, true])!;
        PumpUntil(() => close.IsCompleted, "the pane closed");
        Assert.True(close.Result);
        Assert.Contains(ids[0], _remote.Server.GetSessionIds()); // nothing could be sent yet

        _clock.Advance(FirstRetry); // the link is back: the queued kill goes first

        PumpUntil(() =>
        {
            _remote.Server.ReapExitedSessions(TimeSpan.Zero);
            return !_remote.Server.GetSessionIds().Contains(ids[0]);
        }, "the queued kill ended the closed pane's shell");
        PumpUntil(() => staying.Session is MuxClientSession { IsAttached: true } m && !ReferenceEquals(m, stayingSession), "the other pane reattached");
        Assert.Equal(ids[1], staying.Session!.Id);
        Assert.Contains(ids[1], _remote.Server.GetSessionIds());
    }

    /// <summary>
    /// Task 21 review, Review Focus 1: a link that died silently still looks connected until the liveness ping
    /// notices. A tab closed then must not lose its kill to that dead connection: the host sends it again once
    /// connected.
    /// </summary>
    [AvaloniaFact]
    public void Closing_a_remote_pane_on_a_silently_dead_link_still_kills_it()
    {
        Guid[] ids = SpawnOnRemote(2);
        SaveSession(RemoteLeaf(ids[0]), RemoteLeaf(ids[1]));
        MainWindow window = CreateWindow();
        PumpUntil(() => RemotePanes(window).Count(p => p.Session is MuxClientSession { IsAttached: true }) == 2, "both panes reattached");
        MuxConnectionHost host = RemoteHostOf(window)!;
        MuxClient client = host.CurrentClient!;
        TerminalPane closing = RemotePanes(window).Single(p => p.Session!.Id == ids[0]);

        _remote.StallLink(); // nothing gets through any more, and nothing says so
        PumpUntil(() => Environment.TickCount64 > client.LastReceivedTicks, "the clock moved past the last frame"); // the ping's slice starts after it
        Task<bool> close = Close(window, closing);
        PumpUntil(() => close.IsCompleted, "the pane closed");
        Assert.True(close.Result);
        Assert.True(client.IsConnected); // the close saw a connected session

        _clock.Advance(TimeSpan.FromSeconds(15)); // the liveness ping goes out ...
        _clock.Advance(TimeSpan.FromSeconds(10)); // ... and is not answered: the dead link is dropped

        KillLands(host, ids[0], "the kill reached the daemon over the new connection");
        Assert.Contains(ids[1], _remote.Server.GetSessionIds());
    }

    /// <summary>
    /// Task 21 review, Review Focus 1: a restored remote tab that was never shown has no connection - nothing of
    /// that profile has connected - yet closing it must still end its shell, which nobody could adopt.
    /// </summary>
    [AvaloniaFact]
    public void Closing_a_never_shown_restored_remote_tab_kills_its_shell()
    {
        Guid[] ids = SpawnOnRemote(2);
        SaveTabs(LocalLeaf(), RemoteLeaf(ids[0]));
        MainWindow window = CreateWindow();
        PumpUntil(() => RemotePanes(window).Count == 1, "the background tab was restored");
        TerminalPane unshown = RemotePanes(window).Single();
        Assert.Null(unshown.Session);
        Assert.Equal(ids[0], unshown.MuxSessionIdToRestore);
        Assert.Null(RemoteHostOf(window)); // no pane of this profile has asked for a connection
        Assert.Equal(0, _remote.StartCount);

        Task<bool> close = Close(window, unshown);
        PumpUntil(() => close.IsCompleted, "the tab closed");
        Assert.True(close.Result);

        KillLands(RemoteHostOf(window), ids[0], "the kill reached the daemon");
        Assert.Equal(1, _remote.StartCount); // the host connected for it, once
        Assert.Contains(ids[1], _remote.Server.GetSessionIds());
    }

    /// <summary>
    /// Codex C2: the profile's flag decides whether tabs go to the remote daemon, not whether a shell the user closed
    /// ends. Two panes restored with pending ids on a profile whose flag is now off run plain SSH. Closing one still
    /// builds a host, which delivers the kill in one automatic - non-interactive - attempt and is then released: the
    /// other plain SSH pane keeps its id pending, but needs no connection until its own close. Before, the host
    /// creator declined the profile and the kill was dropped without a word; nothing adopts a remote orphan.
    /// </summary>
    [AvaloniaFact]
    public void Closing_a_pane_whose_profile_stopped_persisting_still_kills_its_pending_remote_shell()
    {
        Guid[] ids = SpawnOnRemote(2);
        _sshProfile.MuxOptions.PersistRemoteSessions = false;
        new JsonSshProfileStore().SaveProfile(_sshProfile);
        SaveSession(RemoteLeaf(ids[0]), RemoteLeaf(ids[1]));
        MainWindow window = CreateWindow();
        PumpUntil(() => RemotePanes(window).Count(p => p.Session is FakeTerminalSession) == 2, "both panes opened plain SSH");
        TerminalPane closing = RemotePanes(window).Single(p => p.MuxSessionIdToRestore == ids[0]);
        TerminalPane staying = RemotePanes(window).Single(p => p.MuxSessionIdToRestore == ids[1]);
        Assert.Null(RemoteHostOf(window));
        Assert.Equal(0, _remote.StartCount);

        Task<bool> close = Close(window, closing);
        Assert.True(close.IsCompletedSuccessfully && close.Result, "the split's pane closed at once");
        MuxConnectionHost? host = RemoteHostOf(window);
        Assert.NotNull(host); // built for the kill, whatever the flag says

        KillLands(host, ids[0], "the kill reached the daemon");
        PumpUntil(() => host.IsClosed && RemoteHostOf(window) is null, "the host built for the kill was released after it");
        Assert.Equal(1, _remote.StartCount);
        Assert.False(Assert.Single(_transportRequests).Interactive); // no prompt can come from it
        Assert.Contains(ids[1], _remote.Server.GetSessionIds());
        Assert.Equal(ids[1], staying.MuxSessionIdToRestore);
        Assert.IsType<FakeTerminalSession>(staying.Session);
    }

    /// <summary>
    /// Codex C2: a pane whose profile was deleted has no host to send its kill through. Nothing is built, and the kill
    /// that cannot be sent is logged rather than dropped without a word.
    /// </summary>
    [AvaloniaFact]
    public void Closing_a_pane_whose_profile_is_gone_logs_the_kill_it_cannot_send()
    {
        Guid[] ids = SpawnOnRemote(1);
        SaveTabs(LocalLeaf(), RemoteLeaf(ids[0]));
        MainWindow window = CreateWindow();
        PumpUntil(() => RemotePanes(window).Count == 1, "the background tab was restored");
        TerminalPane unshown = RemotePanes(window).Single();
        _profileGone = true;

        string line = CannotEndLine(CloseLogging(window, unshown), ids[0]);

        Assert.Null(RemoteHostOf(window));
        Assert.Equal(0, _remote.StartCount);
        Assert.Contains(ids[0], _remote.Server.GetSessionIds());
        // Codex residual round: the line names the reason that holds, not every reason there could be.
        Assert.Contains("its SSH profile is gone", line, StringComparison.Ordinal);
        Assert.DoesNotContain("window is closing", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// Codex residual round: a window whose session persistence was never on has no connection registry at all, and
    /// its log line says so - not that the profile is gone or the window closing, which it was not.
    /// </summary>
    [AvaloniaFact]
    public void Closing_a_pending_remote_pane_without_persistence_says_why_its_kill_cannot_be_sent()
    {
        Guid[] ids = SpawnOnRemote(1);
        SaveTabs(LocalLeaf(), RemoteLeaf(ids[0]));
        MainWindow window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            CommandAssist = TestCommandAssistServices.Instance,
            SessionFactory = new RecordingSessionFactory(new FakeTerminalSession()), // persistence off: no mux factory
        });
        window.Show();
        PumpUntil(() => RemotePanes(window).Count == 1, "the background tab was restored");
        TerminalPane unshown = RemotePanes(window).Single();
        Assert.Equal(ids[0], unshown.MuxSessionIdToRestore);
        Assert.Null(window.MuxHosts);

        string line = CannotEndLine(CloseLogging(window, unshown), ids[0]);

        Assert.Contains("session persistence", line, StringComparison.Ordinal);
        Assert.DoesNotContain("profile is gone", line, StringComparison.Ordinal);
        Assert.Contains(ids[0], _remote.Server.GetSessionIds());
    }

    /// <summary>Closes <paramref name="pane"/>, returning what was logged meanwhile.</summary>
    private static List<string> CloseLogging(MainWindow window, TerminalPane pane)
    {
        var logged = new System.Collections.Concurrent.ConcurrentQueue<string>();
        Action<string>? previous = Ntilde.VT.TerminalLogger.OnLog;
        Ntilde.VT.TerminalLogger.OnLog = line =>
        {
            logged.Enqueue(line);
            previous?.Invoke(line);
        };
        try
        {
            Task<bool> close = Close(window, pane);
            PumpUntil(() => close.IsCompleted, "the tab closed");
            Assert.True(close.Result);
        }
        finally
        {
            Ntilde.VT.TerminalLogger.OnLog = previous;
        }

        return [.. logged];
    }

    /// <summary>The one line saying the kill of <paramref name="id"/> could not be sent.</summary>
    private static string CannotEndLine(List<string> logged, Guid id) =>
        Assert.Single(logged, line => line.Contains(id.ToString(), StringComparison.Ordinal) && line.Contains("kill", StringComparison.Ordinal));

    /// <summary>
    /// Final review F1: closing the last pane of a remote endpoint releases its connection once the kill is
    /// delivered. Before, the host stayed for the window's life: hidden ssh, remote proxy and daemon alive, a ping
    /// every 15 s, and a reconnect after sleep to a host the user had closed.
    /// </summary>
    [AvaloniaFact]
    public void Closing_the_last_remote_pane_releases_its_connection()
    {
        Guid[] ids = SpawnOnRemote(1);
        SaveSession(RemoteLeaf(ids[0]), LocalLeaf());   // the local pane keeps the window open
        MainWindow window = CreateWindow();
        PumpUntil(() => RemotePanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true }), "the remote pane reattached");
        MuxConnectionHost host = RemoteHostOf(window)!;
        FakeRemoteChannel channel = _remote.LastChannel!;

        Task<bool> close = Close(window, RemotePanes(window).Single());
        PumpUntil(() => close.IsCompleted, "the pane closed");
        Assert.True(close.Result);

        PumpUntil(() => host.IsClosed && RemoteHostOf(window) is null, "the connection was released");
        Assert.DoesNotContain(ids[0], _remote.Server.GetSessionIds());   // its kill went out first
        PumpUntil(() => channel.IsDisposed, "the channel ended");
        Assert.Equal(0, _clock.PendingCount);
        _clock.Advance(TimeSpan.FromMinutes(11));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, _remote.StartCount);
    }

    /// <summary>Final review F1: a host stays while another pane of its endpoint still has its session there.</summary>
    [AvaloniaFact]
    public void Closing_one_of_two_remote_panes_keeps_the_connection()
    {
        Guid[] ids = SpawnOnRemote(2);
        SaveSession(RemoteLeaf(ids[0]), RemoteLeaf(ids[1]));
        MainWindow window = CreateWindow();
        PumpUntil(() => RemotePanes(window).Count(p => p.Session is MuxClientSession { IsAttached: true }) == 2, "both panes reattached");
        MuxConnectionHost host = RemoteHostOf(window)!;

        Task<bool> close = Close(window, RemotePanes(window).Single(p => p.Session!.Id == ids[0]));
        PumpUntil(() => close.IsCompleted, "the pane closed");
        KillLands(host, ids[0], "the closed pane's shell ended");
        PumpFor(300);

        Assert.False(host.IsClosed);
        Assert.Same(host, RemoteHostOf(window));
        Assert.Equal(1, _clock.PendingCount);   // still watching the link for the other pane
        Assert.Contains(ids[1], _remote.Server.GetSessionIds());
    }

    /// <summary>
    /// Final review F1: a pane that keeps a session id pending on the endpoint - here a restored tab never shown -
    /// keeps its host, though no pane of that endpoint has a live session.
    /// </summary>
    [AvaloniaFact]
    public void A_pending_restored_remote_tab_keeps_the_connection()
    {
        Guid[] ids = SpawnOnRemote(2);
        SaveTabs(
            new PaneNode { Type = NodeType.Split, SplitOrientation = 0, Children = [RemoteLeaf(ids[0]), LocalLeaf()] },
            RemoteLeaf(ids[1]));   // the second tab restores in the background, unshown
        MainWindow window = CreateWindow();
        PumpUntil(() => RemotePanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true }), "the shown remote pane reattached");
        TerminalPane unshown = RemotePanes(window).Single(p => p.Session is null);
        Assert.Equal(ids[1], unshown.MuxSessionIdToRestore);
        MuxConnectionHost host = RemoteHostOf(window)!;

        Task<bool> close = Close(window, RemotePanes(window).Single(p => p.Session?.Id == ids[0]));
        PumpUntil(() => close.IsCompleted, "the pane closed");
        KillLands(host, ids[0], "the closed pane's shell ended");
        PumpFor(300);

        Assert.False(host.IsClosed);
        Assert.Same(host, RemoteHostOf(window));
        Assert.Contains(ids[1], _remote.Server.GetSessionIds());
    }

    /// <summary>A leaf of the persisted SSH profile with no session yet: shown, it starts a fresh remote shell.</summary>
    private PaneNode NewRemoteLeaf() => new() { Type = NodeType.Leaf, SshProfileId = _sshProfile.Id.ToString() };

    /// <summary>
    /// A window whose second tab - restored in the background - splits a new remote pane with a local one. The remote
    /// pane's factory call is held: shown, the tab runs it to the end (the shell is spawned on the remote daemon), but
    /// its result reaches the pane only when the test completes <c>Deliver</c>, on the UI thread, where the pane's
    /// continuation then posts it at once. The split lets a close finish synchronously, before that.
    /// </summary>
    private (MainWindow Window, TerminalPane Pane, PersistentSessionResult Result, TaskCompletionSource<PersistentSessionResult> Deliver) WindowWithAHeldRemoteSpawn()
    {
        SaveTabs(LocalLeaf(), new PaneNode { Type = NodeType.Split, SplitOrientation = 0, Children = [NewRemoteLeaf(), LocalLeaf()] });
        MainWindow window = CreateWindow();
        PumpUntil(() => AllPanes(window).Any(p => p.Profile?.Type == ConnectionType.SSH), "the background tab was restored");
        TerminalPane pane = AllPanes(window).Single(p => p.Profile?.Type == ConnectionType.SSH);
        Assert.Null(pane.Session);

        var spawned = new TaskCompletionSource<PersistentSessionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliver = new TaskCompletionSource<PersistentSessionResult>(); // inline continuations: SetResult posts the result
        pane.RunOffUiThread = create =>
        {
            _ = Task.Run(() =>
            {
                try { spawned.TrySetResult(create()); }
                catch (Exception ex) { spawned.TrySetException(ex); }
            });
            return deliver.Task;
        };
        window.FindControl<TabControl>("Tabs")!.SelectedItem = pane.FindLogicalAncestorOfType<TabItem>(); // a background tab spawns when shown
        PumpUntil(() => spawned.Task.IsCompleted, "the remote factory call finished");
        PersistentSessionResult result = spawned.Task.Result;
        Assert.Equal(PersistentSessionOutcome.Spawned, result.Outcome);
        Assert.Contains(Assert.IsType<MuxClientSession>(result.Session).Id, _remote.Server.GetSessionIds());
        return (window, pane, result, deliver);
    }

    /// <summary>
    /// Codex C1: a remote result that comes back to a closed pane started a shell nobody will ever show. Its kill goes
    /// through the endpoint's host, as a closed pane's does: with the link down it is queued, the close's release pass
    /// keeps the host for it, and it is delivered when the link is back - then the connection goes. Before, the
    /// result's session sent a fire-and-forget kill into its dead connection and the release closed the host: the
    /// shell ran on with no pane able to reach it.
    /// </summary>
    [AvaloniaFact]
    public void A_stale_results_shell_is_killed_through_its_host_once_the_link_is_back()
    {
        (MainWindow window, TerminalPane pane, PersistentSessionResult result, TaskCompletionSource<PersistentSessionResult> deliver) = WindowWithAHeldRemoteSpawn();
        Guid stale = result.Session!.Id;
        MuxConnectionHost host = RemoteHostOf(window)!;
        _remote.CutLink();
        PumpUntil(() => host.IsReconnecting, "the host noticed the link is down");

        Task<bool> close = Close(window, pane);
        Assert.True(close.IsCompletedSuccessfully && close.Result, "the split's pane closed at once");
        deliver.SetResult(result); // the result comes back to a closed pane, ahead of the close's release pass
        PumpFor(300);

        Assert.False(host.IsClosed, "the connection was released with the stale shell's kill still queued");
        Assert.Same(host, RemoteHostOf(window));
        Assert.Contains(stale, _remote.Server.GetSessionIds());

        _clock.Advance(FirstRetry); // the link is back: the queued kill goes first
        KillLands(host, stale, "the stale result's shell was killed once the link was back");
        PumpUntil(() => host.IsClosed && RemoteHostOf(window) is null, "the connection was released after the kill");
    }

    /// <summary>
    /// Codex C1: the close's release pass may run before the closed pane's result is back, and close the host. The kill
    /// of the shell that result started then goes through a host built for it - one automatic attempt - and that
    /// host is released in its turn once the kill is delivered.
    /// </summary>
    [AvaloniaFact]
    public void A_stale_results_shell_is_killed_after_its_connection_was_released()
    {
        (MainWindow window, TerminalPane pane, PersistentSessionResult result, TaskCompletionSource<PersistentSessionResult> deliver) = WindowWithAHeldRemoteSpawn();
        Guid stale = result.Session!.Id;
        MuxConnectionHost first = RemoteHostOf(window)!;
        FakeRemoteChannel channel = _remote.LastChannel!;

        Task<bool> close = Close(window, pane);
        Assert.True(close.IsCompletedSuccessfully && close.Result, "the split's pane closed at once");
        PumpUntil(() => first.IsClosed && RemoteHostOf(window) is null, "the close released the connection: no pane needs it");
        PumpUntil(() => channel.IsDisposed, "that connection ended");
        Assert.Contains(stale, _remote.Server.GetSessionIds());

        deliver.SetResult(result); // only now does the result come back
        KillLands(RemoteHostOf(window), stale, "the stale result's shell was killed");

        PumpUntil(() => RemoteHostOf(window) is null, "the host built for the kill was released after it");
        Assert.Equal(2, _remote.StartCount); // the pane's connect, then one for the kill
    }

    /// <summary>
    /// Codex E1 residual: a new remote pane closed while its spawn is out on a silent link. Its close's release pass
    /// closes the host it finds unused, which fails the spawn; the daemon still starts the shell, under the id the
    /// factory chose. A closed host drops a kill, so that shell's kill goes through a host built for it, and the
    /// discarded plain-SSH result asks for the release pass that lets that host go once the kill is delivered.
    /// </summary>
    [AvaloniaFact]
    public void A_pane_closed_mid_spawn_on_a_silent_link_still_ends_the_shell_the_daemon_starts()
    {
        SaveTabs(LocalLeaf(), new PaneNode { Type = NodeType.Split, SplitOrientation = 0, Children = [NewRemoteLeaf(), LocalLeaf()] });
        MainWindow window = CreateWindow();
        PumpUntil(() => AllPanes(window).Any(p => p.Profile?.Type == ConnectionType.SSH), "the background tab was restored");
        TerminalPane pane = AllPanes(window).Single(p => p.Profile?.Type == ConnectionType.SSH);
        var gate = new ManualResetEventSlim();
        var linkBack = new ManualResetEventSlim();
        _remote.Shells.CreateGate = gate;
        window.FindControl<TabControl>("Tabs")!.SelectedItem = pane.FindLogicalAncestorOfType<TabItem>(); // shown, it spawns
        PumpUntil(() => _remote.Shells.CreateEntered.IsSet, "the daemon is starting the pane's shell");
        MuxConnectionHost first = RemoteHostOf(window)!;
        _remote.StallLink();                       // the spawn's reply will not come back, and nothing says the link is dead
        _remote.OnStart = ct => linkBack.Wait(ct); // and no new link until the test says so

        Task<bool> close = Close(window, pane);
        Assert.True(close.IsCompletedSuccessfully && close.Result, "the split's pane closed at once");
        PumpUntil(() => first.IsClosed, "the close released the connection: no pane needs it");
        gate.Set();
        PumpUntil(() => _remote.Server.GetSessionIds().Count == 1, "the daemon started the shell all the same");
        Guid orphan = _remote.Server.GetSessionIds().Single();
        PumpUntil(() => RemoteHostOf(window) is { } next && !ReferenceEquals(next, first), "a host was built to send the orphan's kill");

        linkBack.Set();
        KillLands(RemoteHostOf(window), orphan, "the orphan's kill was delivered through the new connection");
        PumpUntil(() => RemoteHostOf(window) is null, "the host built for the kill was released after it");
        Assert.Equal(2, _remote.StartCount); // the pane's connect, then one for the kill
    }

    private static void PumpFor(int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// Phase 5 spec §5: "Attach to session…" lists remote hosts now - a host the detach let go of through its "Connect
    /// to …" row - so a detached remote shell is told to come back the way a local one is.
    /// (The local text is pinned by MainWindowMuxSharingTests.Detach_pane_keeps_the_shell_running_and_the_count_drops.)
    /// </summary>
    [AvaloniaFact]
    public void Detaching_a_remote_pane_says_attach_to_session_reopens_it()
    {
        Guid[] ids = SpawnOnRemote(1);
        SaveSession(RemoteLeaf(ids[0]), LocalLeaf());
        MainWindow window = CreateWindow();
        PumpUntil(() => RemotePanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true }), "the remote pane reattached");
        TerminalPane pane = RemotePanes(window).Single();

        Task<bool> detach = window.DetachPaneAsync(pane);
        PumpUntil(() => detach.IsCompleted, "the detach finished");

        Assert.True(detach.Result);
        PumpUntil(() => Toast(window).Title == "Shell detached", "the detach toast is shown");
        Assert.Equal("Shell kept running on nova@fake-host \u2014 Attach to session\u2026 reopens it", Toast(window).Message);
        Assert.Contains(ids[0], _remote.Server.GetSessionIds());
    }

    /// <summary>
    /// Fix round 1: a profile that stopped keeping its sessions is not offered by the picker, so its detached shell is told
    /// to come back through ntilde-mux on its own host, by the id ntilde-mux ls shows - read at the detach.
    /// </summary>
    [AvaloniaFact]
    public void Detaching_a_remote_pane_whose_profile_stopped_keeping_sessions_gives_the_ntilde_mux_command()
    {
        Guid[] ids = SpawnOnRemote(1);
        SaveSession(RemoteLeaf(ids[0]), LocalLeaf());
        MainWindow window = CreateWindow();
        PumpUntil(() => RemotePanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true }), "the remote pane reattached");
        TerminalPane pane = RemotePanes(window).Single();
        _sshProfile.MuxOptions.PersistRemoteSessions = false;
        new JsonSshProfileStore().SaveProfile(_sshProfile);

        Task<bool> detach = window.DetachPaneAsync(pane);
        PumpUntil(() => detach.IsCompleted, "the detach finished");

        Assert.True(detach.Result);
        PumpUntil(() => Toast(window).Title == "Shell detached", "the detach toast is shown");
        Assert.Equal($"Shell kept running on nova@fake-host \u2014 run 'ntilde-mux attach {ids[0]}' on that host to get it back", Toast(window).Message);
        Assert.Contains(ids[0], _remote.Server.GetSessionIds());
    }

    /// <summary>The window's restored remote pane once its session is attached and registered (Phase 5 spec R8: registration follows the attach).</summary>
    private TerminalPane AttachedRemotePane(MainWindow window)
    {
        PumpUntil(() => RemotePanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true }), "the remote pane reattached");
        TerminalPane pane = RemotePanes(window).Single();
        Assert.True(pane.IsPersistentRemoteTab);
        return pane;
    }

    private static void WaitUntilRegistered(Guid id) => PumpUntil(() => ActiveSshSessionRegistry.Instance.TryGet(id, out _), $"session {id} is registered");

    private static Task Palette(MainWindow window, TerminalPane pane, TransferDirection direction, TransferKind kind) =>
        (Task)typeof(MainWindow).GetMethod("InitiateSftpTransfer", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [pane, direction, kind])!;

    private static void SidebarTransfer(MainWindow window, TerminalPane pane, string remotePath) =>
        typeof(MainWindow).GetMethod("OnPaneRequestRemoteFilesSidebarTransfer", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, [pane, new SidebarTransferRequest(TransferDirection.Download, TransferKind.File, remotePath)]);

    private static void HideToast(MainWindow window) =>
        typeof(MainWindow).GetMethod("HideRecordingToast", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);

    /// <summary>
    /// Phase 5 spec R8: a persisted tab of a Native profile registers as its profile's native session with its host's
    /// password scope, so its Remote Files sidebar opens - no notice - and lists over a connection of its own, as a plain
    /// native tab's does, which carries the password the user typed to sign in to that host (and not the vault's: none is saved).
    /// </summary>
    [AvaloniaFact]
    public void A_native_persistent_tabs_remote_files_list_over_their_own_connection_with_the_password_typed_for_the_host()
    {
        _typedPassword = "typed-for-fake-host";
        Guid[] ids = SpawnOnRemote(1);
        SaveSession(RemoteLeaf(ids[0]));
        MainWindow window = CreateWindow();
        TerminalPane pane = AttachedRemotePane(window);
        WaitUntilRegistered(ids[0]);
        Assert.True(ActiveSshSessionRegistry.Instance.TryGetActiveNativeSession(_sshProfile.Id, ids[0], out ActiveSshSessionDescriptor? descriptor));
        Assert.Equal(RemoteHostOf(window)!.PasswordScopeId, descriptor!.PasswordScopeId);
        var native = new ConnectionRecordingNativeInterop();
        pane.ConfigureRemoteFilesSidebarForTest(new RemoteDirectoryBrowserService(native, ActiveSshSessionRegistry.Instance, () => new SshConnectionService(), passwordResolver: _ => null));
        var notices = new List<string>();
        pane.PersistenceNotice += (_, title, message, _) => notices.Add($"{title}: {message}");

        pane.ToggleRemoteFilesSidebar();

        PumpUntil(() => pane.IsRemoteFilesSidebarVisibleForTest() && native.Listed is not null, "the sidebar opened and listed");
        Ntilde.Platform.Ssh.Native.NativeSshConnectionOptions listed = native.Listed!;
        Assert.Equal(("fake-host", "nova", "typed-for-fake-host"), (listed.Host, listed.User, listed.Password));
        Assert.Empty(notices);
    }

    /// <summary>
    /// Phase 5 spec R8: the palette's transfers and the sidebar's on a native persisted tab name its daemon session, as a
    /// plain tab's name its SSH session: the transfer then opens its own connection with the password typed for the host.
    /// Before (final review I3), both were refused with a notice.
    /// </summary>
    [AvaloniaFact]
    public void A_native_persistent_tabs_transfers_run_over_their_own_connection_with_the_password_typed_for_the_host()
    {
        _typedPassword = "typed-for-fake-host";
        Guid[] ids = SpawnOnRemote(1);
        SaveSession(RemoteLeaf(ids[0]));
        var window = (TransferWindow)CreateWindow(build: services => new TransferWindow(services));
        TerminalPane pane = AttachedRemotePane(window);
        WaitUntilRegistered(ids[0]);

        Task palette = Palette(window, pane, TransferDirection.Upload, TransferKind.File);
        PumpUntil(() => palette.IsCompleted, "the palette's transfer was queued");
        SidebarTransfer(window, pane, "/etc/hosts");
        PumpUntil(() => window.Queued.Count == 2, "the sidebar's transfer was queued");

        TransferDialogRequest dialog = Assert.Single(window.Dialogs);
        Assert.Equal((_sshProfile.Id, ids[0]), (dialog.ProfileId, dialog.SessionId)); // its remote-path completion lists through the session
        Assert.Equal(2, window.Queued.Count);
        Assert.All(window.Queued, job => Assert.Equal((_sshProfile.Id, ids[0]), (job.ProfileId, job.SessionId)));
        Assert.Equal("/etc/hosts", window.Queued[1].RemotePath);
        var native = new ConnectionRecordingNativeInterop();
        SftpService.ExecuteNativeSftpTransfer(
            window.Queued[0],
            pane.Profile!,
            new SshConnectionService(),
            interop: native,
            passwordResolver: _ => null,
            sessionRegistry: ActiveSshSessionRegistry.Instance);
        Assert.Equal(("fake-host", "typed-for-fake-host"), (native.Transferred!.Host, native.Transferred.Password));
    }

    /// <summary>
    /// Spec §15 (codex D2): SFTP rebuilds its options from the stored profile. While the profile names another destination
    /// than the one this tab's host connected to, its files would be the other host's: the sidebar, its transfers and the
    /// palette's are refused with why, and nothing else happens. The profile is read at each request: back as it was, the
    /// tab is no longer refused.
    /// </summary>
    [AvaloniaFact]
    public void A_persistent_tab_whose_profile_was_retargeted_refuses_remote_files_and_transfers_and_says_why()
    {
        Guid[] ids = SpawnOnRemote(1);
        SaveSession(RemoteLeaf(ids[0]));
        var window = (TransferWindow)CreateWindow(build: services => new TransferWindow(services));
        TerminalPane pane = AttachedRemotePane(window);
        WaitUntilRegistered(ids[0]);
        Assert.False(pane.IsOnRetargetedHost);

        _sshProfile.Host = "moved-host"; // edited after the host connected: its shells still run on fake-host
        pane.ToggleRemoteFilesSidebar();

        PumpUntil(() => ToastLines(window).Contains(TerminalPane.RemoteFilesRetargetedMessage), "the toast says why");
        Assert.Equal(TerminalPane.RemoteFilesUnavailableNoticeTitle, Toast(window).Title);
        Assert.False(pane.IsRemoteFilesSidebarVisibleForTest());

        HideToast(window);
        SidebarTransfer(window, pane, "/etc/hosts");
        PumpUntil(() => ToastLines(window).Contains(TerminalPane.RemoteFilesRetargetedMessage), "the sidebar's transfer is refused");

        HideToast(window);
        Task palette = Palette(window, pane, TransferDirection.Download, TransferKind.Folder);
        PumpUntil(() => palette.IsCompleted && ToastLines(window).Contains(TerminalPane.RemoteFilesRetargetedMessage), "the palette's transfer is refused");
        Assert.Empty(window.Dialogs);
        Assert.Empty(window.Queued);

        _sshProfile.Host = "fake-host";
        Assert.False(pane.IsOnRetargetedHost);
    }

    /// <summary>
    /// Fix round 1: a sidebar opened before the profile was edited lists nothing more - not the first listing only is
    /// refused, but every one (a refresh, a navigation): it closes, and the toast says why.
    /// </summary>
    [AvaloniaFact]
    public void An_open_sidebar_lists_nothing_more_once_the_profile_is_retargeted_and_closes_saying_why()
    {
        Guid[] ids = SpawnOnRemote(1);
        SaveSession(RemoteLeaf(ids[0]));
        MainWindow window = CreateWindow();
        TerminalPane pane = AttachedRemotePane(window);
        WaitUntilRegistered(ids[0]);
        var native = new ConnectionRecordingNativeInterop();
        pane.ConfigureRemoteFilesSidebarForTest(new RemoteDirectoryBrowserService(native, ActiveSshSessionRegistry.Instance, () => new SshConnectionService(), passwordResolver: _ => null));
        pane.ToggleRemoteFilesSidebar();
        PumpUntil(() => pane.IsRemoteFilesSidebarVisibleForTest() && native.Listings == 1, "the sidebar opened and listed");

        _sshProfile.Host = "moved-host";
        Task refresh = pane.RefreshRemoteFilesSidebarForTest();

        PumpUntil(() => refresh.IsCompleted && ToastLines(window).Contains(TerminalPane.RemoteFilesRetargetedMessage) && !pane.IsRemoteFilesSidebarVisibleForTest(),
            "the refresh was refused, the toast says why and the sidebar closed");
        Assert.Equal(1, native.Listings);
    }

    /// <summary>
    /// Fix round 1: a transfer is checked again when its dialog (or the sidebar's file picker) is answered. A profile edited
    /// while it was open would otherwise send the job - which reads the stored profile when it starts, at once - to the new
    /// destination.
    /// </summary>
    [AvaloniaFact]
    public void A_transfer_whose_profile_was_retargeted_while_its_dialog_was_open_is_not_started()
    {
        Guid[] ids = SpawnOnRemote(1);
        SaveSession(RemoteLeaf(ids[0]));
        var window = (TransferWindow)CreateWindow(build: services => new TransferWindow(services));
        TerminalPane pane = AttachedRemotePane(window);
        WaitUntilRegistered(ids[0]);
        window.WhileTheDialogIsOpen = () => _sshProfile.Host = "moved-host";

        Task palette = Palette(window, pane, TransferDirection.Upload, TransferKind.File);

        PumpUntil(() => palette.IsCompleted && ToastLines(window).Contains(TerminalPane.RemoteFilesRetargetedMessage), "the confirmed transfer was refused");
        Assert.Single(window.Dialogs);
        Assert.Empty(window.Queued);

        _sshProfile.Host = "fake-host"; // back, and moved again while the sidebar's file picker is open
        HideToast(window);
        SidebarTransfer(window, pane, "/etc/hosts");

        PumpUntil(() => ToastLines(window).Contains(TerminalPane.RemoteFilesRetargetedMessage), "the picked transfer was refused");
        Assert.Equal(1, window.Pickers);
        Assert.Empty(window.Queued);
    }

    /// <summary>
    /// Phase 5 spec R8: an OpenSSH persisted tab gets the palette's transfers - scp in batch mode on a connection of its own,
    /// as on a plain OpenSSH tab - and, as there, no Remote Files sidebar.
    /// </summary>
    [AvaloniaFact]
    public void An_OpenSSH_persistent_tabs_palette_transfer_runs_scp_on_its_own_connection_and_it_has_no_sidebar()
    {
        _sshProfile.BackendKind = SshBackendKind.OpenSsh;
        new JsonSshProfileStore().SaveProfile(_sshProfile);
        Guid[] ids = SpawnOnRemote(1);
        SaveSession(RemoteLeaf(ids[0]));
        var window = (TransferWindow)CreateWindow(build: services => new TransferWindow(services));
        TerminalPane pane = AttachedRemotePane(window);
        Assert.Equal(SshBackendKind.OpenSsh, pane.Profile!.SshBackendKind);

        Task palette = Palette(window, pane, TransferDirection.Upload, TransferKind.File);
        PumpUntil(() => palette.IsCompleted, "the palette's transfer was queued");

        TransferJob job = Assert.Single(window.Queued);
        Assert.Equal((_sshProfile.Id, TransferDirection.Upload, "/srv/a.txt"), (job.ProfileId, job.Direction, job.RemotePath));
        Assert.Equal(SftpTransferBackend.ExternalScp, SftpService.SelectExecutionBackend(pane.Profile!, job));
        Assert.Contains(" -B ", SftpService.BuildScpArguments(job, pane.Profile!, launchDetails: null), StringComparison.Ordinal);

        pane.ToggleRemoteFilesSidebar();
        Dispatcher.UIThread.RunJobs();
        Assert.False(pane.IsRemoteFilesSidebarVisibleForTest());
    }

    /// <summary>
    /// Phase 4 spec §5: one id on two daemons is two sessions. A remote pane that names (here: keeps pending)
    /// the id of a local orphan does not stop the window from adopting that orphan.
    /// </summary>
    [AvaloniaFact]
    public void A_local_orphan_is_adopted_although_a_remote_pane_names_the_same_id()
    {
        Guid orphan;
        using (MuxClient seed = Task.Run(() => MuxClient.ConnectAsync(_localMux.Listener.Connect(), null, default), TestContext.Current.CancellationToken).GetAwaiter().GetResult())
            orphan = Task.Run(() => MuxTestHost.SpawnAsync(seed), TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        PumpUntil(() => _localMux.Mux(orphan).AttachedClients == 0, "the spawning client is gone");
        _remote.Script = FakeRemoteScript.ConnectionRefused; // the remote pane stays pending on its id
        SaveSession(RemoteLeaf(orphan));

        MainWindow window = CreateWindow();

        PumpUntil(() => RemotePanes(window).Any(p => Text(p).Contains(TerminalPane.RemoteUnreachableBanner("nova@fake-host"), StringComparison.Ordinal)), "the remote pane gave up for now");
        PumpUntil(() => AllPanes(window).Any(p => p.MuxAdoptedOrphan && p.MuxSessionIdToRestore == orphan), "the local orphan was adopted");
        TerminalPane adopted = AllPanes(window).Single(p => p.MuxAdoptedOrphan);
        Assert.True(MuxEndpointId.Parse(adopted.MuxEndpoint).IsLocal);
        Assert.Equal(orphan, Assert.Single(RemotePanes(window)).MuxSessionIdToRestore); // the remote pane still keeps its own
    }

    /// <summary>
    /// The attach picker lists the local daemon's sessions: a remote pane naming the same id neither marks a
    /// row "open here" nor is focused in place of opening it.
    /// </summary>
    [AvaloniaFact]
    public void The_attach_picker_ignores_a_remote_pane_naming_a_local_sessions_id()
    {
        // A local session another instance shows: not an orphan, so only the picker can open it here.
        (MuxClient other, ClientPaneModel theirs) = Task.Run(async () =>
        {
            MuxClient c = await _localMux.ConnectClientAsync();
            Guid id = await MuxTestHost.SpawnAsync(c);
            return (c, await MuxTestHost.AttachPaneAsync(c, id));
        }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        _ = other;
        Guid shared = theirs.Session.Id;
        _remote.Script = FakeRemoteScript.ConnectionRefused;
        SaveSession(RemoteLeaf(shared));
        MainWindow window = CreateWindow();
        PumpUntil(() => RemotePanes(window).Any(p => Text(p).Contains(TerminalPane.RemoteUnreachableBanner("nova@fake-host"), StringComparison.Ordinal)), "the remote pane gave up for now");
        Assert.Equal(shared, RemotePanes(window).Single().MuxSessionIdToRestore);

        IReadOnlyList<MuxPickerItem> offered = Assert.Single(Attach(window, (items, _) => MuxPickerChoice.Find(items, shared)));

        Assert.False(Assert.Single(offered.OfType<MuxSessionPickerRow>(), r => r.SessionId == shared).OpenHere);
        PumpUntil(() => AllPanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true } m && m.Id == shared && MuxEndpointId.Parse(p.MuxEndpoint).IsLocal),
            "the local session opened in a new tab");
    }

    /// <summary>
    /// Runs "Attach to session…" to the end, <paramref name="pick"/> answering each picker it opens - given what that one
    /// offers and its number, from 1 - and returns what each offered.
    /// </summary>
    private static List<IReadOnlyList<MuxPickerItem>> Attach(MainWindow window, Func<IReadOnlyList<MuxPickerItem>, int, MuxPickerItem?> pick)
    {
        var offered = new List<IReadOnlyList<MuxPickerItem>>();
        window.PickMuxSession = items =>
        {
            offered.Add(items);
            return Task.FromResult(pick(items, offered.Count));
        };
        Task command = window.AttachToMuxSessionAsync();
        PumpUntil(() => command.IsCompleted, "the attach command finished");
        return offered;
    }

    /// <summary>
    /// A second profile that keeps its sessions, on its own host (<c>nova@twin-host</c>), set before <see cref="CreateWindow"/>.
    /// Its host is disposed with the test, and its profile deleted.
    /// </summary>
    private (SshProfile Profile, FakeRemoteHost Host, MuxEndpointId Endpoint) AddTwin()
    {
        SshProfile twin = RemoteMuxConnectorTests.Profile();
        twin.Host = "twin-host";
        twin.BackendKind = SshBackendKind.Native;
        new JsonSshProfileStore().SaveProfile(twin);
        var host = new FakeRemoteHost("nova@twin-host");
        _otherRemotes.Add(host);
        _twin = (twin, host);
        return (twin, host, MuxEndpointId.ForSsh(twin.Id));
    }

    /// <summary>A pane of <paramref name="profile"/>, as the window saved it: reopening <paramref name="id"/> on that profile's daemon.</summary>
    private static PaneNode LeafOf(SshProfile profile, Guid id) => new()
    {
        Type = NodeType.Leaf,
        SshProfileId = profile.Id.ToString(),
        MuxSessionId = id.ToString("D"),
        MuxEndpoint = MuxEndpointId.ForSsh(profile.Id).ToString(),
    };

    /// <summary>The offered row of session <paramref name="id"/> on <paramref name="endpoint"/>; fails when there is none.</summary>
    private static MuxSessionPickerRow Row(IReadOnlyList<MuxPickerItem> items, Guid id, MuxEndpointId endpoint = default)
    {
        MuxSessionPickerRow? row = MuxPickerChoice.Find(items, id, endpoint);
        Assert.NotNull(row);
        return row;
    }

    private Guid LocalSessionId(MainWindow window)
    {
        PumpUntil(() => AllPanes(window).Any(p => MuxEndpointId.Parse(p.MuxEndpoint).IsLocal && p.Session is MuxClientSession { IsAttached: true }), "the local pane attached");
        return AllPanes(window).Single(p => MuxEndpointId.Parse(p.MuxEndpoint).IsLocal && p.Session is MuxClientSession).Session!.Id;
    }

    /// <summary>
    /// Phase 5 spec §5: the picker lists the local daemon's sessions and each connected remote host's, each remote row
    /// naming its host. A host with no connection now is not listed - listing never connects, which could prompt - but
    /// offered as a "Connect to …" row, as is each profile that keeps its sessions; a profile that does not is not offered.
    /// </summary>
    [AvaloniaFact]
    public void The_picker_lists_connected_hosts_and_offers_to_connect_the_others()
    {
        Guid[] ids = SpawnOnRemote(2);
        (SshProfile twin, FakeRemoteHost twinHost, MuxEndpointId twinEndpoint) = AddTwin();
        twinHost.Script = FakeRemoteScript.ConnectionRefused; // its host exists - a restored tab uses it - but is not connected
        SshProfile plain = RemoteMuxConnectorTests.Profile();
        plain.MuxOptions.PersistRemoteSessions = false;
        new JsonSshProfileStore().SaveProfile(plain);
        try
        {
            SaveSession(RemoteLeaf(ids[0]), LeafOf(twin, Guid.NewGuid()), LocalLeaf());
            MainWindow window = CreateWindow();
            PumpUntil(() => RemotePanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true }), "the remote pane reattached");
            PumpUntil(() => AllPanes(window).Any(p => p.MuxEndpoint == twinEndpoint.ToString() && Shows(p, TerminalPane.RemoteUnreachableBanner("nova@twin-host"))),
                "the twin's pane gave up for now");
            Guid local = LocalSessionId(window);
            Assert.Null(window.MuxHosts!.TryGet(twinEndpoint)!.CurrentClient);
            int twinStarts = twinHost.StartCount;

            IReadOnlyList<MuxPickerItem> items = Assert.Single(Attach(window, (_, _) => null));

            Assert.Equal(4, items.Count);
            List<MuxSessionPickerRow> rows = [.. items.OfType<MuxSessionPickerRow>()];
            Assert.Equal(3, rows.Count);
            Assert.Equal((MuxEndpointId.Local, local, true), (rows[0].Endpoint, rows[0].SessionId, rows[0].OpenHere));
            Assert.False(rows[0].Display.StartsWith('['));
            Assert.Equal(
                [(RemoteId, ids[0], true, "nova@fake-host"), (RemoteId, ids[1], false, "nova@fake-host")],
                rows.Skip(1).Select(r => (r.Endpoint, r.SessionId, r.OpenHere, r.HostDisplayName)).OrderBy(r => r.SessionId == ids[0] ? 0 : 1));
            Assert.Equal(2, rows.Count(r => r.Display.StartsWith("[nova@fake-host] ", StringComparison.Ordinal)));
            MuxSessionPickerConnectRow connect = Assert.IsType<MuxSessionPickerConnectRow>(items[3]);
            Assert.Equal((twin.Id, "nova@twin-host", "Connect to nova@twin-host…"), (connect.ProfileId, connect.HostDisplayName, connect.Display));
            Assert.Equal(twinStarts, twinHost.StartCount); // nothing tried to connect it
        }
        finally
        {
            new JsonSshProfileStore().DeleteProfile(plain.Id);
        }
    }

    /// <summary>
    /// A profile's <c>PersistRemoteSessions</c> is read again for every picker: once it is off, its connected host gives no
    /// rows, and no connect row either (a tab opened from one would run plain SSH with the id pending).
    /// </summary>
    [AvaloniaFact]
    public void A_profile_that_stopped_keeping_its_sessions_is_not_offered()
    {
        Guid[] ids = SpawnOnRemote(1);
        SaveSession(RemoteLeaf(ids[0]), LocalLeaf());
        MainWindow window = CreateWindow();
        PumpUntil(() => RemotePanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true }), "the remote pane reattached");
        Guid local = LocalSessionId(window);
        _sshProfile.MuxOptions.PersistRemoteSessions = false;
        new JsonSshProfileStore().SaveProfile(_sshProfile);

        IReadOnlyList<MuxPickerItem> items = Assert.Single(Attach(window, (_, _) => null));

        MuxSessionPickerRow row = Assert.IsType<MuxSessionPickerRow>(Assert.Single(items));
        Assert.Equal((MuxEndpointId.Local, local), (row.Endpoint, row.SessionId));
    }

    /// <summary>
    /// Phase 5 spec §5: a remote row opens an SSH tab of that profile, not a local shell, attached shared to the session on
    /// that host's daemon - over the host's one connection - and marked shared once another client shows it too.
    /// </summary>
    [AvaloniaFact]
    public void Choosing_a_remote_row_opens_a_shared_ssh_tab_on_that_host()
    {
        Guid[] ids = SpawnOnRemote(1);
        Guid shared = AnotherClientsShell(); // another machine's client shows a second shell on that host
        SaveSession(RemoteLeaf(ids[0]));
        MainWindow window = CreateWindow();
        PumpUntil(() => RemotePanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true }), "the remote pane reattached");
        int starts = _remote.StartCount;

        IReadOnlyList<MuxPickerItem> offered = Assert.Single(Attach(window, (items, _) => MuxPickerChoice.Find(items, shared, RemoteId)));

        Assert.False(Row(offered, shared, RemoteId).OpenHere);
        PumpUntil(() => RemotePanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true } m && m.Id == shared), "the chosen remote shell attached");
        TerminalPane pane = RemotePanes(window).Single(p => p.Session?.Id == shared);
        Assert.Equal(Endpoint, pane.MuxEndpoint);
        Assert.Equal((ConnectionType.SSH, _sshProfile.Id), (pane.Profile!.Type, pane.Profile.Id));
        Assert.Equal(MuxAttachMode.Shared, ((MuxClientSession)pane.Session!).AttachMode);
        Assert.True(pane.MuxSessionIsShare);
        Assert.Equal(starts, _remote.StartCount); // over the host's connection, not a new one
        PumpUntil(() => pane.MuxOtherClients == 1, "the pane counts the other machine's client");
        TabItem tab = window.FindControl<TabControl>("Tabs")!.Items.OfType<TabItem>().Single(t => ReferenceEquals(t.Content, pane));
        PumpUntil(() => (ToolTip.GetTip((Control)tab.Header!) as string)?.Contains(MainWindow.SharedGlyph, StringComparison.Ordinal) == true, "the tab is marked shared");
    }

    /// <summary>One connection cannot hold two views of one session: a remote session this window shows is focused, not opened again.</summary>
    [AvaloniaFact]
    public void Choosing_a_remote_session_already_shown_focuses_its_tab()
    {
        Guid[] ids = SpawnOnRemote(1);
        SaveTabs(RemoteLeaf(ids[0]), LocalLeaf());
        MainWindow window = CreateWindow();
        PumpUntil(() => RemotePanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true }), "the remote pane reattached");
        TerminalPane remote = RemotePanes(window).Single();
        TabControl tabs = window.FindControl<TabControl>("Tabs")!;
        tabs.SelectedIndex = 1;
        LocalSessionId(window);
        int panes = AllPanes(window).Count;

        IReadOnlyList<MuxPickerItem> offered = Assert.Single(Attach(window, (items, _) => MuxPickerChoice.Find(items, ids[0], RemoteId)));

        Assert.True(Row(offered, ids[0], RemoteId).OpenHere);
        Assert.Equal(panes, AllPanes(window).Count);
        Assert.Same(remote, ((TabItem)tabs.SelectedItem!).Content);
    }

    /// <summary>
    /// Phase 5 spec §5: a host the window holds no connection to - the last tab on it detached - is offered as "Connect to …".
    /// Choosing that connects once, then reopens the picker with every host's rows, that host's included; a tab opened from
    /// it attaches over that same connection, which then stays for the tab.
    /// </summary>
    [AvaloniaFact]
    public void Choosing_a_connect_row_connects_once_then_offers_that_hosts_sessions()
    {
        Guid[] ids = SpawnOnRemote(1);
        SaveSession(LocalLeaf());
        MainWindow window = CreateWindow();
        Guid local = LocalSessionId(window);
        Assert.Null(RemoteHostOf(window));

        List<IReadOnlyList<MuxPickerItem>> offered = Attach(window, (items, call) => call == 1
            ? items.OfType<MuxSessionPickerConnectRow>().Single(r => r.ProfileId == _sshProfile.Id)
            : MuxPickerChoice.Find(items, ids[0], RemoteId));

        Assert.Equal(2, offered.Count);
        Assert.DoesNotContain(offered[0], i => i is MuxSessionPickerRow { Endpoint.IsLocal: false });
        Assert.NotNull(MuxPickerChoice.Find(offered[1], local));
        Assert.False(Row(offered[1], ids[0], RemoteId).OpenHere);
        Assert.DoesNotContain(offered[1], i => i is MuxSessionPickerConnectRow);
        MuxConnectionHost host = RemoteHostOf(window)!;
        Assert.Equal(1, host.ConnectAttempts);
        PumpUntil(() => RemotePanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true } m && m.Id == ids[0]), "the chosen remote shell attached");
        Assert.Equal(1, _remote.StartCount); // the tab attached over the connection the picker made
        PumpFor(200);                          // the picker's release pass has run: the tab needs the host
        Assert.Same(host, RemoteHostOf(window));
        Assert.False(host.IsClosed);
    }

    /// <summary>
    /// A host connected only for the picker is held while the reopened picker is open - a release pass then (a pane closing
    /// elsewhere) leaves it alone - and let go once nothing was chosen from it.
    /// </summary>
    [AvaloniaFact]
    public void A_host_connected_only_for_the_picker_is_let_go_when_nothing_is_opened_on_it()
    {
        SpawnOnRemote(1);
        SaveSession(LocalLeaf());
        MainWindow window = CreateWindow();
        LocalSessionId(window);
        var offered = new List<IReadOnlyList<MuxPickerItem>>();
        var reopened = new TaskCompletionSource<MuxPickerItem?>();
        window.PickMuxSession = items =>
        {
            offered.Add(items);
            return offered.Count == 1
                ? Task.FromResult<MuxPickerItem?>(items.OfType<MuxSessionPickerConnectRow>().Single(r => r.ProfileId == _sshProfile.Id))
                : reopened.Task;
        };

        Task command = window.AttachToMuxSessionAsync();
        PumpUntil(() => offered.Count == 2, "the picker reopened");
        MuxConnectionHost host = RemoteHostOf(window)!;
        typeof(MainWindow).GetMethod("ScheduleRemoteMuxHostRelease", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
        PumpFor(200);
        Assert.False(host.IsClosed, "a release pass let go of the host the open picker lists");
        Assert.NotNull(host.CurrentClient);

        reopened.SetResult(null);
        PumpUntil(() => command.IsCompleted, "the attach command finished");
        PumpUntil(() => host.IsClosed && RemoteHostOf(window) is null, "the host connected only for the picker was let go");
        Assert.Equal(1, _remote.StartCount);
    }

    /// <summary>
    /// Ruling: each host is listed within its own wait, in parallel. A host that does not answer gives its error row,
    /// worded in our own words, and the picker still lists every other host.
    /// </summary>
    [AvaloniaFact]
    public void A_host_that_does_not_answer_gives_its_error_row_and_the_others_are_listed()
    {
        Guid[] ids = SpawnOnRemote(1);
        (SshProfile twin, FakeRemoteHost twinHost, MuxEndpointId twinEndpoint) = AddTwin();
        Guid[] twinIds = SpawnOnRemote(1, twinHost);
        SaveSession(RemoteLeaf(ids[0]), LeafOf(twin, twinIds[0]), LocalLeaf());
        MainWindow window = CreateWindow();
        PumpUntil(() => AllPanes(window).Count(p => !MuxEndpointId.Parse(p.MuxEndpoint).IsLocal && p.Session is MuxClientSession { IsAttached: true }) == 2, "both remote panes reattached");
        Guid local = LocalSessionId(window);
        _remote.StallLink(); // nothing gets through any more, and nothing says so

        IReadOnlyList<MuxPickerItem> items = Assert.Single(Attach(window, (_, _) => null));

        Assert.Equal(3, items.Count);
        Assert.Same(MuxPickerChoice.Find(items, local), items[0]);
        MuxSessionPickerErrorRow error = Assert.Single(items.OfType<MuxSessionPickerErrorRow>());
        Assert.Equal((RemoteId, "[nova@fake-host] not reachable: it did not answer in time"), (error.Endpoint, error.Display));
        Assert.NotNull(MuxPickerChoice.Find(items, twinIds[0], twinEndpoint));
        _remote.CutLink(); // the stalled channel ends, so the window's teardown does not wait on it
    }

    /// <summary>
    /// Review minor 3: the hosts are listed in parallel. Two hosts that do not answer are each waited for up to their request
    /// wait, together: the picker is offered after about one wait, not two.
    /// </summary>
    [AvaloniaFact]
    public void Two_hosts_that_do_not_answer_are_waited_for_together()
    {
        TimeSpan wait = TimeSpan.FromSeconds(4);
        _remoteRpcTimeout = wait;
        Guid[] ids = SpawnOnRemote(1);
        (SshProfile twin, FakeRemoteHost twinHost, MuxEndpointId twinEndpoint) = AddTwin();
        Guid[] twinIds = SpawnOnRemote(1, twinHost);
        SaveSession(RemoteLeaf(ids[0]), LeafOf(twin, twinIds[0]), LocalLeaf());
        MainWindow window = CreateWindow();
        PumpUntil(() => AllPanes(window).Count(p => !MuxEndpointId.Parse(p.MuxEndpoint).IsLocal && p.Session is MuxClientSession { IsAttached: true }) == 2, "both remote panes reattached");
        LocalSessionId(window);
        _remote.StallLink();
        twinHost.StallLink();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan offeredAfter = TimeSpan.Zero;

        IReadOnlyList<MuxPickerItem> items = Assert.Single(Attach(window, (_, _) =>
        {
            offeredAfter = clock.Elapsed;
            return null;
        }));

        List<MuxSessionPickerErrorRow> errors = [.. items.OfType<MuxSessionPickerErrorRow>()];
        Assert.Equal(2, errors.Count);
        Assert.Equal([RemoteId, twinEndpoint], errors.Select(e => e.Endpoint).OrderBy(e => e == RemoteId ? 0 : 1));
        Assert.Equal(2, errors.Count(e => e.Error == MuxPickerHostError.TimedOut));
        // Waiting in turn takes at least 2 x wait (8 s); the 1.75 x bound leaves 3 s for a loaded CI runner.
        Assert.InRange(offeredAfter, wait * 0.9, wait * 1.75);
        _remote.CutLink(); // the stalled channels end, so the window's teardown does not wait on them
        twinHost.CutLink();
    }

    /// <summary>The connecting notice of a "Connect to …" row for <see cref="_sshProfile"/>'s host.</summary>
    private const string ConnectingLine = "Connecting to nova@fake-host…";

    /// <summary>Holds every connect to <see cref="_remote"/> until the returned gate is set (or the attempt is cancelled).</summary>
    private ManualResetEventSlim HoldConnects()
    {
        var gate = new ManualResetEventSlim();
        _remote.OnStart = ct => gate.Wait(ct);
        return gate;
    }

    /// <summary>A picker seam whose first picker chooses <see cref="_sshProfile"/>'s connect row; later ones answer <paramref name="then"/>.</summary>
    private Func<IReadOnlyList<MuxPickerItem>, Task<MuxPickerItem?>> ConnectFirst(List<IReadOnlyList<MuxPickerItem>> offered, Func<IReadOnlyList<MuxPickerItem>, Task<MuxPickerItem?>> then) =>
        items =>
        {
            offered.Add(items);
            return offered.Count == 1
                ? Task.FromResult<MuxPickerItem?>(items.OfType<MuxSessionPickerConnectRow>().Single(r => r.ProfileId == _sshProfile.Id))
                : then(items);
        };

    /// <summary>
    /// Fix round 1: a connect row says it is connecting, under the name the row shows, for as long as the connect takes,
    /// and the notice is gone before the reopened picker comes up - it never sits under a modal.
    /// </summary>
    [AvaloniaFact]
    public void A_connect_row_shows_connecting_until_the_picker_reopens()
    {
        SpawnOnRemote(1);
        SaveSession(LocalLeaf());
        MainWindow window = CreateWindow();
        LocalSessionId(window);
        using ManualResetEventSlim gate = HoldConnects();
        var offered = new List<IReadOnlyList<MuxPickerItem>>();
        string[]? linesWhenReopened = null;
        window.PickMuxSession = ConnectFirst(offered, _ =>
        {
            linesWhenReopened = ToastLines(window);
            return Task.FromResult<MuxPickerItem?>(null);
        });

        Task command = window.AttachToMuxSessionAsync();
        PumpUntil(() => _remote.StartCount == 1, "the connect is under way");
        PumpUntil(() => ToastLines(window).Contains(ConnectingLine), "the connecting notice is shown");
        PumpFor(100);
        Assert.Contains(ConnectingLine, ToastLines(window)); // it stays while the connect does

        gate.Set();
        PumpUntil(() => command.IsCompleted, "the attach command finished");
        Assert.Equal(2, offered.Count);
        Assert.NotNull(linesWhenReopened);
        Assert.DoesNotContain(ConnectingLine, linesWhenReopened);
        Assert.DoesNotContain(ConnectingLine, ToastLines(window));
    }

    /// <summary>Fix round 1: a connect that fails replaces its connecting notice with the failure's.</summary>
    [AvaloniaFact]
    public void A_connect_row_that_fails_replaces_its_notice_with_the_failure()
    {
        SaveSession(LocalLeaf());
        MainWindow window = CreateWindow();
        LocalSessionId(window);
        _remote.Script = FakeRemoteScript.ConnectionRefused;
        using ManualResetEventSlim gate = HoldConnects();
        var offered = new List<IReadOnlyList<MuxPickerItem>>();
        window.PickMuxSession = ConnectFirst(offered, _ => Task.FromResult<MuxPickerItem?>(null));

        Task command = window.AttachToMuxSessionAsync();
        PumpUntil(() => ToastLines(window).Contains(ConnectingLine), "the connecting notice is shown");
        gate.Set();
        PumpUntil(() => command.IsCompleted, "the attach command finished");

        PumpUntil(() => ToastLines(window).Contains("Could not connect to nova@fake-host."), "the failure is shown");
        Assert.DoesNotContain(ConnectingLine, ToastLines(window));
        Assert.Single(offered); // no picker reopened
    }

    /// <summary>
    /// Fix round 1: a connect the window gives up on - here the window closes while it is held - takes its connecting notice
    /// away with it, and says nothing more.
    /// </summary>
    [AvaloniaFact]
    public void A_connect_row_cancelled_by_the_window_closing_takes_its_notice_away()
    {
        SaveSession(LocalLeaf());
        MainWindow window = CreateWindow();
        LocalSessionId(window);
        using ManualResetEventSlim gate = HoldConnects();
        var offered = new List<IReadOnlyList<MuxPickerItem>>();
        window.PickMuxSession = ConnectFirst(offered, _ => Task.FromResult<MuxPickerItem?>(null));

        Task command = window.AttachToMuxSessionAsync();
        PumpUntil(() => ToastLines(window).Contains(ConnectingLine), "the connecting notice is shown");
        window.Close();
        PumpUntil(() => command.IsCompleted, "the attach command finished");
        PumpFor(100);

        Assert.DoesNotContain(ConnectingLine, ToastLines(window));
        Assert.DoesNotContain(ToastLines(window), l => l.StartsWith("Could not connect", StringComparison.Ordinal));
        Assert.Single(offered);
    }

    /// <summary>
    /// Review minor 1: "Attach to session…" again while one runs (here its connect is held) starts nothing - no second
    /// connect, no second picker - and the first goes on as before.
    /// </summary>
    [AvaloniaFact]
    public void A_second_attach_while_one_runs_starts_nothing()
    {
        SpawnOnRemote(1);
        SaveSession(LocalLeaf());
        MainWindow window = CreateWindow();
        LocalSessionId(window);
        using ManualResetEventSlim gate = HoldConnects();
        var offered = new List<IReadOnlyList<MuxPickerItem>>();
        window.PickMuxSession = ConnectFirst(offered, _ => Task.FromResult<MuxPickerItem?>(null));

        Task first = window.AttachToMuxSessionAsync();
        PumpUntil(() => _remote.StartCount == 1, "the first command's connect is under way");
        MuxConnectionHost host = RemoteHostOf(window)!;
        Task second = window.AttachToMuxSessionAsync();
        PumpFor(200);

        Assert.True(second.IsCompleted, "the second command waited on the first");
        Assert.Single(offered);
        Assert.Equal(1, host.ConnectAttempts);
        gate.Set();
        PumpUntil(() => first.IsCompleted, "the first command finished");
        Assert.Equal(2, offered.Count); // the first's reopened picker, and nothing else
        Assert.Equal(1, _remote.StartCount);
    }

    /// <summary>
    /// Review minor 2: every remote host the picker lists is held while it is open. The last pane of a listed host closing
    /// meanwhile does not let its connection go, and a row of that host chosen afterwards attaches over it.
    /// </summary>
    [AvaloniaFact]
    public void A_listed_hosts_connection_stays_while_the_picker_is_open()
    {
        Guid[] ids = SpawnOnRemote(2);
        SaveSession(RemoteLeaf(ids[0]), LocalLeaf());
        MainWindow window = CreateWindow();
        PumpUntil(() => RemotePanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true }), "the remote pane reattached");
        LocalSessionId(window);
        MuxConnectionHost host = RemoteHostOf(window)!;
        var offered = new List<IReadOnlyList<MuxPickerItem>>();
        var pick = new TaskCompletionSource<MuxPickerItem?>();
        window.PickMuxSession = items =>
        {
            offered.Add(items);
            return pick.Task;
        };

        Task command = window.AttachToMuxSessionAsync();
        PumpUntil(() => offered.Count == 1, "the picker is open");
        Task<bool> close = Close(window, RemotePanes(window).Single());
        PumpUntil(() => close.IsCompleted, "the host's last pane closed");
        KillLands(host, ids[0], "the closed pane's shell ended");
        PumpFor(300);
        Assert.False(host.IsClosed, "the connection of a host the open picker lists was let go");
        Assert.Same(host, RemoteHostOf(window));

        pick.SetResult(Row(offered[0], ids[1], RemoteId));
        PumpUntil(() => command.IsCompleted, "the attach command finished");
        PumpUntil(() => RemotePanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true } m && m.Id == ids[1]), "the chosen remote shell attached");
        Assert.Equal(1, _remote.StartCount);
        Assert.Same(host, RemoteHostOf(window));
    }

    /// <summary>
    /// Review minor 3: the profile's <c>PersistRemoteSessions</c> is read again at the pick. Turned off while the picker was
    /// open, the row opens nothing (it would run plain SSH with the id pending), and a notice says why.
    /// </summary>
    [AvaloniaFact]
    public void A_remote_row_whose_profile_stopped_keeping_sessions_before_the_pick_opens_nothing()
    {
        Guid[] ids = SpawnOnRemote(2);
        SaveSession(RemoteLeaf(ids[0]), LocalLeaf());
        MainWindow window = CreateWindow();
        PumpUntil(() => RemotePanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true }), "the remote pane reattached");
        LocalSessionId(window);
        int panes = AllPanes(window).Count;

        Attach(window, (items, _) =>
        {
            _sshProfile.MuxOptions.PersistRemoteSessions = false; // turned off while the picker was open
            new JsonSshProfileStore().SaveProfile(_sshProfile);
            return Row(items, ids[1], RemoteId);
        });
        PumpFor(200);

        Assert.Equal(panes, AllPanes(window).Count);
        Assert.DoesNotContain(AllPanes(window), p => p.MuxSessionIdToRestore == ids[1]);
        PumpUntil(() => ToastLines(window).Contains("The profile for nova@fake-host no longer keeps remote sessions running."), "the notice says why");
    }

    /// <summary>The daemon clients of other machines a test started (<see cref="AnotherClientsShell"/>); disposed with it.</summary>
    private readonly List<MuxClient> _otherClients = [];

    /// <summary>
    /// A shell another machine's client spawned on the remote daemon and shows, over a daemon connection of its own that a
    /// cut link leaves alone.
    /// </summary>
    private Guid AnotherClientsShell()
    {
        (MuxClient client, ClientPaneModel shown) = Task.Run(async () =>
        {
            (Stream stream, _) = await _remote.ConnectDaemonAsync(TestContext.Current.CancellationToken);
            MuxClient c = await MuxClient.ConnectAsync(stream, null, TestContext.Current.CancellationToken);
            Guid id = await MuxTestHost.SpawnAsync(c);
            return (c, await MuxTestHost.AttachPaneAsync(c, id));
        }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        _otherClients.Add(client);
        return shown.Session.Id;
    }

    /// <summary>Whether <paramref name="id"/> still runs on the remote daemon: there, and not exited.</summary>
    private bool RunsOnRemote(Guid id) => _remote.Server.TryGetSession(id, out HeadlessTerminalSession? session) && !session.IsExited;

    /// <summary>
    /// Phase 5 Task 26: a window with two tabs on the profile's host, both attached over one connection: <c>Owned</c>, restored,
    /// whose shell is its own (split with a local pane, which keeps the window open), and <c>Share</c>, opened from "Attach to
    /// session…" on <c>Theirs</c> (<see cref="AnotherClientsShell"/>).
    /// </summary>
    private (MainWindow Window, TerminalPane Owned, TerminalPane Share, Guid Theirs) WindowWithAnOwnedAndASharedRemoteTab()
    {
        Guid[] ids = SpawnOnRemote(1);
        Guid theirs = AnotherClientsShell();
        SaveSession(RemoteLeaf(ids[0]), LocalLeaf());
        MainWindow window = CreateWindow();
        PumpUntil(() => RemotePanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true }), "the owned tab reattached");
        TerminalPane owned = RemotePanes(window).Single();

        Attach(window, (items, _) => Row(items, theirs, RemoteId));

        PumpUntil(() => RemotePanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true } m && m.Id == theirs), "the shared tab attached");
        TerminalPane share = RemotePanes(window).Single(p => p.Session?.Id == theirs);
        Assert.True(share.MuxSessionIsShare);
        Assert.Equal(1, _remote.StartCount); // one connection for both
        return (window, owned, share, theirs);
    }

    /// <summary>
    /// Phase 5 Task 26: a shared tab closed while its host reconnects cannot learn who else shows that shell, so it detaches:
    /// nothing is queued for the next connect, and the other client's shell runs on. Before, the close queued a kill that
    /// ended another client's shell as soon as the link was back. The owned tab's reattach is the proof that the link came
    /// back: the kills queued meanwhile go out on the new connection before any pane reattaches over it.
    /// </summary>
    [AvaloniaFact]
    public void Closing_a_shared_remote_tab_while_its_host_reconnects_kills_nothing()
    {
        (MainWindow window, TerminalPane owned, TerminalPane share, Guid theirs) = WindowWithAnOwnedAndASharedRemoteTab();
        var ownedBefore = (MuxClientSession)owned.Session!;
        _remote.CutLink();
        PumpUntil(() => Shows(share, TerminalPane.RemoteReconnectingBanner("nova@fake-host")), "the shared tab is reconnecting");

        Task<bool> close = Close(window, share);
        PumpUntil(() => close.IsCompleted, "the shared tab closed");
        Assert.True(close.Result);
        // Fix round 1, I1: the close that skipped the question still says the shell kept running, as "Pane: Detach" does.
        PumpUntil(() => ToastLines(window).Contains(MainWindow.RemoteDetachedMessage("nova@fake-host", theirs, reopenable: true)), "the detach notice is shown");

        _clock.Advance(FirstRetry); // the link is back
        PumpUntil(() => owned.Session is MuxClientSession { IsAttached: true } m && !ReferenceEquals(m, ownedBefore), "the owned tab reattached over the new connection");

        Assert.True(RunsOnRemote(theirs), "the other client's shell was killed");
        Assert.Equal(ownedBefore.Id, owned.Session!.Id);
    }

    /// <summary>
    /// Fix round 1, I1: the user closes a shared tab while its host reconnects. Nothing is asked - no shared-close question,
    /// no running-process confirmation, since nothing will end - the tab detaches, and the notice "Pane: Detach" shows
    /// says its shell kept running on that host. Before, the tab went with no word, and the shell's fate was unsaid.
    /// </summary>
    [AvaloniaFact]
    public void Closing_a_reconnecting_shared_remote_tab_by_hand_detaches_unasked_and_says_so()
    {
        (MainWindow window, _, TerminalPane share, Guid theirs) = WindowWithAnOwnedAndASharedRemoteTab();
        window.ConfirmSharedClose = _ => throw new InvalidOperationException("a share whose sharing is unknown is not asked about");
        int dialogs = window.OwnedWindows.Count;
        _remote.CutLink();
        PumpUntil(() => Shows(share, TerminalPane.RemoteReconnectingBanner("nova@fake-host")), "the shared tab is reconnecting");

        var close = (Task<bool>)typeof(MainWindow).GetMethod("ClosePaneAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [share, false])!;

        PumpUntil(() => close.IsCompleted || window.OwnedWindows.Count > dialogs, "the close finished or asked");
        Assert.Equal(dialogs, window.OwnedWindows.Count);
        Assert.True(close.Result);
        Assert.DoesNotContain(share, AllPanes(window));
        PumpUntil(() => ToastLines(window).Contains(MainWindow.RemoteDetachedMessage("nova@fake-host", theirs, reopenable: true)), "the detach notice is shown");
    }

    /// <summary>
    /// Phase 5 Task 26 leaves an owned tab's close as it was: closed while its host reconnects, it queues its kill, which ends
    /// its shell once the link is back. A shared tab beside it changes nothing, and its shell runs on.
    /// </summary>
    [AvaloniaFact]
    public void Closing_an_owned_remote_tab_while_its_host_reconnects_still_kills_it()
    {
        (MainWindow window, TerminalPane owned, _, Guid theirs) = WindowWithAnOwnedAndASharedRemoteTab();
        Guid mine = owned.Session!.Id;
        MuxConnectionHost host = RemoteHostOf(window)!;
        _remote.CutLink();
        PumpUntil(() => Shows(owned, TerminalPane.RemoteReconnectingBanner("nova@fake-host")), "the owned tab is reconnecting");

        Task<bool> close = Close(window, owned);
        PumpUntil(() => close.IsCompleted, "the owned tab closed");
        Assert.True(close.Result);
        Assert.Contains(mine, _remote.Server.GetSessionIds()); // nothing could be sent yet

        _clock.Advance(FirstRetry); // the link is back: the queued kill goes first

        KillLands(host, mine, "the queued kill ended the owned tab's shell");
        Assert.True(RunsOnRemote(theirs), "the shared tab's shell was killed");
    }

    /// <summary>
    /// Phase 5 Task 26, the lost route: the profile stopped keeping its sessions after the pick - before the pane routed its
    /// spawn, or while its connect ran - so the shared tab opens plain SSH. It lets go of the shared id (another client's
    /// shell, which it neither saves nor ends), and its close sends no kill. Before, the id stayed pending and the close
    /// killed it. The owned tab's kill, sent after the plain tab's close, is the proof: a kill from that close would have
    /// gone out before it, on the same connection.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_share_whose_profile_stopped_keeping_sessions_opens_plain_ssh_and_its_close_kills_nothing(bool whileConnecting)
    {
        Guid[] ids = SpawnOnRemote(1);
        Guid theirs = AnotherClientsShell();
        SaveSession(RemoteLeaf(ids[0]), LocalLeaf());
        MainWindow window = CreateWindow();
        PumpUntil(() => RemotePanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true }), "the owned tab reattached");
        TerminalPane owned = RemotePanes(window).Single();
        MuxConnectionHost host = RemoteHostOf(window)!;

        Attach(window, (items, _) =>
        {
            // The store still keeps the profile's sessions, so the pick goes ahead; the pane's spawn reads the flag off.
            if (whileConnecting) Volatile.Write(ref _stopPersistingInTheNextFactoryCall, 1);
            else _sshProfile.MuxOptions.PersistRemoteSessions = false;
            return Row(items, theirs, RemoteId);
        });

        PumpUntil(() => AllPanes(window).Any(p => p.Session is FakeTerminalSession), "the shared tab opened plain SSH");
        TerminalPane plain = AllPanes(window).Single(p => p.Session is FakeTerminalSession);
        Assert.Null(plain.MuxSessionIdToRestore);
        Assert.Null(SessionManager.BuildPaneTree(plain)!.MuxSessionId);

        Task<bool> close = Close(window, plain);
        PumpUntil(() => close.IsCompleted, "the plain tab closed");
        Assert.True(close.Result);
        Guid mine = owned.Session!.Id;
        Task<bool> closeOwned = Close(window, owned);
        PumpUntil(() => closeOwned.IsCompleted, "the owned tab closed");

        KillLands(host, mine, "the owned tab's kill landed");
        Assert.True(RunsOnRemote(theirs), "the other client's shell was killed");
        Assert.Equal(1, _remote.StartCount); // one connection carried every kill, so their order held
    }

    /// <summary>
    /// Phase 5 Task 23's parked item: while the local daemon restarts, its sessions are about to end, so the picker lists
    /// none of them - one line says why - and still lists the remote hosts.
    /// </summary>
    [AvaloniaFact]
    public void While_the_local_multiplexer_restarts_the_picker_lists_none_of_its_sessions()
    {
        Guid[] ids = SpawnOnRemote(1);
        SaveSession(RemoteLeaf(ids[0]), LocalLeaf());
        var launch = new MuxPreviousBuildNotice.Launch();
        MainWindow window = CreateWindow(beforeShow: w => w.MuxPreviousBuildLaunch = launch);
        PumpUntil(() => RemotePanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true }), "the remote pane reattached");
        LocalSessionId(window);
        Assert.True(launch.TryBeginRestart(MuxEndpointId.Local));

        IReadOnlyList<MuxPickerItem> items = Assert.Single(Attach(window, (_, _) => null));

        Assert.Equal(2, items.Count);
        MuxSessionPickerErrorRow restarting = Assert.IsType<MuxSessionPickerErrorRow>(items[0]);
        Assert.Equal((MuxEndpointId.Local, "[this computer] not reachable: the multiplexer is restarting"), (restarting.Endpoint, restarting.Display));
        Assert.NotNull(MuxPickerChoice.Find(items, ids[0], RemoteId));
    }

    /// <summary>A local row chosen just as a restart of the local daemon began opens nothing on the dying daemon, and says why.</summary>
    [AvaloniaFact]
    public void A_local_row_chosen_as_a_restart_begins_opens_nothing()
    {
        // A local session another instance shows: not an orphan, so only the picker can open it here.
        (MuxClient other, ClientPaneModel theirs) = Task.Run(async () =>
        {
            MuxClient c = await _localMux.ConnectClientAsync();
            Guid id = await MuxTestHost.SpawnAsync(c);
            return (c, await MuxTestHost.AttachPaneAsync(c, id));
        }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        SaveSession(LocalLeaf());
        var launch = new MuxPreviousBuildNotice.Launch();
        MainWindow window = CreateWindow(beforeShow: w => w.MuxPreviousBuildLaunch = launch);
        LocalSessionId(window);
        int panes = AllPanes(window).Count;

        Attach(window, (items, _) =>
        {
            Assert.True(launch.TryBeginRestart(MuxEndpointId.Local)); // begun while the picker was open
            return MuxPickerChoice.Find(items, theirs.Session.Id);
        });

        PumpUntil(() => ToastLines(window).Contains("[this computer] not reachable: the multiplexer is restarting"), "the notice says why");
        Assert.Equal(panes, AllPanes(window).Count);
        GC.KeepAlive(other);
    }

    /// <summary>
    /// Phase 5 spec §3, ruling R4: the window's windowless sessions are its daemons' sessions that none of its panes
    /// shows or is about to show (a restored tab not shown yet keeps its id pending), on the local daemon and on a remote
    /// endpoint it is connected to. Asked from the pool, as the agent host asks: the window's look at its panes is posted
    /// to the UI thread, which this test pumps.
    /// </summary>
    [AvaloniaFact]
    public void The_windowless_sessions_are_those_no_pane_of_the_window_shows_or_will_show()
    {
        Guid[] ids = SpawnOnRemote(3);
        SaveTabs(
            new PaneNode { Type = NodeType.Split, SplitOrientation = 0, Children = [RemoteLeaf(ids[0]), LocalLeaf()] },
            RemoteLeaf(ids[1]));   // the second tab restores in the background, unshown: ids[1] stays pending
        MainWindow window = CreateWindow();
        PumpUntil(() => RemotePanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true }), "the shown remote pane reattached");
        PumpUntil(() => AllPanes(window).Any(p => MuxEndpointId.Parse(p.MuxEndpoint).IsLocal && p.Session is MuxClientSession { IsAttached: true }), "the local pane attached");
        Assert.Equal(ids[1], RemotePanes(window).Single(p => p.Session is null).MuxSessionIdToRestore);
        // Another instance's local shell, attached there: not an orphan this window adopts, and windowless here.
        Guid theirs = Task.Run(async () =>
        {
            MuxClient other = await _localMux.ConnectClientAsync();
            Guid id = await MuxTestHost.SpawnAsync(other);
            return (await MuxTestHost.AttachPaneAsync(other, id)).Session.Id;
        }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        Ntilde.AgentHost.IWindowlessSessionSource? source = window.WindowlessSessions;
        Assert.NotNull(source);

        Task<IReadOnlyList<Ntilde.AgentHost.WindowlessSessionInfo>> listing = Task.Run(() => source.ListAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        PumpUntil(() => listing.IsCompleted, "the source listed");

        Assert.Equal(
            [("local", theirs, "this computer"), (Endpoint, ids[2], "nova@fake-host")],
            listing.Result.Select(s => (s.Endpoint, s.SessionId, s.HostDisplayName)));
    }

    /// <summary>Phase 5 Task 23: the version this app installs on remote hosts, as the window is told it.</summary>
    private const string ThisBuild = "0.12.0";

    /// <summary>The window of a Task 23 test: persistence on, this build's version pinned, and a launch of its own.</summary>
    private MainWindow CreateRestartWindow(FakeRemoteHost remote) => CreateWindow(
        beforeShow: w =>
        {
            w.MuxThisBuildVersion = ThisBuild;
            w.MuxPreviousBuildLaunch = new MuxPreviousBuildNotice.Launch();
            w.MuxNoticeRaisedForTest = _notices.Add;
        },
        remote: remote,
        settings: new TerminalSettings { SessionPersistence = SessionPersistenceMode.KeepOnClose });

    /// <summary>A remote host whose ntilde-mux, still running, is an older version than the one this app installs; disposed with the test.</summary>
    private FakeRemoteHost PreviousVersionHost()
    {
        var remote = new FakeRemoteHost(serverOptions: new MuxServerOptions { ForceConPtyFiltering = false, AppVersion = "0.0.1" });
        _otherRemotes.Add(remote);
        return remote;
    }

    private readonly List<FakeRemoteHost> _otherRemotes = [];

    /// <summary>
    /// Every multiplexer notice a Task 23 window raised (UI thread): offers and restarts' outcomes. Read instead of the
    /// toast, which other notices share.
    /// </summary>
    private readonly List<MuxPreviousBuildNotice.Raised> _notices = [];

    private static readonly string TwoShellsNotice = MuxPreviousBuildNotice.RemoteMessage("nova@fake-host", "0.0.1", ThisBuild, 2);

    private MuxEndpointId RemoteId => MuxEndpointId.ForSsh(_sshProfile.Id);

    private List<MuxPreviousBuildNotice.Raised> Offers(MuxEndpointId endpoint) => [.. _notices.Where(n => n.Endpoint == endpoint && n.Outcome is null)];

    private List<MuxPreviousBuildNotice.RestartOutcome> Outcomes => [.. _notices.Where(n => n.Outcome is not null).Select(n => n.Outcome!.Value)];

    /// <summary>Two panes of the profile on a host still running an older ntilde-mux: the window shown, both reattached, the offer raised.</summary>
    private (MainWindow Window, FakeRemoteHost Remote, Guid[] Ids) TwoPanesOnAPreviousVersion()
    {
        FakeRemoteHost remote = PreviousVersionHost();
        Guid[] ids = SpawnOnRemote(2, remote);
        SaveSession(RemoteLeaf(ids[0]), RemoteLeaf(ids[1]));
        MainWindow window = CreateRestartWindow(remote);
        PumpUntil(() => RemotePanes(window).Count(p => p.Session is MuxClientSession { IsAttached: true }) == 2, "both panes reattached");
        PumpUntil(() => Offers(RemoteId).Count == 1, "the notice is raised");
        Assert.Equal(TwoShellsNotice, Offers(RemoteId)[0].Message);
        return (window, remote, ids);
    }

    /// <summary>Cuts the link under the profile's host and moves the clock on until the host has connected again.</summary>
    private void Reconnect(FakeRemoteHost remote, MuxConnectionHost host)
    {
        MuxClient? before = host.CurrentClient;
        remote.CutLink();
        PumpUntil(() =>
        {
            if (host.IsReconnecting) _clock.Advance(FirstRetry);
            return host.CurrentClient is { } c && !ReferenceEquals(c, before);
        }, "the host reconnected");
    }

    /// <summary>
    /// Phase 5 Task 23: a remote ntilde-mux still running an older version than the one this app installs (the install
    /// flow replaced its binary, not it) is offered a restart: one notice for the host, naming it, the daemon's version
    /// and its shells, however many panes use it - on the toast, with its button - and none again when the link drops and
    /// comes back to that daemon.
    /// </summary>
    [AvaloniaFact]
    public void A_remote_daemon_of_a_previous_version_is_offered_a_restart_once()
    {
        (MainWindow window, FakeRemoteHost remote, _) = TwoPanesOnAPreviousVersion();

        PumpUntil(() => ToastLines(window).Contains(TwoShellsNotice), "the toast shows the notice");
        MuxPreviousBuildNotice.Raised offer = Assert.Single(Offers(RemoteId));
        Assert.Equal("Restart ntilde-mux on nova@fake-host", offer.Action!.Label);
        Assert.Equal($"{MuxPreviousBuildNotice.Title}\n{RemoteId}", offer.Key);
        Assert.Equal("Restart ntilde-mux on nova@fake-host", ToastButton(window, "RecordingToastAction").Content);

        // Dismissed; then the link drops, and the host reconnects to the same daemon.
        Click(ToastButton(window, "RecordingToastClose"));
        MuxConnectionHost host = RemoteHostOf(window)!;
        Reconnect(remote, host);
        PumpUntilDecided(window, RemoteId, 2);

        Assert.Equal("0.0.1", host.CurrentClient!.ServerVersion);
        Assert.Single(Offers(RemoteId)); // not offered again
        Assert.Empty(Outcomes);
    }

    /// <summary>
    /// Phase 5 Task 23: the remote action asks, naming the host and its shells, then sends <c>shutdown</c> over that host's
    /// connection - to that daemon only: the local daemon is neither probed nor told anything.
    /// </summary>
    [AvaloniaFact]
    public void Restarting_a_remote_daemon_shuts_down_that_daemon_only()
    {
        FakeRemoteHost remote = PreviousVersionHost();
        Guid[] ids = SpawnOnRemote(2, remote);
        SaveSession(RemoteLeaf(ids[0]), RemoteLeaf(ids[1]), LocalLeaf());
        int remoteShutdowns = 0;
        int localShutdowns = 0;
        remote.Server.ShutdownRequested += () => { Interlocked.Increment(ref remoteShutdowns); _ = Task.Run(remote.StopDaemon); };
        _localMux.Server.ShutdownRequested += () => Interlocked.Increment(ref localShutdowns);
        MainWindow window = CreateRestartWindow(remote);
        var asked = new List<(string? Host, int Shells)>();
        window.ConfirmMuxRestart = (host, shells) => { asked.Add((host, shells)); return Task.FromResult(true); };
        bool localProbed = false;
        window.MuxProbeForUpdate = _ => { localProbed = true; return Task.FromResult<MuxClient?>(null); };
        PumpUntil(() => RemotePanes(window).Count(p => p.Session is MuxClientSession { IsAttached: true }) == 2, "both remote panes reattached");
        PumpUntil(() => AllPanes(window).Any(p => MuxEndpointId.Parse(p.MuxEndpoint).IsLocal && p.Session is MuxClientSession { IsAttached: true }), "the local pane attached");
        PumpUntil(() => Offers(RemoteId).Count == 1, "the notice is raised");

        Offers(RemoteId)[0].Action!.Run();
        PumpUntil(() => Volatile.Read(ref remoteShutdowns) == 1, "the remote daemon was told to shut down");
        PumpUntil(() => !window.IsMuxRestartRunningForTest, "the restart finished");

        Assert.Equal([("nova@fake-host", 2)], asked);
        Assert.Equal(0, Volatile.Read(ref localShutdowns));
        Assert.False(localProbed, "the local daemon was probed to be shut down");
        Assert.True(AllPanes(window).Single(p => MuxEndpointId.Parse(p.MuxEndpoint).IsLocal).Session is MuxClientSession { IsAttached: true }, "the local pane was let go of");
        Assert.Empty(Outcomes);
    }

    /// <summary>
    /// Review round 2, item 6: the remote daemon stops the real way (every shell killed, then the daemon gone, its proxies
    /// exiting 3), with every shell's <c>exited</c> delivered first - the order that reaches panes still attached, which
    /// would show "[SSH session disconnected]". The restart lets that endpoint's panes go of their shells before it sends
    /// <c>shutdown</c>: each shows "[ntilde-mux on host stopped]", as the manual promises, the saved session names none of
    /// the ended shells, and Enter starts a new shell on the daemon the next connect starts.
    /// </summary>
    [AvaloniaFact]
    public void Restarting_a_remote_daemon_leaves_its_panes_on_the_stopped_banner()
    {
        (MainWindow window, FakeRemoteHost remote, Guid[] ids) = TwoPanesOnAPreviousVersion();
        List<MuxClientSession> shown = [.. RemotePanes(window).Select(p => p.Session).OfType<MuxClientSession>()];
        MuxServer oldServer = remote.Server;
        oldServer.ShutdownRequested += () => _ = Task.Run(async () =>
        {
            foreach (Guid id in ids)
            {
                Assert.True(oldServer.TryGetSession(id, out HeadlessTerminalSession? session));
                session!.Kill();
                await session.FlushAsync();
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (shown.Any(s => s.IsAttached && s.IsProcessRunning) && sw.ElapsedMilliseconds < 5_000) await Task.Delay(10);
            oldServer.KillAllSessions();
            remote.StopDaemon();
        });
        window.ConfirmMuxRestart = (_, _) => Task.FromResult(true);

        Offers(RemoteId)[0].Action!.Run();
        PumpUntil(() => !window.IsMuxRestartRunningForTest, "the restart finished");
        for (int i = 0; i < 30; i++) { Thread.Sleep(10); Dispatcher.UIThread.RunJobs(); } // whatever the panes still hear is handled

        List<TerminalPane> panes = RemotePanes(window);
        Assert.Equal(2, panes.Count);
        foreach (TerminalPane pane in panes)
        {
            Assert.Contains(TerminalPane.RemoteDaemonStoppedBanner("nova@fake-host"), Text(pane), StringComparison.Ordinal);
            Assert.DoesNotContain("[SSH session disconnected]", Text(pane), StringComparison.Ordinal);
        }

        string saved = File.ReadAllText(AppPaths.SessionFilePath);
        Assert.All(ids, id => Assert.DoesNotContain(id.ToString("D"), saved, StringComparison.Ordinal));
        Assert.Empty(Outcomes);

        TerminalPane first = panes[0];
        PressEnter(first);
        PumpUntil(() => first.Session is MuxClientSession { IsAttached: true }, "Enter started a new shell");
        Assert.Contains(first.Session!.Id, remote.Server.GetSessionIds()); // the new daemon's
        Assert.NotSame(oldServer, remote.Server);
    }

    /// <summary>
    /// Review round 3, item 3: Enter in a remote pane the restart let go of, while that daemon is still stopping, is held -
    /// nothing starts over the old connection - and the line it writes names ntilde-mux on that host, as its banner does.
    /// Once the old daemon is gone, the held Enter starts a shell on the new one.
    /// </summary>
    [AvaloniaFact]
    public void Enter_during_a_remote_restart_is_held_and_says_ntilde_mux_is_restarting()
    {
        (MainWindow window, FakeRemoteHost remote, _) = TwoPanesOnAPreviousVersion();
        var stop = new TaskCompletionSource();
        MuxServer oldServer = remote.Server;
        int shutdowns = 0;
        oldServer.ShutdownRequested += () =>
        {
            Interlocked.Increment(ref shutdowns);
            _ = Task.Run(async () =>
            {
                await stop.Task;
                remote.StopDaemon();
            });
        };
        window.ConfirmMuxRestart = (_, _) => Task.FromResult(true);

        Offers(RemoteId)[0].Action!.Run();
        PumpUntil(() => Volatile.Read(ref shutdowns) == 1, "shutdown was sent");
        TerminalPane first = RemotePanes(window)[0];
        PressEnter(first);
        for (int i = 0; i < 20; i++) { Thread.Sleep(10); Dispatcher.UIThread.RunJobs(); }

        Assert.True(Shows(first, TerminalPane.RemoteMuxRestartingBanner("nova@fake-host")), $"no remote restarting line: {Text(first)}");
        Assert.False(Shows(first, TerminalPane.MuxRestartingBanner), "the local restarting line on a remote pane");
        Assert.Equal(2, oldServer.GetSessionIds().Count); // nothing started over the old connection
        Assert.Null(first.Session);

        stop.SetResult();
        PumpUntil(() => first.Session is MuxClientSession { IsAttached: true }, "the held Enter started a shell once the restart was over");
        Assert.NotSame(oldServer, remote.Server);
        Assert.Contains(first.Session!.Id, remote.Server.GetSessionIds());
    }

    /// <summary>
    /// Review item 4: the link is down when the button is pressed. Nothing can be sent, the notice says so, and the offer
    /// is released: the next connection to that daemon offers the restart again.
    /// </summary>
    [AvaloniaFact]
    public void A_remote_restart_while_not_connected_says_so_and_is_offered_again()
    {
        (MainWindow window, FakeRemoteHost remote, _) = TwoPanesOnAPreviousVersion();
        MuxConnectionHost host = RemoteHostOf(window)!;
        PersistenceNoticeAction action = Offers(RemoteId)[0].Action!;
        int asked = 0;
        window.ConfirmMuxRestart = (_, _) => { asked++; return Task.FromResult(true); };
        remote.CutLink();
        PumpUntil(() => host.CurrentClient is null, "the link is down");

        action.Run();
        PumpUntil(() => !window.IsMuxRestartRunningForTest, "the restart finished");
        Assert.Equal([MuxPreviousBuildNotice.RestartOutcome.NotConnected], Outcomes);
        Assert.Equal(0, asked);

        PumpUntil(() =>
        {
            if (host.IsReconnecting) _clock.Advance(FirstRetry);
            return host.CurrentClient is not null;
        }, "the host reconnected");
        PumpUntil(() => Offers(RemoteId).Count == 2, "offered again");
    }

    /// <summary>
    /// Review item 4: <c>shutdown</c> could not be sent (here it times out). The notice says so, the daemon keeps running,
    /// and the offer is released: the next connection offers the restart again.
    /// </summary>
    [AvaloniaFact]
    public void A_remote_restart_whose_shutdown_fails_says_so_and_is_offered_again()
    {
        (MainWindow window, FakeRemoteHost remote, Guid[] ids) = TwoPanesOnAPreviousVersion();
        MuxConnectionHost host = RemoteHostOf(window)!;
        window.ConfirmMuxRestart = (_, _) => Task.FromResult(true);
        window.MuxShutdownRemoteDaemon = (_, _) => Task.FromException(new TimeoutException("timed out"));

        Offers(RemoteId)[0].Action!.Run();
        PumpUntil(() => !window.IsMuxRestartRunningForTest, "the restart finished");
        Assert.Equal([MuxPreviousBuildNotice.RestartOutcome.ShutdownFailed], Outcomes);
        Assert.All(ids, id => Assert.Contains(id, remote.Server.GetSessionIds()));

        Reconnect(remote, host);
        PumpUntil(() => Offers(RemoteId).Count == 2, "offered again");
    }

    /// <summary>
    /// Review item 5: two profiles of one <c>user@host</c> on two daemons give two notices of the same words, each keyed by
    /// its endpoint (so the toast never merges them into one line with one button: see
    /// <c>MainWindowNoticeActionTests.Notices_of_the_same_words_with_their_own_keys_keep_a_line_each</c>), and each one's
    /// action stops its own daemon only.
    /// </summary>
    [AvaloniaFact]
    public void Two_hosts_of_one_name_get_a_notice_each_and_each_button_stops_its_own()
    {
        FakeRemoteHost first = PreviousVersionHost();
        FakeRemoteHost second = PreviousVersionHost();
        SshProfile twin = RemoteMuxConnectorTests.Profile();
        twin.BackendKind = SshBackendKind.Native;
        new JsonSshProfileStore().SaveProfile(twin);
        _twin = (twin, second);
        Guid[] onFirst = SpawnOnRemote(2, first);
        Guid[] onSecond = SpawnOnRemote(2, second);
        string twinEndpoint = MuxEndpointId.ForSsh(twin.Id).ToString();
        SaveSession(
            RemoteLeaf(onFirst[0]),
            new PaneNode { Type = NodeType.Leaf, SshProfileId = twin.Id.ToString(), MuxSessionId = onSecond[0].ToString("D"), MuxEndpoint = twinEndpoint });
        int firstShutdowns = 0;
        int secondShutdowns = 0;
        first.Server.ShutdownRequested += () => { Interlocked.Increment(ref firstShutdowns); _ = Task.Run(first.StopDaemon); };
        second.Server.ShutdownRequested += () => { Interlocked.Increment(ref secondShutdowns); _ = Task.Run(second.StopDaemon); };
        MainWindow window = CreateRestartWindow(first);
        window.ConfirmMuxRestart = (_, _) => Task.FromResult(true);
        MuxEndpointId twinId = MuxEndpointId.ForSsh(twin.Id);
        PumpUntilDecided(window, RemoteId, 1);
        PumpUntilDecided(window, twinId, 1);

        List<MuxPreviousBuildNotice.Raised> offers = [.. Offers(RemoteId), .. Offers(twinId)];
        Assert.Equal(2, offers.Count);
        Assert.All(offers, o => Assert.Equal(TwoShellsNotice, o.Message)); // the same words for both...
        Assert.NotEqual(offers[0].Key, offers[1].Key);                     // ...under keys of their own

        foreach (MuxPreviousBuildNotice.Raised offer in offers)
        {
            (int First, int Second) before = (Volatile.Read(ref firstShutdowns), Volatile.Read(ref secondShutdowns));
            offer.Action!.Run();
            PumpUntil(() => Volatile.Read(ref firstShutdowns) + Volatile.Read(ref secondShutdowns) == before.First + before.Second + 1, "one daemon was told to shut down");
            PumpUntil(() => !window.IsMuxRestartRunningForTest, "the restart finished");
            Assert.Equal(
                offer.Endpoint == RemoteId ? (before.First + 1, before.Second) : (before.First, before.Second + 1),
                (Volatile.Read(ref firstShutdowns), Volatile.Read(ref secondShutdowns)));
        }
    }
}
