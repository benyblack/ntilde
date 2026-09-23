using Avalonia.Media;
using Ntilde.Shell.Theming;
using Xunit;

namespace Ntilde.Tests.Theming;

/// <summary>
/// Pure math, so plain [Fact]s: nothing here touches Application.Current (see the
/// headless-poisoning note in ThemeService).
/// </summary>
public class ThemeColorMathTests
{
    [Theory]
    [InlineData(0, 0, 0, true)]
    [InlineData(255, 255, 255, false)]
    [InlineData(127, 127, 127, true)]
    [InlineData(128, 128, 128, false)]
    public void IsDark_uses_the_perceived_luma_half_line(byte r, byte g, byte b, bool dark)
        => Assert.Equal(dark, ThemeColorMath.IsDark(Color.FromRgb(r, g, b)));

    [Fact]
    public void Contrast_matches_the_wcag_extremes()
    {
        Assert.Equal(21.0, ThemeColorMath.Contrast(Colors.White, Colors.Black), 2);
        Assert.Equal(1.0, ThemeColorMath.Contrast(Colors.Black, Colors.Black), 3);
    }

    [Fact]
    public void Contrast_composites_a_translucent_foreground_first()
    {
        // Fully transparent white over black renders as black: no contrast at all.
        Assert.Equal(1.0, ThemeColorMath.Contrast(Color.FromArgb(0, 255, 255, 255), Colors.Black), 3);
    }

    [Fact]
    public void Composite_blends_source_over()
    {
        var blended = ThemeColorMath.Composite(Color.FromArgb(0x80, 255, 255, 255), Colors.Black);
        Assert.Equal(Color.FromRgb(128, 128, 128), blended);
    }

    [Fact]
    public void SoftenAccent_PureBlue_LandsOnACalmerAccentWithHuePreserved()
    {
        // RGB(0,0,238): S clamps 1.0 -> 0.68, L (0.467) is inside the band, hue 240° preserved.
        var softened = ThemeColorMath.SoftenAccent(Color.FromRgb(0, 0, 238));
        Assert.Equal(Color.FromRgb(38, 38, 200), softened);
    }

    [Fact]
    public void SoftenAccent_AlreadyCalmAccent_IsUnchanged()
    {
        var calm = Color.FromRgb(0x62, 0x72, 0xa4);
        Assert.Equal(calm, ThemeColorMath.SoftenAccent(calm));
    }

    [Fact]
    public void EnsureContrast_returns_a_readable_color_unchanged()
    {
        var white = Colors.White;
        Assert.Equal(white, ThemeColorMath.EnsureContrast(white, [Colors.Black], 4.5));
    }

    [Fact]
    public void EnsureContrast_lightens_on_dark_surfaces_keeping_hue_and_saturation()
    {
        var dim = Color.FromRgb(0x33, 0x33, 0x33);
        var fixedColor = ThemeColorMath.EnsureContrast(dim, [Colors.Black], 4.5);

        Assert.True(ThemeColorMath.Contrast(fixedColor, Colors.Black) >= 4.5);
        Assert.True(ThemeColorMath.ToHsl(fixedColor).L > ThemeColorMath.ToHsl(dim).L);
        Assert.Equal(0.0, ThemeColorMath.ToHsl(fixedColor).S, 3);
    }

    [Fact]
    public void EnsureContrast_darkens_on_light_surfaces()
    {
        var pale = Color.FromRgb(0xDD, 0xDD, 0xDD);
        var fixedColor = ThemeColorMath.EnsureContrast(pale, [Colors.White], 4.5);

        Assert.True(ThemeColorMath.Contrast(fixedColor, Colors.White) >= 4.5);
        Assert.True(ThemeColorMath.ToHsl(fixedColor).L < ThemeColorMath.ToHsl(pale).L);
    }

    [Fact]
    public void MidpointHue_takes_the_shortest_arc()
    {
        var orange = ThemeColorMath.MidpointHue(Color.FromRgb(255, 0, 0), Color.FromRgb(255, 255, 0));
        Assert.Equal(30.0, ThemeColorMath.ToHsl(orange).H, 0);

        // 350° and 10° meet at 0°, not 180°.
        var wrap = ThemeColorMath.MidpointHue(
            ThemeColorMath.FromHsl(0xFF, 350, 1, 0.5),
            ThemeColorMath.FromHsl(0xFF, 10, 1, 0.5));
        double h = ThemeColorMath.ToHsl(wrap).H;
        Assert.True(h < 1.0 || h > 359.0, $"hue {h}");
    }

    [Fact]
    public void Shift_clamps_each_channel()
    {
        Assert.Equal(Color.FromRgb(255, 20, 10), ThemeColorMath.Shift(Color.FromRgb(250, 10, 0), 10));
        Assert.Equal(Color.FromRgb(0, 0, 5), ThemeColorMath.Shift(Color.FromRgb(5, 3, 15), -10));
    }
}
