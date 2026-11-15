using System.Globalization;
using Ntilde.VT;

namespace Ntilde.VT.Tests;

/// <summary>
/// xterm's video-attribute stack: XTPUSHSGR (<c>CSI Pm # {</c>) and XTPOPSGR (<c>CSI # }</c>). The expectations are
/// written from xterm's control-sequence text (ctlseqs, patch 411): the push parameters are 1 bold, 2 faint, 3 italic,
/// 4 underlined, 5 blink, 7 inverse, 8 invisible, 9 crossed-out, 21 doubly-underlined, 30 foreground, 31 background; no
/// parameter saves all of them; the stack is limited to ten levels; a pop restores the saved attributes. The text names
/// neither overflow, an empty pop, nor what the numbered forms do to attributes not listed, so those tests record
/// Ntilde's choices and say so.
/// </summary>
public sealed class SgrStackTests
{
    private const string Esc = "\u001b";

    private static (AnsiParser Parser, TerminalBuffer Buffer, List<string> Replies) Create(int cols = 20, int rows = 4)
    {
        var buffer = new TerminalBuffer(cols, rows);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        return (parser, buffer, replies);
    }

    /// <summary>The saved level count, read under the buffer's read lock as every read must be.</summary>
    private static int Depth(TerminalBuffer buffer)
    {
        buffer.Lock.EnterReadLock();
        try { return buffer.SgrStackDepth; }
        finally { buffer.Lock.ExitReadLock(); }
    }

    private static string Sgr(string parameters) => Esc + "[" + parameters + "m";
    private static string Push(string parameters = "") => Esc + "[" + parameters + "#{";
    private const string Pop = Esc + "[#}";

    [Fact]
    public void A_push_with_no_parameter_saves_every_attribute_and_a_pop_restores_them_all()
    {
        var (parser, buffer, replies) = Create();
        parser.Process(Sgr("1;2;3;4;5;7;8;9;38;2;10;20;30;48;5;99"));
        parser.Process(Push());
        parser.Process(Sgr("0"));
        Assert.False(buffer.IsBold);
        Assert.True(buffer.IsDefaultForeground);

        parser.Process(Pop);

        Assert.True(buffer.IsBold);
        Assert.True(buffer.IsFaint);
        Assert.True(buffer.IsItalic);
        Assert.True(buffer.IsUnderline);
        Assert.True(buffer.IsBlink);
        Assert.True(buffer.IsInverse);
        Assert.True(buffer.IsHidden);
        Assert.True(buffer.IsStrikethrough);
        Assert.False(buffer.IsDefaultForeground);
        Assert.Equal(-1, buffer.CurrentFgIndex);
        Assert.Equal(new TermColor(10, 20, 30), buffer.CurrentForeground);
        Assert.False(buffer.IsDefaultBackground);
        Assert.Equal(99, buffer.CurrentBgIndex);
        Assert.Empty(replies);
        Assert.Equal(0, Depth(buffer));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void A_numbered_push_restores_only_the_attribute_it_names(int selected)
    {
        var (parser, buffer, _) = Create();
        string all = "1;2;3;4;5;7;8;9";
        parser.Process(Push(selected.ToString(CultureInfo.InvariantCulture)));
        parser.Process(Sgr(all));
        parser.Process(Pop);

        bool[] expected = { false, false, false, false, false, false, false, false };
        int[] order = { 1, 2, 3, 4, 5, 7, 8, 9 };
        // Everything was off when the push saved the named attribute, so that one is off again; the rest stay as set.
        bool[] actual = { buffer.IsBold, buffer.IsFaint, buffer.IsItalic, buffer.IsUnderline, buffer.IsBlink, buffer.IsInverse, buffer.IsHidden, buffer.IsStrikethrough };
        for (int i = 0; i < order.Length; i++) expected[i] = order[i] != selected;
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void A_numbered_push_saves_the_attribute_as_it_was_not_as_it_became()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Sgr("1;3"));
        parser.Process(Push("1"));
        parser.Process(Sgr("22;23;4"));
        parser.Process(Pop);

        Assert.True(buffer.IsBold);   // saved, restored
        Assert.False(buffer.IsItalic); // not saved: keeps the value it had at the pop
        Assert.True(buffer.IsUnderline);
    }

