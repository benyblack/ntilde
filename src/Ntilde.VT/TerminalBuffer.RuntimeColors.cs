using System;
using System.Linq;
using System.Threading;

namespace Ntilde.VT;

public partial class TerminalBuffer
{
    // Published values are never mutated. UI-side preference queries may read them while
    // the parse thread publishes a replacement under the buffer's batch write lock.
    private TerminalRuntimeColors _runtimeColors = new();
    private TerminalTheme? _runtimeTheme;
    private TerminalTheme? _runtimeHostTheme;

    internal TermColor? RuntimeForeground => Color(Volatile.Read(ref _runtimeColors).Current.Foreground);
    internal TermColor? RuntimeBackground => Color(Volatile.Read(ref _runtimeColors).Current.Background);
    private static TermColor? Color(uint? color) => color is uint value ? TermColor.FromUint(value) : null;

    internal TerminalRuntimeColors? ExportRuntimeColors()
    {
        var state = _runtimeColors;
        var current = state.Current;
        bool empty = state.Stack.Length == 0 && current.Foreground is null && current.Background is null &&
            current.Cursor is null && current.SelectionBackground is null && current.SelectionForeground is null &&
            current.Palette.All(color => color is null);
        return empty ? null : state.Clone();
    }

    internal void ImportRuntimeColors(TerminalRuntimeColors? state)
        => PublishRuntimeColors(state?.Clone() ?? new());

    internal TermColor GetPaletteColor(int index, TerminalColorOverrides? overrides = null)
    {
        uint? overridden = (overrides ?? _runtimeColors.Current).Palette[index];
        return overridden is uint color ? TermColor.FromUint(color) : GetHostPaletteColor(index);
    }

    private TermColor GetHostPaletteColor(int index)
    {
        if (index < 16) return Theme.GetAnsiColor(index % 8, index >= 8);
        if (index < 232)
        {
            index -= 16;
            static byte Cube(int value) => (byte)(value == 0 ? 0 : value * 40 + 55);
            return TermColor.FromRgb(Cube(index / 36), Cube(index / 6 % 6), Cube(index % 6));
        }
        byte gray = (byte)((index - 232) * 10 + 8);
        return TermColor.FromRgb(gray, gray, gray);
    }

    internal TerminalColorOverrides CopyColorOverrides() => _runtimeColors.Current.Clone();

    internal void ApplyColorOverrides(TerminalColorOverrides colors)
    {
        if (_runtimeColors.Current.HasSameColors(colors)) return;
        PublishRuntimeColors(new TerminalRuntimeColors { Current = colors, Stack = _runtimeColors.Stack });
    }

    internal TermColor? GetDynamicColor(int code, TerminalColorOverrides overrides) => code switch
    {
        10 => Color(overrides.Foreground) ?? Theme.Foreground,
        11 => Color(overrides.Background) ?? Theme.Background,
        12 => Color(overrides.Cursor) ?? Theme.CursorColor,
        17 => Color(overrides.SelectionBackground) ?? Theme.SelectionBackground,
        19 => Color(overrides.SelectionForeground) ?? Theme.SelectionForeground,
        _ => null
    };

    internal void PushColors()
    {
        var state = _runtimeColors;
        // Internal published frames are immutable. Only exports/imports need deep copies;
        // later mutations always clone Current before changing it.
        var next = new TerminalRuntimeColors
        {
            Current = state.Current,
            Stack = state.Stack.TakeLast(TerminalRuntimeColors.MaxStackDepth - 1).Append(state.Current).ToArray()
        };
        Volatile.Write(ref _runtimeColors, next);
    }

    internal void PopColors()
    {
        var state = _runtimeColors;
        if (state.Stack.Length == 0) return;
        PublishRuntimeColors(new TerminalRuntimeColors { Current = state.Stack[^1], Stack = state.Stack[..^1] });
    }

    private void PublishRuntimeColors(TerminalRuntimeColors next)
    {
        bool changed = !_runtimeColors.Current.HasSameColors(next.Current);
        Volatile.Write(ref _runtimeColors, next);
        if (!changed) return;
        _runtimeTheme = null;
        MarkRenderThemeChangedNoLock();
        Invalidate();
    }

    /// <summary>
    /// Every rendered row's colors changed without its cells changing: a new theme epoch for the
    /// paged-history caches and a touched revision on every live row, so the span diff and the
    /// row-picture cache repaint the whole frame once. Caller holds the write lock.
    /// </summary>
    private void MarkRenderThemeChangedNoLock()
    {
        _renderThemeEpoch++;
        foreach (var row in _viewport) row.TouchRevision();
        foreach (var row in _mainScreen) row.TouchRevision();
        foreach (var row in _altScreen) row.TouchRevision();
    }

