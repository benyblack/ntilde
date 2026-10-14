using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Ntilde.Mux.Contracts;

namespace Ntilde.Mux.Tests.Support;

/// <summary>An IMuxFrameSink that copies every frame it accepts (so it takes no reference).</summary>
internal sealed class RecordingFrameSink : IMuxFrameSink
{
    private readonly object _gate = new();
    private readonly List<RecordedFrame> _frames = new();
    private readonly List<long> _times = new();

    public bool Accept { get; set; } = true;

    /// <summary>Opt in to sessionChanged/killed, like a v2 connection. Default false, so exact-sequence assertions elsewhere are unchanged.</summary>
    public bool WantsSessionEvents { get; set; }

    public IReadOnlyList<RecordedFrame> Frames { get { lock (_gate) return _frames.ToArray(); } }

    public IReadOnlyList<string> Described => Frames.Select(f => f.Describe()).ToArray();

    public bool TryEnqueue(MuxOutboundFrame frame)
    {
        if (!Accept) return false;
        byte[] payload = frame.Bytes[MuxProtocol.FrameHeaderBytes..].ToArray();
        lock (_gate)
        {
            _frames.Add(new RecordedFrame(frame.Kind, payload));
            _times.Add(Environment.TickCount64);
        }

        return true;
    }

    public IReadOnlyList<SessionChangedNotification> SessionChanges =>
        Frames.Where(f => f.IsNotification(MuxMethods.SessionChanged))
              .Select(f => MuxFrames.ParseParams(MuxFrames.ParseJson(f.Payload, MuxJsonContext.Default.MuxNotification).Params, MuxJsonContext.Default.SessionChangedNotification))
              .ToArray();

    public IReadOnlyList<long> SessionChangeTimesMs
    {
        get
        {
            lock (_gate)
            {
                return _frames.Select((f, i) => (f, i)).Where(x => x.f.IsNotification(MuxMethods.SessionChanged)).Select(x => _times[x.i]).ToArray();
            }
        }
    }
}

internal sealed record RecordedFrame(MuxFrameKind Kind, byte[] Payload)
{
    public bool IsNotification(string method) =>
        Kind == MuxFrameKind.Notification && MuxFrames.ParseJson(Payload, MuxJsonContext.Default.MuxNotification).Method == method;

    /// <summary>"Output@0:abc", "Resize@3:40x10", "Snapshot@5+1", "Exited:7", "Faulted", "SessionChanged:2", "Killed:ntilde-cli", "Error:snapshot_too_large".</summary>
    public string Describe()
    {
        switch (Kind)
        {
            case MuxFrameKind.Output:
                _ = MuxFrames.TryParseOutput(Payload, out _, out long seq, out ReadOnlySpan<byte> data);
                return string.Create(CultureInfo.InvariantCulture, $"Output@{seq}:{Encoding.UTF8.GetString(data)}");
            case MuxFrameKind.ResizeEvent:
                _ = MuxFrames.TryParseResizeEvent(Payload, out _, out long rseq, out int cols, out int rows);
                return string.Create(CultureInfo.InvariantCulture, $"Resize@{rseq}:{cols}x{rows}");
            case MuxFrameKind.Snapshot:
                _ = MuxFrames.TryParseSnapshot(Payload, out _, out _, out long sseq, out _);
                return string.Create(CultureInfo.InvariantCulture, $"Snapshot@{sseq}");
            case MuxFrameKind.Notification:
                MuxNotification n = MuxFrames.ParseJson(Payload, MuxJsonContext.Default.MuxNotification);
                if (n.Method == MuxMethods.Faulted) return "Faulted";
                if (n.Method == MuxMethods.SessionChanged)
                {
                    SessionChangedNotification sc = MuxFrames.ParseParams(n.Params, MuxJsonContext.Default.SessionChangedNotification);
                    return string.Create(CultureInfo.InvariantCulture, $"SessionChanged:{sc.AttachedClients}");
                }

                if (n.Method == MuxMethods.Killed)
                {
                    return "Killed:" + MuxFrames.ParseParams(n.Params, MuxJsonContext.Default.KilledNotification).ByClientKind;
                }

                ExitedNotification e = MuxFrames.ParseParams(n.Params, MuxJsonContext.Default.ExitedNotification);
                return string.Create(CultureInfo.InvariantCulture, $"Exited:{e.ExitCode}");
            case MuxFrameKind.Response:
                MuxResponse r = MuxFrames.ParseJson(Payload, MuxJsonContext.Default.MuxResponse);
                return r.Error is { } err ? $"Error:{err.Code}" : "Response";
            default:
                return Kind.ToString();
        }
    }
}
