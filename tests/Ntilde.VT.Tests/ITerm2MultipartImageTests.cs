using Ntilde.VT;

namespace Ntilde.VT.Tests;

/// <summary>
/// The multipart form of iTerm2 inline images, which <c>imgcat</c> 3.7 sends by default:
/// <c>OSC 1337 ; MultipartFile=...</c>, one <c>OSC 1337 ; FilePart=...</c> per 200 base64 characters,
/// then <c>OSC 1337 ; FileEnd</c>. The assembled payload is placed exactly as the single
/// <c>OSC 1337 ; File=...:payload</c> form would place it.
/// </summary>
public sealed class ITerm2MultipartImageTests
{
    private static string Osc(string body, string st = "\x07") => $"\x1b]1337;{body}{st}";

    private static string Single(string arguments, string base64) => Osc($"File=inline=1{arguments}:{base64}");

    private static string Multipart(string arguments, string base64, int chunk = 200, string st = "\x07") =>
        Osc($"MultipartFile=inline=1{arguments}", st)
        + string.Concat(Chunks(base64, chunk).Select(part => Osc("FilePart=" + part, st)))
        + Osc("FileEnd", st);

    private static IEnumerable<string> Chunks(string text, int size)
    {
        for (int i = 0; i < text.Length; i += size) yield return text.Substring(i, Math.Min(size, text.Length - i));
    }

    /// <summary>A PNG header declaring the size, with a body long enough to need several 200-character parts.</summary>
    private static string Png(int width, int height) => Convert.ToBase64String([.. TestImages.PngHeader(width, height), .. new byte[700]]);