    /// <summary>
    /// DECSCNM (<c>CSI ? 5 h/l</c>). A change repaints the whole frame through the same
    /// invalidation a theme change uses; setting the current value is a no-op.
    /// </summary>
    public void SetReverseVideo(bool enabled)
    {
        bool lockTaken = EnterWriteLockIfNeeded();
        try
        {
            if (Modes.IsReverseVideo == enabled) return;
            Modes.IsReverseVideo = enabled;
            MarkRenderThemeChangedNoLock();
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        Invalidate();
    }

    /// <summary>
    /// The default foreground as the screen presents it: the theme's background while DECSCNM is
    /// set. For host overlays drawn in "the text color" over the terminal (link hover, bell flash).
    /// </summary>
    public TermColor PresentedDefaultForeground => Modes.IsReverseVideo ? Theme.Background : Theme.Foreground;

    /// <summary>One reversed presentation of one effective theme, published as a unit.</summary>
    private sealed record ReverseVideoThemeCache(TerminalTheme Source, TerminalTheme SourceColors, TerminalTheme Presented);

    private ReverseVideoThemeCache? _reverseVideoTheme;

    /// <summary>
    /// The theme a render snapshot presents: <see cref="EffectiveRenderTheme"/>, reversed while
    /// DECSCNM is set. Cached per effective theme and re-derived only when its colors change, so a
    /// reversed screen costs no per-frame allocation. Runs under the read lock, beside other
    /// captures; the cache is replaced as one reference.
    /// </summary>
    private TerminalTheme PresentedRenderTheme()
    {
        var theme = EffectiveRenderTheme();
        if (!Modes.IsReverseVideo) return theme;
        var cached = Volatile.Read(ref _reverseVideoTheme);
        if (cached is not null && ReferenceEquals(cached.Source, theme) && cached.SourceColors.HasSameColors(theme))
            return cached.Presented;
        var presented = ReverseVideo(theme);
        Volatile.Write(ref _reverseVideoTheme, new ReverseVideoThemeCache(theme, theme.Clone(), presented));
        return presented;
    }

    /// <summary>
    /// xterm's reverse video: the default foreground and background trade places, and a cursor
    /// color that was one of them follows the swap so the cursor keeps its contrast. The ANSI and
    /// 256-color palette, explicit selection/search colors and accents are unchanged; derived
    /// selection, search and cursor-text colors are derived again from the swapped pair. Cells in
    /// SGR 7 swap their resolved colors as usual, so default-colored inverse cells appear normal.
    /// </summary>
    private static TerminalTheme ReverseVideo(TerminalTheme theme)
    {
        TermColor fg = theme.Foreground, bg = theme.Background;
        TermColor Follow(TermColor color) => color == fg ? bg : color == bg ? fg : color;
        var reversed = theme.Clone();
        reversed.Foreground = bg;
        reversed.Background = fg;
        reversed.CursorColor = Follow(theme.CursorColor);
        if (theme.CursorText is { } cursorText) reversed.CursorText = Follow(cursorText);
        return reversed;
    }

    private TerminalTheme EffectiveRenderTheme()
    {
        // Preserve the original host-theme path when no override affects derived UI colors.
        // Themes are mutable, so an effective-theme cache must detect in-place host edits too.
        // Saved/user theme objects stay untouched.
        var colors = _runtimeColors.Current;
        if (!HasThemeOverrides(colors)) return Theme;
        if (_runtimeTheme is not null && _runtimeHostTheme?.HasSameColors(Theme) == true) return _runtimeTheme;
        var theme = Theme.Clone();
        _runtimeHostTheme = Theme.Clone();
        theme.Foreground = Color(colors.Foreground) ?? theme.Foreground;
        theme.Background = Color(colors.Background) ?? theme.Background;
        theme.CursorColor = Color(colors.Cursor) ?? theme.CursorColor;
        theme.SelectionBackground = Color(colors.SelectionBackground) ?? theme.SelectionBackground;
        theme.SelectionForeground = Color(colors.SelectionForeground) ?? theme.SelectionForeground;
        for (int i = 0; i < 16; i++)
            if (colors.Palette[i] is uint value) theme.SetAnsiColor(i % 8, i >= 8, TermColor.FromUint(value));
        return _runtimeTheme = theme;
    }

    private static bool HasThemeOverrides(TerminalColorOverrides colors)
    {
        if (colors.Foreground.HasValue || colors.Background.HasValue || colors.Cursor.HasValue ||
            colors.SelectionForeground.HasValue || colors.SelectionBackground.HasValue) return true;
        for (int i = 0; i < 16; i++)
            if (colors.Palette[i].HasValue) return true;
        return false;
    }

}
