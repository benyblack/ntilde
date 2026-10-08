using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Daemon;
using Ntilde.Mux.Tests.Support;
using Ntilde.Pty;
using Ntilde.Shell;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Controls; // FakeTerminalSession, RecordingSessionFactory
using Ntilde.Tests.Shell.Mux; // MuxTestText
using static Ntilde.Tests.Core.WindowToast;
using RestartOutcome = Ntilde.Shell.Mux.MuxPreviousBuildNotice.RestartOutcome;

namespace Ntilde.Tests.Core;

/// <summary>
/// Phase 5 Task 23: an update now keeps a compatible daemon running (R9, R10), so the new build can find the previous
/// build's daemon still serving its shells. The window says so once per launch, offering "Restart multiplexer now":
/// asked, then <c>shutdown</c> and up to 5 s for the exit, then by pid as <c>kill-server --force</c> does, then a warm-up
/// that starts a daemon of this build. A daemon of another protocol version - the "Session not persistent" fallback -
/// gets the same action next to its hint.
/// </summary>
/// <remarks>
/// The window's daemon is an in-memory one: <see cref="_old"/> (reporting <see cref="PreviousBuild"/>) until it is told
/// to shut down, then <see cref="_current"/>, this build's - what the launcher's spawn gives, which the host's connect
/// counts (<see cref="_connects"/>). The old daemon stops as the real one does (<c>MuxDaemonHost.RequestStop</c>): it kills
/// every session, then closes. The window's multiplexer notices are read from <see cref="_notices"/>, never from "the"
/// toast, which other notices share (on a machine with no keychain, the credential notice). <see cref="TestAppDataRoot"/>
/// is taken for its lifetime: the window restores and saves its session there.
/// </remarks>
public sealed class MainWindowMuxUpdateTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    private const string ThisBuild = "0.12.0";
    private const string PreviousBuild = "0.0.1";

    private static readonly MuxEndpointDescriptor OldDaemon = new() { Endpoint = "test", Pid = 4242, ProcessName = "Ntilde", MinVersion = 1, MaxVersion = 2 };
    private static readonly MuxEndpointId Local = MuxEndpointId.Local;

    private readonly MuxTestHost _old = new(new MuxServerOptions { ForceConPtyFiltering = false, AppVersion = PreviousBuild });
    private readonly MuxTestHost _current = new(new MuxServerOptions { ForceConPtyFiltering = false, AppVersion = ThisBuild });
    private readonly TaskCompletionSource<bool> _oldExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<MuxConnectionHost> _hosts = [];

    /// <summary>Every multiplexer notice the window raised (UI thread): offers, and restarts' outcomes.</summary>
    private readonly List<MuxPreviousBuildNotice.Raised> _notices = [];

    private volatile bool _oldStopped;
    private int _connects;
    private int _oldShutdowns;

    /// <summary>
    /// Run first when the old daemon is told to shut down, before it stops as the real one does: a test can deliver every
    /// shell's <c>exited</c> first - the order that reaches panes still attached - or hold the stop.
    /// </summary>
    private Func<Task>? _beforeOldStops;

    /// <summary>False: the old daemon answers <c>shutdown</c> but does not stop (a daemon that hangs on its way out).</summary>
    private volatile bool _oldStopsOnShutdown = true;

    public MainWindowMuxUpdateTests()
    {
        if (File.Exists(AppPaths.SessionFilePath)) File.Delete(AppPaths.SessionFilePath);
        _old.Server.ShutdownRequested += () =>
        {
            Interlocked.Increment(ref _oldShutdowns);
            if (!_oldStopsOnShutdown) return;
            _ = Task.Run(async () =>
            {
                if (_beforeOldStops is { } before) await before();
                _oldStopped = true;
                // MuxDaemonHost.RequestStop's order: every shell is killed, then the server closes every connection.
                _old.Server.KillAllSessions();
                _old.Server.Dispose();
                _oldExited.TrySetResult(true);
            });
        };
    }

    public void Dispose()
    {
        TestMainWindowFactory.DisposeCreatedWindows();
        foreach (MuxConnectionHost host in _hosts) host.Dispose();
        _old.Dispose();
        _current.Dispose();
    }

    private static void PumpUntil(Func<bool> condition, string because, int ms = 10_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > ms) Assert.Fail($"Timed out: {because}");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// A window whose local daemon is <see cref="_old"/> until it stops, then <see cref="_current"/>; persistence on unless
    /// <paramref name="settings"/> says otherwise. <paramref name="connect"/> replaces the host's connect.
    /// </summary>
    private MainWindow CreateWindow(TerminalSettings? settings = null, Func<CancellationToken, Task<MuxClient>>? connect = null)
    {
        var host = new MuxConnectionHost(connect ?? (ct =>
        {
            Interlocked.Increment(ref _connects);
            return MuxClient.ConnectAsync((_oldStopped ? _current : _old).Listener.Connect(), null, ct);
        }), "test", null);
        _hosts.Add(host);
        var factory = new MuxTerminalSessionFactory(
            new MuxConnectionHosts(host, _ => null), new RecordingSessionFactory(new FakeTerminalSession()), null);
        MainWindow window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            CommandAssist = TestCommandAssistServices.Instance,
            SessionFactory = factory,
            Settings = settings ?? new TerminalSettings { SessionPersistence = SessionPersistenceMode.KeepOnClose },
        });
        window.MuxThisBuildVersion = ThisBuild;
        window.MuxPreviousBuildLaunch = new MuxPreviousBuildNotice.Launch(); // this test's launch
        window.MuxNoticeRaisedForTest = _notices.Add;
        window.MuxProbeForUpdate = async ct => _oldStopped ? await MuxClient.ConnectAsync(_current.Listener.Connect(), null, ct) : await MuxClient.ConnectAsync(_old.Listener.Connect(), null, ct);
        window.MuxReadDescriptorForUpdate = () => OldDaemon;
        window.MuxWaitForDaemonExitForUpdate = _ => _oldExited.Task;
        window.Show();
        return window;
    }

    private static TerminalSettings ClosesOnExit => new() { SessionPersistence = SessionPersistenceMode.KeepOnClose, ShellExitPolicy = "Always" };

    /// <summary>Three shells running in the old daemon, and a session file whose one tab shows them side by side.</summary>
    private Guid[] ThreePanesOnTheOldDaemon()
    {
        Guid[] ids = Task.Run(async () =>
        {
            MuxClient client = await _old.ConnectClientAsync();
            return new[] { await MuxTestHost.SpawnAsync(client), await MuxTestHost.SpawnAsync(client), await MuxTestHost.SpawnAsync(client) };
        }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        var split = new PaneNode { Type = NodeType.Split, SplitOrientation = 0, Children = [.. ids.Select(LocalLeaf)] };
        var session = new NtildeSession { ActiveTabIndex = 0 };
        session.Tabs.Add(new TabSession { Title = "three", Root = split });
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.SessionFilePath)!);
        File.WriteAllText(AppPaths.SessionFilePath, JsonSerializer.Serialize(session, SessionSerializationContext.Default.NtildeSession));
        return ids;
    }

    private static PaneNode LocalLeaf(Guid id) => new()
    {
        Type = NodeType.Leaf,
        Command = "scripted",
        MuxSessionId = id.ToString("D"),
        MuxEndpoint = MuxEndpointId.Local.ToString(),
    };

    private static List<MuxClientSession> Attached(MainWindow window) =>
        window.AllPanesForTest().Select(p => p.Session).OfType<MuxClientSession>().Where(s => s.IsAttached).ToList();

    private static readonly string ThreeShellsNotice = MuxPreviousBuildNotice.LocalMessage(PreviousBuild, ThisBuild, 3);

    /// <summary>The local daemon's offers so far (outcomes left out).</summary>
    private List<MuxPreviousBuildNotice.Raised> LocalOffers => [.. _notices.Where(n => n.Endpoint == Local && n.Outcome is null)];

    /// <summary>The restarts' outcomes raised so far.</summary>
    private List<RestartOutcome> Outcomes => [.. _notices.Where(n => n.Outcome is not null).Select(n => n.Outcome!.Value)];

    /// <summary>Waits for the window's three panes to reattach their shells, and for the notice that names them; returns its action.</summary>
    private PersistenceNoticeAction PumpUntilOffered(MainWindow window)
    {
        PumpUntil(() => Attached(window).Count == 3, "the three panes reattached");
        PumpUntil(() => LocalOffers.Count == 1, "the notice is raised");
        MuxPreviousBuildNotice.Raised offer = LocalOffers[0];
        Assert.Equal(ThreeShellsNotice, offer.Message);
        return offer.Action!;
    }

    /// <summary>
    /// One connection, three panes: one notice, naming the daemon's version and its three shells, with the restart - on
    /// the toast, as a line of its own with its button. The panes share the host's connection, so it is offered once, not
    /// per pane - and not again when the connection drops and comes back to the same daemon.
    /// </summary>
    [AvaloniaFact]
    public void A_daemon_of_the_previous_build_is_offered_a_restart_once()
    {
        ThreePanesOnTheOldDaemon();
        MainWindow window = CreateWindow();
        MuxConnectionHost host = _hosts.Single();

        PumpUntilOffered(window);
        PumpUntil(() => ToastLines(window).Contains(ThreeShellsNotice), "the toast shows the notice");

        MuxPreviousBuildNotice.Raised offer = Assert.Single(LocalOffers);
        Assert.Equal("Restart multiplexer now", offer.Action!.Label);
        Assert.Equal($"{MuxPreviousBuildNotice.Title}\n{Local}", offer.Key);
        Assert.True(ToastButton(window, "RecordingToastAction").IsVisible);
        Assert.Equal("Restart multiplexer now", ToastButton(window, "RecordingToastAction").Content);
        Assert.Equal(1, Volatile.Read(ref _connects));

        // Dismissed, then the connection drops and comes back to the same old daemon: nothing more is offered.
        Click(ToastButton(window, "RecordingToastClose"));
        MuxClient first = host.CurrentClient!;
        first.Dispose();
        host.WarmUp();
        PumpUntilDecided(window, Local, 2);

        Assert.Equal(2, Volatile.Read(ref _connects));
        Assert.Equal(PreviousBuild, host.CurrentClient!.ServerVersion);
        Assert.Single(_notices); // the one offer: none again, and no outcome
    }

    /// <summary>A daemon of this very build is what the window wants: nothing to say.</summary>
    [AvaloniaFact]
    public void A_daemon_of_this_build_gives_no_notice()
    {
        _oldStopped = true; // the daemon is this build's from the start
        MainWindow window = CreateWindow();
        MuxConnectionHost host = _hosts.Single();
        PumpUntilDecided(window, Local, 1);
        // Another notice on the toast is not this test's business: CI's ubuntu runner has no keychain, and says so there.
        window.EnqueueNotice("Credential storage unavailable", "No system keychain was found, so SSH passwords won't be saved this session.");
        Dispatcher.UIThread.RunJobs();
        Assert.True(Toast(window).Visible);

        Assert.Equal(ThisBuild, host.CurrentClient!.ServerVersion);
        Assert.Empty(_notices);
        Assert.DoesNotContain(ToastLines(window), l => l.StartsWith("The multiplexer", StringComparison.Ordinal));
    }

    /// <summary>With SessionPersistence Off nothing new appears, whatever the daemon the window's panes still use reports.</summary>
    [AvaloniaFact]
    public void With_persistence_off_no_notice_is_shown()
    {
        ThreePanesOnTheOldDaemon();
        MainWindow window = CreateWindow(new TerminalSettings { SessionPersistence = SessionPersistenceMode.Off });
        MuxConnectionHost host = _hosts.Single();
        PumpUntilDecided(window, Local, 1);

        Assert.Equal(PreviousBuild, host.CurrentClient!.ServerVersion);
        Assert.Empty(_notices);
        Assert.DoesNotContain(ThreeShellsNotice, ToastLines(window));
    }

    /// <summary>
    /// Review item 6, against the real shutdown (every shell killed, then the server closed) and a close-on-exit policy.
    /// The restart lets this window's panes go of their shells before it sends <c>shutdown</c>, so none takes the exit
    /// path: every pane stays, shows "multiplexer disconnected", and Enter starts a new shell on the new daemon. The
    /// saved session keeps the layout without naming the shells the restart ended. <paramref name="exitsFirst"/>: every
    /// shell's <c>exited</c> is delivered before the server closes - the order in which a pane still attached would
    /// take the exit path - rather than left to the race of the real stop.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restarting_keeps_every_pane_and_enter_starts_a_shell_on_the_new_daemon(bool exitsFirst)
    {
        Guid[] old = ThreePanesOnTheOldDaemon();
        MainWindow window = CreateWindow(ClosesOnExit);
        MuxConnectionHost host = _hosts.Single();
        PersistenceNoticeAction restart = PumpUntilOffered(window);
        List<MuxClientSession> shown = Attached(window);
        if (exitsFirst)
        {
            _beforeOldStops = async () =>
            {
                foreach (Guid id in old)
                {
                    _old.Mux(id).Kill();
                    await _old.Mux(id).FlushAsync();
                }

                // Until every pane's session has heard its exit, or no longer listens for it.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (shown.Any(s => s.IsAttached && s.IsProcessRunning) && sw.ElapsedMilliseconds < 5_000) await Task.Delay(10);
            };
        }

        var asked = new List<(string? Host, int Shells)>();
        window.ConfirmMuxRestart = (where, shells) => { asked.Add((where, shells)); return Task.FromResult(true); };
        var terminated = new List<MuxEndpointDescriptor>();
        window.MuxTerminateDaemon = d => { lock (terminated) terminated.Add(d); return true; };

        restart.Run();
        PumpUntil(() => host.CurrentClient is { ServerVersion: ThisBuild }, "the host connected to a daemon of this build");
        PumpUntilDecided(window, Local, 2);
        PumpUntil(() => !window.IsMuxRestartRunningForTest, "the restart finished");
        for (int i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); } // any exit a pane still heard is handled

        Assert.Equal([(null, 3)], asked);
        Assert.Equal(1, Volatile.Read(ref _oldShutdowns));
        Assert.Equal(2, Volatile.Read(ref _connects));
        lock (terminated) Assert.Empty(terminated); // it exited on its own
        Assert.Single(_notices); // the offer: no outcome, and no offer for the new daemon
        IReadOnlyList<TerminalPane> after = window.AllPanesForTest();
        Assert.Equal(3, after.Count);
        Assert.Equal(1, window.FindControl<TabControl>("Tabs")!.ItemCount);
        foreach (TerminalPane pane in after)
        {
            Assert.True(Shows(pane, TerminalPane.MuxDisconnectedBanner), $"no disconnected banner: {MuxTestText.VisibleText(pane.Buffer!)}");
            Assert.False(pane.Session is MuxClientSession { IsAttached: true }, "a pane is still on the old daemon");
        }

        // The session saved for the restart keeps the tab and its three panes, and names none of the ended shells.
        string saved = File.ReadAllText(AppPaths.SessionFilePath);
        NtildeSession file = JsonSerializer.Deserialize(saved, SessionSerializationContext.Default.NtildeSession)!;
        Assert.Equal(3, Assert.Single(file.Tabs).Root!.Children.Count);
        Assert.All(old, id => Assert.DoesNotContain(id.ToString("D"), saved, StringComparison.Ordinal));

        // Enter in a pane starts a new shell, on the new daemon.
        TerminalPane first = after[0];
        PressEnter(first);
        PumpUntil(() => first.Session is MuxClientSession { IsAttached: true }, "the pane started a new shell");
        Assert.Contains(first.Session!.Id, _current.Server.GetSessionIds());
        Assert.Equal(3, window.AllPanesForTest().Count);
    }

    /// <summary>
    /// Review M1: until the old daemon is gone, the host still holds its connection, so Enter in a pane the restart let go
    /// must not start a shell there (it would be killed with the rest, and close the pane under a close-on-exit policy).
    /// Enter is held, the pane says so, and its shell starts on the new daemon once the restart is over.
    /// </summary>
    [AvaloniaFact]
    public void Enter_during_a_restart_waits_for_the_new_daemon()
    {
        ThreePanesOnTheOldDaemon();
        MainWindow window = CreateWindow(ClosesOnExit);
        PersistenceNoticeAction restart = PumpUntilOffered(window);
        var stop = new TaskCompletionSource();
        _beforeOldStops = () => stop.Task; // the old daemon holds its stop until the test lets it go
        window.ConfirmMuxRestart = (_, _) => Task.FromResult(true);

        restart.Run();
        PumpUntil(() => Volatile.Read(ref _oldShutdowns) == 1, "shutdown was sent");
        TerminalPane first = window.AllPanesForTest()[0];
        Assert.True(Shows(first, TerminalPane.MuxDisconnectedBanner));
        PressEnter(first);
        for (int i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }

        Assert.Equal(3, _old.Server.GetSessionIds().Count); // nothing started on the old daemon
        Assert.Null(first.Session);
        Assert.True(Shows(first, TerminalPane.MuxRestartingBanner), $"no restarting line: {MuxTestText.VisibleText(first.Buffer!)}");

        stop.SetResult();
        PumpUntil(() => first.Session is MuxClientSession { IsAttached: true }, "the held Enter started a shell once the restart was over");
        Assert.Contains(first.Session!.Id, _current.Server.GetSessionIds());
        Assert.Equal(3, window.AllPanesForTest().Count);
    }

    /// <summary>One tab for each of <paramref name="count"/> shells running in the old daemon, the first selected.</summary>
    private Guid[] TabsOnTheOldDaemon(int count)
    {
        Guid[] ids = Task.Run(async () =>
        {
            MuxClient client = await _old.ConnectClientAsync();
            var spawned = new Guid[count];
            for (int i = 0; i < count; i++) spawned[i] = await MuxTestHost.SpawnAsync(client);
            return spawned;
        }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        var session = new NtildeSession { ActiveTabIndex = 0 };
        foreach (Guid id in ids) session.Tabs.Add(new TabSession { Title = $"tab {session.Tabs.Count}", Root = LocalLeaf(id) });
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.SessionFilePath)!);
        File.WriteAllText(AppPaths.SessionFilePath, JsonSerializer.Serialize(session, SessionSerializationContext.Default.NtildeSession));
        return ids;
    }

    private static void Pump(int times = 20)
    {
        for (int i = 0; i < times; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
    }

    /// <summary>
    /// Review round 3, item 1: a pane the restart let go of starts a shell only through its Enter, as its banner says -
    /// never because it is shown again (a tab switch re-attaches it: the attach fallback), during the restart (when the
    /// host still holds the old daemon's connection) or after it; and the palette's "Pane: Reconnect" is held like Enter.
    /// </summary>
    [AvaloniaFact]
    public void A_released_pane_starts_a_shell_only_through_enter_however_it_is_shown_or_reconnected()
    {
        Guid[] old = TabsOnTheOldDaemon(2);
        MainWindow window = CreateWindow(ClosesOnExit);
        TabControl tabs = window.FindControl<TabControl>("Tabs")!;
        PumpUntil(() => Attached(window).Count == 1, "the first tab's pane attached");
        tabs.SelectedIndex = 1;
        PumpUntil(() => Attached(window).Count == 2, "the second tab's pane attached");
        tabs.SelectedIndex = 0;
        PumpUntil(() => LocalOffers.Count == 1, "the notice is raised");
        TerminalPane first = window.AllPanesForTest().Single(p => p.Session?.Id == old[0]);
        TerminalPane second = window.AllPanesForTest().Single(p => p.Session?.Id == old[1]);
        var stop = new TaskCompletionSource();
        _beforeOldStops = () => stop.Task; // the old daemon holds its stop until the test lets it go
        window.ConfirmMuxRestart = (_, _) => Task.FromResult(true);

        LocalOffers[0].Action!.Run();
        PumpUntil(() => Volatile.Read(ref _oldShutdowns) == 1, "shutdown was sent");

        // During the restart: each tab shown again re-attaches its pane, and nothing starts over the old connection.
        tabs.SelectedIndex = 1;
        Pump();
        tabs.SelectedIndex = 0;
        Pump();
        Assert.Equal(2, _old.Server.GetSessionIds().Count);
        Assert.Null(first.Session);
        Assert.Null(second.Session);

        // The palette's "Pane: Reconnect" is held as Enter is.
        first.Reconnect();
        Pump();
        Assert.Equal(2, _old.Server.GetSessionIds().Count);
        Assert.Null(first.Session);
        Assert.True(Shows(first, TerminalPane.MuxRestartingBanner), $"no restarting line: {MuxTestText.VisibleText(first.Buffer!)}");

        stop.SetResult();
        PumpUntil(() => first.Session is MuxClientSession { IsAttached: true }, "the held reconnect ran once the restart was over");
        Assert.Contains(first.Session!.Id, _current.Server.GetSessionIds());
        PumpUntil(() => !window.IsMuxRestartRunningForTest, "the restart finished");

        // After it: the other pane, shown again, waits for Enter.
        tabs.SelectedIndex = 1;
        Pump();
        Assert.Null(second.Session);
        Assert.True(Shows(second, TerminalPane.MuxDisconnectedBanner));
        PressEnter(second);
        PumpUntil(() => second.Session is MuxClientSession { IsAttached: true }, "Enter started a shell");
        Assert.Contains(second.Session!.Id, _current.Server.GetSessionIds());
        Assert.Equal(2, window.AllPanesForTest().Count);
    }

    /// <summary>
    /// Review item 7: <c>shutdown</c> is answered but the daemon is still there when the exit wait runs out, so it is
    /// terminated by pid (as <c>kill-server --force</c> does) with the descriptor read before the shutdown; then the host
    /// connects to a new daemon.
    /// </summary>
    [AvaloniaFact]
    public void A_daemon_that_does_not_exit_after_shutdown_is_terminated_by_pid()
    {
        ThreePanesOnTheOldDaemon();
        MainWindow window = CreateWindow();
        MuxConnectionHost host = _hosts.Single();
        PersistenceNoticeAction restart = PumpUntilOffered(window);
        _oldStopsOnShutdown = false;
        window.ConfirmMuxRestart = (_, _) => Task.FromResult(true);
        window.MuxWaitForDaemonExitForUpdate = _ => Task.FromResult(false);
        var terminated = new List<MuxEndpointDescriptor>();
        window.MuxTerminateDaemon = d =>
        {
            lock (terminated) terminated.Add(d);
            _oldStopped = true;
            _old.Server.KillAllSessions();
            _old.Server.Dispose();
            return true;
        };

        restart.Run();
        PumpUntil(() => host.CurrentClient is { ServerVersion: ThisBuild }, "the host connected to a daemon of this build");
        PumpUntil(() => !window.IsMuxRestartRunningForTest, "the restart finished");

        Assert.Equal(1, Volatile.Read(ref _oldShutdowns));
        lock (terminated) Assert.Same(OldDaemon, Assert.Single(terminated));
        Assert.Equal(2, Volatile.Read(ref _connects));
        Assert.Empty(Outcomes);
    }

    /// <summary>Review item 4: a daemon that neither exits nor can be terminated is reported, and can be offered again later.</summary>
    [AvaloniaFact]
    public void A_daemon_that_cannot_be_stopped_is_reported()
    {
        ThreePanesOnTheOldDaemon();
        MainWindow window = CreateWindow();
        PersistenceNoticeAction restart = PumpUntilOffered(window);
        _oldStopsOnShutdown = false;
        window.ConfirmMuxRestart = (_, _) => Task.FromResult(true);
        window.MuxWaitForDaemonExitForUpdate = _ => Task.FromResult(false);
        window.MuxTerminateDaemon = _ => false;

        restart.Run();
        PumpUntil(() => !window.IsMuxRestartRunningForTest, "the restart finished");

        Assert.Equal([RestartOutcome.NotStopped], Outcomes);
        Assert.True(window.MuxPreviousBuildLaunch.TryOffer(Local), "the offer was not released");
    }

    /// <summary>
    /// Review M2: when the daemon could not be stopped its shells keep running, and they are shells like any other - one
    /// the user reopens with "Attach to session…" is saved for the next launch, not left out as ended.
    /// </summary>
    [AvaloniaFact]
    public void Shells_a_restart_could_not_stop_are_saved_when_reopened()
    {
        Guid[] ids = ThreePanesOnTheOldDaemon();
        MainWindow window = CreateWindow();
        PersistenceNoticeAction restart = PumpUntilOffered(window);
        _oldStopsOnShutdown = false;
        window.ConfirmMuxRestart = (_, _) => Task.FromResult(true);
        window.MuxWaitForDaemonExitForUpdate = _ => Task.FromResult(false);
        window.MuxTerminateDaemon = _ => false;
        restart.Run();
        PumpUntil(() => !window.IsMuxRestartRunningForTest, "the restart finished");
        Assert.Equal([RestartOutcome.NotStopped], Outcomes);
        Assert.All(ids, id => Assert.False(_old.Mux(id).IsExited)); // still running
        File.Delete(AppPaths.SessionFilePath);

        window.PickMuxSession = _ => Task.FromResult<Guid?>(ids[0]);
        Task attach = window.AttachToMuxSessionAsync();
        PumpUntil(() => attach.IsCompleted && Attached(window).Any(s => s.Id == ids[0]), "the shell was reopened");
        PumpUntil(() => File.Exists(AppPaths.SessionFilePath) && File.ReadAllText(AppPaths.SessionFilePath).Contains(ids[0].ToString("D"), StringComparison.Ordinal),
            "the reopened shell is saved");
    }

    /// <summary>
    /// Review M3: the last look before <c>shutdown</c> finds a daemon advertised but not answering: it is left alone, and
    /// the notice says it could not be reached - not that it is not running.
    /// </summary>
    [AvaloniaFact]
    public void A_daemon_that_cannot_be_reached_at_the_last_look_is_left_alone()
    {
        ThreePanesOnTheOldDaemon();
        MainWindow window = CreateWindow();
        PersistenceNoticeAction restart = PumpUntilOffered(window);
        window.ConfirmMuxRestart = (_, _) =>
        {
            window.MuxProbeForUpdate = _ => Task.FromResult<MuxClient?>(null); // advertised (the descriptor stays), not answering
            return Task.FromResult(true);
        };
        int terminated = 0;
        window.MuxTerminateDaemon = _ => { Interlocked.Increment(ref terminated); return true; };

        restart.Run();
        PumpUntil(() => !window.IsMuxRestartRunningForTest, "the restart finished");

        Assert.Equal([RestartOutcome.Unreachable], Outcomes);
        Assert.Equal(0, Volatile.Read(ref _oldShutdowns));
        Assert.Equal(0, Volatile.Read(ref terminated));
        Assert.Equal(3, Attached(window).Count); // not let go of
    }

    /// <summary>Review M3: no descriptor and nothing answering at the last look: "not running", and nothing is done.</summary>
    [AvaloniaFact]
    public void No_daemon_at_the_last_look_is_reported_as_not_running()
    {
        ThreePanesOnTheOldDaemon();
        MainWindow window = CreateWindow();
        PersistenceNoticeAction restart = PumpUntilOffered(window);
        window.ConfirmMuxRestart = (_, _) =>
        {
            window.MuxProbeForUpdate = _ => Task.FromResult<MuxClient?>(null);
            window.MuxReadDescriptorForUpdate = () => null;
            return Task.FromResult(true);
        };

        restart.Run();
        PumpUntil(() => !window.IsMuxRestartRunningForTest, "the restart finished");

        Assert.Equal([RestartOutcome.NotRunning], Outcomes);
        Assert.Equal(0, Volatile.Read(ref _oldShutdowns));
        Assert.Equal(3, Attached(window).Count);
    }

    /// <summary>
    /// Review M3: <c>shutdown</c> went out but no descriptor says which process to watch, so whether the daemon stopped
    /// cannot be checked - said so, rather than "could not be stopped"; the restart still connects again.
    /// </summary>
    [AvaloniaFact]
    public void A_shutdown_that_cannot_be_watched_says_so()
    {
        ThreePanesOnTheOldDaemon();
        MainWindow window = CreateWindow();
        MuxConnectionHost host = _hosts.Single();
        PersistenceNoticeAction restart = PumpUntilOffered(window);
        window.ConfirmMuxRestart = (_, _) => Task.FromResult(true);
        window.MuxReadDescriptorForUpdate = () => null;

        restart.Run();
        PumpUntil(() => !window.IsMuxRestartRunningForTest, "the restart finished");
        PumpUntil(() => host.CurrentClient is { ServerVersion: ThisBuild }, "the host connected to a daemon of this build");

        Assert.Equal(1, Volatile.Read(ref _oldShutdowns));
        Assert.Equal([RestartOutcome.StopUnconfirmed], Outcomes);
    }

    /// <summary>
    /// Review M4: an unexpected failure after the panes let go of their shells is reported plainly, the offer is
    /// released, and Enter in a pane works again.
    /// </summary>
    [AvaloniaFact]
    public void An_unexpected_failure_mid_restart_is_reported()
    {
        ThreePanesOnTheOldDaemon();
        MainWindow window = CreateWindow();
        PersistenceNoticeAction restart = PumpUntilOffered(window);
        window.ConfirmMuxRestart = (_, _) => Task.FromResult(true);
        window.MuxRestartFaultForTest = () => throw new InvalidOperationException("scripted restart failure");

        restart.Run();
        PumpUntil(() => !window.IsMuxRestartRunningForTest, "the restart finished");

        Assert.Equal([RestartOutcome.Failed], Outcomes);
        Assert.True(window.MuxPreviousBuildLaunch.TryOffer(Local), "the offer was not released");
        TerminalPane first = window.AllPanesForTest()[0];
        PressEnter(first);
        PumpUntil(() => first.Session is MuxClientSession { IsAttached: true }, "Enter starts a shell again");
    }

    /// <summary>Declined: nothing is sent, the old daemon keeps its shells, and this launch does not offer it again.</summary>
    [AvaloniaFact]
    public void Declining_the_restart_sends_nothing()
    {
        Guid[] ids = ThreePanesOnTheOldDaemon();
        MainWindow window = CreateWindow();
        PersistenceNoticeAction restart = PumpUntilOffered(window);
        int asked = 0;
        window.ConfirmMuxRestart = (_, _) => { asked++; return Task.FromResult(false); };
        bool probed = false;
        window.MuxProbeForUpdate = _ => { probed = true; return Task.FromResult<MuxClient?>(null); };
        int terminated = 0;
        window.MuxTerminateDaemon = _ => { Interlocked.Increment(ref terminated); return true; };

        restart.Run();
        PumpUntil(() => asked == 1, "asked");
        PumpUntil(() => !window.IsMuxRestartRunningForTest, "the restart finished");

        Assert.False(probed, "a daemon was probed to be shut down");
        Assert.Equal(0, Volatile.Read(ref _oldShutdowns));
        Assert.Equal(0, Volatile.Read(ref terminated));
        Assert.Equal(1, Volatile.Read(ref _connects));
        Assert.All(ids, id => Assert.False(_old.Mux(id).IsExited));
        Assert.Equal(3, Attached(window).Count);
        Assert.Single(_notices); // the offer: no outcome
        Assert.False(window.MuxPreviousBuildLaunch.TryOffer(Local), "a declined offer was released");
    }

    /// <summary>
    /// Review item 3: a button left from before - another window already restarted the daemon, and the one there now is
    /// this build's - must not stop it. The last look before <c>shutdown</c> finds it current: nothing is sent, the panes
    /// are left alone, and the notice says why.
    /// </summary>
    [AvaloniaFact]
    public void A_stale_button_does_not_stop_a_daemon_of_this_build()
    {
        ThreePanesOnTheOldDaemon();
        MainWindow window = CreateWindow();
        PersistenceNoticeAction restart = PumpUntilOffered(window);
        int currentShutdowns = 0;
        _current.Server.ShutdownRequested += () => Interlocked.Increment(ref currentShutdowns);
        window.ConfirmMuxRestart = (_, _) =>
        {
            _oldStopped = true; // meanwhile another window restarted it: the probe now reaches this build's daemon
            return Task.FromResult(true);
        };
        int terminated = 0;
        window.MuxTerminateDaemon = _ => { Interlocked.Increment(ref terminated); return true; };

        restart.Run();
        PumpUntil(() => !window.IsMuxRestartRunningForTest, "the restart finished");

        Assert.Equal([RestartOutcome.AlreadyThisBuild], Outcomes);
        Assert.Equal(0, Volatile.Read(ref currentShutdowns));
        Assert.Equal(0, Volatile.Read(ref _oldShutdowns));
        Assert.Equal(0, Volatile.Read(ref terminated));
        Assert.Equal(3, Attached(window).Count); // not let go of
    }

    /// <summary>
    /// Review item 3: one restart at a time, process-wide - another window's click, or a second click here, while one is
    /// asking or stopping the daemon sends nothing, and says so.
    /// </summary>
    [AvaloniaFact]
    public void A_second_click_while_a_restart_runs_sends_nothing()
    {
        ThreePanesOnTheOldDaemon();
        MainWindow window = CreateWindow();
        PersistenceNoticeAction restart = PumpUntilOffered(window);
        var answer = new TaskCompletionSource<bool>();
        int asked = 0;
        window.ConfirmMuxRestart = (_, _) => { asked++; return answer.Task; };

        restart.Run();
        PumpUntil(() => asked == 1, "the first click asks");
        restart.Run();
        PumpUntil(() => Outcomes.Contains(RestartOutcome.AlreadyRestarting), "the second click says why");
        answer.SetResult(false);
        PumpUntil(() => !window.IsMuxRestartRunningForTest, "the first restart finished");

        Assert.Equal(1, asked);
        Assert.Equal(0, Volatile.Read(ref _oldShutdowns));
        Assert.Equal([RestartOutcome.AlreadyRestarting], Outcomes);
    }

    /// <summary>
    /// A daemon of another protocol version cannot even be connected to, so the pane falls back to a plain shell and says
    /// so with the hint (kept as it is). The notice now carries the same action: it cannot be asked to shut down, so it is
    /// terminated by pid - as <c>kill-server --force</c> does - and the warm-up that follows does not wait out the
    /// failure cooldown the mismatch started.
    /// </summary>
    [AvaloniaFact]
    public void The_version_mismatch_fallback_offers_the_restart_and_it_terminates_by_pid()
    {
        bool replaced = false;
        int connects = 0;
        MainWindow window = CreateWindow(connect: ct =>
        {
            Interlocked.Increment(ref connects);
            return Volatile.Read(ref replaced)
                ? MuxClient.ConnectAsync(_current.Listener.Connect(), null, ct)
                : throw new MuxUnavailableException($"different version. {MuxDaemonLauncher.KillServerHint}", versionMismatch: true);
        });
        MuxConnectionHost host = _hosts.Single();
        window.MuxProbeForUpdate = async ct => Volatile.Read(ref replaced)
            ? await MuxClient.ConnectAsync(_current.Listener.Connect(), null, ct)
            : throw new MuxUnavailableException("different version", versionMismatch: true);
        var asked = new List<(string? Host, int Shells)>();
        window.ConfirmMuxRestart = (where, shells) => { asked.Add((where, shells)); return Task.FromResult(true); };
        var terminated = new List<MuxEndpointDescriptor>();
        window.MuxTerminateDaemon = d =>
        {
            lock (terminated) terminated.Add(d);
            Volatile.Write(ref replaced, true);
            return true;
        };

        string fallback = $"{TerminalPane.MuxUnavailableBanner}\n{TerminalPane.MuxVersionMismatchHint}";
        PumpUntil(() => Toast(window).Title == TerminalPane.MuxUnavailableNoticeTitle && ToastLines(window).Contains(TerminalPane.MuxVersionMismatchHint), "the fallback notice is shown");
        Assert.Contains(fallback, Toast(window).Message, StringComparison.Ordinal);
        PersistenceNoticeAction action = OfferedAction(window)!;
        Assert.Equal("Restart multiplexer now", action.Label);
        int before = Volatile.Read(ref connects);

        action.Run();
        PumpUntil(() => host.CurrentClient is { ServerVersion: ThisBuild }, "the host connected to a daemon of this build");

        Assert.Equal([(null, -1)], asked); // the count cannot be had from a daemon this build cannot speak to
        lock (terminated) Assert.Same(OldDaemon, Assert.Single(terminated));
        Assert.Equal(before + 1, Volatile.Read(ref connects));
    }
}
