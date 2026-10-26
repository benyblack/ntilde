using System;
using System.Collections.Generic;
using Avalonia.Media;
using Ntilde.VT;
using static Ntilde.Shell.Theming.ThemeColorMath;

namespace Ntilde.Shell.Theming;

/// <summary>
/// The full set of UI color tokens for one theme: an immutable key → color map keyed by
/// <see cref="ThemeKeys"/>. <see cref="Derive"/> is the only producer, and is pure.
/// </summary>
public sealed class ThemeTokens
{
    /// <summary>Minimum contrast for UI text (WCAG AA, normal text).</summary>
    public const double TextContrast = 4.5;

    /// <summary>Minimum contrast for secondary/hint text.</summary>
    public const double SecondaryTextContrast = 3.0;

    /// <summary>Minimum contrast for non-text indicators (status dots, bars).</summary>
    public const double IndicatorContrast = 3.0;

    /// <summary>
    /// Minimum opaque-foreground-vs-ChromeBg contrast used to derive the hover/selected/pressed
    /// row fills (see <see cref="Derive"/>). Higher than <see cref="TextContrast"/>: at the fills'
    /// fixed low alphas (0x14/0x26/0x30), 4.5:1 leaves too little headroom for the composited
    /// tints to read as visually distinct from ChromeBg and from each other.
    /// </summary>
    private const double FillContrast = 6.5;

    // Text-on-accent candidates: white, or the dark navy the Settings XAML always used.
    private static readonly Color LightInk = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private static readonly Color DarkInk = Color.FromRgb(0x0A, 0x16, 0x22);

    private readonly Dictionary<string, Color> _colors;

    private ThemeTokens(Dictionary<string, Color> colors, bool isDark)
    {
        _colors = colors;
        IsDark = isDark;
    }

    /// <summary>Whether the theme background classifies as dark (drives the Fluent variant).</summary>
    public bool IsDark { get; }

    public IReadOnlyDictionary<string, Color> All => _colors;

    public Color this[string key] => _colors[key];

    /// <summary>
    /// Derives every token from the theme's existing 20 colors. Surfaces and text are
    /// contrast-fixed against each other. <paramref name="windowOpacity"/> only affects
    /// <see cref="ThemeKeys.SidebarBg"/>, the one surface that lets a background image show through.
    /// </summary>
    public static ThemeTokens Derive(TerminalTheme theme, double windowOpacity)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var c = new Dictionary<string, Color>(StringComparer.Ordinal);

        var bg = Opaque(theme.Background);
        var themeFg = Opaque(theme.Foreground);
        bool dark = IsDark(bg);

        // Surfaces elevate lighter on dark themes and darker on light ones (the pre-v2 rule). A
        // mid-tone background can make that direction push text under 4.5:1 on panels; flip then.
        int direction = dark ? 1 : -1;
        double usual = Contrast(themeFg, Shift(bg, 18 * direction));
        if (usual < TextContrast && Contrast(themeFg, Shift(bg, -18 * direction)) > usual)
        {
            direction = -direction;
        }

        c[ThemeKeys.WindowBg] = bg;
        c[ThemeKeys.ChromeBg] = Shift(bg, 10 * direction);
        c[ThemeKeys.Panel] = Shift(bg, 18 * direction);
        c[ThemeKeys.PanelAlt] = Shift(bg, 6 * direction);
        c[ThemeKeys.Overlay] = Shift(bg, 24 * direction);
        c[ThemeKeys.Scrim] = Color.FromArgb(dark ? (byte)0x80 : (byte)0x40, 0, 0, 0);
        // The inactive-pane veil's long-standing strengths (alpha 72 on dark, 96 on light).
        c[ThemeKeys.PaneDim] = Color.FromArgb(dark ? (byte)72 : (byte)96, 0, 0, 0);
        c[ThemeKeys.SidebarBg] = WithAlpha(c[ThemeKeys.ChromeBg], (byte)Math.Round(255.0 * Math.Clamp(windowOpacity, 0.0, 1.0)));
        c[ThemeKeys.Hairline] = Shift(bg, 28 * direction);
        c[ThemeKeys.HairlineStrong] = Shift(bg, 40 * direction);

        Color[] textSurfaces = [c[ThemeKeys.WindowBg], c[ThemeKeys.ChromeBg], c[ThemeKeys.Panel], c[ThemeKeys.PanelAlt], c[ThemeKeys.Overlay]];
        var fg = EnsureContrast(themeFg, textSurfaces, TextContrast);
        c[ThemeKeys.Fg] = fg;
        c[ThemeKeys.Fg2] = WithAlpha(fg, 0xC8);
        // Fg3 is translucent (0x9A), so meeting TextContrast at full opacity does not guarantee
        // its composited-over-Panel ratio clears the (lower) secondary bar; some near-white/
        // near-black surfaces (e.g. Solarized) land just under 3:1 otherwise. Fix the composited
        // result directly, same technique as TabPreviewFg below.
        c[ThemeKeys.Fg3] = EnsureContrast(WithAlpha(fg, 0x9A), textSurfaces, SecondaryTextContrast);
        c[ThemeKeys.Fg4] = WithAlpha(fg, 0x6E);

        // Hover/selected/pressed are the same foreground at low, fixed alphas, so a reader tells
        // them apart only by how far each composited tint sits from ChromeBg and from each other.
        // TextContrast (4.5) on the opaque Fg is not enough headroom for that at these alphas: on
        // Solarized, ChromeBg sits within a few units of white/near-black, so hover and selected
        // land almost on top of each other. Derive the three fills from a separately-boosted base
        // instead of Fg itself, so ordinary themes (already far above this bar) are untouched.
        var fillFg = EnsureContrast(fg, [c[ThemeKeys.ChromeBg]], FillContrast);
        c[ThemeKeys.HoverFill] = WithAlpha(fillFg, 0x14);
        c[ThemeKeys.SelectedFill] = WithAlpha(fillFg, 0x26);
        c[ThemeKeys.PressedFill] = WithAlpha(fillFg, 0x30);

