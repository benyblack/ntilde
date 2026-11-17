using System;
using System.Globalization;

namespace Ntilde.VT;

public partial class AnsiParser
{
    private bool HandleColorOsc(string code, string data)
    {
        // Enough for all 256 numeric palette pairs; avoid amplifying the general string
        // accumulator's much larger image-oriented limit into split arrays for color input.
        if (data.Length > 32768 && code is "4" or "5" or "10" or "11" or "12" or "17" or "19" or "104" or "105") return true;
        switch (code)
        {
            case "30001" when data.Length == 0: _buffer.PushColors(); return true;
            case "30101" when data.Length == 0: _buffer.PopColors(); RefreshColorScheme(); return true;
            case "104": ResetPaletteColors(data); return true;
            case "105": ResetSpecialColor(data); return true;
            case "110":
            case "111":
            case "112":
            case "117":
            case "119":
                ResetDynamicColor(code, data);
                return true;
            case "4": HandlePaletteColors(data); return true;
            case "5": HandleSpecialColors(data); return true;
            case "21": HandleKittyColors(data); return true;
            case "10":
            case "11":
            case "12":
            case "17":
            case "19":
                HandleDynamicColors(int.Parse(code, CultureInfo.InvariantCulture), data);
                return true;
            default: return false;
        }
    }

