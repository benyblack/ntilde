using Ntilde.VT;

namespace Ntilde.VT.Tests;

/// <summary>
/// The window/icon title reports (<c>CSI 20 t</c>, <c>CSI 21 t</c>), XTGETXRES and xterm's palette stack
/// (XTPUSHCOLORS, XTPOPCOLORS, XTREPORTCOLORS). The expectations are written from xterm's control-sequence text and
/// source (<c>report_win_label</c>, <c>do_dcs</c> case <c>Q</c>, <c>xtermPushColors</c>/<c>xtermPopColors</c>/
/// <c>xtermReportColors</c>), not from the implementation; the deliberate differences are named in each test.
/// </summary>
public sealed class DeviceReportTests
{
    private const string Esc = "\u001b";
    private const string TermNameHex = "7465726d4e616d65"; // "termName"

    private static (AnsiParser Parser, TerminalBuffer Buffer, List<string> Replies, List<string> Titles) Create(bool allowTitleReport = false, string? term = null)
    {
        var buffer = new TerminalBuffer(20, 4);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { AllowTitleReport = allowTitleReport, TerminalTypeName = term };
        var replies = new List<string>();
        var titles = new List<string>();
        parser.OnResponse = replies.Add;
        parser.OnTitleChanged = titles.Add;
        return (parser, buffer, replies, titles);
    }

    private static string Osc(string value) => Esc + "]" + value + Esc + "\\";

    // --- CSI 20 t / CSI 21 t -------------------------------------------------------------------------------------

    [Fact]
    public void Title_reports_are_silent_by_default()
    {
        var (parser, _, replies, _) = Create();
        Assert.False(parser.AllowTitleReport);

        parser.Process(Esc + "]2;secret\a" + Esc + "]1;icon\a" + Esc + "[21t" + Esc + "[20t");

        Assert.Empty(replies);
    }

    [Fact]
    public void Window_title_report_returns_the_title_the_stream_set_in_xterms_osc_form()
    {
        var (parser, _, replies, _) = Create(allowTitleReport: true);

        parser.Process(Esc + "]2;test-title\a" + Esc + "[21t");

        Assert.Equal(new[] { Esc + "]ltest-title" + Esc + "\\" }, replies);
    }

    [Fact]
    public void Icon_label_report_returns_the_icon_name_and_osc_zero_sets_both()
    {
        var (parser, _, replies, _) = Create(allowTitleReport: true);

        parser.Process(Esc + "]1;test-icon\a" + Esc + "[20t" + Esc + "[21t");
        parser.Process(Esc + "]0;both\a" + Esc + "[20t" + Esc + "[21t");

        Assert.Equal(new[]
        {
            Esc + "]Ltest-icon" + Esc + "\\", Esc + "]l" + Esc + "\\",
            Esc + "]Lboth" + Esc + "\\", Esc + "]lboth" + Esc + "\\",
        }, replies);
    }

    [Fact]
    public void An_unset_title_and_icon_report_as_empty_labels()
    {
        var (parser, _, replies, _) = Create(allowTitleReport: true);

        parser.Process(Esc + "[21t" + Esc + "[20t");

        Assert.Equal(new[] { Esc + "]l" + Esc + "\\", Esc + "]L" + Esc + "\\" }, replies);
    }

    [Fact]
    public void A_report_is_not_a_set_and_follows_the_title_stack()
    {
        var (parser, _, replies, titles) = Create(allowTitleReport: true);
        parser.Process(Esc + "]2;one\a" + Esc + "[22;0t" + Esc + "]2;two\a" + Esc + "[21t" + Esc + "[23;0t" + Esc + "[21t");
        titles.Clear();

        parser.Process(Esc + "[21t");

        Assert.Equal(new[] { Esc + "]ltwo" + Esc + "\\", Esc + "]lone" + Esc + "\\", Esc + "]lone" + Esc + "\\" }, replies);
        Assert.Empty(titles);
    }

