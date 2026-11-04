using System.Text.Json;
using Ntilde.Replay;

namespace Ntilde.VT.Tests;

public sealed class X10MouseTrackingTests
{
    [Fact]
    public void X10_press_only_mode_has_exact_live_queries_and_fragmented_enable_reset()
    {
        var buffer = new TerminalBuffer(20, 5);
        var parser = new AnsiParser(buffer);
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process("\u001b[?9$p");
        foreach (char value in "\u001b[?9h\u001b[?9$p\u001b[?9l\u001b[?9$p") parser.Process(value.ToString());
        Assert.Equal(new[] { "\u001b[?9;2$y", "\u001b[?9;1$y", "\u001b[?9;2$y" }, replies);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(1000)]
    [InlineData(1002)]
    [InlineData(1003)]
    public void Tracking_protocols_are_mutually_exclusive_and_any_recognized_reset_disables_tracking(int selected)
    {
        var buffer = new TerminalBuffer(20, 5);
        var parser = new AnsiParser(buffer);
        var replies = new List<string>(); parser.OnResponse = replies.Add;
        parser.Process("\u001b[?9;1000;1002;1003h" + $"\u001b[?{selected}h");
        foreach (int mode in new[] { 9, 1000, 1002, 1003 }) parser.Process($"\u001b[?{mode}$p");
        Assert.Equal(new[] { 9, 1000, 1002, 1003 }.Select(mode => $"\u001b[?{mode};{(mode == selected ? 1 : 2)}$y"), replies);
        replies.Clear();
        parser.Process("\u001b[?1000l");
        foreach (int mode in new[] { 9, 1000, 1002, 1003 }) parser.Process($"\u001b[?{mode}$p");
        Assert.Equal(new[] { 9, 1000, 1002, 1003 }.Select(mode => $"\u001b[?{mode};2$y"), replies);
    }

    [Fact]
    public void Clone_and_complete_snapshot_preserve_press_only_mode_and_old_snapshots_default_off()
    {
        var source = new TerminalBuffer(20, 5);
        new AnsiParser(source).Process("\u001b[?9;1006h");
        Assert.True(source.Modes.Clone().MouseModeX10PressOnly);
        string json = JsonSerializer.Serialize(source.ExportState(0));
        var target = new TerminalBuffer(20, 5);
        target.ImportState(JsonSerializer.Deserialize<TerminalStateSnapshot>(json)!);
        Assert.True(target.Modes.MouseModeX10PressOnly);
        Assert.True(target.Modes.MouseModeSGR);
        Assert.True(target.IsMouseReportingActive());
        target.ImportState(JsonSerializer.Deserialize<TerminalStateSnapshot>(json.Replace("\"m_9\":true,", ""))!);
        Assert.False(target.Modes.MouseModeX10PressOnly);
        Assert.False(target.IsMouseReportingActive());
    }

    [Fact]
    public void Soft_reset_preserves_tracking_but_full_reset_and_prompt_policy_clear_it()
    {
        var buffer = new TerminalBuffer(20, 5);
        var parser = new AnsiParser(buffer);
        parser.Process("\u001b[?9;1006h\u001b[!p");
        Assert.True(buffer.Modes.MouseModeX10PressOnly); Assert.True(buffer.Modes.MouseModeSGR);
        parser.Process("\u001bc");
        Assert.False(buffer.IsMouseReportingActive()); Assert.False(buffer.Modes.MouseModeX10PressOnly);
        parser.Process("\u001b[?9h");
        buffer.Modes.ResetTransientInputReporting();
        Assert.False(buffer.IsMouseReportingActive()); Assert.False(buffer.Modes.MouseModeX10PressOnly);
    }

    [Fact]
    public void Legacy_press_only_snapshot_replaces_preexisting_tracking_and_screen_switches_keep_global_mode()
    {
        var buffer = new TerminalBuffer(20, 5); var parser = new AnsiParser(buffer);
        parser.Process("\u001b[?1003h");
        buffer.ApplySnapshot(new ReplaySnapshot { Cols = 20, Rows = 5, ScrollBottom = 4, MouseModeX10PressOnly = true });
        Assert.True(buffer.Modes.MouseModeX10PressOnly);
        Assert.False(buffer.Modes.MouseModeX10); Assert.False(buffer.Modes.MouseModeButtonEvent); Assert.False(buffer.Modes.MouseModeAnyEvent);
        parser.Process("\u001b[?1049h\u001b[?1049l"); Assert.True(buffer.Modes.MouseModeX10PressOnly);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Replay")]
    public async Task Complete_and_legacy_recordings_replay_press_only_tracking(bool parserState)
    {
        string path = Path.GetTempFileName();
        try
        {
            var live = new TerminalBuffer(20, 5);
            var parser = new AnsiParser(live); parser.Process("\u001b[?9h");
            using (var recorder = new PtyRecorder(path, 20, 5)) recorder.RecordSnapshot(live, parserState ? parser : null);
            var restored = new TerminalBuffer(20, 5); var restoredParser = new AnsiParser(restored);
            bool applied = false;
            await new ReplayRunner(path).RunAsync(_ => Task.CompletedTask, onSnapshotCallback: snapshot =>
            {
                Assert.True(snapshot.MouseModeX10PressOnly);
                if (parserState) Assert.True(snapshot.State!.Modes.MouseModeX10PressOnly);
                ReplayRunner.ApplySnapshot(restored, restoredParser, snapshot); applied = true;
                return Task.CompletedTask;
            }, ct: TestContext.Current.CancellationToken);
            Assert.True(applied); Assert.True(restored.Modes.MouseModeX10PressOnly);
            restored.ApplySnapshot(JsonSerializer.Deserialize<ReplaySnapshot>("{\"cols\":20,\"rows\":5,\"sb\":4}")!);
            Assert.False(restored.Modes.MouseModeX10PressOnly);
        }
        finally { File.Delete(path); }
    }
}
