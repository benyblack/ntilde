using System;
using System.Text;

namespace Ntilde.VT.Tests;

public sealed class SixelPlacementProvenanceTests
{
    private const string Packet = "\x1bPq!16~-!16~-!16N\x1b\\";

    private static (TerminalBuffer Buffer, AnsiParser Parser) Create(IImageDecoder? decoder = null)
    {
        var buffer = new TerminalBuffer(80, 40);
        return (buffer, new AnsiParser(buffer) { ImageDecoder = decoder, RetainSixelSources = true });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reservation_records_creation_inputs_without_claiming_native_fidelity(bool decoderReported)
    {
        var (buffer, parser) = Create(decoderReported ? new FixedSizeImageDecoder(23, 29) : null);
        parser.CellWidth = 8.5f;
        parser.CellHeight = 18.5f;
        parser.SixelCellSizeOverride = new SixelCellSize(17, 37);
        parser.Process("\x1b[?80h" + Packet);
        var image = Assert.Single(buffer.Images);
        var facts = Assert.IsType<SixelPlacementProvenance>(image.SixelPlacement);
        Assert.Equal(decoderReported ? 23 : 16, facts.ReservationWidthPx);
        Assert.Equal(decoderReported ? 29 : 18, facts.ReservationHeightPx);
        Assert.Equal(decoderReported ? SixelReservationDimensionOrigin.ImageDecoderReported : SixelReservationDimensionOrigin.SixelGeometry, facts.DimensionOrigin);
        Assert.Equal((17f, 37f), (facts.CellWidthPx, facts.CellHeightPx));
        Assert.Equal(SixelReservationMetricOrigin.SixelOverride, facts.CellWidthOrigin);
        Assert.Equal(SixelReservationMetricOrigin.SixelOverride, facts.CellHeightOrigin);
        Assert.Equal((decoderReported ? 2 : 1, 1), (image.CellWidth, image.CellHeight));
        // These are reservation facts, not a native 16x16-pixel destination. A later capture
        // may render the cells at9x19, independently of these creation-time17x37 denominators.
        parser.SixelCellSizeOverride = new SixelCellSize(80, 90);
        parser.CellWidth = 40;
        parser.CellHeight = 50;
        Assert.Equal(facts, image.SixelPlacement);
    }

    [Theory]
    [InlineData(false, 0.5f, 0.25f, 1f, 1f, SixelReservationMetricOrigin.ParserMetric, SixelReservationMetricOrigin.ParserMetric)]
    [InlineData(false, float.NaN, 6f, 10f, 6f, SixelReservationMetricOrigin.DefaultFallback, SixelReservationMetricOrigin.ParserMetric)]
    [InlineData(false, 8f, float.PositiveInfinity, 8f, 20f, SixelReservationMetricOrigin.ParserMetric, SixelReservationMetricOrigin.DefaultFallback)]
    [InlineData(true, -1f, 0f, 10f, 20f, SixelReservationMetricOrigin.DefaultFallback, SixelReservationMetricOrigin.DefaultFallback)]
    [InlineData(true, 8f, 6f, 8f, 6f, SixelReservationMetricOrigin.SixelOverride, SixelReservationMetricOrigin.SixelOverride)]
    public void Each_axis_records_the_actual_sanitized_denominator_and_its_origin(bool useOverride,
        float width, float height, float expectedWidth, float expectedHeight,
        SixelReservationMetricOrigin widthOrigin, SixelReservationMetricOrigin heightOrigin)
    {
        var (buffer, parser) = Create();
        parser.CellWidth = width;
        parser.CellHeight = height;
        if (useOverride) parser.SixelCellSizeOverride = new SixelCellSize(width, height);
        parser.Process("\x1b[?80h" + Packet);
        var image = Assert.Single(buffer.Images);
        var facts = Assert.IsType<SixelPlacementProvenance>(image.SixelPlacement);
        Assert.Equal((expectedWidth, expectedHeight), (facts.CellWidthPx, facts.CellHeightPx));
        Assert.Equal((widthOrigin, heightOrigin), (facts.CellWidthOrigin, facts.CellHeightOrigin));
        Assert.Equal(((int)Math.Ceiling(16 / expectedWidth), (int)Math.Ceiling(18 / expectedHeight)), (image.CellWidth, image.CellHeight));
    }

    [Fact]
    public void Float32_division_before_ceiling_is_preserved_at_an_integer_boundary()
    {
        var (buffer, parser) = Create(new FixedSizeImageDecoder(7, 6));
        parser.SixelCellSizeOverride = new SixelCellSize(1.4f, 6);
        parser.Process("\x1b[?80h" + Packet);
        var image = Assert.Single(buffer.Images);
        var facts = Assert.IsType<SixelPlacementProvenance>(image.SixelPlacement);
        Assert.Equal(5, image.CellWidth);
        Assert.Equal(5, (int)Math.Ceiling(facts.ReservationWidthPx / facts.CellWidthPx));
        Assert.Equal(6, (int)Math.Ceiling(facts.ReservationWidthPx / (double)facts.CellWidthPx));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_decoder_keeps_geometry_origin_and_does_not_claim_decoded_dimensions(bool throws)
    {
        var (buffer, parser) = Create(new FailedDecoder(throws));
        parser.Process("\x1b[?80h" + Packet);
        var facts = Assert.IsType<SixelPlacementProvenance>(Assert.Single(buffer.Images).SixelPlacement);
        Assert.Equal((16, 18, SixelReservationDimensionOrigin.SixelGeometry),
            (facts.ReservationWidthPx, facts.ReservationHeightPx, facts.DimensionOrigin));
    }

    [Fact]
    public void Decoder_report_is_clamped_before_recording_and_nonpositive_report_has_no_placement()
    {
        var (buffer, parser) = Create(new FixedSizeImageDecoder(int.MaxValue, int.MaxValue));
        parser.Process("\x1b[?80h" + Packet);
        var facts = Assert.IsType<SixelPlacementProvenance>(Assert.Single(buffer.Images).SixelPlacement);
        Assert.Equal(SixelGeometry.DefaultMaxPixelDimension, facts.ReservationWidthPx);
        Assert.Equal(SixelGeometry.DefaultMaxPixelDimension, facts.ReservationHeightPx);
        var (empty, invalid) = Create(new FixedSizeImageDecoder(0, 0));
        invalid.Process(Packet);
        Assert.Empty(empty.Images);
    }

    [Fact]
    public void Leased_render_copy_keeps_identity_and_creation_facts_after_clear()
    {
        var (buffer, parser) = Create();
        parser.Process("\x1b[?80h" + Packet);
        var image = Assert.Single(buffer.Images);
        using var snapshot = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest { ViewportRows = 40, ViewportCols = 80, Isolated = true }, out _);
        var projected = snapshot.Images.Array[0];
        Assert.Equal(image.ImageId, projected.PlacementId);
        Assert.Equal(image.SixelPlacement, projected.SixelPlacement);
        buffer.ClearImages();
        Assert.Equal(image.ImageId, snapshot.Images.Array[0].PlacementId);
        Assert.Equal(image.SixelPlacement, snapshot.Images.Array[0].SixelPlacement);
    }

    [Fact]
    public void Generic_direct_and_transferred_images_do_not_invent_creation_provenance_or_grow_state()
    {
        Assert.Null(new TerminalImage(new object(), 0, 0, 1, 1).SixelPlacement);
        var direct = new TerminalBuffer(10, 10);
        direct.PlaceSixelImage(new object(), 1, 1);
        Assert.Null(Assert.Single(direct.Images).SixelPlacement);
        var (buffer, parser) = Create();
        parser.Process("\x1b[?80h" + Packet);
        Assert.NotNull(Assert.Single(buffer.Images).SixelPlacement);
        var state = buffer.ExportState(0);
        string serialized = Encoding.UTF8.GetString(TerminalStateSerializer.ToBytes(state));
        Assert.DoesNotContain("provenance", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reservationWidth", serialized, StringComparison.OrdinalIgnoreCase);
        var restored = new TerminalBuffer(80, 40);
        restored.ImportState(state);
        Assert.Null(Assert.Single(restored.Images).SixelPlacement);
    }

    private sealed class FailedDecoder(bool throws) : IImageDecoder
    {
        public object? DecodeSixel(string data, out int width, out int height)
        {
            width = height = 123;
            if (throws) throw new InvalidOperationException("controlled decoder failure");
            return null;
        }
        public object? DecodeImageBytes(byte[] data, out int width, out int height) => DecodeSixel("", out width, out height);
        public object? DecodeRawImage(byte[] data, int bytesPerPixel, int inputWidth, int inputHeight, out int width, out int height) => DecodeSixel("", out width, out height);
    }
}