    [Theory]
    [InlineData("a\tb")]
    [InlineData("a\u0001b")]
    [InlineData("a\u007fb")]
    [InlineData("a\u0085b")]
    [InlineData("a\u009bb")]
    public void A_label_holding_a_control_character_is_answered_empty_so_the_reply_cannot_carry_a_control(string title)
    {
        var (parser, _, replies, _) = Create(allowTitleReport: true);

        parser.Process(Esc + "]2;" + title + "\a" + Esc + "[21t");

        Assert.Equal(new[] { Esc + "]l" + Esc + "\\" }, replies);
    }

    [Fact]
    public void A_title_too_long_to_retain_is_answered_empty()
    {
        var (parser, _, replies, _) = Create(allowTitleReport: true);

        parser.Process(Esc + "]2;" + new string('x', AnsiParser.MaxSavedTitleChars + 1) + "\a" + Esc + "[21t");

        Assert.Equal(new[] { Esc + "]l" + Esc + "\\" }, replies);
    }

    [Theory]
    [InlineData("\x1b[21;1t")]
    [InlineData("\x1b[20;0t")]
    [InlineData("\x1b[?21t")]
    [InlineData("\x1b[>21t")]
    [InlineData("\x1b[21$t")]
    [InlineData("\x1b[21:1t")]
    [InlineData("\x1b[2 1t")]
    public void Only_the_exact_title_report_spellings_answer(string request)
    {
        var (parser, _, replies, _) = Create(allowTitleReport: true);
        parser.Process(Esc + "]2;title\a" + Esc + "]1;icon\a");

        parser.Process(request);

        Assert.Empty(replies);
    }

    // --- XTGETXRES -----------------------------------------------------------------------------------------------

    private static string Dcs(string body) => Esc + "P" + body + Esc + "\\";

    [Fact]
    public void Xtgetxres_answers_termName_with_the_session_term_as_uppercase_hex()
    {
        var (parser, _, replies, _) = Create(term: "xterm-256color");

        parser.Process(Dcs("+Q" + TermNameHex));

        Assert.Equal(new[] { Dcs("1+R" + TermNameHex + "=787465726D2D323536636F6C6F72") }, replies);
    }

    [Fact]
    public void Xtgetxres_echoes_the_name_as_the_request_spelled_it()
    {
        var (parser, _, replies, _) = Create(term: "xterm-256color");

        parser.Process(Dcs("+Q7465726D4E616D65"));

        Assert.Equal(new[] { Dcs("1+R7465726D4E616D65=787465726D2D323536636F6C6F72") }, replies);
    }

    [Fact]
    public void Xtgetxres_without_a_host_term_answers_termName_as_not_available()
    {
        var (parser, _, replies, _) = Create();

        parser.Process(Dcs("+Q" + TermNameHex));

        Assert.Equal(new[] { Dcs("0+R" + TermNameHex) }, replies);
    }

    [Theory]
    [InlineData("666f6f")]       // foo: not a resource here
    [InlineData("7465726d6e616d65")] // termname: resource names are case-sensitive
    [InlineData("5465726d4e616d65")] // TermName: the class name is not a resource instance
    public void Xtgetxres_refuses_other_resource_names(string hex)
    {
        var (parser, _, replies, _) = Create(term: "xterm-256color");

        parser.Process(Dcs("+Q" + hex));

        Assert.Equal(new[] { Dcs("0+R" + hex) }, replies);
    }

    [Fact]
    public void Xtgetxres_with_several_names_replies_once_and_stops_after_the_first_unknown_name()
    {
        var (parser, _, replies, _) = Create(term: "xterm-256color");

        parser.Process(Dcs("+Q" + TermNameHex + ";" + TermNameHex) + Dcs("+Q" + TermNameHex + ";666f6f;" + TermNameHex) + Dcs("+Q666f6f;" + TermNameHex));

        string value = "=787465726D2D323536636F6C6F72";
        Assert.Equal(new[]
        {
            Dcs("1+R" + TermNameHex + value + ";" + TermNameHex + value),
            Dcs("1+R" + TermNameHex + value + ";666f6f"),
            Dcs("0+R666f6f"),
        }, replies);
    }

