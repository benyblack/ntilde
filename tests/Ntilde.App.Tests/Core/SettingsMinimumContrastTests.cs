using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Ntilde.Settings;
using Ntilde.Settings.Pages;
using Ntilde.Shell;

namespace Ntilde.Tests.Core;

/// <summary>Appearance → Minimum contrast: the stored ratio in, the chosen one out, shown as WCAG writes it.</summary>
public sealed class SettingsMinimumContrastTests
{
    [AvaloniaFact]
    public void The_slider_shows_the_stored_ratio_and_saves_the_chosen_one()
    {
        using var root = SettingsTestRoot.WithSettings("""{"MinimumContrast":4.5}""");
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Appearance));
        var page = window.GetPage<AppearancePage>();

        Assert.Equal(4.5, page.FindControl<Slider>("MinimumContrastSlider")!.Value);
        Assert.Equal("4.5:1", page.FindControl<TextBlock>("MinimumContrastDisplay")!.Text);

        page.FindControl<Slider>("MinimumContrastSlider")!.Value = 1;
        var target = new TerminalSettings { MinimumContrast = 7 };
        page.Collect(target);

        Assert.Equal(1, target.MinimumContrast);
        Assert.Equal("Off", page.FindControl<TextBlock>("MinimumContrastDisplay")!.Text);
    }

    /// <summary>
    /// A pane renders from settings it copies field by field (BuildEffectiveSettings, for profile
    /// overrides). The first version of this setting was missing from that copy, so every pane kept
    /// the default and the setting did nothing.
    /// </summary>
    [AvaloniaFact]
    public void A_pane_hands_the_setting_to_its_terminal_view()
    {
        using var root = SettingsTestRoot.WithSettings("""{"ThemeName":"Default"}""");
        using var pane = new Ntilde.Controls.TerminalPane();

        pane.ApplySettings(new TerminalSettings { MinimumContrast = 4.5 });

        Assert.Equal(4.5, pane.FindControl<Ntilde.Shell.TerminalView>("TermView")!.MinimumContrast);
    }

    [Theory]
    [InlineData(1.0, "Off")]
    [InlineData(3.0, "3:1")]
    [InlineData(4.5, "4.5:1")]
    [InlineData(7.0, "7:1")]
    public void Ratios_read_as_WCAG_writes_them(double ratio, string expected)
        => Assert.Equal(expected, AppearancePage.FormatContrast(ratio));

    [Fact]
    public void Off_by_default()
        => Assert.Equal(1.0, new TerminalSettings().MinimumContrast);
}
