using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Mux;
using Ntilde.Mux.Tests.Support;
using Ntilde.Shell;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Controls; // FakeTerminalSession, RecordingSessionFactory

namespace Ntilde.Tests.Core;

/// <summary>
/// Spec §9: a user closing a pane ends its shell; closing the window detaches and leaves every
/// shell running in the daemon. The window's pane spawns into an in-memory MuxServer.
/// </summary>
/// <remarks>
/// <see cref="TestAppDataRoot"/> is taken for its lifetime: closing a real MainWindow saves the
/// session, which must not land in the developer's own profile (see MainWindowShellExitTests).
/// </remarks>
public sealed class MainWindowMuxLifecycleTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    private readonly MuxTestHost _mux = new();
    private MuxConnectionHost? _host;

    /// <summary>
    /// Each test starts with no saved session: the fixture's file would otherwise restore the previous
    /// test's panes, naming mux sessions this test's MuxTestHost never had (order-dependent outcomes).
    /// </summary>
    public MainWindowMuxLifecycleTests()
    {
        if (File.Exists(AppPaths.SessionFilePath)) File.Delete(AppPaths.SessionFilePath);
    }

    public void Dispose()
    {
        TestMainWindowFactory.DisposeCreatedWindows();
        _host?.Dispose();
        _mux.Dispose();
    }

    private MainWindow CreateWindow()
    {
        _host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(_mux.Listener.Connect(), null, ct), "test", null);
        var factory = new MuxTerminalSessionFactory(_host, new RecordingSessionFactory(new FakeTerminalSession()), null);
        MainWindow window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            CommandAssist = TestCommandAssistServices.Instance,
            SessionFactory = factory,
        });
        Assert.Same(_host, window.MuxHost);
        window.Show();
        PumpUntil(() => AllPanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true }), "the first pane attached");
        return window;
    }

    private static IReadOnlyList<TerminalPane> AllPanes(MainWindow w) => w.AllPanesForTest();

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

    [AvaloniaFact]
    public void Window_close_detaches_and_the_session_keeps_running()
    {
        MainWindow window = CreateWindow();
        Guid id = ((MuxClientSession)AllPanes(window).First().Session!).Id;

        window.Close();

        PumpUntil(() => _mux.Mux(id).AttachedClients == 0, "the daemon saw the detach");
        Assert.False(_mux.Mux(id).IsExited);
        Assert.Contains(id, _mux.Server.GetSessionIds());
        Assert.Null(_host!.CurrentClient); // the teardown closed the shared connection
    }

    [AvaloniaFact]
    public void Closing_last_tab_kills_its_session_before_teardown()
    {
        MainWindow window = CreateWindow();
        TerminalPane pane = AllPanes(window).Single();
        Guid id = ((MuxClientSession)pane.Session!).Id;
        var close = typeof(MainWindow).GetMethod("ClosePaneAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // Closing the last tab closes the window too, and its teardown closes the connection
        // right behind the kill: the kill must still reach the daemon.
        var task = (Task<bool>)close.Invoke(window, [pane, true])!;

        PumpUntil(() => task.IsCompleted, "the close finished");
        Assert.Null(_host!.CurrentClient); // the window's teardown really did close the connection
        PumpUntil(() => !_mux.Server.GetSessionIds().Contains(id), "the session was killed");
    }

    [AvaloniaFact]
    public void Teardown_twice_is_harmless()
    {
        MainWindow window = CreateWindow();
        Guid id = ((MuxClientSession)AllPanes(window).First().Session!).Id;
        var teardown = typeof(MainWindow).GetMethod("PerformAppTeardown", BindingFlags.NonPublic | BindingFlags.Instance)!;

        teardown.Invoke(window, null);
        teardown.Invoke(window, null);

        PumpUntil(() => _mux.Mux(id).AttachedClients == 0, "the daemon saw the detach");
        Assert.Contains(id, _mux.Server.GetSessionIds());
    }

    [AvaloniaFact]
    public void Close_confirmation_refreshes_the_daemons_child_process_state()
    {
        MainWindow window = CreateWindow();
        var mux = (MuxClientSession)AllPanes(window).First().Session!;
        _mux.Fake(mux.Id).HasActiveChildProcesses = true;

        // Off the UI thread: never Wait() a mux task on the Avalonia dispatcher.
        Task.Run(() => MainWindow.RefreshPersistentSessionInfoAsync(mux, TimeSpan.FromSeconds(2)), TestContext.Current.CancellationToken)
            .GetAwaiter().GetResult();

        Assert.True(mux.HasActiveChildProcesses);
    }

    [AvaloniaFact]
    public void Turning_persistence_off_swaps_the_factory_but_keeps_open_mux_panes_connected()
    {
        MainWindow window = CreateWindow();
        TerminalPane pane = AllPanes(window).Single();
        var settings = (TerminalSettings)typeof(MainWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        settings.SessionPersistence = SessionPersistenceMode.Off;

        typeof(MainWindow).GetMethod("ApplySessionPersistenceSetting", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);

        Assert.Same(DefaultTerminalSessionFactory.Instance, pane.SessionFactory); // re-wired: Reconnect makes a normal session
        Assert.Same(_host, window.MuxHost);
        Assert.NotNull(_host!.CurrentClient);
        Assert.True(((MuxClientSession)pane.Session!).IsAttached);
    }

    [AvaloniaFact]
    public void Persistence_is_off_by_default()
    {
        MainWindow window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            CommandAssist = TestCommandAssistServices.Instance,
        });

        Assert.Null(window.MuxHost);
        IReadOnlyList<TerminalPane> panes = AllPanes(window);
        Assert.NotEmpty(panes);
        Assert.All(panes, p => Assert.Same(DefaultTerminalSessionFactory.Instance, p.SessionFactory));
    }

    [AvaloniaFact]
    public void A_host_that_cannot_be_built_falls_back_to_normal_sessions()
    {
        MainWindow window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            CommandAssist = TestCommandAssistServices.Instance,
            SessionFactory = new RecordingSessionFactory(new FakeTerminalSession()),
        });
        TerminalPane pane = AllPanes(window).Single();
        window.MuxHostFactory = () => throw new InvalidOperationException("no process path");
        Settings(window).SessionPersistence = SessionPersistenceMode.KeepOnClose;

        typeof(MainWindow).GetMethod("ApplySessionPersistenceSetting", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);

        Assert.Null(window.MuxHost);
        Assert.IsNotType<MuxTerminalSessionFactory>(pane.SessionFactory);
    }

    [AvaloniaFact]
    public void A_failed_update_apply_leaves_the_teardown_runnable_for_the_later_close()
    {
        MainWindow window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            CommandAssist = TestCommandAssistServices.Instance,
            SessionFactory = new RecordingSessionFactory(new FakeTerminalSession()),
        });
        var coordinator = new Ntilde.Update.UpdateCoordinator(new ThrowingApplyUpdateService(), () => true, _ => { }, _ => { });
        Assert.Equal(Ntilde.Update.UpdateCheckOutcome.UpdateReady,
            Task.Run(() => coordinator.RunManualCheckAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken).GetAwaiter().GetResult());
        window.UpdateCoordinatorForTest = coordinator;

        typeof(MainWindow).GetMethod("ApplyStagedUpdate", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
        Assert.True(File.Exists(AppPaths.SessionFilePath), "the apply's own teardown saved the session");
        File.Delete(AppPaths.SessionFilePath);

        // The user closes the window as the failure toast asks: the session is saved again.
        typeof(MainWindow).GetMethod("PerformAppTeardown", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
        Assert.True(File.Exists(AppPaths.SessionFilePath), "the close after a failed update saved the session again");
    }

    private static TerminalSettings Settings(MainWindow window) =>
        (TerminalSettings)typeof(MainWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;

    private sealed class ThrowingApplyUpdateService : Ntilde.Update.IUpdateService
    {
        public bool IsSupported => true;
        public Task<Ntilde.Update.UpdateAvailability> CheckAndDownloadAsync(CancellationToken ct) => Task.FromResult(new Ntilde.Update.UpdateAvailability(true, "99.0.0"));
        public void ApplyAndRestart() => throw new IOException("Update.exe is locked");
    }

    [AvaloniaFact]
    public void A_successful_attach_writes_the_session_file_with_the_mux_id()
    {
        MainWindow window = CreateWindow();
        Guid id = ((MuxClientSession)AllPanes(window).First().Session!).Id;

        // A crash right after launch must still know which daemon sessions are this window's.
        PumpUntil(() => File.Exists(AppPaths.SessionFilePath) && File.ReadAllText(AppPaths.SessionFilePath).Contains(id.ToString()),
            "the session file names the mux session");
    }

    [AvaloniaFact]
    public void Orphaned_daemon_sessions_open_as_new_tabs_after_restore()
    {
        // An orphan: a running session the saved session file does not reference, with no client.
        Guid orphan;
        using (MuxClient c = Task.Run(() => MuxClient.ConnectAsync(_mux.Listener.Connect(), null, default), TestContext.Current.CancellationToken).GetAwaiter().GetResult())
            orphan = Task.Run(() => MuxTestHost.SpawnAsync(c), TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        PumpUntil(() => _mux.Mux(orphan).AttachedClients == 0, "the spawning client is gone");

        MainWindow window = CreateWindow();
        var tabs = window.FindControl<TabControl>("Tabs")!;
        TerminalPane ownPane = AllPanes(window).First(p => p.Session is MuxClientSession);

        PumpUntil(() => AllPanes(window).Any(p => p.MuxSessionIdToRestore == orphan), "the orphan was adopted");
        // A background tab: selection stays on the window's own tab.
        Assert.Same(ownPane, Assert.IsType<TabItem>(tabs.SelectedItem).Content);
        Assert.Single(AllPanes(window), p => p.MuxSessionIdToRestore == orphan);
        Assert.NotEqual(orphan, ((MuxClientSession)ownPane.Session!).Id);

        // Until visited it has not spawned, yet the session file still names it (no duplicate next launch).
        typeof(MainWindow).GetMethod("PerformAppTeardown", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
        Assert.Contains(orphan.ToString(), File.ReadAllText(AppPaths.SessionFilePath));
    }

    [AvaloniaFact]
    public void An_adopted_tab_attaches_to_its_orphan_when_selected()
    {
        Guid orphan;
        using (MuxClient c = Task.Run(() => MuxClient.ConnectAsync(_mux.Listener.Connect(), null, default), TestContext.Current.CancellationToken).GetAwaiter().GetResult())
            orphan = Task.Run(() => MuxTestHost.SpawnAsync(c), TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        MainWindow window = CreateWindow();
        var tabs = window.FindControl<TabControl>("Tabs")!;
        PumpUntil(() => AllPanes(window).Any(p => p.MuxSessionIdToRestore == orphan), "the orphan was adopted");
        TerminalPane adopted = AllPanes(window).Single(p => p.MuxSessionIdToRestore == orphan);

        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => ReferenceEquals(t.Content, adopted));

        PumpUntil(() => adopted.Session is MuxClientSession { IsAttached: true } m && m.Id == orphan, "the adopted tab attached to the orphan");
        List<Guid> ids = AllPanes(window).Select(p => p.Session).OfType<MuxClientSession>().Select(m => m.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [AvaloniaFact]
    public void Refresh_is_a_no_op_for_a_non_mux_session()
    {
        Task.Run(() => MainWindow.RefreshPersistentSessionInfoAsync(new FakeTerminalSession(), TimeSpan.FromSeconds(1)), TestContext.Current.CancellationToken)
            .GetAwaiter().GetResult();
        Task.Run(() => MainWindow.RefreshPersistentSessionInfoAsync(null, TimeSpan.FromSeconds(1)), TestContext.Current.CancellationToken)
            .GetAwaiter().GetResult();
    }
}
