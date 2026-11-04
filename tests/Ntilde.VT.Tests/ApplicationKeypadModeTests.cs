using System.Text.Json;
using Ntilde.Replay;

namespace Ntilde.VT.Tests;

public sealed class ApplicationKeypadModeTests
{
    [Theory]
    [InlineData("\u001b=", "\u001b>")]
    [InlineData("\u001b[?66h", "\u001b[?66l")]
    [InlineData("\u001b=", "\u001b[?66l")]
    [InlineData("\u001b[?66h", "\u001b>")]
    public void Escape_and_private_mode_aliases_share_queryable_state(string enable, string disable)
    {
        var buffer = new TerminalBuffer(20, 5);
        var parser = new AnsiParser(buffer);
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process("\u001b[?66$p" + enable + "\u001b[?66$p" + disable + "\u001b[?66$p");
        Assert.Equal(new[] { "\u001b[?66;2$y", "\u001b[?66;1$y", "\u001b[?66;2$y" }, replies);
        Assert.False(buffer.Modes.IsApplicationKeypad);
        Assert.Equal((0, 0), (buffer.CursorRow, buffer.CursorCol));
        Assert.Equal(new TerminalBuffer(20, 5).ExportState(0).Main.CellsBase64, buffer.ExportState(0).Main.CellsBase64);
    }

    [Theory]
    [InlineData("\u001bc")]
    [InlineData("\u001b[!p")]
    public void Full_and_soft_reset_return_to_numeric_keypad(string reset)
    {
        var buffer = new TerminalBuffer(20, 5);
        var parser = new AnsiParser(buffer);
        parser.Process("\u001b=" + reset);
        Assert.False(buffer.Modes.IsApplicationKeypad);
    }

    [Fact]
    public void Cursor_save_screen_switch_and_prompt_reporting_reset_do_not_change_keypad_mode()
    {
        var buffer = new TerminalBuffer(20, 5);
        var parser = new AnsiParser(buffer);
        parser.Process("\u001b=\u001b7\u001b>\u001b8");
        Assert.False(buffer.Modes.IsApplicationKeypad);
        parser.Process("\u001b=\u001b[?1049h\u001b[?1049l");
        buffer.Modes.ResetTransientInputReporting();
        Assert.True(buffer.Modes.IsApplicationKeypad);
    }

    [Fact]
    public void Clone_complete_state_and_legacy_replay_preserve_keypad_state()
    {
        var source = new TerminalBuffer(20, 5);
        new AnsiParser(source).Process("\u001b=");
        Assert.True(source.Modes.Clone().IsApplicationKeypad);
        var snapshot = source.ExportState(0);
        var target = new TerminalBuffer(20, 5);
        target.ImportState(JsonSerializer.Deserialize<TerminalStateSnapshot>(JsonSerializer.Serialize(snapshot))!);
        Assert.True(target.Modes.IsApplicationKeypad);
        target.ApplySnapshot(new ReplaySnapshot { Cols = 20, Rows = 5, ScrollBottom = 4, IsApplicationKeypad = true });
        Assert.True(target.Modes.IsApplicationKeypad);
        target.ApplySnapshot(JsonSerializer.Deserialize<ReplaySnapshot>("{\"cols\":20,\"rows\":5,\"sb\":4}")!);
        Assert.False(target.Modes.IsApplicationKeypad);
        var legacyState = JsonSerializer.Serialize(snapshot).Replace("\"kap\":true,", "");
        target.ImportState(JsonSerializer.Deserialize<TerminalStateSnapshot>(legacyState)!);
        Assert.False(target.Modes.IsApplicationKeypad);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Replay")]
    public async Task Recorder_and_replay_preserve_keypad_mode_in_complete_and_legacy_fields(bool parserState)
    {
        string path = Path.GetTempFileName();
        try
        {
            var live = new TerminalBuffer(20, 5);
            var parser = new AnsiParser(live);
            parser.Process("\u001b=");
            using (var recorder = new PtyRecorder(path, 20, 5)) recorder.RecordSnapshot(live, parserState ? parser : null);
            var restored = new TerminalBuffer(20, 5);
            var restoredParser = new AnsiParser(restored);
            bool applied = false;
            await new ReplayRunner(path).RunAsync(_ => Task.CompletedTask, onSnapshotCallback: snapshot =>
            {
                Assert.True(snapshot.IsApplicationKeypad);
                if (parserState) Assert.True(snapshot.State!.Modes.IsApplicationKeypad);
                ReplayRunner.ApplySnapshot(restored, restoredParser, snapshot);
                applied = true;
                return Task.CompletedTask;
            }, ct: TestContext.Current.CancellationToken);
            Assert.True(applied);
            Assert.True(restored.Modes.IsApplicationKeypad);
        }
        finally { File.Delete(path); }
    }

}
