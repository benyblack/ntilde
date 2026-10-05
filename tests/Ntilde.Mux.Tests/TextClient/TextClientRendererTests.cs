using System.Diagnostics;
using System.Text;
using Ntilde.Mux.TextClient;
using Ntilde.VT;
using Ntilde.VT.Export;

namespace Ntilde.Mux.Tests.TextClient;

public sealed class TextClientRendererTests
{
    private sealed class Session
    {
        public Session(int cols, int rows)
        {
            Buffer = new TerminalBuffer(cols, rows);
            Parser = new AnsiParser(Buffer, forceConPtyFiltering: false) { ImageDecoder = null };
        }

        public TerminalBuffer Buffer { get; }
        public AnsiParser Parser { get; }
        public void Feed(string text) => Parser.Process(text);
    }

    /// <summary>An independent terminal fed with everything the renderer wrote.</summary>
    private static TerminalBuffer Outer(string output, int cols, int rows)
    {
        var outer = new TerminalBuffer(cols, rows);
        new AnsiParser(outer, forceConPtyFiltering: false) { ImageDecoder = null }.Process(output);
        return outer;
    }

    private static string[] Rows(TerminalBuffer b) => TerminalExporter.GetVisibleRowTexts(b);

    /// <summary>GetCell requires the buffer's read lock.</summary>
    private static TerminalCell Cell(TerminalBuffer b, int col, int row)
    {
        b.Lock.EnterReadLock();
        try
        {
            return b.GetCell(col, row);
        }
        finally
        {
            b.Lock.ExitReadLock();
        }
    }

    [Fact]
    public void The_first_frame_reproduces_the_screen()
    {
        var s = new Session(80, 24);
        s.Feed("first line\r\n\x1b[1;31mred bold\x1b[0m and plain\r\n\x1b[38;2;1;2;3mtrue\x1b[0m");
        var renderer = new TextClientRenderer(s.Buffer);

        TerminalBuffer outer = Outer(renderer.Render(80, 24), 80, 24);

        Assert.Equal(Rows(s.Buffer), Rows(outer));
        TerminalCell red = Cell(outer, 0, 1);
        Assert.True(red.IsBold);
        Assert.True(red.IsPaletteForeground);
        Assert.Equal(1u, red.Fg);
    }

    [Fact]
    public void Only_changed_rows_are_repainted()
    {
        var s = new Session(80, 24);
        s.Feed("first line\r\nsecond line");
        var renderer = new TextClientRenderer(s.Buffer);
        string frame1 = renderer.Render(80, 24);

        s.Feed("\x1b[6;1Hsixth");
        string frame2 = renderer.Render(80, 24);

        Assert.DoesNotContain("\x1b[2J", frame2, StringComparison.Ordinal);
        Assert.Contains("\x1b[6;1H", frame2, StringComparison.Ordinal);
        Assert.DoesNotContain("first line", frame2, StringComparison.Ordinal);
        Assert.Equal(Rows(s.Buffer), Rows(Outer(frame1 + frame2, 80, 24)));
        Assert.Equal(string.Empty, renderer.Render(80, 24)); // nothing changed, nothing written
    }

