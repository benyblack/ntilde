using System.Text.Json;
using Ntilde.VT.Tests.StateTransfer;

namespace Ntilde.VT.Tests;

/// <summary>
/// OSC 440 remains silent. Only bare OSC 555 requests a host visual flash. Neither OSC raises a bell or changes the buffer.
/// </summary>
public sealed class AudioAttentionOscPolicyTests
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
        parser.OnVisualFlash = () => events.Add("flash");
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
                // OSC 440 (audio): the pinned probe sends "440;bell.wav"; the others are a Windows path, a POSIX path, a URL, a query, an empty value and the bare code.
                "440;bell.wav", "440;C:\\Windows\\Media\\ding.wav", "440;/usr/share/sounds/alert.wav", "440;https://example.com/a.wav", "440;?", "440;", "440",
                // OSC 555 (flash): the pinned probe sends the bare "555"; the others carry an argument, a query and an empty value.
                "555", "555;", "555;1", "555;?", "555;flash",
            };
            foreach (string terminator in new[] { "\u0007", Esc + "\\" })
                foreach (string payload in payloads)
                    cases.Add(payload, terminator);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Only_bare_OSC_555_requests_a_visual_flash_and_all_forms_preserve_terminal_state(string sequence, string terminator)
    {
        var without = Start();
        without.Parser.Process("tail");
        var with = Start();
        with.Parser.Process(Osc(sequence, terminator) + "tail");

        Assert.Empty(with.Replies); // never a reply, not even to a query
        Assert.Equal(sequence == "555" ? new[] { "flash" } : Array.Empty<string>(), with.Events);
        TerminalStateAssert.AssertEquivalent(sequence, without.Buffer, without.Parser, with.Buffer, with.Parser);
        Assert.Equal(Colors(without.Buffer), Colors(with.Buffer));
        Assert.Equal("ABCtail", Row(with.Buffer));
    }

    [Theory]
    [InlineData("440;bell.wav")]
    [InlineData("440;?")]
    [InlineData("555")]
    [InlineData("555;?")]
    public void The_neighbouring_resources_still_answer_and_keep_what_was_set_after_an_audio_or_attention_sequence(string sequence)
    {
        var run = Start();
        run.Parser.Process(Osc(sequence, "\u0007") + Osc("10;?", "\u0007") + Osc("11;?", "\u0007") + Osc("12;?", "\u0007"));

        Assert.Equal(new[]
        {
            Osc("10;rgb:1010/2020/3030", Esc + "\\"),
            Osc("11;rgb:4040/5050/6060", Esc + "\\"),
            Osc("12;rgb:7070/8080/9090", Esc + "\\"),
        }, run.Replies);
        Assert.Equal(sequence == "555" ? new[] { "flash" } : Array.Empty<string>(), run.Events);
    }

    [Fact]
    public void A_plain_BEL_still_rings_so_the_silent_sequences_are_not_hiding_behind_a_dead_bell()
    {
        // Positive control: the attention signal a program can send is BEL itself, through the same collector the tests above leave without a "bell".
        var run = Start();
        run.Parser.Process("\u0007");
        Assert.Equal(new[] { "bell" }, run.Events);

        // An OSC that ends in BEL is terminated by it, not rung: the BEL after the payload is not a bell, for the supported OSC 2 or the ignored ones.
        run.Events.Clear();
        run.Parser.Process(Osc("2;t", "\u0007") + Osc("440;bell.wav", "\u0007") + Osc("555", "\u0007"));
        Assert.Equal(new[] { "title:t", "flash" }, run.Events);
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
    public void Split_sequences_deliver_only_the_completed_bare_flash()
    {
        var without = Start();
        without.Parser.Process("tail");
        var split = Start();
        foreach (char c in Osc("440;bell.wav", "\u0007") + Osc("555", Esc + "\\") + Osc("440;?", "\u0007") + Osc("555;1", Esc + "\\") + "tail")
            split.Parser.Process(c.ToString());

        Assert.Empty(split.Replies);
        Assert.Equal(new[] { "flash" }, split.Events);
        TerminalStateAssert.AssertEquivalent("split", without.Buffer, without.Parser, split.Buffer, split.Parser);
        Assert.Equal("ABCtail", Row(split.Buffer));
    }

    [Fact]
    public void Look_alike_longer_codes_are_ignored_and_silent_too()
    {
        // 4400, 5550 and 4401 are other codes sharing a prefix: also ignored and silent. (Same expected behaviour as the exact codes, so this is a guard against a
        // prefix match answering, not proof of exact matching.)
        var without = Start();
        without.Parser.Process("tail");
        var with = Start();
        with.Parser.Process(Osc("4400;x", "\u0007") + Osc("5550;x", "\u0007") + Osc("4401;?", "\u0007") + Osc("5550", "\u0007") + "tail");

        Assert.Empty(with.Replies);
        Assert.Empty(with.Events);
        TerminalStateAssert.AssertEquivalent("prefix", without.Buffer, without.Parser, with.Buffer, with.Parser);
    }
}
