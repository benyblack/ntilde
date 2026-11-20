using Ntilde.VT.Tests.StateTransfer;

namespace Ntilde.VT.Tests;

/// <summary>
/// DECSCLM, the DEC private mode ?4 that lets a program ask for smooth (rate-limited) output scrolling instead of jump scrolling (#1001). The App deliberately
/// does not implement it: letting a byte stream throttle the terminal is a product decision nobody has made, and recognising the mode without the behaviour
/// would be a false claim. So <c>CSI ? 4 h</c> / <c>l</c> are ignored whole, <c>CSI ? 4 $ p</c> reports the mode as not recognised, and output scrolls at full
/// speed either way. The wheel-scroll animation (the SmoothScrolling setting) is a different feature, and ANSI mode 4 is IRM (insert mode), also separate.
/// These are parser and buffer tests of that policy, not a measurement of the running App.
/// </summary>
public sealed class DecsclmPolicyTests
{
    private const string Esc = "\u001b";

    // Some state a stray ?4 handler could disturb: a scroll region, an SGR pen, autowrap and origin mode, and text.
    private const string Setup = Esc + "[2;4r" + Esc + "[31;4m" + Esc + "[?6h" + Esc + "[?6l" + "ABC";

    private sealed record Run(TerminalBuffer Buffer, AnsiParser Parser, List<string> Replies);

    private static Run Start(int cols = 20, int rows = 6)
    {
        var buffer = new TerminalBuffer(cols, rows);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process(Setup);
        replies.Clear();
        return new Run(buffer, parser, replies);
    }

    private static string Row(TerminalBuffer buffer, int row = 0, int length = 7)
    {
        buffer.Lock.EnterReadLock();
        try { return new string(Enumerable.Range(0, length).Select(col => buffer.GetCell(col, row).Character).ToArray()); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    public static TheoryData<string> Sequences => new()
    {
        { Esc + "[?4h" },
        { Esc + "[?4l" },
        { Esc + "[?4h" + Esc + "[?4l" + Esc + "[?4h" },
        { Esc + "[?4s" },          // XTSAVE: there is no mode 4 slot to save
        { Esc + "[?4h" + Esc + "[?4s" + Esc + "[?4l" + Esc + "[?4r" }, // and nothing to restore
        { Esc + "[?4;4h" },        // a repeated mode in one list
    };

    [Theory]
    [MemberData(nameof(Sequences))]
    public void DECSCLM_sequences_are_not_answered_and_leave_the_state_as_the_same_stream_without_them(string sequence)
    {
        var without = Start();
        without.Parser.Process("tail");
        var with = Start();
        with.Parser.Process(sequence + "tail");

        Assert.Empty(with.Replies);
        TerminalStateAssert.AssertEquivalent(sequence, without.Buffer, without.Parser, with.Buffer, with.Parser);
        Assert.Equal("ABCtail", Row(with.Buffer));
    }

    [Fact]
    public void The_mode_is_reported_as_not_recognised_before_and_after_it_is_set_or_reset()
    {
        var run = Start();
        run.Parser.Process(Esc + "[?4$p" + Esc + "[?4h" + Esc + "[?4$p" + Esc + "[?4l" + Esc + "[?4$p");

        // Pm = 0: not recognised, never 1 (set) or 2 (reset), which would claim state the terminal does not hold.
        Assert.Equal(Enumerable.Repeat(Esc + "[?4;0$y", 3).ToArray(), run.Replies);
    }

    [Fact]
    public void Output_scrolls_at_full_speed_whatever_the_mode_is()
    {
        // No timing can be measured in a parser test, but a rate-limited scroll would leave lines unwritten when Process returns: a burst of output is fully
        // applied, scrolled into history and identical to the same burst with the mode off, in one call.
        string burst = string.Concat(Enumerable.Range(0, 200).Select(i => $"line {i}\r\n"));
        var without = Start(cols: 20, rows: 5);
        without.Parser.Process(Esc + "[r" + Esc + "[2J" + Esc + "[H" + burst);
        var with = Start(cols: 20, rows: 5);
        with.Parser.Process(Esc + "[?4h" + Esc + "[r" + Esc + "[2J" + Esc + "[H" + burst);

        Assert.Equal(without.Buffer.Scrollback.Count, with.Buffer.Scrollback.Count);
        Assert.True(with.Buffer.Scrollback.Count > 150);
        TerminalStateAssert.AssertEquivalent("burst", without.Buffer, without.Parser, with.Buffer, with.Parser);
    }

    [Fact]
    public void A_listed_neighbour_mode_still_applies_when_mode_4_is_in_the_same_list()
    {
        // ?7 is autowrap. The ignored 4 in the list must not stop the 7 beside it, in either order.
        foreach (string list in new[] { "4;7", "7;4" })
        {
            var alone = Start();
            alone.Parser.Process(Esc + "[?7l" + "tail");
            var listed = Start();
            listed.Parser.Process(Esc + "[?" + list + "l" + "tail");
            TerminalStateAssert.AssertEquivalent(list, alone.Buffer, alone.Parser, listed.Buffer, listed.Parser);

            listed.Parser.Process(Esc + "[?7$p");
            Assert.Equal(Esc + "[?7;2$y", Assert.Single(listed.Replies)); // autowrap really is reset: the list was not dropped
        }
    }

    [Fact]
    public void ANSI_mode_4_is_insert_mode_and_independent_of_DECSCLM()
    {
        var run = Start();
        run.Parser.Process(Esc + "[4h" + Esc + "[?4$p" + Esc + "[4$p");
        Assert.Equal(new[] { Esc + "[?4;0$y", Esc + "[4;1$y" }, run.Replies); // IRM is set, the private ?4 is still unrecognised

        // Insert mode really inserts at the cursor.
        run.Parser.Process(Esc + "[1;1HX");
        Assert.Equal("XABC", Row(run.Buffer, 0, 4));

        // And a private ?4 reset does not turn IRM off.
        run.Replies.Clear();
        run.Parser.Process(Esc + "[?4l" + Esc + "[4$p");
        Assert.Equal(Esc + "[4;1$y", Assert.Single(run.Replies));
    }

    [Fact]
    public void DECSCLM_split_across_chunks_is_ignored_the_same_way()
    {
        var without = Start();
        without.Parser.Process("tail");
        var split = Start();
        foreach (char c in Esc + "[?4h" + Esc + "[?4l" + Esc + "[?4s" + "tail")
            split.Parser.Process(c.ToString());

        Assert.Empty(split.Replies);
        TerminalStateAssert.AssertEquivalent("split", without.Buffer, without.Parser, split.Buffer, split.Parser);
        Assert.Equal("ABCtail", Row(split.Buffer));
    }
}
