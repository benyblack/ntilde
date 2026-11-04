using Ntilde.VT;

namespace Ntilde.VT.Tests;

public sealed class OscTitleTests
{
    public static IEnumerable<object[]> TitlePayloads()
    {
        foreach (string code in new[] { "0", "2" })
            foreach (string terminator in new[] { "\a", "\x1b\\" })
                foreach (string payload in new[] { "", "   ", "  padded title  ", "  Ω;title  " })
                    yield return new object[] { code, terminator, payload };
    }

    [Theory]
    [MemberData(nameof(TitlePayloads))]
    [Trait("Category", "Regression")]
    public void Title_requests_replace_the_previous_payload_exactly(string code, string terminator, string payload)
    {
        var buffer = new TerminalBuffer(20, 2);
        var parser = new AnsiParser(buffer);
        var titles = new List<string>(); parser.OnTitleChanged = titles.Add;
        parser.Process("guard\x1b]2;previous\a");
        parser.Process($"\x1b]{code};{payload}{terminator}");
        Assert.Equal(new[] { "previous", payload }, titles);
        Assert.Equal((0, 5), (buffer.CursorRow, buffer.CursorCol));
        Assert.Equal("g", ReadFirstCell(buffer));
    }

    [Theory]
    [InlineData("0", "")]
    [InlineData("2", "")]
    [InlineData("0", "  spaced  ")]
    [InlineData("2", "  spaced  ")]
    [Trait("Category", "Regression")]
    public void Split_title_requests_emit_once_and_leave_the_next_printable_character_intact(string code, string payload)
    {
        string input = $"\x1b]{code};{payload}\x1b\\Z";
        for (int split = 0; split <= input.Length; split++)
        {
            var buffer = new TerminalBuffer(20, 2);
            var parser = new AnsiParser(buffer);
            var titles = new List<string>(); parser.OnTitleChanged = titles.Add;
            parser.Process(input[..split]); parser.Process(input[split..]);
            Assert.Equal(payload, Assert.Single(titles));
            Assert.Equal("Z", ReadFirstCell(buffer));
        }
    }

    private static string ReadFirstCell(TerminalBuffer buffer)
    {
        buffer.Lock.EnterReadLock();
        try { return buffer.GetGrapheme(0, 0); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("1;")]
    [InlineData("22;")]
    public void Missing_separator_and_other_OSC_codes_do_not_clear_the_window_title(string command)
    {
        var parser = new AnsiParser(new TerminalBuffer(20, 2));
        var titles = new List<string>(); parser.OnTitleChanged = titles.Add;
        parser.Process("\x1b]2;previous\a");
        parser.Process($"\x1b]{command}\a");
        Assert.Equal("previous", Assert.Single(titles));
    }
}
