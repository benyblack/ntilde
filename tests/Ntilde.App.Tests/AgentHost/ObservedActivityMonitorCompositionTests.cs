using System;
using Ntilde.AgentHost;
using Ntilde.CommandAssist.Domain;
using Ntilde.Inference;
using Ntilde.VT;

namespace Ntilde.AppTests.AgentHost;

/// <summary>
/// The monitor's real dependencies. Screens must go through the screen-oriented filter, not the
/// command-history one, and the capture must hand the filter logical lines: a token the viewport
/// soft-wrapped across two rows is one string again before any pattern sees it.
/// </summary>
public class ObservedActivityMonitorCompositionTests
{
    [Fact]
    public void Default_screen_filter_is_the_screen_oriented_layer_over_the_history_filter()
    {
        ISecretsFilter filter = ObservedActivityMonitorComposition.CreateScreenSecretsFilter();

        Assert.IsType<ScreenSecretsFilter>(filter);
        // One pattern from each layer, through the composed instance.
        Assert.Equal("--password [REDACTED] DB_PASSWORD=[REDACTED]", filter.Redact("--password x DB_PASSWORD=y").RedactedText);
    }

    [Fact]
    public void JoinSoftWrappedRows_joins_wrapped_rows_and_keeps_hard_breaks()
    {
        string[] lines = ["token is ghp_abcdefghij", "klmnopqrstuvwxyz0123456789", "$ ", ""];
        bool[] wrapped = [true, false, false, false];

        string text = ObservedActivityMonitorComposition.JoinSoftWrappedRows(lines, wrapped);

        // Rows that end a logical line are right-trimmed (a prompt's trailing space goes), so "$ " → "$".
        Assert.Equal("token is ghp_abcdefghijklmnopqrstuvwxyz0123456789\n$", text);
    }

    [Fact]
    public void JoinSoftWrappedRows_keeps_the_trailing_spaces_of_a_row_that_continues()
    {
        // Rows arrive untrimmed; a wrapped row's trailing spaces are content the next row continues.
        string[] lines = ["PASSWORD=           ", "secret              ", "$                   "];
        bool[] wrapped = [true, false, false];

        string text = ObservedActivityMonitorComposition.JoinSoftWrappedRows(lines, wrapped);

        Assert.Equal("PASSWORD=           secret\n$", text);
    }

    [Fact]
    public void CaptureVisibleText_rejoins_a_token_after_a_wide_character_without_inserting_padding()
    {
        // "日" occupies two columns but is one string character, so string length is 19 for a
        // full 20-column row. Padding by string length would insert a space inside the token.
        var buffer = new TerminalBuffer(20, 6);
        var parser = new AnsiParser(buffer);
        const string token = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";
        parser.Process("日" + token + "\r\n$ ");
        var registration = new AgentSessionRegistration(
            paneId: Guid.NewGuid(),
            buffer: buffer,
            title: "pane",
            profileName: "Terminal",
            kind: "local",
            isActive: false);

        ScreenSample? sample = ObservedActivityMonitorComposition.CaptureVisibleText(registration);

        Assert.NotNull(sample);
        Assert.Equal("日" + token + "\n$", sample!.Text);
        string redacted = ObservedActivityMonitorComposition.CreateScreenSecretsFilter().Redact(sample.Text).RedactedText;
        Assert.Equal("日[REDACTED]\n$", redacted);
    }

