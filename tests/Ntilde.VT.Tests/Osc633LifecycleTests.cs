using Ntilde.VT;

namespace Ntilde.VT.Tests;

/// <summary>
/// The VS Code OSC 633 lifecycle markers A-D. They share OSC 133's state machine where the
/// documented semantics agree (prompt start, prompt end, pre-execution, execution finished), so
/// the contract under test is twofold: the same state and events result, and the places where the
/// 633 grammar differs from 133 (C has no payload, D has no duration) are not read as 133's.
/// Non-lifecycle records must never raise lifecycle events on their own.
/// </summary>
public class Osc633LifecycleTests
{
    private sealed class Recorder
    {
        public readonly List<string> Events = [];
        public readonly List<ShellIntegrationMark> Marks = [];
        public readonly List<string?> Accepted = [];
        public readonly List<(int? ExitCode, long? DurationMs)> Finished = [];

        public Recorder(AnsiParser parser)
        {
            parser.OnPromptReady = () => Events.Add("A");
            parser.OnCommandStarted = mark => { Events.Add("B"); Marks.Add(mark); };
            parser.OnCommandAccepted = text => { Events.Add("C"); Accepted.Add(text); };
            parser.OnCommandFinished = _ => Events.Add("D");
            parser.OnCommandFinishedDetailed = (code, duration) => Finished.Add((code, duration));
        }
    }

    private static (AnsiParser Parser, TerminalBuffer Buffer, Recorder Recorder) Make()
    {
        var buffer = new TerminalBuffer(40, 8);
        var parser = new AnsiParser(buffer);
        return (parser, buffer, new Recorder(parser));
    }

    private static string Osc633(string body, string terminator = "\x07") => $"\x1b]633;{body}{terminator}";

