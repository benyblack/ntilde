using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Ntilde.Replay;
using Ntilde.Shell;
using Ntilde.UI.Replay;
using Ntilde.VT;

namespace Ntilde.Tests.Core;

public sealed class ReplaySeekIsolationTests
{
    [AvaloniaTheory]
    [InlineData("\u001b[31;", "1mX", false)]
    [InlineData("\u001b]2;", "title\u0007X", false)]
    [InlineData("\u001bP$q", "m\u001b\\X", false)]
    [InlineData("\u001b(", "0q", false)]
    [InlineData("\u001b[31;", "1mX", true)]
    [InlineData("\u001b]2;", "title\u0007X", true)]
    [InlineData("\u001bP$q", "m\u001b\\X", true)]
    [InlineData("\u001b(", "0q", true)]
    public async Task Repeated_seeks_without_a_checkpoint_do_not_reuse_parser_continuation(string prefix, string tail, bool v1)
    {
        ReplayEvent[] events = [Data(10, prefix), Data(30, tail)];
        using var fixture = new SeekFixture(events, v1: v1);
        await fixture.Ready(30);
        foreach (long target in new[] { 10L, 0L, 30L, 10L, 30L })
        {
            await fixture.Seek(target);
            fixture.AssertMatchesFromStart(events, target, v1 ? 80 : 10, v1 ? 24 : 5);
        }
    }

    [AvaloniaTheory]
    [InlineData("\u001b[31;", "1mX", false)]
    [InlineData("\u001b]2;", "title\u0007X", false)]
    [InlineData("\u001bP$q", "m\u001b\\X", false)]
    [InlineData("\u001b(", "0q", false)]
    [InlineData("\u001b[31;", "1mX", true)]
    [InlineData("\u001b]2;", "title\u0007X", true)]
    [InlineData("\u001bP$q", "m\u001b\\X", true)]
    [InlineData("\u001b(", "0q", true)]
    public async Task A_legacy_snapshot_inside_a_sequence_is_not_a_restart_checkpoint(string prefix, string tail, bool futureState)
    {
        var original = new TerminalBuffer(10, 5);
        new AnsiParser(original).Process(prefix);
        var snapshot = CaptureLegacy(original);
        if (futureState)
        {
            snapshot.State = original.ExportState(0);
            snapshot.State.Version = TerminalStateSnapshot.CurrentVersion + 1;
            snapshot.State.Parser.State = 999; // unsupported versions must not import this parser
        }
        ReplayEvent[] events = [Data(10, prefix), Snapshot(20, snapshot), Data(30, tail), Data(40, "Y")];
        using var fixture = new SeekFixture(events);
        await fixture.Ready(40);
        foreach (long target in new[] { 30L, 10L, 0L, 40L, 10L, 40L })
        {
            await fixture.Seek(target);
            fixture.AssertMatchesFromStart(events, target);
        }
    }

    [AvaloniaFact]
    public async Task Legacy_seek_reconstruction_preserves_screen_banks_saved_charsets_and_modes()
    {
        const string prefix = "main\u001b[?7l\u001b(0\u001b[?1048h\u001b[?47halt";
        const string tail = "\u001b[?47l\u001b[?1048lq";
        var original = new TerminalBuffer(10, 5);
        new AnsiParser(original).Process(prefix);
        ReplayEvent[] events = [Data(10, prefix), Snapshot(20, CaptureLegacy(original)), Data(30, tail)];
        using var fixture = new SeekFixture(events);
        await fixture.Ready(30);
        foreach (long target in new[] { 30L, 0L, 20L, 30L })
        {
            await fixture.Seek(target);
            fixture.AssertMatchesFromStart(events, target);
        }
    }

    [AvaloniaFact]
    public async Task A_complete_checkpoint_before_a_legacy_snapshot_retains_its_geometry_and_parser_cut()
    {
        var original = new TerminalBuffer(20, 6);
        var parser = new AnsiParser(original);
        parser.Process("\u001b[31;");
        var state = original.ExportState(0);
        state.Parser = parser.ExportState();
        ReplayEvent[] events = [Data(10, "\u001b[31;"), Snapshot(20, Complete(state)), Snapshot(25, CaptureLegacy(original)), Data(30, "1mabcdefghijklmnop")];
        using var fixture = new SeekFixture(events);
        await fixture.Ready(30);
        parser.Process("1mabcdefghijklmnop");
        await fixture.Seek(30);
        AssertPair(original, parser, fixture.Buffer, fixture.Parser);
        await fixture.PlayFrom(30);
        AssertPair(original, parser, fixture.Buffer, fixture.Parser);
    }

