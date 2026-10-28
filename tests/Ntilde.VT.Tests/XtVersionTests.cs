using System.Reflection;
using System.Text.RegularExpressions;

namespace Ntilde.VT.Tests;

// #272: XTVERSION (CSI > q / CSI > 0 q) is answered with DCS > | Ntilde <version> ST. It shares
// the q final byte with DECSCUSR (CSI Ps SP q), which must keep working and stay silent.
public class XtVersionTests
{
    private static (TerminalBuffer buffer, AnsiParser parser, List<string> responses) CreateTerminal()
    {
        var buffer = new TerminalBuffer(cols: 40, rows: 5);
        var parser = new AnsiParser(buffer);
        var responses = new List<string>();
        parser.OnResponse = responses.Add;
        return (buffer, parser, responses);
    }

    [Theory]
    [InlineData("\x1b[>q")]
    [InlineData("\x1b[>0q")]
    public void Xtversion_is_answered_with_the_terminal_name_and_version(string query)
    {
        var (_, parser, responses) = CreateTerminal();

        parser.Process(query);

        string reply = Assert.Single(responses);
        Assert.Matches(new Regex(@"^\x1bP>\|Ntilde \d+\.\d+\.\d+[0-9A-Za-z.-]*\x1b\\$"), reply);
    }

    [Fact]
    public void The_version_is_the_builds_version_without_build_metadata()
    {
        // Every project in the solution is built with the same version (Directory.Build.props, or a
        // release's override), so this assembly's version is the app's. SourceLink can append
        // "+<commit>"; the reply must not carry it.
        string expected = typeof(XtVersionTests).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0];

        Assert.Equal($"\x1bP>|Ntilde {expected}\x1b\\", AnsiParser.XtVersionReply);
        Assert.DoesNotContain("+", AnsiParser.XtVersionReply, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\x1b[>1q")] // undefined Ps
    [InlineData("\x1b[>0;1q")] // extra parameters
    [InlineData("\x1b[>;q")]
    [InlineData("\x1b[q")] // DECLL (load LEDs), unimplemented
    [InlineData("\x1b[?q")]
    [InlineData("\x1b[> q")] // a leader and DECSCUSR's intermediate: neither sequence
    public void Other_q_sequences_get_no_reply(string sequence)
    {
        var (_, parser, responses) = CreateTerminal();

        parser.Process(sequence);

        Assert.Empty(responses);
    }

    [Fact]
    public void Decscusr_still_sets_the_cursor_style_and_sends_nothing()
    {
        var (buffer, parser, responses) = CreateTerminal();

        parser.Process("\x1b[6 q");

        Assert.Equal(CursorStyle.Beam, buffer.Modes.CursorStyle);
        Assert.Empty(responses);
    }

    [Fact]
    public void Xtversion_does_not_touch_the_cursor_style()
    {
        var (buffer, parser, _) = CreateTerminal();
        parser.Process("\x1b[6 q");

        parser.Process("\x1b[>0q");

        Assert.Equal(CursorStyle.Beam, buffer.Modes.CursorStyle);
    }
}
