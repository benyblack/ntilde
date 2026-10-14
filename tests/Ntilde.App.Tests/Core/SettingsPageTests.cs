using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Ntilde.Settings;
using Ntilde.Settings.Pages;
using Ntilde.Shell;

namespace Ntilde.Tests.Core;

/// <summary>
/// Each page on its own: Load shows the session's values and Collect writes them back. The
/// window-level round trip (SettingsWindowRoundTripTests) checks the same thing for all pages at
/// once; these tests point at the page that broke.
/// </summary>
public sealed class SettingsPageTests
{
    internal static readonly Func<IEnumerable<string>> NoSystemFonts = () => Array.Empty<string>();

    internal static SettingsSession SessionFor(TerminalSettings settings) => new(settings, NoSystemFonts);

    [AvaloniaFact]
    public void AgentAccessPage_LoadsAndCollectsItsFourSettings()
    {
        var page = new AgentAccessPage();
        page.Load(SessionFor(new TerminalSettings
        {
            AgentAccessObserveEnabled = true,
            AgentReplayExportEnabled = true,
            AgentAccessActEnabled = true,
            AgentIndicatorTabRollup = "All",
        }));
        var target = new TerminalSettings();

        page.Collect(target);

        Assert.True(target.AgentAccessObserveEnabled);
        Assert.True(target.AgentReplayExportEnabled);
        Assert.True(target.AgentAccessActEnabled);
        Assert.Equal("All", target.AgentIndicatorTabRollup);
        Assert.Null(page.Validate());
    }

    [AvaloniaFact]
    public void AgentAccessPage_UnknownRollup_FallsBackToTheFirstItem_AsBefore()
    {
        var page = new AgentAccessPage();
        page.Load(SessionFor(new TerminalSettings { AgentIndicatorTabRollup = "Sometimes" }));
        // Seed with a value that is neither the default nor the expected fallback, so a Load that selects nothing fails.
        var target = new TerminalSettings { AgentIndicatorTabRollup = "All" };

        page.Collect(target);

        Assert.Equal("WritesOnly", target.AgentIndicatorTabRollup);
    }

    [AvaloniaFact]
    public void SshPage_LoadsAndCollectsTheNativeBackendToggle()
    {
        var page = new SshPage();
        page.Load(SessionFor(new TerminalSettings { ExperimentalNativeSshEnabled = false }));
        var target = new TerminalSettings { ExperimentalNativeSshEnabled = true };

        page.Collect(target);

        Assert.False(target.ExperimentalNativeSshEnabled);
    }
}
