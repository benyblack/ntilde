using Ntilde.Mux.TextClient;

namespace Ntilde.Mux.Tests.TextClient;

public sealed class ConsoleSurfacesTests
{
    [Fact]
    public void Without_a_terminal_the_surface_refuses_instead_of_corrupting_the_pipe()
    {
        // Test runners redirect stdin; locally from a terminal it may be a TTY, so this is conditional.
        Assert.SkipUnless(Console.IsInputRedirected && !OperatingSystem.IsWindows(), "stdin is a terminal here (or Windows, whose surface opens CONIN$ directly)");

        Assert.Throws<ConsoleUnavailableException>(() => ConsoleSurfaces.Create().Dispose());
    }

    /// <summary>
    /// The Unix read path's decoder: bytes carried over from a split sequence can complete as a
    /// surrogate pair, one more char than bytes, so a read never takes more than capacity - 2 bytes.
    /// </summary>
    [Fact]
    public void Terminal_input_decoding_never_overflows_the_char_buffer_across_split_sequences()
    {
        Assert.Equal(1022, TerminalInputDecoder.MaxBytes(1024, 4096));
        Assert.Equal(512, TerminalInputDecoder.MaxBytes(1024, 512));
        Assert.Equal(1, TerminalInputDecoder.MaxBytes(2, 1024));

        // "a😀" split so that 3 bytes of the emoji are carried over, then a full buffer's worth after it.
        byte[] emoji = System.Text.Encoding.UTF8.GetBytes("\U0001F600");
        var decoder = new TerminalInputDecoder();
        char[] buffer = new char[4];
        byte[] first = [(byte)'a', emoji[0], emoji[1], emoji[2]];
        Assert.Equal(1, decoder.Decode(first, first.Length, buffer));
        Assert.Equal('a', buffer[0]);

        int max = TerminalInputDecoder.MaxBytes(buffer.Length, 1024);
        byte[] second = [emoji[3], (byte)'b', (byte)'c', (byte)'d'];
        int chars = decoder.Decode(second, max, buffer); // the last emoji byte + 'b' = 3 chars from 2 bytes
        Assert.Equal("\U0001F600b", new string(buffer, 0, chars));
    }
}
