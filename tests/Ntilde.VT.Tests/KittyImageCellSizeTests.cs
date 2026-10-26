using System;
using Ntilde.VT;

namespace Ntilde.VT.Tests;

// How many cells a kitty image covers: c= and r= give columns and rows, w= and h= give pixels (or a
// percentage of the screen), and whichever side is still missing follows from the image's aspect
// ratio and the cell's. Both sides are clamped to 200.
public class KittyImageCellSizeTests
{
    // 50x70 pixels against the parser's default 10x20 cell: 5 columns wide, and an aspect ratio
    // (1.4 rows per column at 0.5 cell ratio) that lands no expected size on an exact integer, so
    // a rounding change cannot pass by accident.
    private const int PixelWidth = 50;
    private const int PixelHeight = 70;

    [Theory]
    [InlineData("", 5, 4)]               // from the pixels: ceil(50/10) = 5, then ceil(5 * 0.5 * 1.4) = 4
    [InlineData("c=8,", 8, 6)]           // ceil(8 * 0.7) = 6
    [InlineData("r=6,", 9, 6)]           // ceil(6 / 0.7) = 9
    [InlineData("c=4,r=3,", 4, 3)]       // both given: no aspect ratio involved
    [InlineData("w=120,", 12, 9)]        // a bare w= is pixels: 120px = 12 columns, ceil(12 * 0.7) = 9
    [InlineData("h=50%,", 18, 12)]       // half of 24 rows = 12, ceil(12 / 0.7) = 18
    [InlineData("c=4,w=200,", 4, 3)]     // c= wins over w=
    [InlineData("c=300,", 200, 200)]     // clamped
    public void Kitty_image_cell_size(string sizeParams, int expectedWidth, int expectedHeight)
    {
        var buffer = new TerminalBuffer(80, 24);
        // forceConPtyFiltering: false, so a native APC image is placed on every platform.
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = new FixedSizeImageDecoder(PixelWidth, PixelHeight) };
        string payload = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });

        parser.Process($"\x1b_G{sizeParams}a=T,f=100;{payload}\x1b\\");

        TerminalImage image = Assert.Single(buffer.Images);
        Assert.Equal((expectedWidth, expectedHeight), (image.CellWidth, image.CellHeight));
    }
}
