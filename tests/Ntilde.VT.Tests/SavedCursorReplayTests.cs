using System.Text;
using Ntilde.Replay;

namespace Ntilde.VT.Tests;

public sealed class SavedCursorReplayTests
{
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
                    Assert.NotNull(snapshot.SavedCursorMain);
                    Assert.NotNull(snapshot.SavedCursorAlt);
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
        var snapshot = new ReplaySnapshot { Cols = 10, Rows = 3, CursorRow = 1 };
        var invalid = new TerminalCursorState { Sgr = new TerminalSgrState { UnderlineStyle = 99 } };
        if (alternate) snapshot.SavedCursorAlt = invalid;
        else snapshot.SavedCursorMain = invalid;
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
        var saved = buffer.ExportSavedCursors();
        saved.Main.Row = 4;
        saved.Main.Sgr.IsBold = false;
        Assert.NotNull(saved.Main.Charsets);
        saved.Main.Charsets[0] = 1;
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
        var replay = new ReplaySnapshot { Cols = 10, Rows = 3, SavedCursorMain = invalid };
        var replayError = Assert.Throws<InvalidOperationException>(() => ReplayRunner.ApplySnapshot(buffer, parser, replay));
        Assert.Contains("saved cursor", replayError.Message);
        var state = buffer.ExportState(0);
        state.Cols = 10;
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
    public async Task Snapshot_round_trip_keeps_origin_wrap_and_character_set_saved_before_the_snapshot(bool alternate)
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
            Assert.False(restored.Modes.IsAutoWrapMode);
            Assert.Equal((2, 4), (restored.CursorRow, restored.CursorCol));
            Assert.Contains('─', BufferSnapshot.Capture(restored).Lines[2]);
        }
        finally { File.Delete(path); }
    }

}