    [Fact]
    public void An_inner_alt_screen_switch_repaints_fully_without_nesting_1049()
    {
        var s = new Session(80, 24);
        s.Feed("shell prompt $ ");
        var renderer = new TextClientRenderer(s.Buffer);
        string frames = renderer.Render(80, 24);

        s.Feed("\x1b[?1049h\x1b[Hvim screen");
        string toAlt = renderer.Render(80, 24);
        frames += toAlt;
        Assert.Contains("\x1b[2J", toAlt, StringComparison.Ordinal);
        Assert.DoesNotContain("1049", toAlt, StringComparison.Ordinal);
        Assert.Equal(Rows(s.Buffer), Rows(Outer(frames, 80, 24)));

        s.Feed("\x1b[?1049l");
        string back = renderer.Render(80, 24);
        frames += back;
        Assert.DoesNotContain("1049", back, StringComparison.Ordinal);
        Assert.Contains("shell prompt $", Rows(Outer(frames, 80, 24))[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_larger_session_is_clipped_with_a_status_line()
    {
        var s = new Session(120, 40);
        var text = new StringBuilder();
        for (int i = 0; i < 40; i++) text.Append("line ").Append(i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)).Append(i < 39 ? "\r\n" : "");
        s.Feed(text.ToString());
        s.Feed("\x1b[11;1H");                                  // cursor on row 10: the window stays at the top
        var renderer = new TextClientRenderer(s.Buffer);

        TerminalBuffer outer = Outer(renderer.Render(80, 24), 80, 24);

        Assert.Equal("line 00", Rows(outer)[0]);
        Assert.Equal("line 22", Rows(outer)[22]);
        Assert.StartsWith("session is 120x40, this terminal is 80x24", Rows(outer)[23], StringComparison.Ordinal);
    }

    [Fact]
    public void The_clip_window_follows_the_cursor()
    {
        var s = new Session(80, 40);
        var text = new StringBuilder();
        for (int i = 0; i < 40; i++) text.Append("row ").Append(i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)).Append(i < 39 ? "\r\n" : "");
        s.Feed(text.ToString());
        s.Feed("\x1b[36;1H");                                  // cursor row 35; 23 visible rows → top = 13
        var renderer = new TextClientRenderer(s.Buffer);

        TerminalBuffer outer = Outer(renderer.Render(80, 24), 80, 24);

        Assert.Equal("row 13", Rows(outer)[0]);
        Assert.Equal("row 35", Rows(outer)[22]);
    }

    [Fact]
    public void Read_only_always_shows_its_status_line()
    {
        var s = new Session(80, 24);
        var renderer = new TextClientRenderer(s.Buffer) { ReadOnly = true };

        TerminalBuffer outer = Outer(renderer.Render(80, 24), 80, 24);

        Assert.StartsWith("read-only", Rows(outer)[23], StringComparison.Ordinal);
        Assert.Contains("d to detach", Rows(outer)[23], StringComparison.Ordinal);
        Assert.DoesNotContain("resize", Rows(outer)[23], StringComparison.Ordinal); // the grids are equal: nothing to resize
    }

    [Fact]
    public void Modes_follow_the_buffer_and_are_emitted_only_on_change()
    {
        var s = new Session(80, 24);
        var renderer = new TextClientRenderer(s.Buffer);
        renderer.Render(80, 24);

        s.Feed("\x1b[?1h\x1b[?2004h\x1b[?25l");
        string on = renderer.Render(80, 24);
        s.Feed("x");
        string again = renderer.Render(80, 24);

        Assert.Contains("\x1b[?1h", on, StringComparison.Ordinal);
        Assert.Contains("\x1b[?2004h", on, StringComparison.Ordinal);
        Assert.Contains("\x1b[?25l", on, StringComparison.Ordinal);
        Assert.DoesNotContain("\x1b[?1h", again, StringComparison.Ordinal);
        Assert.DoesNotContain("\x1b[?2004h", again, StringComparison.Ordinal);
    }

    [Fact]
    public void Enter_turns_autowrap_off_and_leave_turns_it_on()
    {
        Assert.Contains("\x1b[?7l", TextClientRenderer.EnterSequence, StringComparison.Ordinal);
        Assert.Contains("\x1b[?7h", TextClientRenderer.LeaveSequence, StringComparison.Ordinal);
        Assert.EndsWith("\x1b[?1049l", TextClientRenderer.LeaveSequence, StringComparison.Ordinal);
    }

    /// <summary>
    /// .NET's Console init writes terminfo smkx (DECKPAM, ESC =) to a TTY: left set, keypad keys would
    /// reach the inner app as ESC O x. The model does not track the inner keypad mode, so the outer one
    /// is held numeric on entry and on exit, and an inner DECKPAM is never relayed.
    /// </summary>
    [Fact]
    public void The_outer_keypad_is_numeric_on_entry_and_exit_and_an_inner_DECKPAM_is_not_relayed()
    {
        Assert.StartsWith("\x1b[?1049h\x1b[?7l\x1b>", TextClientRenderer.EnterSequence, StringComparison.Ordinal);
        Assert.EndsWith("\x1b>\x1b[?7h\x1b[?1049l", TextClientRenderer.LeaveSequence, StringComparison.Ordinal);

        var s = new Session(80, 24);
        var renderer = new TextClientRenderer(s.Buffer);
        renderer.Render(80, 24);
        s.Feed("\x1b=keypad app");
        string frame = renderer.Render(80, 24);

        Assert.Contains("keypad app", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("\x1b=", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void Mouse_modes_are_mirrored_only_while_unclipped()
    {
        var s = new Session(80, 24);
        s.Feed("\x1b[?1000h\x1b[?1006h");
        var renderer = new TextClientRenderer(s.Buffer);

        string fits = renderer.Render(80, 24);
        string clipped = renderer.Render(60, 20);

        Assert.Contains("\x1b[?1000h", fits, StringComparison.Ordinal);
        Assert.Contains("\x1b[?1006h", fits, StringComparison.Ordinal);
        Assert.Contains("\x1b[?1000l", clipped, StringComparison.Ordinal);
        Assert.Contains("\x1b[?1006l", clipped, StringComparison.Ordinal);
    }

    [Fact]
    public void The_detach_hint_survives_read_only_and_oversized_at_80_columns()
    {
        var s = new Session(120, 40);
        var renderer = new TextClientRenderer(s.Buffer) { ReadOnly = true };

        string status = Rows(Outer(renderer.Render(80, 24), 80, 24))[23];

        Assert.StartsWith("read-only", status, StringComparison.Ordinal);
        Assert.EndsWith("Ctrl+\\ d to detach", status, StringComparison.Ordinal);
        Assert.Contains("…", status, StringComparison.Ordinal);            // the middle part was cut, not the hint
        Assert.True(status.Length <= 80, status);
    }

    [Fact]
    public void The_detach_hint_survives_an_oversized_session_at_60_columns()
    {
        var s = new Session(120, 40);
        var renderer = new TextClientRenderer(s.Buffer);

        string status = Rows(Outer(renderer.Render(60, 24), 60, 24))[23];

        Assert.StartsWith("session is 120x40", status, StringComparison.Ordinal);
        Assert.Contains("d to detach", status, StringComparison.Ordinal);
    }

    [Fact]
    public void A_console_narrower_than_the_hint_shows_as_much_of_the_hint_as_fits()
    {
        var s = new Session(120, 40);
        var renderer = new TextClientRenderer(s.Buffer);

        string status = Rows(Outer(renderer.Render(10, 24), 10, 24))[23];

        Assert.Equal("Ctrl+\\ d t", status);
    }

    [Fact]
    public void A_one_row_console_with_a_status_line_shows_only_the_status_line()
    {
        var s = new Session(80, 24);
        s.Feed("hello");
        var renderer = new TextClientRenderer(s.Buffer) { ReadOnly = true };
        string frames = renderer.Render(80, 1);

        s.Feed("\x1b[1;1Hchanged");                            // row 0 is dirty: it must not be painted over the status
        frames += renderer.Render(80, 1);

        string row = Rows(Outer(frames, 80, 1))[0];
        Assert.StartsWith("read-only", row, StringComparison.Ordinal);
        Assert.DoesNotContain("changed", row, StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_that_gets_shorter_is_erased_before_it_is_rewritten()
    {
        var s = new Session(80, 24);
        s.Feed("a rather long first line\r\nsecond");
        var renderer = new TextClientRenderer(s.Buffer);
        string frames = renderer.Render(80, 24);

        s.Feed("\x1b[1;1H\x1b[2Kshort");
        string frame = renderer.Render(80, 24);
        frames += frame;

        Assert.Contains("\x1b[2K", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("\x1b[2J", frame, StringComparison.Ordinal);
        Assert.Equal("short", Rows(Outer(frames, 80, 24))[0]);
        Assert.Equal(Rows(s.Buffer), Rows(Outer(frames, 80, 24)));
    }

    [Fact]
    public void A_full_scroll_with_the_cursor_at_the_bottom_is_reproduced()
    {
        var s = new Session(80, 24);
        var text = new StringBuilder();
        for (int i = 0; i < 24; i++) text.Append("line ").Append(i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)).Append(i < 23 ? "\r\n" : "");
        s.Feed(text.ToString());
        var renderer = new TextClientRenderer(s.Buffer);
        string frames = renderer.Render(80, 24);

        s.Feed("\r\nline 24\r\nline 25");
        frames += renderer.Render(80, 24);

        TerminalBuffer outer = Outer(frames, 80, 24);
        Assert.Equal(Rows(s.Buffer), Rows(outer));
        Assert.Equal("line 02", Rows(outer)[0]);
        Assert.Equal((s.Buffer.CursorRow, s.Buffer.CursorCol), (outer.CursorRow, outer.CursorCol));
    }

    [Fact]
    public void A_session_resize_forces_a_full_repaint()
    {
        var s = new Session(80, 24);
        s.Feed("some text on the first line");
        var renderer = new TextClientRenderer(s.Buffer);
        string frames = renderer.Render(80, 24);

        s.Buffer.Resize(60, 20);
        string frame = renderer.Render(80, 24);
        frames += frame;

        Assert.Contains("\x1b[2J", frame, StringComparison.Ordinal);
        string[] outer = Rows(Outer(frames, 80, 24));
        Assert.Equal(Rows(s.Buffer), outer[..20]);
        Assert.All(outer[20..], r => Assert.Equal(string.Empty, r));
    }

    [Fact]
    public void When_the_mismatch_ends_the_status_row_is_repainted_with_session_content()
    {
        var s = new Session(120, 40);
        var text = new StringBuilder();
        for (int i = 0; i < 40; i++) text.Append("line ").Append(i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)).Append(i < 39 ? "\r\n" : "");
        s.Feed(text.ToString());
        s.Feed("\x1b[11;1H");
        var renderer = new TextClientRenderer(s.Buffer);
        string frames = renderer.Render(80, 24);
        Assert.StartsWith("session is", Rows(Outer(frames, 120, 40))[23], StringComparison.Ordinal);

        frames += renderer.Render(120, 40);                    // the console grew to fit

        string[] outer = Rows(Outer(frames, 120, 40));
        Assert.Equal("line 23", outer[23]);
        Assert.Equal(Rows(s.Buffer), outer);
    }

    [Fact]
    public void A_session_smaller_than_the_console_leaves_nothing_stale_outside_its_grid()
    {
        var s = new Session(40, 10);
        s.Feed("inside");
        var renderer = new TextClientRenderer(s.Buffer);
        var junk = new StringBuilder();
        for (int r = 1; r <= 24; r++) junk.Append("\x1b[").Append(r).Append(";1H").Append(new string('#', 80));

        string[] outer = Rows(Outer(junk + renderer.Render(80, 24), 80, 24));

        Assert.Equal("inside", outer[0]);
        Assert.All(outer[1..], r => Assert.Equal(string.Empty, r));
    }

    [Fact]
    public void Invalidate_restates_every_mode_on_the_next_frame()
    {
        var s = new Session(80, 24);
        s.Feed("\x1b[?1h\x1b[?2004h\x1b[?1000h");
        var renderer = new TextClientRenderer(s.Buffer);
        renderer.Render(80, 24);

        renderer.Invalidate();
        string frame = renderer.Render(80, 24);

        Assert.Contains("\x1b[2J", frame, StringComparison.Ordinal);
        Assert.Contains("\x1b[?1h", frame, StringComparison.Ordinal);
        Assert.Contains("\x1b[?2004h", frame, StringComparison.Ordinal);
        Assert.Contains("\x1b[?1000h", frame, StringComparison.Ordinal);
        Assert.Contains("\x1b[?1003l", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cursor_is_hidden_during_an_incremental_paint()
    {
        var s = new Session(80, 24);
        var renderer = new TextClientRenderer(s.Buffer);
        renderer.Render(80, 24);

        s.Feed("typed");
        string frame = renderer.Render(80, 24);

        int hide = frame.IndexOf("\x1b[?25l", StringComparison.Ordinal);
        int text = frame.IndexOf("typed", StringComparison.Ordinal);
        int show = frame.LastIndexOf("\x1b[?25h", StringComparison.Ordinal);
        Assert.True(hide >= 0 && hide < text && text < show, frame.Replace("\x1b", "ESC", StringComparison.Ordinal));
    }

    [Fact]
    public void Repaint_cost_for_a_redraw_storm_is_measured()
    {
        var s = new Session(200, 60);
        var renderer = new TextClientRenderer(s.Buffer);
        var rng = new Random(42);
        renderer.Render(200, 60);
        var sw = new Stopwatch();
        const int frames = 100;
        for (int f = 0; f < frames; f++)
        {
            var screen = new StringBuilder("\x1b[H");
            for (int r = 0; r < 60; r++)
            {
                screen.Append("\x1b[3").Append(rng.Next(8)).Append('m');
                for (int c = 0; c < 200; c++) screen.Append((char)('a' + rng.Next(26)));
                if (r < 59) screen.Append("\r\n");
            }

            s.Feed(screen.ToString());
            sw.Start();
            renderer.Render(200, 60);
            sw.Stop();
        }

        double avgMs = sw.Elapsed.TotalMilliseconds / frames;
        TestContext.Current.TestOutputHelper?.WriteLine($"[mux-phase3] text client full-screen repaint (200x60): {avgMs:F2} ms/frame");
        Assert.True(avgMs < 50, $"a full-screen repaint took {avgMs:F2} ms"); // a generous ceiling; the number is for the PR
    }
}
