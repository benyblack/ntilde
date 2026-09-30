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
using Ntilde.VT.Tests.StateTransfer;

namespace Ntilde.Tests.Core;

/// <summary>
/// Phase 3 spec §7: a window attaches shared to a session another client (another instance) holds.
/// The window's panes spawn into an in-memory MuxServer; the "other instance" is a second MuxClient.
/// </summary>
public sealed class MainWindowMuxSharingTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    private readonly MuxTestHost _mux = new();
    private MuxConnectionHost? _host;
    private readonly List<MuxConnectionHost> _otherHosts = [];

    public MainWindowMuxSharingTests()
    {
        if (File.Exists(AppPaths.SessionFilePath)) File.Delete(AppPaths.SessionFilePath);
    }

    public void Dispose()
    {
        TestMainWindowFactory.DisposeCreatedWindows();
        _host?.Dispose();
        foreach (MuxConnectionHost h in _otherHosts) h.Dispose();
        _mux.Dispose();
    }

    private MainWindow CreateWindow()
    {
        _host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(_mux.Listener.Connect(), null, ct), "test", null);
        return CreateWindowOn(_host);
    }

    /// <summary>A second ntilde window with its own connection to the same daemon (another instance's GUI).</summary>
    private MainWindow CreateOtherWindow()
    {
        var host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(_mux.Listener.Connect(), null, ct), "test", null);
        _otherHosts.Add(host);
        return CreateWindowOn(host);
    }

    private static MainWindow CreateWindowOn(MuxConnectionHost host)
    {
        var factory = new MuxTerminalSessionFactory(host, new RecordingSessionFactory(new FakeTerminalSession()), null);
        MainWindow window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            CommandAssist = TestCommandAssistServices.Instance,
            SessionFactory = factory,
        });
        window.Show();
        PumpUntil(() => AllPanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true }), "the first pane attached");
        return window;
    }

    private static IReadOnlyList<TerminalPane> AllPanes(MainWindow w) => w.AllPanesForTest();

    private static TerminalSettings Settings(MainWindow window) =>
        (TerminalSettings)typeof(MainWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;

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

    /// <summary>Another ntilde instance: its own connection, a session it spawned and shows.</summary>
    private (MuxClient Client, ClientPaneModel Pane) OtherInstance() => Task.Run(async () =>
    {
        MuxClient c = await _mux.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(c);
        return (c, await MuxTestHost.AttachPaneAsync(c, id));
    }).GetAwaiter().GetResult();

    /// <summary>Runs "Attach to session…", choosing <paramref name="id"/>, and returns the pane that attached it.</summary>
    private static TerminalPane AttachShared(MainWindow window, Guid id)
    {
        window.PickMuxSession = rows =>
        {
            Assert.Contains(rows, r => r.SessionId == id);
            return Task.FromResult<Guid?>(id);
        };
        Task command = window.AttachToMuxSessionAsync();
        PumpUntil(() => command.IsCompleted, "the attach command finished");
        PumpUntil(() => AllPanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true } m && m.Id == id), "the shared tab attached");
        return AllPanes(window).Single(p => p.Session is MuxClientSession m && m.Id == id);
    }

    private void Settle(Guid id, params MuxClient[] clients)
    {
        Task.Run(() => _mux.SettleAsync(id, clients)).GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }

    private void AssertBothEqualTheMux(Guid id, TerminalPane mine, ClientPaneModel theirs, string because)
    {
        HeadlessTerminalSession m = _mux.Mux(id);
        TerminalStateAssert.AssertEquivalent($"[mine: {because}]", m.Buffer, m.Parser, mine.Buffer!, mine.Parser!);
        _mux.AssertPaneMatchesMux(id, theirs, $"theirs: {because}");
    }

    [AvaloniaFact]
    public void Attach_to_session_opens_a_shared_tab_and_both_clients_equal_the_mux()
    {
        MainWindow window = CreateWindow();
        (MuxClient other, ClientPaneModel theirs) = OtherInstance();
        Guid id = theirs.Session.Id;

        TerminalPane mine = AttachShared(window, id);
        MuxClient mineClient = _host!.CurrentClient!;
        PumpUntil(() => _mux.Mux(id).AttachedClients == 2, "both clients attached");
        // The daemon's sessionChanged reaches the second attacher: its pane knows it is shared.
        PumpUntil(() => mine.MuxOtherClients == 1, "the attaching pane counts the other instance");
        Assert.Equal("shared with 1", mine.MuxSharedText.Text);

        _mux.Fake(id).Emit("shared\r\n\x1b[1mbold\x1b[0m line\r\n");
        Settle(id, mineClient, other);
        AssertBothEqualTheMux(id, mine, theirs, "after output");

        ((MuxClientSession)mine.Session!).Resize(70, 20);
        Settle(id, mineClient, other);
        AssertBothEqualTheMux(id, mine, theirs, "after a resize from this window");

        theirs.Session.Resize(90, 25);
        Settle(id, mineClient, other);
        AssertBothEqualTheMux(id, mine, theirs, "after a resize from the other instance");
    }

    [AvaloniaFact]
    public void A_shared_tab_is_marked_in_the_tab_strip()
    {
        MainWindow window = CreateWindow();
        (_, ClientPaneModel theirs) = OtherInstance();

        TerminalPane mine = AttachShared(window, theirs.Session.Id);

        PumpUntil(() => mine.MuxOtherClients == 1, "the pane knows it is shared");
        TabItem tab = window.FindControl<TabControl>("Tabs")!.Items.OfType<TabItem>().Single(t => ReferenceEquals(t.Content, mine));
        PumpUntil(() => (ToolTip.GetTip((Control)tab.Header!) as string)?.Contains(MainWindow.SharedGlyph, StringComparison.Ordinal) == true,
            "the tab label carries the shared marker");
    }

    [AvaloniaFact]
    public void Closing_the_shared_pane_of_a_split_tab_clears_the_marker()
    {
        MainWindow window = CreateWindow();
        (_, ClientPaneModel theirs) = OtherInstance();
        TerminalPane mine = AttachShared(window, theirs.Session.Id);
        PumpUntil(() => mine.MuxOtherClients == 1, "the pane knows it is shared");
        TabItem tab = window.FindControl<TabControl>("Tabs")!.Items.OfType<TabItem>().Single(t => ReferenceEquals(t.Content, mine));
        string? Tip() => ToolTip.GetTip((Control)tab.Header!) as string;
        PumpUntil(() => Tip()?.Contains(MainWindow.SharedGlyph, StringComparison.Ordinal) == true, "the tab is marked");

        var before = AllPanes(window).ToHashSet();
        typeof(MainWindow).GetProperty("_currentPane", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, mine);
        typeof(MainWindow).GetMethod("SplitPane", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [Avalonia.Layout.Orientation.Horizontal]);
        TerminalPane sibling = AllPanes(window).Single(p => !before.Contains(p));
        var close = (Task<bool>)typeof(MainWindow).GetMethod("ClosePaneAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [mine, true])!;
        PumpUntil(() => close.IsCompleted, "the shared pane closed");

        Assert.Same(sibling, tab.Content);
        PumpUntil(() => Tip()?.Contains(MainWindow.SharedGlyph, StringComparison.Ordinal) == false, "the marker went with the shared pane");
    }

    [AvaloniaFact]
    public void Choosing_a_session_open_here_focuses_its_tab_instead_of_duplicating_it()
    {
        MainWindow window = CreateWindow();
        TerminalPane own = AllPanes(window).Single();
        Guid id = ((MuxClientSession)own.Session!).Id;
        IReadOnlyList<MuxSessionPickerRow>? offered = null;
        window.PickMuxSession = rows =>
        {
            offered = rows;
            return Task.FromResult<Guid?>(id);
        };

        Task command = window.AttachToMuxSessionAsync();
        PumpUntil(() => command.IsCompleted, "the attach command finished");

        Assert.True(Assert.Single(offered!, r => r.SessionId == id).OpenHere);
        Assert.Same(own, Assert.Single(AllPanes(window)));
    }

    private static TabItem TabOf(MainWindow window, TerminalPane pane) =>
        window.FindControl<TabControl>("Tabs")!.Items.OfType<TabItem>().Single(t => ReferenceEquals(t.Content, pane));

    private static void PumpFor(int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    private static (bool Visible, string? Title, string? Message) Toast(MainWindow window) =>
        (window.FindControl<Border>("RecordingToast")!.IsVisible,
         window.FindControl<TextBlock>("RecordingToastTitle")!.Text,
         window.FindControl<TextBlock>("RecordingToastMessage")!.Text);

    /// <summary>Review fix 1: the exit of an already-exited share arrives with its attach; Graceful must not close the tab.</summary>
    [AvaloniaFact]
    public void Attaching_a_session_that_already_exited_keeps_the_tab_and_shows_the_exit()
    {
        MainWindow window = CreateWindow();
        Settings(window).ShellExitPolicy = "Graceful"; // closes a pane whose shell exits with 0
        (_, ClientPaneModel theirs) = OtherInstance(); // keeps the exited session from being reaped
        Guid id = theirs.Session.Id;
        _mux.Fake(id).Exit(0);
        PumpUntil(() => _mux.Mux(id).IsExited, "the mux saw the exit");

        TerminalPane mine = AttachShared(window, id);

        PumpUntil(() => MuxTestText.VisibleText(mine.Buffer!).Contains("[Shell exited]", StringComparison.Ordinal), "the exit banner is on screen");
        PumpFor(300);
        Assert.Contains(mine, AllPanes(window));
        Assert.Same(mine, TabOf(window, mine).Content);
    }

    /// <summary>Review fix 1, the other half: a share that exits while attached follows ShellExitPolicy as before.</summary>
    [AvaloniaFact]
    public void A_share_that_exits_while_attached_follows_the_exit_policy()
    {
        MainWindow window = CreateWindow();
        Settings(window).ShellExitPolicy = "Graceful";
        (_, ClientPaneModel theirs) = OtherInstance();
        Guid id = theirs.Session.Id;
        TerminalPane mine = AttachShared(window, id);
        Assert.False(mine.MuxShareOfExitedSession);

        _mux.Fake(id).Exit(0);

        PumpUntil(() => !AllPanes(window).Contains(mine), "Graceful closed the shared pane");
    }

    /// <summary>Review fix 3: a share whose session vanished between listing and attaching starts no shell.</summary>
    [AvaloniaFact]
    public void A_share_whose_session_vanished_before_the_attach_closes_with_a_notice()
    {
        MainWindow window = CreateWindow();
        TerminalPane own = AllPanes(window).Single();
        Guid id = Task.Run(async () => await MuxTestHost.SpawnAsync(await _mux.ConnectClientAsync())).GetAwaiter().GetResult();
        int sessionsBefore = _mux.Server.GetSessionIds().Count();
        window.PickMuxSession = rows =>
        {
            Assert.Contains(rows, r => r.SessionId == id);
            // Gone while the picker was open: exited with nobody attached, and reaped.
            _mux.Fake(id).Exit(0);
            PumpUntil(() => _mux.Mux(id).IsExited, "the mux saw the exit");
            Assert.Equal(1, _mux.Server.ReapExitedSessions(TimeSpan.Zero));
            return Task.FromResult<Guid?>(id);
        };

        Task command = window.AttachToMuxSessionAsync();
        PumpUntil(() => command.IsCompleted, "the attach command finished");

        PumpUntil(() => Toast(window).Message?.Contains(TerminalPane.MuxShareEndedBanner, StringComparison.Ordinal) == true, "the ended share was announced");
        Assert.Equal(TerminalPane.MuxShareEndedNoticeTitle, Toast(window).Title);
        PumpUntil(() => AllPanes(window).Count == 1, "the ended share's tab closed");
        Assert.Same(own, AllPanes(window).Single());
        Assert.Equal(sessionsBefore - 1, _mux.Server.GetSessionIds().Count()); // no fresh shell was spawned for it
    }

    private static (Window Dialog, ListBox List, Button Attach, Button Cancel) PickerDialog(MainWindow window)
    {
        PumpUntil(() => window.OwnedWindows.Count == 1, "the picker dialog opened");
        Window dialog = Assert.Single(window.OwnedWindows);
        Assert.Equal("Attach to Session", dialog.Title);
        var descendants = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(dialog).ToList();
        ListBox list = Assert.Single(descendants.OfType<ListBox>());
        Button attach = descendants.OfType<Button>().Single(b => b.Content as string == "Attach");
        Button cancel = descendants.OfType<Button>().Single(b => b.Content as string == "Cancel");
        return (dialog, list, attach, cancel);
    }

    /// <summary>Review fix 2: the default picker, driven through its dialog.</summary>
    [AvaloniaFact]
    public void The_default_picker_lists_the_sessions_and_Attach_opens_the_chosen_one_shared()
    {
        MainWindow window = CreateWindow();
        (MuxClient other, ClientPaneModel theirs) = OtherInstance();
        Guid id = theirs.Session.Id;
        Guid own = ((MuxClientSession)AllPanes(window).Single().Session!).Id;

        Task command = window.AttachToMuxSessionAsync();
        (_, ListBox list, Button attach, _) = PickerDialog(window);

        IReadOnlyList<Ntilde.Mux.Contracts.SessionSummary> listed = Task.Run(() => other.ListSessionsAsync()).GetAwaiter().GetResult();
        IReadOnlyList<MuxSessionPickerRow> expected = MuxSessionPicker.BuildRows(listed, new HashSet<Guid> { own });
        Assert.Equal(expected.Select(r => r.Display).ToList(), list.ItemsSource!.Cast<string>().ToList());

        list.SelectedIndex = expected.ToList().FindIndex(r => r.SessionId == id);
        attach.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        PumpUntil(() => command.IsCompleted, "the attach command finished");
        PumpUntil(() => AllPanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true } m && m.Id == id), "the chosen session attached");
        PumpUntil(() => _mux.Mux(id).AttachedClients == 2, "attached shared, beside the other instance");
        Assert.Empty(window.OwnedWindows);
    }

    [AvaloniaFact]
    public void Cancel_in_the_default_picker_opens_nothing()
    {
        MainWindow window = CreateWindow();
        OtherInstance();

        Task command = window.AttachToMuxSessionAsync();
        (_, _, _, Button cancel) = PickerDialog(window);
        cancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        PumpUntil(() => command.IsCompleted, "the attach command finished");
        PumpFor(200);
        Assert.Single(AllPanes(window));
        Assert.Single(window.FindControl<TabControl>("Tabs")!.Items);
    }

    /// <summary>Review fix 5: the vertical strip's chip and the screen-reader label say "shared" too.</summary>
    [AvaloniaFact]
    public void A_shared_tab_shows_the_vertical_chip_and_announces_shared()
    {
        MainWindow window = CreateWindow();
        (_, ClientPaneModel theirs) = OtherInstance();
        TerminalPane mine = AttachShared(window, theirs.Session.Id);
        PumpUntil(() => mine.MuxOtherClients == 1, "the pane knows it is shared");

        Settings(window).TabStripOrientation = "Vertical";
        window.ApplyTabLayout();
        window.UpdateTabVisuals();
        TabItem tab = TabOf(window, mine);

        PumpUntil(() => MainWindow.FindTabHeaderDescendant<TextBlock>(tab.Header, "TabSharedChip")?.IsVisible == true, "the shared chip is visible");
        Assert.Equal(MainWindow.SharedGlyph, MainWindow.FindTabHeaderDescendant<TextBlock>(tab.Header, "TabSharedChip")!.Text);
        PumpUntil(() => Avalonia.Automation.AutomationProperties.GetName(tab)?.EndsWith(" shared", StringComparison.Ordinal) == true, "the tab is announced as shared");

        TabItem own = window.FindControl<TabControl>("Tabs")!.Items.OfType<TabItem>().Single(t => t != tab);
        Assert.False(MainWindow.FindTabHeaderDescendant<TextBlock>(own.Header, "TabSharedChip")!.IsVisible);
        Assert.DoesNotContain("shared", Avalonia.Automation.AutomationProperties.GetName(own) ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>Review fix 5: turning persistence off at runtime takes the command away, palette and chord both.</summary>
    [AvaloniaFact]
    public void Turning_persistence_off_unregisters_and_disarms_attach_to_session()
    {
        MainWindow window = CreateWindow();
        Settings(window).Keybindings["attach_session"] = "Ctrl+Alt+J";
        int picks = 0;
        window.PickMuxSession = _ =>
        {
            picks++;
            return Task.FromResult<Guid?>(null);
        };
        MethodInfo setupPalette = typeof(MainWindow).GetMethod("SetupCommandPalette", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Avalonia.Input.KeyEventArgs Chord() => new()
        {
            RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
            Key = Avalonia.Input.Key.J,
            KeyModifiers = Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Alt,
            Source = window,
        };

        setupPalette.Invoke(window, null);
        Assert.Contains(CommandRegistry.GetCommands(), c => c.Id == "attach_session");
        Avalonia.Input.KeyEventArgs on = Chord();
        window.RaiseEvent(on);
        Assert.True(on.Handled);
        PumpUntil(() => picks == 1, "the chord opened the picker while persistence was on");

        Settings(window).SessionPersistence = SessionPersistenceMode.Off;
        typeof(MainWindow).GetMethod("ApplySessionPersistenceSetting", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
        Assert.NotNull(window.MuxHost); // the host outlives the flip, for the mux panes still open

        setupPalette.Invoke(window, null);
        Assert.DoesNotContain(CommandRegistry.GetCommands(), c => c.Id == "attach_session");
        window.RaiseEvent(Chord());
        Task direct = window.AttachToMuxSessionAsync();
        PumpUntil(() => direct.IsCompleted, "the command returned");
        PumpFor(200);
        Assert.Equal(1, picks);
    }

    [AvaloniaFact]
    public void Detach_pane_keeps_the_shell_running_and_the_count_drops()
    {
        MainWindow window = CreateWindow();
        (_, ClientPaneModel theirs) = OtherInstance();
        Guid id = theirs.Session.Id;
        TerminalPane mine = AttachShared(window, id);
        PumpUntil(() => _mux.Mux(id).AttachedClients == 2, "both attached");

        Task<bool> detach = window.DetachPaneAsync(mine);
        PumpUntil(() => detach.IsCompleted, "the detach finished");

        Assert.True(detach.Result);
        PumpUntil(() => _mux.Mux(id).AttachedClients == 1, "the daemon saw the detach");
        Assert.Contains(id, _mux.Server.GetSessionIds());
        Assert.False(_mux.Mux(id).IsExited);
        Assert.DoesNotContain(mine, AllPanes(window));
        // Notices are coalesced into one Background flush (EnqueueNotice), so the toast lands a pass later.
        PumpUntil(() => Toast(window).Title == "Shell detached", "the detach toast is shown");
        Assert.Equal("Shell kept running — Attach to session… to get it back", Toast(window).Message);
        PumpUntil(() => theirs.Session.AttachedClients == 1, "the other instance heard it");
    }

    [AvaloniaTheory]
    [InlineData("Cancel")]
    [InlineData("Detach")]
    [InlineData("Close")]
    public void Close_with_others_attached_prompts_and_each_choice_behaves(string choiceName)
    {
        SharedCloseChoice choice = Enum.Parse<SharedCloseChoice>(choiceName);
        MainWindow window = CreateWindow();
        (_, ClientPaneModel theirs) = OtherInstance();
        Guid id = theirs.Session.Id;
        TerminalPane mine = AttachShared(window, id);
        PumpUntil(() => _mux.Mux(id).AttachedClients == 2, "both attached");
        var mineSession = (MuxClientSession)mine.Session!; // the pane lets go of it on close
        int asked = -1;
        window.ConfirmSharedClose = others =>
        {
            asked = others;
            return Task.FromResult(choice);
        };
        var close = typeof(MainWindow).GetMethod("ClosePaneAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var task = (Task<bool>)close.Invoke(window, [mine, false])!;
        PumpUntil(() => task.IsCompleted, "the close finished");

        Assert.Equal(1, asked);
        switch (choice)
        {
            case SharedCloseChoice.Cancel:
                Assert.False(task.Result);
                Assert.Contains(mine, AllPanes(window));
                Assert.Equal(2, _mux.Mux(id).AttachedClients);
                break;
            case SharedCloseChoice.Detach:
                Assert.True(task.Result);
                PumpUntil(() => _mux.Mux(id).AttachedClients == 1, "the daemon saw the detach");
                Assert.False(_mux.Mux(id).IsExited);
                // The other instance is still attached: only the detach that leaves nobody marks it
                // (spec §7.7). The prompt's Detach sending the mark is pinned by
                // Detach_in_the_shared_prompt_marks_the_session_detached_by_user.
                Assert.False(_mux.Mux(id).DetachedByUser);
                break;
            case SharedCloseChoice.Close:
                Assert.True(task.Result);
                PumpUntil(() => !_mux.Server.GetSessionIds().Contains(id), "the shell was killed");
                PumpUntil(() => theirs.Session.WasKilledElsewhere, "the other instance was told who ended it");
                // The closer ended it: its own pane is never told "ended from another window".
                PumpFor(200);
                Assert.False(mineSession.WasKilledElsewhere);
                Assert.DoesNotContain(TerminalPane.MuxKilledElsewhereBanner, MuxTestText.VisibleText(mine.Buffer!), StringComparison.Ordinal);
                break;
        }
    }

    /// <summary>
    /// Review Focus 6 through two GUIs: a Close in one window reaches the other window's pane through its
    /// real ProcessExited and ShellExitPolicy path, which says who ended it. The closing pane never says so.
    /// </summary>
    [AvaloniaFact]
    public void Close_in_one_window_shows_ended_from_another_window_in_the_other()
    {
        MainWindow other = CreateOtherWindow();
        Settings(other).ShellExitPolicy = "Graceful";
        TerminalPane theirs = AllPanes(other).Single();
        Guid id = ((MuxClientSession)theirs.Session!).Id;
        MainWindow window = CreateWindow();
        TerminalPane mine = AttachShared(window, id);
        PumpUntil(() => _mux.Mux(id).AttachedClients == 2, "both windows attached");
        var mineSession = (MuxClientSession)mine.Session!; // the pane lets go of it on close
        int asked = -1;
        window.ConfirmSharedClose = others =>
        {
            asked = others;
            return Task.FromResult(SharedCloseChoice.Close);
        };
        var close = typeof(MainWindow).GetMethod("ClosePaneAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var task = (Task<bool>)close.Invoke(window, [mine, false])!;
        PumpUntil(() => task.IsCompleted, "the close finished");

        Assert.True(task.Result);
        Assert.Equal(1, asked);
        PumpUntil(() => MuxTestText.VisibleText(theirs.Buffer!).Contains(TerminalPane.MuxKilledElsewhereBanner, StringComparison.Ordinal),
            "the other window's pane says the shell was ended from another window");
        Assert.Contains(theirs, AllPanes(other)); // Graceful and a kill's exit code -1: the pane stays
        Assert.DoesNotContain(mine, AllPanes(window));
        Assert.False(mineSession.WasKilledElsewhere);
        Assert.DoesNotContain(TerminalPane.MuxKilledElsewhereBanner, MuxTestText.VisibleText(mine.Buffer!), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void A_lone_pane_is_decided_without_the_shared_prompt()
    {
        MainWindow window = CreateWindow();
        Settings(window).PaneClosePolicy = "Force";
        TerminalPane own = AllPanes(window).Single();
        int asked = -1;
        window.ConfirmSharedClose = others =>
        {
            asked = others;
            return Task.FromResult(SharedCloseChoice.Cancel);
        };
        var decide = typeof(MainWindow).GetMethod("DecidePaneCloseAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var task = (Task<SharedCloseChoice>)decide.Invoke(window, [own])!;
        PumpUntil(() => task.IsCompleted, "the decision finished");

        Assert.Equal(SharedCloseChoice.Close, task.Result);
        Assert.Equal(-1, asked);
    }

    [AvaloniaFact]
    public void Killed_elsewhere_shows_the_ended_from_another_window_banner()
    {
        MainWindow window = CreateWindow();
        Settings(window).ShellExitPolicy = "Graceful";
        (MuxClient other, ClientPaneModel theirs) = OtherInstance();
        Guid id = theirs.Session.Id;
        TerminalPane mine = AttachShared(window, id);

        Task.Run(() => other.KillAsync(id)).GetAwaiter().GetResult();

        PumpUntil(() => MuxTestText.VisibleText(mine.Buffer!).Contains(TerminalPane.MuxKilledElsewhereBanner, StringComparison.Ordinal), "the banner is shown");
        Assert.Contains(mine, AllPanes(window)); // Graceful and a kill's exit code -1: the pane stays
    }

    [AvaloniaFact]
    public void Mux_commands_are_registered_with_persistence_on()
    {
        MainWindow window = CreateWindow();

        typeof(MainWindow).GetMethod("SetupCommandPalette", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);

        Assert.Contains(CommandRegistry.GetCommands(), c => c.Id == "attach_session");
        Assert.Contains(CommandRegistry.GetCommands(), c => c.Id == "detach_pane");
    }

    [AvaloniaFact]
    public void Mux_commands_are_not_registered_when_persistence_is_off()
    {
        MainWindow window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            CommandAssist = TestCommandAssistServices.Instance,
        });
        Assert.Null(window.MuxHost);

        typeof(MainWindow).GetMethod("SetupCommandPalette", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);

        Assert.DoesNotContain(CommandRegistry.GetCommands(), c => c.Id is "attach_session" or "detach_pane");
    }

    [AvaloniaFact]
    public void Detach_pane_marks_the_session_detached_by_user()
    {
        MainWindow window = CreateWindow();
        (_, ClientPaneModel theirs) = OtherInstance();
        Guid id = theirs.Session.Id;
        TerminalPane mine = AttachShared(window, id);
        PumpUntil(() => _mux.Mux(id).AttachedClients == 2, "both attached");
        theirs.Session.Dispose();                      // an ordinary detach: this window is now the last viewer
        PumpUntil(() => _mux.Mux(id).AttachedClients == 1, "the other instance left");
        Assert.False(_mux.Mux(id).DetachedByUser);

        Task<bool> detach = window.DetachPaneAsync(mine);
        PumpUntil(() => detach.IsCompleted, "the detach finished");

        PumpUntil(() => _mux.Mux(id).AttachedClients == 0, "the daemon saw the detach");
        Assert.True(_mux.Mux(id).DetachedByUser);
        Assert.False(_mux.Mux(id).IsExited);
    }

    /// <summary>Fix round 1: the real shared-close dialog, driven through its buttons (and its X).</summary>
    [AvaloniaTheory]
    [InlineData(1, "Cancel", "Cancel")]
    [InlineData(2, "Detach", "Detach")]
    [InlineData(1, "Close (ends it)", "Close")]
    [InlineData(2, null, "Cancel")] // closed with X
    public void The_default_shared_close_dialog_is_worded_and_maps_each_button(int others, string? button, string expectedName)
    {
        MainWindow window = CreateWindow();
        SharedCloseChoice expected = Enum.Parse<SharedCloseChoice>(expectedName);

        Task<SharedCloseChoice> answer = window.ConfirmSharedClose(others);
        PumpUntil(() => window.OwnedWindows.Count == 1, "the dialog opened");

        Window dialog = Assert.Single(window.OwnedWindows);
        Assert.Equal("Close Shared Shell", dialog.Title);
        var descendants = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(dialog).ToList();
        string headline = others == 1 ? "1 other window is attached to this shell." : $"{others} other windows are attached to this shell.";
        Assert.Contains(descendants.OfType<TextBlock>(), t => t.Text == headline);
        List<Button> buttons = descendants.OfType<Button>().ToList();
        Assert.Equal(["Cancel", "Detach", "Close (ends it)"], buttons.Select(b => b.Content as string));

        if (button is null) dialog.Close();
        else buttons.Single(b => b.Content as string == button).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        PumpUntil(() => answer.IsCompleted, "the dialog closed");
        Assert.Equal(expected, answer.Result);
        Assert.Empty(window.OwnedWindows);
    }

    /// <summary>Fix round 1: a tab close whose shared pane is answered Detach keeps that shell; the tab's own pane is closed.</summary>
    [AvaloniaFact]
    public void Closing_a_split_tab_with_Detach_for_the_shared_pane_keeps_its_shell()
    {
        MainWindow window = CreateWindow();
        Settings(window).PaneClosePolicy = "Force"; // the tab's own pane closes without a prompt
        (_, ClientPaneModel theirs) = OtherInstance();
        Guid id = theirs.Session.Id;
        TerminalPane mine = AttachShared(window, id);
        PumpUntil(() => _mux.Mux(id).AttachedClients == 2, "both attached");
        TabItem tab = TabOf(window, mine);
        var before = AllPanes(window).ToHashSet();
        typeof(MainWindow).GetProperty("_currentPane", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, mine);
        typeof(MainWindow).GetMethod("SplitPane", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [Avalonia.Layout.Orientation.Horizontal]);
        TerminalPane sibling = AllPanes(window).Single(p => !before.Contains(p));
        PumpUntil(() => sibling.Session is MuxClientSession { IsAttached: true }, "the split pane attached");
        Guid siblingId = ((MuxClientSession)sibling.Session!).Id;
        int asked = 0;
        window.ConfirmSharedClose = _ =>
        {
            asked++;
            LeaveWhileThePromptIsOpen(theirs, id);
            return Task.FromResult(SharedCloseChoice.Detach);
        };

        var close = (Task<bool>)typeof(MainWindow).GetMethod("CloseTabAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [tab, false])!;
        PumpUntil(() => close.IsCompleted, "the tab close finished");

        Assert.True(close.Result);
        Assert.Equal(1, asked); // only the shared pane asks
        Assert.DoesNotContain(mine, AllPanes(window));
        Assert.DoesNotContain(sibling, AllPanes(window));
        PumpUntil(() => _mux.Mux(id).AttachedClients == 0, "the daemon saw the detach");
        Assert.True(_mux.Mux(id).DetachedByUser);
        Assert.False(_mux.Mux(id).IsExited);
        PumpUntil(() => !_mux.Server.GetSessionIds().Contains(siblingId), "the tab's own pane was closed, its shell killed");
    }

    /// <summary>
    /// Fix round 1: the prompt's Detach is a user detach (spec §7.7). The daemon marks only the detach
    /// that leaves nobody attached, so the other instance leaves while the prompt is open.
    /// </summary>
    [AvaloniaFact]
    public void Detach_in_the_shared_prompt_marks_the_session_detached_by_user()
    {
        MainWindow window = CreateWindow();
        (_, ClientPaneModel theirs) = OtherInstance();
        Guid id = theirs.Session.Id;
        TerminalPane mine = AttachShared(window, id);
        PumpUntil(() => _mux.Mux(id).AttachedClients == 2, "both attached");
        window.ConfirmSharedClose = _ =>
        {
            LeaveWhileThePromptIsOpen(theirs, id);
            return Task.FromResult(SharedCloseChoice.Detach);
        };

        var close = (Task<bool>)typeof(MainWindow).GetMethod("ClosePaneAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [mine, false])!;
        PumpUntil(() => close.IsCompleted, "the close finished");

        Assert.True(close.Result);
        PumpUntil(() => _mux.Mux(id).AttachedClients == 0, "the daemon saw the detach");
        Assert.True(_mux.Mux(id).DetachedByUser);
        Assert.False(_mux.Mux(id).IsExited);
    }

    /// <summary>
    /// The other instance detaches (an ordinary detach) and the daemon has seen it. Runs inside the prompt
    /// seam, on the UI thread: it waits on the server alone and never pumps the dispatcher.
    /// </summary>
    private void LeaveWhileThePromptIsOpen(ClientPaneModel theirs, Guid id)
    {
        theirs.Session.Dispose();
        Assert.True(SpinWait.SpinUntil(() => _mux.Mux(id).AttachedClients == 1, 10_000), "the other instance left");
    }

    /// <summary>Fix round 1: a pane whose shell already exited has nothing to keep running; Detach closes it plainly.</summary>
    [AvaloniaFact]
    public void Detaching_a_pane_whose_shell_exited_closes_it_without_the_kept_running_toast()
    {
        MainWindow window = CreateWindow();
        Settings(window).ShellExitPolicy = "Never"; // the dead pane stays for the command to act on
        (_, ClientPaneModel theirs) = OtherInstance(); // keeps the exited session from being reaped
        Guid id = theirs.Session.Id;
        TerminalPane mine = AttachShared(window, id);
        var mineSession = (MuxClientSession)mine.Session!;
        _mux.Fake(id).Exit(0);
        PumpUntil(() => !mineSession.IsProcessRunning, "the pane saw the exit");

        Task<bool> detach = window.DetachPaneAsync(mine);
        PumpUntil(() => detach.IsCompleted, "the detach finished");

        Assert.True(detach.Result);
        Assert.DoesNotContain(mine, AllPanes(window));
        PumpFor(200);
        Assert.NotEqual("Shell detached", Toast(window).Title);
        PumpUntil(() => _mux.Mux(id).AttachedClients == 1, "the daemon saw the pane go");
        Assert.False(_mux.Mux(id).DetachedByUser);
    }

    /// <summary>Fix round 1: detaching the last pane closes the window; the queued user detach survives the host's teardown.</summary>
    [AvaloniaFact]
    public void Detaching_the_last_pane_closes_the_window_and_the_shell_stays_detached_by_user()
    {
        MainWindow window = CreateWindow();
        TerminalPane own = AllPanes(window).Single();
        Guid id = ((MuxClientSession)own.Session!).Id;
        PumpUntil(() => _mux.Mux(id).AttachedClients == 1, "the pane attached");

        Task<bool> detach = window.DetachPaneAsync(own);
        PumpUntil(() => detach.IsCompleted, "the detach finished");

        Assert.True(detach.Result);
        PumpUntil(() => !window.IsVisible, "the window closed with its last tab");
        _host!.Dispose(); // the teardown's host flush, whether or not the close already ran it
        PumpUntil(() => _mux.Mux(id).AttachedClients == 0, "the daemon saw the detach");
        Assert.Contains(id, _mux.Server.GetSessionIds());
        Assert.True(_mux.Mux(id).DetachedByUser);
        Assert.False(_mux.Mux(id).IsExited);
    }

    /// <summary>
    /// Fix round 1: a lone pane's close decision spends one daemon budget, not one per read. The sharing
    /// read times out after the whole (one-second) budget, so the child-process read is handed what is
    /// left of it: nothing, where a fresh budget per read would hand it a second full one.
    /// </summary>
    [AvaloniaFact]
    public void A_stalled_daemon_costs_a_lone_pane_close_one_budget()
    {
        var gate = new GatedStream(_mux.Listener.Connect());
        _host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(gate, null, ct), "test", null);
        MainWindow window = CreateWindowOn(_host);
        Settings(window).PaneClosePolicy = "Force";
        TerminalPane own = AllPanes(window).Single();
        var decide = typeof(MainWindow).GetMethod("DecidePaneCloseAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        gate.Pause(); // nothing more reaches the daemon: both reads can only time out
        try
        {
            var task = (Task<SharedCloseChoice>)decide.Invoke(window, [own])!;
            PumpUntil(() => task.IsCompleted, "the decision finished");

            Assert.Equal(SharedCloseChoice.Close, task.Result);
            TimeSpan? second = window.LastPaneCloseRefreshBudgetForTest;
            Assert.NotNull(second);
            Assert.True(second <= TimeSpan.FromMilliseconds(100), $"the second read got {second}, not what was left of the one budget");
        }
        finally
        {
            gate.Resume();
        }
    }

    /// <summary>
    /// Final review: an agent's close (no prompt it could answer) of a shell another window shows lets go
    /// of it instead of ending it; the other window keeps a working shell.
    /// </summary>
    [AvaloniaFact]
    public void An_agent_close_of_a_shared_shell_detaches_and_the_other_window_keeps_it()
    {
        MainWindow other = CreateOtherWindow();
        TerminalPane theirs = AllPanes(other).Single();
        var theirSession = (MuxClientSession)theirs.Session!;
        Guid id = theirSession.Id;
        MainWindow window = CreateWindow();
        TerminalPane mine = AttachShared(window, id);
        var mineSession = (MuxClientSession)mine.Session!; // the pane lets go of it on close
        PumpUntil(() => mineSession.AttachedClients == 2, "the pane knows the shell is shared");

        Task<bool> close = ((Ntilde.AgentHost.IAgentActionExecutor)window).ClosePaneAsync(mine.PaneId);
        PumpUntil(() => close.IsCompleted, "the agent's close finished");

        Assert.True(close.Result);
        Assert.DoesNotContain(mine, AllPanes(window));
        PumpUntil(() => _mux.Mux(id).AttachedClients == 1, "the daemon saw the pane go");
        Assert.Contains(id, _mux.Server.GetSessionIds());
        Assert.False(_mux.Mux(id).IsExited);
        Assert.False(_mux.Mux(id).DetachedByUser); // not a deliberate detach
        Assert.False(theirSession.WasKilledElsewhere);

        theirSession.SendInput("still-here");
        PumpUntil(() => string.Concat(_mux.Fake(id).SentInput).Contains("still-here", StringComparison.Ordinal), "the other window's input reaches the shell");
        _mux.Fake(id).Emit("echoed-back");
        PumpUntil(() => MuxTestText.VisibleText(theirs.Buffer!).Contains("echoed-back", StringComparison.Ordinal), "the other window shows the shell's output");
    }

    /// <summary>Final review: a shared shell that already exited has nothing a Close would end for anyone; no prompt.</summary>
    [AvaloniaFact]
    public void A_shared_pane_whose_shell_exited_is_closed_without_the_shared_prompt()
    {
        MainWindow window = CreateWindow();
        Settings(window).ShellExitPolicy = "Never"; // the dead pane stays for the close to act on
        (_, ClientPaneModel theirs) = OtherInstance(); // keeps the exited session from being reaped
        Guid id = theirs.Session.Id;
        TerminalPane mine = AttachShared(window, id);
        var mineSession = (MuxClientSession)mine.Session!;
        PumpUntil(() => _mux.Mux(id).AttachedClients == 2, "both attached");
        _mux.Fake(id).Exit(0);
        PumpUntil(() => !mineSession.IsProcessRunning, "the pane saw the exit");
        int asked = -1;
        window.ConfirmSharedClose = others =>
        {
            asked = others;
            return Task.FromResult(SharedCloseChoice.Cancel);
        };
        var decide = typeof(MainWindow).GetMethod("DecidePaneCloseAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var task = (Task<SharedCloseChoice>)decide.Invoke(window, [mine])!;
        PumpUntil(() => task.IsCompleted, "the decision finished");

        Assert.Equal(-1, asked);
        Assert.Equal(SharedCloseChoice.Close, task.Result);
    }

    /// <summary>Final review: a persistent pane that lost its daemon connection is not "not a persistent shell".</summary>
    [AvaloniaFact]
    public void Detaching_a_disconnected_persistent_pane_says_the_connection_was_lost()
    {
        MainWindow window = CreateWindow();
        TerminalPane own = AllPanes(window).Single();
        var session = (MuxClientSession)own.Session!;
        _host!.Dispose();
        PumpUntil(() => !session.IsConnected, "the connection is gone");

        Task<bool> detach = window.DetachPaneAsync(own);
        PumpUntil(() => detach.IsCompleted, "the detach finished");

        Assert.False(detach.Result);
        PumpUntil(() => Toast(window).Title == "Pane: Detach", "the detach notice is shown");
        Assert.Equal("The multiplexer connection was lost.", Toast(window).Message);
    }

    /// <summary>A client-side stream whose writes can be held back: a daemon that stopped reading.</summary>
    private sealed class GatedStream(Stream inner) : Stream
    {
        private readonly ManualResetEventSlim _open = new(initialState: true);

        public void Pause() => _open.Reset();
        public void Resume() => _open.Set();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { _open.Wait(); inner.Flush(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { _open.Wait(); inner.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { _open.Wait(); inner.Write(buffer); }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _open.Set();
                inner.Dispose();
                _open.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