    [Fact]
    public void CaptureVisibleText_does_not_join_after_a_default_erase_line_from_column_zero()
    {
        // CSI K (erase to end) from column 0 clears the whole continuation row without going
        // through the whole-row erase path; the wrap relationship must still be invalidated.
        var buffer = new TerminalBuffer(20, 6);
        var parser = new AnsiParser(buffer);
        parser.Process("abcdefghijklmnopqrstuvwxyz0123456789\r\n$ ");
        parser.Process("\x1b[2;1H\x1b[KPASSWORD=hunter2");
        var registration = new AgentSessionRegistration(
            paneId: Guid.NewGuid(),
            buffer: buffer,
            title: "pane",
            profileName: "Terminal",
            kind: "local",
            isActive: false);

        ScreenSample? sample = ObservedActivityMonitorComposition.CaptureVisibleText(registration);

        Assert.NotNull(sample);
        Assert.Equal("abcdefghijklmnopqrst\nPASSWORD=hunter2\n$", sample!.Text);
        string redacted = ObservedActivityMonitorComposition.CreateScreenSecretsFilter().Redact(sample.Text).RedactedText;
        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureVisibleText_keeps_a_wrap_whose_last_column_is_a_written_space()
    {
        // "PASSWORD=" + 11 spaces fills a 20-column row; "secret" lands on the next row. The wrap
        // is genuine even though the last cell is a space, so the rows must rejoin and the
        // assignment must be redacted.
        var buffer = new TerminalBuffer(20, 6);
        var parser = new AnsiParser(buffer);
        parser.Process("PASSWORD=" + new string(' ', 11) + "secret\r\n$ ");
        var registration = new AgentSessionRegistration(
            paneId: Guid.NewGuid(),
            buffer: buffer,
            title: "pane",
            profileName: "Terminal",
            kind: "local",
            isActive: false);

        ScreenSample? sample = ObservedActivityMonitorComposition.CaptureVisibleText(registration);

        Assert.NotNull(sample);
        Assert.Equal("PASSWORD=           secret\n$", sample!.Text);
        // The inner history filter's Password= pattern fires first and consumes the padding with
        // the value; what matters is that "secret" is gone.
        string redacted = ObservedActivityMonitorComposition.CreateScreenSecretsFilter().Redact(sample.Text).RedactedText;
        Assert.Equal("PASSWORD=[REDACTED]\n$", redacted);
        Assert.DoesNotContain("secret", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureVisibleText_rejoins_a_token_the_terminal_wrapped_so_the_filter_can_redact_it()
    {
        // 20 columns: the 40-char token below wraps across three rows.
        var buffer = new TerminalBuffer(20, 6);
        var parser = new AnsiParser(buffer);
        const string token = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";
        parser.Process("key " + token + "\r\n$ ");
        var registration = new AgentSessionRegistration(
            paneId: Guid.NewGuid(),
            buffer: buffer,
            title: "pane",
            profileName: "Terminal",
            kind: "local",
            isActive: false);

        ScreenSample? sample = ObservedActivityMonitorComposition.CaptureVisibleText(registration);

        Assert.NotNull(sample);
        Assert.Equal("key " + token + "\n$", sample!.Text);
        Assert.Equal(6, sample.Rows);
        Assert.Equal(20, sample.Cols);

        string redacted = ObservedActivityMonitorComposition.CreateScreenSecretsFilter().Redact(sample.Text).RedactedText;
        Assert.Equal("key [REDACTED]\n$", redacted);
    }

    [Fact]
    public void CaptureVisibleText_does_not_join_a_row_that_was_erased_and_repainted_shorter()
    {
        // Row 0 wraps into row 1 and row 1 into row 2. A cursor-addressed repaint then erases row 0
        // and writes "prefix" into it. Row 0 must not be glued to row 1: the token on row 1 would
        // otherwise lose its word boundary and slip past the filter.
        var buffer = new TerminalBuffer(20, 6);
        var parser = new AnsiParser(buffer);
        parser.Process("key ghp_abcdefghijklmnopqrstuvwxyz0123456789\r\n$ ");
        parser.Process("\x1b[1;1H\x1b[2Kprefix");
        var registration = new AgentSessionRegistration(
            paneId: Guid.NewGuid(),
            buffer: buffer,
            title: "pane",
            profileName: "Terminal",
            kind: "local",
            isActive: false);

        ScreenSample? sample = ObservedActivityMonitorComposition.CaptureVisibleText(registration);

        Assert.NotNull(sample);
        Assert.Equal("prefix\nmnopqrstuvwxyz0123456789\n$", sample!.Text);
    }

    [Fact]
    public void CaptureVisibleText_does_not_join_a_full_row_to_a_continuation_row_that_was_erased_and_repainted()
    {
        // The other direction: row 0 is a genuine full-width wrap, row 1 (its continuation) is
        // erased and a bare token is painted there. Row 0 must not be glued to the token.
        var buffer = new TerminalBuffer(20, 6);
        var parser = new AnsiParser(buffer);
        parser.Process("abcdefghijklmnopqrstuvwxyz0123456789\r\n$ ");
        parser.Process("\x1b[2;1H\x1b[2Kghp_abcdefghijklmnop");
        var registration = new AgentSessionRegistration(
            paneId: Guid.NewGuid(),
            buffer: buffer,
            title: "pane",
            profileName: "Terminal",
            kind: "local",
            isActive: false);

        ScreenSample? sample = ObservedActivityMonitorComposition.CaptureVisibleText(registration);

        Assert.NotNull(sample);
        Assert.Equal("abcdefghijklmnopqrst\nghp_abcdefghijklmnop\n$", sample!.Text);
    }
}
