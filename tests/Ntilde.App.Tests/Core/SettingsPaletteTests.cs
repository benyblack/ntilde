using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Settings;
using Ntilde.Shell;
using Ntilde.VT;

namespace Ntilde.Tests.Core;

/// <summary>
/// The Nt* palette moves out of each window into a merged dictionary (Settings/NtPalette.axaml).
/// These tests pin what that move must not break: a theme switch still recolours the brush
/// instances the controls hold, and each surface still owns its own instances.
/// </summary>
public sealed class SettingsPaletteTests
{
    /// <summary>A light theme whose NtPanel works out to RGB(227,224,215): background shifted by -18.</summary>
    internal static TerminalTheme LightTheme() => new()
    {
        Name = "Palette test light",
        Background = TermColor.FromRgb(245, 242, 233),
        Foreground = TermColor.FromRgb(20, 20, 20),
        Blue = TermColor.FromRgb(0x03, 0x66, 0xd6),
    };

    internal static readonly Color LightPanel = Color.FromRgb(227, 224, 215);

    /// <summary>
    /// Apply used to read <c>resources[key]</c>, which sees only the dictionary's own entries. For a
    /// brush living in a merged dictionary it then added a second brush, leaving every control that
    /// held the merged one on the old colour, with no error anywhere.
    /// </summary>
    [Fact]
    public void Apply_RecolorsABrushThatLivesInAMergedDictionary()
    {
        var held = new SolidColorBrush(Colors.Magenta);
        var merged = new ResourceDictionary { ["NtPanel"] = held };
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(merged);

        ThemePaletteResources.Apply(resources, LightTheme());

        Assert.Equal(LightPanel, held.Color);
        Assert.False(resources.ContainsKey("NtPanel"), "Apply shadowed the merged brush with a new one.");
    }

    [AvaloniaFact]
    public void NtPalette_HoldsEveryKeyApplyWrites()
    {
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(new NtPalette());

        ThemePaletteResources.Apply(resources, LightTheme());

        // Any key Apply writes that the palette lacks would land here as a new own entry.
        Assert.Empty(resources);
    }

    [AvaloniaFact]
    public void NtPalette_EachInstanceOwnsItsBrushes()
    {
        var first = new NtPalette();
        var second = new NtPalette();

        foreach (object key in first.Keys)
        {
            if (first[key] is SolidColorBrush brush)
            {
                Assert.NotSame(brush, second[key]);
            }
        }
    }

    [AvaloniaFact]
    public void NtPalette_KeepsTheColoursBothSurfacesShippedWith()
    {
        var palette = new NtPalette();

        Assert.Equal(Color.Parse("#16181d"), ((SolidColorBrush)palette["NtWindowBg"]!).Color);
        Assert.Equal(Color.Parse("#353b46"), ((SolidColorBrush)palette["NtHairlineStrong"]!).Color);
        Assert.Equal(Color.Parse("#2461afef"), ((SolidColorBrush)palette["NtBlueDim"]!).Color);
        Assert.Equal(Color.Parse("#c678dd"), ((SolidColorBrush)palette["NtMagenta"]!).Color);
        Assert.IsType<FontFamily>(palette["NtMono"]);
    }

    [AvaloniaFact]
    public void SettingsWindow_ControlsHoldTheMergedPaletteBrushes_AndRecolourWithThem()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Nothing shadows the merged palette. (The window's own dictionary is not empty:
            // UiScale.PinScale writes NtUiScaleTransform there to hold the window's scale.)
            foreach (object key in new NtPalette().Keys)
            {
                Assert.False(window.Resources.ContainsKey(key), $"{key} is shadowed by an own entry.");
            }

            var panel = Assert.IsType<SolidColorBrush>(window.FindResource("NtPanel"));
            var cancel = window.FindControl<Button>("BtnCancel")!; // Pill: Background = NtPanel via NtStyles
            Assert.Same(panel, cancel.Background);

            var chrome = Assert.IsType<SolidColorBrush>(window.FindResource("NtChromeBg"));
            var footer = cancel.GetLogicalAncestors().OfType<Border>().First(); // {StaticResource NtChromeBg}
            Assert.Same(chrome, footer.Background);

            // What the window's ApplyTheme runs on a theme switch.
            ThemePaletteResources.Apply(window.Resources, LightTheme());

            Assert.Equal(LightPanel, panel.Color);
            Assert.Same(panel, cancel.Background);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SettingsWindowAndConnectionManager_DoNotShareBrushes()
    {
        using var root = SettingsTestRoot.Empty();
        var settings = new Ntilde.SettingsWindow();
        var manager = new ConnectionManager();

        Assert.NotSame(settings.FindResource("NtPanel"), manager.FindResource("NtPanel"));

        manager.ApplyTheme(LightTheme());

        Assert.Equal(LightPanel, ((SolidColorBrush)manager.FindResource("NtPanel")!).Color);
        Assert.NotEqual(LightPanel, ((SolidColorBrush)settings.FindResource("NtPanel")!).Color);
    }

    [AvaloniaFact]
    public void ConnectionManager_ControlsHoldItsPaletteBrushes_AndRecolourWithThem()
    {
        var manager = new ConnectionManager();

        Assert.Empty(manager.Resources); // the palette is merged, not copied in
        var windowBg = Assert.IsType<SolidColorBrush>(manager.FindResource("NtWindowBg"));
        var rootGrid = Assert.IsType<Grid>(manager.Content); // Background="{StaticResource NtWindowBg}"
        Assert.Same(windowBg, rootGrid.Background);

        manager.ApplyTheme(LightTheme());

        Assert.Equal(Color.FromRgb(245, 242, 233), windowBg.Color);
        Assert.Same(windowBg, rootGrid.Background);
    }
}
