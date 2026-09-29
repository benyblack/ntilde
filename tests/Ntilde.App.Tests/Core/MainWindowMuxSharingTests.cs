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

    public MainWindowMuxSharingTests()
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
}