    [Fact]
    public void Several_parameters_select_the_union_and_foreground_and_background_are_separate_choices()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Sgr("31;42;1;3"));
        parser.Process(Push("30;1"));
        parser.Process(Sgr("0;34;44;1;3"));
        parser.Process(Pop);

        Assert.True(buffer.IsBold);                  // 1 saved on
        Assert.True(buffer.IsItalic);                // 3 not selected: keeps its value
        Assert.Equal(1, buffer.CurrentFgIndex);      // 30 restored the foreground
        Assert.Equal(4, buffer.CurrentBgIndex);      // 31 not selected: the background keeps its value
    }

    [Fact]
    public void A_saved_default_color_is_restored_as_the_default_that_follows_the_theme()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Push("30;31"));
        parser.Process(Sgr("31;42"));
        Assert.False(buffer.IsDefaultForeground);

        parser.Process(Pop);

        Assert.True(buffer.IsDefaultForeground);
        Assert.True(buffer.IsDefaultBackground);
        Assert.Equal(-1, buffer.CurrentFgIndex);
        Assert.Equal(-1, buffer.CurrentBgIndex);
        Assert.Equal(buffer.Theme.Foreground, buffer.CurrentForeground);
    }

    [Fact]
    public void The_double_underline_values_save_and_restore_the_underline_pen()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Sgr("21"));
        parser.Process(Push("21"));
        parser.Process(Sgr("24"));
        parser.Process(Pop);
        Assert.True(buffer.IsUnderline);
        Assert.Equal(UnderlineStyle.Double, buffer.UnderlineStyle);

        parser.Process(Sgr("0"));
        parser.Process(Push("4"));
        parser.Process(Sgr("4:3"));
        parser.Process(Pop);
        Assert.False(buffer.IsUnderline);

        parser.Process(Sgr("4:3"));
        parser.Process(Push("4"));
        parser.Process(Sgr("0"));
        parser.Process(Pop);
        Assert.True(buffer.IsUnderline);
        Assert.Equal(UnderlineStyle.Curly, buffer.UnderlineStyle);
    }

    [Fact]
    public void The_underline_and_the_double_underline_are_restored_independently()
    {
        var (parser, buffer, _) = Create();

        // Only the underline was saved: a double underline set since stays, it was not selected.
        parser.Process(Push("4") + Sgr("21") + Pop);
        Assert.True(buffer.IsUnderline);
        Assert.Equal(UnderlineStyle.Double, buffer.UnderlineStyle);

        // Only the double underline was saved: a single underline set since stays.
        parser.Process(Sgr("0") + Push("21") + Sgr("4") + Pop);
        Assert.True(buffer.IsUnderline);
        Assert.Equal(UnderlineStyle.Single, buffer.UnderlineStyle);

        // A selected attribute takes its saved value: saved off, it is cleared.
        parser.Process(Sgr("0") + Push("4") + Sgr("4") + Pop);
        Assert.False(buffer.IsUnderline);
        parser.Process(Sgr("0") + Push("21") + Sgr("21") + Pop);
        Assert.False(buffer.IsUnderline);

        // Both selected: the saved underline comes back whole.
        parser.Process(Sgr("0;4") + Push("4;21") + Sgr("21") + Pop);
        Assert.True(buffer.IsUnderline);
        Assert.Equal(UnderlineStyle.Single, buffer.UnderlineStyle);
        parser.Process(Sgr("0;21") + Push() + Sgr("4") + Pop);
        Assert.Equal(UnderlineStyle.Double, buffer.UnderlineStyle);
    }

    [Fact]
    public void Both_underline_flags_at_once_resolve_to_the_double_underline_Ntilde_policy()
    {
        // xterm holds the underline and the double underline together; the pen holds one style.
        var (parser, buffer, _) = Create();
        parser.Process(Sgr("4") + Push("4") + Sgr("0;21") + Pop);
        Assert.Equal(UnderlineStyle.Double, buffer.UnderlineStyle);
        Assert.True(buffer.IsUnderline);
        parser.Process(Sgr("0;21") + Push("21") + Sgr("0;4") + Pop);
        Assert.Equal(UnderlineStyle.Double, buffer.UnderlineStyle);
    }

    [Fact]
    public void Every_parameter_selects_however_long_the_list()
    {
        var (parser, buffer, _) = Create();
        string many = string.Join(';', Enumerable.Repeat("1", 40)) + ";30";
        parser.Process(Sgr("1;31") + Push(many) + Sgr("0") + Pop);

        Assert.True(buffer.IsBold);
        Assert.Equal(1, buffer.CurrentFgIndex);
    }

    [Fact]
    public void Levels_come_back_last_in_first_out_and_ten_levels_hold()
    {
        var (parser, buffer, _) = Create();
        for (int level = 0; level < 10; level++) parser.Process(Sgr((30 + level % 8).ToString(CultureInfo.InvariantCulture)) + Push("30"));
        Assert.Equal(10, Depth(buffer));

        for (int level = 9; level >= 0; level--)
        {
            parser.Process(Sgr("39") + Pop);
            Assert.Equal(level % 8, buffer.CurrentFgIndex);
        }
        Assert.Equal(0, Depth(buffer));
    }

    [Fact]
    public void A_push_onto_a_full_stack_and_a_pop_of_an_empty_one_do_nothing_Ntilde_policy()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Sgr("1;31") + Pop);
        Assert.True(buffer.IsBold);
        Assert.Equal(1, buffer.CurrentFgIndex);

        for (int i = 0; i < 12; i++) parser.Process(Push("1"));
        Assert.Equal(10, Depth(buffer));
    }

    [Fact]
    public void A_value_the_text_does_not_list_still_saves_a_level_that_restores_nothing_Ntilde_policy()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Push("99"));
        Assert.Equal(1, Depth(buffer));
        parser.Process(Sgr("1;31") + Pop);

        Assert.Equal(0, Depth(buffer));
        Assert.True(buffer.IsBold);
        Assert.Equal(1, buffer.CurrentFgIndex);
    }

    [Fact]
    public void The_overline_the_underline_color_and_character_protection_are_not_part_of_the_stack()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Push());
        parser.Process(Sgr("53;58;5;77") + Esc + "[1\"q");
        parser.Process(Pop);

        Assert.True(buffer.IsOverline);
        Assert.Equal(77, buffer.UnderlineColorIndex);
        Assert.True(buffer.IsCharacterProtected);
    }

    [Theory]
    [InlineData("?", "#{")]
    [InlineData("1:2", "#{")]
    [InlineData(">", "#{")]
    [InlineData("1", "#}")]
    [InlineData("?", "#}")]
    [InlineData("", "{")]
    [InlineData("", "}")]
    [InlineData("", "$#{")]
    [InlineData("", "#$}")]
    public void Spellings_other_than_the_exact_one_never_push_or_pop(string parameters, string tail)
    {
        var (parser, buffer, replies) = Create();
        parser.Process(Push("1"));
        int before = Depth(buffer);
        parser.Process(Esc + "[" + parameters + tail);

        Assert.Equal(before, Depth(buffer));
        Assert.Empty(replies);
    }

    [Fact]
    public void The_neighbouring_final_bytes_keep_their_own_meaning()
    {
        var (parser, buffer, _) = Create(10, 3);
        parser.Process("abcdefghij" + Esc + "[1;1H" + Esc + "[1;1;1;3$z" + Esc + "[2'}");
        parser.Process(Push("1") + Esc + "[1;1;1;5${");
        Assert.Equal(1, Depth(buffer));
    }

    [Fact]
    public void The_stack_requests_do_not_touch_the_screen_cursor_or_modes()
    {
        var (parser, buffer, _) = Create();
        parser.Process("abc\r" + Push() + Push("1;2") + Pop + Pop);

        buffer.Lock.EnterReadLock();
        try { Assert.Equal("abc", new string(Enumerable.Range(0, 3).Select(col => buffer.GetCell(col, 0).Character).ToArray())); }
        finally { buffer.Lock.ExitReadLock(); }
        Assert.Equal((0, 0), (buffer.CursorRow, buffer.CursorCol));
    }

    [Fact]
    public void Text_written_after_a_pop_carries_the_restored_attributes()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Sgr("1;31") + Push() + Sgr("0") + "a" + Pop + "b");

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.False(buffer.GetCell(0, 0).IsBold);
            Assert.True(buffer.GetCell(1, 0).IsBold);
            Assert.Equal(1, buffer.GetCell(1, 0).FgIndex);
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void The_stack_is_shared_by_both_screens_and_RIS_empties_it_Ntilde_policy()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Sgr("1") + Push("1") + Esc + "[?1049h" + Sgr("22") + Pop);
        Assert.True(buffer.IsBold);

        parser.Process(Push("1"));
        parser.Process(Esc + "[?1049l");
        Assert.Equal(1, Depth(buffer));
        parser.Process(Esc + "c");
        Assert.Equal(0, Depth(buffer));
    }

    [Fact]
    public void The_stack_survives_a_state_export_and_import()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Sgr("1;38;2;1;2;3") + Push("1;30") + Sgr("0") + Push());
        var state = buffer.ExportState(0);
        state.Parser = parser.ExportState();
        Assert.Equal(2, state.SgrStack!.Count);

        var target = new TerminalBuffer(20, 4);
        var targetParser = new AnsiParser(target, forceConPtyFiltering: false);
        TerminalStateTransfer.Restore(target, targetParser, state);

        Assert.Equal(2, Depth(target));
        targetParser.Process(Sgr("31") + Pop);
        Assert.False(target.IsBold);
        targetParser.Process(Pop);
        Assert.True(target.IsBold);
        Assert.Equal(new TermColor(1, 2, 3), target.CurrentForeground);
    }

    [Fact]
    public void An_empty_stack_is_absent_from_the_export()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Push() + Pop);
        Assert.Null(buffer.ExportState(0).SgrStack);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Invalid_sgr_stack_snapshots_are_rejected_before_any_state_change(int kind)
    {
        var source = new TerminalBuffer(20, 4);
        var state = source.ExportState(0);
        state.SgrStack = kind switch
        {
            0 => Enumerable.Range(0, 11).Select(_ => new TerminalSgrStackEntry { Mask = 1 }).ToList(),
            1 => new List<TerminalSgrStackEntry> { new() { Mask = 1 << 11 } },
            2 => new List<TerminalSgrStackEntry> { new() { Mask = -1 } },
            3 => new List<TerminalSgrStackEntry> { new() { Mask = 1, Pen = new TerminalSgrState { UnderlineStyle = 9 } } },
            _ => new List<TerminalSgrStackEntry> { new() { Mask = 1, Pen = new TerminalSgrState { FgIndex = 300 } } },
        };
        var target = new TerminalBuffer(10, 3);
        var parser = new AnsiParser(target);
        parser.Process(Push("1"));

        Assert.Throws<InvalidOperationException>(() => TerminalStateTransfer.Restore(target, parser, state));
        Assert.Equal((10, 3), (target.Cols, target.Rows));
        Assert.Equal(1, Depth(target));
    }
}
