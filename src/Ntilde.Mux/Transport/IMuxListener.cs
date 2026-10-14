namespace Ntilde.Mux.Transport;

/// <summary>
/// Where a <c>MuxServer</c> gets connections. Phase 1 has only <see cref="InMemoryMuxListener"/>;
/// Phase 2 plugs a named-pipe / Unix-socket listener in here, which is why the server only ever
/// sees a <see cref="Stream"/>.
/// </summary>
public interface IMuxListener : IDisposable
{
    /// <summary>Blocks until a client connects. Returns null once the listener is closed or cancelled.</summary>
    Stream? Accept(CancellationToken cancellationToken);
}
