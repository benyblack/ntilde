namespace Ntilde.VT.Tests;

public sealed class KittyAllKeyProtocolTests
{
    [Fact]
    public void All_key_reporting_is_negotiated_without_associated_text()
    {
        var buffer = new TerminalBuffer(40, 8);
        var parser = new AnsiParser(buffer);
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process("\u001b[>31u\u001b[?u");
        Assert.Equal("\u001b[?15u", Assert.Single(replies));
    }

    [Fact]
    public void Old_peer_ceiling_does_not_destroy_the_requested_all_key_flag()
    {
        var state = new KittyKeyboardState();
        state.Push(15);
        state.SetSupportedFlags(7);
        Assert.Equal(7, state.Flags);
        state.SetSupportedFlags(15);
        Assert.Equal(15, state.Flags);
    }
}