    [Theory]
    [InlineData("")]
    [InlineData("7")]
    [InlineData("zz")]
    [InlineData("74;")]
    [InlineData(";74")]
    [InlineData("20")]          // a space is not a printable resource name character
    [InlineData("7f")]
    [InlineData("c3a9")]        // not ASCII
    public void A_malformed_xtgetxres_request_is_not_answered(string body)
    {
        var (parser, _, replies, _) = Create(term: "xterm-256color");

        parser.Process(Dcs("+Q" + body));

        Assert.Empty(replies);
    }

    [Fact]
    public void Xtgetxres_with_too_many_names_or_the_wrong_terminator_is_not_answered()
    {
        var (parser, _, replies, _) = Create(term: "xterm-256color");

        parser.Process(Dcs("+Q" + string.Join(";", Enumerable.Repeat(TermNameHex, AnsiParser.XtgetxresMaxNames + 1))));
        parser.Process(Esc + "P+Q" + TermNameHex + "\a");

        Assert.Empty(replies);
    }

    [Fact]
    public void Xtgetxres_and_xtgettcap_stay_separate_requests()
    {
        var (parser, _, replies, _) = Create(term: "xterm-256color");

        parser.Process(Dcs("+q544e"));

        Assert.Equal(new[] { Dcs("0+r") }, replies); // TN is still refused: the VT does not claim a terminfo name
    }

    // --- XTPUSHCOLORS / XTPOPCOLORS / XTREPORTCOLORS --------------------------------------------------------------

    private static string Report(AnsiParser parser, List<string> replies)
    {
        replies.Clear();
        parser.Process(Esc + "[#R");
        return Assert.Single(replies);
    }

    [Fact]
    public void An_empty_palette_stack_reports_zero_depth_and_zero_deepest()
    {
        var (parser, _, replies, _) = Create();

        Assert.Equal(Esc + "[?0;0#Q", Report(parser, replies));
    }

    [Fact]
    public void Push_and_pop_move_the_depth_and_the_deepest_never_shrinks()
    {
        var (parser, _, replies, _) = Create();

        parser.Process(Esc + "[#P");
        Assert.Equal(Esc + "[?1;1#Q", Report(parser, replies));
        parser.Process(Esc + "[#P");
        Assert.Equal(Esc + "[?2;2#Q", Report(parser, replies));
        parser.Process(Esc + "[#Q");
        Assert.Equal(Esc + "[?1;2#Q", Report(parser, replies));
        parser.Process(Esc + "[#Q" + Esc + "[#Q");
        Assert.Equal(Esc + "[?0;2#Q", Report(parser, replies));
    }

    [Theory]
    [InlineData("\x1b[#P", "\x1b[#Q", "\x1b[#R")]
    [InlineData("\x1b[0#P", "\x1b[0#Q", "\x1b[0#R")]
    public void Zero_and_omitted_parameters_mean_the_top(string push, string pop, string report)
    {
        var (parser, _, replies, _) = Create();

        parser.Process(push + push + pop + report);

        Assert.Equal(new[] { Esc + "[?1;2#Q" }, replies);
    }

    [Fact]
    public void Pop_restores_the_dynamic_colors_and_palette_that_were_pushed()
    {
        var (parser, buffer, _, _) = Create();
        parser.Process(Osc("11;#112233") + Osc("4;1;#445566") + Esc + "[#P");
        parser.Process(Osc("11;#aabbcc") + Osc("4;1;#ddeeff") + Osc("10;#010203"));

        parser.Process(Esc + "[#Q");

        var colors = buffer.ExportState(0).Colors!.Current;
        Assert.Equal(0xff112233u, colors.Background);
        Assert.Equal(0xff445566u, colors.Palette[1]);
        Assert.Null(colors.Foreground);
    }

