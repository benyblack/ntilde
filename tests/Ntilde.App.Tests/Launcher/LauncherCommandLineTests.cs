using Ntilde.Launcher;

namespace Ntilde.Tests.Launcher;

/// <summary>
/// ntilde.com hands Ntilde.exe the raw tail of its own command line (Phase 4 spec §11.1): everything
/// after argv[0] as the MSVC CRT delimits it, byte for byte. Pure string work, so these run on every OS.
/// </summary>
public sealed class LauncherCommandLineTests
{
    [Theory]
    [InlineData("ntilde.com mux attach abcd", "mux attach abcd")]
    [InlineData(@"""C:\Program Files\x\ntilde.com"" mux attach ""a b""", @"mux attach ""a b""")]
    [InlineData("ntilde.com", "")]
    [InlineData("ntilde.com   --x", "--x")]
    // CRT: quotes in argv[0] toggle and are not escapable, and the name runs on to the first blank outside them.
    [InlineData(@"""C:\a""b\ntilde.com x", "x")]
    [InlineData("ntilde.com\tmux ls", "mux ls")]
    [InlineData("ntilde.com \t \tmux ls", "mux ls")]
    // Only the separator after argv[0] goes; blanks inside and after the arguments stay.
    [InlineData("ntilde.com a  \"b  c\"\t d ", "a  \"b  c\"\t d ")]
    // Backslashes and escaped quotes after argv[0] are the child's to parse, so they pass untouched.
    [InlineData(@"ntilde.com \""a\"" b\\", @"\""a\"" b\\")]
    // An unterminated quote makes the whole line argv[0].
    [InlineData(@"""C:\a b\ntilde.com x", "")]
    [InlineData(@""""" x", "x")]
    [InlineData(@"""C:\x\ntilde.com""x y", "y")]
    // No escapes in argv[0]: the quote after C:\x\ closes, so argv[0] is C:\x\y. An escape-aware parser
    // would read \" as a literal quote, stay inside the quotes to the end and return "".
    [InlineData(@"""C:\x\""y z", "z")]
    // CRT: a leading blank ends an empty argv[0], so the next word is already an argument.
    [InlineData(" ntilde.com x", "ntilde.com x")]
    [InlineData("", "")]
    public void Tail_is_everything_after_the_CRT_argv0_byte_identical(string raw, string expected)
    {
        Assert.Equal(expected, LauncherCommandLine.Tail(raw));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Ntilde\Ntilde.exe", "mux attach abcd", @"""C:\Program Files\Ntilde\Ntilde.exe"" mux attach abcd")]
    [InlineData(@"C:\Ntilde\Ntilde.exe", "", @"""C:\Ntilde\Ntilde.exe""")]
    [InlineData(@"C:\Ntilde\Ntilde.exe", @"/d /c echo ""a  b""&exit 0", @"""C:\Ntilde\Ntilde.exe"" /d /c echo ""a  b""&exit 0")]
    public void Build_quotes_the_target_and_appends_the_tail_unchanged(string target, string tail, string expected)
    {
        Assert.Equal(expected, LauncherCommandLine.Build(target, tail));
    }

    /// <summary>The child, parsing the line it is given, sees exactly the tail the launcher was given.</summary>
    [Theory]
    [InlineData("mux attach abcd")]
    [InlineData(@"mux attach ""a b""  --x")]
    [InlineData("")]
    [InlineData("a\t\"b\"\\ ")]
    public void The_child_sees_the_launchers_tail(string tail)
    {
        string line = LauncherCommandLine.Build(@"C:\Program Files\Ntilde\Ntilde.exe", tail);

        Assert.Equal(tail, LauncherCommandLine.Tail(line));
    }

    [Fact]
    public void The_contract_values_are_the_specs()
    {
        Assert.Equal("NTILDE_LAUNCHER_RELEASE", LauncherCommandLine.ReleaseEventVariable);
        Assert.Equal(9009, LauncherCommandLine.TargetMissingExitCode);
    }
}
