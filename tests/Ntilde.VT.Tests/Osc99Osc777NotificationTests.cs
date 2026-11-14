namespace Ntilde.VT.Tests;

// OSC 777 notify and kitty OSC 99 desktop notifications. The parser decodes, sanitizes and caps; showing is the
// App's call. The expectations below are written from the two protocols, not from the implementation.
public class Osc99Osc777NotificationTests
{
    private static (AnsiParser Parser, List<(string Title, string Body)> Notifications, List<string> Responses, List<string> Plain) Create()
    {
        var buffer = new TerminalBuffer(cols: 10, rows: 2);
        var parser = new AnsiParser(buffer);
        var notifications = new List<(string, string)>();
        var responses = new List<string>();
        var plain = new List<string>();
        parser.OnTitledDesktopNotification += (title, body) => notifications.Add((title, body));
        parser.OnDesktopNotification += plain.Add;
        parser.OnResponse = responses.Add;
        return (parser, notifications, responses, plain);
    }

    [Fact]
    public void Osc777_notify_carries_title_and_body()
    {
        var (parser, notifications, _, plain) = Create();

        parser.Process("\x1b]777;notify;Build;All green\x07");

        Assert.Equal(("Build", "All green"), Assert.Single(notifications));
        Assert.Empty(plain);
    }

    [Fact]
    public void Osc777_body_keeps_later_semicolons_and_title_only_is_allowed()
    {
        var (parser, notifications, _, _) = Create();

        parser.Process("\x1b]777;notify;T;a;b;c\x1b\\");
        parser.Process("\x1b]777;notify;Only a title\x07");

        Assert.Equal(new[] { ("T", "a;b;c"), ("Only a title", "") }, notifications.ToArray());
    }

    [Fact]
    public void Osc777_other_subcommands_and_empty_notifications_are_ignored()
    {
        var (parser, notifications, responses, _) = Create();

        parser.Process("\x1b]777;precmd\x07\x1b]777;preexec;ls\x07\x1b]777;notify;;\x07\x1b]777;notify;\t;\t\x07\x1b]777;\x07");

        Assert.Empty(notifications);
        Assert.Empty(responses);
    }

    [Fact]
    public void Osc777_text_is_flattened_and_capped()
    {
        var (parser, notifications, _, _) = Create();

        parser.Process("\x1b]777;notify;a\tb;" + new string('x', 250) + "\x07");

        var (title, body) = Assert.Single(notifications);
        Assert.Equal("a b", title);
        Assert.Equal(200, body.Length);
        Assert.EndsWith("…", body);
    }

    [Fact]
    public void Osc99_default_payload_is_a_title_shown_at_once()
    {
        var (parser, notifications, responses, _) = Create();

        parser.Process("\x1b]99;;Hello world\x1b\\");

        Assert.Equal(("Hello world", ""), Assert.Single(notifications));
        Assert.Empty(responses);
    }

    [Fact]
    public void Osc99_chunked_title_and_body_show_once_when_done()
    {
        var (parser, notifications, _, _) = Create();

        parser.Process("\x1b]99;i=1:d=0;Hello world\x1b\\");
        Assert.Empty(notifications);
        parser.Process("\x1b]99;i=1:p=body;This is cool\x1b\\");

        Assert.Equal(("Hello world", "This is cool"), Assert.Single(notifications));
    }

    [Fact]
    public void Osc99_chunks_concatenate_and_ids_are_independent()
    {
        var (parser, notifications, _, _) = Create();

        parser.Process("\x1b]99;i=a:d=0;Hel\x1b\\\x1b]99;i=b:d=0;Other\x1b\\\x1b]99;i=a;lo\x1b\\");
        Assert.Equal(("Hello", ""), Assert.Single(notifications));
        parser.Process("\x1b]99;i=b:p=body;B\x1b\\");

        Assert.Equal(new[] { ("Hello", ""), ("Other", "B") }, notifications.ToArray());
    }

    [Fact]
    public void Osc99_base64_payload_is_decoded_and_bad_base64_is_dropped()
    {
        var (parser, notifications, _, _) = Create();

        parser.Process("\x1b]99;e=1;SGVsbG8gYmFzZTY0\x1b\\");
        parser.Process("\x1b]99;e=1;***not base64***\x1b\\");
        parser.Process("\x1b]99;e=1;/w==\x1b\\"); // 0xFF, invalid UTF-8

        Assert.Equal(("Hello base64", ""), Assert.Single(notifications));
    }

    [Fact]
    public void Osc99_unsupported_payload_types_and_unknown_keys_do_not_notify_or_break_framing()
    {
        var (parser, notifications, responses, _) = Create();

        parser.Process("\x1b]99;i=1:p=close;\x1b\\\x1b]99;i=2:p=alive;\x1b\\\x1b]99;p=icon;AAAA\x1b\\\x1b]99;p=buttons;a\x1b\\\x1b]99;p=zzz;x\x1b\\");
        parser.Process("\x1b]99;u=2:z=9:a=report;Kept\x1b\\");

        Assert.Equal(("Kept", ""), Assert.Single(notifications));
        Assert.Empty(responses);
    }

    [Fact]
    public void Osc99_no_metadata_separator_is_not_a_notification()
    {
        var (parser, notifications, responses, _) = Create();

        parser.Process("\x1b]99\x07\x1b]99;onlyone\x07");

        Assert.Empty(notifications);
        Assert.Empty(responses);
    }

