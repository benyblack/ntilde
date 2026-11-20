using System.Text.Json;
using Ntilde.VT.Tests.StateTransfer;

namespace Ntilde.VT.Tests;

/// <summary>
/// The terminal identity, locale and property OSCs (#996): foot's app-id (OSC 176), the terminal-property carrier (OSC 666), and the locale and version
/// queries (OSC 701, OSC 702). Ntilde has no honest value to report for any of them (no Wayland app-id on every platform, no locale of the child, no
/// property store) and nothing to mutate, so all four are deliberately unsupported: every form is ignored whole, never answered, and changes neither the
/// terminal state nor anything the host is told. These are parser and buffer tests of that policy, not a measurement of the running App.
/// </summary>
public sealed class IdentityPropertyOscPolicyTests
{
    private const string Esc = "\u001b";

    // What a program sets before the sequences arrive: a title, a working directory, a pointer shape, colours and some text, so a stray handler would have
    // something visible to clobber.
    private const string Setup = Esc + "]2;before" + Esc + "\\" + Esc + "]7;file://host/tmp/before" + Esc + "\\" + Esc + "]22;text" + Esc + "\\" +
        Esc + "]10;#102030" + Esc + "\\" + Esc + "]11;#405060" + Esc + "\\" + Esc + "]12;#708090" + Esc + "\\" + "ABC";

    private static string Osc(string value, string terminator) => Esc + "]" + value + terminator;

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

    private sealed record Run(TerminalBuffer Buffer, AnsiParser Parser, List<string> Replies, List<string> Events);

    /// <summary>A parser that has processed <see cref="Setup"/> and nothing else. Replies and every host notification are collected from here on.</summary>
    private static Run Start()
    {
        var buffer = new TerminalBuffer(20, 5);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        var replies = new List<string>();
        var events = new List<string>();
        parser.OnResponse = replies.Add;
        parser.OnBell = () => events.Add("bell");
        parser.OnTitleChanged = title => events.Add("title:" + title);
        parser.OnWorkingDirectoryChanged = dir => events.Add("cwd:" + dir);
        parser.OnDesktopNotification = text => events.Add("notification:" + text);
        parser.OnTitledDesktopNotification = (title, body) => events.Add("notification:" + title + "|" + body);
        parser.OnProgressReported = (state, percent) => events.Add("progress:" + state + "/" + percent);
        parser.OnClipboardWrite = (selection, _) => events.Add("clipboard:" + selection);
        parser.OnPointerShapeRequested = (shape, _) => events.Add("pointer:" + (shape ?? "(empty)"));
        parser.OnPointerShapeReset = () => events.Add("pointer:reset");
        parser.OnPromptReady = () => events.Add("prompt");
        parser.OnCommandStarted = _ => events.Add("command-started");
        parser.OnCommandFinished = _ => events.Add("command-finished");
        parser.OnCommandFinishedDetailed = (_, _) => events.Add("command-finished-detailed");
        parser.OnCommandAccepted = _ => events.Add("command-accepted");
        parser.OnKittyClipboardPacket = _ => events.Add("kitty-clipboard");
        parser.OnKittyClipboardReset = () => events.Add("kitty-clipboard-reset");
        parser.Process(Setup);
        Assert.Contains("title:before", events); // the collectors really are wired: a handler that fired would be seen
        replies.Clear();
        events.Clear();
        return new Run(buffer, parser, replies, events);
    }

