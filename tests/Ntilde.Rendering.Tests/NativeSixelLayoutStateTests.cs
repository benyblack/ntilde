using Ntilde.VT;
using SkiaSharp;

namespace Ntilde.Rendering.Tests;

public sealed class NativeSixelLayoutStateTests
{
    [Fact]
    public void Native_creation_exports_its_measured_production_intent()
    {
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, new NativeSixelBudget(), new SixelCellSize(10, 20));
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false)
        {
            ImageDecoder = new SkiaImageDecoder(),
            SixelCellSizeOverride = new(10, 20),
        };
        parser.Process("\x1b[?80h\x1bPq#2!16~-!16~-!16N\x1b\\");
        var state = Assert.Single(buffer.ExportState(0).Sixels!);
        var intent = Assert.IsType<SixelRasterIntent>(state.NativeRaster);
        Assert.Equal((16, 18), (intent.CanvasWidth, intent.CanvasHeight));
        Assert.Equal(new SixelSourceCrop(0, 0, 16, 18), intent.Crop);
    }

    [Fact]
    public void Materialized_source_expiry_refuses_whole_native_export_without_retiring_live_pixels()
    {
        var budget = new NativeSixelBudget();
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, budget, new SixelCellSize(10, 20), new SixelSourceBudget(100, 1));
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false)
        {
            ImageDecoder = new SkiaImageDecoder(),
            SixelCellSizeOverride = new(10, 20),
        };
        parser.Process("\x1b[?80h\x1bPq#2~\x1b\\");
        var placement = Assert.Single(buffer.Images);
        var owner = Assert.IsType<NativeSixelImage>(placement.ImageHandle);
        int work = 1;
        var bitmap = Assert.IsType<SKBitmap>(owner.GetBitmap(ref work, out _));
        using var pressure = buffer.SixelSources.Retain("q@");
        Assert.Null(owner.Source.Data);
        var usage = budget.Usage;
        var error = Assert.Throws<InvalidOperationException>(() => buffer.ExportState(0));
        Assert.Contains("source", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(placement, Assert.Single(buffer.Images));
        Assert.Equal(usage, budget.Usage);
        Assert.True(owner.IsMaterialized);
        Assert.Same(bitmap, owner.GetBitmap(ref work, out _));
        Assert.Equal(SKColors.Red, bitmap.GetPixel(0, 0));
    }

    [Fact]
    public void Latent_materialized_raster_is_not_omitted_after_metrics_grow_and_encoded_source_expires()
    {
        var budget = new NativeSixelBudget();
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 20, budget, new SixelCellSize(10, 20), new SixelSourceBudget(100, 1));
        var parser = new AnsiParser(buffer, false) { ImageDecoder = new SkiaImageDecoder() };
        parser.Process("\x1b[?1049h\x1b[?80h\x1bPq#2!16~-!16~-!16N\x1b\\");
        var image = Assert.Single(buffer.Images);
        var owner = Assert.IsType<NativeSixelImage>(image.ImageHandle);
        int work = 1;
        var bitmap = Assert.IsType<SKBitmap>(owner.GetBitmap(ref work, out _));
        parser.SixelCellSizeOverride = new(10, 1);
        parser.Process("\x1b[2;20r\x1b[S\x1b[S");
        parser.SixelCellSizeOverride = new(10, 20);
        using var pressure = buffer.SixelSources.Retain("q@");
        Assert.Null(owner.Source.Data);
        var crop = image.NativeRaster;
        var charge = budget.Usage;
        Assert.Throws<InvalidOperationException>(() => buffer.ExportState(100));
        Assert.Same(image, Assert.Single(buffer.Images));
        Assert.Equal(crop, image.NativeRaster);
        Assert.Equal(charge, budget.Usage);
        Assert.Equal(SKColors.Red, bitmap.GetPixel(0, 0));
        buffer.ClearImages();
        var retired = new List<object>();
        buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        foreach (var handle in retired) ((IDisposable)handle).Dispose();
        Assert.Equal((0, 0L), budget.Usage);
    }
}
