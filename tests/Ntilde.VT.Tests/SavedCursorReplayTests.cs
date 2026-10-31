using System.Text;
using Ntilde.Replay;

namespace Ntilde.VT.Tests;

public sealed class SavedCursorReplayTests
{
    private static ReplaySnapshot StateSnapshot(int cols, int rows) => new()
    {
        Cols = cols,
        Rows = rows,
        State = new TerminalBuffer(cols, rows).ExportState(0)
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Replay")]
    public async Task Snapshot_and_tail_restore_the_active_saved_cursor_bank(bool alternate)
    {
        string path = Path.GetTempFileName();
        try
        {
            var live = new TerminalBuffer(10, 8);
            var parser = new AnsiParser(live);
            if (alternate) parser.Process("\u001b[?47h");
            parser.Process("\u001b[3;10H\u001b[31;1;4:3;58:5:2mX\u001b(0\u001b[?1048h\u001b(B\u001b[1;1H\u001b[0m");
            const string tail = "\u001b[?1048lq";
            using (var recorder = new PtyRecorder(path, 10, 8))
            {
                recorder.RecordSnapshot(live, parser);
                byte[] bytes = Encoding.UTF8.GetBytes(tail);
                recorder.RecordChunkAt(100, bytes, bytes.Length);
            }
            parser.Process(tail);
            var restored = new TerminalBuffer(10, 8);
            var restoredParser = new AnsiParser(restored);
            bool applied = false;
            await new ReplayRunner(path).RunAsync(
                data => { restoredParser.Process(Encoding.UTF8.GetString(data)); return Task.CompletedTask; },
                onSnapshotCallback: snapshot =>
                {
                    Assert.NotNull(snapshot.State);
                    Assert.NotNull(snapshot.State.SavedCursorMain);
                    Assert.NotNull(snapshot.State.SavedCursorAlt);
                    ReplayRunner.ApplySnapshot(restored, restoredParser, snapshot);
                    applied = true;
                    return Task.CompletedTask;
                }, ct: TestContext.Current.CancellationToken);
            Assert.True(applied);
            Assert.Equal(alternate, restored.IsAltScreenActive);
            Assert.Equal((live.CursorRow, live.CursorCol), (restored.CursorRow, restored.CursorCol));
            Assert.Equal(BufferSnapshot.Capture(live).ToFormattedString(), BufferSnapshot.Capture(restored).ToFormattedString());
            Assert.True(restored.IsBold);
            Assert.Equal(UnderlineStyle.Curly, restored.UnderlineStyle);
            Assert.Equal((short)2, restored.UnderlineColorIndex);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("\u001b[31;", "1mX")]
    [InlineData("\u001b]2;", "title\u0007X")]
    [InlineData("\u001bP$q", "m\u001b\\X")]
    [InlineData("\u001b(", "0q")]
    [Trait("Category", "Replay")]
    public async Task Buffer_only_snapshot_preserves_parser_continuation_from_preceding_data(string prefix, string tail)
    {
        string path = Path.GetTempFileName();
        try
        {
            var live = new TerminalBuffer(20, 8);
            var parser = new AnsiParser(live);
            parser.Process(prefix);
            using (var recorder = new PtyRecorder(path, 20, 8))
            {
                byte[] before = Encoding.UTF8.GetBytes(prefix);
                recorder.RecordChunkAt(0, before, before.Length);
                recorder.RecordSnapshot(live);
                byte[] after = Encoding.UTF8.GetBytes(tail);
                recorder.RecordChunkAt(100, after, after.Length);
            }
            parser.Process(tail);
            var restored = new TerminalBuffer(20, 8);
            var restoredParser = new AnsiParser(restored);
            bool legacy = false;
            await new ReplayRunner(path).RunAsync(
                data => { restoredParser.Process(Encoding.UTF8.GetString(data)); return Task.CompletedTask; },
                onSnapshotCallback: snapshot =>
                {
                    legacy = snapshot.State is null;
                    ReplayRunner.ApplySnapshot(restored, restoredParser, snapshot);
                    return Task.CompletedTask;
                }, ct: TestContext.Current.CancellationToken);
            Assert.Equal(BufferSnapshot.Capture(live).ToFormattedString(), BufferSnapshot.Capture(restored).ToFormattedString());
            Assert.Equal(parser.ExportState().ToDebugString(), restoredParser.ExportState().ToDebugString());
            Assert.Equal(live.IsBold, restored.IsBold);
            Assert.Equal(live.CurrentFgIndex, restored.CurrentFgIndex);
            Assert.True(legacy);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Legacy_snapshot_uses_default_banks_instead_of_a_previous_seek_saved_cursor()
    {
        var buffer = new TerminalBuffer(20, 5);
        var parser = new AnsiParser(buffer);
        parser.Process("\u001b[4;5H\u001b[31;1m\u001b[?1048h");
        ReplayRunner.ApplySnapshot(buffer, parser, new ReplaySnapshot { Cols = 20, Rows = 5, CursorCol = 7, CursorRow = 2 });
        parser.Process("\u001b[?1048l");
        Assert.Equal((0, 0), (buffer.CursorRow, buffer.CursorCol));
        Assert.False(buffer.IsBold);
        Assert.True(buffer.IsDefaultForeground);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Invalid_saved_cursor_is_rejected_before_buffer_or_geometry_changes(bool alternate)
    {
        var buffer = new TerminalBuffer(20, 5);
        var parser = new AnsiParser(buffer);
        parser.Process("preserved\u001b[2;3H\u001b[?1048h");
        var before = BufferSnapshot.Capture(buffer).ToFormattedString();
        var snapshot = StateSnapshot(10, 3);
        var invalid = new TerminalCursorState { Sgr = new TerminalSgrState { UnderlineStyle = 99 } };
        if (alternate) snapshot.State!.SavedCursorAlt = invalid;
        else snapshot.State!.SavedCursorMain = invalid;
        Assert.Throws<InvalidOperationException>(() => ReplayRunner.ApplySnapshot(buffer, parser, snapshot));
        Assert.Equal((20, 5), (buffer.Cols, buffer.Rows));
        Assert.Equal(before, BufferSnapshot.Capture(buffer).ToFormattedString());
        parser.Process("\u001b[1;1H\u001b[?1048l");
        Assert.Equal((1, 2), (buffer.CursorRow, buffer.CursorCol));
    }

    [Fact]
    public void Exported_saved_cursors_are_detached_copies()
    {
        var buffer = new TerminalBuffer(20, 5);
        var parser = new AnsiParser(buffer);
        parser.Process("\u001b[2;3H\u001b[1m\u001b[?1048h");
        var saved = buffer.ExportState(0);
        saved.SavedCursorMain.Row = 4;
        saved.SavedCursorMain.Sgr.IsBold = false;
        Assert.NotNull(saved.SavedCursorMain.Charsets);
        saved.SavedCursorMain.Charsets[0] = 1;
        parser.Process("\u001b[1;1H\u001b[0m\u001b[?1048l");
        Assert.Equal((1, 2), (buffer.CursorRow, buffer.CursorCol));
        Assert.True(buffer.IsBold);
        parser.Process("q");
        Assert.Equal(0, parser.ExportState().Charsets[0]);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Invalid_saved_charset_context_is_rejected_by_replay_and_state_transfer_before_mutation(int corruption)
    {
        var buffer = new TerminalBuffer(20, 5);
        var parser = new AnsiParser(buffer);
        parser.Process("preserved");
        var invalid = new TerminalCursorState { Charsets = [0, 0, 0, 0] };
        if (corruption == 0) invalid.Charsets = [0, 0, 0];
        else if (corruption == 1) invalid.Charsets[0] = 99;
        else invalid.Gl = 4;
        var replay = StateSnapshot(10, 3);
        replay.State!.SavedCursorMain = invalid;
        var replayError = Assert.Throws<InvalidOperationException>(() => ReplayRunner.ApplySnapshot(buffer, parser, replay));
        Assert.Contains("saved cursor", replayError.Message);
        var state = new TerminalBuffer(10, 3).ExportState(0);
        state.SavedCursorMain = invalid;
        state.Parser = parser.ExportState();
        var transferError = Assert.Throws<InvalidOperationException>(() => TerminalStateTransfer.Restore(buffer, parser, state));
        Assert.Contains("saved cursor", transferError.Message);
        Assert.Equal((20, 5), (buffer.Cols, buffer.Rows));
        Assert.Equal("preserved", BufferSnapshot.Capture(buffer).Lines[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Replay")]
    public async Task Snapshot_round_trip_restores_saved_origin_and_charsets_but_keeps_current_autowrap(bool alternate)
    {
        string path = Path.GetTempFileName();
        try
        {
            var live = new TerminalBuffer(20, 8);
            var parser = new AnsiParser(live);
            if (alternate) parser.Process("\u001b[?47h");
            parser.Process("\u001b[?6h\u001b[?7l\u001b(0\u001b[3;4H\u001b[?1048h" +
                "\u001b[?6l\u001b[?7h\u001b(B\u001b[1;1H");
            using (var recorder = new PtyRecorder(path, 20, 8)) recorder.RecordSnapshot(live, parser);
            var restored = new TerminalBuffer(20, 8);
            var restoredParser = new AnsiParser(restored);
            await new ReplayRunner(path).RunAsync(_ => Task.CompletedTask,
                onSnapshotCallback: snapshot => { ReplayRunner.ApplySnapshot(restored, restoredParser, snapshot); return Task.CompletedTask; },
                ct: TestContext.Current.CancellationToken);
            restoredParser.Process("\u001b[?1048lq");
            Assert.True(restored.Modes.IsOriginMode);
            Assert.True(restored.Modes.IsAutoWrapMode);
            Assert.Equal((2, 4), (restored.CursorRow, restored.CursorCol));
            Assert.Contains('─', BufferSnapshot.Capture(restored).Lines[2]);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Replay")]
    public async Task A_snapshot_after_restore_retains_current_parser_context_for_later_data(bool partialCsi)
    {
        string path = Path.GetTempFileName();
        try
        {
            var live = new TerminalBuffer(20, 8);
            var parser = new AnsiParser(live) { SixelCellSizeOverride = new SixelCellSize(10, 20) };
            parser.Process("\u001b(0\u001b[3;4H\u001b[?1048h\u001b(B\u001b[?1048l");
            if (partialCsi) parser.Process("\u001b[31;");
            string tail = partialCsi ? "1mq" : "q";
            using (var recorder = new PtyRecorder(path, 20, 8)) recorder.RecordSnapshot(live, parser);
            parser.Process(tail);
            var restored = new TerminalBuffer(20, 8);
            var restoredParser = new AnsiParser(restored);
            await new ReplayRunner(path).RunAsync(_ => Task.CompletedTask,
                onSnapshotCallback: snapshot => { ReplayRunner.ApplySnapshot(restored, restoredParser, snapshot); return Task.CompletedTask; },
                ct: TestContext.Current.CancellationToken);
            restoredParser.Process(tail);
            Assert.Equal(BufferSnapshot.Capture(live).ToFormattedString(), BufferSnapshot.Capture(restored).ToFormattedString());
            Assert.Equal(parser.ExportState().ToDebugString(), restoredParser.ExportState().ToDebugString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Invalid_parser_snapshot_is_rejected_before_buffer_resize_or_mutation()
    {
        var buffer = new TerminalBuffer(20, 5);
        var parser = new AnsiParser(buffer);
        parser.Process("preserved");
        var snapshot = StateSnapshot(10, 3);
        snapshot.State!.Parser = new AnsiParserState { State = 999 };
        var ex = Assert.Throws<InvalidOperationException>(() => ReplayRunner.ApplySnapshot(buffer, parser, snapshot));
        Assert.Contains("parser state", ex.Message);
        Assert.Equal((20, 5), (buffer.Cols, buffer.Rows));
        Assert.Equal("preserved", BufferSnapshot.Capture(buffer).Lines[0]);
    }

    [Theory]
    [InlineData(true, -2)]
    [InlineData(true, 256)]
    [InlineData(false, -2)]
    [InlineData(false, 256)]
    public void Saved_palette_indices_are_rejected_before_replay_mutation(bool foreground, short index)
    {
        var buffer = new TerminalBuffer(20, 5);
        var parser = new AnsiParser(buffer);
        parser.Process("preserved");
        var snapshot = StateSnapshot(10, 3);
        var state = snapshot.State!;
        if (foreground) state.SavedCursorMain.Sgr.FgIndex = index;
        else state.SavedCursorMain.Sgr.BgIndex = index;
        var ex = Assert.Throws<InvalidOperationException>(() => ReplayRunner.ApplySnapshot(buffer, parser, snapshot));
        Assert.Contains(foreground ? "foreground palette" : "background palette", ex.Message);
        Assert.Equal((20, 5), (buffer.Cols, buffer.Rows));
        Assert.Equal("preserved", BufferSnapshot.Capture(buffer).Lines[0]);
    }

    [Fact]
    [Trait("Category", "Replay")]
    public async Task Snapshot_inside_1049_restores_the_armed_main_cursor_its_context_and_both_screens()
    {
        string path = Path.GetTempFileName();
        try
        {
            var live = new TerminalBuffer(20, 8);
            var parser = new AnsiParser(live);
            parser.Process("main\u001b[?6h\u001b[?7l\u001b(0\u001b[3;4H\u001b[31;1m" +
                "\u001b[?1049h\u001b[?6l\u001b[?7h\u001b(Balt");
            using (var recorder = new PtyRecorder(path, 20, 8)) recorder.RecordSnapshot(live, parser);
            const string tail = "\u001b[?1049lq";
            parser.Process(tail);
            var restored = new TerminalBuffer(20, 8);
            var restoredParser = new AnsiParser(restored);
            await new ReplayRunner(path).RunAsync(_ => Task.CompletedTask,
                onSnapshotCallback: snapshot => { ReplayRunner.ApplySnapshot(restored, restoredParser, snapshot); return Task.CompletedTask; },
                ct: TestContext.Current.CancellationToken);
            restoredParser.Process(tail);
            Assert.False(restored.IsAltScreenActive);
            Assert.True(restored.Modes.IsOriginMode);
            Assert.True(restored.Modes.IsAutoWrapMode);
            Assert.True(restored.IsBold);
            Assert.Equal(BufferSnapshot.Capture(live).ToFormattedString(), BufferSnapshot.Capture(restored).ToFormattedString());
            Assert.StartsWith("main", BufferSnapshot.Capture(restored).Lines[0]);
            restoredParser.Process("\u001b[?47h");
            Assert.StartsWith("alt", BufferSnapshot.Capture(restored).Lines[0]);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(10f, 20f)]
    public void Complete_snapshot_keeps_parser_SIXEL_metrics_over_outer_fallback(float? width, float? height)
    {
        var source = new TerminalBuffer(10, 5);
        var parser = new AnsiParser(source) { SixelCellSizeOverride = new SixelCellSize(20, 6) };
        parser.Process("\u001b[?80h\u001b[4;3H\u001bPq!30~");
        var state = source.ExportState(0);
        state.Parser = parser.ExportState();
        var snapshot = new ReplaySnapshot { State = state, Cols = 10, Rows = 5, SixelCellWidthPx = width, SixelCellHeightPx = height };
        var restored = new TerminalBuffer(10, 5);
        var resumed = new AnsiParser(restored);
        ReplayRunner.ApplySnapshot(restored, resumed, snapshot);
        Assert.Equal(new SixelCellSize(20, 6), resumed.SixelCellSizeOverride);
        resumed.Process("-~\u001b\\\u001b[?80l\u001bPq!30~-~\u001b\\");
        Assert.Equal((0, 4), (restored.CursorCol, restored.CursorRow));
        Assert.Equal(6, restored.TotalLines);
    }

    [Fact]
    public void Complete_snapshot_without_parser_SIXEL_metrics_uses_outer_fallback()
    {
        var snapshot = StateSnapshot(10, 5);
        snapshot.SixelCellWidthPx = 20;
        snapshot.SixelCellHeightPx = 6;
        var buffer = new TerminalBuffer(10, 5);
        var parser = new AnsiParser(buffer);
        ReplayRunner.ApplySnapshot(buffer, parser, snapshot);
        Assert.Equal(new SixelCellSize(20, 6), parser.SixelCellSizeOverride);
    }

    [Theory]
    [InlineData(true, -2)]
    [InlineData(false, -2)]
    [InlineData(true, 256)]
    [InlineData(false, 256)]
    public void Invalid_live_palette_is_rejected_before_resize_or_mutation(bool foreground, short index)
    {
        var buffer = new TerminalBuffer(20, 5);
        var parser = new AnsiParser(buffer);
        parser.Process("preserved");
        var snapshot = StateSnapshot(10, 3);
        if (foreground) snapshot.State!.Sgr.FgIndex = index;
        else snapshot.State!.Sgr.BgIndex = index;
        var error = Assert.Throws<InvalidOperationException>(() => ReplayRunner.ApplySnapshot(buffer, parser, snapshot));
        Assert.Contains("palette", error.Message);
        Assert.Equal((20, 5), (buffer.Cols, buffer.Rows));
        Assert.Equal("preserved", BufferSnapshot.Capture(buffer).Lines[0]);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(-1, 3)]
    [InlineData(10, 0)]
    [InlineData(10, -1)]
    public void Invalid_legacy_geometry_with_extended_text_is_rejected_before_mutation(int cols, int rows)
    {
        var buffer = new TerminalBuffer(20, 5);
        var parser = new AnsiParser(buffer);
        parser.Process("preserved");
        var snapshot = new ReplaySnapshot { Cols = cols, Rows = rows, ExtendedText = new() { [0] = "wide" } };
        var error = Assert.Throws<InvalidOperationException>(() => ReplayRunner.ApplySnapshot(buffer, parser, snapshot));
        Assert.Contains("geometry", error.Message);
        Assert.Equal((20, 5), (buffer.Cols, buffer.Rows));
        Assert.Equal("preserved", BufferSnapshot.Capture(buffer).Lines[0]);
    }

    [Fact]
    public void Complete_snapshot_without_a_parser_is_rejected_before_mutation()
    {
        var buffer = new TerminalBuffer(20, 5);
        var parser = new AnsiParser(buffer);
        parser.Process("preserved\u001b[31;");
        string before = parser.ExportState().ToDebugString();
        var snapshot = StateSnapshot(10, 3);
        snapshot.State!.Parser = null!; // malformed JSON can explicitly null the required parser half
        var error = Assert.Throws<InvalidOperationException>(() => ReplayRunner.ApplySnapshot(buffer, parser, snapshot));
        Assert.Contains("parser", error.Message);
        Assert.Equal((20, 5), (buffer.Cols, buffer.Rows));
        Assert.Equal("preserved", BufferSnapshot.Capture(buffer).Lines[0]);
        Assert.Equal(before, parser.ExportState().ToDebugString());
    }

    [Fact]
    public void Unknown_optional_state_versions_use_the_legacy_viewport_fields()
    {
        var buffer = new TerminalBuffer(20, 5);
        var parser = new AnsiParser(buffer);
        parser.Process("preserved\u001b[6 q");
        var snapshot = StateSnapshot(10, 3);
        snapshot.Cols = 20;
        snapshot.Rows = 5;
        snapshot.CursorRow = 2;
        snapshot.CursorCol = 7;
        snapshot.State!.Version = int.MaxValue;
        snapshot.State.Parser = new AnsiParserState { State = 999 };
        ReplayRunner.ApplySnapshot(buffer, parser, snapshot);
        Assert.Equal((20, 5), (buffer.Cols, buffer.Rows));
        Assert.Equal((2, 7), (buffer.CursorRow, buffer.CursorCol));
        Assert.Equal("preserved", BufferSnapshot.Capture(buffer).Lines[0]);
        Assert.Equal(0, parser.ExportState().State);
        Assert.True(buffer.Modes.CursorStyleIsDefault);
    }

}
