namespace Ntilde.Mux.TextClient;

public static class ConsoleSurfaces
{
    /// <summary>The real terminal. Throws <see cref="ConsoleUnavailableException"/> when there is none.</summary>
    public static IConsoleSurface Create()
    {
        if (OperatingSystem.IsWindows()) return new WindowsConsoleSurface();
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD()) return new UnixConsoleSurface();
        throw new ConsoleUnavailableException("mux attach is not supported on this platform.");
    }
}
