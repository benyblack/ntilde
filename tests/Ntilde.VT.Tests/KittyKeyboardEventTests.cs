namespace Ntilde.VT.Tests;

public sealed class KittyKeyboardEventTests
{
    [Theory]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(7, 3)]
    [InlineData(31, 3)]
    public void Query_AdvertisesEventReportingAndMasksOtherEnhancements(int requested, int expected)
    {
        var buffer = new TerminalBuffer(40, 8);
        var parser = new AnsiParser(buffer);
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process($"\x1b[>{requested}u\x1b[?u");
        Assert.Equal($"\x1b[?{expected}u", Assert.Single(replies));
    }

    [Fact]
    public void EventFlag_FollowsStackScreenAndResetState()
    {
        var buffer = new TerminalBuffer(40, 8);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b[>3u\x1b[>1u");
        Assert.Equal(1, buffer.Modes.KittyKeyboard.Flags);
        parser.Process("\x1b[<u");
        Assert.Equal(3, buffer.Modes.KittyKeyboard.Flags);
        parser.Process("\x1b[?1049h\x1b[>2u");
        Assert.Equal(2, buffer.Modes.KittyKeyboard.Flags);
        parser.Process("\x1b[?1049l");
        Assert.Equal(3, buffer.Modes.KittyKeyboard.Flags);
        parser.Process("\u001bc");
        Assert.Equal(0, buffer.Modes.KittyKeyboard.Flags);
    }
    [Fact]
    public void HostCapabilityCeiling_DoesNotDestroyTheRequestedStack()
    {
        var state = new KittyKeyboardState();
        state.Push(3);
        state.SetSupportedFlags(1);
        Assert.Equal(1, state.Flags);
        Assert.Equal("\x1b[?1u", state.FormatQueryResponse());
        state.SetSupportedFlags(0);
        Assert.Equal(0, state.Flags);
        state.SetSupportedFlags(3);
        Assert.Equal(3, state.Flags);
        Assert.True(state.ReportEventTypes);
    }
}
