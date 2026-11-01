namespace Ntilde.VT.Tests;

public sealed class Osc66ResourceTests
{
    [Fact]
    public void Retained_metadata_has_a_budget_and_rejection_does_not_move_the_cursor()
    {
        var buffer = new TerminalBuffer(49, 7);
        var parser = new AnsiParser(buffer);
        string payload = new('x', 4096);
        string block = "\x1b]66;s=7:w=7;" + payload + "\a";
        for (int i = 0; i < 600; i++)
        {
            parser.Process(block);
            parser.Process("\x1b[7;1H\n\n\n\n\n\n\n\x1b[1;1H");
        }
        var state = buffer.ExportState(int.MaxValue);
        Assert.InRange(state.TextSizing!.Count, 1, 550);
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process("\x1b[1;49H" + block + "\x1b[6n");
        Assert.Equal("\x1b[1;49R", Assert.Single(replies));
        Assert.False(buffer.IsPendingWrap);
        // Clearing retained history makes that budget available again.
        parser.Process("\x1b[3J\x1b[1;1H" + block);
        Assert.Single(buffer.ExportState(int.MaxValue).TextSizing!);
    }

    [Fact]
    public void Oversized_transferred_metadata_is_rejected_before_import()
    {
        var buffer = new TerminalBuffer(49, 7);
        new AnsiParser(buffer).Process("\x1b]66;s=7:w=7;X\a");
        var state = buffer.ExportState(0);
        var block = Assert.Single(state.TextSizing!);
        state.TextSizing = Enumerable.Repeat(block, 131073).ToList();
        Assert.Throws<InvalidOperationException>(() => buffer.ImportState(state));
    }
}
