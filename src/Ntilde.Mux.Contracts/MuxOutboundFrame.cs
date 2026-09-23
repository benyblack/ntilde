using System.Buffers;

namespace Ntilde.Mux.Contracts;

/// <summary>
/// A frame ready to write - header and payload in one <see cref="ArrayPool{T}"/> buffer - shared by
/// reference count: a session builds one <c>Output</c> frame per chunk and every attached client's
/// sender holds a reference until it has written it. The creator owns the first reference.
/// </summary>
public sealed class MuxOutboundFrame
{
    private byte[]? _buffer;
    private int _refCount = 1;

    private MuxOutboundFrame(MuxFrameKind kind, byte[] buffer, int payloadLength)
    {
        Kind = kind;
        _buffer = buffer;
        PayloadLength = payloadLength;
    }

    public MuxFrameKind Kind { get; }
    public int PayloadLength { get; }
    public int Length => MuxProtocol.FrameHeaderBytes + PayloadLength;

    /// <summary>Writable payload area; for the creator, before the frame is shared.</summary>
    public Span<byte> Payload => Buffer.AsSpan(MuxProtocol.FrameHeaderBytes, PayloadLength);

    public ReadOnlySpan<byte> Bytes => Buffer.AsSpan(0, Length);

    private byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(MuxOutboundFrame));

    public static MuxOutboundFrame Rent(MuxFrameKind kind, int payloadLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);
        if (payloadLength > MuxProtocol.MaxFrameBytes)
        {
            throw new MuxProtocolException(MuxErrorCodes.FrameTooLarge,
                $"A {payloadLength}-byte payload exceeds the {MuxProtocol.MaxFrameBytes}-byte frame limit.");
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(MuxProtocol.FrameHeaderBytes + payloadLength);
        MuxFrameCodec.WriteHeader(buffer, kind, payloadLength);
        return new MuxOutboundFrame(kind, buffer, payloadLength);
    }

    /// <summary>
    /// Increment-then-corrective-decrement is two separate atomic operations: a concurrent AddRef
    /// on an already-released frame (count 0) could interleave between them and observe a
    /// transient post-increment value greater than 1, succeeding on a frame whose buffer is
    /// already back in the pool. A single CompareExchange retry loop makes "is this frame still
    /// alive" and "take a reference" one atomic step, so no caller ever sees a torn state.
    /// </summary>
    public void AddRef()
    {
        int current = Volatile.Read(ref _refCount);
        while (true)
        {
            if (current <= 0)
            {
                throw new ObjectDisposedException(nameof(MuxOutboundFrame), "AddRef on a frame already returned to the pool.");
            }

            int observed = Interlocked.CompareExchange(ref _refCount, current + 1, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    public void Release()
    {
        int remaining = Interlocked.Decrement(ref _refCount);
        if (remaining == 0)
        {
            byte[]? buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
        }
        else if (remaining < 0)
        {
            throw new InvalidOperationException("MuxOutboundFrame released more times than it was referenced.");
        }
    }

    public void WriteTo(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Write(Bytes);
    }
}
