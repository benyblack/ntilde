using Ntilde.VT;

namespace Ntilde.VT.Tests;

/// <summary>
/// The iTerm2 feature-reporting reply (<c>OSC 1337 ; RequestCapabilities</c> →
/// <c>OSC 1337 ; Capabilities = {FeatureString} ST</c>). The expected strings below are transcribed
/// from the specification's code table and this terminal's implemented, tested capability list —
/// they are written out literally and never derived from the production constants, so a change to
/// either side without the other fails. Ownership and limits:
/// <c>docs/qa/terminfo/iterm-feature-reporting.md</c>.
/// </summary>
public sealed class ITerm2CapabilitiesReplyTests
{
    private const string Esc = "\u001b";
    /// <summary>The declared codes with clipboard writing (a host that accepts OSC 52 writes), per the specification's table order.</summary>
    private const string FullFeatures = "T3CwLrMSc7UTs3BFGsGoSyHNoP";
    /// <summary>The declared codes when the host's OSC 52 write acceptance is unknown or false.</summary>
    private const string FeaturesWithoutClipboardWrite = "T3LrMSc7UTs3BFGsGoSyHNoP";

    private static (AnsiParser Parser, TerminalBuffer Buffer, List<string> Replies) Create(Func<bool>? clipboardWriteAvailable = null)
    {
        var buffer = new TerminalBuffer(20, 4);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ClipboardWriteAvailable = clipboardWriteAvailable };
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        return (parser, buffer, replies);
    }

    private static string FirstCell(TerminalBuffer buffer)
    {
        buffer.Lock.EnterReadLock();
        try { return buffer.GetGrapheme(0, 0); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Theory]
    [InlineData("\a")]
    [InlineData("\x1b\\")]
    [Trait("Category", "Regression")]
    public void RequestCapabilities_is_answered_by_one_st_terminated_capabilities_reply(string terminator)
    {
        var (parser, buffer, replies) = Create(clipboardWriteAvailable: () => true);

        parser.Process($"{Esc}]1337;RequestCapabilities{terminator}");

        var reply = Assert.Single(replies);
        Assert.Equal($"{Esc}]1337;Capabilities={FullFeatures}{Esc}\\", reply);
        Assert.Equal((0, 0), (buffer.CursorRow, buffer.CursorCol));
        Assert.Equal(" ", FirstCell(buffer));
    }

    [Theory]
    [InlineData(true, FullFeatures)]
    [InlineData(false, FeaturesWithoutClipboardWrite)]
    [Trait("Category", "Regression")]
    public void The_declaration_tracks_the_host_osc52_write_acceptance(bool acceptsWrite, string expectedFeatures)
    {
        var (parser, _, replies) = Create(clipboardWriteAvailable: () => acceptsWrite);
        var (unknownParser, _, unknownReplies) = Create(clipboardWriteAvailable: null);

        parser.Process($"{Esc}]1337;RequestCapabilities\a");
        unknownParser.Process($"{Esc}]1337;RequestCapabilities\a");

        Assert.Equal($"{Esc}]1337;Capabilities={expectedFeatures}{Esc}\\", Assert.Single(replies));
        // A host that cannot answer is treated like one that refused: the code is omitted, never advertised.
        Assert.Equal($"{Esc}]1337;Capabilities={FeaturesWithoutClipboardWrite}{Esc}\\", Assert.Single(unknownReplies));
    }

    [Fact]
    [Trait("Category", "Regression")]
    public void A_request_split_across_chunks_is_answered_once()
    {
        string input = $"{Esc}]1337;RequestCapabilities\x1b\\";
        for (int split = 0; split <= input.Length; split++)
        {
            var (parser, _, replies) = Create(clipboardWriteAvailable: () => true);
            parser.Process(input[..split]);
            parser.Process(input[split..]);
            Assert.Equal($"{Esc}]1337;Capabilities={FullFeatures}{Esc}\\", Assert.Single(replies));
        }
    }

    [Theory]
    [InlineData("1337;Capabilities")]
    [InlineData("1337;Capabilities=")]
    [InlineData("1337;Capabilities?")]
    [InlineData("1337;Capabilities=TSGoUwTsBFSyHNoSxP")]
    [InlineData("1337;RequestCapabilitiesX")]
    [InlineData("1337;requestcapabilities")]
    [InlineData("1337;REQUESTCAPABILITIES")]
    [InlineData("1337;RequestCapabilities ")]
    [InlineData("1338;RequestCapabilities")]
    [Trait("Category", "Regression")]
    public void Anything_but_the_exact_request_payload_stays_silent(string payload)
    {
        var (parser, buffer, replies) = Create(clipboardWriteAvailable: () => true);

        parser.Process($"{Esc}]{payload}\a");

        Assert.Empty(replies);
        Assert.Equal((0, 0), (buffer.CursorRow, buffer.CursorCol));
        Assert.Equal(" ", FirstCell(buffer));
    }

    [Fact]
    [Trait("Category", "Regression")]
    public void Repeated_requests_each_get_their_own_reply_and_an_unsolicited_assignment_stays_silent()
    {
        var (parser, _, replies) = Create(clipboardWriteAvailable: () => true);

        parser.Process($"{Esc}]1337;RequestCapabilities\a");
        parser.Process($"{Esc}]1337;RequestCapabilities\x1b\\");
        parser.Process($"{Esc}]1337;Capabilities=unsolicited\x1b\\");

        Assert.Equal(2, replies.Count);
        Assert.Equal($"{Esc}]1337;Capabilities={FullFeatures}{Esc}\\", replies[0]);
        Assert.Equal($"{Esc}]1337;Capabilities={FullFeatures}{Esc}\\", replies[1]);
    }
}
