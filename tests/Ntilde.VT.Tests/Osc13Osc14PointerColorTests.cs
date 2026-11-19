using System.Text.Json;
using Ntilde.VT.Tests.StateTransfer;

namespace Ntilde.VT.Tests;

/// <summary>
/// Pointer foreground and background colours (xterm's OSC 13 and 14, reset by OSC 113 and 114, #991). The App cannot recolour a native pointer,
/// so they are deliberately unsupported: all four are ignored whole, never answered, and change nothing the other colour OSCs own. These are
/// parser and buffer tests of that policy, not a measurement of the running App.
/// </summary>
public sealed class Osc13Osc14PointerColorTests
{
    private const string Esc = "\u001b";

    // What a program sets before the pointer sequences arrive: every colour resource a pointer sequence could be mistaken for, a pointer shape and some text.
    private const string Setup = Esc + "]10;#102030" + Esc + "\\" + Esc + "]11;#405060" + Esc + "\\" + Esc + "]12;#708090" + Esc + "\\" +
        Esc + "]17;#a0b0c0" + Esc + "\\" + Esc + "]19;#d0e0f0" + Esc + "\\" + Esc + "]4;9;#0a0b0c" + Esc + "\\" + Esc + "]5;1;#112233" + Esc + "\\" +
        Esc + "]22;text" + Esc + "\\" + "ABC";

    private static string Osc(string value, string terminator) => Esc + "]" + value + terminator;

    /// <summary>The terminal always answers a colour query with an ST-terminated frame, whatever terminated the request.</summary>
    private static string Reply(string value) => Esc + "]" + value + Esc + "\\";

