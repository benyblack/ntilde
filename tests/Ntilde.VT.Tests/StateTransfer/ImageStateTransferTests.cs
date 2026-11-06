using System.IO.Compression;
using System.Text.Json;
using Ntilde.VT;

namespace Ntilde.VT.Tests;

/// <summary>
/// Kitty and iTerm2 images in complete state transfer (#838): where the parser retains image
/// sources, a snapshot carries the bytes each image was sent as and its placements, and a restore
/// puts them back at the same cells, decoded by the importer's decoder or kept undecoded without one.
/// </summary>
public sealed class ImageStateTransferTests
{
    private static string Kitty(string controls, string payload = "") => $"\x1b_G{controls};{payload}\x1b\\";
    private static string ITerm(string arguments, string payload) => $"\x1b]1337;File=inline=1{arguments}:{payload}\x07";

    private static readonly string Traffic =
        "first line\r\n"
        + Kitty("a=T,i=1,f=100", TestImages.PngBase64(40, 20)) + "after kitty\r\n"
        + Kitty("a=t,i=2,f=100", TestImages.PngBase64(30, 30)) // transmitted only
        + Kitty("a=T,I=7,f=100,c=3,r=2,C=1", TestImages.PngBase64(16, 16)) + "\r\n\r\n\r\n"
        + Kitty("a=T,i=10,f=32,s=2,v=2,p=5", Convert.ToBase64String(new byte[16])) + "\r\n"
        + Kitty("a=T,i=11,f=100,o=z,C=1", Convert.ToBase64String(Deflate(TestImages.PngHeader(24, 24)))) + "\r\n\r\n"
        + ITerm(";width=4;preserveAspectRatio=0", TestImages.PngBase64(10, 10)) + "tail";

    [Fact]
    public void Retained_images_round_trip_to_the_same_cells_and_stay_usable()
    {
        var (source, sourceParser) = Create(retain: true, new FixedSizeAwareDecoder());
        sourceParser.Process(Traffic);
        TerminalStateSnapshot exported = Export(source, sourceParser);
        Assert.Equal(5, exported.KittyImages!.Count);
        Assert.Equal(4, exported.KittyPlacements!.Count);
        Assert.Single(exported.ITermImages!);

        var (restored, restoredParser) = Restore(exported, new FixedSizeAwareDecoder());

        Assert.Equal(Placements(source), Placements(restored));
        Assert.All(restored.Images, image => Assert.IsNotType<UndecodedImage>(image.ImageHandle));
        Assert.Equal(Json(exported), Json(Export(restored, restoredParser))); // nothing lost or changed

        // The transmitted-only image is still there to place, and deletion still finds its images.
        var replies = new List<string>();
        restoredParser.OnResponse = replies.Add;
        restoredParser.Process(Kitty("a=p,i=2,C=1") + Kitty("a=p,I=7,C=1"));
        Assert.Equal(2, replies.Count);
        Assert.Equal("\x1b_Gi=2;OK\x1b\\", replies[0]);
        Assert.EndsWith(",I=7;OK\x1b\\", replies[1], StringComparison.Ordinal);
        restoredParser.Process(Kitty("a=d,d=I,i=1"));
        Assert.DoesNotContain(restored.Images, image => image.KittyImageId == 1);
    }

    [Fact]
    public void Without_a_decoder_the_restored_images_hold_their_cells_undecoded()
    {
        var (source, sourceParser) = Create(retain: true, decoder: null);
        sourceParser.Process(Traffic);
        var (restored, _) = Restore(Export(source, sourceParser), decoder: null);
        Assert.Equal(Placements(source), Placements(restored));
        Assert.All(restored.Images, image => Assert.IsType<UndecodedImage>(image.ImageHandle));
    }

    [Fact]
    public void A_parser_that_does_not_retain_sources_exports_no_images()
    {
        var (source, sourceParser) = Create(retain: false, new FixedSizeAwareDecoder());
        sourceParser.Process(Traffic);
        TerminalStateSnapshot exported = Export(source, sourceParser);
        Assert.Null(exported.KittyImages);
        Assert.Null(exported.KittyPlacements);
        Assert.Null(exported.ITermImages);
        Assert.NotEmpty(source.Images);
    }

