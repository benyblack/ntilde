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

        // Rows are right-trimmed (a prompt's trailing space goes with the padding), so "$ " → "$".
        Assert.Equal("token is ghp_abcdefghijklmnopqrstuvwxyz0123456789\n$", text);
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
}
