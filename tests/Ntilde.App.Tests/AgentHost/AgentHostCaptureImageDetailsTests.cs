using System;
using System.IO;
using System.Text.Json;
using Ntilde.AgentHost;
using Ntilde.AgentHost.Contracts;
using Ntilde.Rendering;
using Ntilde.Shell;
using Ntilde.VT;
using SkiaSharp;

namespace Ntilde.AppTests.AgentHost;

public partial class AgentHostCaptureProtocolTests
{
    private const string RedSixel = "\x1bPq#0;2;100;0;0#0!16~-!16~-!16N\x1b\\";

    [Theory]
    [InlineData("live", false)]
    [InlineData("live", true)]
    [InlineData("render", false)]
    public void Image_facts_require_explicit_render_details_before_any_capture(string mode, bool details)
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry);
        var journal = new AgentActivityJournal();
        using var service = NewService(registry, journal);
        var response = Handle(service, CaptureRequestLine(registration.PaneId, mode: mode, details: details, imageDetails: true));
        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, response.Error?.Code);
        Assert.Empty(journal.Snapshot());
        Assert.Equal(AgentAttentionTier.Idle, registration.AttentionMachine.Snapshot().Tier);
        Assert.Empty(Directory.Exists(_exportDir) ? Directory.GetFiles(_exportDir) : Array.Empty<string>());
    }

    [Theory]
    [InlineData(1.0, false)]
    [InlineData(2.0, false)]
    [InlineData(1.0, true)]
    [InlineData(2.0, true)]
    public void Exact_draw_facts_keep_parse_time_units_and_do_not_claim_native_pixel_fidelity(double scale, bool deferred)
    {
        var registry = new AgentSessionRegistry();
        var metrics = Metrics with { CellWidth = 8.5f, CellHeight = 18.5f };
        var registration = Register(registry, metrics: metrics);
        registration.Buffer.Theme = new TerminalTheme { Background = TermColor.Black };
        var parser = new AnsiParser(registration.Buffer)
        {
            ImageDecoder = new SkiaImageDecoder { DeferSixelDecoding = deferred },
            SixelCellSizeOverride = new SixelCellSize(17, 37),
        };
        parser.Process("\x1b[?25l" + RedSixel);
        var placed = Assert.Single(registration.Buffer.Images);
        var original = placed.SixelPlacement;
        if (deferred) Assert.False(Assert.IsType<DeferredSixelImage>(placed.ImageHandle).IsMaterialized);
        // Later host/parser changes cannot become alleged creation inputs.
        parser.CellWidth = parser.CellHeight = 100;
        parser.SixelCellSizeOverride = new SixelCellSize(200, 200);
        using var service = NewService(registry);
        var ordinary = Result(Handle(service, CaptureRequestLine(registration.PaneId, scale: scale, details: true)));
        var capture = Result(Handle(service, CaptureRequestLine(registration.PaneId, scale: scale, details: true, imageDetails: true)));
        Assert.Equal(File.ReadAllBytes(ordinary.FilePath), File.ReadAllBytes(capture.FilePath));
        Assert.Equal(JsonSerializer.Serialize(ordinary.RenderDetails, AgentHostJsonContext.Default.CaptureRenderDetails),
            JsonSerializer.Serialize(capture.RenderDetails, AgentHostJsonContext.Default.CaptureRenderDetails));
        Assert.Equal(1, capture.RenderDetails!.SchemaVersion);
        var images = Assert.IsType<CaptureImageDetails>(capture.ImageDetails);
        Assert.True(images.Complete);
        Assert.Equal(1, images.TotalPlacements);
        var image = Assert.Single(images.Placements);
        Assert.Equal(placed.ImageId, image.PlacementId);
        Assert.Equal((0, 0, 1, 1), (image.Col, image.ViewportRow, image.WidthCells, image.HeightCells));
        Assert.Equal((16, 18), (image.BitmapWidthPx, image.BitmapHeightPx));
        var sixel = Assert.IsType<CaptureSixelPlacementDetails>(image.SixelPlacement);
        Assert.Equal((16, 18), (sixel.ReservationWidthPx, sixel.ReservationHeightPx));
        Assert.Equal((17d, 37d), (sixel.CellWidthPx, sixel.CellHeightPx));
        Assert.Equal("image-decoder", sixel.DimensionOrigin);
        Assert.Equal("sixel-override", sixel.CellWidthOrigin);
        Assert.Equal("sixel-override", sixel.CellHeightOrigin);
        Assert.Equal("float32-ceiling-v1", sixel.Arithmetic);
        Assert.Equal(original, placed.SixelPlacement);
        // Metadata faithfully reports whole-cell projection, including existing stretching.
        // It does not promise that source pixels equal controlled-capture pixels.
        var grid = capture.RenderDetails.Grid;
        Assert.NotEqual(16, image.WidthCells * grid.CellWidthPx);
        using var png = SKBitmap.Decode(File.ReadAllBytes(capture.FilePath));
        Assert.Equal(SKColors.Red, png.GetPixel(grid.OriginXPx, grid.OriginYPx));
        Assert.Equal(SKColors.Red, png.GetPixel(grid.OriginXPx + grid.CellWidthPx - 1, grid.OriginYPx));
        Assert.Equal(SKColors.Black, png.GetPixel(grid.OriginXPx + grid.CellWidthPx, grid.OriginYPx));
        Assert.Equal(SKColors.Black, png.GetPixel(grid.OriginXPx, grid.OriginYPx + grid.CellHeightPx - 1));
        registration.Buffer.ClearImages();
        Assert.Equal((16, 18), (image.BitmapWidthPx, image.BitmapHeightPx));
        Assert.Equal((17d, 37d), (sixel.CellWidthPx, sixel.CellHeightPx));
    }

    [Fact]
    public void Json_retains_the_exact_widened_float32_denominator_not_a_short_float_decimal()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry);
        var parser = new AnsiParser(registration.Buffer)
        { ImageDecoder = new SkiaImageDecoder(), SixelCellSizeOverride = new SixelCellSize(1.4f, 6) };
        parser.Process(RedSixel);
        using var service = NewService(registry);
        var response = Handle(service, CaptureRequestLine(registration.PaneId, details: true, imageDetails: true));
        var result = Result(response);
        var sixel = Assert.Single(result.ImageDetails!.Placements).SixelPlacement!;
        Assert.Equal((double)1.4f, sixel.CellWidthPx);
        Assert.NotEqual(1.4d, sixel.CellWidthPx);
        double wire = response.Result!.Value.GetProperty("imageDetails").GetProperty("placements")[0]
            .GetProperty("sixelPlacement").GetProperty("cellWidthPx").GetDouble();
        Assert.Equal((double)1.4f, wire);
    }

    [Fact]
    public void Generic_clipped_aspect_and_unavailable_images_keep_unknown_provenance()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry);
        using var bitmap = new SKBitmap(16, 18);
        bitmap.Erase(SKColors.Red);
        var generic = new TerminalImage(bitmap, -1, -1, 4, 3)
        { PreserveAspectRatio = true, ClipTopRows = 1, ClipBottomRows = 1, ClipRightColumns = 1 };
        registration.Buffer.AddImage(generic);
        registration.Buffer.AddImage(new TerminalImage(new object(), 5, 1, 1, 1));
        using var service = NewService(registry);
        var result = Result(Handle(service, CaptureRequestLine(registration.PaneId, details: true, imageDetails: true)));
        var image = result.ImageDetails!.Placements[0];
        Assert.Equal(generic.ImageId, image.PlacementId);
        Assert.Equal((-1, -1, 4, 3), (image.Col, image.ViewportRow, image.WidthCells, image.HeightCells));
        Assert.Equal((1, 1, 1), (image.ClipTopRows, image.ClipBottomRows, image.ClipRightColumns));
        Assert.True(image.PreserveAspectRatio);
        Assert.Equal((16, 18), (image.BitmapWidthPx, image.BitmapHeightPx));
        Assert.Null(image.SixelPlacement);
        Assert.Null(result.ImageDetails.Placements[1].SixelPlacement);
        Assert.Null(result.ImageDetails.Placements[1].BitmapWidthPx);
        Assert.Null(result.ImageDetails.Placements[1].BitmapHeightPx);
        var oldJson = JsonSerializer.Serialize(result, AgentHostJsonContext.Default.CaptureScreenResult);
        Assert.Contains("\"sixelPlacement\":null", oldJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(512, true)]
    [InlineData(513, false)]
    public void Metadata_has_an_explicit_fixed_ceiling_without_silent_truncation(int count, bool complete)
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry);
        for (int i = 0; i < count; i++) registration.Buffer.AddImage(new TerminalImage(new object(), 0, 0, 1, 1));
        using var service = NewService(registry);
        var result = Result(Handle(service, CaptureRequestLine(registration.PaneId, details: true, imageDetails: true)));
        Assert.Equal(count, result.ImageDetails!.TotalPlacements);
        Assert.Equal(complete, result.ImageDetails.Complete);
        Assert.Equal(complete ? count : 0, result.ImageDetails.Placements.Length);
        Assert.Equal(512, AgentHostProtocol.MaxCaptureImagePlacements);
    }

    [Fact]
    public void Clearing_and_retiring_after_snapshot_keeps_original_leased_bitmap_placement_and_provenance()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry);
        var parser = new AnsiParser(registration.Buffer)
        { ImageDecoder = new SkiaImageDecoder(), SixelCellSizeOverride = new SixelCellSize(17, 37) };
        parser.Process("\x1b[?25l" + RedSixel);
        var original = Assert.Single(registration.Buffer.Images);
        using var originalBitmap = Assert.IsType<SKBitmap>(original.ImageHandle);
        using var replacementBitmap = new SKBitmap(8, 8);
        replacementBitmap.Erase(SKColors.Green);
        var replacement = new TerminalImage(replacementBitmap, 5, 5, 2, 2);
        var retired = new System.Collections.Generic.List<object>();
        int callbacks = 0;
        Assert.True(registration.TryCapturePng(0, 1, out var capture, out _,
            new AgentCaptureOptions(IncludeRenderDetails: true, IncludeImageDetails: true)
            {
                AfterSnapshotForTest = () =>
                {
                    callbacks++;
                    Assert.False(registration.Buffer.Lock.IsReadLockHeld);
                    Assert.False(registration.Buffer.Lock.IsWriteLockHeld);
                    registration.Buffer.ClearImages();
                    registration.Buffer.AddImage(replacement);
                    registration.Buffer.ClearImages();
                    // Native retirement cannot destroy either borrowed handle during this draw.
                    Assert.False(registration.Buffer.DrainRetiredImageHandles(retired, long.MaxValue));
                    Assert.Empty(retired);
                    Assert.NotEqual(IntPtr.Zero, originalBitmap.Handle);
                    // A current replacement can be disposed: it is not this draw's source.
                    replacementBitmap.Dispose();
                    parser.SixelCellSizeOverride = new SixelCellSize(999, 999);
                },
            }));
        Assert.Equal(1, callbacks);
        var image = Assert.Single(capture.ImageDetails!.Placements);
        Assert.Equal(original.ImageId, image.PlacementId);
        Assert.Equal((0, 0, 1, 1), (image.Col, image.ViewportRow, image.WidthCells, image.HeightCells));
        Assert.Equal((16, 18), (image.BitmapWidthPx, image.BitmapHeightPx));
        Assert.Equal((17d, 37d), (image.SixelPlacement!.CellWidthPx, image.SixelPlacement.CellHeightPx));
        using var png = SKBitmap.Decode(capture.Png);
        Assert.Equal(SKColors.Red, png.GetPixel(capture.RenderDetails!.Grid.OriginXPx, 0));
        Assert.Empty(registration.Buffer.Images);
        Assert.True(registration.Buffer.DrainRetiredImageHandles(retired, long.MaxValue));
        Assert.Contains(originalBitmap, retired);
        foreach (var handle in retired) (handle as IDisposable)?.Dispose();
        Assert.Equal((16, 18), (image.BitmapWidthPx, image.BitmapHeightPx));
    }

    [Fact]
    public void Mutation_before_draw_reports_the_actual_replacement_not_a_second_later_lookup()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry);
        var old = new TerminalImage(new object(), 1, 1, 1, 1);
        var replacement = new TerminalImage(new object(), 3, 2, 4, 2);
        registration.Buffer.AddImage(old);
        Assert.True(registration.TryCapturePng(0, 1, out var capture, out _,
            new AgentCaptureOptions(IncludeRenderDetails: true, IncludeImageDetails: true)
            { BeforeDrawForTest = () => { registration.Buffer.ClearImages(); registration.Buffer.AddImage(replacement); } }));
        var projected = Assert.Single(capture.ImageDetails!.Placements);
        Assert.Equal(replacement.ImageId, projected.PlacementId);
        Assert.Equal((3, 2, 4, 2), (projected.Col, projected.ViewportRow, projected.WidthCells, projected.HeightCells));
        registration.Buffer.ClearImages();
        Assert.Equal(replacement.ImageId, projected.PlacementId);
    }
}
