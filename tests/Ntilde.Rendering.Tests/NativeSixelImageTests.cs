using Ntilde.VT;
using SkiaSharp;

namespace Ntilde.Rendering.Tests;

public sealed class NativeSixelImageTests
{
    public static TheoryData<string> LegacyCases => new()
    {
        "q~", "q#2~#3@", "q#2;2;100;0;0#2!5~$#3!2@-#4!3B",
        "q#1;1;120;50;100#1~", "q#1;2;200;50;0#1~",
        "q#4095;2;100;0;100#4095~", "q#4096~", "q#9~#9;2;100;0;0",
        "q#2~#2;2;0;100;0", "q!214748364799999999999~", "q!$~", "q!0~@",
        "q#1;;2;3;4~", "q#1;2;3;4;5;6~", "q#2147483648~",
        "q\"1;1;16;16#2!16~-!16~-!16N", "q#2~$#3@-#4B",
    };

    [Theory]
    [MemberData(nameof(LegacyCases))]
    public void Fixed_storage_decoder_preserves_existing_pixels_and_register_semantics(string data)
    {
        using var expected = new SixelDecoder().Decode(data);
        Assert.NotNull(expected);
        var budget = new NativeSixelBudget();
        var (width, height) = SixelGeometry.Measure(data);
        var allocation = Assert.IsType<NativeSixelAllocation>(budget.TryReserve(width, height, data.Length));
        var source = new SixelSourceBudget().Retain(data);
        using (var image = Assert.IsType<NativeSixelImage>(((INativeSixelDecoder)new SkiaImageDecoder()).DecodeNativeSixel(source, allocation)))
        {
            Assert.Equal((1, width * (long)height * 6 + 16384), budget.Usage);
            int work = 1;
            var actual = image.GetBitmap(ref work, out bool pending);
            Assert.False(pending);
            Assert.NotNull(actual);
            Assert.Equal(expected.Width, actual.Width);
            Assert.Equal(expected.Height, actual.Height);
            Assert.Equal(width * sizeof(uint), actual.RowBytes);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++) Assert.Equal(expected.GetPixel(x, y), actual.GetPixel(x, y));
            Assert.Equal((1, width * (long)height * 4), budget.Usage);
        }
        Assert.Equal((0, 0L), budget.Usage);
    }

    [Fact]
    public void Deferred_work_holds_scratch_until_decode_and_pixels_until_actual_disposal()
    {
        const string data = "q#2!16~-!16~-!16N";
        var budget = new NativeSixelBudget();
        var allocation = Assert.IsType<NativeSixelAllocation>(budget.TryReserve(16, 18, data.Length));
        var source = new SixelSourceBudget().Retain(data);
        var image = Assert.IsType<NativeSixelImage>(((INativeSixelDecoder)new SkiaImageDecoder()).DecodeNativeSixel(source, allocation));
        int work = 0;
        Assert.Null(image.GetBitmap(ref work, out bool pending));
        Assert.True(pending);
        Assert.True(image.NeedsSource);
        Assert.Equal((1, 16L * 18 * 6 + 16384), budget.Usage);
        work = 1;
        var bitmap = image.GetBitmap(ref work, out pending);
        Assert.NotNull(bitmap);
        Assert.False(pending);
        Assert.False(image.NeedsSource);
        Assert.Equal(SKColors.Red, bitmap.GetPixel(15, 15));
        Assert.Equal(0, bitmap.GetPixel(15, 16).Alpha);
        Assert.Equal((1, 16L * 18 * 4), budget.Usage);
        Assert.Same(bitmap, image.GetBitmap(ref work, out _));
        Assert.Equal(0, work);
        image.Dispose();
        image.Dispose();
        Assert.Equal((0, 0L), budget.Usage);
        Assert.Null(image.GetBitmap(ref work, out _));
    }

    [Fact]
    public void Retired_snapshot_keeps_real_pixels_and_charge_until_it_can_be_disposed()
    {
        var budget = new NativeSixelBudget(maxPlacements: 1);
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 5, budget);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = new SkiaImageDecoder() };
        parser.Process("\x1b[?80h\x1bPq#2~\x1b\\");
        var image = Assert.IsType<NativeSixelImage>(Assert.Single(buffer.Images).ImageHandle);
        int work = 1;
        var bitmap = image.GetBitmap(ref work, out _);
        Assert.NotNull(bitmap);
        int snapshot = buffer.BeginSnapshotSession();
        parser.Process("\u001bc");
        var retired = new List<object>();
        buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Empty(retired);
        Assert.Equal(SKColors.Red, bitmap.GetPixel(0, 0));
        Assert.Equal((1, 24L), budget.Usage);
        parser.Process("\x1bPq~\x1b\\");
        Assert.Empty(buffer.Images);
        buffer.EndSnapshotSession(snapshot);
        buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Same(image, Assert.Single(retired));
        Assert.Equal((1, 24L), budget.Usage); // Drain is not disposal.
        image.Dispose();
        Assert.Equal((0, 0L), budget.Usage);
        parser.Process("\x1bPq~\x1b\\");
        Assert.Single(buffer.Images);
        buffer.Clear(true);
        retired.Clear();
        buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        foreach (var handle in retired) ((IDisposable)handle).Dispose();
        Assert.Equal((0, 0L), budget.Usage);
    }
}
