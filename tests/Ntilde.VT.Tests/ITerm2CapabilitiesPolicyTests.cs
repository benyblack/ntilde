using Ntilde.VT;

namespace Ntilde.VT.Tests;

/// <summary>
/// Anti-fabrication regression from the <c>device.term-features</c> policy audit (#1003): an
/// unsolicited or misspelled iTerm2 Capabilities payload gets no reply, so no feature list can be
/// invented from a form the terminal never treats as a request. The genuine
/// <c>OSC 1337 ; RequestCapabilities</c> query is answered by the feature-reporting implementation —
/// see <see cref="ITerm2CapabilitiesReplyTests"/> and
/// <c>docs/qa/terminfo/iterm-feature-reporting.md</c>.
/// </summary>
public sealed class ITerm2CapabilitiesPolicyTests
{
    private const string Esc = "\u001b";

    [Theory]
    [InlineData("\a")]
    [InlineData("\x1b\\")]
    [Trait("Category", "Regression")]
    public void Unsolicited_capabilities_forms_get_no_reply_and_change_nothing(string terminator)
    {
        var buffer = new TerminalBuffer(20, 4);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        var replies = new List<string>();
        parser.OnResponse = replies.Add;

        parser.Process($"{Esc}]1337;Capabilities{terminator}");
        parser.Process($"{Esc}]1337;Capabilities?{terminator}");
        parser.Process($"{Esc}]1337;Capabilities=TSGoUwTsBFSyHNoSxP{terminator}");

        Assert.Empty(replies);
        Assert.Equal((0, 0), (buffer.CursorRow, buffer.CursorCol));
        Assert.Equal(" ", FirstCell(buffer));
    }

    private static string FirstCell(TerminalBuffer buffer)
    {
        buffer.Lock.EnterReadLock();
        try { return buffer.GetGrapheme(0, 0); }
        finally { buffer.Lock.ExitReadLock(); }
    }
}