    [Fact]
    public void Alternate_screen_images_round_trip_on_their_screen()
    {
        var (source, sourceParser) = Create(retain: true, decoder: null);
        sourceParser.Process(Kitty("a=T,i=1,f=100", TestImages.PngBase64(20, 20)) + "\x1b[?1049h"
            + Kitty("a=T,i=1,f=100", TestImages.PngBase64(30, 10)) + ITerm("", TestImages.PngBase64(8, 8)));
        var (restored, _) = Restore(Export(source, sourceParser), decoder: null);
        Assert.Equal(Placements(source), Placements(restored));
        Assert.Contains(restored.Images, image => image.IsAltScreenImage && image.KittyImageId == 1);
        Assert.Contains(restored.Images, image => !image.IsAltScreenImage && image.KittyImageId == 1);
    }

    [Fact]
    public void Deleted_and_replaced_images_give_their_bytes_back()
    {
        var (source, sourceParser) = Create(retain: true, decoder: null);
        sourceParser.Process(Traffic);
        Assert.True(source.ImageSources.RetainedBytes > 0);
        sourceParser.Process(Kitty("a=d,d=A") + Kitty("a=d,d=I,i=2") + "\x1b[2J\x1b[3J");
        source.ClearImages();
        Assert.Equal(0, source.ImageSources.RetainedBytes);
        Assert.Equal(0, source.ImageSources.Count);
    }

    [Fact]
    public void Sources_over_the_budget_are_evicted_oldest_first_and_their_images_stay_on_screen()
    {
        var buffer = new TerminalBuffer(40, 20) { ImageSources = new ImageSourceBudget(maxBytes: 100) };
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { RetainImageSources = true };
        string png = TestImages.PngBase64(4, 4); // 33 bytes each
        for (int id = 1; id <= 5; id++) parser.Process(Kitty($"a=T,i={id},f=100,C=1", png));
        Assert.Equal(5, buffer.Images.Count);
        TerminalStateSnapshot exported = buffer.ExportState(int.MaxValue);
        Assert.Equal([3u, 4u, 5u], exported.KittyImages!.Select(image => image.Id));
        Assert.Equal(3, exported.KittyPlacements!.Count);
    }

    [Fact]
    public void An_image_scrolled_out_of_the_exported_history_leaves_its_placement_but_not_its_data()
    {
        var (source, sourceParser) = Create(retain: true, decoder: null);
        sourceParser.Process(Kitty("a=T,i=1,f=100", TestImages.PngBase64(20, 20)) + new string('\n', 60));
        TerminalStateSnapshot exported = source.ExportState(maxScrollbackRows: 5);
        Assert.Single(exported.KittyImages!); // the asset is still there to place again
        Assert.Null(exported.KittyPlacements); // the placement scrolled out of what was exported
    }

    [Fact]
    public void A_snapshot_without_image_sections_imports_as_before()
    {
        var (source, sourceParser) = Create(retain: false, decoder: null);
        sourceParser.Process("plain text");
        byte[] bytes = TerminalStateSerializer.ToBytes(Export(source, sourceParser));
        string json = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("kitty_images", json, StringComparison.Ordinal);
        Assert.DoesNotContain("iterm_images", json, StringComparison.Ordinal);
        var (restored, _) = Restore(TerminalStateSerializer.FromBytes(bytes), decoder: null);
        Assert.Empty(restored.Images);
    }