    [AvaloniaTheory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task A_checkpoint_resumes_after_its_record_even_when_adjacent_output_shares_its_timestamp(string newline)
    {
        var original = new TerminalBuffer(10, 5);
        var parser = new AnsiParser(original);
        parser.Process("A");
        var state = original.ExportState(0);
        state.Parser = parser.ExportState();
        ReplayEvent[] events = [Data(10, "A"), Snapshot(10, Complete(state)), Data(10, "B"), Data(20, "C")];
        using var fixture = new SeekFixture(events, newline: newline, blankLines: true);
        await fixture.Ready(20);
        foreach (long target in new[] { 10L, 20L, 0L, 10L })
        {
            await fixture.Seek(target);
            fixture.AssertMatchesFromStart(events, target);
        }
        await fixture.PlayFrom(20);
        fixture.AssertMatchesFromStart(events, 20);
    }

    [AvaloniaFact]
    public async Task A_malformed_complete_checkpoint_does_not_reset_the_current_parser_or_buffer()
    {
        var state = new TerminalBuffer(10, 5).ExportState(0);
        state.Parser = null!;
        using var fixture = new SeekFixture([Snapshot(20, Complete(state))]);
        await fixture.Ready(20);
        fixture.Parser.Process("preserved\u001b[31;");
        var before = Pair(fixture.Buffer, fixture.Parser);
        await fixture.Seek(20);
        Assert.Equivalent(before, Pair(fixture.Buffer, fixture.Parser), strict: true);
        Assert.Equal(0, fixture.Model.CurrentTimeMs);
    }

    [AvaloniaTheory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Invalid_legacy_or_future_snapshot_layout_preserves_the_displayed_pair(bool futureState, bool wrongSize, bool playback)
    {
        var snapshot = CaptureLegacy(new TerminalBuffer(10, 5));
        if (futureState)
        {
            snapshot.State = new TerminalBuffer(10, 5).ExportState(0);
            snapshot.State.Version = TerminalStateSnapshot.CurrentVersion + 1;
        }
        if (wrongSize) snapshot.CellsSizeOf = -1;
        else snapshot.CellsLayoutId = "unsupported-layout";
        using var fixture = new SeekFixture([Data(10, "prefix"), Snapshot(20, snapshot), Data(30, "later")]);
        await fixture.Ready(30);
        await fixture.Seek(10);
        fixture.Parser.Process("preserved\u001b[31;");
        var before = Pair(fixture.Buffer, fixture.Parser);
        if (playback) await fixture.PlayFrom(20);
        else await fixture.Seek(20);
        Assert.Equivalent(before, Pair(fixture.Buffer, fixture.Parser), strict: true);
        Assert.Equal(10, fixture.Model.CurrentTimeMs);
    }

    [AvaloniaFact]
    public async Task Closing_invalidates_callbacks_from_the_last_playback_session()
    {
        using var fixture = new SeekFixture([Data(10, "X")]);
        await fixture.Ready(10);
        int session = fixture.Gate.BeginSession();
        fixture.Parser.SixelCellSizeOverride = new SixelCellSize(20, 6);
        fixture.Window.Close();
        await fixture.InvokeTask("ApplyCellMetricsAsync", session, 10f, 20f);
        Assert.Equal(new SixelCellSize(20, 6), fixture.Parser.SixelCellSizeOverride);
    }

    [AvaloniaFact]
    public async Task Equal_time_checkpoints_choose_the_last_physical_cut()
    {
        var small = new TerminalBuffer(10, 5);
        var smallParser = new AnsiParser(small);
        smallParser.Process("A");
        var large = new TerminalBuffer(20, 6);
        var largeParser = new AnsiParser(large);
        largeParser.Process("AB");
        ReplayEvent[] events = [Data(10, "A"), Snapshot(10, Complete(Pair(small, smallParser))),
            Data(10, "B"), Snapshot(10, Complete(Pair(large, largeParser))), Data(10, "C"), Data(20, "")];
        using var fixture = new SeekFixture(events);
        await fixture.Ready(20);
        largeParser.Process("C");
        await fixture.Seek(20);
        AssertPair(large, largeParser, fixture.Buffer, fixture.Parser);
    }

    [AvaloniaFact]
    public async Task A_checkpoint_at_EOF_keeps_its_position_when_no_later_event_runs()
    {
        var source = new TerminalBuffer(10, 5);
        var parser = new AnsiParser(source);
        parser.Process("checkpoint");
        using var fixture = new SeekFixture([Data(10, "checkpoint"), Snapshot(20, Complete(Pair(source, parser)))]);
        await fixture.Ready(20);
        await fixture.Seek(20);
        AssertPair(source, parser, fixture.Buffer, fixture.Parser);
        Assert.Equal(20, fixture.Model.CurrentTimeMs);
        await fixture.PlayFrom(20);
        AssertPair(source, parser, fixture.Buffer, fixture.Parser);
        Assert.Equal(20, fixture.Model.CurrentTimeMs);
    }

    public static IEnumerable<object[]> SplitUtf8Cases()
    {
        foreach (string text in new[] { "ñ", "€", "🙂" })
            for (int cut = 1; cut < Encoding.UTF8.GetByteCount(text); cut++)
                foreach (bool v1 in new[] { false, true })
                    yield return new object[] { text, cut, v1 };
    }

    [AvaloniaTheory]
    [MemberData(nameof(SplitUtf8Cases))]
    public async Task Each_restart_preserves_split_utf8_without_inheriting_the_previous_runs_tail(string text, int cut, bool v1)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        var prefix = new ReplayEvent { Type = "data", TimeOffsetMs = 10, Data = Convert.ToBase64String(bytes[..cut]) };
        var tail = new ReplayEvent { Type = "data", TimeOffsetMs = 30, Data = Convert.ToBase64String(bytes[cut..]) };
        ReplayEvent[] events = v1 ? [prefix, tail] : [prefix, Snapshot(20, CaptureLegacy(new TerminalBuffer(10, 5))), tail];
        using var fixture = new SeekFixture(events, v1: v1);
        await fixture.Ready(30);
        var blank = new TerminalBuffer(v1 ? 80 : 10, v1 ? 24 : 5);
        var blankParser = new AnsiParser(blank);
        var expected = new TerminalBuffer(v1 ? 80 : 10, v1 ? 24 : 5);
        var expectedParser = new AnsiParser(expected);
        expectedParser.Process(text);
        for (int run = 0; run < 2; run++)
        {
            await fixture.Seek(10);
            AssertPair(blank, blankParser, fixture.Buffer, fixture.Parser);
            await fixture.Seek(30);
            AssertPair(expected, expectedParser, fixture.Buffer, fixture.Parser);
        }
        await fixture.PlayFrom(10);
        AssertPair(expected, expectedParser, fixture.Buffer, fixture.Parser);
        await fixture.PlayFrom(30);
        AssertPair(expected, expectedParser, fixture.Buffer, fixture.Parser);
    }

    public static IEnumerable<object[]> CompleteCheckpointUtf8Cases()
    {
        foreach (var row in SplitUtf8Cases().Where(row => !(bool)row[2]))
            foreach (string newline in new[] { "\n", "\r\n" })
                yield return new[] { row[0], row[1], newline };
    }

    [AvaloniaTheory]
    [MemberData(nameof(CompleteCheckpointUtf8Cases))]
    public async Task A_complete_checkpoint_primes_only_utf8_continuation_from_the_skipped_prefix(string text, int cut, string newline)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        var prefixBytes = Encoding.UTF8.GetBytes("raw-prefix").Concat(bytes[..cut]).ToArray();
        var source = new TerminalBuffer(20, 6);
        var parser = new AnsiParser(source);
        parser.Process("checkpoint");
        var checkpoint = Pair(source, parser);
        ReplayEvent[] events = [
            new() { Type = "data", TimeOffsetMs = 10, Data = Convert.ToBase64String(prefixBytes) },
            Snapshot(20, Complete(checkpoint)),
            new() { Type = "data", TimeOffsetMs = 30, Data = Convert.ToBase64String(bytes[cut..]) },
            Data(40, "Z")];
        using var fixture = new SeekFixture(events, newline: newline, blankLines: true);
        await fixture.Ready(40);
        parser.Process(text + "Z");
        foreach (long target in new[] { 40L, 20L, 40L })
        {
            await fixture.Seek(target);
            if (target == 20) Assert.Equivalent(checkpoint, Pair(fixture.Buffer, fixture.Parser), strict: true);
            else AssertPair(source, parser, fixture.Buffer, fixture.Parser);
        }
        await fixture.PlayFrom(20);
        AssertPair(source, parser, fixture.Buffer, fixture.Parser);
        await fixture.PlayFrom(40);
        AssertPair(source, parser, fixture.Buffer, fixture.Parser);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prefix_reconstruction_does_not_publish_times_before_the_requested_playback_position(bool complete)
    {
        var source = new TerminalBuffer(10, 5);
        var parser = new AnsiParser(source);
        parser.Process("A");
        ReplaySnapshot snapshot = complete ? Complete(Pair(source, parser)) : CaptureLegacy(source);
        using var fixture = new SeekFixture([Data(10, "A"), Snapshot(20, snapshot), Data(30, "B")]);
        await fixture.Ready(30);
        var published = new List<long>();
        fixture.Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ReplayViewModel.CurrentTimeMs)) published.Add(fixture.Model.CurrentTimeMs);
        };
        await fixture.PlayFrom(25);
        Assert.NotEmpty(published);
        Assert.All(published, time => Assert.True(time >= 25, $"Playback moved back to {time}."));
        Assert.Equal(30, published[^1]);
        parser.Process("B");
        AssertPair(source, parser, fixture.Buffer, fixture.Parser);
    }

    private static ReplayEvent Data(long time, string text) => new()
    {
        Type = "data",
        TimeOffsetMs = time,
        Data = Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
    };
    private static ReplayEvent Snapshot(long time, ReplaySnapshot snapshot) => new() { Type = "snapshot", TimeOffsetMs = time, Snapshot = snapshot };
    private static ReplaySnapshot Complete(TerminalStateSnapshot state) => new() { Cols = state.Cols, Rows = state.Rows, State = state };
    private static TerminalStateSnapshot Pair(TerminalBuffer buffer, AnsiParser parser)
    {
        var state = buffer.ExportState(0);
        state.Parser = parser.ExportState();
        return state;
    }
    private static void AssertPair(TerminalBuffer expected, AnsiParser expectedParser, TerminalBuffer actual, AnsiParser actualParser) =>
        Assert.Equivalent(Pair(expected, expectedParser), Pair(actual, actualParser), strict: true);

    private static ReplaySnapshot CaptureLegacy(TerminalBuffer buffer)
    {
        string path = Path.GetTempFileName();
        try
        {
            using (var recorder = new PtyRecorder(path, buffer.Cols, buffer.Rows)) recorder.RecordSnapshot(buffer);
            var snapshot = JsonSerializer.Deserialize(File.ReadAllLines(path)[1], ReplayJsonContext.Default.ReplayEvent)!.Snapshot!;
            Assert.Null(snapshot.State);
            return snapshot;
        }
        finally { File.Delete(path); }
    }

    private sealed class SeekFixture : IDisposable
    {
        private readonly string _path = Path.GetTempFileName();
        public ReplayWindow Window { get; }
        private readonly TerminalBuffer _originalBuffer;
        private readonly AnsiParser _originalParser;
        private readonly object? _decoder;
        public ReplayViewModel Model => (ReplayViewModel)Window.DataContext!;
        public TerminalBuffer Buffer => Field<TerminalBuffer>("_buffer");
        public AnsiParser Parser => Field<AnsiParser>("_parser");
        public PlaybackSessionGate Gate => Field<PlaybackSessionGate>("_playbackSessionGate");

        public SeekFixture(ReplayEvent[] events, bool v1 = false, string newline = "\n", bool blankLines = false)
        {
            var lines = new List<string>();
            if (!v1) lines.Add(JsonSerializer.Serialize(new ReplayHeader { Type = ReplayHeader.TypeToken, Cols = 10, Rows = 5 }, ReplayJsonContext.Default.ReplayHeader));
            foreach (var item in events)
            {
                if (blankLines) lines.Add("");
                lines.Add(v1
                    ? JsonSerializer.Serialize(new { t = item.TimeOffsetMs, d = item.Data })
                    : JsonSerializer.Serialize(item, ReplayJsonContext.Default.ReplayEvent));
            }
            File.WriteAllText(_path, string.Join(newline, lines) + newline);
            Window = new ReplayWindow(_path);
            _originalBuffer = Buffer;
            _originalParser = Parser;
            _decoder = Parser.ImageDecoder;
        }
        public Task Ready(long duration) => Ntilde.Mux.Tests.Support.TestWait.UntilAsync(() => Model.DurationMs == duration, "seek fixture reached the end of the recording");
        public async Task Seek(long time)
        {
            await InvokeTask("PerformSeek", time, CancellationToken.None);
            AssertBindings();
        }
        public async Task PlayFrom(long time)
        {
            Model.CurrentTimeMs = time;
            Model.PlaybackSpeed = 1_000_000;
            await InvokeTask("PlaybackLoop", Gate.BeginSession(), CancellationToken.None);
            AssertBindings();
        }
        public Task InvokeTask(string method, params object[] args) =>
            (Task)typeof(ReplayWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Window, args)!;
        public void AssertMatchesFromStart(ReplayEvent[] events, long target, int cols = 10, int rows = 5)
        {
            var expected = new TerminalBuffer(cols, rows);
            var parser = new AnsiParser(expected);
            foreach (var item in events.Where(e => e.Type == "data" && e.TimeOffsetMs <= target))
                parser.Process(Encoding.UTF8.GetString(Convert.FromBase64String(item.Data!)));
            AssertPair(expected, parser, Buffer, Parser);
        }
        private void AssertBindings()
        {
            Assert.Same(_originalBuffer, Buffer);
            Assert.Same(_originalParser, Parser);
            Assert.Same(_decoder, Parser.ImageDecoder);
            Assert.Same(Buffer, Window.FindControl<TerminalView>("TermView")!.Buffer);
        }
        private T Field<T>(string name) => (T)typeof(ReplayWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window)!;
        public void Dispose() { Window.Close(); File.Delete(_path); }
    }
}