    [Theory]
    [InlineData("", 200)]
    [InlineData(";width=5", 200)]
    [InlineData(";height=50px;preserveAspectRatio=0", 200)]
    [InlineData(";width=20%;height=3", 7)] // parts that do not end on a base64 quantum
    public void The_assembled_payload_is_placed_as_the_single_sequence_form_places_it(string arguments, int chunk)
    {
        string png = Png(30, 40);
        string expected = Run("before\r\n" + Single(arguments, png) + "\nafter");
        string actual = Run("before\r\n" + Multipart(arguments, png, chunk) + "\nafter");
        Assert.Contains("image ", expected, StringComparison.Ordinal);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void What_imgcat_sends_draws_an_image()
    {
        // imgcat 3.7.7's print_image, verbatim in shape: name, size and type in the header, BEL terminators,
        // 200-character parts, and a newline after FileEnd that the placement absorbs.
        string png = Png(64, 32);
        string header = $";size={Convert.FromBase64String(png).Length};name={Convert.ToBase64String("cat.png"u8.ToArray())};type=image/png";
        var (buffer, parser) = Create();
        parser.Process(Multipart(header, png) + "\n$ ");

        TerminalImage image = Assert.Single(buffer.Images);
        Assert.Equal((0, 0), (image.CellX, image.CellY));
        Assert.Equal((10, 2), (image.CellWidth, image.CellHeight)); // 64 x 32 px in 10 x 20 cells, at least 10 wide
        Assert.Equal((2, 2), (buffer.CursorRow, buffer.CursorCol));
    }

    [Fact]
    public void String_terminators_work_as_well_as_bel()
    {
        string png = Png(30, 40);
        Assert.Equal(Run(Single("", png)), Run(Multipart("", png, st: "\x1b\\")));
    }

    [Fact]
    public void Output_between_parts_leaves_the_transfer_pending_and_the_image_lands_at_file_end()
    {
        // The transfer is parser state, not a sequence, so output between parts (tmux redrawing, a
        // shell's job notice) does not cancel it: the image is placed where the cursor is at FileEnd.
        string png = Png(30, 40);
        string[] parts = Chunks(png, 200).ToArray();
        var (buffer, parser) = Create();
        parser.Process(Osc("MultipartFile=inline=1;width=4") + Osc("FilePart=" + parts[0]) + "noise\r\n\x1b[31mred\x1b[m ");
        foreach (string part in parts.Skip(1)) parser.Process(Osc("FilePart=" + part));
        Assert.Empty(buffer.Images);
        parser.Process(Osc("FileEnd"));

        TerminalImage image = Assert.Single(buffer.Images);
        Assert.Equal((4, 1, 4), (image.CellX, image.CellY, image.CellWidth));
    }

    [Fact]
    public void A_payload_over_the_single_sequence_limit_is_refused_and_released()
    {
        string png = Png(30, 40); // 980 base64 characters
        var (buffer, parser) = Create();
        parser.MaxStringSequenceChars = 900;
        parser.Process(Multipart("", png));
        Assert.Empty(buffer.Images);
        Assert.Null(parser.ExportState().ITerm2Multipart);

        // Overflow is latched until FileEnd: the payload is freed at once and no later part revives it.
        parser.Process(Osc("MultipartFile=inline=1") + string.Concat(Chunks(png, 200).Select(p => Osc("FilePart=" + p))));
        ITerm2MultipartState state = Assert.IsType<ITerm2MultipartState>(parser.ExportState().ITerm2Multipart);
        Assert.True(state.Overflow);
        Assert.Equal(string.Empty, state.Payload);
        parser.Process(Osc("FileEnd"));
        Assert.Empty(buffer.Images);

        // The limit is the same as for one File= sequence: what fits there fits here.
        parser.MaxStringSequenceChars = 16 * 1024 * 1024;
        parser.Process(Multipart("", png));
        Assert.Single(buffer.Images);
    }

    [Fact]
    public void A_new_transfer_replaces_one_that_never_ended()
    {
        // An interrupted imgcat never sends FileEnd. The next MultipartFile starts over, so its parts
        // are not appended to the abandoned ones.
        var (buffer, parser) = Create();
        parser.Process(Osc("MultipartFile=inline=1;width=9") + Osc("FilePart=" + Png(500, 500)[..400]));
        parser.Process(Multipart(";width=3", Png(30, 40)));

        TerminalImage image = Assert.Single(buffer.Images);
        Assert.Equal(3, image.CellWidth);
        Assert.Null(parser.ExportState().ITerm2Multipart);
    }

    [Fact]
    public void A_single_sequence_image_during_a_transfer_is_placed_and_leaves_the_transfer_alone()
    {
        string png = Png(30, 40);
        string[] parts = Chunks(png, 200).ToArray();
        var (buffer, parser) = Create();
        parser.Process(Osc("MultipartFile=inline=1;width=4") + Osc("FilePart=" + parts[0]));
        parser.Process(Single(";width=2", TestImages.PngBase64(8, 8)));
        Assert.Single(buffer.Images);
        parser.Process(string.Concat(parts.Skip(1).Select(p => Osc("FilePart=" + p))) + Osc("FileEnd"));
        Assert.Equal([2, 4], buffer.Images.Select(i => i.CellWidth));
    }

    [Fact]
    public void Parts_and_ends_outside_a_transfer_are_ignored()
    {
        var (buffer, parser) = Create();
        parser.Process(Osc("FilePart=" + TestImages.PngBase64(8, 8)) + Osc("FileEnd") + "x");
        Assert.Empty(buffer.Images);
        Assert.Equal((0, 1), (buffer.CursorRow, buffer.CursorCol));

        // A second FileEnd after a finished transfer does not place the image again.
        parser.Process(Multipart("", Png(30, 40)) + Osc("FileEnd"));
        Assert.Single(buffer.Images);
    }

    [Theory]
    [InlineData("!!!!")] // not base64
    [InlineData("AAAA:AAAA")] // a colon, as if a File= payload had been split in two
    [InlineData("A")] // leaves the whole payload one character past a quantum
    public void A_malformed_part_spoils_only_its_own_transfer(string badPart)
    {
        string png = Png(30, 40);
        var (buffer, parser) = Create();
        parser.Process(Osc("MultipartFile=inline=1") + Osc("FilePart=" + png[..200]) + Osc("FilePart=" + badPart)
            + Osc("FilePart=" + png[200..]) + Osc("FileEnd"));
        Assert.Empty(buffer.Images);
        Assert.Null(parser.ExportState().ITerm2Multipart);

        parser.Process(Multipart("", png));
        Assert.Single(buffer.Images);
    }

    [Theory]
    [InlineData("MultipartFile=inline=0")]
    [InlineData("MultipartFile=size=980")]
    public void A_transfer_that_is_not_inline_keeps_nothing(string header)
    {
        // iTerm2 would offer a non-inline file as a download, which Ntilde does not do.
        var (buffer, parser) = Create();
        parser.Process(Osc(header) + Osc("FilePart=" + Png(30, 40)));
        Assert.Null(parser.ExportState().ITerm2Multipart);
        parser.Process(Osc("FileEnd"));
        Assert.Empty(buffer.Images);
    }

    [Fact]
    public void An_overlong_dimension_refuses_the_transfer()
    {
        var (buffer, parser) = Create();
        parser.Process(Multipart(";width=" + new string('1', 65), Png(30, 40)));
        Assert.Empty(buffer.Images);
        Assert.Null(parser.ExportState().ITerm2Multipart);
    }

    [Fact]
    public void Ris_ends_a_transfer()
    {
        string png = Png(30, 40);
        var (buffer, parser) = Create();
        parser.Process(Osc("MultipartFile=inline=1") + Osc("FilePart=" + png[..200]));
        Assert.NotNull(parser.ExportState().ITerm2Multipart);
        parser.Process("\x1b" + "c");
        Assert.Null(parser.ExportState().ITerm2Multipart);
        parser.Process(Osc("FilePart=" + png[200..]) + Osc("FileEnd"));
        Assert.Empty(buffer.Images);
    }

    [Fact]
    public void With_images_disabled_nothing_is_kept()
    {
        var (buffer, parser) = Create();
        parser.AllowNonSixelImages = false;
        parser.Process(Osc("MultipartFile=inline=1") + Osc("FilePart=" + Png(30, 40)));
        Assert.Null(parser.ExportState().ITerm2Multipart);
        parser.Process(Osc("FileEnd"));
        Assert.Empty(buffer.Images);
    }

    [Fact]
    public void Under_image_policy_1_the_form_is_ignored_as_it_was_before_it_was_supported()
    {
        // Policy 1 is what a persistent-session peer from before the multipart form negotiates, and that peer
        // ignores the form: every parser of such a session must do the same. Policy 2 assembles it.
        string png = Png(30, 40);
        string multipart = Multipart(";width=6", png);
        string input = "a" + multipart + "\nb" + Single(";width=2", TestImages.PngBase64(8, 8)) + "c";

        var (legacy, legacyParser) = Create();
        PersistentImagePolicy.Apply(legacyParser, PersistentImagePolicy.RetainedImagesVersion);
        Assert.False(legacyParser.AllowITerm2Multipart);
        legacyParser.Process(input);
        var (stripped, strippedParser) = Create();
        PersistentImagePolicy.Apply(strippedParser, PersistentImagePolicy.RetainedImagesVersion);
        strippedParser.Process(input.Replace(multipart, string.Empty, StringComparison.Ordinal));
        Assert.Equal(Describe(stripped), Describe(legacy));
        Assert.Null(legacyParser.ExportState().ITerm2Multipart);

        var (current, currentParser) = Create();
        PersistentImagePolicy.Apply(currentParser, PersistentImagePolicy.MultipartImagesVersion);
        Assert.True(currentParser.AllowITerm2Multipart);
        currentParser.Process(input);
        Assert.Equal([6, 2], current.Images.Select(i => i.CellWidth));
    }

    [Fact]
    public void A_snapshot_taken_between_any_two_sequences_resumes_the_transfer()
    {
        string png = Png(30, 40);
        string[] sequences =
        [
            "start\r\n",
            Osc("MultipartFile=inline=1;width=6;preserveAspectRatio=0"),
            .. Chunks(png, 200).Select(p => Osc("FilePart=" + p)),
            Osc("FileEnd"),
            "\nend",
        ];
        string expected = Run(string.Concat(sequences));

        for (int cut = 0; cut <= sequences.Length; cut++)
        {
            var (source, sourceParser) = Create();
            sourceParser.Process(string.Concat(sequences.Take(cut)));
            TerminalStateSnapshot snapshot = source.ExportState(int.MaxValue);
            snapshot.Parser = sourceParser.ExportState();

            var (restored, restoredParser) = Create();
            TerminalStateTransfer.Restore(restored, restoredParser, TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(snapshot)));
            Assert.Equal(sourceParser.ExportState().ToDebugString(), restoredParser.ExportState().ToDebugString());
            restoredParser.Process(string.Concat(sequences.Skip(cut)));
            Assert.Equal(expected, Describe(restored));
        }
    }

