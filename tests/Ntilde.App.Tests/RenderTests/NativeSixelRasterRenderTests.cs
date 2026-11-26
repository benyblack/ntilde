using Ntilde.Rendering;
using Ntilde.Shell;
using Ntilde.VT;
using Ntilde.Tests.Infra;
using SkiaSharp;

namespace Ntilde.Tests.RenderTests;

[Trait("Lane", "PlatformBoot")]
[Collection("GoldenPng")]
public sealed class NativeSixelRasterRenderTests : IDisposable
{
    private readonly HashSet<TerminalBuffer> _ownedBuffers = new();
    public NativeSixelRasterRenderTests() => SnapshotService.EnsureAvaloniaInitialized();
    private const string Raster = "q\"1;1;16;16#1;2;100;0;0#2;2;0;100;0#3;2;0;0;100#4;2;100;100;0" +
        "#1!5~#2!11~-#1!5B#2!11B$#3!5{#4!11{-#3!5N#4!11N";

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    public void Native_pixels_use_presenter_P_independently_of_capture_S(double presenter, double capture)
    {
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, new NativeSixelBudget(), new SixelCellSize(10, 20));
        var parser = Parser(buffer);
        parser.Process("\x1b[?80h\x1bP" + Raster + "\x1b\\");
        Assert.Equal((16, 18), SixelGeometry.Measure(Raster));
        var placement = Assert.Single(buffer.Images);
        Assert.Equal((2, 1), (placement.CellWidth, placement.CellHeight));
        using var pixels = Capture(buffer, 10, 20, presenter, capture);
        int factorWidth = (int)(16 * capture / presenter);
        var bounds = ColoredBounds(pixels);
        Assert.Equal(((int)(4 * capture), 0, factorWidth, factorWidth), bounds);
        Assert.Equal(factorWidth * factorWidth, ColoredCount(pixels));
        Assert.Equal(SKColors.Red, pixels.GetPixel(bounds.X, bounds.Y));
        Assert.Equal(SKColors.Lime, pixels.GetPixel(bounds.X + bounds.Width - 1, bounds.Y));
        Assert.Equal(SKColors.Blue, pixels.GetPixel(bounds.X, bounds.Y + bounds.Height - 1));
        Assert.Equal(SKColors.Yellow, pixels.GetPixel(bounds.X + bounds.Width - 1, bounds.Y + bounds.Height - 1));
        Assert.Equal(SixelRasterPolicy.PresenterNativeVersion, SixelRasterPolicy.EnabledVersion);
    }

    [Theory]
    [InlineData(5, 8)]
    [InlineData(21, 40)]
    public void Font_metrics_move_cells_without_stretching_native_raster(float width, float height)
    {
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, new NativeSixelBudget(), new SixelCellSize(10, 20));
        Parser(buffer).Process("\x1b[?80h\x1bP" + Raster + "\x1b\\");
        var before = Assert.Single(buffer.ExportState(0).Sixels!).NativeRaster;
        using var pixels = Capture(buffer, width, height, 1, 1);
        Assert.Equal((4, 0, 16, 16), ColoredBounds(pixels));
        Assert.Equal(before, Assert.Single(buffer.ExportState(0).Sixels!).NativeRaster);
    }

    [Fact]
    public void Native_overhang_is_visible_when_reservation_is_above_the_current_viewport()
    {
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, new NativeSixelBudget(), new SixelCellSize(10, 20));
        var parser = Parser(buffer);
        parser.Process("\x1b[?80h\x1bP" + Raster + "\x1b\\");
        var intent = Assert.Single(buffer.ExportState(0).Sixels!).NativeRaster;
        parser.Process("\x1b[?80l\x1b[6;1H\n\n");
        Assert.Equal(8, buffer.TotalLines);
        using var pixels = Capture(buffer, 10, 4, 1, 1);
        Assert.Equal((4, 0, 16, 8), ColoredBounds(pixels));
        Assert.Equal(intent, Assert.Single(buffer.ExportState(100).Sixels!).NativeRaster);
    }

    [Fact]
    public void Missing_presenter_context_refuses_native_capture_instead_of_returning_blank_success()
    {
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, new NativeSixelBudget(), new SixelCellSize(10, 20));
        Parser(buffer).Process("\x1b[?80h\x1bP" + Raster + "\x1b\\");
        Assert.Throws<InvalidOperationException>(() => TerminalSnapshotRenderer.Capture(buffer,
            Metrics(10, 20), 100, 120, new TerminalSnapshotOptions { HideCursor = true }));
    }

    [Fact]
    public void Default_policy_keeps_the_legacy_whole_cell_projection()
    {
        var buffer = new TerminalBuffer(10, 6);
        Parser(buffer).Process("\x1b[?80h\x1bP" + Raster + "\x1b\\");
        using var pixels = Capture(buffer, 10, 20, 1, 1);
        Assert.Equal((4, 0, 20, 18), ColoredBounds(pixels));
    }


    [Fact]
    public void Materialized_source_expiry_preserves_rendered_pixels_but_refuses_state_export()
    {
        var budget = new NativeSixelBudget();
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, budget, new SixelCellSize(10, 20), new SixelSourceBudget(1000, 1));
        Parser(buffer).Process("\x1b[?80h\x1bP" + Raster + "\x1b\\");
        using var before = Capture(buffer, 10, 20, 1, 1);
        var placement = Assert.Single(buffer.Images);
        var handle = placement.ImageHandle;
        var cursor = (buffer.CursorCol, buffer.CursorRow);
        using var pressure = buffer.SixelSources.Retain("q@");
        var charged = budget.Usage;
        Assert.Throws<InvalidOperationException>(() => buffer.ExportState(100));
        using var after = Capture(buffer, 10, 20, 1, 1);
        Assert.Equal(before.Pixels, after.Pixels);
        Assert.Equal(cursor, (buffer.CursorCol, buffer.CursorRow));
        Assert.Same(handle, Assert.Single(buffer.Images).ImageHandle);
        Assert.Equal(charged, budget.Usage);
        buffer.ClearImages();
        var retired = new List<object>();
        buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        foreach (var owner in retired) ((IDisposable)owner).Dispose();
    }

    [Fact]
    public void Canonical_crop_survives_transfer_and_repeated_presentation_changes()
    {
        var source = TerminalBuffer.CreateNativeSixelTestBuffer(10, 20, new NativeSixelBudget(), new SixelCellSize(10, 20));
        var parser = Parser(source);
        parser.SixelCellSizeOverride = new(10, 3.25f);
        parser.Process("\x1b[?1049h\x1b[?80h\x1bP" + Raster + "\x1b\\\x1b[2;10r\x1b[S");
        var state = source.ExportState(0);
        state.Parser = parser.ExportState();
        Assert.Equal(6.5, Assert.Single(state.Sixels!).NativeRaster!.Crop.Top);
        var copy = TerminalBuffer.CreateNativeSixelTestBuffer(10, 20, new NativeSixelBudget(), new SixelCellSize(10, 20));
        var restored = Parser(copy);
        TerminalStateTransfer.Restore(copy, restored, state);
        using var original = Capture(source, 10, 3, 1, 1);
        using var transferred = Capture(copy, 10, 3, 1, 1);
        Assert.Equal(original.Pixels, transferred.Pixels);
        using var changed = Capture(copy, 21, 20, 2, 2);
        using var again = Capture(copy, 10, 3, 1, 1);
        Assert.Equal(original.Pixels, again.Pixels);
        Assert.Equal(6.5, Assert.Single(copy.Images).NativeRaster!.Crop.Top);
    }

    [Fact]
    public void Bitmap_canvas_mismatch_is_unavailable_instead_of_a_stretched_success()
    {
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, new NativeSixelBudget(), new SixelCellSize(10, 20));
        Parser(buffer).Process("\x1b[?80h\x1bP" + Raster + "\x1b\\");
        var placement = Assert.Single(buffer.Images);
        object original = placement.ImageHandle;
        using var wrong = new SKBitmap(2, 2);
        placement.ImageHandle = wrong;
        try { Assert.Throws<InvalidOperationException>(() => Capture(buffer, 10, 20, 1, 1)); }
        finally { placement.ImageHandle = original; }
    }


    [Theory]
    [InlineData(1, 1, 5, 8)]
    [InlineData(1, 2, 5, 8)]
    [InlineData(2, 1, 10, 20)]
    public void Pixels_outside_the_independent_raster_and_clear_envelopes_are_unchanged(double presenter, double capture, float width, float height)
    {
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, new NativeSixelBudget(), new SixelCellSize(10, 20));
        var parser = Parser(buffer);
        parser.Process("\x1b[5;9H\x1b[35mZ\x1b[?80h");
        using var before = Capture(buffer, width, height, presenter, capture);
        parser.Process("\x1bP" + Raster + "\x1b\\");
        using var after = Capture(buffer, width, height, presenter, capture);
        int left = (int)(4 * capture);
        int right = left + (int)Math.Ceiling(Math.Max(2 * Math.Round(width * capture), 16 * capture / presenter));
        int bottom = (int)Math.Ceiling(Math.Max(Math.Round(height * capture), 18 * capture / presenter));
        int compared = 0;
        for (int y = 0; y < after.Height; y++)
            for (int x = 0; x < after.Width; x++)
            {
                if (x >= left && x < right && y < bottom) continue;
                Assert.Equal(before.GetPixel(x, y), after.GetPixel(x, y));
                compared++;
            }
        Assert.True(compared > after.Width * after.Height / 2);
    }

    [Theory]
    [InlineData(5, 8)]
    [InlineData(21, 40)]
    public void Native_cell_anchor_moves_with_font_metrics_while_pixel_extent_stays_fixed(float width, float height)
    {
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, new NativeSixelBudget(), new SixelCellSize(10, 20));
        Parser(buffer).Process("\x1b[?80l\x1b[2;3H\x1bP" + Raster + "\x1b\\");
        using var pixels = Capture(buffer, width, height, 1, 1);
        Assert.Equal((4 + 2 * (int)width, (int)height, 16, 16), ColoredBounds(pixels));
    }

    [Fact]
    public void Fully_clipped_integer_reservation_still_draws_its_cropped_native_pixels()
    {
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 20, new NativeSixelBudget(), new SixelCellSize(10, 20));
        var parser = Parser(buffer);
        parser.Process("\x1b[?1049h\x1b[?80h\x1bP" + Raster + "\x1b\\");
        parser.SixelCellSizeOverride = new(10, 1);
        parser.Process("\x1b[2;20r\x1b[S\x1b[S");
        var image = Assert.Single(buffer.Images);
        Assert.Equal(image.CellHeight, image.ClipTopRows);
        using var pixels = Capture(buffer, 10, 1, 1, 1);
        Assert.Equal((4, 1, 16, 13), ColoredBounds(pixels));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1e-300)]
    public void Invalid_native_capture_scale_is_refused_before_rasterization(double scale)
    {
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, new NativeSixelBudget(), new SixelCellSize(10, 20));
        Assert.Throws<ArgumentOutOfRangeException>(() => TerminalSnapshotRenderer.Capture(buffer, Metrics(10, 20), 100, 120,
            new TerminalSnapshotOptions { HideCursor = true, NativeRasterPresenter = new(1), RenderScaling = scale }));
    }

    private AnsiParser Parser(TerminalBuffer buffer)
    {
        _ownedBuffers.Add(buffer);
        return new(buffer, false)
        {
            ImageDecoder = new SkiaImageDecoder(),
            SixelCellSizeOverride = new(10, 20),
        };
    }

    public void Dispose()
    {
        foreach (var buffer in _ownedBuffers)
        {
            buffer.ClearImages();
            var retired = new List<object>();
            buffer.DrainRetiredImageHandles(retired, long.MaxValue);
            foreach (var owner in retired) ((IDisposable)owner).Dispose();
            if (buffer.NativeSixelBudget is { } budget) Assert.Equal((0, 0L), budget.Usage);
        }
    }

    private static CellMetrics Metrics(float width, float height) => new()
    {
        CellWidth = width,
        CellHeight = height,
        Baseline = height * 0.75f,
        Ascent = height * 0.75f,
        Descent = height * 0.25f,
    };

    private static SKBitmap Capture(TerminalBuffer buffer, float width, float height, double presenter, double capture) =>
        TerminalSnapshotRenderer.Capture(buffer, Metrics(width, height), (int)(buffer.Cols * width + 4), (int)(buffer.Rows * height),
            new TerminalSnapshotOptions
            {
                HideCursor = true,
                RenderScaling = capture,
                NativeRasterPresenter = new(presenter),
            });

    private static bool Colored(SKColor value) => value == SKColors.Red || value == SKColors.Lime ||
        value == SKColors.Blue || value == SKColors.Yellow;

    private static int ColoredCount(SKBitmap bitmap) => bitmap.Pixels.Count(Colored);

    private static (int X, int Y, int Width, int Height) ColoredBounds(SKBitmap bitmap)
    {
        var positions = new List<(int X, int Y)>();
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                if (Colored(bitmap.GetPixel(x, y))) positions.Add((x, y));
        Assert.NotEmpty(positions);
        int left = positions.Min(p => p.X), top = positions.Min(p => p.Y);
        return (left, top, positions.Max(p => p.X) - left + 1, positions.Max(p => p.Y) - top + 1);
    }
}
