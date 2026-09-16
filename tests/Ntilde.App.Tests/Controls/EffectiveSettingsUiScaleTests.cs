using Avalonia.Headless.XUnit;
using Ntilde.Controls;
using Ntilde.Shell;

namespace Ntilde.Tests.Controls;

/// <summary>
/// AGENTS.md: a new TerminalSettings field must be carried through TerminalPane's effective
/// settings whitelist, or the pane-side copy silently disagrees with the application setting.
/// UiScale missed that whitelist on PR #466 (Codex P1); this pins it, and any future field that
/// forgets the same edit fails here instead of in a reviewer's head.
/// </summary>
public sealed class EffectiveSettingsUiScaleTests
{
    [AvaloniaFact]
    public void BuildEffectiveSettings_CarriesTheInterfaceScale()
    {
        using var pane = new TerminalPane();

        TerminalSettings effective = pane.BuildEffectiveSettings(new TerminalSettings { UiScale = 1.5 });

        Assert.Equal(1.5, effective.UiScale);
    }
}
