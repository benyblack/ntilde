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
    public void Overlapping_images_keep_their_drawing_order_across_protocols()
    {
        // Placements are drawn in list order, so the later one covers the earlier. A restore must not
        // regroup them by protocol: an iTerm2 image under a Kitty image stays under it.
        var buffer = new TerminalBuffer(40, 20);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { RetainImageSources = true, RetainSixelSources = true };
        parser.Process("\x1b[H" + ITerm(";width=4", TestImages.PngBase64(8, 8))
            + "\x1b[H\x1bPq#0;2;100;0;0#0!16~\x1b\\"
            + "\x1b[H" + Kitty("a=T,i=1,f=100,c=4,r=2,C=1", TestImages.PngBase64(20, 20))
            + "\x1b[H" + ITerm(";width=2", TestImages.PngBase64(6, 6)));
        Assert.Equal("iterm,sixel,kitty,iterm", Kinds(buffer));

        var (restored, _) = Restore(Export(buffer, parser), decoder: null);

        Assert.Equal("iterm,sixel,kitty,iterm", Kinds(restored));
        Assert.Equal(buffer.Images.Select(i => (i.CellX, i.CellY, i.CellWidth, i.CellHeight)), restored.Images.Select(i => (i.CellX, i.CellY, i.CellWidth, i.CellHeight)));
    }

    [Fact]
    public void A_crafted_raw_size_is_refused_before_any_inflation()
    {
        // A compressed raw image declaring 4000 x 2000 inflates to 32 MB of zeros from ~31 KB. Live
        // transmission refuses that size before inflating; a snapshot must not license it either.
        var (source, sourceParser) = Create(retain: true, decoder: null);
        sourceParser.Process(Traffic);
        TerminalStateSnapshot snapshot = TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(Export(source, sourceParser)));
        TerminalKittyImageState raw = snapshot.KittyImages!.First(i => i.Format == 32);
        (raw.RawWidth, raw.RawHeight, raw.Width, raw.Height, raw.Compressed) = (4000, 2000, 4000, 2000, true);
        raw.Data = Deflate(new byte[4000 * 2000 * 4]);

        var (target, targetParser) = Create(retain: true, decoder: null);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.ThrowsAny<Exception>(() => TerminalStateTransfer.Restore(target, targetParser, snapshot));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 8 * 1024 * 1024, $"Validation allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void Kitty_ids_allocated_after_a_restore_match_the_source()
    {
        var (source, sourceParser) = Create(retain: true, decoder: null);
        string png = TestImages.PngBase64(4, 4);
        sourceParser.Process(Kitty("a=t,I=1,f=100,q=1", png) + Kitty("a=t,I=2,f=100,q=1", png) + Kitty("a=d,d=I,i=1"));
        var (restored, restoredParser) = Restore(Export(source, sourceParser), decoder: null);

        var sourceReplies = new List<string>();
        var restoredReplies = new List<string>();
        sourceParser.OnResponse = sourceReplies.Add;
        restoredParser.OnResponse = restoredReplies.Add;
        string next = Kitty("a=t,I=3,f=100", png);
        sourceParser.Process(next);
        restoredParser.Process(next);
        Assert.Equal(sourceReplies, restoredReplies);
        Assert.Equal("\x1b_Gi=3,I=3;OK\x1b\\", Assert.Single(restoredReplies));
    }

    [Fact]
    public void The_restored_budget_evicts_the_same_image_next()
    {
        // The budget evicts oldest first across protocols: iTerm2 then Kitty means iTerm2 goes first.
        static (TerminalBuffer, AnsiParser) Small()
        {
            var buffer = new TerminalBuffer(40, 20) { ImageSources = new ImageSourceBudget(maxSources: 2) };
            return (buffer, new AnsiParser(buffer, forceConPtyFiltering: false) { RetainImageSources = true });
        }
        var (source, sourceParser) = Small();
        sourceParser.Process(ITerm("", TestImages.PngBase64(4, 4)) + Kitty("a=T,i=1,f=100,q=2", TestImages.PngBase64(5, 5)));
        TerminalStateSnapshot exported = Export(source, sourceParser);

        var (restored, restoredParser) = Small();
        TerminalStateTransfer.Restore(restored, restoredParser, TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(exported)));

        string next = Kitty("a=T,i=2,f=100,q=2", TestImages.PngBase64(6, 6));
        sourceParser.Process(next);
        restoredParser.Process(next);
        TerminalStateSnapshot fromSource = Export(source, sourceParser);
        Assert.Empty(Assert.Single(fromSource.ITermImages!).Data); // the iTerm2 bytes were the oldest; the placement stays
        Assert.Equal(Json(fromSource), Json(Export(restored, restoredParser)));
    }

    [Fact]
    public void Replacing_an_image_frees_its_old_bytes_before_making_room()
    {
        // Two 33-byte images fill a 70-byte budget. Sending id 2 again must replace its bytes, not
        // evict id 1 to make room for a moment.
        var buffer = new TerminalBuffer(40, 20) { ImageSources = new ImageSourceBudget(maxBytes: 70) };
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { RetainImageSources = true };
        string png = TestImages.PngBase64(4, 4);
        parser.Process(Kitty("a=t,i=1,f=100,q=2", png) + Kitty("a=t,i=2,f=100,q=2", png) + Kitty("a=t,i=2,f=100,q=2", png));
        Assert.Equal([1u, 2u], buffer.ExportState(int.MaxValue).KittyImages!.Select(image => image.Id));
        Assert.Equal(66, buffer.ImageSources.RetainedBytes);
    }

    [Fact]
    public void Quota_victims_give_up_their_bytes_before_the_new_image_is_charged()
    {
        // Four 2000 x 2000 images fill the 64 MiB pixel quota and a four-source byte budget. Id 1 is on
        // screen, 2-4 are only transmitted. A fifth image evicts unplaced id 2 from the quota; its bytes
        // must go first, so the budget never drops the visible id 1 to make room.
        byte[] image = [.. TestImages.PngHeader(2000, 2000), .. new byte[967]]; // 1000 bytes
        string payload = Convert.ToBase64String(image);
        var buffer = new TerminalBuffer(40, 20) { ImageSources = new ImageSourceBudget(maxBytes: 4000) };
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { RetainImageSources = true };
        parser.Process(Kitty("a=T,i=1,f=100,q=2,C=1", payload));
        for (int id = 2; id <= 5; id++) parser.Process(Kitty($"a=t,i={id},f=100,q=2", payload));
        Assert.Equal([1u, 3u, 4u, 5u], buffer.ExportState(int.MaxValue).KittyImages!.Select(state => state.Id));
        Assert.Single(buffer.ExportState(int.MaxValue).KittyPlacements!);
    }

    [Fact]
    public void An_image_running_past_the_last_row_keeps_its_hidden_rows()
    {
        // Placed on the last row with C=1, a two-row image runs one row past the screen. Growing the
        // screen shows that row again, so a restore must not clip it away for good.
        var (source, sourceParser) = Create(retain: true, decoder: null);
        // (iTerm2 reserves its rows by writing newlines, which scroll, so it never runs past the end.)
        sourceParser.Process("\x1b[20;1H" + Kitty("a=T,i=1,f=100,c=2,r=2,C=1,q=2", TestImages.PngBase64(8, 8)));
        var (restored, _) = Restore(Export(source, sourceParser), decoder: null);
        Assert.Equal(Placements(source), Placements(restored));
        Assert.Equal(0, Assert.Single(restored.Images).ClipBottomRows);
        source.Resize(40, 25);
        restored.Resize(40, 25);
        Assert.Equal(Placements(source), Placements(restored));
    }

    [Fact]
    public void A_snapshot_object_shares_no_bytes_with_either_buffer()
    {
        var (source, sourceParser) = Create(retain: true, decoder: null);
        sourceParser.Process(Traffic);
        TerminalStateSnapshot first = Export(source, sourceParser);
        string before = Json(first);
        foreach (var image in first.KittyImages!) Array.Clear(image.Data);
        foreach (var image in first.ITermImages!) Array.Clear(image.Data);
        Assert.Equal(before, Json(Export(source, sourceParser))); // the source is untouched

        TerminalStateSnapshot second = Export(source, sourceParser);
        var (restored, restoredParser) = Create(retain: true, decoder: null);
        TerminalStateTransfer.Restore(restored, restoredParser, second);
        foreach (var image in second.KittyImages!) Array.Clear(image.Data);
        foreach (var image in second.ITermImages!) Array.Clear(image.Data);
        Assert.Equal(before, Json(Export(restored, restoredParser))); // so is the destination
    }

    [Fact]
    public void A_placement_past_a_narrowed_screen_comes_back_when_it_widens()
    {
        // The alternate screen keeps a Kitty image at column 30 after narrowing to 10 columns, and shows
        // it again when widened. A snapshot taken while narrow must not drop or move it.
        var (source, sourceParser) = Create(retain: true, decoder: null);
        sourceParser.Process("\x1b[?1049h\x1b[3;31H" + Kitty("a=T,i=1,f=100,c=3,r=2,C=1,q=2", TestImages.PngBase64(8, 8)));
        source.Resize(10, 20);
        Assert.Equal(30, Assert.Single(source.Images).CellX);

        var (restored, _) = Restore(Export(source, sourceParser), decoder: null);
        Assert.Equal(Placements(source), Placements(restored));
        source.Resize(40, 20);
        restored.Resize(40, 20);
        Assert.Equal(Placements(source), Placements(restored));
    }

    [Fact]
    public void Compressed_images_cannot_make_validation_inflate_without_limit()
    {
        // Five compressed PNGs that each inflate to almost 32 MiB: each is within the live bound, but
        // together they ask for 160 MiB of inflation. Validation counts as it streams and refuses the
        // snapshot past 128 MiB, without allocating what it inflates.
        byte[] padded = new byte[(int)(32 * 1024 * 1024) - 64];
        TestImages.PngHeader(4, 4).CopyTo(padded, 0);
        byte[] compressed = Deflate(padded);
        var snapshot = new TerminalBuffer(40, 20).ExportState(int.MaxValue);
        snapshot.KittyImages = Enumerable.Range(1, 5).Select(id => new TerminalKittyImageState
        {
            Id = (uint)id,
            Format = 100,
            Compressed = true,
            Width = 4,
            Height = 4,
            Data = compressed.ToArray(), // separate arrays, as deserialization produces
        }).ToList();

        var (target, targetParser) = Create(retain: true, decoder: null);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var error = Assert.ThrowsAny<Exception>(() => TerminalStateTransfer.Restore(target, targetParser, snapshot));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains("inflate", error.Message, StringComparison.Ordinal);
        Assert.True(allocated < 16 * 1024 * 1024, $"Validation allocated {allocated:N0} bytes.");

        // The limit is on what the images inflate to, not on the work: one array named five times
        // is measured once but still counts five times.
        foreach (var image in snapshot.KittyImages) image.Data = compressed;
        Assert.Contains("inflate", Assert.ThrowsAny<Exception>(() => TerminalStateTransfer.Restore(target, targetParser, snapshot)).Message, StringComparison.Ordinal);

        snapshot.KittyImages.RemoveRange(4, 1); // four fit: 128 MiB of inflation is allowed
        TerminalStateTransfer.Restore(target, targetParser, snapshot);
        Assert.Equal(4, target.ExportState(int.MaxValue).KittyImages!.Count);
    }

    [Fact]
    public void Images_whose_bytes_do_not_fit_are_restored_without_them()
    {
        // The importer's budget holds one source. The others still come back with their ids and cells,
        // undecoded, and stay placeable; only a later snapshot cannot carry them.
        var (source, sourceParser) = Create(retain: true, decoder: null);
        sourceParser.Process(Kitty("a=T,i=1,f=100,C=1", TestImages.PngBase64(8, 8)) + Kitty("a=t,i=2,f=100", TestImages.PngBase64(9, 9))
            + ITerm("", TestImages.PngBase64(10, 10)));
        TerminalStateSnapshot exported = Export(source, sourceParser);

        var restored = new TerminalBuffer(40, 20) { ImageSources = new ImageSourceBudget(maxSources: 1) };
        var restoredParser = new AnsiParser(restored, forceConPtyFiltering: false) { RetainImageSources = true, ImageDecoder = new FixedSizeAwareDecoder() };
        TerminalStateTransfer.Restore(restored, restoredParser, TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(exported)));

        Assert.Equal(Placements(source), Placements(restored));
        var replies = new List<string>();
        restoredParser.OnResponse = replies.Add;
        restoredParser.Process(Kitty("a=p,i=2,C=1"));
        Assert.Equal(["\x1b_Gi=2;OK\x1b\\"], replies);
        Assert.Equal(1, restored.ImageSources.Count);
    }

    [Fact]
    public void Restore_decodes_iterm2_images_only_up_to_the_pixel_cap_newest_first()
    {
        // 2000 x 1000 images are 8 MB of pixels each, so 16 fit under the 128 MiB cap.
        var (source, sourceParser) = Create(retain: true, decoder: null);
        for (int i = 0; i < 20; i++) sourceParser.Process(ITerm(";width=1;height=1", TestImages.PngBase64(2000, 1000)));
        var (restored, _) = Restore(Export(source, sourceParser), new FixedSizeAwareDecoder());

        bool[] decoded = restored.Images.Select(image => image.ImageHandle is not UndecodedImage).ToArray();
        int fit = (int)(TerminalBuffer.MaxTransferredITermPixelBytes / (2000L * 1000 * 4));
        Assert.Equal(20, decoded.Length);
        Assert.Equal(fit, decoded.Count(d => d));
        Assert.All(decoded[^fit..], Assert.True); // the newest
        Assert.All(decoded[..^fit], Assert.False);
    }

    [Fact]
    public void A_compressed_jpeg_with_its_frame_after_large_metadata_transfers()
    {
        // Five 64 KiB APP1 segments put the JPEG frame header past the 256 KiB start validation keeps.
        // Live transmission accepts it, so a snapshot of it must restore.
        var jpeg = new List<byte> { 0xFF, 0xD8 };
        for (int i = 0; i < 5; i++)
        {
            jpeg.AddRange([0xFF, 0xE1, 0xFF, 0xFF]);
            jpeg.AddRange(new byte[0xFFFD]);
        }
        jpeg.AddRange([0xFF, 0xC0, 0x00, 0x11, 8, 0x00, 0x18, 0x00, 0x20, 3, 1, 0x11, 0, 2, 0x11, 1, 3, 0x11, 1]); // 32 x 24
        jpeg.AddRange([0xFF, 0xD9]);
        var (source, sourceParser) = Create(retain: true, decoder: null);
        sourceParser.Process(Kitty("a=T,i=1,f=100,o=z,q=2", Convert.ToBase64String(Deflate(jpeg.ToArray()))));
        Assert.Single(source.Images);

        var (restored, _) = Restore(Export(source, sourceParser), decoder: null);
        Assert.Equal(Placements(source), Placements(restored));
    }

    [Fact]
    public void A_restore_with_a_decoder_inflates_each_compressed_image_at_most_twice()
    {
        // Restore validates, ImportState validates again, then the decoder needs the pixels: measured
        // naively that is three inflations per image. The second validation reuses the first, so four
        // images of almost 32 MiB inflate twice each and stay within the restore-wide bound.
        byte[] padded = new byte[(int)(32 * 1024 * 1024) - 64];
        TestImages.PngHeader(4, 4).CopyTo(padded, 0);
        byte[] compressed = Deflate(padded);
        var snapshot = new TerminalBuffer(40, 20).ExportState(int.MaxValue);
        snapshot.KittyImages = Enumerable.Range(1, 4).Select(id => new TerminalKittyImageState
        {
            Id = (uint)id,
            Format = 100,
            Compressed = true,
            Width = 4,
            Height = 4,
            Data = compressed.ToArray(), // separate arrays, as deserialization produces
        }).ToList();

        var (target, targetParser) = Create(retain: true, new FixedSizeAwareDecoder());
        long before = InflationMeter.Bytes;
        TerminalStateTransfer.Restore(target, targetParser, snapshot);
        long inflated = InflationMeter.Bytes - before;

        Assert.Equal(8L * padded.Length, inflated); // once to validate, once to decode
        Assert.True(inflated <= 2 * TerminalBuffer.MaxSnapshotInflatedBytes);
        Assert.Equal(4, target.ExportState(int.MaxValue).KittyImages!.Count);
    }

    [Fact]
    public void One_array_named_with_equivalent_container_formats_is_measured_once()
    {
        // f=0 and f=100 are both containers. Four images sharing one array and alternating between them
        // are measured once across both validation passes, then each is inflated once to decode.
        byte[] padded = new byte[8 * 1024 * 1024];
        TestImages.PngHeader(4, 4).CopyTo(padded, 0);
        byte[] compressed = Deflate(padded);
        var snapshot = new TerminalBuffer(40, 20).ExportState(int.MaxValue);
        snapshot.KittyImages = Enumerable.Range(1, 4).Select(id => new TerminalKittyImageState
        {
            Id = (uint)id,
            Format = id % 2 == 0 ? 0 : 100,
            Compressed = true,
            Width = 4,
            Height = 4,
            Data = compressed,
        }).ToList();

        var (target, targetParser) = Create(retain: true, new FixedSizeAwareDecoder());
        long before = InflationMeter.Bytes;
        TerminalStateTransfer.Restore(target, targetParser, snapshot);
        Assert.Equal(5L * padded.Length, InflationMeter.Bytes - before); // one measurement, four decodes
    }

    [Fact]
    public void Bytes_larger_than_the_whole_budget_are_not_copied_on_import()
    {
        byte[] large = new byte[4 * 1024 * 1024];
        TestImages.PngHeader(10, 10).CopyTo(large, 0);
        var (source, sourceParser) = Create(retain: true, decoder: null);
        sourceParser.Process(ITerm("", Convert.ToBase64String(large)));
        TerminalStateSnapshot snapshot = TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(Export(source, sourceParser)));

        var restored = new TerminalBuffer(40, 20) { ImageSources = new ImageSourceBudget(maxBytes: 1024 * 1024) };
        var restoredParser = new AnsiParser(restored, forceConPtyFiltering: false) { RetainImageSources = true };
        long before = GC.GetAllocatedBytesForCurrentThread();
        TerminalStateTransfer.Restore(restored, restoredParser, snapshot);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < large.Length, $"Import allocated {allocated:N0} bytes for a {large.Length:N0}-byte image it cannot keep.");
        Assert.IsType<UndecodedImage>(Assert.Single(restored.Images).ImageHandle);
        Assert.Equal(0, restored.ImageSources.RetainedBytes);
    }

    [Fact]
    public void A_placement_shifted_left_of_column_zero_keeps_its_column()
    {
        // Deleting characters shifts a placement left with its row, past column 0 if need be.
        var (source, sourceParser) = Create(retain: true, decoder: null);
        sourceParser.Process("\x1b[1;3H" + Kitty("a=T,i=1,f=100,c=4,r=1,C=1,q=2", TestImages.PngBase64(8, 8)) + "\x1b[1;2H\x1b[3P");
        Assert.True(Assert.Single(source.Images).CellX < 0);
        var (restored, _) = Restore(Export(source, sourceParser), decoder: null);
        Assert.Equal(Placements(source), Placements(restored));
    }

    [Fact]
    public void A_placement_shifted_wholly_off_the_left_edge_stays_invisible()
    {
        var (source, sourceParser) = Create(retain: true, decoder: null);
        sourceParser.Process("\x1b[1;3H" + Kitty("a=T,i=1,f=100,c=2,r=1,C=1,q=2", TestImages.PngBase64(8, 8)) + "\x1b[1;1H\x1b[10P");
        Assert.True(Assert.Single(source.Images).CellX + 2 <= 0);
        var (restored, _) = Restore(Export(source, sourceParser), decoder: null);
        Assert.Equal(Placements(source), Placements(restored));
    }

    private static string Kinds(TerminalBuffer buffer) =>
        string.Join(",", buffer.Images.Select(i => i.IsSixelImage ? "sixel" : i.KittyImageId is not null ? "kitty" : "iterm"));

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
        sourceParser.Process(Kitty("a=d,d=A") + Kitty("a=d,d=I,i=2"));
        // Hard deletion alone gives every Kitty image's bytes back; only the iTerm2 image's remain.
        Assert.Equal(1, source.ImageSources.Count);
        Assert.Equal(TestImages.PngHeader(10, 10).Length, source.ImageSources.RetainedBytes);
        sourceParser.Process("\x1b[2J\x1b[3J");
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
        // Every image is carried, but only the newest three with bytes: the evicted ones keep their identity and cells.
        Assert.Equal([1u, 2u, 3u, 4u, 5u], exported.KittyImages!.Select(image => image.Id));
        Assert.Equal([3u, 4u, 5u], exported.KittyImages!.Where(image => image.Data.Length > 0).Select(image => image.Id));
        Assert.Equal(5, exported.KittyPlacements!.Count);
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
        { "duplicate id", s => s.KittyImages!.Add(s.KittyImages[0]) },
        { "iterm too large", s => (s.ITermImages![0].Data, s.ITermImages[0].PixelWidth, s.ITermImages[0].PixelHeight) = (TestImages.PngHeader(3000, 1), 3000, 1) },
        { "evicted with no size", s => (s.KittyImages![0].Data, s.KittyImages[0].Width) = ([], 0) },
        { "evicted iterm too large", s => (s.ITermImages![0].Data, s.ITermImages[0].PixelWidth) = ([], 3000) },
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

    [Fact]
    public void Placements_keep_their_screen_rows_when_the_importer_keeps_less_history()
    {
        // The importer's scrollback budget can keep fewer history rows than the snapshot carries. Main-screen
        // placements then move up by the rows it dropped, as live eviction moves them: each stays on the
        // screen row it was on, one partly in dropped history keeps its hidden rows, and one wholly in it is gone.
        var buffer = new TerminalBuffer(40, 20);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { RetainImageSources = true, RetainSixelSources = true };
        parser.Process(string.Concat(Enumerable.Range(0, 300).Select(i => $"line {i}\r\n")));
        parser.Process("\x1b[1;1H" + Kitty("a=T,i=1,f=100,c=4,r=4,C=1", TestImages.PngBase64(20, 20))
            + "\x1b[1;20H" + Kitty("a=T,i=2,f=100,c=2,r=1,C=1", TestImages.PngBase64(8, 8))
            + "\x1b[8;3H" + ITerm(";width=4", TestImages.PngBase64(10, 10))
            + "\x1b[14;3H\x1bPq#0;2;100;0;0#0!16~\x1b\\"
            + "\x1b[18;10H" + Kitty("a=T,i=3,f=100,c=3,r=2,C=1", TestImages.PngBase64(12, 12))
            + "\x1b[20;1H\r\n\r\n");
        TerminalStateSnapshot snapshot = Export(buffer, parser);

        var (restored, restoredParser) = Create(retain: true, decoder: null);
        restoredParser.RetainSixelSources = true;
        restored.MaxScrollbackBytes = 1;
        TerminalStateTransfer.Restore(restored, restoredParser, TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(snapshot)));

        Assert.Equal(0, restored.TotalLines - restored.Rows);
        Assert.Equal(OnScreen(buffer), OnScreen(restored));
        int partlyGone = buffer.Images.Single(image => image.KittyImageId == 1).CellY - (buffer.TotalLines - buffer.Rows);
        Assert.True(partlyGone < 0);
        Assert.Contains(restored.Images, image => image.KittyImageId == 1 && image.CellY == partlyGone && image.ClipTopRows == 0);
        Assert.DoesNotContain(restored.Images, image => image.KittyImageId == 2);
    }

    [Fact]
    public void The_source_budget_keeps_compressed_images_within_what_a_restore_inflates()
    {
        // Five compressed PNGs that each inflate to almost 32 MiB are each within the live bound, but a
        // restore refuses a snapshot that inflates past 128 MiB. The source budget counts the same
        // inflation and evicts the oldest, so the snapshot carries four and restores; the restored
        // budget counts them too, so its next snapshot restores as well.
        byte[] padded = new byte[(int)(32 * 1024 * 1024) - 64];
        TestImages.PngHeader(4, 4).CopyTo(padded, 0);
        string payload = Convert.ToBase64String(Deflate(padded));
        var (buffer, parser) = Create(retain: true, decoder: null);
        for (int id = 1; id <= 5; id++) parser.Process(Kitty($"a=t,i={id},f=100,o=z", payload));

        TerminalStateSnapshot snapshot = Export(buffer, parser);
        Assert.Equal(new uint[] { 2, 3, 4, 5 }, snapshot.KittyImages!.Where(image => image.Data.Length > 0).Select(image => image.Id));
        var (restored, restoredParser) = Restore(snapshot, decoder: null);

        restoredParser.Process(Kitty("a=t,i=6,f=100,o=z", payload));
        TerminalStateSnapshot next = Export(restored, restoredParser);
        Assert.Equal(new uint[] { 3, 4, 5, 6 }, next.KittyImages!.Where(image => image.Data.Length > 0).Select(image => image.Id));
        Restore(next, decoder: null);
    }

    [Fact]
    public void Alternate_screen_rows_scrolled_above_the_first_row_come_back_after_a_restore()
    {
        // Scrolling the alternate screen up moves an image partly above the first row without clipping it,
        // and scrolling back down shows it whole again. Those rows are not history that was dropped: a
        // restore in between must not clip them.
        var (source, sourceParser) = Create(retain: true, decoder: null);
        sourceParser.Process("\x1b[?1049h\x1b[H" + Kitty("a=T,i=1,f=100,c=4,r=4,C=1", TestImages.PngBase64(20, 20))
            + "\x1b[1;10H" + ITerm(";width=4;height=4;preserveAspectRatio=0", TestImages.PngBase64(10, 10)) + "\x1b[2S");
        Assert.All(source.Images, image => Assert.Equal(-2, image.CellY));

        var (restored, restoredParser) = Restore(Export(source, sourceParser), decoder: null);
        Assert.Equal(Placements(source), Placements(restored));

        sourceParser.Process("\x1b[2T");
        restoredParser.Process("\x1b[2T");
        Assert.Equal(Placements(source), Placements(restored));
        Assert.All(restored.Images, image => Assert.Equal((0, 0), (image.CellY, image.ClipTopRows)));
    }

    [Fact]
    public void Main_screen_rows_above_the_exported_history_come_back_after_a_restore()
    {
        // Scrolling the main screen up moves an image's top rows into history without clipping them, and
        // scrolling back down shows the image whole again. A snapshot that carries less history leaves
        // those rows above its first row: they are hidden, not gone, and must come back the same way.
        var (source, sourceParser) = Create(retain: true, decoder: null);
        sourceParser.Process("\x1b[H" + Kitty("a=T,i=1,f=100,c=4,r=4,C=1", TestImages.PngBase64(20, 20))
            + "\x1b[1;10H" + ITerm(";width=4;height=4;preserveAspectRatio=0", TestImages.PngBase64(10, 10)) + "\x1b[2S");
        TerminalStateSnapshot snapshot = source.ExportState(0);
        snapshot.Parser = sourceParser.ExportState();

        var (restored, restoredParser) = Restore(snapshot, decoder: null);
        Assert.Equal(OnScreen(source), OnScreen(restored));

        sourceParser.Process("\x1b[2T");
        restoredParser.Process("\x1b[2T");
        Assert.Equal(OnScreen(source), OnScreen(restored));
        Assert.All(restored.Images, image => Assert.Equal((0, 0), (image.CellY, image.ClipTopRows)));
    }

    [Fact]
    public void An_iterm_image_left_in_dropped_history_does_not_evict_a_surviving_image()
    {
        // The importer keeps no history and one source. The newer iTerm2 image's rows are all in history
        // it cannot keep, so it is not restored, and charging its bytes first would evict the older
        // Kitty image's bytes for nothing, losing them from every later snapshot.
        var (source, sourceParser) = Create(retain: true, decoder: null);
        sourceParser.Process(Kitty("a=t,i=1,f=100", TestImages.PngBase64(20, 20))
            + "\x1b[H" + ITerm(";width=4;height=1", TestImages.PngBase64(10, 10))
            + string.Concat(Enumerable.Repeat("\r\n", 25)));
        TerminalStateSnapshot snapshot = Export(source, sourceParser);
        Assert.Single(snapshot.ITermImages!);

        var restored = new TerminalBuffer(40, 20) { ImageSources = new ImageSourceBudget(maxSources: 1), MaxScrollbackBytes = 1 };
        var restoredParser = new AnsiParser(restored, forceConPtyFiltering: false) { RetainImageSources = true };
        TerminalStateTransfer.Restore(restored, restoredParser, TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(snapshot)));

        Assert.Empty(restored.Images);
        TerminalStateSnapshot next = Export(restored, restoredParser);
        Assert.Equal(1u, Assert.Single(next.KittyImages!).Id);
        Assert.Equal(1, restored.ImageSources.Count);
    }

    [Fact]
    public void Images_whose_bytes_were_evicted_keep_their_identity_and_cells_across_a_restore()
    {
        // The source budget can evict an image's bytes while the image lives on, holding its cells and drawing nothing.
        // A snapshot still carries it, without bytes: a side restored without it would not find it when the program
        // places it later, and its cursor would not move where the other sides' does.
        var buffer = new TerminalBuffer(40, 20) { ImageSources = new ImageSourceBudget(maxBytes: 40) }; // one 33-byte PNG
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { RetainImageSources = true };
        parser.Process(Kitty("a=t,i=1,f=100", TestImages.PngBase64(20, 40)) + ITerm(";width=4", TestImages.PngBase64(10, 10))
            + "\r\n" + Kitty("a=t,i=2,f=100", TestImages.PngBase64(8, 8)));
        TerminalStateSnapshot snapshot = Export(buffer, parser);
        Assert.Equal(new uint[] { 1, 2 }, snapshot.KittyImages!.Select(image => image.Id));
        Assert.Empty(snapshot.KittyImages![0].Data);
        Assert.Empty(Assert.Single(snapshot.ITermImages!).Data);

        var (restored, restoredParser) = Restore(snapshot, decoder: null);
        Assert.Equal(Placements(buffer), Placements(restored));

        string place = Kitty("a=p,i=1,c=2,r=2") + "after";
        parser.Process(place);
        restoredParser.Process(place);
        Assert.Equal((buffer.CursorRow, buffer.CursorCol), (restored.CursorRow, restored.CursorCol));
        Assert.Equal(Placements(buffer), Placements(restored));
        Assert.Equal(Json(Export(buffer, parser)), Json(Export(restored, restoredParser)));
    }

    [Fact]
    public void An_export_without_images_copies_none_of_their_bytes()
    {
        // A mux readScreen exports text only (#838): it runs on the session's parse thread, so the image sections are
        // left out before their bytes are copied, not stripped after.
        var (buffer, parser) = Create(retain: true, decoder: null);
        byte[] png = new byte[4 * 1024 * 1024];
        TestImages.PngHeader(4, 4).CopyTo(png, 0);
        parser.Process(Kitty("a=T,i=1,f=100", Convert.ToBase64String(png)));
        Assert.Single(buffer.Images);

        long before = GC.GetAllocatedBytesForCurrentThread();
        TerminalStateSnapshot text = buffer.ExportState(0, includeImages: false);
        long withoutImages = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        TerminalStateSnapshot full = buffer.ExportState(0);
        long withImages = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(text.KittyImages);
        Assert.Null(text.KittyPlacements);
        Assert.Single(full.KittyImages!);
        Assert.True(withImages >= png.Length, $"the full export allocated {withImages:N0} bytes");
        Assert.True(withoutImages < png.Length / 4, $"the text export allocated {withoutImages:N0} bytes");
    }

    /// <summary>Each placement's screen row and visible part; rows above the first history row are clipped.</summary>
    private static string OnScreen(TerminalBuffer buffer)
    {
        int history = buffer.TotalLines - buffer.Rows;
        return string.Join(";", buffer.Images
            .Select(i => (Image: i, Y: i.CellY - history, Top: Math.Max(i.ClipTopRows, history - i.CellY)))
            .Where(e => e.Top + e.Image.ClipBottomRows < e.Image.CellHeight)
            .OrderBy(e => e.Y).ThenBy(e => e.Image.CellX)
            .Select(e => $"{e.Image.IsSixelImage},{e.Image.KittyImageId},{e.Image.CellX},{e.Y},{e.Image.CellWidth}x{e.Image.CellHeight},{e.Top},{e.Image.ClipBottomRows},{e.Image.ClipRightColumns}"));
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
