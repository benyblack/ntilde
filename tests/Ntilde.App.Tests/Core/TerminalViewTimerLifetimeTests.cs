using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Shell;
using Ntilde.VT;

namespace Ntilde.Tests.Core;

/// <summary>
/// #543: no <see cref="TerminalView"/> timer may outlive the view's window or its pane.
/// </summary>
/// <remarks>
/// A running <see cref="DispatcherTimer"/> is rooted by the dispatcher, and each of these holds its
/// view, which holds its pane, which holds the whole window. The view created its timers with the
/// constructor overload that also starts them, and stopped them only on leaving the visual tree
/// after it had started them itself - so a view that never got that far ticked a 16 ms render timer
/// forever, and every closed MainWindow stayed alive: the App.Tests host grew to 11 GB.
/// </remarks>
public class TerminalViewTimerLifetimeTests
{
    [AvaloniaFact]
    [Trait("Category", "Regression")]
    public void AViewThatIsNeverShown_RunsNoTimer()
    {
        // A tab that was never selected: its pane is built, its view never joins the tree, so
        // nothing would ever stop a timer it started.
        var view = new TerminalView();
        view.SetBuffer(new TerminalBuffer(80, 24));
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(view.RunningTimersForTest);
    }

    [AvaloniaFact]
    [Trait("Category", "Regression")]
    public void ClosingTheWindow_StopsEveryTimerOfItsView()
    {
        // Avalonia takes a closing window's controls out of the visual tree, and the view stops its timers there. Pinned
        // because nothing else stops the timers of a closed window's views.
        var view = new TerminalView();
        view.SetBuffer(new TerminalBuffer(80, 24));
        var window = new Window { Content = view, Width = 800, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.NotEmpty(view.RunningTimersForTest); // setup: a shown view renders

        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(view.RunningTimersForTest);
    }

    [AvaloniaFact]
    [Trait("Category", "Regression")]
    public void DisposingAShownPane_StopsEveryTimerOfItsView()
    {
        // Disposed where it stands: nothing takes the view out of the tree until the pane itself is
        // removed, and a disposed pane must not depend on that to stop.
        var pane = new TerminalPane();
        var window = new Window { Content = pane, Width = 800, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            TerminalView view = Assert.IsType<TerminalView>(pane.FindControl<TerminalView>("TermView"));
            Assert.NotEmpty(view.RunningTimersForTest); // setup: a shown view renders

            pane.Dispose();
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(view.RunningTimersForTest);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    [Trait("Category", "Regression")]
    public void RetiringAShownView_StopsEveryTimer_AndShowingItAgainStartsNone()
    {
        var view = new TerminalView();
        view.SetBuffer(new TerminalBuffer(80, 24));
        var window = new Window { Content = view, Width = 800, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            view.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.NotEmpty(view.RunningTimersForTest); // setup: a shown view renders

            view.Retire();
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(view.RunningTimersForTest);

            // Each of these starts a timer on a live view.
            view.IsVisible = false;
            Dispatcher.UIThread.RunJobs();
            view.IsVisible = true;
            Dispatcher.UIThread.RunJobs();
            window.Content = null;
            Dispatcher.UIThread.RunJobs();
            window.Content = view;
            Dispatcher.UIThread.RunJobs();
            view.Focus();
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(view.RunningTimersForTest);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    [Trait("Category", "Regression")]
    public void ARetiredView_StartsNoTimer_WhenScrolledOrResized()
    {
        var view = new TerminalView();
        view.SetBuffer(new TerminalBuffer(80, 24));
        var window = new Window { Content = view, Width = 800, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            view.Retire();
            Dispatcher.UIThread.RunJobs();

            // A wheel gesture arms the smooth-scroll animation, and a resize the throttle (held,
            // so it waits on the timer instead of going inline).
            view.ScrollByWheelLines(3);
            view.HoldResizeThrottleForTest();
            window.Width = 500;
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(view.RunningTimersForTest);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
