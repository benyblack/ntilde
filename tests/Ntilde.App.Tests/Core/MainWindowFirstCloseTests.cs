using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Mux;
using Ntilde.Mux.Tests.Support;
using Ntilde.Shell;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Controls; // FakeTerminalSession, RecordingSessionFactory
using Ntilde.Tests.Shell.Mux;
using FirstCloseAction = Ntilde.MainWindow.FirstCloseAction;
using FirstCloseAnswer = Ntilde.MainWindow.FirstCloseAnswer;

namespace Ntilde.Tests.Core;

/// <summary>
/// Spec R1: the first time a window closes with live local shells it asks whether they keep running, and
/// "Don't ask again" remembers the answer in a flag file under the app-data root. The window's panes spawn
/// into an in-memory MuxServer; the <see cref="MainWindow.ConfirmFirstClose"/> seam answers instead of a modal.
/// </summary>
/// <remarks>
/// <see cref="TestAppDataRoot"/> is taken for its lifetime: closing a real MainWindow saves the session, and the
/// Settings test writes settings.json and the default flag file, none of which may land in the developer's profile.
/// </remarks>
public sealed class MainWindowFirstCloseTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    private readonly TestAppDataRoot _appData;
    private readonly MuxTestHost _mux = new();
    private readonly List<MuxConnectionHost> _hosts = [];
    private readonly List<IDisposable> _owned = [];
    private readonly string _choiceRoot = Path.Combine(Path.GetTempPath(), $"ntilde_first_close_{Guid.NewGuid():N}");

    /// <summary>Every question any window of the test asked, by the count it was given.</summary>
    private readonly List<int> _asked = [];

    public MainWindowFirstCloseTests(TestAppDataRoot appData)
    {
        _appData = appData;
        if (File.Exists(AppPaths.SessionFilePath)) File.Delete(AppPaths.SessionFilePath);
        Directory.CreateDirectory(_choiceRoot);
    }

    public void Dispose()
    {
        TestMainWindowFactory.DisposeCreatedWindows();
        foreach (MuxConnectionHost host in _hosts) host.Dispose();
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _mux.Dispose();
        try { Directory.Delete(_choiceRoot, recursive: true); } catch { /* best effort */ }
    }

    private MuxCloseChoiceStore Store => new(_choiceRoot);

    private string FlagPath => Path.Combine(_choiceRoot, MuxCloseChoiceStore.FileName);

    private static Func<int, Task<FirstCloseAnswer>> Answer(FirstCloseAction action, bool remember = false) =>
        _ => Task.FromResult(new FirstCloseAnswer(action, remember));

    /// <summary>A window with session persistence on (unless <paramref name="persistence"/> says otherwise) over the test's daemon.</summary>
    /// <param name="keepPlaceholders">
    /// The restored background tabs stay placeholders for the window's life, as they are for a close that comes before the
    /// startup restore's background pass has built them (<see cref="TestMainWindowFactory.KeepStartupPlaceholders"/>).
    /// </param>
    private MainWindow CreateWindow(
        Func<int, Task<FirstCloseAnswer>> answer,
        string persistence = SessionPersistenceMode.KeepOnClose,
        Func<MuxEndpointId, MuxConnectionHost?>? createRemote = null,
        TimeSpan? disposeFlush = null,
        Func<Stream, Stream>? link = null,
        bool keepPlaceholders = false)
    {
        var host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(link is null ? _mux.Listener.Connect() : link(_mux.Listener.Connect()), null, ct), "test", null)
        {
            DisposeFlushTimeout = disposeFlush ?? TimeSpan.FromSeconds(1),
        };
        _hosts.Add(host);
        var factory = new MuxTerminalSessionFactory(
            new MuxConnectionHosts(host, createRemote ?? (_ => null)), new RecordingSessionFactory(new FakeTerminalSession()), null);
        MainWindow window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            CommandAssist = TestCommandAssistServices.Instance,
            SessionFactory = factory,
            Settings = new TerminalSettings { SessionPersistence = persistence },
        });
        window.MuxCloseChoiceStore = Store;
        window.ConfirmFirstClose = count =>
        {
            _asked.Add(count);
            return answer(count);
        };
        if (keepPlaceholders) TestMainWindowFactory.KeepStartupPlaceholders(window);
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

    private void AssertKeptRunning(Guid id)
    {
        PumpUntil(() => _mux.Mux(id).AttachedClients == 0, "the daemon saw the detach");
        Assert.Contains(id, _mux.Server.GetSessionIds());
        Assert.False(_mux.Mux(id).IsExited);
    }

    [AvaloniaFact]
    public void Closing_with_live_local_shells_asks_once()
    {
        var pending = new TaskCompletionSource<FirstCloseAnswer>();
        MainWindow window = CreateWindow(_ => pending.Task);
        MuxConnectionHost host = window.MuxHost!;
        Guid id = LocalSession(window)!.Id;

        window.Close();
        Dispatcher.UIThread.RunJobs();
        window.Close(); // a second close while the question is open is held, not asked again
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([1], _asked);
        Assert.True(window.IsVisible);

        pending.SetResult(new FirstCloseAnswer(FirstCloseAction.Cancel, Remember: false));
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsVisible);
        Assert.NotNull(host.CurrentClient); // nothing was torn down
        Assert.Equal(1, _mux.Mux(id).AttachedClients);
        Assert.Contains(id, _mux.Server.GetSessionIds());
        Assert.False(_mux.Mux(id).IsExited);
        Assert.Equal(0, host.PendingKillCountForTest);
        Assert.False(File.Exists(FlagPath));

        // Cancel is not an answer: the next close asks again.
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([1, 1], _asked);
        Assert.True(window.IsVisible);
    }

    [AvaloniaFact]
    public void Keep_running_detaches_and_the_shell_survives()
    {
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Keep));
        MuxConnectionHost host = window.MuxHost!;
        Guid id = LocalSession(window)!.Id;

        window.Close();
        PumpUntil(() => !window.IsVisible, "the window closed after the answer");

        Assert.Equal([1], _asked);
        Assert.Null(host.CurrentClient);
        AssertKeptRunning(id);
        Assert.False(File.Exists(FlagPath));
        Assert.Contains(id.ToString(), File.ReadAllText(AppPaths.SessionFilePath)); // the next launch reattaches it
    }

    /// <remarks>
    /// The link holds every write back for a while once the close starts, and no ping flush runs at teardown
    /// (<see cref="MuxConnectionHost.DisposeFlushTimeout"/> zero): the kill lands only because the host tracks it and
    /// waits for its reply before it disconnects. One sent and forgotten would still be held when the client closes
    /// its end, and be dropped with it.
    /// </remarks>
    [AvaloniaFact]
    public void Close_them_ends_the_shells()
    {
        DelayingStream? link = null;
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Close), disposeFlush: TimeSpan.Zero, link: s => link = new DelayingStream(s));
        MuxConnectionHost host = window.MuxHost!;
        Guid id = LocalSession(window)!.Id;
        MuxClient client = host.CurrentClient!;
        // Read the instant the connection closes: the daemon must have handled the kill by then.
        var runningAtDisconnect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += _ => runningAtDisconnect.TrySetResult(_mux.Server.GetSessionIds().Contains(id));
        link!.Delay = TimeSpan.FromMilliseconds(300);

        window.Close();
        PumpUntil(() => !window.IsVisible, "the window closed after the answer");

        Assert.Equal([1], _asked);
        Assert.Null(host.CurrentClient);
        PumpUntil(() => runningAtDisconnect.Task.IsCompleted, "the client disconnected");
        Assert.False(runningAtDisconnect.Task.Result, "the kill reached the daemon before the client disconnected");
        Assert.DoesNotContain(id, _mux.Server.GetSessionIds());
        Assert.False(File.Exists(FlagPath));
        // Not named for reattach: the next launch starts a fresh shell quietly instead of reporting this one lost.
        Assert.DoesNotContain(id.ToString(), File.ReadAllText(AppPaths.SessionFilePath));
    }

    /// <summary>
    /// Fix round 1: "Close them" never ends a shell another client is also typing into - a pane close never does
    /// that without asking (the shared-close question). That pane detaches, as Keep does; the unshared one is killed.
    /// </summary>
    [AvaloniaFact]
    public void Close_them_never_ends_a_shared_shell()
    {
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Close));
        Guid own = LocalSession(window)!.Id;
        // Another instance spawned and shows a shell; this window attaches it too ("Attach to session…").
        (MuxClient _, ClientPaneModel theirs) = Task.Run(async () =>
        {
            MuxClient c = await _mux.ConnectClientAsync();
            Guid id = await MuxTestHost.SpawnAsync(c);
            return (c, await MuxTestHost.AttachPaneAsync(c, id));
        }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        Guid shared = theirs.Session.Id;
        window.PickMuxSession = MuxPickerChoice.Session(shared);
        Task attach = window.AttachToMuxSessionAsync();
        PumpUntil(() => attach.IsCompleted, "the attach command finished");
        TerminalPane mine = window.AllPanesForTest().Single(p => p.Session is MuxClientSession m && m.Id == shared);
        PumpUntil(() => mine.MuxOtherClients == 1 && ((MuxClientSession)mine.Session!).InteractiveOthers == 1, "this window knows the shell is shared");

        window.Close();
        PumpUntil(() => !window.IsVisible, "the window closed after the answer");

        Assert.Equal([2], _asked); // both are counted as shells the close would otherwise keep
        Assert.DoesNotContain(own, _mux.Server.GetSessionIds());
        Assert.Contains(shared, _mux.Server.GetSessionIds());
        Assert.False(_mux.Mux(shared).IsExited);
        PumpUntil(() => _mux.Mux(shared).AttachedClients == 1, "this window detached; the other instance still shows it");
        Assert.Contains(shared.ToString(), File.ReadAllText(AppPaths.SessionFilePath)); // kept, so still named for reattach
    }

    /// <summary>
    /// Phase 5 Task 26 review, M1: a share whose attach is still in flight has no count yet that would say another client
    /// shows it - but it was joined because one does. "Close them" leaves it running, as it does a share it knows of. The
    /// unshared shell beside it, ended in the same pass and flushed by the same teardown, is the proof the kills went out.
    /// </summary>
    [AvaloniaFact]
    public void Close_them_never_ends_a_share_whose_attach_is_in_flight()
    {
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Close));
        Guid own = LocalSession(window)!.Id;
        (MuxClient other, ClientPaneModel theirs) = Task.Run(async () =>
        {
            MuxClient c = await _mux.ConnectClientAsync();
            Guid id = await MuxTestHost.SpawnAsync(c);
            return (c, await MuxTestHost.AttachPaneAsync(c, id));
        }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        Guid shared = theirs.Session.Id;
        using var parsing = new ManualResetEventSlim(); // holds the shell's parse thread: this window's attach is not answered
        Task<bool> held = _mux.Mux(shared).InvokeAsync(() => parsing.Wait(TimeSpan.FromSeconds(30)));
        try
        {
            window.PickMuxSession = MuxPickerChoice.Session(shared);
            Task attach = window.AttachToMuxSessionAsync();
            PumpUntil(() => attach.IsCompleted, "the attach command finished");
            TerminalPane mine = window.AllPanesForTest().Single(p => p.Session is MuxClientSession m && m.Id == shared);
            Assert.False(((MuxClientSession)mine.Session!).IsAttached);
            Assert.Equal((0, (int?)null), (mine.MuxOtherClients, ((MuxClientSession)mine.Session!).InteractiveOthers));

            window.Close();
            PumpUntil(() => !window.IsVisible, "the window closed after the answer");

            Assert.Equal([2], _asked);
            Assert.DoesNotContain(own, _mux.Server.GetSessionIds()); // the teardown waited for the kills' replies
            Assert.Contains(shared, _mux.Server.GetSessionIds());
        }
        finally
        {
            parsing.Set();
            Assert.True(held.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        }

        Assert.False(_mux.Mux(shared).IsExited);
        Assert.True(theirs.Session.IsAttached);
        GC.KeepAlive(other);
    }

    // ── Final review M3: restored tabs not shown yet hold their shells' ids pending ──

    /// <summary>
    /// A pane as the window saved it: a fresh shell when <paramref name="id"/> is null, else reopening local daemon session
    /// <paramref name="id"/>; <paramref name="shared"/> for one joined through "Attach to session…".
    /// </summary>
    private static Ntilde.Pty.PaneNode LocalLeaf(Guid? id = null, bool shared = false) => new()
    {
        Type = Ntilde.Pty.NodeType.Leaf,
        Command = "scripted",
        MuxSessionId = id?.ToString("D"),
        MuxEndpoint = id is null ? null : MuxEndpointId.Local.ToString(),
        MuxShared = shared,
    };

    /// <summary>A session file with one tab per root, the first selected: the others are built but not shown, so their panes do not spawn.</summary>
    private static void SaveTabs(params Ntilde.Pty.PaneNode[] roots)
    {
        var session = new Ntilde.Pty.NtildeSession { ActiveTabIndex = 0 };
        foreach (Ntilde.Pty.PaneNode root in roots) session.Tabs.Add(new Ntilde.Pty.TabSession { Title = $"tab {session.Tabs.Count}", Root = root });
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.SessionFilePath)!);
        File.WriteAllText(AppPaths.SessionFilePath, System.Text.Json.JsonSerializer.Serialize(session, Ntilde.Pty.SessionSerializationContext.Default.NtildeSession));
    }

    /// <summary>Shells running in the test's daemon that no client shows, as a window's Keep left them.</summary>
    private Guid[] SpawnUnshown(int count) => Task.Run(async () =>
    {
        MuxClient client = await _mux.ConnectClientAsync();
        var ids = new Guid[count];
        for (int i = 0; i < count; i++) ids[i] = await MuxTestHost.SpawnAsync(client);
        return ids;
    }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    /// <summary>A shell in the test's daemon that the user detached ("Detach"): no client shows it, and startup never adopts it.</summary>
    private Guid SpawnDetachedByUser() => Task.Run(async () =>
    {
        MuxClient client = await _mux.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(client, id);
        pane.Session.Detach(userDetached: true);
        await TestWait.UntilAsync(() => _mux.Mux(id).DetachedByUser && _mux.Mux(id).AttachedClients == 0, "the daemon recorded the user detach");
        return id;
    }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    private static int PendingPanes(MainWindow window) => window.AllPanesForTest().Count(p => p.MuxSessionIdToRestore is not null);

    /// <summary>
    /// Final review M3: a restore brings back 5 tabs and the user looks at one. The 4 not shown yet hold their shells' ids
    /// pending, and those shells run all the same: "Close them" counts all 5 and ends all 5, and the session file names
    /// none of them, so they do not come back at the next launch.
    /// </summary>
    [AvaloniaFact]
    public void Close_them_counts_and_ends_the_shells_of_tabs_not_shown_yet()
    {
        Guid[] ids = SpawnUnshown(5);
        SaveTabs([.. ids.Select(id => LocalLeaf(id))]);
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Close));
        PumpUntil(() => window.AllPanesForTest().Count == 5, "every restored tab was built");
        Assert.Equal(4, PendingPanes(window));

        window.Close();
        PumpUntil(() => !window.IsVisible, "the window closed after the answer");

        Assert.Equal([5], _asked);
        Assert.All(ids, id => Assert.DoesNotContain(id, _mux.Server.GetSessionIds()));
        string saved = File.ReadAllText(AppPaths.SessionFilePath);
        Assert.All(ids, id => Assert.DoesNotContain(id.ToString(), saved, StringComparison.Ordinal));
    }

    /// <summary>
    /// Final review M3: the only running shells are in tabs not shown yet (the shown tab's shell exited). The question is
    /// still asked, counting them, and Keep keeps them, named for the next launch.
    /// </summary>
    [AvaloniaFact]
    public void Shells_only_in_tabs_not_shown_yet_are_still_asked_about()
    {
        Guid[] ids = SpawnUnshown(2);
        SaveTabs(LocalLeaf(), LocalLeaf(ids[0]), LocalLeaf(ids[1]));
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Keep));
        PumpUntil(() => window.AllPanesForTest().Count == 3, "every restored tab was built");
        Assert.Equal(2, PendingPanes(window));
        MuxClientSession shown = LocalSession(window)!;
        // Non-zero: under the default "Graceful" ShellExitPolicy the pane stays (exit 0 would close the tab).
        _mux.Fake(shown.Id).Exit(3);
        PumpUntil(() => !shown.IsProcessRunning, "the pane saw the exit");

        window.Close();
        PumpUntil(() => !window.IsVisible, "the window closed after the answer");

        Assert.Equal([2], _asked);
        Assert.All(ids, id => Assert.Contains(id, _mux.Server.GetSessionIds()));
        string saved = File.ReadAllText(AppPaths.SessionFilePath);
        Assert.All(ids, id => Assert.Contains(id.ToString(), saved, StringComparison.Ordinal));
    }

    /// <summary>
    /// Final review M3: tabs not shown yet whose shells no longer run (ended while Ntilde was closed) leave nothing to keep:
    /// once the daemon has said so, the window closes without asking.
    /// </summary>
    [AvaloniaFact]
    public void Tabs_not_shown_yet_whose_shells_are_gone_do_not_ask()
    {
        SaveTabs(LocalLeaf(), LocalLeaf(Guid.NewGuid()), LocalLeaf(Guid.NewGuid()));
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Cancel));
        PumpUntil(() => window.AllPanesForTest().Count == 3, "every restored tab was built");
        Assert.Equal(2, PendingPanes(window));
        MuxClientSession shown = LocalSession(window)!;
        _mux.Fake(shown.Id).Exit(3);
        PumpUntil(() => !shown.IsProcessRunning, "the pane saw the exit");

        window.Close();
        PumpUntil(() => !window.IsVisible, "the window closed without asking");

        Assert.Empty(_asked);
    }

    /// <summary>
    /// Final review M3, as for live panes: "Close them" never ends a pending shell another client may be typing into. A
    /// share's pending id ("Attach to session…" joined it shared) is neither counted nor ended; nor is a pending id the
    /// daemon says another client shows (an <c>ntilde mux attach</c> in a terminal, say). The shown tab's shell and the
    /// window's own pending one beside them are ended.
    /// </summary>
    [AvaloniaFact]
    public void Close_them_never_ends_a_pending_share_or_a_pending_shell_another_client_shows()
    {
        Guid[] own = SpawnUnshown(1);
        (Guid shared, Guid shownElsewhere, ClientPaneModel[] theirs) = Task.Run(async () =>
        {
            MuxClient other = await _mux.ConnectClientAsync();
            Guid a = await MuxTestHost.SpawnAsync(other);
            Guid b = await MuxTestHost.SpawnAsync(other);
            ClientPaneModel[] panes = [await MuxTestHost.AttachPaneAsync(other, a), await MuxTestHost.AttachPaneAsync(other, b)];
            return (a, b, panes);
        }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        SaveTabs(LocalLeaf(), LocalLeaf(own[0]), LocalLeaf(shared, shared: true), LocalLeaf(shownElsewhere));
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Close));
        PumpUntil(() => window.AllPanesForTest().Count == 4, "every restored tab was built");
        Assert.Equal(3, PendingPanes(window));
        Guid first = LocalSession(window)!.Id;

        window.Close();
        PumpUntil(() => !window.IsVisible, "the window closed after the answer");

        Assert.Equal([2], _asked); // the shown tab's shell and the window's own pending one
        Assert.DoesNotContain(first, _mux.Server.GetSessionIds());
        Assert.DoesNotContain(own[0], _mux.Server.GetSessionIds());
        Assert.False(_mux.Mux(shared).IsExited);
        Assert.False(_mux.Mux(shownElsewhere).IsExited);
        string saved = File.ReadAllText(AppPaths.SessionFilePath);
        Assert.Contains(shared.ToString(), saved, StringComparison.Ordinal);
        Assert.Contains(shownElsewhere.ToString(), saved, StringComparison.Ordinal);
        GC.KeepAlive(theirs);
    }

    /// <summary>
    /// Residual N2: the question can stay open for minutes. A client that opens a pending shell meanwhile (an
    /// <c>ntilde mux attach</c>, another window's Attach to session…) keeps it: "Close them" asks the daemon again just
    /// before it ends anything, and ends only what is still shown by nobody. The shown tab's shell is ended as asked.
    /// </summary>
    [AvaloniaFact]
    public void Close_them_spares_a_pending_shell_another_client_opened_while_the_question_was_open()
    {
        Guid[] own = SpawnUnshown(1);
        SaveTabs(LocalLeaf(), LocalLeaf(own[0]));
        ClientPaneModel? theirs = null;
        MainWindow window = CreateWindow(_ =>
        {
            theirs = Task.Run(async () => await MuxTestHost.AttachPaneAsync(await _mux.ConnectClientAsync(), own[0]), TestContext.Current.CancellationToken)
                .GetAwaiter().GetResult();
            return Task.FromResult(new FirstCloseAnswer(FirstCloseAction.Close, Remember: false));
        });
        PumpUntil(() => window.AllPanesForTest().Count == 2, "every restored tab was built");
        Guid first = LocalSession(window)!.Id;

        window.Close();
        PumpUntil(() => !window.IsVisible, "the window closed after the answer");

        Assert.Equal([2], _asked); // asked when nobody else showed it
        Assert.DoesNotContain(first, _mux.Server.GetSessionIds());
        Assert.Contains(own[0], _mux.Server.GetSessionIds());
        Assert.False(_mux.Mux(own[0]).IsExited);
        Assert.True(theirs!.Session.IsAttached);
        Assert.Contains(own[0].ToString(), File.ReadAllText(AppPaths.SessionFilePath), StringComparison.Ordinal);
    }

    /// <summary>
    /// Residual N3: the listing for tabs not shown yet fails in a way nobody planned for. That is "none" - the safe answer,
    /// those shells keep running - and the window still closes: the close is never left held for good.
    /// </summary>
    [AvaloniaFact]
    public void A_listing_that_throws_anything_never_leaves_the_window_unclosable()
    {
        Guid[] ids = SpawnUnshown(1);
        SaveTabs(LocalLeaf(), LocalLeaf(ids[0]));
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Close));
        window.MuxListSessionsForClose = (_, _) => throw new InvalidOperationException("not planned for");
        PumpUntil(() => window.AllPanesForTest().Count == 2, "every restored tab was built");
        MuxClientSession shown = LocalSession(window)!;
        _mux.Fake(shown.Id).Exit(3);
        PumpUntil(() => !shown.IsProcessRunning, "the pane saw the exit");

        window.Close();
        for (int i = 0; i < 50 && window.IsVisible; i++) { Thread.Sleep(10); Dispatcher.UIThread.RunJobs(); }
        if (window.IsVisible) window.Close(); // the next attempt
        PumpUntil(() => !window.IsVisible, "the window closed");

        Assert.Empty(_asked);
        Assert.Contains(ids[0], _mux.Server.GetSessionIds());
    }

    /// <summary>
    /// Final review M3: a remembered "Close them" (Don't ask again) ends the shells of tabs not shown yet too, without
    /// asking - once the daemon has said no other client shows them - and they are not named for the next launch.
    /// </summary>
    [AvaloniaFact]
    public void A_remembered_close_ends_the_shells_of_tabs_not_shown_yet()
    {
        Guid[] ids = SpawnUnshown(2);
        SaveTabs(LocalLeaf(), LocalLeaf(ids[0]), LocalLeaf(ids[1]));
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Cancel));
        Store.Remember(MuxCloseChoice.Close);
        PumpUntil(() => window.AllPanesForTest().Count == 3, "every restored tab was built");
        Guid first = LocalSession(window)!.Id;

        window.Close();
        PumpUntil(() => !window.IsVisible, "the window closed");

        Assert.Empty(_asked);
        Assert.DoesNotContain(first, _mux.Server.GetSessionIds());
        Assert.All(ids, id => Assert.DoesNotContain(id, _mux.Server.GetSessionIds()));
        string saved = File.ReadAllText(AppPaths.SessionFilePath);
        Assert.All(ids, id => Assert.DoesNotContain(id.ToString(), saved, StringComparison.Ordinal));
    }

    // ── PR #511 review (Greptile P1): startup tabs not built yet are placeholders, not panes ──

    private static int PlaceholderTabs(MainWindow window) => TestMainWindowFactory.PlaceholderTabs(window);

    /// <summary>
    /// A restore brings back three tabs; a remembered "Close them" closes the window before the two background tabs are
    /// built. Their shells run all the same, named only in the placeholders' saved trees: all three are ended, without
    /// asking, and the session file names none of them.
    /// </summary>
    [AvaloniaFact]
    public void A_remembered_close_ends_the_shells_of_tabs_still_holding_placeholders()
    {
        Guid[] ids = SpawnUnshown(3);
        SaveTabs([.. ids.Select(id => LocalLeaf(id))]);
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Cancel), keepPlaceholders: true);
        Store.Remember(MuxCloseChoice.Close);
        Assert.Single(window.AllPanesForTest());
        Assert.Equal(2, PlaceholderTabs(window));

        window.Close();
        PumpUntil(() => !window.IsVisible, "the window closed");

        Assert.Empty(_asked);
        Assert.All(ids, id => Assert.DoesNotContain(id, _mux.Server.GetSessionIds()));
        string saved = File.ReadAllText(AppPaths.SessionFilePath);
        Assert.All(ids, id => Assert.DoesNotContain(id.ToString(), saved, StringComparison.Ordinal));
    }

    /// <summary>The same restore, asked: the question counts the placeholders' shells too, and "Close them" ends all three.</summary>
    [AvaloniaFact]
    public void Close_them_counts_and_ends_the_shells_of_tabs_still_holding_placeholders()
    {
        Guid[] ids = SpawnUnshown(3);
        SaveTabs([.. ids.Select(id => LocalLeaf(id))]);
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Close), keepPlaceholders: true);
        Assert.Equal(2, PlaceholderTabs(window));

        window.Close();
        PumpUntil(() => !window.IsVisible, "the window closed after the answer");

        Assert.Equal([3], _asked);
        Assert.All(ids, id => Assert.DoesNotContain(id, _mux.Server.GetSessionIds()));
        string saved = File.ReadAllText(AppPaths.SessionFilePath);
        Assert.All(ids, id => Assert.DoesNotContain(id.ToString(), saved, StringComparison.Ordinal));
    }

    /// <summary>
    /// A placeholder's tree can name a share ("Attach to session…" joined it). It is never counted or ended, even when the
    /// daemon says nobody shows it now - the saved node's share mark decides, as a pane's does - and it stays named for the
    /// next launch. Its own-shell sibling in the same split, and the shown tab's shell, are counted and ended.
    /// </summary>
    [AvaloniaFact]
    public void Close_them_never_ends_a_share_in_a_tab_still_holding_a_placeholder()
    {
        Guid[] ids = SpawnUnshown(3);
        Guid own = ids[1], shared = ids[2];
        var split = new Ntilde.Pty.PaneNode { Type = Ntilde.Pty.NodeType.Split, SplitOrientation = 0, Children = [LocalLeaf(own), LocalLeaf(shared, shared: true)] };
        SaveTabs(LocalLeaf(ids[0]), split);
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Close), keepPlaceholders: true);
        Assert.Equal(1, PlaceholderTabs(window));

        window.Close();
        PumpUntil(() => !window.IsVisible, "the window closed after the answer");

        Assert.Equal([2], _asked); // the shown tab's shell and the placeholder's own one
        Assert.DoesNotContain(ids[0], _mux.Server.GetSessionIds());
        Assert.DoesNotContain(own, _mux.Server.GetSessionIds());
        Assert.Contains(shared, _mux.Server.GetSessionIds());
        Assert.False(_mux.Mux(shared).IsExited);
        string saved = File.ReadAllText(AppPaths.SessionFilePath);
        Assert.Contains(shared.ToString(), saved, StringComparison.Ordinal);
        Assert.DoesNotContain(own.ToString(), saved, StringComparison.Ordinal);
    }

    /// <summary>
    /// PR #511 residual M4: the other exclusions, in a placeholder's tree. A leaf a reboot or logoff already ended (spec R2's
    /// quiet mark), and a leaf on a remote endpoint whose id the local daemon happens to run too, are neither counted nor
    /// ended - even though the local daemon says nobody shows that id - and both stay named for the next launch.
    /// </summary>
    /// <remarks>
    /// The remote row's local shell is one the user detached: a crash orphan that no local leaf names would be adopted into
    /// a tab of its own at startup, and that tab's pending id is rightly the window's to count and end.
    /// </remarks>
    [AvaloniaTheory]
    [InlineData("quiet-lost")]
    [InlineData("remote")]
    public void Close_them_never_ends_a_quiet_lost_or_remote_leaf_in_a_tab_still_holding_a_placeholder(string kind)
    {
        Guid[] ids = [SpawnUnshown(1)[0], kind == "remote" ? SpawnDetachedByUser() : SpawnUnshown(1)[0]];
        Ntilde.Pty.PaneNode leaf = LocalLeaf(ids[1]);
        if (kind == "remote") leaf.MuxEndpoint = MuxEndpointId.ForSsh(Guid.NewGuid()).ToString();
        else leaf.MuxQuietPreviousLost = true;
        SaveTabs(LocalLeaf(ids[0]), leaf);
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Close), keepPlaceholders: true);
        Assert.Equal(1, PlaceholderTabs(window));

        window.Close();
        PumpUntil(() => !window.IsVisible, "the window closed after the answer");

        Assert.Equal([1], _asked); // the shown tab's shell only
        Assert.DoesNotContain(ids[0], _mux.Server.GetSessionIds());
        Assert.Contains(ids[1], _mux.Server.GetSessionIds());
        Assert.False(_mux.Mux(ids[1]).IsExited);
        Assert.Contains(ids[1].ToString(), File.ReadAllText(AppPaths.SessionFilePath), StringComparison.Ordinal);
    }

    /// <summary>
    /// PR #511 residual M2: a hand-edited or merged session file can hold a null child in a split. A close before that tab
    /// is built walks its saved tree without throwing, and the leaf beside the null is counted and ended as any other.
    /// </summary>
    [AvaloniaFact]
    public void A_null_child_in_a_placeholders_tree_is_skipped()
    {
        Guid[] ids = SpawnUnshown(2);
        var split = new Ntilde.Pty.PaneNode { Type = Ntilde.Pty.NodeType.Split, SplitOrientation = 0, Children = [null!, LocalLeaf(ids[1])] };
        SaveTabs(LocalLeaf(ids[0]), split);
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Close), keepPlaceholders: true);
        Assert.Equal(1, PlaceholderTabs(window));

        window.Close();
        PumpUntil(() => !window.IsVisible, "the window closed after the answer");

        Assert.Equal([2], _asked);
        Assert.All(ids, id => Assert.DoesNotContain(id, _mux.Server.GetSessionIds()));
        string saved = File.ReadAllText(AppPaths.SessionFilePath);
        Assert.All(ids, id => Assert.DoesNotContain(id.ToString(), saved, StringComparison.Ordinal));
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Dont_ask_again_remembers_the_answer(bool keep)
    {
        FirstCloseAction action = keep ? FirstCloseAction.Keep : FirstCloseAction.Close;
        MainWindow first = CreateWindow(Answer(action, remember: true));

        first.Close();
        PumpUntil(() => !first.IsVisible, "the first window closed after the answer");

        Assert.Equal(keep ? "keep" : "close", File.ReadAllText(FlagPath));

        // A later window over the same daemon, with a new shell of its own (no session file to restore).
        if (File.Exists(AppPaths.SessionFilePath)) File.Delete(AppPaths.SessionFilePath);
        MainWindow second = CreateWindow(Answer(FirstCloseAction.Cancel));
        MuxConnectionHost host = second.MuxHost!;
        Guid id = LocalSession(second)!.Id;

        second.Close();
        Assert.False(second.IsVisible); // closed at once: nothing was asked, nothing posted

        Assert.Equal([1], _asked); // the first window's question only
        Assert.Null(host.CurrentClient);
        if (keep)
        {
            AssertKeptRunning(id);
        }
        else
        {
            Assert.DoesNotContain(id, _mux.Server.GetSessionIds()); // killed, and the kill flushed before the disconnect
        }
    }

    [AvaloniaTheory]
    [InlineData("exited")]
    [InlineData("remote")]
    [InlineData("persistence off")]
    public void No_question_without_live_local_shells(string why)
    {
        MainWindow window;
        switch (why)
        {
            case "exited":
                window = CreateWindow(Answer(FirstCloseAction.Cancel));
                MuxClientSession mux = LocalSession(window)!;
                // Non-zero: under the default "Graceful" ShellExitPolicy the pane stays (exit 0 would close the window).
                _mux.Fake(mux.Id).Exit(3);
                PumpUntil(() => !mux.IsProcessRunning, "the pane saw the exit");
                break;
            case "remote":
                window = CreateRemoteOnlyWindow();
                break;
            default:
                window = CreateWindow(Answer(FirstCloseAction.Cancel), persistence: SessionPersistenceMode.Off);
                break;
        }

        window.Close();

        Assert.False(window.IsVisible);
        Assert.Empty(_asked);
    }

    // ---- release hardening item 1: persistence turned Off while shells were open; the close ends them, as "Close them" does

    /// <summary>Settings saved with SessionPersistence Off: the factory swaps, the open panes stay connected until the close.</summary>
    private static void TurnPersistenceOff(MainWindow window)
    {
        var settings = (TerminalSettings)typeof(MainWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        settings.SessionPersistence = SessionPersistenceMode.Off;
        typeof(MainWindow).GetMethod("ApplySessionPersistenceSetting", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
    }

    [AvaloniaFact]
    public void With_persistence_turned_off_a_close_never_ends_a_shared_shell()
    {
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Keep));
        Guid own = LocalSession(window)!.Id;
        (MuxClient _, ClientPaneModel theirs) = Task.Run(async () =>
        {
            MuxClient c = await _mux.ConnectClientAsync();
            Guid id = await MuxTestHost.SpawnAsync(c);
            return (c, await MuxTestHost.AttachPaneAsync(c, id));
        }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        Guid shared = theirs.Session.Id;
        window.PickMuxSession = MuxPickerChoice.Session(shared);
        Task attach = window.AttachToMuxSessionAsync();
        PumpUntil(() => attach.IsCompleted, "the attach command finished");
        TerminalPane mine = window.AllPanesForTest().Single(p => p.Session is MuxClientSession m && m.Id == shared);
        PumpUntil(() => mine.MuxOtherClients == 1 && ((MuxClientSession)mine.Session!).InteractiveOthers == 1, "this window knows the shell is shared");
        TurnPersistenceOff(window);

        window.Close();
        PumpUntil(() => !window.IsVisible, "the window closed");

        Assert.Empty(_asked);   // Off asks nothing
        Assert.DoesNotContain(own, _mux.Server.GetSessionIds());
        Assert.Contains(shared, _mux.Server.GetSessionIds());
        PumpUntil(() => _mux.Mux(shared).AttachedClients == 1, "this window detached; the other instance still shows it");
    }

    /// <summary>
    /// Tabs still holding placeholders keep their shells' ids pending: once the daemon has said no other client shows them,
    /// the close ends them too and the session file names none of them. A pending share is never ended.
    /// </summary>
    [AvaloniaFact]
    public void With_persistence_turned_off_a_close_ends_the_shells_of_tabs_still_holding_placeholders()
    {
        Guid[] ids = SpawnUnshown(4);
        SaveTabs(LocalLeaf(ids[0]), LocalLeaf(ids[1]), LocalLeaf(ids[2]), LocalLeaf(ids[3], shared: true));
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Keep), keepPlaceholders: true);
        Assert.Equal(3, PlaceholderTabs(window));
        TurnPersistenceOff(window);

        window.Close();
        PumpUntil(() => !window.IsVisible, "the window closed");

        Assert.Empty(_asked);
        Assert.All(ids[..3], id => Assert.DoesNotContain(id, _mux.Server.GetSessionIds()));
        Assert.Contains(ids[3], _mux.Server.GetSessionIds());
        string saved = File.ReadAllText(AppPaths.SessionFilePath);
        Assert.All(ids[..3], id => Assert.DoesNotContain(id.ToString(), saved, StringComparison.Ordinal));
    }

    /// <summary>A shutdown cannot be held for the daemon's answer: the live shells end, the pending ones are left as they are.</summary>
    [AvaloniaTheory]
    [InlineData(WindowCloseReason.OSShutdown)]
    [InlineData(WindowCloseReason.ApplicationShutdown)]
    public void With_persistence_turned_off_a_shutdown_ends_the_live_shells(WindowCloseReason reason)
    {
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Keep));
        Guid id = LocalSession(window)!.Id;
        TurnPersistenceOff(window);

        Assert.False(window.HandleClosingForTest(reason));
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(_asked);
        Assert.DoesNotContain(id, _mux.Server.GetSessionIds());
    }

    /// <summary>A window whose only pane shows a live shell on a remote daemon (a second in-memory one).</summary>
    private MainWindow CreateRemoteOnlyWindow()
    {
        var remoteMux = new MuxTestHost();
        _owned.Add(remoteMux);
        Guid remoteSession = Task.Run(async () =>
        {
            MuxClient c = await remoteMux.ConnectClientAsync();
            return await MuxTestHost.SpawnAsync(c);
        }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        MuxEndpointId remoteId = MuxEndpointId.ForSsh(Guid.NewGuid());
        var remoteHost = new MuxConnectionHost(ct => MuxClient.ConnectAsync(remoteMux.Listener.Connect(), null, ct), "remote", null, MuxHostPolicy.Remote("box"));
        _hosts.Add(remoteHost);
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Cancel), createRemote: id => id == remoteId ? remoteHost : null);
        TerminalPane pane = window.AllPanesForTest().Single();

        // Until SSH requests are routed to a remote endpoint, a stand-in factory opens it there.
        pane.SessionFactory = new RemoteEndpointFactory(window.MuxHosts!, remoteId, remoteSession);
        pane.Reconnect();
        PumpUntil(() => pane.Session is MuxClientSession { IsConnected: true, IsProcessRunning: true } m && m.Id == remoteSession, "the pane shows the remote shell");
        Assert.Equal(remoteId.ToString(), pane.MuxEndpoint);
        return window;
    }

    /// <summary>Opens an existing session on the remote endpoint's host.</summary>
    private sealed class RemoteEndpointFactory(MuxConnectionHosts hosts, MuxEndpointId endpoint, Guid sessionId) : IPersistentSessionFactory
    {
        public Ntilde.Pty.ITerminalSession Create(Ntilde.Pty.TerminalSessionRequest request) => CreatePersistent(request).Session!;

        public PersistentSessionResult CreatePersistent(Ntilde.Pty.TerminalSessionRequest request)
        {
            MuxClient client = hosts.GetOrCreate(endpoint)!.GetClient(TimeSpan.FromSeconds(5))
                ?? throw new InvalidOperationException("the remote host did not connect");
            return new(client.OpenSession(sessionId, request.Command, request.Arguments), PersistentSessionOutcome.Spawned, endpoint.ToString(), null);
        }
    }

    /// <summary>The OS ending the session never asks and never kills, whatever was remembered: it behaves like Keep.</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void OS_shutdown_never_asks(bool closeRemembered)
    {
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Close));
        if (closeRemembered) Store.Remember(MuxCloseChoice.Close);
        MuxConnectionHost host = window.MuxHost!;
        Guid id = LocalSession(window)!.Id;

        bool held = window.HandleClosingForTest(WindowCloseReason.OSShutdown);

        Assert.False(held);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(_asked);
        Assert.Null(host.CurrentClient); // the teardown ran
        AssertKeptRunning(id);
    }

    /// <summary>
    /// Fix round 1: the application lifetime's shutdown (macOS Cmd+Q, TryShutdown) never asks - holding it would
    /// cancel the shutdown - but applies a remembered answer: a remembered "close" ends the shells. With nothing
    /// remembered it keeps them.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("nothing")]
    [InlineData("keep")]
    [InlineData("close")]
    public void Application_shutdown_applies_a_remembered_answer_without_asking(string remembered)
    {
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Close));
        if (remembered != "nothing") Store.Remember(remembered == "close" ? MuxCloseChoice.Close : MuxCloseChoice.Keep);
        MuxConnectionHost host = window.MuxHost!;
        Guid id = LocalSession(window)!.Id;

        bool held = window.HandleClosingForTest(WindowCloseReason.ApplicationShutdown);

        Assert.False(held);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(_asked);
        Assert.Null(host.CurrentClient); // the teardown ran
        if (remembered == "close")
        {
            Assert.DoesNotContain(id, _mux.Server.GetSessionIds()); // killed, flushed before the disconnect
        }
        else
        {
            AssertKeptRunning(id);
        }
    }

    // ---- release hardening item 2: macOS Cmd+Q is the usual quit, so the application's shutdown request asks too (R19 amended)

    /// <summary>What App does with the lifetime's ShutdownRequested: true cancels it; the window quits through the callback.</summary>
    private static bool RequestQuit(MainWindow window, Action quit, bool osShutdown = false) =>
        window.HoldShutdownForFirstClose(osShutdown, quit);

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cmd_Q_asks_and_Keep_quits_with_the_shells_kept(bool remember)
    {
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Keep, remember));
        Guid id = LocalSession(window)!.Id;
        int quits = 0;

        Assert.True(RequestQuit(window, () => quits++));
        PumpUntil(() => quits == 1, "the settled close quit the application");

        Assert.Equal([1], _asked);
        Assert.False(window.IsVisible);
        AssertKeptRunning(id);
        Assert.Equal(remember, File.Exists(FlagPath));   // "Don't ask again" is reachable from Cmd+Q
    }

    [AvaloniaFact]
    public void Cmd_Q_Close_them_ends_the_shells_and_quits()
    {
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Close));
        Guid id = LocalSession(window)!.Id;
        int quits = 0;

        Assert.True(RequestQuit(window, () => quits++));
        PumpUntil(() => quits == 1, "the settled close quit the application");

        Assert.Equal([1], _asked);
        Assert.False(window.IsVisible);
        Assert.DoesNotContain(id, _mux.Server.GetSessionIds());
    }

    [AvaloniaFact]
    public void Cmd_Q_Cancel_stays_open_and_does_not_quit()
    {
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Cancel));
        Guid id = LocalSession(window)!.Id;
        int quits = 0;

        Assert.True(RequestQuit(window, () => quits++));
        PumpUntil(() => _asked.Count == 1, "the question was asked");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, quits);
        Assert.True(window.IsVisible);
        Assert.Equal(1, _mux.Mux(id).AttachedClients);

        // Cancel is not an answer: the next Cmd+Q asks again.
        Assert.True(RequestQuit(window, () => quits++));
        PumpUntil(() => _asked.Count == 2, "asked again");
    }

    /// <summary>A remembered answer needs no question, so the shutdown is not held: the close applies it, as before.</summary>
    [AvaloniaTheory]
    [InlineData("keep")]
    [InlineData("close")]
    public void Cmd_Q_with_a_remembered_answer_is_not_held_and_applies_it(string remembered)
    {
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Cancel));
        Store.Remember(remembered == "close" ? MuxCloseChoice.Close : MuxCloseChoice.Keep);
        Guid id = LocalSession(window)!.Id;

        Assert.False(RequestQuit(window, () => Assert.Fail("not held: the lifetime shuts down on its own")));
        Assert.False(window.HandleClosingForTest(WindowCloseReason.ApplicationShutdown));
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(_asked);
        if (remembered == "close") Assert.DoesNotContain(id, _mux.Server.GetSessionIds());
        else AssertKeptRunning(id);
    }

    /// <summary>A remembered "Close them" with tabs not shown yet waits for the daemon's answer: held, then quits.</summary>
    [AvaloniaFact]
    public void Cmd_Q_with_a_remembered_close_and_tabs_not_shown_yet_is_held_until_they_end()
    {
        Guid[] ids = SpawnUnshown(2);
        SaveTabs([.. ids.Select(id => LocalLeaf(id))]);
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Cancel), keepPlaceholders: true);
        Store.Remember(MuxCloseChoice.Close);
        int quits = 0;

        Assert.True(RequestQuit(window, () => quits++));
        PumpUntil(() => quits == 1, "the settled close quit the application");

        Assert.Empty(_asked);
        Assert.All(ids, id => Assert.DoesNotContain(id, _mux.Server.GetSessionIds()));
    }

    [AvaloniaFact]
    public void An_OS_shutdown_request_is_never_held()
    {
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Close));

        Assert.False(RequestQuit(window, () => Assert.Fail("never held"), osShutdown: true));
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(_asked);
        Assert.True(window.IsVisible);
    }

    [AvaloniaFact]
    public void Cmd_Q_with_nothing_to_keep_is_not_held()
    {
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Close));
        MuxClientSession mux = LocalSession(window)!;
        _mux.Fake(mux.Id).Exit(3);
        PumpUntil(() => !mux.IsProcessRunning, "the pane saw the exit");

        Assert.False(RequestQuit(window, () => Assert.Fail("never held")));
        Assert.Empty(_asked);
    }

    /// <summary>Fix round 1: a close that the question's post has not reached yet, overtaken by a shutdown, never shows it.</summary>
    [AvaloniaFact]
    public void A_question_overtaken_by_a_shutdown_is_never_shown()
    {
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Close));
        Guid id = LocalSession(window)!.Id;

        window.Close(); // held; the question is posted, not yet run
        Assert.False(window.HandleClosingForTest(WindowCloseReason.OSShutdown));
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(_asked);
        AssertKeptRunning(id);
    }

    /// <summary>A shutdown while the question is open is not held; the answer that arrives afterwards is dropped.</summary>
    [AvaloniaFact]
    public void A_shutdown_while_the_question_is_open_drops_the_late_answer()
    {
        var pending = new TaskCompletionSource<FirstCloseAnswer>();
        MainWindow window = CreateWindow(_ => pending.Task);
        Guid id = LocalSession(window)!.Id;
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([1], _asked);

        Assert.False(window.HandleClosingForTest(WindowCloseReason.ApplicationShutdown));
        pending.SetResult(new FirstCloseAnswer(FirstCloseAction.Close, Remember: true));
        Dispatcher.UIThread.RunJobs();

        AssertKeptRunning(id); // no kill was sent for the late "Close them"
        Assert.False(File.Exists(FlagPath)); // nor was it remembered
    }

    /// <summary>A question that cannot be shown closes the window as Keep: nothing is lost, and the window is not stuck open.</summary>
    [AvaloniaFact]
    public void A_question_that_cannot_be_shown_closes_as_Keep()
    {
        MainWindow window = CreateWindow(_ => throw new InvalidOperationException("Cannot show window with non-visible owner."));
        Guid id = LocalSession(window)!.Id;

        window.Close();
        PumpUntil(() => !window.IsVisible, "the window closed anyway");

        Assert.Equal([1], _asked);
        AssertKeptRunning(id);
        Assert.False(File.Exists(FlagPath));
    }

    /// <summary>
    /// Fix round 1: the last tab closing while the question is open (its shell exited) closes the window: there is
    /// nothing left to ask about, and a Cancel would otherwise leave a window with no tabs. The late answer is dropped.
    /// </summary>
    [AvaloniaFact]
    public void A_close_with_nothing_left_to_keep_goes_through_while_the_question_is_open()
    {
        var pending = new TaskCompletionSource<FirstCloseAnswer>();
        MainWindow window = CreateWindow(_ => pending.Task);
        MuxClientSession mux = LocalSession(window)!;
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([1], _asked);
        _mux.Fake(mux.Id).Exit(3);
        PumpUntil(() => !mux.IsProcessRunning, "the pane saw the exit");

        window.Close();

        Assert.False(window.IsVisible);
        pending.SetResult(new FirstCloseAnswer(FirstCloseAction.Close, Remember: true));
        Dispatcher.UIThread.RunJobs();
        Assert.False(File.Exists(FlagPath));
    }

    /// <summary>Fix round 1: a window from the test factory never opens the real modal, whatever a test forgets to set.</summary>
    [AvaloniaFact]
    public void Test_windows_never_show_the_real_dialog()
    {
        var host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(_mux.Listener.Connect(), null, ct), "test", null);
        _hosts.Add(host);
        MainWindow window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            CommandAssist = TestCommandAssistServices.Instance,
            SessionFactory = new MuxTerminalSessionFactory(new MuxConnectionHosts(host, _ => null), new RecordingSessionFactory(new FakeTerminalSession()), null),
            Settings = new TerminalSettings { SessionPersistence = SessionPersistenceMode.KeepOnClose },
        });
        window.MuxCloseChoiceStore = Store;
        window.Show();
        PumpUntil(() => LocalSession(window) is { IsAttached: true }, "the first pane attached");

        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(window.OwnedWindows);
        Assert.True(window.IsVisible); // the factory's default answer is Cancel
    }

    [AvaloniaFact]
    public void A_window_close_reason_asks()
    {
        MainWindow window = CreateWindow(_ => new TaskCompletionSource<FirstCloseAnswer>().Task);

        bool held = window.HandleClosingForTest(WindowCloseReason.WindowClosing);
        Dispatcher.UIThread.RunJobs();

        Assert.True(held);
        Assert.Equal([1], _asked);
        Assert.NotNull(window.MuxHost!.CurrentClient);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Changing_the_setting_forgets_the_choice(bool change)
    {
        // The default store follows the app-data root, which this class's fixture points at a scratch directory.
        Assert.StartsWith(_appData.RootPath, MuxCloseChoiceStore.Default.FilePath, StringComparison.Ordinal);
        MuxCloseChoiceStore.Default.Remember(MuxCloseChoice.Keep);
        Assert.True(File.Exists(MuxCloseChoiceStore.Default.FilePath));
        try
        {
            var settings = new SettingsWindow();
            ComboBox list = settings.FindControl<ComboBox>("SessionPersistenceList")!;
            var loaded = (ComboBoxItem)list.SelectedItem!;
            if (change) list.SelectedItem = list.Items.Cast<ComboBoxItem>().First(i => !ReferenceEquals(i, loaded));

            typeof(SettingsWindow).GetMethod("SaveAndClose", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(settings, null);

            Assert.Equal(!change, File.Exists(MuxCloseChoiceStore.Default.FilePath));
        }
        finally
        {
            MuxCloseChoiceStore.Default.Forget();
            if (File.Exists(AppPaths.SettingsFilePath)) File.Delete(AppPaths.SettingsFilePath);
        }
    }

    /// <summary>
    /// Fix round 1: an Import or Restore inside Settings replaces settings.json and the window adopts it whatever the
    /// dialog does next; when that changed the effective persistence mode, the remembered answer is forgotten too.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_import_that_changes_the_setting_forgets_the_choice(bool change)
    {
        Assert.StartsWith(_appData.RootPath, MuxCloseChoiceStore.Default.FilePath, StringComparison.Ordinal);
        try
        {
            var settings = new SettingsWindow();
            string loaded = TerminalSettings.Load().SessionPersistence;
            string imported = change == SessionPersistenceMode.IsKeepOnClose(loaded) ? SessionPersistenceMode.Off : SessionPersistenceMode.KeepOnClose;
            new TerminalSettings { SessionPersistence = imported }.Save(); // what a successful Import/Restore leaves on disk
            MuxCloseChoiceStore.Default.Remember(MuxCloseChoice.Close);

            var reload = (Task)typeof(SettingsWindow).GetMethod("ReloadSettingsAfterExternalChangeAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(settings, null)!;
            PumpUntil(() => reload.IsCompleted, "the reload finished");

            Assert.Equal(!change, File.Exists(MuxCloseChoiceStore.Default.FilePath));
        }
        finally
        {
            MuxCloseChoiceStore.Default.Forget();
            if (File.Exists(AppPaths.SettingsFilePath)) File.Delete(AppPaths.SettingsFilePath);
        }
    }

    private static List<string?> Texts(Window dialog) =>
        dialog.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

    private static List<Button> Buttons(Window dialog) =>
        dialog.GetLogicalDescendants().OfType<Button>().Where(b => b is not ToggleButton).ToList();

    [AvaloniaFact]
    public void The_dialog_offers_keep_and_close_and_Escape_cancels()
    {
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Cancel));
        Dispatcher.UIThread.RunJobs(); // the window's startup focus jobs run before the dialog opens (see PressKey)

        (Window dialog, Task<FirstCloseAnswer> result) = window.BuildFirstCloseDialog(3);

        Assert.Equal("Close Ntilde", dialog.Title);
        List<string?> texts = Texts(dialog);
        Assert.Contains("Your shells keep running in the background.", texts);
        Assert.Contains("Reopen Ntilde to get them back. (3 shells)", texts);
        Assert.Contains("Turn this off in Settings → Keep shells running when the window closes.", texts);
        CheckBox dontAsk = Assert.Single(dialog.GetLogicalDescendants().OfType<CheckBox>());
        Assert.Equal("Don't ask again", dontAsk.Content);
        List<Button> buttons = Buttons(dialog);
        Assert.Equal(["Keep running", "Close them"], buttons.Select(b => b.Content as string ?? "").ToArray());
        Assert.True(buttons[0].IsDefault);
        Assert.False(buttons[1].IsDefault);

        dontAsk.IsChecked = true; // Escape still remembers nothing
        PressKey(dialog, PhysicalKey.Escape);

        PumpUntil(() => result.IsCompleted, "the dialog closed");
        Assert.Equal(new FirstCloseAnswer(FirstCloseAction.Cancel, Remember: false), result.Result);
    }

    [AvaloniaTheory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void The_dialogs_buttons_answer_with_the_checkbox(bool keep, bool dontAskAgain)
    {
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Cancel));
        (Window dialog, Task<FirstCloseAnswer> result) = window.BuildFirstCloseDialog(1);
        Assert.Contains("Reopen Ntilde to get them back.", Texts(dialog)); // one shell: no count
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Single(dialog.GetLogicalDescendants().OfType<CheckBox>()).IsChecked = dontAskAgain;
        Buttons(dialog)[keep ? 0 : 1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        PumpUntil(() => result.IsCompleted, "the dialog closed");
        Assert.Equal(new FirstCloseAnswer(keep ? FirstCloseAction.Keep : FirstCloseAction.Close, dontAskAgain), result.Result);
    }

    /// <summary>
    /// The production question (not the seam): a close from the taskbar can reach a minimized window, and a dialog that
    /// window owns would open out of sight, leaving a close that seems to do nothing. The window is restored first.
    /// </summary>
    [AvaloniaFact]
    public void The_question_restores_a_minimized_window_first()
    {
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Cancel));
        window.WindowState = WindowState.Minimized;
        Dispatcher.UIThread.RunJobs();

        var shown = (Task<FirstCloseAnswer>)typeof(MainWindow).GetMethod("ShowFirstCloseDialogAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, [2])!;
        Dispatcher.UIThread.RunJobs();

        Assert.NotEqual(WindowState.Minimized, window.WindowState);
        Window dialog = Assert.Single(window.OwnedWindows);
        Assert.Equal("Close Ntilde", dialog.Title);
        dialog.Close();
        PumpUntil(() => shown.IsCompleted, "the dialog closed");
        Assert.Equal(new FirstCloseAnswer(FirstCloseAction.Cancel, Remember: false), shown.Result);
    }

    [AvaloniaFact]
    public void Enter_keeps_the_shells()
    {
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Cancel));
        Dispatcher.UIThread.RunJobs();
        (Window dialog, Task<FirstCloseAnswer> result) = window.BuildFirstCloseDialog(2);

        PressKey(dialog, PhysicalKey.Enter);

        PumpUntil(() => result.IsCompleted, "the dialog closed");
        Assert.Equal(new FirstCloseAnswer(FirstCloseAction.Keep, Remember: false), result.Result);
    }

    /// <summary>
    /// Fix round 1: ticking "Don't ask again" leaves focus on the CheckBox, which would take Enter for itself (toggling
    /// the box off) before Keep running's IsDefault is consulted. Enter still keeps, and remembers.
    /// </summary>
    [AvaloniaFact]
    public void Enter_after_ticking_dont_ask_again_keeps_and_remembers()
    {
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Cancel));
        Dispatcher.UIThread.RunJobs();
        (Window dialog, Task<FirstCloseAnswer> result) = window.BuildFirstCloseDialog(2);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        CheckBox dontAsk = Assert.Single(dialog.GetLogicalDescendants().OfType<CheckBox>());
        dontAsk.IsChecked = true; // what a click does: it ticks the box and takes focus
        dontAsk.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.Same(dontAsk, dialog.FocusManager?.GetFocusedElement());

        PressKey(dialog, PhysicalKey.Enter);

        PumpUntil(() => result.IsCompleted, "the dialog closed", ms: 3_000);
        Assert.Equal(new FirstCloseAnswer(FirstCloseAction.Keep, Remember: true), result.Result);
    }

    /// <summary>
    /// The client's end of a connection whose writes can be held back: once <see cref="Delay"/> is set, each write reaches
    /// the daemon that long after it was made, in order. Whatever is still held when the stream is disposed is dropped,
    /// as a real link drops what was not yet sent when the client closes. Reads pass straight through.
    /// </summary>
    private sealed class DelayingStream : Stream
    {
        private readonly Stream _inner;
        private readonly System.Collections.Concurrent.BlockingCollection<(long Due, byte[] Bytes)> _held = new();
        private readonly CancellationTokenSource _closed = new();
        private long _delayMs;

        public DelayingStream(Stream inner)
        {
            _inner = inner;
            new Thread(Pump) { IsBackground = true, Name = "DelayingStream pump" }.Start();
        }

        public TimeSpan Delay { set => Volatile.Write(ref _delayMs, (long)value.TotalMilliseconds); }

        private void Pump()
        {
            try
            {
                foreach ((long due, byte[] bytes) in _held.GetConsumingEnumerable(_closed.Token))
                {
                    long wait = due - Environment.TickCount64;
                    if (wait > 0 && _closed.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(wait))) return; // closed first: dropped
                    _inner.Write(bytes);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // Closed: whatever is still held is dropped.
            }
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            try
            {
                _held.Add((Environment.TickCount64 + Volatile.Read(ref _delayMs), buffer.ToArray()));
            }
            catch (InvalidOperationException)
            {
                throw new ObjectDisposedException(nameof(DelayingStream));
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed.IsCancellationRequested)
            {
                _closed.Cancel();
                _held.CompleteAdding();
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private static void PressKey(Window dialog, PhysicalKey key)
    {
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        IInputElement? focused = dialog.FocusManager?.GetFocusedElement();
        Assert.True(focused is null || TopLevel.GetTopLevel(focused as Avalonia.Visual) == dialog,
            $"the dialog must own keyboard focus before the key press, but {focused?.GetType().Name} in another window has it");
        dialog.KeyPressQwerty(key, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }
}
