using System.Text;
using Ntilde.Replay;

namespace Ntilde.VT.Tests;

public sealed class RectangularEraseTests
{
    /// <summary>Builds distinct viewport rows above retained history, with a nondefault pen and parked cursor.</summary>
    private static (TerminalBuffer Buffer, AnsiParser Parser) Create(bool alternate = false)
    {
        var buffer = new TerminalBuffer(8, 4);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        for (int line = 0; line < 8; line++) parser.Process($"old{line}\r\n");
        if (alternate) parser.Process("\u001b[?1049h");
        for (int row = 0; row < 4; row++) parser.Process($"\u001b[{row + 1};1H" + new string((char)('A' + row), 8));
        parser.Process("\u001b[3;6H\u001b[31;44;1;4m");
        return (buffer, parser);
    }

    /// <summary>Copies all viewport characters under the buffer read lock without visual rendering assumptions.</summary>
    private static string[] Lines(TerminalBuffer buffer)
    {
        buffer.Lock.EnterReadLock();
        try
        {
            return Enumerable.Range(0, buffer.Rows).Select(row => new string(Enumerable.Range(0, buffer.Cols)
                .Select(col => buffer.GetCell(col, row).Character).ToArray())).ToArray();
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    /// <summary>Copies one cell while respecting the same buffer read boundary as rendering and snapshots.</summary>
    private static TerminalCell Cell(TerminalBuffer buffer, int col, int row)
    {
        buffer.Lock.EnterReadLock();
        try { return buffer.GetCell(col, row); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    /// <summary>Pins inclusive one-based borders in both screen banks while preserving cursor, pen, history and the hidden bank.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Erases_only_the_requested_text_rectangle(bool alternate)
    {
        var (buffer, parser) = Create(alternate);
        var before = buffer.ExportState(int.MaxValue);
        parser.Process("\u001b[2;3;3;6$z");
        var after = buffer.ExportState(int.MaxValue);
        Assert.Equal(new[] { "AAAAAAAA", "BB    BB", "CC    CC", "DDDDDDDD" }, Lines(buffer));
        Assert.Equal((before.CursorRow, before.CursorCol, before.IsPendingWrap), (after.CursorRow, after.CursorCol, after.IsPendingWrap));
        Assert.Equal(before.Scrollback.CellsBase64, after.Scrollback.CellsBase64);
        Assert.Equal(alternate ? before.Main.CellsBase64 : before.Alt.CellsBase64,
            alternate ? after.Main.CellsBase64 : after.Alt.CellsBase64);
        Assert.Equal((before.Sgr.IsBold, before.Sgr.IsUnderline, before.Sgr.Foreground, before.Sgr.Background),
            (after.Sgr.IsBold, after.Sgr.IsUnderline, after.Sgr.Foreground, after.Sgr.Background));
    }

    /// <summary>Omitted and explicit-zero borders cover the active page independently of the cursor position.</summary>
    [Theory]
    [InlineData("\u001b[$z")]
    [InlineData("\u001b[0;0;0;0$z")]
    [InlineData("\u001b[;$z")]
    public void Default_borders_erase_the_active_page(string sequence)
    {
        var (buffer, parser) = Create();
        parser.Process(sequence);
        Assert.All(Lines(buffer), line => Assert.Equal("        ", line));
        Assert.Equal((2, 5), (buffer.CursorRow, buffer.CursorCol));
    }

    /// <summary>Oversized coordinates clamp to the page before the rectangle-order check.</summary>
    [Fact]
    public void Oversized_borders_are_clamped()
    {
        var (buffer, parser) = Create();
        parser.Process("\u001b[2;3;99999;99999$z");
        Assert.Equal(new[] { "AAAAAAAA", "BB      ", "CC      ", "DD      " }, Lines(buffer));
    }

    /// <summary>Origin mode offsets rows but page margins do not clip the erasure to the scrolling region.</summary>
    [Theory]
    [InlineData(false, "AAAAAAAA", "  BBBBBB", "CCCCCCCC", "DDDDDDDD")]
    [InlineData(true, "AAAAAAAA", "BBBBBBBB", "  CCCCCC", "  DDDDDD")]
    public void Origin_and_margins_follow_page_coordinates(bool origin, string a, string b, string c, string d)
    {
        var (buffer, parser) = Create();
        parser.Process("\u001b[2;3r" + (origin ? "\u001b[?6h" : ""));
        parser.Process(origin ? "\u001b[2;1;99;2$z" : "\u001b[2;1;2;2$z");
        Assert.Equal(new[] { a, b, c, d }, Lines(buffer));
        Assert.Equal((1, 2), (buffer.ScrollTop, buffer.ScrollBottom));
    }

    /// <summary>Distinct CSI identities, subparameters, excessive arity and reversed bounds cannot erase text.</summary>
    [Theory]
    [InlineData("\u001b[3;1;2;4$z")]
    [InlineData("\u001b[1;5;3;4$z")]
    [InlineData("\u001b[?1;1;4;8$z")]
    [InlineData("\u001b[>1;1;4;8$z")]
    [InlineData("\u001b[1;1;4;8z")]
    [InlineData("\u001b[1;1;4;8$$z")]
    [InlineData("\u001b[1:1;4;8$z")]
    [InlineData("\u001b[1;1;4;8;1$z")]
    public void Other_identities_and_invalid_rectangles_are_ignored(string sequence)
    {
        var (buffer, parser) = Create();
        var before = TerminalStateSerializer.ToBytes(buffer.ExportState(int.MaxValue));
        parser.Process(sequence);
        Assert.Equal(before, TerminalStateSerializer.ToBytes(buffer.ExportState(int.MaxValue)));
    }

    /// <summary>Erased cells lose visual and side-table attributes, use existing erase colors, and leave the live pen intact.</summary>
    [Fact]
    public void Erasure_clears_cell_metadata_without_applying_pen_rendition()
    {
        var (buffer, parser) = Create();
        parser.Process("\u001b[2;2H\u001b]8;;https://example.invalid/decera\a\u001b[7;1;4:3;58:5:5mA\u0301" +
            "\u001b]8;;\a\u001b[31;44;1;4m\u001b[3;6H");
        Assert.True(Cell(buffer, 1, 1).HasExtendedText);
        parser.Process("\u001b[2;2;2;2$z");
        var erased = Cell(buffer, 1, 1);
        Assert.Equal(' ', erased.Character);
        Assert.False(erased.IsBold);
        Assert.False(erased.IsUnderline);
        Assert.False(erased.IsInverse);
        Assert.False(erased.HasExtendedText);
        Assert.Equal(buffer.CurrentForeground, erased.Foreground);
        Assert.Equal(buffer.CurrentBackground, erased.Background);
        Assert.True(buffer.IsBold);
        Assert.True(buffer.IsUnderline);
        buffer.Lock.EnterReadLock();
        try
        {
            var row = buffer.GetRowAbsolute(buffer.Scrollback.Count + 1)!;
            Assert.Null(row.GetExtendedText(1));
            Assert.Null(row.GetHyperlink(1));
            Assert.Null(row.GetUnderline(1));
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    /// <summary>Touching either half of a wide glyph clears the complete glyph while preserving its adjacent cell.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Wide_glyphs_are_not_left_split(int column)
    {
        var buffer = new TerminalBuffer(8, 4);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        parser.Process("界X");
        Assert.True(Cell(buffer, 0, 0).IsWide);
        parser.Process($"\u001b[1;{column};1;{column}$z");
        Assert.Equal(' ', Cell(buffer, 0, 0).Character);
        Assert.Equal(' ', Cell(buffer, 1, 0).Character);
        Assert.False(Cell(buffer, 0, 0).IsWide);
        Assert.False(Cell(buffer, 1, 0).IsWideContinuation);
        Assert.Equal('X', Cell(buffer, 2, 0).Character);
    }

    /// <summary>Both halves of an intersected wide glyph receive the complete erase blank and lose all side metadata in either bank.</summary>
    [Theory]
    [InlineData(1, false, false)]
    [InlineData(2, false, false)]
    [InlineData(1, true, false)]
    [InlineData(2, true, false)]
    [InlineData(1, false, true)]
    [InlineData(2, false, true)]
    [InlineData(1, true, true)]
    [InlineData(2, true, true)]
    public void Wide_erasure_applies_one_blank_to_the_whole_unit(int column, bool alternate, bool defaultForeground)
    {
        var buffer = new TerminalBuffer(8, 4);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        if (alternate) parser.Process("\u001b[?1049h");
        parser.Process("\u001b]8;;https://example.invalid/wide\a\u001b[32;44;1;4:3;53;58:5:5m界\u001b]8;;\aX");
        var adjacent = Cell(buffer, 2, 0);
        parser.Process(defaultForeground ? "\u001b[39;41m" : "\u001b[31;49m");
        parser.Process($"\u001b[1;{column};1;{column}$z");
        for (int col = 0; col < 2; col++)
        {
            var cell = Cell(buffer, col, 0);
            Assert.Equal(new TerminalCell(' ', buffer.CurrentForeground, buffer.CurrentBackground,
                false, false, buffer.IsDefaultForeground, buffer.IsDefaultBackground), cell);
        }
        Assert.Equal(adjacent, Cell(buffer, 2, 0));
        Assert.True(buffer.IsBold);
        Assert.True(buffer.IsUnderline);
        buffer.Lock.EnterReadLock();
        try
        {
            var row = buffer.GetRowAbsolute(alternate ? 0 : buffer.Scrollback.Count)!;
            for (int col = 0; col < 2; col++)
            {
                Assert.Null(row.GetExtendedText(col));
                Assert.Null(row.GetHyperlink(col));
                Assert.Null(row.GetUnderline(col));
            }
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    /// <summary>DECERA reuses the existing OSC66 whole-unit erasure contract rather than retaining a partial scaled glyph.</summary>
    [Fact]
    public void Scaled_text_blocks_are_erased_atomically()
    {
        var buffer = new TerminalBuffer(8, 4);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        parser.Process("\u001b]66;s=2;A\a\u001b[2;2;2;2$z");
        Assert.Null(buffer.ExportState(0).TextSizing);
        Assert.Equal(' ', Cell(buffer, 0, 0).Character);
        Assert.Equal(' ', Cell(buffer, 1, 1).Character);
    }

    /// <summary>A scaled glyph spanning history and viewport remains one unit; erasure keeps the history row but clears its shared anchor.</summary>
    [Fact]
    public void Scaled_text_crossing_the_viewport_boundary_keeps_existing_atomicity()
    {
        var buffer = new TerminalBuffer(8, 3);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        parser.Process("\u001b]66;s=2;A\a\u001b[3;1H\n");
        Assert.Equal(1, buffer.Scrollback.Count);
        Assert.NotNull(buffer.ExportState(int.MaxValue).TextSizing);
        parser.Process("\u001b[1;2;1;2$z");
        Assert.Equal(1, buffer.Scrollback.Count);
        Assert.Null(buffer.ExportState(int.MaxValue).TextSizing);
        buffer.Lock.EnterReadLock();
        try { Assert.Equal(' ', buffer.GetCellAbsolute(0, 0).Character); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    /// <summary>Every split within the admitted command produces the same bounded rectangle.</summary>
    [Fact]
    public void The_command_is_equivalent_across_every_input_split()
    {
        const string command = "\u001b[2;3;3;6$z";
        for (int split = 0; split <= command.Length; split++)
        {
            var (buffer, parser) = Create();
            parser.Process(command[..split]);
            parser.Process(command[split..]);
            Assert.Equal(new[] { "AAAAAAAA", "BB    BB", "CC    CC", "DDDDDDDD" }, Lines(buffer));
        }
    }

    /// <summary>Erasing a soft-wrapped row does not change its continuation flag or move the active cursor.</summary>
    [Fact]
    public void Soft_wrap_flags_survive_rectangular_erasure()
    {
        var buffer = new TerminalBuffer(4, 3);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        parser.Process("ABCDE");
        var before = buffer.ExportState(0);
        Assert.True(before.Main.RowWraps![0]);
        parser.Process("\u001b[1;1;1;4$z");
        var after = buffer.ExportState(0);
        Assert.Equal(before.Main.RowWraps, after.Main.RowWraps);
        Assert.Equal((before.CursorRow, before.CursorCol), (after.CursorRow, after.CursorCol));
    }

    /// <summary>The existing version1 snapshot format preserves an incomplete rectangle command and its screen bank.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Snapshot_JSON_and_parser_continuation_need_no_new_state_format(bool alternate)
    {
        var (live, parser) = Create(alternate);
        parser.Process("\u001b[2;3;");
        var state = live.ExportState(int.MaxValue);
        state.Parser = parser.ExportState();
        var decoded = TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(state));
        var restored = new TerminalBuffer(8, 4);
        restored.ImportState(decoded);
        var restoredParser = new AnsiParser(restored, forceConPtyFiltering: false);
        restoredParser.ImportState(decoded.Parser);
        parser.Process("3;6$z");
        restoredParser.Process("3;6$z");
        Assert.Equal(TerminalStateSnapshot.CurrentVersion, restored.ExportState(0).Version);
        Assert.Equal(TerminalStateSerializer.ToBytes(live.ExportState(int.MaxValue)),
            TerminalStateSerializer.ToBytes(restored.ExportState(int.MaxValue)));
    }

    /// <summary>Actual recording snapshot-and-tail replay preserves the rectangle without changing the recording schema.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Replay")]
    public async Task Recorded_snapshot_and_rectangle_tail_replay_identically(bool alternate)
    {
        string path = Path.GetTempFileName();
        try
        {
            var (live, parser) = Create(alternate);
            const string tail = "\u001b[2;3;3;6$z";
            using (var recorder = new PtyRecorder(path, 8, 4))
            {
                recorder.RecordSnapshot(live, parser);
                byte[] data = Encoding.UTF8.GetBytes(tail);
                recorder.RecordChunkAt(100, data, data.Length);
            }
            parser.Process(tail);
            var replay = new TerminalBuffer(8, 4);
            var replayParser = new AnsiParser(replay, forceConPtyFiltering: false);
            int snapshotHistory = -1;
            await new ReplayRunner(path).RunAsync(
                data => { replayParser.Process(Encoding.UTF8.GetString(data)); return Task.CompletedTask; },
                onSnapshotCallback: snapshot =>
                {
                    ReplayRunner.ApplySnapshot(replay, replayParser, snapshot);
                    snapshotHistory = replay.Scrollback.Count;
                    return Task.CompletedTask;
                }, ct: TestContext.Current.CancellationToken);
            Assert.Equal(Lines(live), Lines(replay));
            Assert.Equal((live.CursorRow, live.CursorCol), (replay.CursorRow, replay.CursorCol));
            // PtyRecorder intentionally omits history from its checkpoints. The
            // full JSON continuation test above separately retains all history.
            Assert.Equal(0, snapshotHistory);
            Assert.Equal(snapshotHistory, replay.Scrollback.Count);
        }
        finally { File.Delete(path); }
    }

    /// <summary>The text-only contract leaves the separately unqualified inline-image plane and ownership untouched.</summary>
    [Fact]
    public void Stored_text_erasure_does_not_dispose_inline_images()
    {
        var (buffer, parser) = Create();
        var image = new TerminalImage(new object(), 1, buffer.Scrollback.Count + 1, 2, 1);
        buffer.AddImage(image);
        parser.Process("\u001b[$z");
        Assert.Same(image, Assert.Single(buffer.Images));
    }
}