    public static TheoryData<string, string> Cases
    {
        get
        {
            var cases = new TheoryData<string, string>();
            string[] payloads =
            {
                // OSC 176 (foot): set, query, unset, and the bare code; the pinned probe sends "176;terminfo-test".
                "176;terminfo-test", "176;com.example.app", "176;?", "176;", "176",
                // OSC 666: the pinned probe sends "666;test-prop=value"; the others are a query, a bare key and empty forms.
                "666;test-prop=value", "666;app-id=?", "666;app-id", "666;?", "666;", "666",
                // OSC 701 (locale): the pinned query, a set, an unset and the bare code.
                "701;?", "701;en_US.UTF-8", "701;", "701",
                // OSC 702 (version): the pinned bare form, a query, a value.
                "702", "702;?", "702;1.2.3",
            };
            foreach (string terminator in new[] { "\u0007", Esc + "\\" })
                foreach (string payload in payloads)
                    cases.Add(payload, terminator);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void An_identity_or_property_sequence_is_not_answered_and_leaves_the_state_and_the_host_as_the_same_stream_without_it(string sequence, string terminator)
    {
        var without = Start();
        without.Parser.Process("tail");
        var with = Start();
        with.Parser.Process(Osc(sequence, terminator) + "tail");

        Assert.Empty(with.Replies); // never a reply, not even to a query
        Assert.Empty(with.Events);  // no title, directory, notification, progress, clipboard, pointer, prompt or command event
        TerminalStateAssert.AssertEquivalent(sequence, without.Buffer, without.Parser, with.Buffer, with.Parser);
        Assert.Equal(Colors(without.Buffer), Colors(with.Buffer));
        Assert.Equal("ABCtail", Row(with.Buffer));
    }

    [Theory]
    [InlineData("176;?")]
    [InlineData("666;app-id=?")]
    [InlineData("701;?")]
    [InlineData("702")]
    public void The_neighbouring_resources_still_answer_and_keep_what_was_set_after_an_identity_query(string sequence)
    {
        var run = Start();
        run.Parser.Process(Osc(sequence, "\u0007") + Osc("10;?", "\u0007") + Osc("11;?", "\u0007") + Osc("12;?", "\u0007"));

        Assert.Equal(new[]
        {
            Osc("10;rgb:1010/2020/3030", Esc + "\\"),
            Osc("11;rgb:4040/5050/6060", Esc + "\\"),
            Osc("12;rgb:7070/8080/9090", Esc + "\\"),
        }, run.Replies);
        Assert.Empty(run.Events);
    }

    [Fact]
    public void XTVERSION_still_identifies_the_terminal_independently_of_the_unanswered_version_query()
    {
        // Positive control for 702: the version the terminal does report is XTVERSION's, a DCS reply of its own, not an OSC 702 frame.
        var run = Start();
        run.Parser.Process(Osc("702", "\u0007") + Esc + "[>q");

        string reply = Assert.Single(run.Replies);
        Assert.StartsWith(Esc + "P>|", reply);
        Assert.EndsWith(Esc + "\\", reply);
        Assert.DoesNotContain("]702", reply);
    }

    [Fact]
    public void A_title_sequence_still_reaches_the_host_so_the_empty_event_lists_above_are_not_a_dead_collector()
    {
        // Positive control: a supported OSC does fire its event through the same collector the tests above leave empty.
        var run = Start();
        run.Parser.Process(Osc("2;after", "\u0007"));
        Assert.Equal(new[] { "title:after" }, run.Events);
    }

    [Fact]
    public void Identity_sequences_split_across_chunks_are_ignored_the_same_way()
    {
        var without = Start();
        without.Parser.Process("tail");
        var split = Start();
        foreach (char c in Osc("176;terminfo-test", "\u0007") + Osc("666;test-prop=value", Esc + "\\") + Osc("701;?", "\u0007") + Osc("702", Esc + "\\") + "tail")
            split.Parser.Process(c.ToString());

        Assert.Empty(split.Replies);
        Assert.Empty(split.Events);
        TerminalStateAssert.AssertEquivalent("split", without.Buffer, without.Parser, split.Buffer, split.Parser);
        Assert.Equal("ABCtail", Row(split.Buffer));
    }

    [Fact]
    public void Look_alike_longer_codes_are_ignored_and_silent_too()
    {
        // 1760, 6660, 7010 and 7020 are other codes sharing a prefix: also ignored and silent. (Same expected behaviour as the exact codes, so this is a guard against a prefix match answering, not proof of exact matching.)
        var without = Start();
        without.Parser.Process("tail");
        var with = Start();
        with.Parser.Process(Osc("1760;x", "\u0007") + Osc("6660;x", "\u0007") + Osc("7010;?", "\u0007") + Osc("7020", "\u0007") + "tail");

        Assert.Empty(with.Replies);
        Assert.Empty(with.Events);
        TerminalStateAssert.AssertEquivalent("prefix", without.Buffer, without.Parser, with.Buffer, with.Parser);
    }
}
