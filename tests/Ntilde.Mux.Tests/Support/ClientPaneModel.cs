using Ntilde.VT;

namespace Ntilde.Mux.Tests.Support;

/// <summary>
/// What Phase 2's TerminalPane will do with a MuxClientSession: restore on SnapshotReceived, parse
/// OnOutputReceived, resize on StreamResize - and never send its own parser's replies.
/// </summary>
internal sealed class ClientPaneModel
{
    private readonly object _gate = new();
    private readonly List<string> _events = new();
    private readonly List<string> _responses = new();

    public ClientPaneModel(MuxClientSession session)
    {
        Session = session;
        Buffer = new TerminalBuffer(80, 24);
        Parser = new AnsiParser(Buffer, session.ForceConPtyFiltering) { ImageDecoder = null, AllowNativeKittyGraphics = false };
        Parser.OnResponse = reply => { lock (_gate) _responses.Add(reply); }; // parsed, never sent
        session.SnapshotReceived += OnSnapshot;
        session.OnOutputReceived += OnOutput;
        session.StreamResize += OnResize;
    }

    public MuxClientSession Session { get; }
    public TerminalBuffer Buffer { get; }
    public AnsiParser Parser { get; }
    public TerminalStateSnapshot? LastSnapshot { get; private set; }
    public IReadOnlyList<string> Events { get { lock (_gate) return _events.ToArray(); } }
    public IReadOnlyList<string> Responses { get { lock (_gate) return _responses.ToArray(); } }

    private void OnSnapshot(TerminalStateSnapshot snapshot)
    {
        TerminalStateTransfer.Restore(Buffer, Parser, snapshot);
        lock (_gate)
        {
            LastSnapshot = snapshot;
            _events.Add($"snapshot@{snapshot.StreamSeq}+{snapshot.DecoderTail?.Length ?? 0}");
        }
    }

    private void OnOutput(string text)
    {
        Parser.Process(text);
        lock (_gate) _events.Add("out:" + text);
    }

    private void OnResize(int cols, int rows)
    {
        Buffer.Resize(cols, rows);
        lock (_gate) _events.Add($"resize:{cols}x{rows}");
    }
}
