using System.Security.Cryptography;
using System.Text;
using Ntilde.Platform;
using Ntilde.Rendering;
using Ntilde.Shell;
using Ntilde.Tests.Infra;
using Ntilde.VT;
using SkiaSharp;

namespace Ntilde.Tests.RenderTests;

[Trait("Lane", "PlatformBoot")]
[Collection("GoldenPng")]
// Stable creation-cell to capture-grid projection; no live monitor/font-resize claim.
public sealed class SixelNativePixelReproductionTests
{
    // Exact independently retained fixture, not a renderer-generated expectation.
    private const string Fixture = "\u001bP0;1q\"1;1;16;16#0;2;100;0;0#1;2;0;100;0#2;2;0;0;100#3;2;100;100;0#0~~~~~???????????$#1?????~~~~~~~~~~~$#2????????????????$#3????????????????-#0@@@@@???????????$#1?????@@@@@@@@@@@$#2}}}}}???????????$#3?????}}}}}}}}}}}-#0????????????????$#1????????????????$#2NNNNN???????????$#3?????NNNNNNNNNNN\u001b\\";
    private const string FixtureSha256 = "12625065714710ecd364e1cb9ad7e2afe2cf73974dffa132cb6a86c9f14f64a4";
    private static readonly TermColor Background = TermColor.FromRgb(16, 16, 16);
    private static readonly CellMetrics CaptureMetrics = new()
    { CellWidth = 10, CellHeight = 20, Baseline = 15, Ascent = 15, Descent = 5 };

    [Theory]
    [InlineData(10, 20, 2)]
    [InlineData(20, 40, 1)]
    public void Fixed_source_decodes_to_16_by_18_with_16_painted_rows_and_reserves_expected_cells(float cellWidth, float cellHeight, int columns)
    {
        var buffer = NewBuffer(cellWidth, cellHeight); var parser = NewParser(buffer, cellWidth, cellHeight);
        try
        {
            parser.Process(Fixture);
            AssertSourceAndPlacement(buffer, columns);
        }
        finally { DisposeImages(buffer); }
    }

    [Theory]
    [InlineData(10, 20, 2, 16, 16)]
    [InlineData(20, 40, 1, 8, 8)]
    public void Native_pixels_keep_their_calibrated_extent_instead_of_filling_the_reserved_cells(
        float cellWidth, float cellHeight, int columns, int paintedWidth, int paintedHeight)
    {
        var buffer = NewBuffer(cellWidth, cellHeight); var parser = NewParser(buffer, cellWidth, cellHeight);
        using var before = Capture(buffer, cellWidth / CaptureMetrics.CellWidth, out PixelGrid grid);
        Assert.Equal((10, 20), (grid.CellWidthPx, grid.CellHeightPx));
        try
        {
            parser.Process(Fixture);
            var placement = AssertSourceAndPlacement(buffer, columns);
            using var after = Capture(buffer, cellWidth / CaptureMetrics.CellWidth, out PixelGrid afterGrid);
            Assert.Equal(grid, afterGrid);
            var envelope = new SKRectI(grid.XForCol(3), grid.YForRowTop(3), grid.XForCol(3 + columns), grid.YForRowTop(4));
            AssertUnchangedOutside(before, after, envelope, grid);
            var actual = PaintedBounds(after, envelope);
            var expected = new SKRectI(envelope.Left, envelope.Top, envelope.Left + paintedWidth, envelope.Top + paintedHeight);
            int mismatches = 0;
            for (int y = envelope.Top; y < envelope.Bottom; y++) for (int x = envelope.Left; x < envelope.Right; x++)
            {
                // Pixel centres independently select nearest source pixels at the explicit native
                // projection. At an exact tie, the upper/left texel owns the boundary.
                int sx = (int)Math.Ceiling((x - envelope.Left + 0.5) * cellWidth / grid.CellWidthPx) - 1;
                int sy = (int)Math.Ceiling((y - envelope.Top + 0.5) * cellHeight / grid.CellHeightPx) - 1;
                SKColor native = sx < 16 && sy < 16 ? SourceColor(sx, sy) : new SKColor(16, 16, 16);
                if (after.GetPixel(x, y) != native) mismatches++;
            }
            Assert.True(actual.Equals(expected) && mismatches == 0,
                $"Native SIXEL mismatch: parse cells={cellWidth}x{cellHeight}, capture grid={grid.CellWidthPx}x{grid.CellHeightPx}, " +
                $"reservation={placement.CellWidth}x{placement.CellHeight}, bitmap=16x18, " +
                $"expected painted={paintedWidth}x{paintedHeight} at ({expected.Left},{expected.Top}), " +
                $"actual painted={actual.Width}x{actual.Height} at ({actual.Left},{actual.Top}), " +
                $"native pixel mismatches={mismatches}, source SHA-256={FixtureSha256}.");
        }
        finally { DisposeImages(buffer); }
    }