    private static bool TryPaletteIndex(string value, out int index)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out index) && index is >= 0 and < 256;

    /// <summary>rxvt's special-color selector: a number in [0, 5) naming bold, underline, blink, reverse or italic.</summary>
    private static bool TrySpecialIndex(string value, out int index)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out index) && index is >= 0 and < TerminalColorOverrides.SpecialColorCount;

    private void ResetPaletteColors(string data)
    {
        TerminalColorOverrides colors = _buffer.CopyColorOverrides();
        if (data.Length == 0) Array.Clear(colors.Palette);
        else
        {
            foreach (string item in data.Split(';'))
            {
                if (TryPaletteIndex(item, out int index)) colors.Palette[index] = null;
            }
        }
        _buffer.ApplyColorOverrides(colors);
    }

    private void ResetDynamicColor(string code, string data)
    {
        if (data.Length != 0) return;
        TerminalColorOverrides colors = _buffer.CopyColorOverrides();
        SetDynamicOverride(colors, int.Parse(code, CultureInfo.InvariantCulture) - 100, null);
        _buffer.ApplyColorOverrides(colors);
        RefreshColorScheme();
    }

    // rxvt's OSC 5: chained "selector;value" pairs where value "?" reports that special color as a complete
    // OSC 5 frame and any other value sets it. The special colors are their own resource set: nothing here
    // touches the palette (OSC 4), the dynamic colors (OSC 10/11/12/17/19) or the kitty keys (OSC 21).
    private void HandleSpecialColors(string data)
    {
        TerminalColorOverrides colors = _buffer.CopyColorOverrides();
        string[] values = data.Split(';');
        int replies = 0;
        for (int i = 0; i + 1 < values.Length; i += 2)
        {
            if (!TrySpecialIndex(values[i], out int index)) continue;
            if (values[i + 1] == "?")
            {
                // One complete special-color set per command; later updates still apply.
                if (replies++ < 256)
                    OnResponse?.Invoke(FormatSpecialColorResponse(index, ResolveSpecialColor(index, colors)));
            }
            else if (TryParseColor(values[i + 1], out TermColor color)) colors.SpecialColors[index] = color.ToUint();
        }
        _buffer.ApplyColorOverrides(colors);
    }

    /// <summary>
    /// rxvt's OSC 105: reset special colors to their defaults. A bare <c>OSC 105</c> resets all five;
    /// listed selectors reset exactly those. The default of every special color is the default foreground:
    /// the App has no per-attribute color resources, and rxvt's per-attribute color modes (OSC 106) are
    /// not modeled, so a stored special color is queried and reset but never render-applied.
    /// </summary>
    private void ResetSpecialColor(string data)
    {
        TerminalColorOverrides colors = _buffer.CopyColorOverrides();
        if (data.Length == 0) Array.Clear(colors.SpecialColors);
        else
        {
            foreach (string item in data.Split(';'))
            {
                if (TrySpecialIndex(item, out int index)) colors.SpecialColors[index] = null;
            }
        }
        _buffer.ApplyColorOverrides(colors);
    }

    private TermColor ResolveSpecialColor(int index, TerminalColorOverrides colors)
        => colors.SpecialColors[index] is uint color ? TermColor.FromUint(color) : DefaultForeground ?? TermColor.FromRgb(0xc0, 0xc0, 0xc0);

    private static string FormatSpecialColorResponse(int selector, TermColor color)
    {
        int r = color.R * 0x101, g = color.G * 0x101, b = color.B * 0x101;
        return $"\x1b]5;{selector.ToString(CultureInfo.InvariantCulture)};rgb:{r:x4}/{g:x4}/{b:x4}\x1b\\";
    }

    private void HandlePaletteColors(string data)
    {
        TerminalColorOverrides colors = _buffer.CopyColorOverrides();
        string[] values = data.Split(';');
        int replies = 0;
        for (int i = 0; i + 1 < values.Length; i += 2)
        {
            if (!TryPaletteIndex(values[i], out int index)) continue;
            if (values[i + 1] == "?")
            {
                // One complete 256-color palette per command; later updates still apply.
                if (replies++ < 256)
                    OnResponse?.Invoke(FormatOscColorResponse("4;" + index.ToString(CultureInfo.InvariantCulture), _buffer.GetPaletteColor(index, colors)));
            }
            else if (TryParseColor(values[i + 1], out TermColor color)) colors.Palette[index] = color.ToUint();
        }
        _buffer.ApplyColorOverrides(colors);
    }

    private void HandleDynamicColors(int code, string data)
    {
        TerminalColorOverrides colors = _buffer.CopyColorOverrides();
        foreach (string value in data.Split(';'))
        {
            if (value == "?") ReportDynamicColor(code, colors);
            else if (TryParseColor(value, out TermColor color)) SetDynamicOverride(colors, code, color);
            code++;
        }
        _buffer.ApplyColorOverrides(colors);
        RefreshColorScheme();
    }

    private TermColor? ResolveDynamicColor(int code, TerminalColorOverrides colors) => code switch
    {
        10 => colors.Foreground is uint fg ? TermColor.FromUint(fg) : DefaultForeground ?? TermColor.FromRgb(0xc0, 0xc0, 0xc0),
        11 => colors.Background is uint bg ? TermColor.FromUint(bg) : DefaultBackground ?? TermColor.Black,
        _ => _buffer.GetDynamicColor(code, colors)
    };

    private void ReportDynamicColor(int code, TerminalColorOverrides colors)
    {
        if (ResolveDynamicColor(code, colors) is TermColor result)
            OnResponse?.Invoke(FormatOscColorResponse(code.ToString(CultureInfo.InvariantCulture), result));
    }

    private static void SetDynamicOverride(TerminalColorOverrides colors, int code, TermColor? color)
    {
        uint? value = color?.ToUint();
        switch (code)
        {
            case 10: colors.Foreground = value; break;
            case 11: colors.Background = value; break;
            case 12: colors.Cursor = value; break;
            case 17: colors.SelectionBackground = value; break;
            case 19: colors.SelectionForeground = value; break;
        }
    }

    // Deliberately numeric-only: #RGB/#RRGGBB/#RRRGGGBBB/#RRRRGGGGBBBB,
    // rgb: (1-4 hex digits per channel), and finite rgbi: intensities in [0,1].
    // No OS color database, names, alpha, or untrusted text is used in replies.
    private static bool TryParseColor(string value, out TermColor color)
    {
        color = default;
        if (value.Length > 64) return false;
        string[] channels;
        bool intensity = value.StartsWith("rgbi:", StringComparison.OrdinalIgnoreCase);
        if (intensity || value.StartsWith("rgb:", StringComparison.OrdinalIgnoreCase))
            channels = value[(intensity ? 5 : 4)..].Split('/');
        else if (value.StartsWith('#') && value.Length is 4 or 7 or 10 or 13)
        {
            int width = (value.Length - 1) / 3;
            channels = [value.Substring(1, width), value.Substring(1 + width, width), value.Substring(1 + 2 * width, width)];
        }
        else return false;
        if (channels.Length != 3) return false;
        Span<byte> rgb = stackalloc byte[3];
        for (int i = 0; i < 3; i++)
        {
            if (!TryParseColorChannel(channels[i], intensity, out rgb[i])) return false;
        }

        color = TermColor.FromRgb(rgb[0], rgb[1], rgb[2]);
        return true;
    }

    private static bool TryParseColorChannel(string value, bool intensity, out byte channel)
    {
        channel = 0;
        if (intensity)
        {
            if (!double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double component) ||
                !double.IsFinite(component) || component < 0 || component > 1) return false;
            channel = (byte)(component * 255);
        }
        else
        {
            if (value.Length is < 1 or > 4 || !uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint component)) return false;
            channel = (byte)(component * 255 / ((1u << (value.Length * 4)) - 1));
        }
        return true;
    }

}