    [Fact]
    public void Osc99_query_replies_with_the_exact_capability_set_and_no_notification()
    {
        var (parser, notifications, responses, _) = Create();

        parser.Process("\x1b]99;i=abc-1.2:p=?;\x1b\\");

        Assert.Equal("\x1b]99;i=abc-1.2:p=?;o=always:p=title,body\x1b\\", Assert.Single(responses));
        Assert.Empty(notifications);
    }

    [Fact]
    public void Osc99_query_id_is_sanitized_before_it_is_echoed()
    {
        var (parser, _, responses, _) = Create();

        parser.Process("\x1b]99;i=a b!c/d:p=?;\x1b\\");
        parser.Process("\x1b]99;i=" + new string('z', 200) + ":p=?;\x1b\\");
        parser.Process("\x1b]99;p=?;\x1b\\");

        Assert.Equal("\x1b]99;i=abcd:p=?;o=always:p=title,body\x1b\\", responses[0]);
        Assert.Equal($"\x1b]99;i={new string('z', 64)}:p=?;o=always:p=title,body\x1b\\", responses[1]);
        Assert.Equal("\x1b]99;p=?;o=always:p=title,body\x1b\\", responses[2]);
    }

    [Fact]
    public void Osc99_pending_notifications_are_bounded()
    {
        var (parser, notifications, _, _) = Create();

        for (int i = 0; i < 12; i++) parser.Process($"\x1b]99;i=n{i}:d=0;T{i}\x1b\\");
        parser.Process("\x1b]99;i=n0;\x1b\\"); // evicted: finishing it shows nothing of the dropped chunk
        parser.Process("\x1b]99;i=n11;\x1b\\");
        parser.Process("\x1b]99;i=big:d=0;" + new string('x', 4096) + "\x1b\\");
        parser.Process("\x1b]99;i=big:d=0;" + new string('y', 4096) + "\x1b\\");
        parser.Process("\x1b]99;i=big:d=0;" + new string('z', 4096) + "\x1b\\");
        parser.Process("\x1b]99;i=big;tail\x1b\\");

        Assert.Equal(("T11", ""), Assert.Single(notifications));
    }

    [Fact]
    public void Ris_discards_an_unfinished_osc99_notification()
    {
        var (parser, notifications, _, _) = Create();

        parser.Process("\x1b]99;i=r:d=0;stale\x1b\\\u001bc");
        parser.Process("\x1b]99;i=r;fresh\x1b\\");

        Assert.Equal(("fresh", ""), Assert.Single(notifications));
    }

    [Fact]
    public void Osc99_ids_are_never_merged_by_trimming_or_sanitising()
    {
        var (parser, notifications, _, _) = Create();
        string shared = new string('a', 64);

        // Two ids sharing their first 64 characters are different notifications; an over-long or out-of-alphabet id is refused outright.
        parser.Process($"\x1b]99;i={shared}1:d=0;one\x1b\\\x1b]99;i={shared}2:d=0;two\x1b\\");
        parser.Process($"\x1b]99;i={shared}3;refused\x1b\\\x1b]99;i=a b;refused\x1b\\\x1b]99;i=a/b;refused\x1b\\");
        Assert.Empty(notifications);
        parser.Process($"\x1b]99;i={shared};x\x1b\\");
        Assert.Equal(("x", ""), Assert.Single(notifications));
    }

    [Fact]
    public void Osc99_final_icon_or_buttons_packet_completes_the_held_notification_and_adds_nothing()
    {
        var (parser, notifications, _, _) = Create();

        parser.Process("\x1b]99;i=ic:d=0;Held title\x1b\\\x1b]99;i=ic:d=0:p=icon;AAAA\x1b\\");
        Assert.Empty(notifications);
        parser.Process("\x1b]99;i=ic:p=icon;BBBB\x1b\\");
        Assert.Equal(("Held title", ""), Assert.Single(notifications));

        // The pending state is gone: a later notification reusing the id starts clean; a close or alive packet never completes one.
        parser.Process("\x1b]99;i=ic:d=0;Second\x1b\\\x1b]99;i=ic:p=close;\x1b\\\x1b]99;i=ic:p=alive;\x1b\\");
        Assert.Single(notifications);
        parser.Process("\x1b]99;i=ic:p=buttons;x\x1b\\");
        Assert.Equal(("Second", ""), notifications[1]);
    }

    [Fact]
    public void Importing_parser_state_discards_the_pending_osc99_notification_of_the_target()
    {
        var (target, notifications, _, _) = Create();
        var source = new AnsiParser(new TerminalBuffer(cols: 10, rows: 2));

        target.Process("\x1b]99;i=m:d=0;stale\x1b\\");
        target.ImportState(source.ExportState());
        target.Process("\x1b]99;i=m;fresh\x1b\\");

        Assert.Equal(("fresh", ""), Assert.Single(notifications));
    }

    [Fact]
    public void Osc99_oversized_single_chunk_is_dropped()
    {
        var (parser, notifications, _, _) = Create();

        parser.Process("\x1b]99;;" + new string('x', 4097) + "\x1b\\");

        Assert.Empty(notifications);
    }
}
