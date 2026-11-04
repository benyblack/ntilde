using Ntilde.Platform;
using Ntilde.Rendering;
using Ntilde.Shell;
using Ntilde.Tests.Infra;
using Ntilde.VT;
using SkiaSharp;

namespace Ntilde.Tests.RenderTests;

[Trait("Lane", "PlatformBoot")]
[Collection("GoldenPng")]
public sealed class KittyImageLifecycleRenderTests
{
    private static readonly CellMetrics Metrics = new() { CellWidth = 8, CellHeight = 16, Baseline = 12, Ascent = 12, Descent = 4 };
    private static string Command(string controls, string payload = "") => $"\x1b_G{controls};{payload}\x1b\\";

    [Fact]
    public void Numbered_transmission_only_paints_after_placement_and_explicit_delete_removes_the_ink()
    {
        var buffer = new TerminalBuffer(20, 10);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false)
        { ImageDecoder = new SkiaImageDecoder(), CellWidth = 8, CellHeight = 16 };
        var replies = new List<string>(); parser.OnResponse = replies.Add;
        parser.Process("\x1b[3;5H");
        var retired = new List<object>();
        try
        {
            SKColor background = CapturePixel(buffer, 40, 40);
            parser.Process(Command("a=t,f=32,s=1,v=1,I=13", "/wAA/w=="));
            Assert.Equal("\x1b_Gi=1,I=13;OK\x1b\\", Assert.Single(replies));
            Assert.Equal(background, CapturePixel(buffer, 40, 40)); Assert.Empty(buffer.Images);
            parser.Process(Command("a=p,I=13,c=2,r=2,C=1"));
            TerminalImage image = Assert.Single(buffer.Images);
            Assert.Equal((4, 2, 2, 2), (image.CellX, image.CellY, image.CellWidth, image.CellHeight));
            Assert.Equal(SKColors.Red, CapturePixel(buffer, 40, 40));
            Assert.Equal(background, CapturePixel(buffer, 40, 48)); // Square source fits above the lower cell padding.
            // The production snapshot grid starts four pixels inside the capture.
            Assert.Equal(background, CapturePixel(buffer, 35, 40)); Assert.Equal(background, CapturePixel(buffer, 52, 40));
            parser.Process(Command("a=d,d=n,I=13")); Assert.Equal(background, CapturePixel(buffer, 40, 40));
            parser.Process(Command("a=p,i=1,c=2,r=2,C=1")); Assert.Equal(SKColors.Red, CapturePixel(buffer, 40, 40));
            parser.Process(Command("a=d,d=I,i=1")); Assert.Equal(background, CapturePixel(buffer, 40, 40));
            parser.Process(Command("a=p,i=1,C=1")); Assert.Contains(";ENOENT:", replies[^1], StringComparison.Ordinal);
            Assert.Equal(background, CapturePixel(buffer, 40, 40));
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
