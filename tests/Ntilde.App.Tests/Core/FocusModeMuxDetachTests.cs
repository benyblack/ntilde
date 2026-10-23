using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
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
/// Focus mode with persistent shells, which "Pane: Detach" needs: its "Shell detached" answers the user at once (final
/// review I1). The window's panes spawn into an in-memory MuxServer, as in <see cref="MainWindowMuxLifecycleTests"/>.
/// </summary>
/// <remarks>
/// <see cref="TestAppDataRoot"/> is taken for its lifetime: closing a real MainWindow saves the session, which must not
/// land in the developer's own profile.
/// </remarks>
public sealed class FocusModeMuxDetachTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    private const BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    private readonly MuxTestHost _mux = new();
    private MuxConnectionHost? _host;

    /// <summary>Each test starts with no saved session, which would otherwise name the previous test's mux sessions.</summary>
    public FocusModeMuxDetachTests()
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
        _host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(_mux.Listener.Connect(), null, ct), "test", null)
        {
            DisposeFlushTimeout = TimeSpan.FromSeconds(1),
        };
        var factory = new MuxTerminalSessionFactory(
            new MuxConnectionHosts(_host, _ => null), new RecordingSessionFactory(new FakeTerminalSession()), null);
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

    private static TerminalPane? CurrentPane(MainWindow window) =>
        (TerminalPane?)typeof(MainWindow).GetProperty("_currentPane", NonPublicInstance)!.GetValue(window);

    private static (bool Visible, string? Title, string? Message) Toast(MainWindow window) =>
        (window.FindControl<Border>("RecordingToast")!.IsVisible,
         window.FindControl<TextBlock>("RecordingToastTitle")!.Text,
         window.FindControl<TextBlock>("RecordingToastMessage")!.Text);

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

    /// <summary>Splits the window's one pane; the new pane is attached and current.</summary>
    private static (TerminalPane First, TerminalPane Second) SplitTheOnlyPane(MainWindow window)
    {
        TerminalPane first = AllPanes(window).Single();
        typeof(MainWindow).GetProperty("_currentPane", NonPublicInstance)!.SetValue(window, first);
        typeof(MainWindow).GetMethod("SplitPane", NonPublicInstance)!.Invoke(window, [Avalonia.Layout.Orientation.Horizontal]);
        TerminalPane second = AllPanes(window).Single(p => !ReferenceEquals(p, first));
        PumpUntil(() => second.Session is MuxClientSession { IsAttached: true }, "the split pane attached");
        WindowTestSupport.Invoke(window, "UpdateActivePane", second);
        Assert.Same(second, CurrentPane(window));
        return (first, second);
    }

    [AvaloniaFact]
    public void Detaching_one_pane_of_a_split_in_focus_mode_says_so_at_once()
    {
        MainWindow window = CreateWindow();
        (_, TerminalPane second) = SplitTheOnlyPane(window);
        Guid id = ((MuxClientSession)second.Session!).Id;
        window.EnterFocusMode();

        WindowTestSupport.Invoke(window, "DetachActivePane");
        PumpUntil(() => !AllPanes(window).Contains(second), "the pane detached");

        // The tab survives, so focus mode stays, and the answer to the user's command is not held for later.
        Assert.True(window.IsFocusMode);
        PumpUntil(() => Toast(window) is { Visible: true, Title: "Shell detached" }, "the detach toast shows in focus mode");
        Assert.False(_mux.Mux(id).IsExited);
    }

    /// <summary>Final review I2: detaching a tab's only pane closes the tab, which moves the user to another shell.</summary>
    [AvaloniaFact]
    public void Detaching_the_only_pane_of_a_tab_leaves_focus_mode_first()
    {
        MainWindow window = CreateWindow();
        var tabs = window.FindControl<TabControl>("Tabs")!;
        var a = (TabItem)tabs.SelectedItem!;
        TerminalPane only = AllPanes(window).Single();
        Guid id = ((MuxClientSession)only.Session!).Id;

        // A second tab, so the window stays open when the first goes.
        window.KeyPress(Avalonia.Input.Key.T, Avalonia.Input.RawInputModifiers.Control | Avalonia.Input.RawInputModifiers.Shift, Avalonia.Input.PhysicalKey.T, null);
        PumpUntil(() => AllPanes(window).Count(p => p.Session is MuxClientSession { IsAttached: true }) == 2, "the second tab attached");
        var b = (TabItem)tabs.SelectedItem!;
        Assert.NotSame(a, b);
        tabs.SelectedItem = a;
        Dispatcher.UIThread.RunJobs();
        Assert.Same(only, CurrentPane(window));
        window.EnterFocusMode();

        WindowTestSupport.Invoke(window, "DetachActivePane");
        PumpUntil(() => !AllPanes(window).Contains(only), "the pane detached");

        Assert.False(window.IsFocusMode);
        Assert.Same(b, tabs.SelectedItem);
        Assert.False(_mux.Mux(id).IsExited);
    }
}
