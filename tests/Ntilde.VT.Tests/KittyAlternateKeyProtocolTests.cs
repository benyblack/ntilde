namespace Ntilde.VT.Tests;

public sealed class KittyAlternateKeyProtocolTests
{
    [Fact]
    public void Alternate_flag_is_negotiated_but_all_keys_and_associated_text_remain_masked()
    {
        var buffer = new TerminalBuffer(40, 8);
        var parser = new AnsiParser(buffer);
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process("\x1b[>31u\x1b[?u");
        Assert.Equal("\x1b[?7u", Assert.Single(replies));
    }

    [Fact]
    public void Capability_downgrade_and_restore_preserve_the_requested_alternate_flag()
    {
        var state = new KittyKeyboardState();
        state.Push(7);
        state.SetSupportedFlags(3);
        Assert.Equal(3, state.Flags);
        state.SetSupportedFlags(7);
        Assert.Equal(7, state.Flags);
        state.SetSupportedFlags(1);
        Assert.Equal(1, state.Flags);
        state.SetSupportedFlags(7);
        Assert.Equal(7, state.Flags);
    }
}
