using System.Globalization;
using System.Text;
using Ntilde.VT;

namespace Ntilde.VT.Tests;

/// <summary>
/// xterm's saved DEC private modes: XTSAVE (<c>CSI ? Pm s</c>) and XTRESTORE (<c>CSI ? Pm r</c>). The expectations are
/// written from xterm's control-sequence text (ctlseqs, patch 411: "Ps values are the same as for DECSET", "a one-level cache",
/// "specific settings can be saved and restored independently", "only those modes listed as parameters") and from the shape of
/// xterm's savemodes/restoremodes (one slot per mode, the mouse tracking selectors sharing one slot and the coordinate encodings
/// another, ?1048 being the cursor save). What the text leaves open (a restore with nothing saved, RIS, DECSTR, the alternate
/// screen modes) records Ntilde's choice and says so.
/// </summary>
public sealed class ModeSaveTests
{
    private const string Esc = "\u001b";

    private static (AnsiParser Parser, TerminalBuffer Buffer, List<string> Replies) Create(int cols = 10, int rows = 4)
    {
        var buffer = new TerminalBuffer(cols, rows);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        return (parser, buffer, replies);
    }

    private static string RowText(TerminalBuffer buffer, int row, int cols = 5)
    {
        var sb = new StringBuilder();
        buffer.Lock.EnterReadLock();
        try
        {
            for (int col = 0; col < cols; col++) sb.Append(buffer.GetGrapheme(col, row));
        }
        finally { buffer.Lock.ExitReadLock(); }
        return sb.ToString().TrimEnd('\0', ' ');
    }

    private static string Set(string modes) => Esc + "[?" + modes + "h";
    private static string Reset(string modes) => Esc + "[?" + modes + "l";
    private static string Save(string modes = "") => Esc + "[?" + modes + "s";
    private static string Restore(string modes = "") => Esc + "[?" + modes + "r";

    [Fact]
    public void A_saved_autowrap_is_restored_after_it_was_changed()
    {
        var (parser, buffer, replies) = Create();
        parser.Process(Save("7") + Reset("7"));
        Assert.False(buffer.Modes.IsAutoWrapMode);

        parser.Process(Restore("7"));

        Assert.True(buffer.Modes.IsAutoWrapMode);
        Assert.Empty(replies);
    }