    [Fact]
    public void A_pop_with_nothing_pushed_changes_neither_the_colors_nor_the_depth()
    {
        var (parser, buffer, replies, _) = Create();
        parser.Process(Osc("11;#112233") + Esc + "[#Q");

        Assert.Equal(0xff112233u, buffer.ExportState(0).Colors!.Current.Background);
        Assert.Equal(Esc + "[?0;0#Q", Report(parser, replies));
    }

    [Fact]
    public void Numbered_slots_store_and_restore_without_a_deepening_push()
    {
        var (parser, buffer, replies, _) = Create();
        parser.Process(Osc("11;#111111") + Esc + "[3#P");
        Assert.Equal(Esc + "[?3;0#Q", Report(parser, replies)); // xterm: the depth becomes the slot, the deepest is untouched

        parser.Process(Osc("11;#222222") + Esc + "[3#Q");

        Assert.Equal(0xff111111u, buffer.ExportState(0).Colors!.Current.Background);
        Assert.Equal(Esc + "[?2;0#Q", Report(parser, replies)); // xterm: a numbered restore sets the depth to slot - 1
    }

    [Fact]
    public void A_never_stored_slot_and_slots_past_ten_change_nothing()
    {
        var (parser, buffer, replies, _) = Create();
        parser.Process(Osc("11;#111111") + Esc + "[7#Q" + Esc + "[11#P" + Esc + "[11#Q" + Esc + "[99#P");

        Assert.Equal(0xff111111u, buffer.ExportState(0).Colors!.Current.Background);
        Assert.Equal(Esc + "[?0;0#Q", Report(parser, replies));
    }

    [Fact]
    public void The_stack_holds_ten_entries_and_an_eleventh_push_does_nothing()
    {
        var (parser, _, replies, _) = Create();
        for (int i = 0; i < 12; i++) parser.Process(Esc + "[#P");

        Assert.Equal(Esc + "[?10;10#Q", Report(parser, replies));
    }

    [Fact]
    public void A_parameter_list_applies_each_slot_in_order()
    {
        var (parser, _, replies, _) = Create();

        parser.Process(Esc + "[1;2;3#P");

        Assert.Equal(Esc + "[?3;0#Q", Report(parser, replies));
    }

    [Fact]
    public void The_xterm_stack_and_the_kitty_stack_are_independent()
    {
        var (parser, buffer, replies, _) = Create();
        parser.Process(Osc("11;#111111") + Osc("30001") + Osc("11;#222222") + Esc + "[#P" + Osc("11;#333333"));
        Assert.Equal(Esc + "[?1;1#Q", Report(parser, replies));

        parser.Process(Osc("30101"));
        Assert.Equal(0xff111111u, buffer.ExportState(0).Colors!.Current.Background);
        Assert.Equal(Esc + "[?1;1#Q", Report(parser, replies));

        parser.Process(Esc + "[#Q");
        Assert.Equal(0xff222222u, buffer.ExportState(0).Colors!.Current.Background);
        Assert.Empty(buffer.ExportState(0).Colors!.Stack);
    }

    [Fact]
    public void Color_changes_keep_the_xterm_stack()
    {
        var (parser, _, replies, _) = Create();
        parser.Process(Esc + "[#P" + Esc + "[#P" + Osc("11;#111111") + Osc("104") + Osc("30001") + Osc("30101"));

        Assert.Equal(Esc + "[?2;2#Q", Report(parser, replies));
    }

    [Fact]
    public void Full_reset_clears_the_xterm_stack()
    {
        var (parser, _, replies, _) = Create();
        parser.Process(Esc + "[#P" + Esc + "[#P" + Esc + "c");

        Assert.Equal(Esc + "[?0;0#Q", Report(parser, replies));
    }

