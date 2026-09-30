using Ntilde.Mux.TextClient;

namespace Ntilde.Mux.Tests.TextClient;

/// <summary>The Windows surface's read loop against scripted ReadConsoleW results (final review).</summary>
public sealed class ConsoleReadAssemblerTests
{
    [Fact]
    public void A_zero_char_read_with_a_held_back_high_half_returns_it_instead_of_spinning()
    {
        var console = new ScriptedConsole((true, ""));
        char? pending = '\ud83d';
        var buffer = new char[16];

        int n = ConsoleReadAssembler.Read(buffer, ref pending, console.Read);

        Assert.Equal(1, n);
        Assert.Equal('\ud83d', buffer[0]);
        Assert.Null(pending);
    }

    [Fact]
    public void A_zero_char_read_with_nothing_pending_is_retried_not_read_as_closed()
    {
        var console = new ScriptedConsole((true, ""), (true, "a"));
        char? pending = null;
        var buffer = new char[16];

        int n = ConsoleReadAssembler.Read(buffer, ref pending, console.Read);

        Assert.Equal("a", new string(buffer, 0, n));
    }

    [Fact]
    public void Only_a_failed_read_means_input_closed()
    {
        var console = new ScriptedConsole((false, ""));
        char? pending = null;

        Assert.Equal(0, ConsoleReadAssembler.Read(new char[16], ref pending, console.Read));
    }

    [Fact]
    public void A_split_pair_is_held_back_and_joined_on_the_next_read()
    {
        var console = new ScriptedConsole((true, "xy\ud83d"), (true, "\ude00z"));
        char? pending = null;
        var buffer = new char[16];

        Assert.Equal("xy", new string(buffer, 0, ConsoleReadAssembler.Read(buffer, ref pending, console.Read)));
        Assert.Equal("😀z", new string(buffer, 0, ConsoleReadAssembler.Read(buffer, ref pending, console.Read)));
    }

    [Fact]
    public void Ctrl_Z_is_an_ordinary_char()
    {
        var console = new ScriptedConsole((true, "\u001a"));
        char? pending = null;
        var buffer = new char[16];

        Assert.Equal("\u001a", new string(buffer, 0, ConsoleReadAssembler.Read(buffer, ref pending, console.Read)));
    }

    /// <summary>Plays back ReadConsoleW results; one read past the script fails the test rather than blocking or spinning.</summary>
    private sealed class ScriptedConsole(params (bool Ok, string Chars)[] results)
    {
        private int _next;

        public bool Read(char[] buffer, int offset, int count, out int read)
        {
            if (_next == results.Length) throw new InvalidOperationException("read past the scripted console results");
            (bool ok, string chars) = results[_next++];
            chars.AsSpan(0, Math.Min(chars.Length, count)).CopyTo(buffer.AsSpan(offset));
            read = Math.Min(chars.Length, count);
            return ok;
        }
    }
}