    [Fact]
    public void A_saved_reset_value_is_restored_as_reset()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Reset("7") + Save("7") + Set("7"));
        Assert.True(buffer.Modes.IsAutoWrapMode);

        parser.Process(Restore("7"));

        Assert.False(buffer.Modes.IsAutoWrapMode);
    }

    [Fact]
    public void A_restored_autowrap_decides_whether_text_wraps_at_the_right_edge()
    {
        var (parser, buffer, _) = Create(cols: 5, rows: 3);
        parser.Process(Save("7") + Reset("7") + "abcdefg" + Restore("7") + Esc + "[2;1H" + "abcdefg");

        Assert.Equal("abcdg", RowText(buffer, 0));
        Assert.Equal("abcde", RowText(buffer, 1));
        Assert.Equal("fg", RowText(buffer, 2));
    }

    [Fact]
    public void Only_the_listed_modes_are_saved_and_restored()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Save("7") + Reset("7") + Set("25") + Reset("25") + Restore("7"));

        Assert.True(buffer.Modes.IsAutoWrapMode);
        Assert.False(buffer.Modes.IsCursorVisible);
    }

    [Fact]
    public void Several_modes_in_one_sequence_are_saved_and_restored_independently()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Set("1") + Save("1;7;25") + Reset("1;7;25") + Restore("1;25"));

        Assert.True(buffer.Modes.IsApplicationCursorKeys);
        Assert.True(buffer.Modes.IsCursorVisible);
        Assert.False(buffer.Modes.IsAutoWrapMode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(25)]
    [InlineData(45)]
    [InlineData(66)]
    [InlineData(69)]
    [InlineData(80)]
    [InlineData(1004)]
    [InlineData(1007)]
    [InlineData(2004)]
    public void Each_saved_mode_returns_to_its_saved_value_both_ways(int mode)
    {
        string text = mode.ToString(CultureInfo.InvariantCulture);
        foreach (bool initiallySet in new[] { false, true })
        {
            var (parser, _, replies) = Create();
            parser.Process(initiallySet ? Set(text) : Reset(text));
            parser.Process(Save(text));
            parser.Process(initiallySet ? Reset(text) : Set(text));
            replies.Clear();

            parser.Process(Restore(text));
            parser.Process(Esc + "[?" + mode + "$p");

            Assert.Equal(new[] { $"{Esc}[?{mode};{(initiallySet ? 1 : 2)}$y" }, replies);
        }
    }

    [Fact]
    public void A_restore_does_not_empty_its_slot_and_a_second_save_replaces_the_first()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Save("7") + Reset("7") + Restore("7") + Reset("7") + Restore("7"));
        Assert.True(buffer.Modes.IsAutoWrapMode);

        parser.Process(Reset("7") + Save("7") + Set("7") + Restore("7"));
        Assert.False(buffer.Modes.IsAutoWrapMode);
    }

    [Fact]
    public void A_restore_with_nothing_saved_leaves_the_mode_alone_Ntilde_policy()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Reset("7") + Set("1") + Restore("7;1;25"));

        Assert.False(buffer.Modes.IsAutoWrapMode);
        Assert.True(buffer.Modes.IsApplicationCursorKeys);
        Assert.True(buffer.Modes.IsCursorVisible);
    }

    [Fact]
    public void A_restored_origin_mode_homes_the_cursor_as_DECSET_does()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Save("6") + Set("6") + Esc + "[3;3H" + Restore("6"));

        Assert.False(buffer.Modes.IsOriginMode);
        Assert.Equal((0, 0), (buffer.CursorCol, buffer.CursorRow));
    }

    [Fact]
    public void The_mouse_tracking_selectors_share_one_slot()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Set("1002") + Save("1000") + Reset("1002") + Set("1003"));
        Assert.True(buffer.Modes.MouseModeAnyEvent);

        parser.Process(Restore("1003"));

        Assert.True(buffer.Modes.MouseModeButtonEvent);
        Assert.False(buffer.Modes.MouseModeAnyEvent);
        Assert.False(buffer.Modes.MouseModeX10);
    }

    [Fact]
    public void A_saved_absence_of_tracking_switches_tracking_off()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Save("1000") + Set("1003") + Restore("1002"));

        Assert.Equal(0, buffer.Modes.MouseTrackingSelector);
    }

    [Fact]
    public void The_x10_slot_is_its_own_and_keeps_the_tracking_selector()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Set("1003") + Save("9") + Set("1000"));
        parser.Process(Restore("1000"));
        Assert.Equal(1000, buffer.Modes.MouseTrackingSelector);

        parser.Process(Restore("9"));
        Assert.Equal(1003, buffer.Modes.MouseTrackingSelector);
    }

    [Fact]
    public void The_coordinate_encodings_share_one_slot_independent_of_tracking()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Set("1006") + Set("1000") + Save("1015") + Set("1005") + Reset("1000"));
        Assert.True(buffer.Modes.MouseModeUtf8);

        parser.Process(Restore("1005"));

        Assert.True(buffer.Modes.MouseModeSGR);
        Assert.False(buffer.Modes.MouseModeUtf8);
        Assert.Equal(0, buffer.Modes.MouseTrackingSelector);
    }

    [Fact]
    public void The_pixel_encoding_shares_the_coordinate_encoding_slot()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Set("1016") + Save("1006") + Set("1006"));
        Assert.True(buffer.Modes.MouseModeSGR);
        Assert.False(buffer.Modes.MouseModePixel);

        parser.Process(Restore("1015"));

        Assert.True(buffer.Modes.MouseModePixel);
        Assert.False(buffer.Modes.MouseModeSGR);
        Assert.Equal(1016, buffer.Modes.MouseEncodingSelector);
    }

    [Fact]
    public void Mode_1048_saves_and_restores_the_cursor_like_DECSC_and_DECRC()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Esc + "[2;4H" + Save("1048") + Esc + "[1;1H" + Restore("1048"));

        Assert.Equal((3, 1), (buffer.CursorCol, buffer.CursorRow));
    }

    [Fact]
    public void A_bare_save_or_restore_and_unknown_modes_do_nothing()
    {
        var (parser, buffer, replies) = Create();
        parser.Process(Esc + "[2;4H" + Save() + Set("7") + Save("0;4242;1049") + Reset("7") + Restore() + Restore("0;4242;1049"));

        Assert.False(buffer.Modes.IsAutoWrapMode);
        Assert.Equal((3, 1), (buffer.CursorCol, buffer.CursorRow));
        Assert.Empty(replies);
    }

    [Fact]
    public void The_alternate_screen_modes_are_not_saved_Ntilde_policy()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Save("47;1047;1049") + Set("1049") + Restore("47;1047;1049"));

        Assert.True(buffer.IsAltScreenActive);
    }

    [Fact]
    public void Only_the_exact_spelling_is_a_mode_save_or_restore()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Esc + "[?7:1s" + Reset("7") + Esc + "[?7:1r");
        Assert.False(buffer.Modes.IsAutoWrapMode);

        parser.Process(Save("7") + Set("7") + Esc + "[?7 r" + Esc + "[>7r" + Esc + "[=7r" + Esc + "[?7$r");
        Assert.True(buffer.Modes.IsAutoWrapMode);
        parser.Process(Restore("7"));
        Assert.False(buffer.Modes.IsAutoWrapMode);
    }

    [Fact]
    public void The_bare_forms_keep_their_own_meaning()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Esc + "[2;4H" + Esc + "[s" + Esc + "[1;1H" + Esc + "[u");
        Assert.Equal((3, 1), (buffer.CursorCol, buffer.CursorRow));

        parser.Process(Esc + "[2;3r");
        Assert.Equal((1, 2), (buffer.ScrollTop, buffer.ScrollBottom));
    }

    [Fact]
    public void Saved_modes_are_shared_by_both_screens_and_RIS_empties_them_Ntilde_policy()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Save("7") + Set("1049") + Reset("7") + Restore("7"));
        Assert.True(buffer.Modes.IsAutoWrapMode);

        parser.Process(Reset("7") + Esc + "c" + Reset("7") + Restore("7"));
        Assert.False(buffer.Modes.IsAutoWrapMode);
    }

    [Fact]
    public void DECSTR_leaves_the_saved_modes_Ntilde_policy()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Reset("7") + Save("7") + Set("7") + Esc + "[!p" + Restore("7"));

        Assert.False(buffer.Modes.IsAutoWrapMode);
    }

    [Fact]
    public void Saved_modes_survive_a_state_export_and_import()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Reset("7") + Set("1002") + Save("7;1000;25") + Set("7") + Set("1003"));
        var state = buffer.ExportState(0);
        state.Parser = parser.ExportState();
        Assert.Equal(new[] { 7, 25, 1000 }, state.SavedModes!.Select(entry => entry.Mode).OrderBy(mode => mode).ToArray());

        var target = new TerminalBuffer(10, 4);
        var targetParser = new AnsiParser(target, forceConPtyFiltering: false);
        TerminalStateTransfer.Restore(target, targetParser, state);
        targetParser.Process(Restore("7;1000"));

        Assert.False(target.Modes.IsAutoWrapMode);
        Assert.Equal(1002, target.Modes.MouseTrackingSelector);
    }

    [Fact]
    public void Empty_slots_are_absent_from_the_export()
    {
        var (parser, buffer, _) = Create();
        parser.Process(Restore("7"));
        Assert.Null(buffer.ExportState(0).SavedModes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Invalid_saved_mode_snapshots_are_rejected_before_any_state_change(int kind)
    {
        var source = new TerminalBuffer(20, 4);
        var state = source.ExportState(0);
        state.SavedModes = kind switch
        {
            0 => new List<TerminalSavedModeEntry> { new() { Mode = 1002, Value = 1 } },
            1 => new List<TerminalSavedModeEntry> { new() { Mode = 7, Value = 2 } },
            2 => new List<TerminalSavedModeEntry> { new() { Mode = 7, Value = 1 }, new() { Mode = 7, Value = 0 } },
            3 => new List<TerminalSavedModeEntry> { new() { Mode = 1000, Value = 1001 } },
            _ => new List<TerminalSavedModeEntry> { new() { Mode = 1005, Value = 1000 } },
        };
        var target = new TerminalBuffer(10, 3);
        var parser = new AnsiParser(target);
        parser.Process(Save("7") + Reset("7"));

        Assert.Throws<InvalidOperationException>(() => TerminalStateTransfer.Restore(target, parser, state));
        Assert.Equal((10, 3), (target.Cols, target.Rows));
        parser.Process(Restore("7"));
        Assert.True(target.Modes.IsAutoWrapMode);
    }
}
