using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace Ntilde.Tests.Core;

/// <summary>
/// #543: a MainWindow that has closed and been torn down can be collected.
/// </summary>
/// <remarks>
/// Every closed window used to stay reachable, about 55 MB each, through the dispatcher's running
/// <see cref="Ntilde.Shell.TerminalView"/> timers; the App.Tests host reached 11 GB that way. Any other
/// root that holds a closed window fails this test too, which is the point of asking the GC rather
/// than the timers.
/// </remarks>
public sealed class MainWindowLifetimeTests : IDisposable
{
    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

    [AvaloniaFact]
    [Trait("Category", "Regression")]
    public void AClosedAndTornDownWindow_IsCollected()
    {
        WeakReference window = OpenCloseAndTearDownAWindow();

        // The pane's shell is ended on the thread pool, and its exit is posted back to the dispatcher holding the pane, so the
        // window is let go only once that post has run: keep pumping and collecting until it goes, or the deadline passes.
        var waited = Stopwatch.StartNew();
        while (window.IsAlive && waited.Elapsed < TimeSpan.FromSeconds(10))
        {
            Dispatcher.UIThread.RunJobs();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            if (window.IsAlive) Thread.Sleep(50);
        }

        Assert.False(window.IsAlive, $"the closed window is still reachable after {waited.Elapsed.TotalSeconds:F0} s of collections");
    }

    // Not inlined, so no local of the test method's frame can keep the window alive.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference OpenCloseAndTearDownAWindow()
    {
        Ntilde.MainWindow window = TestMainWindowFactory.Create();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.True(closed, "setup: the window did not close");

        TestMainWindowFactory.DisposeCreatedWindows();
        Dispatcher.UIThread.RunJobs();
        return new WeakReference(window);
    }
}
