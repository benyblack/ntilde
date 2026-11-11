using Ntilde.AgentHost;
using Ntilde.AgentHost.Contracts;
using Ntilde.VT;

namespace Ntilde.AppTests.AgentHost;

public sealed class AgentPaneMetadataCaptureTests
{
    private static PaneBufferMetadata Read(TerminalBuffer buffer)
    {
        Assert.True(AgentPaneMetadataCapture.TryRead(buffer, out var value));
        return Assert.IsType<PaneBufferMetadata>(value);
    }

    [Fact]
    public void Icon_name_observation_is_parser_state_independent_of_title_and_screen()
    {
        var buffer = new TerminalBuffer(80, 4); var parser = new AnsiParser(buffer);
        parser.Process("\u001b]0;baseline\aMARK\u001b[22;1t\u001b]1;distinct-icon\a");
        Assert.True(AgentPaneMetadataCapture.TryRead(buffer, out var changed, parser));
        Assert.Equal("distinct-icon", changed!.RequestedIconName);
        parser.Process("\u001b]2;other-title\a");
        Assert.True(AgentPaneMetadataCapture.TryRead(buffer, out var titleOnly, parser));
        Assert.Equal("distinct-icon", titleOnly!.RequestedIconName);
        parser.Process("\u001b[23;1t");
        Assert.True(AgentPaneMetadataCapture.TryRead(buffer, out var restored, parser));
        Assert.Equal("baseline", restored!.RequestedIconName);
        Assert.Equal((0, 4), (restored.CursorRow, restored.CursorCol));
        Assert.Equal("distinct-icon", changed.RequestedIconName);
        Assert.Null(Read(buffer).RequestedIconName);
    }

    [Fact]
    public void Complete_link_runs_preserve_reference_identity_without_opening_any_target()
    {
        var buffer = new TerminalBuffer(80, 4); var parser = new AnsiParser(buffer);
        parser.Process("\u001b]8;id=shared;https://example.invalid/a\aAB\u001b]8;;\a \u001b]8;id=shared;https://example.invalid/a\aC\u001b]8;;\a ");
        parser.Process("\u001b]8;;https://example.invalid/a\aD\u001b]8;;\a\u001b]8;;https://example.invalid/a\aE\u001b]8;;\a");
        var value = Read(buffer);
        Assert.Equal(4, value.Links.Length);
        Assert.Equal(new[] { 0, 3, 5, 6 }, value.Links.Select(link => link.Col));
        Assert.Equal(new[] { 2, 1, 1, 1 }, value.Links.Select(link => link.Length));
        Assert.Equal(new[] { 0, 0, 1, 2 }, value.Links.Select(link => link.Identity));
        Assert.All(value.Links, link => Assert.Equal("https://example.invalid/a", link.Uri));
        parser.Process("\u001b[2J");
        Assert.Empty(Read(buffer).Links);
        Assert.Equal(4, value.Links.Length); // Detached result remains unchanged after erasure.
    }

    [Fact]
    public void Viewport_coordinates_exclude_history_links_and_keep_alternate_links_separate()
    {
        var buffer = new TerminalBuffer(80, 3); var parser = new AnsiParser(buffer);
        parser.Process("\u001b]8;;https://example.invalid/history\aH\u001b]8;;\a\r\none\r\ntwo\r\nthree\r\n\u001b[2J\u001b[H");
        parser.Process("\u001b]8;;https://example.invalid/main\aMAIN\u001b]8;;\a");
        var main = Read(buffer);
        Assert.True(main.ScrollbackLines > 0);
        var link = Assert.Single(main.Links);
        Assert.Equal((0, 0, 4, "https://example.invalid/main"), (link.Row, link.Col, link.Length, link.Uri));
        parser.Process("\u001b[?47h\u001b]8;;https://example.invalid/alt\aALT\u001b]8;;\a");
        Assert.True(Read(buffer).AlternateScreen);
        Assert.Equal("https://example.invalid/alt", Assert.Single(Read(buffer).Links).Uri);
        parser.Process("\u001b[?47l");
        Assert.Equal("https://example.invalid/main", Assert.Single(Read(buffer).Links).Uri);
    }

