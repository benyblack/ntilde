namespace Ntilde.VT.Tests;

// OSC 9 desktop notifications and OSC 9;4 progress (issue #271). Two shapes share the
// code: "9;<text>" raises OnDesktopNotification, "9;4;<state>[;<pct>]" raises
// OnProgressReported. The parser flattens/caps text and clamps the percentage; it never
// decides visibility — that is the App layer's call.
public class Osc9NotificationProgressTests
{
    private static (AnsiParser Parser, List<string> Notifications, List<(int State, int? Percent)> Progress) Create()
    {
        var buffer = new TerminalBuffer(cols: 10, rows: 2);
        var parser = new AnsiParser(buffer);
        var notifications = new List<string>();
        var progress = new List<(int State, int? Percent)>();
        parser.OnDesktopNotification += notifications.Add;
        parser.OnProgressReported += (state, percent) => progress.Add((state, percent));
        return (parser, notifications, progress);
    }

    [Fact]
    public void Osc9_text_raises_desktop_notification_with_the_text()
    {
        var (parser, notifications, progress) = Create();

        parser.Process("\x1b]9;Build finished\x07");

        Assert.Empty(progress);
        var notification = Assert.Single(notifications);
        Assert.Equal("Build finished", notification);
    }

    [Fact]
    public void Osc9_text_is_flattened_to_one_line_and_trimmed()
    {
        var (parser, notifications, _) = Create();

        parser.Process("\x1b]9;  line one\r\nline two  \x07");

        Assert.Equal("line one line two", Assert.Single(notifications));
    }

    [Fact]
    public void Osc9_surviving_control_characters_become_spaces()
    {
        var (parser, notifications, _) = Create();

        // BEL/0x9C/ESC terminate the sequence, but TAB, NEL and other controls reach
        // the payload and must not land in the toast verbatim.
        parser.Process("\x1b]9;a\tb\u0085c\u0000d\x07");

        Assert.Equal("a b c d", Assert.Single(notifications));
    }

    [Fact]
    public void Osc9_controls_only_payload_is_dropped_after_sanitization()
    {
        var (parser, notifications, progress) = Create();

        parser.Process("\x1b]9;\t\t\x07");
        parser.Process("\x1b]9;\u0085\u0000\x07");

        Assert.Empty(notifications);
        Assert.Empty(progress);
    }

    [Fact]
    public void Osc9_text_longer_than_the_cap_is_elided()
    {
        var (parser, notifications, _) = Create();

        string text = new('x', 500);
        parser.Process($"\x1b]9;{text}\x07");

        string elided = Assert.Single(notifications);
        Assert.Equal(200, elided.Length);
        Assert.EndsWith("…", elided, StringComparison.Ordinal);
        Assert.All(elided[..199], c => Assert.Equal('x', c));
    }

    [Fact]
    public void Osc9_with_empty_or_whitespace_text_is_dropped()
    {
        var (parser, notifications, progress) = Create();

        parser.Process("\x1b]9;\x07");
        parser.Process("\x1b]9;   \x07");

        Assert.Empty(notifications);
        Assert.Empty(progress);
    }

    [Fact]
    public void Osc9_progress_normal_state_with_percent()
    {
        var (parser, notifications, progress) = Create();

        parser.Process("\x1b]9;4;1;42\x07");

        Assert.Empty(notifications);
        Assert.Equal((1, 42), Assert.Single(progress));
    }

    [Fact]
    public void Osc9_progress_without_percent_reports_null()
    {
        var (parser, _, progress) = Create();

        parser.Process("\x1b]9;4;3\x07");

        Assert.Equal((3, (int?)null), Assert.Single(progress));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void Osc9_progress_error_paused_and_off_states_pass_through(int state)
    {
        var (parser, _, progress) = Create();

        parser.Process($"\x1b]9;4;{state};10\x07");

        Assert.Equal((state, 10), Assert.Single(progress));
    }

    [Fact]
    public void Osc9_progress_percent_out_of_range_is_clamped()
    {
        var (parser, _, progress) = Create();

        parser.Process("\x1b]9;4;1;250\x07");
        parser.Process("\x1b]9;4;1;-7\x07");

        Assert.Equal((1, 100), progress[0]);
        Assert.Equal((1, 0), progress[1]);
    }

    [Fact]
    public void Osc9_progress_with_unparseable_state_is_dropped()
    {
        var (parser, notifications, progress) = Create();

        // No state parameter at all: "4;" alone is not a progress payload.
        parser.Process("\x1b]9;4;\x07");
        // Non-numeric state.
        parser.Process("\x1b]9;4;x;50\x07");

        Assert.Empty(progress);
        Assert.Empty(notifications);
    }

    [Fact]
    public void Osc9_bare_four_without_semicolon_is_notification_text()
    {
        var (parser, notifications, progress) = Create();

        // ConEmu requires the state parameter, so "4" is just text (e.g. a program
        // notifying the literal string "4"). The "4;" prefix is what selects progress.
        parser.Process("\x1b]9;4\x07");

        Assert.Empty(progress);
        Assert.Equal("4", Assert.Single(notifications));
    }

    [Fact]
    public void Osc9_progress_with_unparseable_percent_reports_null()
    {
        var (parser, _, progress) = Create();

        parser.Process("\x1b]9;4;1;abc\x07");

        Assert.Equal((1, (int?)null), Assert.Single(progress));
    }

    [Fact]
    public void Osc9_both_forms_interleave_without_cross_talk()
    {
        var (parser, notifications, progress) = Create();

        parser.Process("\x1b]9;4;1;10\x07");
        parser.Process("\x1b]9;step one done\x07");
        parser.Process("\x1b]9;4;0\x07");

        Assert.Single(notifications);
        Assert.Equal((1, 10), progress[0]);
        Assert.Equal((0, (int?)null), progress[1]);
    }

    [Fact]
    public void Osc9_payloads_do_not_write_to_the_grid()
    {
        var buffer = new TerminalBuffer(cols: 10, rows: 2);
        var parser = new AnsiParser(buffer);

        parser.Process("\x1b]9;4;1;42\x07X");

        // Only the post-sequence literal "X" lands in cells; the OSC payload never does.
        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal('X', buffer.GetGrapheme(0, viewRow: 0)[0]);
            Assert.Equal(' ', buffer.GetGrapheme(1, viewRow: 0)[0]);
        }
        finally { buffer.Lock.ExitReadLock(); }
    }
}
