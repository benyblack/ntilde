using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
/// every session, then closes. <see cref="TestAppDataRoot"/> is taken for its lifetime: the window restores and saves its
/// session there.
/// </remarks>
public sealed class MainWindowMuxUpdateTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    private const string ThisBuild = "0.12.0";
    private const string PreviousBuild = "0.0.1";

    private static readonly MuxEndpointDescriptor OldDaemon = new() { Endpoint = "test", Pid = 4242, ProcessName = "Ntilde", MinVersion = 1, MaxVersion = 2 };

    private readonly MuxTestHost _old = new(new MuxServerOptions { ForceConPtyFiltering = false, AppVersion = PreviousBuild });
    private readonly MuxTestHost _current = new(new MuxServerOptions { ForceConPtyFiltering = false, AppVersion = ThisBuild });
    private readonly TaskCompletionSource<bool> _oldExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<MuxConnectionHost> _hosts = [];
    private volatile bool _oldStopped;
    private int _connects;
    private int _oldShutdowns;

    /// <summary>
    /// Run first when the old daemon is told to shut down, before it stops as the real one does: a test can deliver every
    /// shell's <c>exited</c> first - the order that reaches panes still attached - or leave the daemon running.
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
    private MainWindow CreateWindow(TerminalSettings? settings = null, Func<CancellationToken, Task<MuxClient>>? connect = null, MuxPreviousBuildNotice.Launch? launch = null)
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
        window.MuxPreviousBuildLaunch = launch ?? new MuxPreviousBuildNotice.Launch(); // this test's launch
        window.MuxProbeForUpdate = async ct => _oldStopped ? await MuxClient.ConnectAsync(_current.Listener.Connect(), null, ct) : await MuxClient.ConnectAsync(_old.Listener.Connect(), null, ct);
        window.MuxReadDescriptorForUpdate = () => OldDaemon;
        window.MuxWaitForDaemonExitForUpdate = _ => _oldExited.Task;
        window.Show();
        return window;
    }

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

    /// <summary>Waits for the window's three panes to reattach their shells, and for the notice that names them.</summary>
    private static void PumpUntilOffered(MainWindow window)
    {
        PumpUntil(() => Attached(window).Count == 3, "the three panes reattached");
        PumpUntil(() => Toast(window).Message?.Contains(ThreeShellsNotice, StringComparison.Ordinal) == true, "the notice is shown");
    }

    private static string Unwrapped(string text) =>
        text.Replace("\n", string.Empty, StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);

    private static bool Shows(TerminalPane pane, string banner) =>
        pane.Buffer is { } buffer && Unwrapped(MuxTestText.VisibleText(buffer)).Contains(Unwrapped(banner), StringComparison.Ordinal);

    /// <summary>
    /// One connection, three panes: one notice, naming the daemon's version and its three shells, with the restart. The
    /// panes share the host's connection, so it is offered once, not per pane - and not again when the connection drops
    /// and comes back to the same daemon.
    /// </summary>
    [AvaloniaFact]
    public void A_daemon_of_the_previous_build_is_offered_a_restart_once()
    {
        ThreePanesOnTheOldDaemon();
        MainWindow window = CreateWindow();
        MuxConnectionHost host = _hosts.Single();

        PumpUntilOffered(window);

        (bool visible, string? title, string? message) = Toast(window);
        Assert.True(visible);
        Assert.Equal(MuxPreviousBuildNotice.Title, title);
        Assert.Equal(ThreeShellsNotice, message);
        Assert.True(ToastButton(window, "RecordingToastAction").IsVisible);
        Assert.Equal("Restart multiplexer now", ToastButton(window, "RecordingToastAction").Content);
        Assert.Equal(1, Volatile.Read(ref _connects));

        // Dismissed, then the connection drops and comes back to the same old daemon: nothing more is offered.
        Click(ToastButton(window, "RecordingToastClose"));
        MuxClient first = host.CurrentClient!;
        first.Dispose();
        host.WarmUp();
        PumpUntilDecided(window, MuxEndpointId.Local, 2);

        Assert.Equal(2, Volatile.Read(ref _connects));
        Assert.Equal(PreviousBuild, host.CurrentClient!.ServerVersion);
        Assert.False(Toast(window).Visible, $"offered again: '{Toast(window).Message}'");
    }

    /// <summary>A daemon of this very build is what the window wants: nothing to say.</summary>
    [AvaloniaFact]
    public void A_daemon_of_this_build_gives_no_notice()
    {
        _oldStopped = true; // the daemon is this build's from the start
        MainWindow window = CreateWindow();
        MuxConnectionHost host = _hosts.Single();
        PumpUntilDecided(window, MuxEndpointId.Local, 1);

        Assert.Equal(ThisBuild, host.CurrentClient!.ServerVersion);
        Assert.False(Toast(window).Visible, $"a notice was shown: '{Toast(window).Message}'");
    }

    /// <summary>With SessionPersistence Off nothing new appears, whatever the daemon the window's panes still use reports.</summary>
    [AvaloniaFact]
    public void With_persistence_off_no_notice_is_shown()
    {
        ThreePanesOnTheOldDaemon();
        MainWindow window = CreateWindow(new TerminalSettings { SessionPersistence = SessionPersistenceMode.Off });
        MuxConnectionHost host = _hosts.Single();
        PumpUntilDecided(window, MuxEndpointId.Local, 1);

        Assert.Equal(PreviousBuild, host.CurrentClient!.ServerVersion);
        Assert.False(Toast(window).Visible && Toast(window).Title == MuxPreviousBuildNotice.Title, $"a notice was shown: '{Toast(window).Message}'");
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
        MainWindow window = CreateWindow(new TerminalSettings { SessionPersistence = SessionPersistenceMode.KeepOnClose, ShellExitPolicy = "Always" });
        MuxConnectionHost host = _hosts.Single();
        PumpUntilOffered(window);
        List<TerminalPane> panes = [.. window.AllPanesForTest()];
        Assert.Equal(3, panes.Count);
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

        Click(ToastButton(window, "RecordingToastAction"));
        PumpUntil(() => host.CurrentClient is { ServerVersion: ThisBuild }, "the host connected to a daemon of this build");
        PumpUntilDecided(window, MuxEndpointId.Local, 2);
        for (int i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); } // any exit a pane still heard is handled

        Assert.Equal([(null, 3)], asked);
        Assert.Equal(1, Volatile.Read(ref _oldShutdowns));
        Assert.Equal(2, Volatile.Read(ref _connects));
        lock (terminated) Assert.Empty(terminated); // it exited on its own
        Assert.False(Toast(window).Visible, $"a notice followed the restart: '{Toast(window).Message}'");
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
        first.TermView.Focus();
        TopLevel.GetTopLevel(first)!.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        PumpUntil(() => first.Session is MuxClientSession { IsAttached: true }, "the pane started a new shell");
        Assert.Contains(first.Session!.Id, _current.Server.GetSessionIds());
        Assert.Equal(3, window.AllPanesForTest().Count);
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
        PumpUntilOffered(window);
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

        Click(ToastButton(window, "RecordingToastAction"));
        PumpUntil(() => host.CurrentClient is { ServerVersion: ThisBuild }, "the host connected to a daemon of this build");

        Assert.Equal(1, Volatile.Read(ref _oldShutdowns));
        lock (terminated) Assert.Same(OldDaemon, Assert.Single(terminated));
        Assert.Equal(2, Volatile.Read(ref _connects));
    }

    /// <summary>Review item 4: a daemon that neither exits nor can be terminated is reported, and can be offered again later.</summary>
    [AvaloniaFact]
    public void A_daemon_that_cannot_be_stopped_is_reported()
    {
        ThreePanesOnTheOldDaemon();
        MainWindow window = CreateWindow();
        PumpUntilOffered(window);
        _oldStopsOnShutdown = false;
        window.ConfirmMuxRestart = (_, _) => Task.FromResult(true);
        window.MuxWaitForDaemonExitForUpdate = _ => Task.FromResult(false);
        window.MuxTerminateDaemon = _ => false;

        Click(ToastButton(window, "RecordingToastAction"));
        PumpUntil(() => Toast(window).Message == MuxPreviousBuildNotice.NotStopped, "the failure is reported");
        PumpUntil(() => !window.IsMuxRestartRunningForTest, "the restart finished");

        Assert.True(window.MuxPreviousBuildLaunch.TryOffer(MuxEndpointId.Local), "the offer was not released");
    }

    /// <summary>Declined: nothing is sent, the old daemon keeps its shells, and this launch does not offer it again.</summary>
    [AvaloniaFact]
    public void Declining_the_restart_sends_nothing()
    {
        Guid[] ids = ThreePanesOnTheOldDaemon();
        MainWindow window = CreateWindow();
        PumpUntilOffered(window);
        int asked = 0;
        window.ConfirmMuxRestart = (_, _) => { asked++; return Task.FromResult(false); };
        bool probed = false;
        window.MuxProbeForUpdate = _ => { probed = true; return Task.FromResult<MuxClient?>(null); };
        int terminated = 0;
        window.MuxTerminateDaemon = _ => { Interlocked.Increment(ref terminated); return true; };

        Click(ToastButton(window, "RecordingToastAction"));
        PumpUntil(() => asked == 1, "asked");
        PumpUntil(() => !window.IsMuxRestartRunningForTest, "the restart finished");

        Assert.False(probed, "a daemon was probed to be shut down");
        Assert.Equal(0, Volatile.Read(ref _oldShutdowns));
        Assert.Equal(0, Volatile.Read(ref terminated));
        Assert.Equal(1, Volatile.Read(ref _connects));
        Assert.All(ids, id => Assert.False(_old.Mux(id).IsExited));
        Assert.Equal(3, Attached(window).Count);
        Assert.False(Toast(window).Visible);
        Assert.False(window.MuxPreviousBuildLaunch.TryOffer(MuxEndpointId.Local), "a declined offer was released");
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
        PumpUntilOffered(window);
        int currentShutdowns = 0;
        _current.Server.ShutdownRequested += () => Interlocked.Increment(ref currentShutdowns);
        window.ConfirmMuxRestart = (_, _) =>
        {
            _oldStopped = true; // meanwhile another window restarted it: the probe now reaches this build's daemon
            return Task.FromResult(true);
        };
        int terminated = 0;
        window.MuxTerminateDaemon = _ => { Interlocked.Increment(ref terminated); return true; };

        Click(ToastButton(window, "RecordingToastAction"));
        PumpUntil(() => Toast(window).Message == MuxPreviousBuildNotice.NothingToRestart(null, ThisBuild), "the notice says why");

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
        PumpUntilOffered(window);
        PersistenceNoticeAction action = OfferedAction(window)!;
        var answer = new TaskCompletionSource<bool>();
        int asked = 0;
        window.ConfirmMuxRestart = (_, _) => { asked++; return answer.Task; };

        action.Run();
        PumpUntil(() => asked == 1, "the first click asks");
        action.Run();
        PumpUntil(() => Toast(window).Message == MuxPreviousBuildNotice.AlreadyRestarting(null), "the second click says why");
        answer.SetResult(false);
        PumpUntil(() => !window.IsMuxRestartRunningForTest, "the first restart finished");

        Assert.Equal(1, asked);
        Assert.Equal(0, Volatile.Read(ref _oldShutdowns));
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

        PumpUntil(() => Toast(window).Title == TerminalPane.MuxUnavailableNoticeTitle, "the fallback notice is shown");
        Assert.Contains(TerminalPane.MuxVersionMismatchHint, Toast(window).Message, StringComparison.Ordinal);
        Assert.True(ToastButton(window, "RecordingToastAction").IsVisible);
        Assert.Equal("Restart multiplexer now", ToastButton(window, "RecordingToastAction").Content);
        int before = Volatile.Read(ref connects);

        Click(ToastButton(window, "RecordingToastAction"));
        PumpUntil(() => host.CurrentClient is { ServerVersion: ThisBuild }, "the host connected to a daemon of this build");

        Assert.Equal([(null, -1)], asked); // the count cannot be had from a daemon this build cannot speak to
        lock (terminated) Assert.Same(OldDaemon, Assert.Single(terminated));
        Assert.Equal(before + 1, Volatile.Read(ref connects));
    }
}