    private static string Row(TerminalBuffer buffer)
    {
        buffer.Lock.EnterReadLock();
        try { return new string(Enumerable.Range(0, 7).Select(col => buffer.GetCell(col, 0).Character).ToArray()); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    /// <summary>The runtime colour resources as exported (what <c>TerminalStateAssert</c> leaves out), asserted non-empty so two empty states cannot compare equal.</summary>
    private static string Colors(TerminalBuffer buffer)
    {
        var colors = buffer.ExportRuntimeColors();
        Assert.NotNull(colors);
        Assert.NotNull(colors.Current.Foreground);
        return JsonSerializer.Serialize(colors);
    }

    private static void AssertSameState(string where, (TerminalBuffer Buffer, AnsiParser Parser, List<string> Replies, List<string?> Shapes) expected, (TerminalBuffer Buffer, AnsiParser Parser, List<string> Replies, List<string?> Shapes) actual)
    {
        TerminalStateAssert.AssertEquivalent(where, expected.Buffer, expected.Parser, actual.Buffer, actual.Parser);
        Assert.Equal(Colors(expected.Buffer), Colors(actual.Buffer)); // the colour overrides, the palette and the special colors
        Assert.Equal("ABCtail", Row(actual.Buffer));
    }

    /// <summary>A parser that has processed <see cref="Setup"/> and nothing else; replies and shape requests are collected from here on.</summary>
    private static (TerminalBuffer Buffer, AnsiParser Parser, List<string> Replies, List<string?> Shapes) Start()
    {
        var buffer = new TerminalBuffer(20, 5);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        var replies = new List<string>();
        var shapes = new List<string?>();
        parser.OnResponse = replies.Add;
        parser.OnPointerShapeRequested = (shape, _) => shapes.Add(shape);
        parser.Process(Setup);
        replies.Clear();
        shapes.Clear();
        return (buffer, parser, replies, shapes);
    }

    public static TheoryData<string, string> Cases
    {
        get
        {
            var cases = new TheoryData<string, string>();
            foreach (string terminator in new[] { "\u0007", Esc + "\\" })
                foreach (string code in new[] { "13", "14" })
                    foreach (string payload in new[] { ";?", ";rgb:11/22/33", ";#aabbcc", ";rgbi:1/0/0", ";", ";not-a-colour", ";?;?", "" })
                        cases.Add(code + payload, terminator);
            foreach (string terminator in new[] { "\u0007", Esc + "\\" })
                foreach (string code in new[] { "113", "114" })
                    foreach (string payload in new[] { "", ";", ";1", ";rgb:11/22/33", ";?" })
                        cases.Add(code + payload, terminator);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_pointer_colour_sequence_is_not_answered_and_leaves_the_state_as_the_same_stream_without_it(string sequence, string terminator)
    {
        var without = Start();
        without.Parser.Process("tail");
        var with = Start();
        with.Parser.Process(Osc(sequence, terminator) + "tail");

        Assert.Empty(with.Replies);   // never a reply, not even to a query
        Assert.Empty(with.Shapes);    // the pointer shape (OSC 22) is a different resource
        AssertSameState(sequence, without, with);
    }

    [Theory]
    [InlineData("13")]
    [InlineData("14")]
    [InlineData("113")]
    [InlineData("114")]
    public void The_other_colour_resources_still_answer_after_a_pointer_sequence_and_keep_what_was_set(string code)
    {
        var (_, parser, replies, _) = Start();
        string payload = code.Length == 2 ? code + ";?" : code;
        parser.Process(Osc(payload, "\u0007") + Osc("10;?", "\u0007") + Osc("11;?", "\u0007") + Osc("12;?", "\u0007") + Osc("17;?", "\u0007") + Osc("19;?", "\u0007") +
            Osc("4;9;?", "\u0007") + Osc("5;1;?", "\u0007"));

        Assert.Equal(new[]
        {
            Reply("10;rgb:1010/2020/3030"),
            Reply("11;rgb:4040/5050/6060"),
            Reply("12;rgb:7070/8080/9090"),
            Reply("17;rgb:a0a0/b0b0/c0c0"),
            Reply("19;rgb:d0d0/e0e0/f0f0"),
            Reply("4;9;rgb:0a0a/0b0b/0c0c"),
            Reply("5;1;rgb:1111/2222/3333"),
        }, replies);
    }

    [Fact]
    public void The_neighbouring_reset_still_resets_so_the_pointer_resets_are_not_hiding_behind_a_dead_path()
    {
        // Positive control: a reset that is supported does change the state the tests above leave alone.
        var (_, parser, replies, _) = Start();
        parser.Process(Osc("113", "\u0007") + Osc("114", "\u0007") + Osc("10;?", "\u0007"));
        Assert.Equal(new[] { Reply("10;rgb:1010/2020/3030") }, replies);

        replies.Clear();
        parser.Process(Osc("110", "\u0007") + Osc("10;?", "\u0007"));
        Assert.Single(replies);
        Assert.NotEqual(Reply("10;rgb:1010/2020/3030"), replies[0]);
    }

    [Fact]
    public void In_a_chain_the_pointer_slots_take_their_values_and_stay_silent_so_the_later_slots_do_not_shift()
    {
        // xterm continues OSC 10;a;b;... through 11, 12, 13 (pointer foreground), 14 (pointer background), 15, 16, 17, 18, 19: a value
        // given for a slot Ntilde ignores must still be consumed by it.
        var buffer = new TerminalBuffer(20, 5);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process(Osc("10;#010101;#020202;#030303;#040404;#050505;#060606;#070707;#080808;#090909;#0a0a0a", "\u0007"));
        parser.Process(Osc("10;?;?;?;?;?;?;?;?;?;?", "\u0007"));

        Assert.Equal(new[]
        {
            Reply("10;rgb:0101/0101/0101"),
            Reply("11;rgb:0202/0202/0202"),
            Reply("12;rgb:0303/0303/0303"),
            Reply("17;rgb:0808/0808/0808"),
            Reply("19;rgb:0a0a/0a0a/0a0a"),
        }, replies);
    }

    [Fact]
    public void A_query_chain_across_the_pointer_slots_replies_only_for_the_resources_that_exist()
    {
        var (_, parser, replies, _) = Start();
        parser.Process(Osc("12;?;?;?;?;?;?", "\u0007")); // 12, 13, 14, 15, 16, 17

        Assert.Equal(new[] { Reply("12;rgb:7070/8080/9090"), Reply("17;rgb:a0a0/b0b0/c0c0") }, replies);
    }

    [Fact]
    public void Pointer_colour_sequences_split_across_chunks_are_ignored_the_same_way()
    {
        var without = Start();
        without.Parser.Process("tail");
        var split = Start();
        foreach (char c in Reply("13;rgb:11/22/33") + Osc("14;?", Esc + "\\") + Osc("113", "\u0007") + Osc("114", Esc + "\\") + "tail")
            split.Parser.Process(c.ToString());

        Assert.Empty(split.Replies);
        Assert.Empty(split.Shapes);
        AssertSameState("split", without, split);
    }
}
