using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Mux;
using Ntilde.Mux.Tests.Support;
using Ntilde.Shell;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Controls; // FakeTerminalSession, RecordingSessionFactory

namespace Ntilde.Tests.Core;

/// <summary>
/// Task 17: "Quit and close all shells" ends every shell the local daemon runs (this window's, a shared one, a
/// detached one), shuts the daemon down and closes the window, and the Settings link that starts it.
/// </summary>
public sealed class MainWindowQuitAndCloseAllTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    private const string CommandTitle = "Session: Quit and Close All Shells";

    private readonly MuxTestHost _mux = new();
    private readonly List<MuxConnectionHost> _hosts = [];
    private int _shutdowns;

    public MainWindowQuitAndCloseAllTests(TestAppDataRoot appData)
    {
        _ = appData;
        if (File.Exists(AppPaths.SessionFilePath)) File.Delete(AppPaths.SessionFilePath);
        _mux.Server.ShutdownRequested += () => Interlocked.Increment(ref _shutdowns);
    }

    public void Dispose()
    {
        TestMainWindowFactory.DisposeCreatedWindows();
        foreach (MuxConnectionHost host in _hosts) host.Dispose();
        _mux.Dispose();
    }

    /// <param name="keepPlaceholders">The restored background tabs stay startup placeholders (<see cref="TestMainWindowFactory.KeepStartupPlaceholders"/>).</param>
    /// <param name="beforeShow">Runs just before the window is shown: its restored selected tab is built, and has not spawned yet.</param>
    private MainWindow CreateWindow(Func<int, Task<bool>> confirm, string persistence = SessionPersistenceMode.KeepOnClose,
        bool keepPlaceholders = false, Action<MainWindow>? beforeShow = null)
    {
        var host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(_mux.Listener.Connect(), null, ct), "test", null);
        _hosts.Add(host);
        var factory = new MuxTerminalSessionFactory(
            new MuxConnectionHosts(host, _ => null), new RecordingSessionFactory(new FakeTerminalSession()), null);
        MainWindow window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            CommandAssist = TestCommandAssistServices.Instance,
            SessionFactory = factory,
            Settings = new TerminalSettings { SessionPersistence = persistence },
            // Long before any session file these tests write: a restore is never quietened by a reboot (spec R2).
            SessionsCannotPredateUtc = static () => new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        window.ConfirmQuitAndCloseAll = confirm;
        // The real probe would look for the machine's daemon; the test's daemon is the one to shut down.
        window.MuxProbeForUpdate = async ct => await _mux.ConnectClientAsync();
        window.MuxReadDescriptorForUpdate = () => null;
        if (keepPlaceholders) TestMainWindowFactory.KeepStartupPlaceholders(window);
        beforeShow?.Invoke(window);
        window.Show();
        PumpUntil(() => LocalSession(window) is { IsAttached: true }, "the first pane attached");
        return window;
    }

    private static MuxClientSession? LocalSession(MainWindow window) =>
        window.AllPanesForTest().Select(p => p.Session).OfType<MuxClientSession>().FirstOrDefault();

    private static void PumpUntil(Func<bool> condition, string because, int ms = 10_000)
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

    /// <summary>The window's own shell, a second one it attached, and a third nobody shows (detached).</summary>
    private (Guid Own, Guid Attached, Guid Detached) ThreeShells(MainWindow window)
    {
        Guid own = LocalSession(window)!.Id;
        (Guid attached, Guid detached) = Task.Run(async () =>
        {
            MuxClient c = await _mux.ConnectClientAsync();
            return (await MuxTestHost.SpawnAsync(c), await MuxTestHost.SpawnAsync(c));
        }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        window.PickMuxSession = MuxPickerChoice.Session(attached);
        Task attach = window.AttachToMuxSessionAsync();
        PumpUntil(() => attach.IsCompleted, "the attach command finished");
        PumpUntil(() => window.AllPanesForTest().Any(p => p.Session is MuxClientSession { IsAttached: true } m && m.Id == attached), "the second shell attached");
        return (own, attached, detached);
    }

    [AvaloniaFact]
    public void Confirmed_ends_every_shell_shuts_the_daemon_down_and_closes_the_window()
    {
        var asked = new List<int>();
        MainWindow window = CreateWindow(count =>
        {
            asked.Add(count);
            return Task.FromResult(true);
        });
        (Guid own, Guid attached, Guid detached) = ThreeShells(window);

        Task quit = window.QuitAndCloseAllShellsAsync();
        PumpUntil(() => quit.IsCompleted && !window.IsVisible, "the window closed");

        Assert.Equal([3], asked);
        PumpUntil(() => !_mux.Server.GetSessionIds().Contains(own) && !_mux.Server.GetSessionIds().Contains(attached) && !_mux.Server.GetSessionIds().Contains(detached),
            "all three shells were killed");
        PumpUntil(() => Volatile.Read(ref _shutdowns) == 1, "the daemon received shutdown");
        // None is named for reattach: the next launch must not report them lost.
        string saved = File.ReadAllText(AppPaths.SessionFilePath);
        Assert.DoesNotContain(own.ToString(), saved);
        Assert.DoesNotContain(attached.ToString(), saved);
    }

    [AvaloniaFact]
    public void Declined_does_nothing()
    {
        var asked = new List<int>();
        MainWindow window = CreateWindow(count =>
        {
            asked.Add(count);
            return Task.FromResult(false);
        });
        (Guid own, Guid attached, Guid detached) = ThreeShells(window);

        Task quit = window.QuitAndCloseAllShellsAsync();
        PumpUntil(() => quit.IsCompleted, "the command returned");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([3], asked);
        Assert.True(window.IsVisible);
        Assert.Equal(0, Volatile.Read(ref _shutdowns));
        foreach (Guid id in new[] { own, attached, detached })
        {
            Assert.Contains(id, _mux.Server.GetSessionIds());
            Assert.False(_mux.Mux(id).IsExited);
        }
    }

    // ── PR #511 review (Greptile P2): restored tabs not shown yet ──

    /// <summary>Shells running in <paramref name="mux"/>'s daemon that no client shows, as a window's Keep left them.</summary>
    internal static Guid[] SpawnUnshown(MuxTestHost mux, int count) => Task.Run(async () =>
    {
        MuxClient client = await mux.ConnectClientAsync();
        var ids = new Guid[count];
        for (int i = 0; i < count; i++) ids[i] = await MuxTestHost.SpawnAsync(client);
        return ids;
    }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    /// <summary>A pane as the window saved it, reopening local daemon session <paramref name="id"/>; <paramref name="shared"/> for one joined through "Attach to session…".</summary>
    internal static Ntilde.Pty.PaneNode LocalLeaf(Guid id, bool shared = false) => new()
    {
        Type = Ntilde.Pty.NodeType.Leaf,
        Command = "scripted",
        MuxSessionId = id.ToString("D"),
        MuxEndpoint = MuxEndpointId.Local.ToString(),
        MuxShared = shared,
    };

    /// <summary>A session file with one tab per root, the first selected: the others are not shown, so their panes do not spawn.</summary>
    internal static void SaveTabs(params Ntilde.Pty.PaneNode[] roots)
    {
        var session = new Ntilde.Pty.NtildeSession { ActiveTabIndex = 0 };
        foreach (Ntilde.Pty.PaneNode root in roots) session.Tabs.Add(new Ntilde.Pty.TabSession { Title = $"tab {session.Tabs.Count}", Root = root });
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.SessionFilePath)!);
        File.WriteAllText(AppPaths.SessionFilePath, System.Text.Json.JsonSerializer.Serialize(session, Ntilde.Pty.SessionSerializationContext.Default.NtildeSession));
    }

    private static List<Guid> AttachedIds(MainWindow window) =>
        window.AllPanesForTest().Select(p => p.Session).OfType<MuxClientSession>().Where(m => m.IsAttached).Select(m => m.Id).ToList();

    /// <summary>
    /// The window the next launch opens over the same daemon restores <paramref name="tabs"/> tabs whose panes name no
    /// daemon session: each, shown in turn, starts a fresh shell - none of <paramref name="ended"/> - and none says a
    /// previous session was lost, or raises any other persistence notice. Heard from the panes, from before each spawns.
    /// </summary>
    internal static void AssertTheNextWindowStartsFreshShellsQuietly(Func<Action<MainWindow>, MainWindow> createWindow, int tabs, IReadOnlyCollection<Guid> ended)
    {
        var notices = new List<string>();
        var heard = new HashSet<TerminalPane>();
        void Listen(MainWindow w)
        {
            foreach (TerminalPane p in w.AllPanesForTest())
                if (heard.Add(p)) p.PersistenceNotice += (_, title, _, _) => notices.Add(title);
        }

        MainWindow next = createWindow(Listen);
        PumpUntil(() => next.AllPanesForTest().Count == tabs, "the next window built every restored tab");
        Listen(next); // before each is first shown: it spawns only then
        Assert.All(next.AllPanesForTest(), p => Assert.Null(p.MuxSessionIdToRestore));
        TabControl tabControl = next.FindControl<TabControl>("Tabs")!;
        for (int i = 1; i < tabs; i++)
        {
            tabControl.SelectedIndex = i;
            int shown = i + 1;
            PumpUntil(() => AttachedIds(next).Count == shown, $"tab {i} started a shell");
        }

        Assert.Empty(AttachedIds(next).Intersect(ended));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(tabs, heard.Count);
        Assert.Empty(notices);
    }

    /// <summary>
    /// PR #511 review (Greptile P2): the quit ends every shell the daemon runs, those of restored tabs not shown yet among
    /// them - built (a pane holding the id pending) or still a startup placeholder - and a share's, which the stop ends as
    /// surely. None stays named in the session file, so the next window starts each tab's fresh shell quietly, with no
    /// "previous session lost" notice.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Quitting_with_tabs_not_shown_yet_names_none_of_their_shells_for_the_next_launch(bool placeholders)
    {
        Guid[] ids = SpawnUnshown(_mux, 4);
        SaveTabs(LocalLeaf(ids[0]), LocalLeaf(ids[1]), LocalLeaf(ids[2]), LocalLeaf(ids[3], shared: true));
        MainWindow window = CreateWindow(_ => Task.FromResult(true), keepPlaceholders: placeholders);
        if (placeholders)
        {
            Assert.Equal(3, TestMainWindowFactory.PlaceholderTabs(window));
        }
        else
        {
            PumpUntil(() => window.AllPanesForTest().Count == 4, "every restored tab was built");
            Assert.Equal(3, window.AllPanesForTest().Count(p => p.MuxSessionIdToRestore is not null));
        }

        Task quit = window.QuitAndCloseAllShellsAsync();
        PumpUntil(() => quit.IsCompleted && !window.IsVisible, "the window closed");

        PumpUntil(() => !ids.Any(id => _mux.Server.GetSessionIds().Contains(id)), "every shell was killed");
        string saved = File.ReadAllText(AppPaths.SessionFilePath);
        Assert.All(ids, id => Assert.DoesNotContain(id.ToString(), saved, StringComparison.Ordinal));
        AssertTheNextWindowStartsFreshShellsQuietly(listen => CreateWindow(_ => Task.FromResult(false), beforeShow: listen), 4, ids);
    }

    private static readonly Ntilde.Mux.Contracts.MuxEndpointDescriptor Described =
        new() { Endpoint = "test", Pid = 4242, ProcessName = "Ntilde", MinVersion = 1, MaxVersion = 2 };

    /// <summary>The quit's stop fails: none answers its probe (<paramref name="reached"/> false), or it never exits after <c>shutdown</c>.</summary>
    private static void FailTheStop(MainWindow window, bool reached)
    {
        if (reached)
        {
            window.MuxReadDescriptorForUpdate = () => Described;
            window.MuxWaitForDaemonExitForUpdate = _ => Task.FromResult(false);
        }
        else
        {
            window.MuxProbeForUpdate = _ => Task.FromResult<MuxClient?>(null);
        }
    }

    /// <summary>
    /// PR #511 residual M1: the listing before the quit timed out, so no shell of a tab not shown yet is killed by name, and
    /// the daemon's stop fails (nothing answers it, or it never exits). Those shells still run, so they stay named for the
    /// next launch, which reopens them in their tabs - only the shown tab's shell, ended through the host, is dropped.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void A_quit_that_cannot_list_or_stop_keeps_the_shells_of_tabs_not_shown_yet_named(bool reached, bool placeholders)
    {
        Guid[] ids = SpawnUnshown(_mux, 3);
        SaveTabs(LocalLeaf(ids[0]), LocalLeaf(ids[1]), LocalLeaf(ids[2]));
        var asked = new List<int>();
        MainWindow window = CreateWindow(count => { asked.Add(count); return Task.FromResult(true); }, keepPlaceholders: placeholders);
        if (!placeholders) PumpUntil(() => window.AllPanesForTest().Count(p => p.MuxSessionIdToRestore is not null) == 2, "every restored tab was built");
        window.MuxListSessionsForClose = (_, _) => throw new TimeoutException("scripted: the listing timed out");
        FailTheStop(window, reached);

        Task quit = window.QuitAndCloseAllShellsAsync();
        PumpUntil(() => quit.IsCompleted && !window.IsVisible, "the window closed");

        Assert.Equal([-1], asked); // the count was unknown
        PumpUntil(() => !_mux.Server.GetSessionIds().Contains(ids[0]), "the shown tab's shell was ended through the host");
        Assert.Contains(ids[1], _mux.Server.GetSessionIds());
        Assert.Contains(ids[2], _mux.Server.GetSessionIds());
        string saved = File.ReadAllText(AppPaths.SessionFilePath);
        Assert.DoesNotContain(ids[0].ToString(), saved, StringComparison.Ordinal);
        Assert.Contains(ids[1].ToString(), saved, StringComparison.Ordinal);
        Assert.Contains(ids[2].ToString(), saved, StringComparison.Ordinal);
    }

    /// <summary>
    /// PR #511 residual M1, the other half: when the stop fails but the listing worked, each shell of a tab not shown yet
    /// that was killed by name is known to be gone, and is dropped from the session file as before.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_quit_whose_stop_fails_still_drops_the_shells_it_killed_by_name(bool reached)
    {
        Guid[] ids = SpawnUnshown(_mux, 3);
        SaveTabs(LocalLeaf(ids[0]), LocalLeaf(ids[1]), LocalLeaf(ids[2]));
        MainWindow window = CreateWindow(_ => Task.FromResult(true), keepPlaceholders: true);
        FailTheStop(window, reached);

        Task quit = window.QuitAndCloseAllShellsAsync();
        PumpUntil(() => quit.IsCompleted && !window.IsVisible, "the window closed");

        PumpUntil(() => !ids.Any(id => _mux.Server.GetSessionIds().Contains(id)), "every shell was killed");
        string saved = File.ReadAllText(AppPaths.SessionFilePath);
        Assert.All(ids, id => Assert.DoesNotContain(id.ToString(), saved, StringComparison.Ordinal));
    }

    [AvaloniaTheory]
    [InlineData(SessionPersistenceMode.KeepOnClose, true)]
    [InlineData(SessionPersistenceMode.Off, false)]
    public void The_palette_command_exists_only_while_persistence_is_on(string persistence, bool registered)
    {
        MainWindow window = CreateWindow(_ => Task.FromResult(false));
        if (persistence == SessionPersistenceMode.Off)
        {
            // The factory follows the setting at runtime; the window's host outlives the flip.
            ((TerminalSettings)typeof(MainWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!)
                .SessionPersistence = SessionPersistenceMode.Off;
            typeof(MainWindow).GetMethod("ApplySessionPersistenceSetting", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
        }

        typeof(MainWindow).GetMethod("SetupCommandPalette", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);

        Assert.Equal(registered, CommandRegistry.GetCommands().Any(c => c.Title == CommandTitle));
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_settings_link_follows_whether_persistence_is_active(bool active)
    {
        var settings = new SettingsWindow { SessionPersistenceActive = active };

        Button link = settings.FindControl<Button>("QuitAndCloseAllShellsLink")!;

        Assert.Equal(active, link.IsVisible);
        Assert.Equal("Quit and close all shells…", link.Content);
    }

    [AvaloniaFact]
    public void The_settings_link_asks_the_owner_and_closes_without_saving()
    {
        var settings = new SettingsWindow { SessionPersistenceActive = true };
        var owner = new Window();
        owner.Show();
        Task<bool> result = settings.ShowDialog<bool>(owner);
        Dispatcher.UIThread.RunJobs();

        settings.FindControl<Button>("QuitAndCloseAllShellsLink")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        PumpUntil(() => result.IsCompleted, "settings closed");
        Assert.True(settings.QuitAndCloseAllRequested);
        Assert.False(result.Result);
    }
}
