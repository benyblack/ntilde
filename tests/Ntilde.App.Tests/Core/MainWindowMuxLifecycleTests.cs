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
using Ntilde.Tests.Shell.Mux;

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

    /// <param name="bootTimeUtc">The boot (or logon) the window's startup restore sees (spec R2); the real one when null.</param>
    /// <param name="beforeShow">Runs on the built window before it is shown, when no pane has spawned yet.</param>
    private MainWindow CreateWindow(TimeSpan? disposeFlush = null, Func<MuxEndpointId, MuxConnectionHost?>? createRemote = null, DateTime? bootTimeUtc = null,
        Action<MainWindow>? beforeShow = null)
    {
        _host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(_mux.Listener.Connect(), null, ct), "test", null)
        {
            DisposeFlushTimeout = disposeFlush ?? TimeSpan.FromSeconds(1),
        };
        var factory = new MuxTerminalSessionFactory(
            new MuxConnectionHosts(_host, createRemote ?? (_ => null)), new RecordingSessionFactory(new FakeTerminalSession()), null);
        AppServiceBundle services = AppServices.BuildForDesigner() with
        {
            CommandAssist = TestCommandAssistServices.Instance,
            SessionFactory = factory,
        };
        if (bootTimeUtc is { } boot)
        {
            services = services with { SessionsCannotPredateUtc = () => boot };
        }

        MainWindow window = TestMainWindowFactory.Create(services);
        Assert.Same(_host, window.MuxHost);
        beforeShow?.Invoke(window);
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

    /// <summary>
    /// PR #489 follow-up: with no ping flush at all, the kill for the last closed tab must still land,
    /// because the host now waits for the kill's own reply. (The deterministic RED for this change is
    /// MuxConnectionHostTests.Dispose_waits_for_a_tracked_kill; before the fix this test fails only
    /// when the fire-and-forget kill is still in the client's outbound queue at close, which is usual
    /// but not guaranteed.)
    /// </summary>
    [AvaloniaFact]
    public void Closing_last_tab_with_no_ping_flush_still_kills()
    {
        MainWindow window = CreateWindow(disposeFlush: TimeSpan.Zero);
        TerminalPane pane = AllPanes(window).Single();
        Guid id = ((MuxClientSession)pane.Session!).Id;
        var close = typeof(MainWindow).GetMethod("ClosePaneAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var task = (Task<bool>)close.Invoke(window, [pane, true])!;

        PumpUntil(() => task.IsCompleted, "the close finished");
        Assert.DoesNotContain(id, _mux.Server.GetSessionIds());
    }

    [AvaloniaFact]
    public void A_close_of_an_exited_mux_pane_sends_no_kill()
    {
        MainWindow window = CreateWindow();
        TerminalPane pane = AllPanes(window).Single();
        var mux = (MuxClientSession)pane.Session!;
        // Non-zero: under the default "Graceful" ShellExitPolicy the pane stays (exit 0 would close
        // the last tab, and with it the window).
        _mux.Fake(mux.Id).Exit(3);
        PumpUntil(() => !mux.IsProcessRunning, "the pane saw the exit");

        typeof(MainWindow).GetMethod("KillMuxSessionOnClose", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, [mux, PaneDisposition.EndSession, pane.MuxEndpoint]);

        Assert.Equal(0, _host!.PendingKillCountForTest);
    }

    /// <summary>
    /// Phase 4 spec §5: a closed pane's kill is tracked by the host of the pane's own endpoint, whose
    /// teardown flush then waits for it. Tracked on the local host instead, the remote connection
    /// could close right behind the kill and drop it.
    /// </summary>
    [AvaloniaFact]
    public void Closing_a_remote_pane_tracks_the_kill_on_its_own_host()
    {
        // The remote daemon answers the hello and nothing after it: the pane's attach and its kill
        // stay unanswered, so the kill is still pending when the test looks.
        using FakeMuxServerEnd remoteDaemon = FakeMuxServerEnd.Create();
        // On the pool: started here it would resume on the UI thread, which GetClient blocks.
        Task hello = Task.Run(() => remoteDaemon.AcceptHelloAsync(), TestContext.Current.CancellationToken);
        MuxEndpointId remoteId = MuxEndpointId.ForSsh(Guid.NewGuid());
        var remoteHost = new MuxConnectionHost(ct => MuxClient.ConnectAsync(remoteDaemon.ClientEnd, null, ct), "remote", null, MuxHostPolicy.Remote("box"))
        {
            KillFlushTimeout = TimeSpan.FromMilliseconds(200), // the window's teardown gives up on it quickly
        };
        MainWindow window = CreateWindow(createRemote: id => id == remoteId ? remoteHost : null);
        TerminalPane first = AllPanes(window).Single();

        // A second pane, so closing the remote one leaves the window open.
        typeof(MainWindow).GetProperty("_currentPane", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, first);
        typeof(MainWindow).GetMethod("SplitPane", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [Avalonia.Layout.Orientation.Horizontal]);
        TerminalPane pane = AllPanes(window).Single(p => !ReferenceEquals(p, first));
        PumpUntil(() => pane.Session is MuxClientSession { IsAttached: true }, "the split pane attached locally");

        // Until SSH requests are routed to a remote endpoint, a stand-in factory opens it there.
        pane.SessionFactory = new RemoteEndpointFactory(window.MuxHosts!, remoteId);
        pane.Reconnect();
        Assert.True(hello.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "the remote host connected");
        Assert.IsType<MuxClientSession>(pane.Session);
        Assert.Equal(remoteId.ToString(), pane.MuxEndpoint);

        var close = (Task<bool>)typeof(MainWindow).GetMethod("ClosePaneAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [pane, true])!;
        PumpUntil(() => close.IsCompleted, "the remote pane closed");

        Assert.True(close.Result);
        Assert.Equal(1, remoteHost.PendingKillCountForTest);
        Assert.Equal(0, _host!.PendingKillCountForTest);
    }

    /// <summary>Opens a new session on the remote endpoint's host, the way Task 19's factory will.</summary>
    private sealed class RemoteEndpointFactory(MuxConnectionHosts hosts, MuxEndpointId endpoint) : IPersistentSessionFactory
    {
        public Ntilde.Pty.ITerminalSession Create(Ntilde.Pty.TerminalSessionRequest request) => CreatePersistent(request).Session!;

        public PersistentSessionResult CreatePersistent(Ntilde.Pty.TerminalSessionRequest request)
        {
            MuxClient client = hosts.GetOrCreate(endpoint)!.GetClient(TimeSpan.FromSeconds(5))
                ?? throw new InvalidOperationException("the remote host did not connect");
            return new(client.OpenSession(Guid.NewGuid(), request.Command, request.Arguments), PersistentSessionOutcome.Spawned, endpoint.ToString(), null);
        }
    }

    /// <summary>
    /// Final-fix item 3: a workspace/template/bundle is a layout. One that names a daemon session
    /// (saved before capture stripped the ids, or brought in from elsewhere) must rebuild panes with
    /// fresh shells - not claim the named session, and not print "lost".
    /// </summary>
    [AvaloniaFact]
    public void Applying_a_snapshot_that_names_mux_sessions_starts_fresh_shells()
    {
        MainWindow window = CreateWindow();
        Guid named = Task.Run(async () =>
        {
            MuxClient c = await _mux.ConnectClientAsync();
            return await MuxTestHost.SpawnAsync(c);
        }).GetAwaiter().GetResult();
        var snapshot = new Ntilde.Pty.NtildeSession
        {
            Tabs =
            {
                new Ntilde.Pty.TabSession
                {
                    Title = "ws",
                    Root = new Ntilde.Pty.PaneNode { Type = Ntilde.Pty.NodeType.Leaf, Command = "scripted", MuxSessionId = named.ToString("D"), MuxEndpoint = "test" },
                },
            },
        };
        var apply = typeof(MainWindow).GetMethod("ApplySessionSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!;

        apply.Invoke(window, [snapshot]);

        PumpUntil(() => AllPanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true }), "the rebuilt pane attached");
        var rebuilt = (MuxClientSession)AllPanes(window).Single().Session!;
        Assert.NotEqual(named, rebuilt.Id);
        Assert.Contains(named, _mux.Server.GetSessionIds());
        Assert.False(_mux.Mux(named).IsExited);
        Assert.Equal("test", snapshot.Tabs[0].Root!.MuxEndpoint); // the caller's snapshot is not mutated
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

    // A_failed_update_apply_leaves_the_teardown_runnable_for_the_later_close moved to
    // Ntilde.Tests.Update.UpdateClosesMuxTests (tests/Ntilde.App.Tests/Update/UpdateClosesMuxTests.cs):
    // that file properly awaits ApplyStagedUpdateAsync via a pumped wait rather than relying on the
    // fire-and-forget entry point's async chain happening to finish synchronously, so it lives in
    // one place rather than being duplicated across files.

    private static TerminalSettings Settings(MainWindow window) =>
        (TerminalSettings)typeof(MainWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;

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

    private static (bool Visible, string? Title, string? Message) Toast(MainWindow window) =>
        (window.FindControl<Border>("RecordingToast")!.IsVisible,
         window.FindControl<TextBlock>("RecordingToastTitle")!.Text,
         window.FindControl<TextBlock>("RecordingToastMessage")!.Text);

    /// <summary>
    /// A pane that had to start a new shell in place of a lost one says so in a window toast - not in
    /// its buffer, where the new shell's first frame would paint over it.
    /// </summary>
    [AvaloniaFact]
    public void A_lost_previous_session_is_announced_as_a_toast_not_in_the_buffer()
    {
        MainWindow window = CreateWindow();
        TerminalPane pane = AllPanes(window).Single();
        Guid old = ((MuxClientSession)pane.Session!).Id;
        pane.MuxSessionIdToRestore = Guid.NewGuid(); // a daemon session that no longer exists

        pane.Reconnect();

        PumpUntil(() => Toast(window).Title == TerminalPane.MuxPreviousLostNoticeTitle, "the lost-session toast is shown");
        var fresh = Assert.IsType<MuxClientSession>(pane.Session);
        Assert.NotEqual(old, fresh.Id);
        (bool visible, _, string? message) = Toast(window);
        Assert.True(visible);
        Assert.Equal(TerminalPane.MuxPreviousLostBanner, message);
        Assert.DoesNotContain(TerminalPane.MuxPreviousLostBanner, MuxTestText.VisibleText(pane.Buffer!));
    }

    private static readonly DateTime Boot = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>A pane as the window saved it: reopening local daemon session <paramref name="id"/>.</summary>
    private static Ntilde.Pty.PaneNode LocalLeaf(Guid id) => new()
    {
        Type = Ntilde.Pty.NodeType.Leaf,
        Command = "scripted",
        MuxSessionId = id.ToString("D"),
        MuxEndpoint = MuxEndpointId.Local.ToString(),
    };

    /// <summary>A session file with one tab per root, the first selected, last written at <paramref name="savedUtc"/>.</summary>
    private static void SaveTabs(DateTime savedUtc, params Ntilde.Pty.PaneNode[] roots)
    {
        var session = new Ntilde.Pty.NtildeSession { ActiveTabIndex = 0 };
        foreach (Ntilde.Pty.PaneNode root in roots) session.Tabs.Add(new Ntilde.Pty.TabSession { Title = $"tab {session.Tabs.Count}", Root = root });
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.SessionFilePath)!);
        File.WriteAllText(AppPaths.SessionFilePath, System.Text.Json.JsonSerializer.Serialize(session, Ntilde.Pty.SessionSerializationContext.Default.NtildeSession));
        File.SetLastWriteTimeUtc(AppPaths.SessionFilePath, savedUtc);
    }

    /// <summary>
    /// Waits until the session file names each pane's new session. A pane raises its lost notice in the UI-thread
    /// callback that also asks for that save, and the toast is flushed at the save's priority, ahead of it: once the
    /// file names them all, every lost notice there will be is on the toast.
    /// </summary>
    private static void PumpUntilSaved(IEnumerable<Guid> ids) => PumpUntil(
        () => File.Exists(AppPaths.SessionFilePath) && File.ReadAllText(AppPaths.SessionFilePath) is var saved && ids.All(id => saved.Contains(id.ToString("D"))),
        "the session file names every new session");

    private static List<Guid> AttachedIds(MainWindow window) =>
        AllPanes(window).Select(p => p.Session).OfType<MuxClientSession>().Where(m => m.IsAttached).Select(m => m.Id).ToList();

    /// <summary>
    /// Spec R2: the session file predates the boot, so the reboot ended its daemon sessions. Each restored pane starts
    /// a fresh shell with no "previous sessions were lost" toast - the selected tab's panes at startup, and a
    /// background tab's when it is first shown.
    /// </summary>
    [AvaloniaFact]
    public void A_restore_saved_before_boot_starts_fresh_shells_without_the_lost_toast()
    {
        Guid[] gone = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        var split = new Ntilde.Pty.PaneNode { Type = Ntilde.Pty.NodeType.Split, SplitOrientation = 0, Children = [LocalLeaf(gone[0]), LocalLeaf(gone[1])] };
        SaveTabs(Boot - TimeSpan.FromMinutes(5), split, LocalLeaf(gone[2]));
        // Heard from the panes as well as read off the toast: a toast hides itself after a few seconds.
        var notices = new List<string>();
        var heard = new HashSet<TerminalPane>();
        void Listen(MainWindow w)
        {
            foreach (TerminalPane p in AllPanes(w))
                if (heard.Add(p)) p.PersistenceNotice += (_, title, _, _) => notices.Add(title);
        }

        MainWindow window = CreateWindow(bootTimeUtc: Boot, beforeShow: Listen);
        PumpUntil(() => AttachedIds(window).Count == 2, "the selected tab's panes started fresh shells");
        PumpUntil(() => AllPanes(window).Count == 3, "the background tab was built");
        Listen(window); // before it is first shown: it spawns only then
        window.FindControl<TabControl>("Tabs")!.SelectedIndex = 1;
        PumpUntil(() => AttachedIds(window).Count == 3, "the background tab's pane started a fresh shell");

        List<Guid> fresh = AttachedIds(window);
        Assert.Empty(fresh.Intersect(gone));
        PumpUntilSaved(fresh);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, heard.Count);
        Assert.Empty(notices);
        (bool visible, string? title, string? message) = Toast(window);
        Assert.False(visible && message?.Contains("lost", StringComparison.Ordinal) == true, $"no lost toast; got '{title}': '{message}'");
    }

    /// <summary>Spec R2: a file saved after the boot names sessions that ended since, without a reboot - still news.</summary>
    [AvaloniaFact]
    public void A_restore_saved_after_boot_whose_sessions_are_gone_still_toasts()
    {
        Guid gone = Guid.NewGuid();
        SaveTabs(Boot + TimeSpan.FromMinutes(5), LocalLeaf(gone));

        MainWindow window = CreateWindow(bootTimeUtc: Boot);

        PumpUntil(() => Toast(window).Title == TerminalPane.MuxPreviousLostNoticeTitle, "the lost-session toast is shown");
        Assert.Equal((true, TerminalPane.MuxPreviousLostNoticeTitle, TerminalPane.MuxPreviousLostBanner), Toast(window));
        Assert.DoesNotContain(gone, AttachedIds(window));
    }

    /// <summary>
    /// Spec R2 across launches in one boot. Launch 1 restores after a reboot and never shows tab B, so B keeps its
    /// pre-boot id - and launch 1's save gives the file a post-boot time. Launch 2 must still open B quietly: the
    /// quiet mark travels with the id in the session file.
    /// </summary>
    [AvaloniaFact]
    public void A_background_tab_restored_after_a_reboot_stays_quiet_in_the_next_launch()
    {
        Guid goneA = Guid.NewGuid(), goneB = Guid.NewGuid();
        SaveTabs(Boot - TimeSpan.FromMinutes(5), LocalLeaf(goneA), LocalLeaf(goneB));

        // Launch 1: tab A starts a fresh shell; tab B is built but never shown.
        MainWindow first = CreateWindow(bootTimeUtc: Boot);
        PumpUntil(() => AllPanes(first).Count == 2, "launch 1 built the background tab");
        Guid freshA = AttachedIds(first).Single();
        typeof(MainWindow).GetMethod("PerformAppTeardown", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(first, null);
        Assert.Contains(goneB.ToString("D"), File.ReadAllText(AppPaths.SessionFilePath)); // B still names its pre-boot session
        Assert.True(File.GetLastWriteTimeUtc(AppPaths.SessionFilePath) > Boot, "launch 1's save is after the boot");

        // Launch 2, same boot: A reattaches; B, shown for the first time, finds its session gone.
        MainWindow second = CreateWindow(bootTimeUtc: Boot);
        PumpUntil(() => AllPanes(second).Count == 2, "launch 2 built the background tab");
        TerminalPane paneB = Assert.Single(AllPanes(second), p => p.MuxSessionIdToRestore == goneB);
        var notices = new List<string>();
        paneB.PersistenceNotice += (_, title, _, _) => notices.Add(title);
        second.FindControl<TabControl>("Tabs")!.SelectedIndex = 1;
        PumpUntil(() => paneB.Session is MuxClientSession { IsAttached: true }, "tab B started a fresh shell");

        Guid freshB = paneB.Session!.Id;
        Assert.NotEqual(goneB, freshB);
        Assert.Contains(freshA, AttachedIds(second));
        PumpUntilSaved([freshA, freshB]);
        Assert.Empty(notices);
    }

    [AvaloniaFact]
    public void Notices_from_several_panes_at_once_coalesce_into_one_toast()
    {
        MainWindow window = CreateWindow();
        TerminalPane pane = AllPanes(window).Single();
        var handler = typeof(MainWindow).GetMethod("OnPanePersistenceNotice", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Dispatcher.UIThread.RunJobs();

        for (int i = 0; i < 3; i++)
            handler.Invoke(window, [pane, TerminalPane.MuxPreviousLostNoticeTitle, TerminalPane.MuxPreviousLostBanner, null]);
        Dispatcher.UIThread.RunJobs();

        (bool visible, string? title, string? message) = Toast(window);
        Assert.True(visible);
        Assert.Equal(TerminalPane.MuxPreviousLostNoticeTitle, title);
        Assert.Equal("[3 previous sessions were lost — started new shells]", message);
    }

    /// <summary>Task 22: the orphaned-daemon notice is shown at most once per window launch, however many panes fall back.</summary>
    [AvaloniaFact]
    public void The_orphaned_daemon_notice_is_shown_once_per_window()
    {
        MainWindow window = CreateWindow();
        TerminalPane pane = AllPanes(window).Single();
        var handler = typeof(MainWindow).GetMethod("OnPanePersistenceNotice", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Dispatcher.UIThread.RunJobs();

        handler.Invoke(window, [pane, TerminalPane.MuxOrphanedNoticeTitle, TerminalPane.MuxOrphanedBanner, null]);
        handler.Invoke(window, [pane, TerminalPane.MuxOrphanedNoticeTitle, TerminalPane.MuxOrphanedBanner, null]);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((true, TerminalPane.MuxOrphanedNoticeTitle, TerminalPane.MuxOrphanedBanner), Toast(window));

        // A later pane (after the connection cooldown, say) raises it again: only the other notice shows.
        handler.Invoke(window, [pane, TerminalPane.MuxOrphanedNoticeTitle, TerminalPane.MuxOrphanedBanner, null]);
        handler.Invoke(window, [pane, TerminalPane.MuxPreviousLostNoticeTitle, TerminalPane.MuxPreviousLostBanner, null]);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((true, TerminalPane.MuxPreviousLostNoticeTitle, TerminalPane.MuxPreviousLostBanner), Toast(window));
    }

    [AvaloniaFact]
    public void Refresh_is_a_no_op_for_a_non_mux_session()
    {
        var fake = new FakeTerminalSession();
        // Both return before their first await, so neither touches the dispatcher or waits.
        Task forFake = MainWindow.RefreshPersistentSessionInfoAsync(fake, TimeSpan.FromSeconds(1));
        Task forNull = MainWindow.RefreshPersistentSessionInfoAsync(null, TimeSpan.FromSeconds(1));

        Assert.True(forFake.IsCompletedSuccessfully);
        Assert.True(forNull.IsCompletedSuccessfully);
        Assert.Empty(fake.SentInput);
    }

    [AvaloniaFact]
    public void Detached_shells_are_announced_once_not_adopted()
    {
        Guid detached = Task.Run(async () =>
        {
            MuxClient c = await _mux.ConnectClientAsync();
            Guid id = await MuxTestHost.SpawnAsync(c);
            ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(c, id);
            pane.Session.Detach(userDetached: true);
            await TestWait.UntilAsync(() => _mux.Mux(id).DetachedByUser, "the daemon recorded the user detach");
            return id;
        }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        MainWindow window = CreateWindow();

        PumpUntil(() => Toast(window).Message == "1 detached shell is running — Attach to session… to reopen it", "the detached shell was announced");
        Assert.DoesNotContain(AllPanes(window), p => p.MuxSessionIdToRestore == detached);
        Assert.DoesNotContain(AllPanes(window), p => p.Session is MuxClientSession m && m.Id == detached);
        Assert.Equal(0, _mux.Mux(detached).AttachedClients);
    }

    /// <summary>
    /// Review fix: the adoption toast and the detached-shells reminder are raised in the same startup
    /// job. Both go through the notice coalescer, so one toast carries both lines - neither replaces the other.
    /// </summary>
    [AvaloniaFact]
    public void Startup_adoption_and_the_detached_reminder_share_one_toast()
    {
        Guid orphan;
        using (MuxClient c = Task.Run(() => MuxClient.ConnectAsync(_mux.Listener.Connect(), null, default), TestContext.Current.CancellationToken).GetAwaiter().GetResult())
            orphan = Task.Run(() => MuxTestHost.SpawnAsync(c), TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        PumpUntil(() => _mux.Mux(orphan).AttachedClients == 0, "the spawning client is gone");
        Task.Run(async () =>
        {
            MuxClient c = await _mux.ConnectClientAsync();
            Guid id = await MuxTestHost.SpawnAsync(c);
            ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(c, id);
            pane.Session.Detach(userDetached: true);
            await TestWait.UntilAsync(() => _mux.Mux(id).DetachedByUser, "the daemon recorded the user detach");
        }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        MainWindow window = CreateWindow();

        PumpUntil(() => AllPanes(window).Any(p => p.MuxSessionIdToRestore == orphan), "the orphan was adopted");
        PumpUntil(() => Toast(window).Message?.Contains("detached shell is running", StringComparison.Ordinal) == true, "the reminder was shown");
        string[] lines = Toast(window).Message!.Split('\n');
        Assert.Contains("Reattached 1 detached session", lines);
        Assert.Contains("1 detached shell is running — Attach to session… to reopen it", lines);
    }

    [AvaloniaFact]
    public void The_detached_shells_reminder_is_shown_once_per_launch()
    {
        MainWindow window = CreateWindow();
        Dispatcher.UIThread.RunJobs();

        window.AnnounceDetachedShellsOnce(2);
        window.AnnounceDetachedShellsOnce(3);
        Dispatcher.UIThread.RunJobs();

        // A second announcement would coalesce into the same line as "(2 panes)", or name 3.
        Assert.Equal("2 detached shells are running — Attach to session… to reopen them", Toast(window).Message);
    }

    [AvaloniaFact]
    public void Several_attached_elsewhere_notices_coalesce_into_a_plural_line()
    {
        Assert.Equal("[3 previous shells are open in another window — started new shells]",
            MainWindow.BuildPersistenceNoticeMessage(TerminalPane.MuxAttachedElsewhereNoticeTitle, TerminalPane.MuxAttachedElsewhereBanner, 3));
        Assert.Equal(TerminalPane.MuxAttachedElsewhereBanner,
            MainWindow.BuildPersistenceNoticeMessage(TerminalPane.MuxAttachedElsewhereNoticeTitle, TerminalPane.MuxAttachedElsewhereBanner, 1));
    }
}
