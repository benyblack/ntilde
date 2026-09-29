using Ntilde.VT;

namespace Ntilde.Mux.TextClient;

/// <summary>
/// The text client's own copy of a session (Phase 1's ClientPaneModel, generalised; spec §6.1):
/// restore on snapshot, parse output, resize in stream - all on the client's delivery thread. The
/// parser's replies are discarded: the mux has already answered every device query.
/// </summary>
public sealed class TextClientModel
{
    public TextClientModel(MuxClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;
        Buffer = new TerminalBuffer(80, 24);
        Parser = new AnsiParser(Buffer, session.ForceConPtyFiltering)
        {
            ImageDecoder = null,
            AllowNativeKittyGraphics = false,
        };
        Parser.OnResponse = static _ => { };
        session.SnapshotReceived += OnSnapshot;
        session.OnOutputReceived += OnOutput;
        session.StreamResize += OnResize;
    }

    public MuxClientSession Session { get; }
    public TerminalBuffer Buffer { get; }
    public AnsiParser Parser { get; }

    /// <summary>Raised on the delivery thread after every change to <see cref="Buffer"/>; the render thread's wake-up.</summary>
    public event Action? Changed;

    private void OnSnapshot(TerminalStateSnapshot snapshot)
    {
        TerminalStateTransfer.Restore(Buffer, Parser, snapshot);
        Changed?.Invoke();
    }

    private void OnOutput(string text)
    {
        Parser.Process(text);
        Changed?.Invoke();
    }

    private void OnResize(int cols, int rows)
    {
        Buffer.Resize(cols, rows);
        Changed?.Invoke();
    }
}
