namespace Ntilde.VT.Tests;

public sealed class Osc66ProtocolTests
{
    [Fact]
    public void Support_probe_distinguishes_explicit_width_and_scale()
    {
        var buffer = new TerminalBuffer(40, 8);
        var parser = new AnsiParser(buffer);
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process("\u001b[1;1H\u001b[2K\r\u001b[6n\u001b]66;w=2; \a\u001b[6n\u001b]66;s=2; \a\u001b[6n");
        Assert.Equal(new[] { "\u001b[1;1R", "\u001b[1;3R", "\u001b[1;5R" }, replies);
    }

    [Theory]
    [InlineData("ab", 1)]
    [InlineData("👩‍💻", 2)]
    public void Explicit_width_keeps_the_complete_text_at_one_anchor(string text, int width)
    {
        var buffer = new TerminalBuffer(40, 8);
        var parser = new AnsiParser(buffer);
        parser.Process($"\u001b]66;w={width};{text}\u001b\\");
        buffer.Lock.EnterReadLock();
        try { Assert.Equal(text, buffer.GetGrapheme(0, 0)); }
        finally { buffer.Lock.ExitReadLock(); }
    }
}
