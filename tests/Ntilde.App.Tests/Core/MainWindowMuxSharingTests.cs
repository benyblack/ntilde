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
}
