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
