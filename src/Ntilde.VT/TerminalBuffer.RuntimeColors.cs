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

    internal TermColor GetPaletteColor(int index)
    {
        uint? overridden = _runtimeColors.Current.Palette[index];
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

    internal TermColor? GetDynamicColor(int code) => code switch
    {
        10 => RuntimeForeground ?? Theme.Foreground,
        11 => RuntimeBackground ?? Theme.Background,
        12 => Color(_runtimeColors.Current.Cursor) ?? Theme.CursorColor,
        17 => Color(_runtimeColors.Current.SelectionBackground) ?? Theme.SelectionBackground,
        19 => Color(_runtimeColors.Current.SelectionForeground) ?? Theme.SelectionForeground,
        _ => null
    };

    internal void SetDynamicColor(int code, TermColor? color)
    {
        TerminalRuntimeColors next = _runtimeColors.Clone();
        uint? value = color?.ToUint();
        switch (code)
        {
            case 10: next.Current.Foreground = value; break;
            case 11: next.Current.Background = value; break;
            case 12: next.Current.Cursor = value; break;
            case 17: next.Current.SelectionBackground = value; break;
            case 19: next.Current.SelectionForeground = value; break;
            default: return;
        }
        PublishRuntimeColors(next);
    }

    internal void SetPaletteColor(int index, TermColor? color)
    {
        TerminalRuntimeColors next = _runtimeColors.Clone();
        next.Current.Palette[index] = color?.ToUint();
        PublishRuntimeColors(next);
    }

    internal void ResetPalette()
    {
        TerminalRuntimeColors next = _runtimeColors.Clone();
        Array.Clear(next.Current.Palette);
        PublishRuntimeColors(next);
    }

    internal void PushColors()
    {
        TerminalRuntimeColors next = _runtimeColors.Clone();
        next.Stack = next.Stack.TakeLast(TerminalRuntimeColors.MaxStackDepth - 1)
            .Append(next.Current.Clone()).ToArray();
        // Saving changes neither rendering nor the effective background preference.
        Volatile.Write(ref _runtimeColors, next);
    }

    internal void PopColors()
    {
        if (_runtimeColors.Stack.Length == 0) return;
        TerminalRuntimeColors next = _runtimeColors.Clone();
        next.Current = next.Stack[^1];
        next.Stack = next.Stack[..^1];
        PublishRuntimeColors(next);
    }

    private void PublishRuntimeColors(TerminalRuntimeColors next)
    {
        Volatile.Write(ref _runtimeColors, next);
        _runtimeTheme = null;
        _renderThemeEpoch++;
        foreach (var row in _viewport) row.TouchRevision();
        foreach (var row in _mainScreen) row.TouchRevision();
        foreach (var row in _altScreen) row.TouchRevision();
        Invalidate();
    }

    private TerminalTheme EffectiveRenderTheme()
    {
        // Cache is derived only from the host theme and the published overrides. Host theme
        // assignment and every color mutation clear it. Saved/user theme objects stay untouched.
        if (_runtimeTheme is not null) return _runtimeTheme;
        var theme = Theme.Clone();
        var colors = _runtimeColors.Current;
        theme.Foreground = Color(colors.Foreground) ?? theme.Foreground;
        theme.Background = Color(colors.Background) ?? theme.Background;
        theme.CursorColor = Color(colors.Cursor) ?? theme.CursorColor;
        theme.SelectionBackground = Color(colors.SelectionBackground) ?? theme.SelectionBackground;
        theme.SelectionForeground = Color(colors.SelectionForeground) ?? theme.SelectionForeground;
        for (int i = 0; i < 16; i++)
            if (colors.Palette[i] is uint value) theme.SetAnsiColor(i % 8, i >= 8, TermColor.FromUint(value));
        return _runtimeTheme = theme;
    }
}