    [Fact]
    public void Parser_produced_prompt_anchor_and_input_window_are_read_with_their_history_generation()
    {
        var buffer = new TerminalBuffer(80, 4); var parser = new AnsiParser(buffer);
        parser.Process("\u001b[2;4H\u001b]133;B\a");
        var value = Read(buffer); var mark = Assert.IsType<PaneShellMark>(value.CommandStart);
        Assert.True(value.AcceptingCommandInput);
        Assert.Equal((1, 3, false), (mark.Row, mark.Col, mark.AlternateScreen));
        Assert.Equal(buffer.Scrollback.Generation, value.HistoryGeneration);
        Assert.Equal(value.HistoryGeneration, mark.Generation);
        Assert.Equal(buffer.Scrollback.TotalRowsEvicted, value.RowsEvicted);
        parser.Process("\u001b]133;C\a");
        Assert.False(Read(buffer).AcceptingCommandInput);
        parser.Process("\u001b]133;B\a\u001b]133;D;0\a");
        Assert.False(Read(buffer).AcceptingCommandInput);
    }

    [Fact]
    public void Too_many_runs_refuse_the_whole_snapshot()
    {
        var buffer = new TerminalBuffer(258, 1); var parser = new AnsiParser(buffer);
        string run = "\u001b]8;;https://example.invalid/\aX\u001b]8;;\a ";
        parser.Process(string.Concat(Enumerable.Repeat(run, 128)));
        Assert.Equal(128, Read(buffer).Links.Length);
        parser.Process(run);
        Assert.False(AgentPaneMetadataCapture.TryRead(buffer, out var value)); Assert.Null(value);
    }

    [Fact]
    public void Link_text_budget_refuses_the_whole_snapshot()
    {
        var buffer = new TerminalBuffer(80, 2); var parser = new AnsiParser(buffer);
        string uri = "https://example.invalid/" + new string('x', 2000);
        string run = "\u001b]8;;" + uri + "\aX\u001b]8;;\a ";
        parser.Process(string.Concat(Enumerable.Repeat(run, 16)));
        Assert.Equal(16, Read(buffer).Links.Length);
        parser.Process(run);
        Assert.False(AgentPaneMetadataCapture.TryRead(buffer, out var value)); Assert.Null(value);
    }

    [Fact]
    public void Maximum_grid_is_complete_and_accepted()
    {
        var value = Read(new TerminalBuffer(128, 64));
        Assert.Equal((64, 128), (value.Rows, value.Cols));
        Assert.Empty(value.Links);
    }

    [Theory]
    [InlineData(128, 65)]
    [InlineData(8193, 1)]
    public void Oversized_grids_do_not_return_partial_metadata(int cols, int rows)
    {
        var buffer = new TerminalBuffer(cols, rows);
        Assert.False(AgentPaneMetadataCapture.TryRead(buffer, out var value)); Assert.Null(value);
    }

    [Fact]
    public void Field_and_aggregate_text_limits_reject_truncation()
    {
        Assert.False(AgentPaneMetadataCapture.TextFits([new string('x', 4097)]));
        Assert.True(AgentPaneMetadataCapture.TextFits(Enumerable.Repeat(new string('x', 4096), 8)));
        Assert.False(AgentPaneMetadataCapture.TextFits(Enumerable.Repeat(new string('x', 4096), 9)));
    }

    [Fact]
    public void Busy_parser_does_not_queue_a_blocking_UI_read()
    {
        var buffer = new TerminalBuffer(80, 4);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var writer = new Thread(() =>
        {
            buffer.Lock.EnterWriteLock();
            try { entered.Set(); release.Wait(TestContext.Current.CancellationToken); }
            finally { buffer.Lock.ExitWriteLock(); }
        })
        { IsBackground = true };
        writer.Start(); entered.Wait(TestContext.Current.CancellationToken);
        try
        {
            Assert.False(AgentPaneMetadataCapture.TryRead(buffer, out var value));
            Assert.Null(value); Assert.Equal(0, buffer.Lock.WaitingReadCount);
        }
        finally { release.Set(); Assert.True(writer.Join(TimeSpan.FromSeconds(5))); }
    }
}
