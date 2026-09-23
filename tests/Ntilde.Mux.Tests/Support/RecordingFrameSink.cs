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

    public bool Accept { get; set; } = true;

    public IReadOnlyList<RecordedFrame> Frames { get { lock (_gate) return _frames.ToArray(); } }

    public IReadOnlyList<string> Described => Frames.Select(f => f.Describe()).ToArray();

    public bool TryEnqueue(MuxOutboundFrame frame)
    {
        if (!Accept) return false;
        byte[] payload = frame.Bytes[MuxProtocol.FrameHeaderBytes..].ToArray();
        lock (_gate) _frames.Add(new RecordedFrame(frame.Kind, payload));
        return true;
    }
}

internal sealed record RecordedFrame(MuxFrameKind Kind, byte[] Payload)
{
    /// <summary>"Output@0:abc", "Resize@3:40x10", "Snapshot@5+1", "Exited:7", "Error:snapshot_too_large".</summary>
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
