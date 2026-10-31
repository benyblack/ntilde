using Ntilde.VT;
using Ntilde.Replay;
using System.Text;
using System.Text.Json;

namespace Ntilde.VT.Tests;

public sealed class ColorSchemeReportingTests
{
    [Theory]
    [InlineData(0, 0, 0, 1)]
    [InlineData(255, 255, 255, 2)]
    [InlineData(255, 0, 0, 1)]
    [InlineData(0, 255, 0, 2)]
    [InlineData(0, 0, 255, 1)]
    [InlineData(127, 127, 127, 1)]
    [InlineData(128, 128, 128, 2)]
    public void Private_query_reports_the_effective_background(int r, int g, int b, int expected)
    {
        var parser = new AnsiParser(new TerminalBuffer(10, 5))
        {
            DefaultBackground = TermColor.FromRgb((byte)r, (byte)g, (byte)b)
        };
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process("\u001b[?996n");
        Assert.Equal(new[] { $"\u001b[?997;{expected}n" }, replies);
    }

    [Fact]
    public void Reporting_is_opt_in_and_only_changes_publish_notifications()
    {
        var parser = new AnsiParser(new TerminalBuffer(10, 5));
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process("\u001b[?2031$p");
        Assert.Equal(new[] { "\u001b[?2031;2$y" }, replies);
        replies.Clear();
        parser.DefaultBackground = TermColor.White;
        Assert.Empty(replies);
        parser.Process("\u001b[?2031h\u001b[?2031h");
        Assert.Empty(replies);
        parser.Process("\u001b[?2031$p");
        Assert.Equal(new[] { "\u001b[?2031;1$y" }, replies);
        replies.Clear();
        parser.DefaultBackground = TermColor.Black;
        parser.DefaultBackground = TermColor.Black;
        parser.DefaultBackground = TermColor.FromRgb(10, 20, 30);
        Assert.Equal(new[] { "\u001b[?997;1n" }, replies);
        replies.Clear();
        parser.DefaultBackground = TermColor.White;
        Assert.Equal(new[] { "\u001b[?997;2n" }, replies);
        replies.Clear();
        parser.Process("\u001b[?2031l");
        parser.DefaultBackground = TermColor.Black;
        Assert.Empty(replies);
        parser.Process("\u001b[?2031$p\u001b[?996n");
        Assert.Equal(new[] { "\u001b[?2031;2$y", "\u001b[?997;1n" }, replies);
    }

    [Theory]
    [InlineData("\u001b[996n")]
    [InlineData("\u001b[>996n")]
    [InlineData("\u001b[<996n")]
    [InlineData("\u001b[=996n")]
    [InlineData("\u001b[?996$n")]
    [InlineData("\u001b[?996;1n")]
    [InlineData("\u001b[?996:1n")]
    [InlineData("\u001b[?997;1n")]
    public void Other_sequence_forms_do_not_request_a_color_scheme(string sequence)
    {
        var parser = new AnsiParser(new TerminalBuffer(10, 5));
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process(sequence);
        Assert.Empty(replies);
    }

    [Theory]
    [InlineData("\u001b[!p", 1)]
    [InlineData("\u001bc", 2)]
    public void Soft_reset_preserves_reporting_but_RIS_disables_it(string reset, int expectedMode)
    {
        var parser = new AnsiParser(new TerminalBuffer(10, 5)) { DefaultBackground = TermColor.White };
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process("\u001b[?2031h" + reset + "\u001b[?2031$p\u001b[?996n");
        Assert.Equal(new[] { $"\u001b[?2031;{expectedMode}$y", "\u001b[?997;2n" }, replies);
    }

    [Fact]
    public void Paired_restore_keeps_the_subscription_and_the_receiving_hosts_theme()
    {
        var source = new TerminalBuffer(10, 5);
        var original = new AnsiParser(source) { DefaultBackground = TermColor.Black };
        original.Process("\u001b[?2031h\u001b[31;");
        var snapshot = source.ExportState(0);
        snapshot.Parser = original.ExportState();
        var target = new TerminalBuffer(10, 5);
        var restored = new AnsiParser(target) { DefaultBackground = TermColor.White };
        var replies = new List<string>();
        restored.OnResponse = replies.Add;
        TerminalStateTransfer.Restore(target, restored, snapshot);
        Assert.Empty(replies);
        restored.Process("1mX\u001b[?2031$p\u001b[?996n");
        Assert.Equal(new[] { "\u001b[?2031;1$y", "\u001b[?997;2n" }, replies);
        Assert.True(target.IsBold);
        replies.Clear();
        restored.DefaultBackground = TermColor.Black;
        Assert.Equal(new[] { "\u001b[?997;1n" }, replies);
    }

    [Fact]
    [Trait("Category", "Replay")]
    public async Task Serialized_recording_snapshots_preserve_the_subscription_without_overwriting_host_colors()
    {
        string path = Path.GetTempFileName();
        try
        {
            var buffer = new TerminalBuffer(10, 5);
            var parser = new AnsiParser(buffer) { DefaultBackground = TermColor.Black };
            parser.Process("\u001b[?2031h\u001b[31;");
            using (var recorder = new PtyRecorder(path, 10, 5))
            {
                recorder.RecordSnapshot(buffer, parser);
                byte[] tail = Encoding.UTF8.GetBytes("1mX\u001b[?2031$p\u001b[?996n");
                recorder.RecordChunkAt(10, tail, tail.Length);
            }
            var restoredBuffer = new TerminalBuffer(10, 5);
            var restored = new AnsiParser(restoredBuffer) { DefaultBackground = TermColor.White };
            var replies = new List<string>();
            restored.OnResponse = replies.Add;
            bool applied = false;
            await new ReplayReader(path).RunWithResultAsync(
                data => { restored.Process(Encoding.UTF8.GetString(data)); return Task.CompletedTask; },
                onSnapshotCallback: snapshot =>
                {
                    ReplayRunner.ApplySnapshot(restoredBuffer, restored, snapshot);
                    Assert.Empty(replies);
                    applied = true;
                    return Task.CompletedTask;
                }, ct: TestContext.Current.CancellationToken);
            Assert.True(applied);
            Assert.True(restoredBuffer.IsBold);
            Assert.Equal(new[] { "\u001b[?2031;1$y", "\u001b[?997;2n" }, replies);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_legacy_parser_payload_without_the_subscription_resets_it_to_off()
    {
        var parser = new AnsiParser(new TerminalBuffer(10, 5));
        parser.Process("\u001b[?2031h");
        var legacy = JsonSerializer.Deserialize("{}", ReplayJsonContext.Default.AnsiParserState)!;
        parser.ImportState(legacy);
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.DefaultBackground = TermColor.White;
        Assert.Empty(replies);
        parser.Process("\u001b[?2031$p");
        Assert.Equal(new[] { "\u001b[?2031;2$y" }, replies);
    }

    [Fact]
    public void Query_and_subscription_are_independent_of_input_chunk_boundaries()
    {
        const string sequence = "\u001b[?2031h\u001b[?2031$p\u001b[?996n";
        for (int cut = 1; cut < sequence.Length; cut++)
        {
            var parser = new AnsiParser(new TerminalBuffer(10, 5));
            var replies = new List<string>();
            parser.OnResponse = replies.Add;
            parser.Process(sequence[..cut]);
            parser.Process(sequence[cut..]);
            Assert.Equal(new[] { "\u001b[?2031;1$y", "\u001b[?997;1n" }, replies);
        }
    }
}
