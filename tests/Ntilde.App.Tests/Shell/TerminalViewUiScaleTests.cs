using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Shell;

namespace Ntilde.Tests.Shell;

/// <summary>
/// The terminal rasterizes its glyphs into an atlas at the monitor's render scale and draws the
/// sprites back at 1/scale, so they sit on the device-pixel grid. Under an interface scale the
/// layout transform then enlarges those bitmaps, and 150% text came out visibly blurry while the
/// tab title next to it (Avalonia's own vector text) stayed crisp. The terminal's effective render
/// scale must therefore include the interface scale, and follow it when it changes, so the atlas
/// is rebuilt at true device density. The headless top level reports RenderScaling 1.0, which is
/// what makes the numbers below exactly the interface scale.
/// </summary>
public sealed class TerminalViewUiScaleTests
{
    [AvaloniaFact]
    public void EffectiveRenderScaling_IncludesTheInterfaceScale_OnAttach()
    {
        UiScale.Apply(1.5);
        var view = new TerminalView();
        var window = new Window { Width = 400, Height = 300, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1.5, view.EffectiveRenderScaling, precision: 6);
        }
        finally
        {
            window.Close();
            UiScale.Apply(1.0);
        }
    }

    [AvaloniaFact]
    public void EffectiveRenderScaling_FollowsAnInterfaceScaleChange_WhileAttached()
    {
        UiScale.Apply(1.0);
        var view = new TerminalView();
        var window = new Window { Width = 400, Height = 300, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1.0, view.EffectiveRenderScaling, precision: 6);

            UiScale.Apply(2.0);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(2.0, view.EffectiveRenderScaling, precision: 6);
        }
        finally
        {
            window.Close();
            UiScale.Apply(1.0);
        }
    }

    /// <summary>
    /// A window can render at a scale other than the app's: Settings holds its opening scale, and
    /// FitWindow reduces a window the screen cannot fit (the Replay window has a terminal in it).
    /// The atlas must be built for the scale that window actually renders at - the transform
    /// resource in effect for this view - not for UiScale.Current (Codex on PR #466).
    /// </summary>
    [AvaloniaFact]
    public void EffectiveRenderScaling_UsesTheOwningWindowsScale_WhenItIsPinned()
    {
        UiScale.Apply(2.0);
        var view = new TerminalView();
        var window = new Window { Width = 400, Height = 300, Content = view };
        try
        {
            UiScale.PinScale(window, 1.25);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1.25, view.EffectiveRenderScaling, precision: 6);

            // Releasing the pin while attached is a resource change too, and must be followed.
            window.Resources.Remove(UiScale.TransformResourceKey);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(2.0, view.EffectiveRenderScaling, precision: 6);
        }
        finally
        {
            window.Close();
            UiScale.Apply(1.0);
        }
    }

    [AvaloniaFact]
    public void EffectiveRenderScaling_StopsFollowing_AfterDetach()
    {
        UiScale.Apply(1.0);
        var view = new TerminalView();
        var window = new Window { Width = 400, Height = 300, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.Content = null;
            Dispatcher.UIThread.RunJobs();

            UiScale.Apply(2.0);
            Dispatcher.UIThread.RunJobs();

            // A detached view must not keep a live subscription (that is a leak of every closed
            // pane), so it keeps whatever it last knew.
            Assert.Equal(1.0, view.EffectiveRenderScaling, precision: 6);
        }
        finally
        {
            window.Close();
            UiScale.Apply(1.0);
        }
    }
}
