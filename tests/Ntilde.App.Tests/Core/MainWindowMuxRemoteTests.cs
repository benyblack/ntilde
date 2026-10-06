using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Controls;
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

namespace Ntilde.Tests.Core;

/// <summary>
/// The window's side of persisted remote panes (Phase 4 spec §5, §7.6, §8.4): a restored session file
/// whose SSH panes name sessions on their profile's remote daemon - a <see cref="FakeRemoteHost"/> on a clock
/// the test advances - reattaches them over one connection, a remote tab closed while its link is down still
/// ends its shell, the window's lists of "its" daemon sessions mean the local daemon only, and the remote-files
/// sidebar is not offered on such a tab.
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
        _remote.Dispose();
        _localMux.Dispose();
        new JsonSshProfileStore().DeleteProfile(_sshProfile.Id);
    }

    private string Endpoint => MuxEndpointId.ForSsh(_sshProfile.Id).ToString();

    private SshProfile? Resolve(Guid id) => id == _sshProfile.Id ? _sshProfile : null;

    private int _uiThread;
    private int _factoryCallsOffUi; // CreatePersistent resolves the profile once, first: one per remote factory call

    /// <summary>The factory's own lookup, counting the calls made off the UI thread (the panes' remote factory calls).</summary>
    private SshProfile? FactoryResolve(Guid id)
    {
        if (Environment.CurrentManagedThreadId != Volatile.Read(ref _uiThread)) Interlocked.Increment(ref _factoryCallsOffUi);
        return Resolve(id);
    }

    /// <summary>The app's wiring over the test's daemons: the local one in memory, the remote one behind <see cref="_remote"/>.</summary>
    private MainWindow CreateWindow()
    {
        Volatile.Write(ref _uiThread, Environment.CurrentManagedThreadId);
        _local = new MuxConnectionHost(ct => MuxClient.ConnectAsync(_localMux.Listener.Connect(), null, ct), "test", null)
        {
            DisposeFlushTimeout = TimeSpan.FromSeconds(1),
        };
        var hosts = _hosts = new MuxConnectionHosts(_local, id => RemoteMuxHostFactory.Create(id, Resolve, (_, _) => _remote, log: null, userPrompts: null, scheduler: _clock));
        var factory = new MuxTerminalSessionFactory(hosts, new RecordingSessionFactory(new FakeTerminalSession()), FactoryResolve, null);
        MainWindow window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            CommandAssist = TestCommandAssistServices.Instance,
            SessionFactory = factory,
        });
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
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
    private void KillLands(MuxConnectionHost host, Guid id, string because) => PumpUntil(() =>
    {
        if (host.IsReconnecting) _clock.Advance(FirstRetry);
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
    private Guid[] SpawnOnRemote(int count) => Task.Run(async () =>
    {
        (Stream stream, _) = await _remote.ConnectDaemonAsync(TestContext.Current.CancellationToken);
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

    private static (bool Visible, string? Title, string? Message) Toast(MainWindow window) =>
        (window.FindControl<Border>("RecordingToast")!.IsVisible,
         window.FindControl<TextBlock>("RecordingToastTitle")!.Text,
         window.FindControl<TextBlock>("RecordingToastMessage")!.Text);

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

        KillLands(RemoteHostOf(window)!, ids[0], "the kill reached the daemon");
        Assert.Equal(1, _remote.StartCount); // the host connected for it, once
        Assert.Contains(ids[1], _remote.Server.GetSessionIds());
    }

    /// <summary>
    /// Final review F2: "Attach to session…" lists local shells only, so a detached remote shell is told to come
    /// back through ntilde-mux on its own host - named as the banners name it - by the id ntilde-mux ls shows.
    /// (The local text is pinned by MainWindowMuxSharingTests.Detach_pane_keeps_the_shell_running_and_the_count_drops.)
    /// </summary>
    [AvaloniaFact]
    public void Detaching_a_remote_pane_names_its_host_and_the_command_that_gets_it_back()
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
        Assert.Equal($"Shell kept running on nova@fake-host — run 'ntilde-mux attach {ids[0]}' on that host to get it back", Toast(window).Message);
        Assert.Contains(ids[0], _remote.Server.GetSessionIds());
    }

    /// <summary>Spec §8.4: no native SSH session stands behind a persisted remote tab, so the sidebar has nothing to browse.</summary>
    [AvaloniaFact]
    public void Remote_files_sidebar_is_unavailable_on_a_persistent_remote_tab()
    {
        Guid[] ids = SpawnOnRemote(1);
        SaveSession(RemoteLeaf(ids[0]));
        MainWindow window = CreateWindow();
        PumpUntil(() => RemotePanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true }), "the remote pane reattached");
        TerminalPane pane = RemotePanes(window).Single();
        Assert.True(pane.IsPersistentRemoteTab);
        Assert.False(ActiveSshSessionRegistry.Instance.TryGet(pane.Session!.Id, out _));

        pane.ToggleRemoteFilesSidebar();

        PumpUntil(() => Toast(window).Message == TerminalPane.RemoteFilesUnavailableMessage, "the toast says why");
        Assert.True(Toast(window).Visible);
        Assert.False(pane.IsRemoteFilesSidebarVisibleForTest());

        // A transfer request from such a pane (the sidebar's buttons) gets the same answer and nothing else.
        typeof(MainWindow).GetMethod("HideRecordingToast", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
        Assert.False(Toast(window).Visible);
        typeof(MainWindow).GetMethod("OnPaneRequestRemoteFilesSidebarTransfer", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, [pane, new SidebarTransferRequest(TransferDirection.Download, TransferKind.File, "/etc/hosts")]);
        PumpUntil(() => Toast(window).Visible, "the toast is shown again");
        Assert.Equal(TerminalPane.RemoteFilesUnavailableMessage, Toast(window).Message);
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

        IReadOnlyList<MuxSessionPickerRow>? offered = null;
        window.PickMuxSession = rows =>
        {
            offered = rows;
            return Task.FromResult<Guid?>(shared);
        };
        Task command = window.AttachToMuxSessionAsync();
        PumpUntil(() => command.IsCompleted, "the attach command finished");

        Assert.False(Assert.Single(offered!, r => r.SessionId == shared).OpenHere);
        PumpUntil(() => AllPanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true } m && m.Id == shared && MuxEndpointId.Parse(p.MuxEndpoint).IsLocal),
            "the local session opened in a new tab");
    }
}
