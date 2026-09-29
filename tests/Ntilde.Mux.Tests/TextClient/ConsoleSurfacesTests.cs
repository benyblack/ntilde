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
}
