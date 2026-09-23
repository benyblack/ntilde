using System.Linq;
using Avalonia.Themes.Fluent;
using static Ntilde.Shell.Theming.ThemeColorMath;

namespace Ntilde.Shell.Theming;

/// <summary>
/// Maps tokens onto Fluent's per-variant palette, so stock controls (ComboBox dropdowns, context
/// menus, tooltips, text boxes, scrollbars) take the theme's tones instead of stock grey. The
/// ChromeBlack* colors are left at Fluent's defaults: they are shadow and overlay blacks, the
/// same on every theme.
/// </summary>
internal static class FluentPaletteMapping
{
    // Tokens the palette reads. Apply skips the palette when none of them changed: the
    // window-opacity slider re-applies on every tick and moves only SidebarBg.
    private static readonly string[] SourceKeys =
    [
        ThemeKeys.WindowBg, ThemeKeys.ChromeBg, ThemeKeys.Panel, ThemeKeys.Overlay,
        ThemeKeys.Hairline, ThemeKeys.HairlineStrong,
        ThemeKeys.Fg, ThemeKeys.Fg2, ThemeKeys.Fg3, ThemeKeys.Fg4,
        ThemeKeys.HoverFill, ThemeKeys.PressedFill, ThemeKeys.Error, ThemeKeys.Accent,
    ];

    public static bool SameInputs(ThemeTokens a, ThemeTokens b)
        => a.IsDark == b.IsDark && SourceKeys.All(key => a[key] == b[key]);

    public static void Write(ColorPaletteResources palette, ThemeTokens t)
    {
        var bg = t[ThemeKeys.WindowBg];
        var fg = t[ThemeKeys.Fg];

        palette.RegionColor = bg;
        palette.AltHigh = bg;
        palette.AltMediumHigh = WithAlpha(bg, 0xCC);
        palette.AltMedium = WithAlpha(bg, 0x99);
        palette.AltMediumLow = WithAlpha(bg, 0x66);
        palette.AltLow = WithAlpha(bg, 0x33);
        palette.BaseHigh = fg;
        palette.BaseMediumHigh = WithAlpha(fg, 0xCC);
        palette.BaseMedium = WithAlpha(fg, 0x99);
        palette.BaseMediumLow = WithAlpha(fg, 0x66);
        palette.BaseLow = WithAlpha(fg, 0x33);
        palette.ChromeAltLow = Composite(t[ThemeKeys.Fg2], bg);
        palette.ChromeDisabledHigh = t[ThemeKeys.Hairline];
        palette.ChromeDisabledLow = Composite(t[ThemeKeys.Fg4], bg);
        palette.ChromeGray = Composite(t[ThemeKeys.Fg3], bg);
        palette.ChromeHigh = t[ThemeKeys.HairlineStrong];
        palette.ChromeLow = t[ThemeKeys.ChromeBg];
        palette.ChromeMedium = t[ThemeKeys.Panel];
        palette.ChromeMediumLow = t[ThemeKeys.Overlay];
        palette.ChromeWhite = fg;
        palette.ListLow = t[ThemeKeys.HoverFill];
        palette.ListMedium = t[ThemeKeys.PressedFill];
        palette.ErrorText = t[ThemeKeys.Error];

        // Accent LAST, and via default. In Avalonia 12.0.4 every property above stores its value
        // (ColorPaletteResources.SetColor) without raising ResourcesChanged. Only an Accent change
        // does. Resetting and re-setting it guarantees one notification carries everything above,
        // even when two themes share an accent.
        palette.Accent = default;
        palette.Accent = t[ThemeKeys.Accent];
    }
}
