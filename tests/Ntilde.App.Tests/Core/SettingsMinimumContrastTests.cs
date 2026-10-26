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
    /// The file and the MCP settings tool accept up to 21:1, the slider stops at 7:1. A stored
    /// ratio above the slider is shown as itself and survives a save that leaves the slider alone;
    /// it used to be clamped to 7 and written back from any page's save.
    /// </summary>
    [AvaloniaFact]
    public void A_stored_ratio_above_the_slider_survives_a_save()
    {
        using var root = SettingsTestRoot.WithSettings("""{"MinimumContrast":12}""");
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Appearance));
        var page = window.GetPage<AppearancePage>();

        Assert.Equal("12:1", page.FindControl<TextBlock>("MinimumContrastDisplay")!.Text);
        var untouched = new TerminalSettings();
        page.Collect(untouched);
        Assert.Equal(12, untouched.MinimumContrast);

        // Moving the slider is a choice: it is saved as chosen.
        page.FindControl<Slider>("MinimumContrastSlider")!.Value = 3;
        var moved = new TerminalSettings();
        page.Collect(moved);
        Assert.Equal(3, moved.MinimumContrast);
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