    [Fact]
    public void Import_holds_a_payload_to_this_parsers_limit()
    {
        var (_, parser) = Create();
        parser.MaxStringSequenceChars = 16;
        var state = new AnsiParserState { ITerm2Multipart = new ITerm2MultipartState { Width = "4", Height = "", Payload = new string('A', 17) } };
        parser.ImportState(state);
        ITerm2MultipartState imported = Assert.IsType<ITerm2MultipartState>(parser.ExportState().ITerm2Multipart);
        Assert.True(imported.Overflow);
        Assert.Equal(string.Empty, imported.Payload);
        Assert.Equal("4", imported.Width);
    }

    [Theory]
    [InlineData(null, "", "")]
    [InlineData("", null, "")]
    [InlineData("", "", null)]
    [InlineData("11111111111111111111111111111111111111111111111111111111111111111", "", "")]
    public void Import_refuses_a_transfer_no_stream_could_produce(string? width, string? height, string? payload)
    {
        var (_, parser) = Create();
        var state = new AnsiParserState { ITerm2Multipart = new ITerm2MultipartState { Width = width!, Height = height!, Payload = payload! } };
        Assert.ThrowsAny<Exception>(() => parser.ImportState(state));
    }

    [Fact]
    public void A_snapshot_without_the_field_has_no_transfer()
    {
        var (_, parser) = Create();
        parser.Process(Osc("MultipartFile=inline=1") + Osc("FilePart=AAAA"));
        parser.ImportState(new AnsiParserState());
        Assert.Null(parser.ExportState().ITerm2Multipart);
    }

    private static string Run(string input)
    {
        var (buffer, parser) = Create();
        parser.Process(input);
        return Describe(buffer);
    }

    private static string Describe(TerminalBuffer buffer) =>
        string.Join("; ", buffer.Images.Select(i => $"image {i.CellX},{i.CellY} {i.CellWidth}x{i.CellHeight} aspect={i.PreserveAspectRatio}"))
        + $" | cursor {buffer.CursorRow},{buffer.CursorCol} | "
        + string.Join("/", Enumerable.Range(0, buffer.Rows).Select(r => RowText(buffer, r).TrimEnd()));

    private static string RowText(TerminalBuffer buffer, int row)
    {
        buffer.Lock.EnterReadLock();
        try { return string.Concat(Enumerable.Range(0, buffer.Cols).Select(col => buffer.GetGrapheme(col, row))); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    private static (TerminalBuffer Buffer, AnsiParser Parser) Create()
    {
        var buffer = new TerminalBuffer(40, 20);
        return (buffer, new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = null, RetainImageSources = true });
    }
}
