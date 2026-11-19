using Ntilde.Platform;
using Ntilde.Rendering;
using Ntilde.Shell;
using Ntilde.Tests.Infra;
using Ntilde.VT;
using SkiaSharp;

namespace Ntilde.Tests.RenderTests;

/// <summary>Painted pixels of Kitty Unicode placeholders (#989): the image appears on the placeholder cells, and only there.</summary>
[Trait("Lane", "PlatformBoot")]
[Collection("GoldenPng")]
public sealed class KittyUnicodePlaceholderRenderTests
{
    private static readonly CellMetrics Metrics = new() { CellWidth = 8, CellHeight = 16, Baseline = 12, Ascent = 12, Descent = 4 };
    private const string Placeholder = "\U0010EEEE";
    private static string Command(string controls, string payload = "") => $"\x1b_G{controls};{payload}\x1b\\";
    private static string Mark(int number) => char.ConvertFromUtf32(KittyPlaceholders.DiacriticFor(number));

    // Red and green over blue and yellow, one pixel each.
    private static readonly string Quadrants = Convert.ToBase64String(
    [
        255, 0, 0, 255, 0, 255, 0, 255,
        0, 0, 255, 255, 255, 255, 0, 255
    ]);

    [Fact]
    public void The_image_is_painted_on_the_placeholder_cells_only_and_erasing_them_removes_it()
    {
        var buffer = new TerminalBuffer(20, 10);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false)
        { ImageDecoder = new SkiaImageDecoder(), CellWidth = 8, CellHeight = 16 };
        var retired = new List<object>();
        try
        {
            SKColor background = CapturePixel(buffer, 32, 36);
            parser.Process(Command("a=t,f=32,s=2,v=2,i=42", Quadrants));
            parser.Process(Command("a=p,U=1,i=42,c=2,r=1"));
            Assert.Equal(background, CapturePixel(buffer, 32, 36)); // a virtual placement paints nothing

            // Two cells wide, one row high: a square 16 x 16 px grid, so each cell is one half of the image. The
            // production snapshot grid starts four pixels inside the capture, so cells 3 and 4 of row 2 are x 28-44.
            parser.Process($"\x1b[3;4H\x1b[38;5;42m{Placeholder}{Mark(0)}{Mark(0)}{Placeholder}{Mark(0)}{Mark(1)}\x1b[39m");
            Assert.Equal(SKColors.Red, CapturePixel(buffer, 32, 36));
            Assert.Equal(SKColors.Lime, CapturePixel(buffer, 40, 36));
            Assert.Equal(SKColors.Blue, CapturePixel(buffer, 32, 44));
            Assert.Equal(SKColors.Yellow, CapturePixel(buffer, 40, 44));
            Assert.Equal(background, CapturePixel(buffer, 24, 36));
            Assert.Equal(background, CapturePixel(buffer, 48, 36));

            parser.Process("\x1b[2J");
            Assert.Equal(background, CapturePixel(buffer, 32, 36));
            Assert.Equal(background, CapturePixel(buffer, 40, 44));
        }
        finally
        {
            buffer.ClearImages(); buffer.DrainRetiredImageHandles(retired, long.MaxValue);
            foreach (IDisposable handle in retired.Distinct(ReferenceEqualityComparer.Instance)) handle.Dispose();
        }
    }

    [Fact]
    public void A_cell_showing_the_second_column_paints_that_half_of_the_image_wherever_it_is_printed()
    {
        var buffer = new TerminalBuffer(20, 10);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false)
        { ImageDecoder = new SkiaImageDecoder(), CellWidth = 8, CellHeight = 16 };
        var retired = new List<object>();
        try
        {
            parser.Process(Command("a=t,f=32,s=2,v=2,i=42", Quadrants));
            parser.Process(Command("a=p,U=1,i=42,c=2,r=1"));
            // Only the second column of the grid, in the first screen cell of row 0 (x 4-12).
            parser.Process($"\x1b[1;1H\x1b[38;5;42m{Placeholder}{Mark(0)}{Mark(1)}\x1b[39m");
            Assert.Equal(SKColors.Lime, CapturePixel(buffer, 8, 4));
            Assert.Equal(SKColors.Yellow, CapturePixel(buffer, 8, 12));
        }
        finally
        {
            buffer.ClearImages(); buffer.DrainRetiredImageHandles(retired, long.MaxValue);
            foreach (IDisposable handle in retired.Distinct(ReferenceEqualityComparer.Instance)) handle.Dispose();
        }
    }

    private static SKColor CapturePixel(TerminalBuffer buffer, int x, int y)
    {
        byte[] png = SnapshotService.CapturePng(buffer, Metrics, 160, 160, new SnapshotCaptureOptions { HideCursor = true });
        using var bitmap = SKBitmap.Decode(png);
        Assert.NotNull(bitmap);
        return bitmap.GetPixel(x, y);
    }
}
