namespace Ntilde.VT.Tests;

/// <summary>Exact currently unavailable spellings, not an assertion that their feature families are absent.</summary>
public sealed class QueryVariantAuditTests
{
    public static TheoryData<string, string> UnavailableVariants => new()
    {
        { "extensions.osc1337-cellsize", "\u001b]1337;ReportCellSize\u0007" },
        { "extensions.osc1337-capabilities", "\u001b]1337;RequestCapabilities\u0007" },
        { "extensions.osc5-special-color", "\u001b]5;0;?\u0007" },
        { "extensions.osc99-kitty-notify", "\u001b]99;i=ntilde-query-audit:p=?;\u001b\\" },
        { "extensions.osc21-kitty-color", "\u001b]21;foreground=?\u001b\\" },
        { "extensions.osc7770-font-size", "\u001b]7770;?\u0007" },
        { "extensions.osc7777-font-window-size", "\u001b]7777;?\u0007" },
        { "extensions.osc701-locale", "\u001b]701;?\u0007" },
        { "extensions.osc702-version", "\u001b]702\u0007" },
        { "extensions.osc776-cell-size", "\u001b]776\u0007" },
        { "extensions.sixel-geometry-report", "\u001b[?2;1;0S" },
        { "editing.decrqcra", "\u001b[1;1;1;1;1;1*y" },
        { "device.tertiary-da", "\u001b[=c" },
        { "device.xtwinops-20", "\u001b[20t" },
        { "device.xtwinops-21", "\u001b[21t" },
        { "device.xtreportcolors", "\u001b[#R" },
        { "device.xtgetxres", "\u001bP+Q7465726d4e616d65\u001b\\" },
    };

    [Theory]
    [MemberData(nameof(UnavailableVariants))]
    public void Unavailable_query_spelling_stays_silent_without_changing_terminal_state(string featureId, string request)
    {
        Assert.NotEmpty(featureId);
        var buffer = new TerminalBuffer(12, 5);
        var responses = new List<string>();
        var parser = new AnsiParser(buffer) { OnResponse = responses.Add };
        parser.Process("\x1b[31;4mcontent\x1b[2;4r\x1b[?6h");
        byte[] before = TerminalStateSerializer.ToBytes(buffer.ExportState(0));
        parser.Process(request);
        Assert.Empty(responses);
        Assert.Equal(before, TerminalStateSerializer.ToBytes(buffer.ExportState(0)));
        // A subsequent independent DA query proves framing recovered. It is not
        // substituted for a reply to the unavailable request.
        parser.Process("\x1b[c");
        Assert.Equal("\x1b[?62;4;22c", Assert.Single(responses));
    }

    [Theory]
    [InlineData("m")] // SGR
    [InlineData("r")] // top/bottom margins
    [InlineData("s")] // left/right margins
    [InlineData(" q")] // cursor style
    [InlineData("\"p")] // conformance level
    [InlineData("\"q")] // character protection
    [InlineData("t")] // lines per page
    [InlineData("$|")] // columns per page
    [InlineData("*|")] // lines per page
    [InlineData("*x")] // rectangular change extent
    [InlineData("unknown")]
    [InlineData("")]
    public void Each_unimplemented_DECRQSS_setting_has_a_bounded_negative_reply(string setting)
    {
        AssertNegativeDcsQuery("$q" + setting, "\x1bP0$r\x1b\\");
    }

    [Theory]
    [InlineData("544e")] // TN
    [InlineData("436f")] // Co
    [InlineData("636f6c6f7273")] // colors
    [InlineData("524742")] // RGB
    [InlineData("6b63757531")] // kcuu1
    [InlineData("544e;436f")] // list
    [InlineData("6e6f742d612d6361706162696c697479")] // unknown
    [InlineData("5")] // odd-length hex
    [InlineData("zz")] // invalid hex
    [InlineData("")]
    public void Each_unimplemented_XTGETTCAP_name_or_malformed_list_has_a_bounded_negative_reply(string names)
    {
        AssertNegativeDcsQuery("+q" + names, "\x1bP0+r\x1b\\");
    }

    [Theory]
    [InlineData("$q", "\x1bP0$r\x1b\\")]
    [InlineData("+q", "\x1bP0+r\x1b\\")]
    public void Overlong_queries_cannot_amplify_the_rejection_or_poison_the_next_request(string prefix, string expected)
    {
        var responses = new List<string>();
        var parser = new AnsiParser(new TerminalBuffer(12, 5)) { OnResponse = responses.Add, MaxStringSequenceChars = 64 };
        parser.Process("\x1bP" + prefix + new string('A', 4096) + "\x1b\\");
        Assert.Equal(expected, Assert.Single(responses));
        parser.Process("\x1b[c");
        Assert.Equal(new[] { expected, "\x1b[?62;4;22c" }, responses);
    }

    private static void AssertNegativeDcsQuery(string payload, string expected)
    {
        string request = "\x1bP" + payload + "\x1b\\";
        for (int split = 0; split <= request.Length; split++)
        {
            var responses = new List<string>();
            var parser = new AnsiParser(new TerminalBuffer(12, 5)) { OnResponse = responses.Add };
            parser.Process(request[..split]);
            if (split < request.Length) Assert.Empty(responses);
            parser.Process(request[split..]);
            Assert.Equal(expected, Assert.Single(responses));
        }
    }
}