    private static TerminalBuffer NewBuffer(float width, float height)
    {
        var buffer = new TerminalBuffer(20, 8, SixelRasterPolicy.PresenterNativeVersion)
        { Theme = new TerminalTheme { Foreground = TermColor.White, Background = Background } };
        new AnsiParser(buffer) { SixelCellSizeOverride = new(width, height) }.Process("\u001b[48;2;17;41;73m \u001b[0m\u001b[?25l\u001b[4;4H");
        return buffer;
    }

    private static AnsiParser NewParser(TerminalBuffer buffer, float width, float height) => new(buffer, forceConPtyFiltering: false)
    {
        ImageDecoder = new SkiaImageDecoder(),
        CellWidth = CaptureMetrics.CellWidth,
        CellHeight = CaptureMetrics.CellHeight,
        SixelCellSizeOverride = new SixelCellSize(width, height),
    };

    private static TerminalImage AssertSourceAndPlacement(TerminalBuffer buffer, int columns)
    {
        Assert.Equal(FixtureSha256, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Fixture))).ToLowerInvariant());
        Assert.Equal((16, 18), SixelGeometry.Measure(Fixture.AsSpan(2, Fixture.Length - 4)));
        var placement = Assert.Single(buffer.Images);
        Assert.True(placement.IsSixelImage);
        Assert.Equal((3, 3, columns, 1), (placement.CellX, placement.CellY, placement.CellWidth, placement.CellHeight));
        Assert.Equal((0, 0, 0), (placement.ClipTopRows, placement.ClipBottomRows, placement.ClipRightColumns));
        int decodes = 1;
        var source = Assert.IsType<NativeSixelImage>(placement.ImageHandle).GetBitmap(ref decodes, out _);
        Assert.NotNull(source);
        Assert.Equal((16, 18), (source.Width, source.Height));
        for (int y = 0; y < 18; y++) for (int x = 0; x < 16; x++)
            Assert.Equal(y < 16 ? SourceColor(x, y) : new SKColor(0, 0, 0, 0), source.GetPixel(x, y));
        return placement;
    }

    private static SKColor SourceColor(int x, int y) => (x < 5, y < 7) switch
    { (true, true) => SKColors.Red, (false, true) => SKColors.Lime, (true, false) => SKColors.Blue, _ => SKColors.Yellow };

    private static SKBitmap Capture(TerminalBuffer buffer, double presenterScale, out PixelGrid grid)
    {
        SnapshotService.EnsureAvaloniaInitialized();
        var result = TerminalSnapshotRenderer.CaptureWithContext(buffer, CaptureMetrics, 240, 160,
            new TerminalSnapshotOptions { HideCursor = true, FillBackground = true, RenderScaling = 1,
                NativeRasterPresenter = new NativeRasterPresenterContext(presenterScale) }, true, out var info);
        grid = Assert.IsType<SnapshotRenderContext>(info.Context).Frame.PixelGrid;
        return result;
    }

    private static SKRectI PaintedBounds(SKBitmap image, SKRectI envelope)
    {
        int left = envelope.Right, right = envelope.Left, top = envelope.Bottom, bottom = envelope.Top;
        for (int y = envelope.Top; y < envelope.Bottom; y++) for (int x = envelope.Left; x < envelope.Right; x++)
        {
            if (image.GetPixel(x, y) == new SKColor(16, 16, 16)) continue;
            left = Math.Min(left, x); right = Math.Max(right, x + 1); top = Math.Min(top, y); bottom = Math.Max(bottom, y + 1);
        }
        Assert.True(right > left && bottom > top);
        return new SKRectI(left, top, right, bottom);
    }

    private static void AssertUnchangedOutside(SKBitmap before, SKBitmap after, SKRectI envelope, PixelGrid grid)
    {
        Assert.Equal(before.Info, after.Info);
        Assert.Equal(new SKColor(17, 41, 73), before.GetPixel(grid.OriginXPx + 1, grid.OriginYPx + 1));
        for (int y = 0; y < before.Height; y++) for (int x = 0; x < before.Width; x++)
            if (envelope.Contains(x, y)) Assert.Equal(new SKColor(16, 16, 16), before.GetPixel(x, y));
            else Assert.Equal(before.GetPixel(x, y), after.GetPixel(x, y));
    }

    private static void DisposeImages(TerminalBuffer buffer)
    {
        buffer.ClearImages(); var retired = new List<object>(); buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        foreach (IDisposable handle in retired.Distinct(ReferenceEqualityComparer.Instance)) handle.Dispose();
    }
}
