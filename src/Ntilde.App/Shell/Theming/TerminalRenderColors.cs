using System;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Ntilde.VT;
using static Ntilde.Shell.Theming.ThemeColorMath;

namespace Ntilde.Shell.Theming;

/// <summary>
/// The colors the terminal renderer draws beyond the cells' own (spec §2 "Render colors"): the
/// selection, search matches and the glyph under a block cursor. They come from the pane's
/// <em>effective</em> theme, through the same derivation as the UI tokens, so a selection
/// matches the accent the chrome around it uses.
/// </summary>
/// <remarks>
/// <see cref="TerminalDrawOperation"/> asks for these once per frame on the render thread, so
/// <see cref="For"/> caches per theme instance and re-derives only when the theme's colors change.
/// Everything here is pure: no resources, no Application.
/// </remarks>
public readonly record struct TerminalRenderColors(
    Color Selection,
    Color? SelectionForeground,
    Color CursorText,
    Color SearchMatch,
    Color SearchMatchActive)
{
    private sealed record CacheEntry(int Fingerprint, TerminalRenderColors Colors);

    private static readonly ConditionalWeakTable<TerminalTheme, CacheEntry> Cache = new();

    /// <summary>Explicit theme values verbatim; otherwise the spec's derivations.</summary>
    public static TerminalRenderColors Derive(TerminalTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);

        // Window opacity only moves SidebarBg, which none of these read.
        var tokens = ThemeTokens.Derive(theme, 1.0);
        var warning = tokens[ThemeKeys.Warning];
        var error = tokens[ThemeKeys.Error];

        var background = WithAlpha(theme.Background.ToAvaloniaColor(), 0xFF);
        var selection = theme.SelectionBackground?.ToAvaloniaColor() ?? WithAlpha(tokens[ThemeKeys.Accent], 0x66);

        return new TerminalRenderColors(
            Selection: selection,
            SelectionForeground: theme.SelectionForeground?.ToAvaloniaColor() ?? DeriveSelectionForeground(theme, selection, background),
            CursorText: theme.CursorText?.ToAvaloniaColor() ?? background,
            SearchMatch: theme.SearchMatch?.ToAvaloniaColor() ?? WithAlpha(warning, 0x64),
            SearchMatchActive: theme.SearchMatchActive?.ToAvaloniaColor() ?? WithAlpha(MidpointHue(warning, error), 0x96));
    }

    /// <summary>
    /// Null - selected cells keep their own colors - while the theme's text reads on the selection.
    /// On a low-contrast theme (Solarized's text clears 4.5:1 on its own background by a hair, or
    /// not at all) any visible selection tint drops it under; rather than fade the selection until
    /// it disappears, the selected text gets the theme's foreground moved until it reads.
    /// </summary>
    private static Color? DeriveSelectionForeground(TerminalTheme theme, Color selection, Color background)
    {
        var surface = Composite(selection, background);
        var text = WithAlpha(theme.Foreground.ToAvaloniaColor(), 0xFF);
        return Contrast(text, surface) >= ThemeTokens.TextContrast
            ? null
            : EnsureContrast(text, [surface], ThemeTokens.TextContrast);
    }

    /// <summary>
    /// <see cref="Derive"/>, cached per theme instance. A theme edited in place is re-derived:
    /// the cache entry carries a fingerprint of every color the derivation reads.
    /// </summary>
    public static TerminalRenderColors For(TerminalTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        int fingerprint = Fingerprint(theme);
        if (Cache.TryGetValue(theme, out var entry) && entry.Fingerprint == fingerprint)
        {
            return entry.Colors;
        }

        var colors = Derive(theme);
        Cache.AddOrUpdate(theme, new CacheEntry(fingerprint, colors));
        return colors;
    }

    private static int Fingerprint(TerminalTheme theme)
    {
        var hash = new HashCode();
        hash.Add(theme.Foreground);
        hash.Add(theme.Background);
        for (int i = 0; i < 8; i++)
        {
            hash.Add(theme.GetAnsiColor(i, bright: false));
            hash.Add(theme.GetAnsiColor(i, bright: true));
        }

        hash.Add(theme.SelectionBackground);
        hash.Add(theme.SelectionForeground);
        hash.Add(theme.CursorText);
        hash.Add(theme.SearchMatch);
        hash.Add(theme.SearchMatchActive);
        hash.Add(theme.Accent);
        hash.Add(theme.Success);
        hash.Add(theme.Warning);
        hash.Add(theme.Error);
        hash.Add(theme.Info);
        return hash.ToHashCode();
    }
}
