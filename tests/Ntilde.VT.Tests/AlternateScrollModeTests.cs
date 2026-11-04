using System.Text;
using Ntilde.Replay;

namespace Ntilde.VT.Tests;

/// <summary>
/// Issue #828: DEC private mode ?1007 (alternate scroll). The VT layer tracks the application's
/// DECSET/DECRST on top of a host default, reports the effective value through DECRPM, and carries
/// only the application's half through reset, state transfer and replay. The wheel translation
/// itself lives in the App (<c>TerminalView.OnPointerWheelChanged</c>).
/// </summary>
public sealed class AlternateScrollModeTests
{
    private static (TerminalBuffer Buffer, AnsiParser Parser, List<string> Responses) Create(bool hostDefault = true)
    {
        var buffer = new TerminalBuffer(cols: 20, rows: 6);
        buffer.Modes.AlternateScrollDefault = hostDefault;
        var parser = new AnsiParser(buffer);
        var responses = new List<string>();
        parser.OnResponse = responses.Add;
        return (buffer, parser, responses);
    }

    private static string Query(AnsiParser parser, List<string> responses)
    {
        responses.Clear();
        parser.Process("\u001b[?1007$p");
        return Assert.Single(responses);
    }

    [Fact]
    public void Fresh_terminal_reports_the_host_default()
    {
        var (on, onParser, onReplies) = Create(hostDefault: true);
        Assert.Null(on.Modes.AlternateScrollOverride);
        Assert.True(on.Modes.IsAlternateScroll);
        Assert.Equal("\u001b[?1007;1$y", Query(onParser, onReplies));

        var (off, offParser, offReplies) = Create(hostDefault: false);
        Assert.False(off.Modes.IsAlternateScroll);
        Assert.Equal("\u001b[?1007;2$y", Query(offParser, offReplies));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Set_and_reset_override_the_host_default(bool hostDefault)
    {
        var (buffer, parser, responses) = Create(hostDefault);

        parser.Process("\u001b[?1007h");
        Assert.True(buffer.Modes.AlternateScrollOverride);
        Assert.True(buffer.Modes.IsAlternateScroll);
        Assert.Equal("\u001b[?1007;1$y", Query(parser, responses));

        parser.Process("\u001b[?1007l");
        Assert.False(buffer.Modes.AlternateScrollOverride);
        Assert.False(buffer.Modes.IsAlternateScroll);
        Assert.Equal("\u001b[?1007;2$y", Query(parser, responses));
    }

    [Fact]
    public void A_host_default_change_does_not_override_the_applications_choice()
    {
        var (buffer, parser, responses) = Create(hostDefault: true);
        parser.Process("\u001b[?1007l");

        buffer.Modes.AlternateScrollDefault = true;
        Assert.Equal("\u001b[?1007;2$y", Query(parser, responses));

        // Untouched, the mode follows the host default live.
        var (fresh, freshParser, freshReplies) = Create(hostDefault: true);
        fresh.Modes.AlternateScrollDefault = false;
        Assert.Equal("\u001b[?1007;2$y", Query(freshParser, freshReplies));
    }

    [Fact]
    public void Mode_is_combinable_with_other_private_modes_in_one_sequence()
    {
        var (buffer, parser, _) = Create(hostDefault: true);
        parser.Process("\u001b[?1049;1007l");
        Assert.False(buffer.Modes.IsAlternateScroll);
        parser.Process("\u001b[?1;1007h");
        Assert.True(buffer.Modes.IsApplicationCursorKeys);
        Assert.True(buffer.Modes.IsAlternateScroll);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Ris_returns_to_the_host_default(bool hostDefault)
    {
        var (buffer, parser, responses) = Create(hostDefault);
        parser.Process(hostDefault ? "\u001b[?1007l" : "\u001b[?1007h");
        Assert.NotEqual(hostDefault, buffer.Modes.IsAlternateScroll);

        parser.Process("\u001bc");

        Assert.Null(buffer.Modes.AlternateScrollOverride);
        Assert.Equal(hostDefault, buffer.Modes.AlternateScrollDefault);
        Assert.Equal(hostDefault, buffer.Modes.IsAlternateScroll);
        Assert.Equal($"\u001b[?1007;{(hostDefault ? 1 : 2)}$y", Query(parser, responses));
    }

    [Fact]
    public void Decstr_keeps_the_applications_choice()
    {
        // xterm's soft reset does not list alternateScroll; neither does ours.
        var (buffer, parser, responses) = Create(hostDefault: true);
        parser.Process("\u001b[?1007l");

        parser.Process("\u001b[!p");

        Assert.False(buffer.Modes.AlternateScrollOverride);
        Assert.Equal("\u001b[?1007;2$y", Query(parser, responses));
    }

    [Fact]
    public void Clone_copies_both_halves()
    {
        var modes = new ModeState { AlternateScrollOverride = false, AlternateScrollDefault = false };

        ModeState clone = modes.Clone();

        Assert.False(clone.AlternateScrollOverride);
        Assert.False(clone.AlternateScrollDefault);
    }

    [Theory]
    [InlineData("\u001b[?1007l", false)]
    [InlineData("\u001b[?1007h", true)]
    [InlineData("", null)]
    public void State_round_trip_carries_the_applications_choice_not_the_host_default(string sequence, bool? expected)
    {
        var (source, sourceParser, _) = Create(hostDefault: false);
        sourceParser.Process(sequence);

        TerminalStateSnapshot snapshot = source.ExportState(0);
        Assert.Equal(expected, snapshot.Modes.AlternateScrollOverride);
        snapshot.Parser = sourceParser.ExportState();
        TerminalStateSnapshot wire = TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(snapshot));

        var (target, targetParser, targetReplies) = Create(hostDefault: true);
        TerminalStateTransfer.Restore(target, targetParser, wire);

        Assert.Equal(expected, target.Modes.AlternateScrollOverride);
        // The importing host keeps its own default; an untouched mode follows it.
        Assert.True(target.Modes.AlternateScrollDefault);
        Assert.Equal($"\u001b[?1007;{((expected ?? true) ? 1 : 2)}$y", Query(targetParser, targetReplies));
    }

    [Fact]
    public void An_untouched_mode_is_omitted_from_the_wire_and_a_legacy_payload_reads_as_untouched()
    {
        var (source, _, _) = Create();
        string json = Encoding.UTF8.GetString(TerminalStateSerializer.ToBytes(source.ExportState(0)));
        Assert.DoesNotContain("\"alt_scroll\"", json);

        source.Modes.AlternateScrollOverride = false;
        string set = Encoding.UTF8.GetString(TerminalStateSerializer.ToBytes(source.ExportState(0)));
        Assert.Contains("\"alt_scroll\":false", set);

        // A payload from a build that predates ?1007 has no field at all.
        TerminalStateSnapshot legacy = TerminalStateSerializer.FromBytes(Encoding.UTF8.GetBytes(json));
        Assert.Null(legacy.Modes.AlternateScrollOverride);
    }

    [Fact]
    [Trait("Category", "Replay")]
    public async Task Recorded_checkpoint_restores_the_applications_choice()
    {
        string path = Path.GetTempFileName();
        try
        {
            var (live, liveParser, _) = Create(hostDefault: true);
            liveParser.Process("\u001b[?1049h\u001b[?1007l");
            using (var recorder = new PtyRecorder(path, 20, 6))
            {
                recorder.RecordSnapshot(live, liveParser);
                byte[] tail = Encoding.UTF8.GetBytes("\u001b[?1007h\u001b[?1007l");
                recorder.RecordChunkAt(100, tail, tail.Length);
            }

            var (restored, restoredParser, replies) = Create(hostDefault: true);
            bool applied = false;
            await new ReplayRunner(path).RunAsync(
                data => { restoredParser.Process(Encoding.UTF8.GetString(data)); return Task.CompletedTask; },
                onSnapshotCallback: snapshot =>
                {
                    ReplayRunner.ApplySnapshot(restored, restoredParser, snapshot);
                    applied = true;
                    Assert.False(restored.Modes.AlternateScrollOverride);
                    return Task.CompletedTask;
                }, ct: TestContext.Current.CancellationToken);

            Assert.True(applied);
            Assert.True(restored.IsAltScreenActive);
            Assert.False(restored.Modes.AlternateScrollOverride);
            Assert.Equal("\u001b[?1007;2$y", Query(restoredParser, replies));
        }
        finally { File.Delete(path); }
    }
}
