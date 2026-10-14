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

    public async Task<MuxClient> ConnectClientAsync(MuxClientOptions? options = null)
    {
        MuxClient client = await MuxClient.ConnectAsync(Listener.Connect(), options, TestContext.Current.CancellationToken);
        Own(client);
        return client;
    }

    public static Task<Guid> SpawnAsync(MuxClient client, int cols = 80, int rows = 24) =>
        client.SpawnAsync(new SpawnParams { Command = "scripted", Cols = cols, Rows = rows, Title = "test" }, TestContext.Current.CancellationToken);

    public static async Task<ClientPaneModel> AttachPaneAsync(
        MuxClient client, Guid sessionId, MuxPresentation? presentation = null, int maxScrollbackRows = 10_000)
    {
        MuxClientSession session = client.OpenSession(sessionId, "scripted");
        var pane = new ClientPaneModel(session);
        await session.AttachAsync(maxScrollbackRows, presentation ?? DefaultPresentation, TestContext.Current.CancellationToken);
        return pane;
    }

    /// <summary>
    /// Quiesces one session: ping each client (the server has handled everything it sent), flush the
    /// parse thread (every byte and control item is processed and its frames enqueued), ping again
    /// (every frame enqueued before the pong has been delivered - the reader is sequential).
    /// </summary>
    public async Task SettleAsync(Guid sessionId, params MuxClient[] clients)
    {
        foreach (MuxClient c in clients) await c.PingAsync(TestContext.Current.CancellationToken);
        await Mux(sessionId).FlushAsync();
        foreach (MuxClient c in clients) await c.PingAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Call only after <see cref="SettleAsync"/>: reads both sides with their threads idle.</summary>
    public void AssertPaneMatchesMux(Guid sessionId, ClientPaneModel pane, string because)
    {
        HeadlessTerminalSession mux = Mux(sessionId);
        Assert.Equal(mux.StreamPosition, pane.Session.StreamPosition);
        Ntilde.VT.Tests.StateTransfer.TerminalStateAssert.AssertEquivalent(
            $"[{because}]", mux.Buffer, mux.Parser, pane.Buffer, pane.Parser);
    }

    public void Dispose()
    {
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        Server.Dispose();
    }

    internal void Own(IDisposable disposable) => _owned.Add(disposable);
}