    public static TheoryData<string, Action<TerminalStateSnapshot>> Corruptions => new()
    {
        { "wrong size", s => s.KittyImages![0].Width++ },
        { "unknown format", s => s.KittyImages![0].Format = 7 },
        { "not an image", s => s.KittyImages![0].Data = "not an image"u8.ToArray() },
        { "raw length", s => s.KittyImages!.First(i => i.Format == 32).RawWidth = 3 },
        { "placement names no image", s => s.KittyPlacements![0].Image = 99 },
        { "placement cell size", s => s.KittyPlacements![0].Width = 0 },
        { "iterm size", s => s.ITermImages![0].PixelHeight = 99 },
        { "iterm data", s => s.ITermImages![0].Data = [1, 2, 3] },
        { "too many images", s => s.KittyImages!.AddRange(Enumerable.Range(0, 130).Select(_ => s.KittyImages![0])) },
    };

    [Theory]
    [MemberData(nameof(Corruptions))]
    public void A_corrupt_image_section_is_refused_before_anything_changes(string name, Action<TerminalStateSnapshot> corrupt)
    {
        var (source, sourceParser) = Create(retain: true, decoder: null);
        sourceParser.Process(Traffic);
        TerminalStateSnapshot snapshot = TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(Export(source, sourceParser)));
        corrupt(snapshot);

        var (target, targetParser) = Create(retain: true, decoder: null);
        targetParser.Process("keep me");
        byte[] before = TerminalStateSerializer.ToBytes(target.ExportState(int.MaxValue));
        Assert.ThrowsAny<Exception>(() => TerminalStateTransfer.Restore(target, targetParser, snapshot));
        Assert.True(before.AsSpan().SequenceEqual(TerminalStateSerializer.ToBytes(target.ExportState(int.MaxValue))), name);
    }

    private static (TerminalBuffer Buffer, AnsiParser Parser) Create(bool retain, IImageDecoder? decoder)
    {
        var buffer = new TerminalBuffer(40, 20);
        return (buffer, new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = decoder, RetainImageSources = retain });
    }

    private static TerminalStateSnapshot Export(TerminalBuffer buffer, AnsiParser parser)
    {
        TerminalStateSnapshot snapshot = buffer.ExportState(int.MaxValue);
        snapshot.Parser = parser.ExportState();
        return snapshot;
    }

    private static (TerminalBuffer Buffer, AnsiParser Parser) Restore(TerminalStateSnapshot snapshot, IImageDecoder? decoder)
    {
        var (buffer, parser) = Create(retain: true, decoder);
        TerminalStateTransfer.Restore(buffer, parser, TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(snapshot)));
        return (buffer, parser);
    }

    private static string Placements(TerminalBuffer buffer) => string.Join(";", buffer.Images
        .OrderBy(i => i.IsAltScreenImage).ThenBy(i => i.CellY).ThenBy(i => i.CellX)
        .Select(i => $"{i.CellX},{i.CellY},{i.CellWidth}x{i.CellHeight},{i.KittyImageId},{i.KittyPlacementId},{i.PreserveAspectRatio},{i.IsAltScreenImage},{i.ClipTopRows},{i.ClipBottomRows},{i.ClipRightColumns}"));

    private static string Json(TerminalStateSnapshot snapshot) => JsonSerializer.Serialize(new { snapshot.KittyImages, snapshot.KittyPlacements, snapshot.ITermImages });

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal)) zlib.Write(data);
        return output.ToArray();
    }

    /// <summary>Decodes to fresh handles at the size the header declares, as a working decoder does.</summary>
    private sealed class FixedSizeAwareDecoder : IImageDecoder
    {
        public object? DecodeImageBytes(byte[] imageData, out int pixelWidth, out int pixelHeight)
        {
            pixelWidth = pixelHeight = 0;
            if (!ImageHeaderProbe.TryGetSize(imageData, out _, out pixelWidth, out pixelHeight)) return null;
            return new object();
        }

        public object? DecodeSixel(string sixelData, out int pixelWidth, out int pixelHeight) { pixelWidth = pixelHeight = 0; return null; }

        public object? DecodeRawImage(byte[] data, int bytesPerPixel, int width, int height, out int pixelWidth, out int pixelHeight)
        {
            (pixelWidth, pixelHeight) = (width, height);
            return new object();
        }
    }
}