    private static string Row(TerminalBuffer buffer, int row)
    {
        buffer.Lock.EnterReadLock();
        try
        {
            var chars = new char[buffer.Cols];
            for (int col = 0; col < chars.Length; col++)
            {
                char c = buffer.GetCell(col, row).Character;
                chars[col] = c == '\0' ? ' ' : c;
            }

            return new string(chars).TrimEnd();
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void A_RaisesPromptReady_AndLeavesTheScreenAlone()
    {
        var (parser, buffer, recorder) = Make();

        parser.Process(Osc633("A") + "X");

        Assert.Equal(["A"], recorder.Events);
        Assert.Equal("X", Row(buffer, 0));
    }

    [Fact]
    public void A_ClearsLeakedInputReporting_LikeAPrompt133A()
    {
        var (parser, buffer, _) = Make();
        parser.Process("\x1b[?1000h\x1b[?1006h\x1b[?1004h");
        Assert.True(buffer.Modes.MouseModeSGR);

        parser.Process(Osc633("A"));

        Assert.False(buffer.Modes.MouseModeSGR);
        Assert.False(buffer.Modes.IsFocusEventReporting);
    }

    [Fact]
    public void B_MarksTheCursorCellAndOpensTheInputWindow()
    {
        var (parser, buffer, recorder) = Make();

        parser.Process("ntilde$ " + Osc633("B") + "ls");

        var mark = Assert.Single(recorder.Marks);
        Assert.Equal(0, mark.Row);
        Assert.Equal("ntilde$ ".Length, mark.Column);
        Assert.True(buffer.IsAcceptingCommandInput);
        Assert.Equal(mark, buffer.CommandStartMark);
        Assert.Equal("ntilde$ ls", Row(buffer, 0));
    }

    [Fact]
    public void C_RaisesTheExecutionEdge_ClosesTheWindow_AndKeepsTheCommandAnchor()
    {
        var (parser, buffer, recorder) = Make();
        parser.Process("$ " + Osc633("B") + "echo hi");
        var anchor = buffer.CommandStartMark;

        parser.Process(Osc633("C"));

        Assert.Equal(["B", "C"], recorder.Events);
        Assert.Null(Assert.Single(recorder.Accepted));
        Assert.False(buffer.IsAcceptingCommandInput);
        Assert.Equal(anchor, buffer.CommandStartMark);
    }

    [Theory]
    [InlineData("C;Z2l0IHN0YXR1cw==")]
    [InlineData("C;git status")]
    [InlineData("C;cmdline_url=ls%20-l")]
    public void C_NeverReadsAPayloadAsCommandText(string body)
    {
        var (parser, _, recorder) = Make();

        parser.Process(Osc633(body));

        // 633's C has no parameters; the command line is the separate E record. Whatever follows
        // the letter is therefore not command text, unlike OSC 133;C's base64/plain payload.
        Assert.Equal(["C"], recorder.Events);
        Assert.Null(Assert.Single(recorder.Accepted));
    }

    [Fact]
    public void D_ClosesTheWindowAndRaisesFinishedWithTheDocumentedExitCode()
    {
        var (parser, buffer, recorder) = Make();
        parser.Process("$ " + Osc633("B") + "false");

        parser.Process(Osc633("D;7"));

        Assert.Equal(["B", "D"], recorder.Events);
        Assert.Equal((7, (long?)null), Assert.Single(recorder.Finished));
        Assert.False(buffer.IsAcceptingCommandInput);
    }

    [Theory]
    [InlineData("D", null)]
    [InlineData("D;", null)]
    [InlineData("D;0", 0)]
    [InlineData("D;-1", -1)]
    [InlineData("D;abc", null)]
    public void D_ExitCodeIsOptionalAndMalformedCodesAreNotInvented(string body, int? expected)
    {
        var (parser, _, recorder) = Make();

        parser.Process(Osc633(body));

        Assert.Equal((expected, (long?)null), Assert.Single(recorder.Finished));
    }

    [Fact]
    public void D_DoesNotReadOsc133sDurationField()
    {
        var (parser, _, recorder) = Make();

        parser.Process(Osc633("D;3;2450"));
        parser.Process("\x1b]133;D;3;2450\x07");

        Assert.Equal([(3, (long?)null), (3, 2450L)], recorder.Finished);
    }

    [Theory]
    [InlineData("E;ls -l")]
    [InlineData("E;ls\\x3bpwd;nonce")]
    [InlineData("P;Cwd=/tmp")]
    [InlineData("P;IsWindows=True")]
    [InlineData("F")]
    [InlineData("G")]
    [InlineData("H")]
    [InlineData("I")]
    [InlineData("a")]
    [InlineData("")]
    public void OtherRecords_HaveNoLifecycleEffect(string body)
    {
        var (parser, buffer, recorder) = Make();
        parser.Process("$ " + Osc633("B") + "x");
        recorder.Events.Clear();
        var anchor = buffer.CommandStartMark;

        parser.Process(Osc633(body) + "y");

        Assert.Empty(recorder.Events);
        Assert.Empty(recorder.Finished);
        Assert.True(buffer.IsAcceptingCommandInput);
        Assert.Equal(anchor, buffer.CommandStartMark);
        Assert.Equal("$ xy", Row(buffer, 0));
    }

    [Fact]
    public void StTerminatedAndSplitSequencesBehaveLikeBel()
    {
        var (parser, _, recorder) = Make();

        parser.Process(Osc633("A", "\x1b\\"));
        parser.Process("\x1b]633;");
        parser.Process("D;");
        parser.Process("9\x07");

        Assert.Equal(["A", "D"], recorder.Events);
        Assert.Equal((9, (long?)null), Assert.Single(recorder.Finished));
    }

    [Fact]
    public void ALifecycleThrough633MatchesTheSameLifecycleThrough133()
    {
        static (string Events, string Screen, ShellIntegrationMark? Anchor, bool Accepting) Run(string family)
        {
            var (parser, buffer, recorder) = Make();
            string Mark(string m) => $"\x1b]{family};{m}\x07";

            parser.Process(Mark("A") + "ntilde$ " + Mark("B") + "echo ok");
            parser.Process(Mark("C"));
            parser.Process("\r\nok\r\n");
            bool acceptingAfterC = buffer.IsAcceptingCommandInput;
            parser.Process(Mark("D;0"));
            recorder.Events.Add($"accepting-after-c={acceptingAfterC}");
            return (string.Join(",", recorder.Events), Row(buffer, 0) + "|" + Row(buffer, 1), buffer.CommandStartMark is { } anchor ? anchor with { Generation = 0 } : null, buffer.IsAcceptingCommandInput);
        }

        // Generation is a per-buffer epoch counter, not part of the lifecycle state being compared.
        Assert.Equal(Run("133"), Run("633"));
    }

    [Fact]
    public void Osc133IsUnchangedByTheDialectSplit()
    {
        var (parser, _, recorder) = Make();

        parser.Process("\x1b]133;C;Z2l0IHN0YXR1cw==\x07\x1b]133;D;17;2450\x07");

        Assert.Equal("git status", Assert.Single(recorder.Accepted));
        Assert.Equal((17, 2450L), Assert.Single(recorder.Finished));
    }
}
