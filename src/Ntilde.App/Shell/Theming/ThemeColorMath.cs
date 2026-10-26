using System;
using System.Collections.Generic;
using Avalonia.Media;

namespace Ntilde.Shell.Theming;

/// <summary>
/// The one home for UI color math: light/dark classification, WCAG contrast, alpha compositing
/// and HSL adjustments. Before theme v2 the same luminance formula lived in five places with
/// three different thresholds. Everything here is a pure function over <see cref="Color"/> - no
/// Application, no resources - so plain unit tests and the shipped-theme contrast test can call
/// it without a UI thread.
/// </summary>
public static class ThemeColorMath
{
    /// <summary>
    /// Perceived-luma classification: 0.299R + 0.587G + 0.114B below half is dark. This is the
    /// pre-v2 rule, kept so no shipped theme changes sides.
    /// </summary>
    public static bool IsDark(Color color)
        => (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0 < 0.5;

    /// <summary>WCAG 2.x relative luminance of the RGB channels (alpha ignored).</summary>
    public static double RelativeLuminance(Color color)
        => 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);

    /// <summary>
    /// WCAG contrast ratio (1..21). A translucent <paramref name="foreground"/> is composited over
    /// the (forced opaque) <paramref name="background"/> first, because that is what renders.
    /// </summary>
    public static double Contrast(Color foreground, Color background)
    {
        var surface = WithAlpha(background, 0xFF);
        double a = RelativeLuminance(Composite(foreground, surface));
        double b = RelativeLuminance(surface);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>Source-over composite of <paramref name="top"/> onto <paramref name="bottom"/>; the result is opaque.</summary>
    public static Color Composite(Color top, Color bottom)
    {
        double alpha = top.A / 255.0;
        return Color.FromArgb(0xFF, Mix(top.R, bottom.R, alpha), Mix(top.G, bottom.G, alpha), Mix(top.B, bottom.B, alpha));
    }

    public static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>Adds <paramref name="delta"/> to each RGB channel, clamped: the surface-elevation step.</summary>
    public static Color Shift(Color color, int delta)
        => Color.FromArgb(color.A, Clamp(color.R + delta), Clamp(color.G + delta), Clamp(color.B + delta));

    /// <summary>
    /// Pulls a color into a range that works as UI chrome: saturation capped at 0.68 and lightness
    /// held between 0.45 and 0.62. Colors already inside those caps pass through unchanged; extreme
    /// ones like the Default theme's RGB(0,0,238) land on a comfortable indigo-blue. Hue is never
    /// shifted, so the result keeps the theme's identity. (Moved from ThemePaletteResources.)
    /// </summary>
    public static Color SoftenAccent(Color color)
    {
        var (h, s, l) = ToHsl(color);
        return FromHsl(color.A, h, Math.Min(s, 0.68), Math.Clamp(l, 0.45, 0.62));
    }

    public static Color ShiftLightness(Color color, double delta)
    {
        var (h, s, l) = ToHsl(color);
        return FromHsl(color.A, h, s, Math.Clamp(l + delta, 0.0, 1.0));
    }

    /// <summary>
    /// Returns <paramref name="color"/> unchanged if it already reaches <paramref name="target"/>
    /// against every surface. Otherwise it moves HSL lightness in 0.01 steps away from the
    /// surfaces (lighter when the first surface is dark, darker when it is light) until the target
    /// holds or lightness runs out. Hue, saturation and alpha are preserved. Only derived colors go
    /// through this; an explicit theme value is never rewritten.
    /// </summary>
    public static Color EnsureContrast(Color color, IReadOnlyList<Color> surfaces, double target)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        if (surfaces.Count == 0 || MeetsTarget(color, surfaces, target))
        {
            return color;
        }

        double step = IsDark(surfaces[0]) ? 0.01 : -0.01;
        var (h, s, l) = ToHsl(color);
        var candidate = color;
        for (int i = 0; i < 100; i++)
        {
            l = Math.Clamp(l + step, 0.0, 1.0);
            candidate = FromHsl(color.A, h, s, l);
            if (MeetsTarget(candidate, surfaces, target) || l <= 0.0 || l >= 1.0)
            {
                break;
            }
        }

        return candidate;
    }

    /// <summary>
    /// A color whose hue sits midway between <paramref name="a"/> and <paramref name="b"/> on the
    /// shortest arc, with their mean saturation and lightness (e.g. the "agent wrote" orange
    /// between warning yellow and error red).
    /// </summary>
    public static Color MidpointHue(Color a, Color b)
    {
        var (ha, sa, la) = ToHsl(a);
        var (hb, sb, lb) = ToHsl(b);
        double arc = ((hb - ha + 540.0) % 360.0) - 180.0;
        double h = (ha + arc / 2.0 + 360.0) % 360.0;
        return FromHsl(0xFF, h, (sa + sb) / 2.0, (la + lb) / 2.0);
    }

    public static (double H, double S, double L) ToHsl(Color c)
    {
        // Achromatic and channel-dominance comparisons run on the source bytes: they are exact
        // integer checks, immune to the float-equality warning the scaled doubles would raise.
        byte maxByte = Math.Max(c.R, Math.Max(c.G, c.B));
        byte minByte = Math.Min(c.R, Math.Min(c.G, c.B));

        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = maxByte / 255.0, min = minByte / 255.0;
        double l = (max + min) / 2.0;

        if (maxByte == minByte)
        {
            return (0.0, 0.0, l);
        }

        double d = max - min;
        double s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
        double h;
        if (maxByte == c.R)
        {
            h = (g - b) / d + (g < b ? 6.0 : 0.0);
        }
        else if (maxByte == c.G)
        {
            h = (b - r) / d + 2.0;
        }
        else
        {
            h = (r - g) / d + 4.0;
        }

        return (h * 60.0, s, l);
    }

    public static Color FromHsl(byte alpha, double h, double s, double l)
    {
        double c = (1.0 - Math.Abs(2.0 * l - 1.0)) * s;
        double hPrime = h / 60.0;
        double x = c * (1.0 - Math.Abs(hPrime % 2.0 - 1.0));
        double r = 0, g = 0, b = 0;
        if (hPrime < 1) { r = c; g = x; }
        else if (hPrime < 2) { r = x; g = c; }
        else if (hPrime < 3) { g = c; b = x; }
        else if (hPrime < 4) { g = x; b = c; }
        else if (hPrime < 5) { r = x; b = c; }
        else { r = c; b = x; }

        double m = l - c / 2.0;
        return Color.FromArgb(
            alpha,
            (byte)Math.Round(Math.Clamp(r + m, 0.0, 1.0) * 255.0),
            (byte)Math.Round(Math.Clamp(g + m, 0.0, 1.0) * 255.0),
            (byte)Math.Round(Math.Clamp(b + m, 0.0, 1.0) * 255.0));
    }

    private static bool MeetsTarget(Color color, IReadOnlyList<Color> surfaces, double target)
    {
        foreach (var surface in surfaces)
        {
            if (Contrast(color, surface) < target)
            {
                return false;
            }
        }

        return true;
    }

    private static double Linear(byte channel)
    {
        double c = channel / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static byte Mix(byte top, byte bottom, double alpha)
        => (byte)Math.Round(top * alpha + bottom * (1.0 - alpha));

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
}
