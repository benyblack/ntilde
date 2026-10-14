using Avalonia.Controls;
using Avalonia.Media;
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
}
