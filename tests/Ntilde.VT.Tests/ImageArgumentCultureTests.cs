using System;
using System.Globalization;
using Ntilde.VT;

namespace Ntilde.VT.Tests;

// #108: iTerm2 image arguments are ASCII protocol text, so they parse the same in every locale. They went through the
// current culture: de-DE read a "25.5px" width as 255 pixels (the dot is its group separator), and tr-TR lowered a "WIDTH"
// key to "w<dotless i>dth", which then matched no argument.
public class ImageArgumentCultureTests
{
    [Fact]
    public void A_fractional_pixel_width_parses_the_same_under_a_comma_decimal_culture()
    {
        TerminalImage image = PlaceUnder("de-DE", "inline=1;width=25.5px");

        Assert.Equal(3, image.CellWidth); // 25.5 px over the parser's default 10 px cell, rounded up
    }

    [Fact]
    public void An_upper_case_argument_name_is_recognised_under_a_turkish_culture()
    {
        TerminalImage image = PlaceUnder("tr-TR", "inline=1;WIDTH=5");

        Assert.Equal(5, image.CellWidth); // ignored, it would fall back to the 10-cell default
    }

    private static TerminalImage PlaceUnder(string culture, string arguments)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer) { ImageDecoder = new FixedSizeImageDecoder(30, 40) };
            parser.Process($"\x1b]1337;File={arguments}:{Convert.ToBase64String(new byte[] { 1, 2, 3, 4 })}\x07");
            return Assert.Single(buffer.Images);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
