using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Pty;
using Ntilde.Shell;

namespace Ntilde.Tests.Core;

/// <summary>
/// #543: a MainWindow that has closed and been torn down can be collected.
/// </summary>
/// <remarks>
/// Every closed window used to stay reachable, about 55 MB each, through the dispatcher's running
/// <see cref="Ntilde.Shell.TerminalView"/> timers; the App.Tests host reached 11 GB that way. Any other
/// root that holds a closed window fails this test too, which is the point of asking the GC rather
/// than the timers.
/// <para>
/// <see cref="TestAppDataRoot"/> is taken for its lifetime: closing a real MainWindow saves the session, which must not
/// land in the developer's profile, and a window built against that profile restores its tabs too.
/// </para>
/// </remarks>
public sealed class MainWindowLifetimeTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    public MainWindowLifetimeTests(TestAppDataRoot appData)
    {
        _ = appData;
        if (File.Exists(AppPaths.SessionFilePath)) File.Delete(AppPaths.SessionFilePath);
    }

    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

    [AvaloniaFact]
    [Trait("Category", "Regression")]
    public async Task AClosedAndTornDownWindow_IsCollected()
    {
        TornDownWindow torn = OpenCloseAndTearDownAWindow();
        WeakReference window = torn.Window;

        // The pane's shell is ended on the thread pool, and its exit is posted back to the dispatcher holding the pane, so the
        // window is let go only once that post has run: keep pumping and collecting until it goes, or the deadline passes.
        var waited = Stopwatch.StartNew();
        while (window.IsAlive && waited.Elapsed < TimeSpan.FromSeconds(10))
        {
            Dispatcher.UIThread.RunJobs();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            if (window.IsAlive) await Task.Delay(50);
        }

        // What else is still reachable says which root holds the window: its panes, the sessions they ran, or a session
        // a pane started after it was disposed.
        Assert.False(
            window.IsAlive,
            $"the closed window is still reachable after {waited.Elapsed.TotalSeconds:F0} s of collections; " +
            $"panes still reachable: {torn.Panes.Count(p => p.IsAlive)} of {torn.Panes.Count}; " +
            $"their sessions still reachable: {string.Join(", ", torn.Sessions.Where(s => s.IsAlive).Select(s => Describe(s.Target)))} " +
            $"({torn.Sessions.Count(s => s.IsAlive)} of {torn.Sessions.Count}); " +
            $"panes holding a session started after teardown: {torn.Panes.Count(p => p.Target is Ntilde.Controls.TerminalPane { Session: not null })}");
    }

    // A session's own threads that are still running. A live one roots the session, and through the pane's handlers on its
    // events, the pane and the window - whereas a session reachable only from the window is held, not holding.
    private static string Describe(object? session)
    {
        if (session is null) return "(collected)";
        string[] threadFields = ["_readLoopThread", "_processLoopThread", "_exitWatchThread", "_writeLoopThread"];
        var running = threadFields.Where(field =>
            session.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(session) is Thread { IsAlive: true });
        return $"{session.GetType().Name} [threads running: {string.Join("/", running.DefaultIfEmpty("none"))}]";
    }

    /// <summary>A session file with <paramref name="count"/> tabs of the platform's plain shell, the first selected.</summary>
    private static void SaveTabs(int count)
    {
        string shell = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        var session = new NtildeSession { ActiveTabIndex = 0 };
        for (int i = 0; i < count; i++)
        {
            string paneId = Guid.NewGuid().ToString();
            session.Tabs.Add(new TabSession
            {
                TabId = Guid.NewGuid().ToString(),
                Title = $"tab {i}",
                Root = new PaneNode { Type = NodeType.Leaf, PaneId = paneId, Command = shell, Arguments = string.Empty },
                ActivePaneId = paneId,
            });
        }

        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.SessionFilePath)!);
        File.WriteAllText(AppPaths.SessionFilePath, JsonSerializer.Serialize(session, SessionSerializationContext.Default.NtildeSession));
    }

    private sealed record TornDownWindow(WeakReference Window, IReadOnlyList<WeakReference> Panes, IReadOnlyList<WeakReference> Sessions);

    // Not inlined, so no local of the test method's frame can keep the window alive.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TornDownWindow OpenCloseAndTearDownAWindow()
    {
        // Restored tabs, as at startup: the selected one is shown, and the others are built in the background and never
        // shown. A view that was never shown is the one whose timers outlived the window, so those tabs are the point.
        SaveTabs(3);
        Ntilde.MainWindow window = TestMainWindowFactory.Create();
        window.Show();
        for (int i = 0; i < 50 && window.AllPanesForTest().Count < 3; i++) Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, window.AllPanesForTest().Count); // setup: every restored tab was built
        Assert.Equal(2, window.AllPanesForTest().Count(pane => TopLevel.GetTopLevel(pane) is null)); // setup: two were never shown
        var panes = window.AllPanesForTest().Select(pane => new WeakReference(pane)).ToList();
        var sessions = window.AllPanesForTest().Select(pane => pane.Session).OfType<object>().Select(session => new WeakReference(session)).ToList();

        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.True(closed, "setup: the window did not close");

        TestMainWindowFactory.DisposeCreatedWindows();
        Dispatcher.UIThread.RunJobs();
        return new TornDownWindow(new WeakReference(window), panes, sessions);
    }
}
