namespace Ntilde.VT.Tests;

public sealed class KittyAssociatedTextProtocolTests
{
    [Fact]
    public void All_defined_enhancements_are_negotiated()
    {
        var buffer = new TerminalBuffer(40, 8);
        var parser = new AnsiParser(buffer);
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process("\u001b[>31u\u001b[?u");
        Assert.Equal("\u001b[?31u", Assert.Single(replies));
    }

    [Fact]
    public void Downgrade_preserves_the_requested_associated_text_flag_for_recovery()
    {
        var state = new KittyKeyboardState();
        state.Push(31);
        state.SetSupportedFlags(15);
        Assert.Equal(15, state.Flags);
        state.SetSupportedFlags(31);
        Assert.Equal(31, state.Flags);
    }
}
