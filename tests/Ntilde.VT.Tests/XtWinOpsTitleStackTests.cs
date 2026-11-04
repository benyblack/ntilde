using System.Text;
using System.Text.Json.Nodes;
using Ntilde.VT;

namespace Ntilde.VT.Tests;

/// <summary>
/// XTWINOPS title stack: <c>CSI 22 ; Ps t</c> saves and <c>CSI 23 ; Ps t</c> restores the raw
/// window title and icon name (#830). Semantics follow xterm's <c>xtermPushTitle</c> /
/// <c>xtermPopTitle</c>; the deliberate differences (RIS clears it, direct slot access is
/// ignored) are pinned here too.
/// </summary>
public sealed class XtWinOpsTitleStackTests
{
    private const string Push = "\x1b[22;0t";
    private const string Pop = "\x1b[23;0t";

    private static (AnsiParser Parser, TerminalBuffer Buffer, List<string> Titles) NewParser()
    {
        var buffer = new TerminalBuffer(20, 4);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = null };
        var titles = new List<string>();
        parser.OnTitleChanged = titles.Add;
        return (parser, buffer, titles);
    }

    private static string Title(string text) => $"\x1b]2;{text}\a";

    [Theory]
    [InlineData("\x1b[22t", "\x1b[23t")]
    [InlineData("\x1b[22;0t", "\x1b[23;0t")]
    [InlineData("\x1b[22;t", "\x1b[23;t")]
    [InlineData("\x1b[22;2t", "\x1b[23;2t")]
    [InlineData("\x1b[22;0;0t", "\x1b[23;0;0t")]
    [InlineData("\u009b22;0t", "\u009b23;0t")]
    [Trait("Category", "Regression")]
    public void Pop_restores_the_exact_raw_title_that_was_pushed(string push, string pop)
    {
        var (parser, buffer, titles) = NewParser();
        parser.Process(Title("  shell: ~/Ω;dir  ") + push + Title("file.txt - VIM") + "A");
        Assert.Equal(new[] { "  shell: ~/Ω;dir  ", "file.txt - VIM" }, titles);

        parser.Process(pop + "B");
        Assert.Equal(new[] { "  shell: ~/Ω;dir  ", "file.txt - VIM", "  shell: ~/Ω;dir  " }, titles);
        Assert.Equal((0, 2), (buffer.CursorRow, buffer.CursorCol));
        Assert.Null(parser.ExportState().TitleStack);
    }

    [Fact]
    public void Nested_pushes_pop_in_reverse_order()
    {
        var (parser, _, titles) = NewParser();
        parser.Process(Title("one") + Push + Title("two") + Push + Title("three") + Push + Title("four"));
        titles.Clear();

        parser.Process(Pop + Pop + Pop);

        Assert.Equal(new[] { "three", "two", "one" }, titles);
    }

    [Fact]
    public void Pop_on_an_empty_stack_is_a_no_op_and_the_stack_still_works_afterwards()
    {
        var (parser, _, titles) = NewParser();
        parser.Process(Title("current") + Pop + "\x1b[23;2t" + "\x1b[23;1t");
        Assert.Equal(new[] { "current" }, titles);
        Assert.Equal("current", parser.ExportState().WindowTitle);

        parser.Process(Push + Title("next") + Pop + Pop);
        Assert.Equal(new[] { "current", "next", "current" }, titles);
    }

    [Fact]
    public void Push_before_any_title_saves_the_empty_default_and_pop_clears_to_the_host_fallback()
    {
        var (parser, _, titles) = NewParser();
        parser.Process(Push + Title("vim") + Pop);
        Assert.Equal(new[] { "vim", "" }, titles);
    }

    /// <summary>
    /// xterm keeps ten entries (MAX_SAVED_TITLES); its ring overwrites the oldest on the eleventh
    /// push, and the slot that would hold it is empty by the time it is popped. Pinned as: newest
    /// ten retained, oldest discarded, extra pops silent.
    /// </summary>
    [Fact]
    public void Overflow_keeps_the_newest_ten_entries_and_discards_the_oldest()
    {
        var (parser, _, titles) = NewParser();
        for (int i = 0; i <= AnsiParser.MaxSavedTitles; i++)
        {
            parser.Process(Title($"t{i}") + Push);
        }

        Assert.Equal(AnsiParser.MaxSavedTitles, parser.ExportState().TitleStack!.Count);
        Assert.Equal("t1", parser.ExportState().TitleStack![0].Title);
        parser.Process(Title("top"));
        titles.Clear();

        for (int i = 0; i <= AnsiParser.MaxSavedTitles; i++)
        {
            parser.Process(Pop);
        }

        Assert.Equal(Enumerable.Range(1, 10).Reverse().Select(i => $"t{i}"), titles);
        Assert.Null(parser.ExportState().TitleStack);
    }

    [Fact]
    public void Selector_one_saves_and_restores_only_the_icon_name_without_a_title_callback()
    {
        var (parser, _, titles) = NewParser();
        parser.Process("\x1b]1;saved-icon\a" + Title("saved-title") + "\x1b[22;1t");
        parser.Process("\x1b]0;new\a");
        Assert.Equal(("new", "new"), (parser.ExportState().WindowTitle, parser.ExportState().IconName));
        titles.Clear();

        parser.Process("\x1b[23;1t");

        Assert.Empty(titles);
        Assert.Equal(("new", "saved-icon"), (parser.ExportState().WindowTitle, parser.ExportState().IconName));
    }

    [Fact]
    public void Selector_two_saves_and_restores_only_the_window_title()
    {
        var (parser, _, titles) = NewParser();
        parser.Process("\x1b]1;icon-a\a" + Title("title-a") + "\x1b[22;2t" + "\x1b]0;b\a");
        titles.Clear();

        parser.Process("\x1b[23;2t");

        Assert.Equal(new[] { "title-a" }, titles);
        Assert.Equal(("title-a", "b"), (parser.ExportState().WindowTitle, parser.ExportState().IconName));
    }

    [Fact]
    public void Selector_zero_restores_both_halves()
    {
        var (parser, _, titles) = NewParser();
        parser.Process("\x1b]1;icon-a\a" + Title("title-a") + Push + "\x1b]0;b\a");
        titles.Clear();

        parser.Process(Pop);

        Assert.Equal(new[] { "title-a" }, titles);
        Assert.Equal(("title-a", "icon-a"), (parser.ExportState().WindowTitle, parser.ExportState().IconName));
    }

    [Fact]
    public void OSC_1_sets_only_the_icon_name_and_raises_no_title_callback()
    {
        var (parser, _, titles) = NewParser();
        parser.Process(Title("title") + "\x1b]1;  icon  \a");
        Assert.Equal(new[] { "title" }, titles);
        Assert.Equal(("title", "  icon  "), (parser.ExportState().WindowTitle, parser.ExportState().IconName));
    }

    /// <summary>
    /// A one-sided push copies the other half from the entry below (xterm's pop-time "try higher
    /// levels" lookup), and with nothing below that half is absent: restoring it changes nothing.
    /// </summary>
    [Fact]
    public void One_sided_push_takes_the_other_half_from_the_entry_below_or_leaves_it_absent()
    {
        var (parser, _, titles) = NewParser();
        parser.Process("\x1b]0;base\a" + "\x1b[22;1t" + Title("changed"));
        titles.Clear();
        parser.Process(Pop);
        Assert.Empty(titles); // icon-only entry with nothing below: the title half is absent
        Assert.Equal(("changed", "base"), (parser.ExportState().WindowTitle, parser.ExportState().IconName));

        parser.Process("\x1b]0;lower\a" + Push + "\x1b]1;upper-icon\a" + "\x1b[22;1t" + "\x1b]0;later\a");
        titles.Clear();
        parser.Process(Pop);
        Assert.Equal(new[] { "lower" }, titles);
        Assert.Equal(("lower", "upper-icon"), (parser.ExportState().WindowTitle, parser.ExportState().IconName));
        parser.Process(Pop);
        Assert.Equal(new[] { "lower", "lower" }, titles);
        Assert.Equal("lower", parser.ExportState().IconName);
    }

    [Theory]
    [InlineData("\x1b[22;3t")]
    [InlineData("\x1b[22;0;1t")]
    [InlineData("\x1b[22;0;0;0t")]
    [InlineData("\x1b[22:0t")]
    [InlineData("\x1b[22;0:1t")]
    [InlineData("\x1b[?22;0t")]
    [InlineData("\x1b[>22;0t")]
    [InlineData("\x1b[22;0 t")]
    [InlineData("\x1b[22;0$t")]
    public void Undefined_spellings_do_not_push(string push)
    {
        var (parser, _, titles) = NewParser();
        parser.Process(Title("a") + push + Title("b") + Pop);
        Assert.Equal(new[] { "a", "b" }, titles);
        Assert.Null(parser.ExportState().TitleStack);
    }

    [Theory]
    [InlineData("\x1b[23;3t")]
    [InlineData("\x1b[23;0;1t")]
    [InlineData("\x1b[23;0;0;0t")]
    [InlineData("\x1b[23:0t")]
    [InlineData("\x1b[?23;0t")]
    [InlineData("\x1b[23;0 t")]
    public void Undefined_spellings_do_not_pop(string pop)
    {
        var (parser, _, titles) = NewParser();
        parser.Process(Title("a") + Push + Title("b") + pop);
        Assert.Equal(new[] { "a", "b" }, titles);
        Assert.Single(parser.ExportState().TitleStack!);
    }

    [Fact]
    public void RIS_clears_the_stack_and_keeps_the_current_title_without_a_callback()
    {
        var (parser, _, titles) = NewParser();
        parser.Process(Title("a") + Push + Push + Title("b") + "\u001bc");
        Assert.Equal(new[] { "a", "b" }, titles);
        Assert.Null(parser.ExportState().TitleStack);
        Assert.Equal("b", parser.ExportState().WindowTitle);

        parser.Process(Pop + Push + Title("c") + Pop);
        Assert.Equal(new[] { "a", "b", "c", "b" }, titles);
    }

    [Fact]
    public void DECSTR_keeps_the_stack()
    {
        var (parser, _, titles) = NewParser();
        parser.Process(Title("a") + Push + Title("b") + "\x1b[!p" + Pop);
        Assert.Equal(new[] { "a", "b", "a" }, titles);
    }

    [Theory]
    [InlineData("\x1b[?1049h", "\x1b[?1049l")]
    [InlineData("\x1b[?1047h", "\x1b[?1047l")]
    [InlineData("\x1b[?47h", "\x1b[?47l")]
    public void The_stack_is_session_wide_across_alternate_screen_switches(string enter, string exit)
    {
        var (parser, _, titles) = NewParser();
        parser.Process(Title("shell") + Push + enter + Title("vim") + Push + Title("vim help"));
        parser.Process(Pop + exit);
        Assert.Equal(new[] { "shell", "vim", "vim help", "vim" }, titles);

        parser.Process(Pop);
        Assert.Equal("shell", titles[^1]);
    }

    [Fact]
    public void Over_long_titles_are_sent_in_full_but_not_retained_for_the_stack()
    {
        var (parser, _, titles) = NewParser();
        string longTitle = new('x', AnsiParser.MaxSavedTitleChars + 1);
        string limit = new('y', AnsiParser.MaxSavedTitleChars);
        parser.Process(Title(longTitle) + Push + Title("next") + Pop);
        Assert.Equal(new[] { longTitle, "next" }, titles);

        parser.Process(Title(limit) + Push + Title("next") + Pop);
        Assert.Equal(limit, titles[^1]);
    }

    [Fact]
    public void Every_input_split_restores_the_same_titles()
    {
        string input = Title("a") + Push + Title("b") + "\x1b[22;2t" + Title("c") + "\x1b[23;2t" + Pop + "Z";
        for (int split = 0; split <= input.Length; split++)
        {
            var (parser, buffer, titles) = NewParser();
            parser.Process(input[..split]);
            parser.Process(input[split..]);
            Assert.Equal(new[] { "a", "b", "c", "b", "a" }, titles);
            Assert.Equal((0, 1), (buffer.CursorRow, buffer.CursorCol));
        }
    }

    [Fact]
    public void Complete_snapshot_round_trip_carries_the_title_icon_and_stack()
    {
        var (source, sourceBuffer, _) = NewParser();
        source.Process("\x1b]1;icon-a\a" + Title("  title a  ") + Push + "\x1b]0;b\a" + "\x1b[22;2t" + Title("vim") + "\x1b[2");

        TerminalStateSnapshot snapshot = sourceBuffer.ExportState(0);
        snapshot.Parser = source.ExportState();
        snapshot = TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(snapshot));
        var (target, targetBuffer, titles) = NewParser();
        TerminalStateTransfer.Restore(targetBuffer, target, snapshot);
        Assert.Empty(titles); // import is silent
        Assert.Equal(source.ExportState().ToDebugString(), target.ExportState().ToDebugString());

        target.Process("3;2t" + Pop + Pop);
        Assert.Equal(new[] { "b", "  title a  " }, titles);
        Assert.Equal("icon-a", target.ExportState().IconName);
    }

    [Fact]
    public void Legacy_snapshots_without_title_state_import_the_empty_default()
    {
        var (source, sourceBuffer, _) = NewParser();
        source.Process(Title("daemon title") + Push);
        TerminalStateSnapshot snapshot = sourceBuffer.ExportState(0);
        snapshot.Parser = source.ExportState();
        var json = JsonNode.Parse(TerminalStateSerializer.ToBytes(snapshot))!;
        JsonObject parser = json["parser"]!.AsObject();
        Assert.True(parser.Remove("title") && parser.Remove("icon") && parser.Remove("title_stack"));
        TerminalStateSnapshot legacy = TerminalStateSerializer.FromBytes(Encoding.UTF8.GetBytes(json.ToJsonString()));
        Assert.Equal((string.Empty, string.Empty, null), (legacy.Parser!.WindowTitle, legacy.Parser.IconName, legacy.Parser.TitleStack));

        var (target, targetBuffer, titles) = NewParser();
        target.Process(Title("stale") + Push);
        TerminalStateTransfer.Restore(targetBuffer, target, legacy);
        Assert.Null(target.ExportState().TitleStack);
        target.Process(Pop + Push + Title("vim") + Pop);
        Assert.Equal(new[] { "stale", "vim", "" }, titles);
    }

    [Fact]
    public void Import_clamps_depth_and_length_like_the_live_path()
    {
        string tooLong = new('z', AnsiParser.MaxSavedTitleChars + 1);
        var state = new AnsiParser(new TerminalBuffer(20, 4)).ExportState();
        state.WindowTitle = tooLong;
        state.IconName = "icon";
        state.TitleStack = Enumerable.Range(0, 12)
            .Select(i => new SavedTitleEntry { Icon = null, Title = i == 11 ? tooLong : $"e{i}" })
            .ToList();

        var (parser, _, titles) = NewParser();
        parser.ImportState(state);
        AnsiParserState imported = parser.ExportState();

        Assert.Null(imported.WindowTitle);
        Assert.Equal(AnsiParser.MaxSavedTitles, imported.TitleStack!.Count);
        Assert.Equal("e2", imported.TitleStack[0].Title);
        Assert.Null(imported.TitleStack[^1].Title);

        parser.Process(Pop + Pop);
        Assert.Equal(new[] { "e10" }, titles);
    }

    [Fact]
    public void Import_rejects_a_null_stack_entry_before_any_change()
    {
        var state = new AnsiParser(new TerminalBuffer(20, 4)).ExportState();
        state.TitleStack = new List<SavedTitleEntry> { null! };
        var (parser, _, _) = NewParser();
        parser.Process(Title("kept") + Push);

        Assert.Throws<InvalidOperationException>(() => parser.ImportState(state));
        Assert.Single(parser.ExportState().TitleStack!);
    }
}
