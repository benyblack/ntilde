using System.Text.Json;
using Ntilde.Replay;

namespace Ntilde.VT.Tests;

public sealed class FullResetScreenTests
{
    private const string Esc = "\u001b";
    private static string Json<T>(T value) => JsonSerializer.Serialize(value);

    private static (TerminalBuffer Buffer, AnsiParser Parser) Seed(bool alternate, bool inheritedWrap)
    {
        var buffer = new TerminalBuffer(12, 4);
        var parser = new AnsiParser(buffer);
        parser.Process("history0\r\nhistory1\r\nhistory2\r\nhistory3\r\nhistory4\r\n");
        foreach (bool alt in new[] { false, true })
        {
            if (alt) parser.Process(Esc + "[?47h");
            parser.Process(Esc + "[?7h" + Esc + "[1;1H123456789012W" + Esc + "[1;1H" +
                Esc + "]8;;https://fixture.invalid/reset\a" + "e\u0301" + Esc + "]8;;\a" +
                Esc + "[1;4:3;53;38;2;17;34;51;48;2;68;85;102;58;2;119;136;153mU" +
                Esc + "[3;12H" + Esc + ")0\u000eq" + Esc + "7");
        }
        if (!alternate) parser.Process(Esc + "[?47l");
        parser.Process(Esc + (inheritedWrap ? "[?7h" : "[?7l"));
        return (buffer, parser);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RIS_clears_both_screen_cells_metadata_and_saved_pen_wrap_banks(bool alternate, bool inheritedWrap)
    {
        var (buffer, parser) = Seed(alternate, inheritedWrap);
        var fresh = new TerminalBuffer(12, 4).ExportState(0);
        var before = buffer.ExportState(100);
        Assert.NotEqual(Json(fresh.Main), Json(before.Main));
        Assert.NotEqual(Json(fresh.Alt), Json(before.Alt));
        Assert.NotEmpty(before.Hyperlinks); Assert.NotEmpty(before.Underlines);
        Assert.Contains(true, before.Main.RowWraps!); Assert.Contains(true, before.Alt.RowWraps!);
        Assert.True(before.SavedCursorMain.IsPendingWrap); Assert.True(before.SavedCursorAlt.IsPendingWrap);
        Assert.True(buffer.Scrollback.Count > 0);
        parser.Process(Esc); parser.Process("c");
        var after = buffer.ExportState(100);
        Assert.False(buffer.IsAltScreenActive);
        Assert.Equal(Json(fresh.Main), Json(after.Main)); Assert.Equal(Json(fresh.Alt), Json(after.Alt));
        Assert.Equal(0, buffer.Scrollback.Count); Assert.Empty(after.Hyperlinks); Assert.Empty(after.Underlines);
        Assert.Equal(Json(fresh.SavedCursorMain), Json(after.SavedCursorMain));
        Assert.Equal(Json(fresh.SavedCursorAlt), Json(after.SavedCursorAlt));
        Assert.Equal(Json(fresh.Sgr), Json(after.Sgr));
        Assert.True(buffer.Modes.IsAutoWrapMode); Assert.False(buffer.IsPendingWrap);
        Assert.Equal((0, 0), (buffer.CursorRow, buffer.CursorCol));
        Assert.Equal(new[] { 0, 0, 0, 0 }, parser.ExportState().Charsets); Assert.Equal(0, parser.ExportState().Gl);
        parser.Process(Esc + "8"); Assert.Equal((0, 0), (buffer.CursorRow, buffer.CursorCol));
        parser.Process("q"); Assert.StartsWith("q", BufferSnapshot.Capture(buffer).Lines[0]);
        parser.Process(Esc + "[?47h" + Esc + "8q");
        Assert.StartsWith("q", BufferSnapshot.Capture(buffer).Lines[0]);
        Assert.Equal(Json(fresh.Sgr), Json(buffer.ExportState(0).Sgr));
    }

    [Fact]
    public void RIS_clears_sized_text_from_both_screen_banks()
    {
        var buffer = new TerminalBuffer(12, 4); var parser = new AnsiParser(buffer);
        parser.Process(Esc + "]66;s=2:w=2;MAIN\a" + Esc + "[?47h" + Esc + "]66;s=2:w=2;ALT\a");
        Assert.NotNull(buffer.ExportState(0).TextSizing);
        parser.Process(Esc + "c");
        Assert.Null(buffer.ExportState(0).TextSizing);
        parser.Process(Esc + "[?47h");
        Assert.Null(buffer.ExportState(0).TextSizing);
        Assert.All(BufferSnapshot.Capture(buffer).Lines, line => Assert.Equal(string.Empty, line));
    }

    [Fact]
    public void RIS_retires_both_screens_image_handles_once_without_bypassing_snapshot_pins()
    {
        var buffer = new TerminalBuffer(12, 4); var parser = new AnsiParser(buffer);
        var main = new object(); var alternate = new object();
        buffer.AddImage(new TerminalImage(main, 0, 0, 1, 1));
        parser.Process(Esc + "[?47h");
        buffer.AddImage(new TerminalImage(alternate, 0, 0, 1, 1));
        int pin = buffer.BeginSnapshotSession();
        try
        {
            parser.Process(Esc + "c");
            Assert.Empty(buffer.Images);
            var blocked = new List<object>();
            Assert.False(buffer.DrainRetiredImageHandles(blocked, long.MaxValue)); Assert.Empty(blocked);
        }
        finally { buffer.EndSnapshotSession(pin); }
        var retired = new List<object>();
        Assert.True(buffer.DrainRetiredImageHandles(retired, long.MaxValue));
        Assert.Equal(2, retired.Count); Assert.Contains(main, retired); Assert.Contains(alternate, retired);
        Assert.False(buffer.DrainRetiredImageHandles(new List<object>(), long.MaxValue));
    }

    [Fact]
    [Trait("Category", "Replay")]
    public async Task Cleared_main_and_alternate_screen_cuts_survive_actual_complete_recording_and_restore()
    {
        var (live, parser) = Seed(alternate: true, inheritedWrap: false);
        parser.Process(Esc + "c");
        string path = Path.GetTempFileName();
        try
        {
            using (var recorder = new PtyRecorder(path, 12, 4)) recorder.RecordSnapshot(live, parser);
            var restored = new TerminalBuffer(12, 4); var restoredParser = new AnsiParser(restored);
            bool applied = false;
            await new ReplayRunner(path).RunAsync(_ => Task.CompletedTask, onSnapshotCallback: snapshot =>
            {
                ReplayRunner.ApplySnapshot(restored, restoredParser, snapshot); applied = true;
                return Task.CompletedTask;
            }, ct: TestContext.Current.CancellationToken);
            Assert.True(applied);
            var fresh = new TerminalBuffer(12, 4).ExportState(0); var after = restored.ExportState(0);
            Assert.Equal(Json(fresh.Main), Json(after.Main)); Assert.Equal(Json(fresh.Alt), Json(after.Alt));
            Assert.Equal(Json(fresh.SavedCursorMain), Json(after.SavedCursorMain));
            Assert.Equal(Json(fresh.SavedCursorAlt), Json(after.SavedCursorAlt));
        }
        finally { File.Delete(path); }
    }
}
