using System.Text;
using System.Text.Json;
using Ntilde.VT;

namespace Ntilde.VT.Tests;

public sealed class SixelRasterFoundationTests
{
    private static SixelRasterIntent Intent() => new(1, 16, 18, new(0.5, 1, 16, 17.5));

    [Fact]
    public void Intent_is_immutable_bounded_and_serializable_without_native_dependencies()
    {
        var state = new TerminalBuffer(10, 3).ExportState(0);
        state.Sixels = [new() { Source = "q~", Width = 2, Height = 1, NativeRaster = Intent() }];
        var bytes = TerminalStateSerializer.ToBytes(state);
        var copy = TerminalStateSerializer.FromBytes(bytes);
        Assert.Equal(Intent(), Assert.Single(copy.Sixels!).NativeRaster);
        Assert.Equal(bytes, TerminalStateSerializer.ToBytes(copy));
        Assert.Equal(0, SixelRasterPolicy.EnabledVersion);
        Assert.All(typeof(SixelRasterIntent).GetProperties(), property => Assert.False(property.CanWrite));
        Assert.All(typeof(SixelSourceCrop).GetProperties(), property => Assert.False(property.CanWrite));
    }

    [Theory]
    [InlineData(0, 16, 18)]
    [InlineData(2, 16, 18)]
    [InlineData(1, 0, 18)]
    [InlineData(1, 16, 0)]
    [InlineData(1, int.MaxValue, 18)]
    [InlineData(1, 16, int.MaxValue)]
    public void Unknown_versions_and_invalid_dimensions_are_rejected(int version, int width, int height)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new SixelRasterIntent(version, width, height, new(0, 0, 1, 1)));

    [Theory]
    [InlineData(double.NaN, 0, 1, 1)]
    [InlineData(0, double.PositiveInfinity, 1, 1)]
    [InlineData(0, 0, double.NegativeInfinity, 1)]
    [InlineData(0, 0, 1, double.NaN)]
    [InlineData(-1, 0, 1, 1)]
    [InlineData(0, -1, 1, 1)]
    [InlineData(2, 0, 1, 1)]
    [InlineData(0, 2, 1, 1)]
    public void Malformed_crop_is_rejected(double left, double top, double right, double bottom)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new SixelSourceCrop(left, top, right, bottom));

    [Fact]
    public void Crop_must_belong_to_the_canvas_but_may_be_empty_or_fractional()
    {
        Assert.Throws<ArgumentNullException>(() => new SixelRasterIntent(1, 16, 18, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SixelRasterIntent(1, 16, 18, new(0, 0, 17, 18)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SixelRasterIntent(1, 16, 18, new(0, 0, 16, 19)));
        Assert.Equal(new SixelSourceCrop(2.5, 3, 2.5, 3), new SixelRasterIntent(1, 16, 18, new(2.5, 3, 2.5, 3)).Crop);
        var limit = SixelGeometry.DefaultMaxPixelDimension;
        Assert.Equal(limit, new SixelRasterIntent(1, limit, limit, new(0, 0, limit, limit)).CanvasWidth);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Disabled_native_state_is_rejected_before_any_buffer_parser_or_decoder_mutation(bool restore, bool outsideBudget)
    {
        var source = new TerminalBuffer(30, 8).ExportState(0);
        source.Sixels = [new() { Source = "q~", Width = 2, Height = 1, NativeRaster = Intent() }];
        if (outsideBudget)
            for (int i = 0; i < SixelSourceBudget.DefaultMaxSources; i++)
                source.Sixels.Add(new() { Source = "q~", Width = 1, Height = 1 });
        var target = new TerminalBuffer(10, 3);
        var parser = new AnsiParser(target) { RetainSixelSources = true };
        parser.Process("keep\x1b[?25l\x1bPq~\x1b\\");
        var decoder = new CountingDecoder();
        parser.ImageDecoder = decoder;
        var images = target.Images.ToArray();
        var before = target.ExportState(0);
        before.Parser = parser.ExportState();
        var bytes = TerminalStateSerializer.ToBytes(before);
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (restore) TerminalStateTransfer.Restore(target, parser, source);
            else target.ImportState(source);
        });
        var after = target.ExportState(0);
        after.Parser = parser.ExportState();
        Assert.Equal(bytes, TerminalStateSerializer.ToBytes(after));
        Assert.Equal(images, target.Images);
        Assert.Equal(0, decoder.Calls);
    }

    [Fact]
    public void Legacy_placement_and_header_bytes_are_exactly_preserved()
    {
        const string placement = "{\"source\":\"q~\",\"x\":1,\"y\":2,\"w\":3,\"h\":4,\"alt\":false,\"top\":0,\"bottom\":0,\"right\":0}";
        Assert.Equal(placement, JsonSerializer.Serialize(new TerminalSixelImageState { Source = "q~", X = 1, Y = 2, Width = 3, Height = 4 }));
        const string header = "{\"type\":\"ntilderec\",\"v\":2,\"cols\":10,\"rows\":3,\"date\":\"fixed\",\"shell\":\"sh\"}";
        Assert.Equal(header, JsonSerializer.Serialize(new ReplayHeader { Type = ReplayHeader.TypeToken, Cols = 10, Rows = 3, Date = "fixed", Shell = "sh" }, ReplayJsonContext.Default.ReplayHeader));
        var legacy = JsonSerializer.Deserialize<TerminalSixelImageState>(placement)!;
        Assert.Null(legacy.NativeRaster);
        Assert.Equal(placement, JsonSerializer.Serialize(legacy));
        var state = new TerminalBuffer(10, 3).ExportState(0);
        Assert.DoesNotContain("native_raster", Encoding.UTF8.GetString(TerminalStateSerializer.ToBytes(state)));
        Assert.Equal(0, JsonSerializer.Deserialize(header, ReplayJsonContext.Default.ReplayHeader)!.RequiredSixelRasterPolicy);
    }

    private sealed class CountingDecoder : IImageDecoder
    {
        internal int Calls { get; private set; }
        public object? DecodeSixel(string data, out int width, out int height)
        {
            Calls++;
            width = height = 0;
            return null;
        }
        public object? DecodeImageBytes(byte[] data, out int width, out int height) => DecodeSixel("", out width, out height);
        public object? DecodeRawImage(byte[] data, int bytesPerPixel, int width, int height, out int pixelWidth, out int pixelHeight)
            => DecodeSixel("", out pixelWidth, out pixelHeight);
    }
}
