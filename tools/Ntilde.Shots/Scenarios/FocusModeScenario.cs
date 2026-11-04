using Avalonia.Controls;
using Avalonia.Layout;
using Ntilde.Controls;
using Ntilde.Shell;

namespace Ntilde.Shots.Scenarios;

/// <summary>
/// Focus mode over a working split: vim on the left, the banner, a test run and a git graph on the
/// right, and
/// none of the app chrome - the tab strip and title-bar items give way to the slim strip that names
/// the tab.
/// </summary>
internal sealed class FocusModeScenario : IScenario
{
    public ShotSpec Spec { get; } = new(
        Name: "focus-mode",
        Tier: 2,
        LogicalWidth: 1600,
        LogicalHeight: 960,
        Intent: "Focus mode: no tab strip or title-bar buttons, only the slim strip at the top naming " +
                "the tab. Below it one tab split in two: vim editing the Rust source with syntax " +
                "colouring on the left; on the right the Ntilde banner, a passing test run and a git " +
                "graph, filling the pane.");

    /// <summary>Two panes side by side need ~90 columns each for the prompt plus a command.</summary>
    public Action<TerminalSettings>? Settings => settings => settings.FontSize = 14;

    public async Task RunAsync(ShotContext context)
    {
        TerminalPane left = context.OpenTab(context.World.DemoProfile);
        await context.RunCommandAsync(left, "clear");

        // Split before either pane has content: a pane shrunk after it is filled loses its
        // scrollback (see HeroSplitScenario.RunAsync).
        TerminalPane right = HeroSplitScenario.SplitAndGetNewPane(context, Orientation.Horizontal);

        // Focus mode goes on before the panes are filled too, since hiding the chrome grows them.
        // Entered after the commands, the right pane's cursor was drawn one row above its prompt.
        context.Window.EnterFocusMode();
        context.Driver.Pump(5);

        await context.RunCommandAsync(right, "bash scripts/ntilde-banner.sh");
        await context.RunCommandAsync(right, "bash scripts/demo-test.sh");
        // Fills the lower half of the pane, which the banner and test run leave empty.
        await context.RunCommandAsync(right, "git log --graph --oneline -10");

        // nowrap: at this pane height a long line lands at the bottom, and vim fills a wrapped line
        // that does not fit with an "@" row.
        await context.RunCommandAsync(left, TuiVimScenario.VimCommand.Replace("vim -c", "vim -c 'set nowrap' -c", StringComparison.Ordinal));
        context.WaitForAltScreen(left, active: true, TimeSpan.FromSeconds(20), "vim to switch to the alternate screen");

        if (!context.Window.IsFocusMode
            || context.Window.FindControl<Grid>("FocusStrip") is not { IsVisible: true })
        {
            throw new InvalidOperationException(
                "EnterFocusMode did not show the focus strip. MainWindow's focus-mode chrome changed - update the scenario.");
        }

        context.Capture();

        context.Window.LeaveFocusMode();
        left.Session!.SendInput(":q!\n");
        context.WaitForAltScreen(left, active: false, TimeSpan.FromSeconds(10), "vim to restore the primary screen after ':q!'");
    }
}
