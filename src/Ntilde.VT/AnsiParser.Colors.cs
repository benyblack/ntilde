using System;
using System.Globalization;

namespace Ntilde.VT;

public partial class AnsiParser
{
    private bool HandleColorOsc(string code, string data)
    {
        // Enough for all 256 numeric palette pairs; avoid amplifying the general string
        // accumulator's much larger image-oriented limit into split arrays for color input.
        if (data.Length > 32768 && code is "4" or "10" or "11" or "12" or "17" or "19" or "104") return true;
        switch (code)
        {
            case "30001" when data.Length == 0: _buffer.PushColors(); return true;
            case "30101" when data.Length == 0: _buffer.PopColors(); RefreshColorScheme(); return true;
            case "104":
                if (data.Length == 0) _buffer.ResetPalette();
                else foreach (string item in data.Split(';'))
                    if (TryPaletteIndex(item, out int index)) _buffer.SetPaletteColor(index, null);
                return true;
            case "110":
            case "111":
            case "112":
            case "117":
            case "119":
                if (data.Length == 0)
                {
                    _buffer.SetDynamicColor(int.Parse(code, CultureInfo.InvariantCulture) - 100, null);
                    RefreshColorScheme();
                }
                return true;
            case "4": HandlePaletteColors(data); return true;
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

    private void HandlePaletteColors(string data)
    {
        string[] values = data.Split(';');
        for (int i = 0; i + 1 < values.Length; i += 2)
        {
            if (!TryPaletteIndex(values[i], out int index)) continue;
            if (values[i + 1] == "?")
                OnResponse?.Invoke(FormatOscColorResponse("4;" + index.ToString(CultureInfo.InvariantCulture), _buffer.GetPaletteColor(index)));
            else if (TryParseColor(values[i + 1], out TermColor color)) _buffer.SetPaletteColor(index, color);
        }
    }

    private void HandleDynamicColors(int code, string data)
    {
        foreach (string value in data.Split(';'))
        {
            if (value == "?")
            {
                TermColor? color = code switch
                {
                    10 => _buffer.RuntimeForeground ?? DefaultForeground ?? TermColor.FromRgb(0xc0, 0xc0, 0xc0),
                    11 => _buffer.RuntimeBackground ?? DefaultBackground ?? TermColor.Black,
                    _ => _buffer.GetDynamicColor(code)
                };
                if (color is TermColor result)
                    OnResponse?.Invoke(FormatOscColorResponse(code.ToString(CultureInfo.InvariantCulture), result));
            }
            else if (TryParseColor(value, out TermColor color)) _buffer.SetDynamicColor(code, color);
            code++;
        }
        RefreshColorScheme();
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
