using Ntilde.VT;

namespace Ntilde.Mux.TextClient;

/// <summary>
/// The text client's own copy of a session (Phase 1's ClientPaneModel, generalised; spec §6.1):
/// restore on snapshot, parse output, resize in stream - all on the client's delivery thread. The
/// parser's replies are discarded: the mux has already answered every device query.
/// </summary>
public sealed class TextClientModel : IDisposable
{
    public TextClientModel(MuxClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;
        Buffer = new TerminalBuffer(80, 24);
        Parser = new AnsiParser(Buffer, session.ForceConPtyFiltering)
        {
            ImageDecoder = null,
            RetainSixelSources = true,
            AllowNonSixelImages = false,
            AllowNativeKittyGraphics = false,
        };
        // Kitty and iTerm2 images reserve the same cells as in the daemon and every window, and show no pixels (#838).
        PersistentImagePolicy.Apply(Parser, session.RequiredImagePolicy);
        // A t=f command takes what the daemon read for it (#887); the text client never opens a file.
        if (PersistentImagePolicy.AllowsFileTransport(session.RequiredImagePolicy)) Parser.ReadFileBytes = session.ReadTransportFile;
        Parser.OnResponse = static _ => { };
        session.SnapshotReceived += OnSnapshot;
        session.OutputBytesReceived += OnOutput;
        session.StreamResize += OnResize;
        session.SixelCellSizeChanged += OnSixelCellSize;
    }

    public MuxClientSession Session { get; }
    public TerminalBuffer Buffer { get; }
    public AnsiParser Parser { get; }

    /// <summary>Raised on the delivery thread after every change to <see cref="Buffer"/>; the render thread's wake-up.</summary>
    public event Action? Changed;

    /// <summary>Stops following the session; the buffer keeps its last state.</summary>
    public void Dispose()
    {
        Session.SnapshotReceived -= OnSnapshot;
        Session.OutputBytesReceived -= OnOutput;
        Session.StreamResize -= OnResize;
        Session.SixelCellSizeChanged -= OnSixelCellSize;
    }

    private void OnSnapshot(TerminalStateSnapshot snapshot)
    {
        TerminalStateTransfer.Restore(Buffer, Parser, snapshot);
        Changed?.Invoke();
    }

    private void OnOutput(ReadOnlySpan<byte> bytes)
    {
        if (Parser.ProcessBytes(bytes)) Changed?.Invoke();
    }

    private void OnSixelCellSize(float width, float height) =>
        Parser.SixelCellSizeOverride = new SixelCellSize(width, height);

    private void OnResize(int cols, int rows)
    {
        Buffer.Resize(cols, rows);
        Changed?.Invoke();
    }
}
