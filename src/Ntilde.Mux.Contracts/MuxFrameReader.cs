using System.Buffers;

namespace Ntilde.Mux.Contracts;

/// <summary>A received frame whose payload lives in a pooled buffer until <see cref="Dispose"/>.</summary>
public sealed class MuxInboundFrame : IDisposable
{
    private byte[]? _rented;

    internal MuxInboundFrame(MuxFrameKind kind, byte[] rented, int length)
    {
        Kind = kind;
        _rented = rented;
        Length = length;
    }

    public MuxFrameKind Kind { get; }
    public int Length { get; }

    public ReadOnlySpan<byte> Payload =>
        (_rented ?? throw new ObjectDisposedException(nameof(MuxInboundFrame))).AsSpan(0, Length);

    public void Dispose()
    {
        byte[]? rented = Interlocked.Exchange(ref _rented, null);
        if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
    }
}

public static class MuxFrameReader
{
    /// <summary>
    /// Blocking read of one frame, for dedicated reader threads. Returns null on a clean end of
    /// stream at a frame boundary; throws <see cref="EndOfStreamException"/> mid-frame and
    /// <see cref="MuxProtocolException"/> for a header that fails validation (before any payload
    /// byte is read, so an oversize announcement costs nothing).
    /// </summary>
    public static MuxInboundFrame? Read(Stream stream, int maxFrameBytes = MuxProtocol.MaxFrameBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> header = stackalloc byte[MuxProtocol.FrameHeaderBytes];
        int got = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (got == 0) return null;
        if (got < header.Length) throw new EndOfStreamException("Stream ended inside a frame header.");

        (MuxFrameKind kind, int length) = MuxFrameCodec.ReadHeader(header, maxFrameBytes);
        byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
        try
        {
            stream.ReadExactly(rented, 0, length);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rented);
            throw;
        }

        return new MuxInboundFrame(kind, rented, length);
    }
}