        // Accent: soften the ANSI blue for chrome use, pick the better-contrasting ink, and if even
        // that ink misses 4.5:1, move the accent away from it.
        var accent = SoftenAccent(Opaque(theme.Blue));
        var accentFg = Contrast(LightInk, accent) >= Contrast(DarkInk, accent) ? LightInk : DarkInk;
        accent = EnsureContrast(accent, [accentFg], TextContrast);
        c[ThemeKeys.Accent] = accent;
        c[ThemeKeys.AccentFg] = accentFg;
        c[ThemeKeys.AccentHover] = ShiftLightness(accent, 0.08);
        c[ThemeKeys.AccentPressed] = ShiftLightness(accent, -0.06);
        c[ThemeKeys.AccentDim] = WithAlpha(accent, 0x24);
        c[ThemeKeys.AccentFaint] = WithAlpha(accent, 0x15);
        c[ThemeKeys.AccentBorder] = WithAlpha(accent, 0x4D);

        // Status: softened ANSI, readable as text on every panel-like surface.
        Color[] statusSurfaces = [c[ThemeKeys.Panel], c[ThemeKeys.WindowBg], c[ThemeKeys.Overlay]];
        Color Status(TermColor ansi) => EnsureContrast(SoftenAccent(Opaque(ansi)), statusSurfaces, TextContrast);
        var success = Status(theme.Green);
        var warning = Status(theme.Yellow);
        var error = Status(theme.Red);
        var info = Status(theme.Cyan);
        SetStatus(c, ThemeKeys.Success, ThemeKeys.SuccessDim, ThemeKeys.SuccessBorder, success);
        SetStatus(c, ThemeKeys.Warning, ThemeKeys.WarningDim, ThemeKeys.WarningBorder, warning);
        SetStatus(c, ThemeKeys.Error, ThemeKeys.ErrorDim, ThemeKeys.ErrorBorder, error);
        SetStatus(c, ThemeKeys.Info, ThemeKeys.InfoDim, ThemeKeys.InfoBorder, info);
        SetStatus(c, ThemeKeys.Attention, ThemeKeys.AttentionDim, ThemeKeys.AttentionBorder, warning);

        // Aliases. AgentWrote gets its own orange between warning and error: the sidebar dot must
        // tell "agent wrote" apart from "attention" (spec §4), so it is never merged into Warning.
        var agentWrote = EnsureContrast(MidpointHue(warning, error), statusSurfaces, TextContrast);
        c[ThemeKeys.AgentWrote] = agentWrote;
        c[ThemeKeys.AgentWatched] = info;
        c[ThemeKeys.AgentIdle] = c[ThemeKeys.Fg4];
        c[ThemeKeys.DiffAdded] = success;
        c[ThemeKeys.DiffRemoved] = error;
        c[ThemeKeys.DiffHunk] = warning;
        c[ThemeKeys.Recording] = error;
        c[ThemeKeys.Link] = accent;
        c[ThemeKeys.LinkHover] = c[ThemeKeys.AccentHover];

        // Tab strip. The horizontal strip sits on the title bar (ChromeBg); the vertical sidebar's
        // opaque tone is the same ChromeBg, so one surface list covers both orientations.
        Color[] strip = [c[ThemeKeys.ChromeBg]];
        c[ThemeKeys.TabFg] = c[ThemeKeys.Fg2];
        c[ThemeKeys.TabFgSelected] = fg;
        c[ThemeKeys.TabHoverFill] = c[ThemeKeys.HoverFill];
        c[ThemeKeys.TabSelectedFill] = c[ThemeKeys.SelectedFill];
        c[ThemeKeys.TabIndicator] = accent;
        c[ThemeKeys.TabPreviewFg] = EnsureContrast(Composite(c[ThemeKeys.Fg3], c[ThemeKeys.ChromeBg]), strip, TextContrast);
        c[ThemeKeys.TabWorking] = EnsureContrast(accent, strip, IndicatorContrast);
        c[ThemeKeys.TabAttention] = EnsureContrast(warning, strip, IndicatorContrast);
        c[ThemeKeys.TabAgentWrote] = EnsureContrast(agentWrote, strip, IndicatorContrast);
        c[ThemeKeys.TabAgentWatched] = EnsureContrast(info, strip, IndicatorContrast);
        c[ThemeKeys.TabChipActivity] = c[ThemeKeys.TabPreviewFg];
        c[ThemeKeys.TabChipAttention] = EnsureContrast(warning, strip, TextContrast);
        c[ThemeKeys.TabChipAgentWrote] = EnsureContrast(agentWrote, strip, TextContrast);
        c[ThemeKeys.TabChipAgentWatched] = EnsureContrast(info, strip, TextContrast);
        c[ThemeKeys.SidebarDivider] = c[ThemeKeys.Hairline];
        c[ThemeKeys.GripHover] = c[ThemeKeys.AccentBorder];

        return new ThemeTokens(c, dark);
    }

    private static Color Opaque(TermColor color) => WithAlpha(color.ToAvaloniaColor(), 0xFF);

    private static void SetStatus(Dictionary<string, Color> c, string key, string dimKey, string borderKey, Color color)
    {
        c[key] = color;
        c[dimKey] = WithAlpha(color, 0x24);
        c[borderKey] = WithAlpha(color, 0x4D);
    }
}
