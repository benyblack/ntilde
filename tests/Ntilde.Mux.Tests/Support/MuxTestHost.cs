using Ntilde.Mux.Contracts;
using Ntilde.Mux.Transport;

namespace Ntilde.Mux.Tests.Support;

/// <summary>A started server over an in-memory listener with a scripted session factory.</summary>
internal sealed class MuxTestHost : IDisposable
{
    public static readonly MuxPresentation DefaultPresentation = new() { Cols = 80, Rows = 24, CellWidthPx = 10, CellHeightPx = 20 };

    private readonly List<IDisposable> _owned = new();

    public MuxTestHost(MuxServerOptions? options = null, int pipeCapacityBytes = 1 << 20)
    {
        Factory = new ScriptedSessionFactory();
        Listener = new InMemoryMuxListener(pipeCapacityBytes);
        Server = new MuxServer(Factory, options ?? new MuxServerOptions { ForceConPtyFiltering = false });
        Server.Start(Listener);
    }

    public ScriptedSessionFactory Factory { get; }
    public InMemoryMuxListener Listener { get; }
    public MuxServer Server { get; }

    public RawMuxConnection ConnectRaw()
    {
        var raw = new RawMuxConnection(Listener.Connect());
        _owned.Add(raw);
        return raw;
    }

    public HeadlessTerminalSession Mux(Guid id) =>
        Server.TryGetSession(id, out HeadlessTerminalSession? session) ? session : throw new InvalidOperationException($"No session {id}.");

    public ScriptedTerminalSession Fake(Guid id) => (ScriptedTerminalSession)Mux(id).Inner;

    public void Dispose()
    {
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        Server.Dispose();
    }

    internal void Own(IDisposable disposable) => _owned.Add(disposable);
}
