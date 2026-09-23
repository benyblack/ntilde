using System.Buffers.Binary;

namespace Ntilde.Mux.Contracts;

public static class MuxFrameCodec
{
    public static bool IsKnownKind(byte kind) => kind is 0x01 or 0x02 or 0x03 or 0x10 or 0x11 or 0x12 or 0x13;

    public static void WriteHeader(Span<byte> destination, MuxFrameKind kind, int payloadLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);
        destination[0] = (byte)kind;
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(1, 4), (uint)payloadLength);
    }

    /// <summary>
    /// Validates a received header. Length is checked before kind so an oversize announcement is
    /// always reported as such.
    /// </summary>
    public static (MuxFrameKind Kind, int PayloadLength) ReadHeader(ReadOnlySpan<byte> header, int maxFrameBytes)
    {
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(1, 4));
        if (length > (uint)maxFrameBytes)
        {
            throw new MuxProtocolException(MuxErrorCodes.FrameTooLarge,
                $"Frame announces {length} payload bytes; the limit is {maxFrameBytes}.");
        }

        if (!IsKnownKind(header[0]))
        {
            throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"Unknown frame kind 0x{header[0]:X2}.");
        }

        return ((MuxFrameKind)header[0], (int)length);
    }
}