    [Theory]
    [InlineData("\x1b[?#R")]
    [InlineData("\x1b[>#R")]
    [InlineData("\x1b[1#R")]
    [InlineData("\x1b[0;0#R")]
    [InlineData("\x1b[0:0#R")]
    [InlineData("\x1b[#$R")]
    [InlineData("\x1b[R")]
    public void Only_the_exact_report_spelling_answers(string request)
    {
        var (parser, _, replies, _) = Create();

        parser.Process(request);

        Assert.Empty(replies);
    }

    [Theory]
    [InlineData("\x1b[?#P")]
    [InlineData("\x1b[>#P")]
    [InlineData("\x1b[1:2#P")]
    [InlineData("\x1b[1:2#Q")]
    [InlineData("\x1b[?#Q")]
    [InlineData("\x1b[#$P")]
    public void Other_spellings_do_not_move_the_stack(string request)
    {
        var (parser, _, replies, _) = Create();
        parser.Process(request);

        Assert.Equal(Esc + "[?0;0#Q", Report(parser, replies));
    }

    [Fact]
    public void Push_is_not_delete_character_and_the_stack_requests_do_not_touch_the_screen()
    {
        var (parser, buffer, _, _) = Create();
        parser.Process("abc\r" + Esc + "[#P" + Esc + "[2#P" + Esc + "[#Q" + Esc + "[#R");

        buffer.Lock.EnterReadLock();
        try { Assert.Equal("abc", new string(Enumerable.Range(0, 3).Select(col => buffer.GetCell(col, 0).Character).ToArray())); }
        finally { buffer.Lock.ExitReadLock(); }
        Assert.Equal((0, 0), (buffer.CursorRow, buffer.CursorCol));
    }

    [Fact]
    public void The_xterm_stack_survives_a_state_export_and_import()
    {
        var (parser, buffer, _, _) = Create();
        parser.Process(Osc("11;#111111") + Esc + "[#P" + Osc("11;#222222") + Esc + "[#P" + Esc + "[#Q");
        var state = buffer.ExportState(0);
        state.Parser = parser.ExportState();

        var target = new TerminalBuffer(20, 4);
        var targetParser = new AnsiParser(target, forceConPtyFiltering: false);
        var replies = new List<string>();
        targetParser.OnResponse = replies.Add;
        TerminalStateTransfer.Restore(target, targetParser, state);

        Assert.Equal(Esc + "[?1;2#Q", Report(targetParser, replies));
        targetParser.Process(Esc + "[#Q");
        Assert.Equal(0xff111111u, target.ExportState(0).Colors!.Current.Background);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Invalid_xterm_stack_snapshots_are_rejected_before_any_state_change(int kind)
    {
        var source = new TerminalBuffer(20, 4);
        var state = source.ExportState(0);
        state.Colors = new TerminalRuntimeColors();
        switch (kind)
        {
            case 0: state.Colors.XtermSlots = new TerminalColorOverrides?[9]; break;
            case 1: state.Colors.XtermSlots = new TerminalColorOverrides?[10]; state.Colors.XtermUsed = 11; break;
            case 2: state.Colors.XtermSlots = new TerminalColorOverrides?[10]; state.Colors.XtermLast = -1; break;
            case 3: state.Colors.XtermUsed = 2; break;
            case 4:
                state.Colors.XtermSlots = new TerminalColorOverrides?[10];
                state.Colors.XtermSlots[0] = new TerminalColorOverrides { Palette = new uint?[3] };
                break;
        }
        var target = new TerminalBuffer(10, 3);
        var parser = new AnsiParser(target);
        parser.Process(Osc("10;#123456"));

        Assert.Throws<InvalidOperationException>(() => TerminalStateTransfer.Restore(target, parser, state));
        Assert.Equal((10, 3), (target.Cols, target.Rows));
        Assert.Equal(0xff123456u, target.ExportState(0).Colors!.Current.Foreground);
    }
}
