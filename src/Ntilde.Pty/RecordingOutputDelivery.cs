namespace Ntilde.Pty;

/// <summary>
/// Pairs original bytes with decoded output for a single recording host.
/// Host installation and byte delivery are serialized by the session's output invocation lock.
/// </summary>
public sealed class RecordingOutputDelivery
{
    private Ntilde.Replay.RecordingCellMetrics _cellMetrics = new(10, 20);
    public Ntilde.Replay.RecordingCellMetrics CellMetrics => Volatile.Read(ref _cellMetrics);

    private int _mode; // 0: transport, 1: external raw host, 2: decoded-output host
    public int Mode => Volatile.Read(ref _mode);
    public ReadOnlyMemory<byte> Bytes { get; set; }

    public void SetCellMetrics(float widthPx, float heightPx, Ntilde.Replay.PtyRecorder? recorder, Ntilde.Replay.FlightRecordingBuffer? flight)
    {
        var size = new Ntilde.Replay.RecordingCellMetrics(widthPx, heightPx);
        if (CellMetrics == size) return;
        Volatile.Write(ref _cellMetrics, size);
        recorder?.RecordSixelCellSize(widthPx, heightPx);
        flight?.RecordSixelCellSize(widthPx, heightPx);
    }

    public void UseRawHost() => Volatile.Write(ref _mode, 1);

    public Action<string> CreateDecodedHost(Action<string, ReadOnlyMemory<byte>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (Mode == 2) throw new InvalidOperationException("A recording output host is already installed.");
        Volatile.Write(ref _mode, 2);
        return text => handler(text, Bytes);
    }
}
