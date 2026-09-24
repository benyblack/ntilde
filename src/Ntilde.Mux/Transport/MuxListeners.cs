namespace Ntilde.Mux.Transport;

public static class MuxListeners
{
    /// <summary>The platform's local listener for <paramref name="endpoint"/> (spec §3).</summary>
    public static IMuxListener Create(string endpoint) =>
        OperatingSystem.IsWindows() ? new NamedPipeMuxListener(endpoint) : new UnixSocketMuxListener(endpoint);
}
