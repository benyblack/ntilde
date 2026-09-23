using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Ntilde.Mux.Contracts;

/// <summary>
/// Builders and parsers for every frame payload. Binary layouts (spec §6), all little-endian,
/// Guids in <see cref="Guid.TryWriteBytes(Span{byte})"/> layout:
/// Input = id[16] + utf8; Output = id[16] + seq i64 + bytes; ResizeEvent = id[16] + seq i64 +
/// cols i32 + rows i32; Snapshot = requestId i64 + id[16] + seq i64 + json.
/// Parsers length-check before slicing: every byte here came from a peer.
/// </summary>
public static class MuxFrames
{
    public const int SessionIdBytes = 16;
    public const int OutputHeaderBytes = SessionIdBytes + sizeof(long);
    public const int ResizeEventPayloadBytes = SessionIdBytes + sizeof(long) + (2 * sizeof(int));
    public const int SnapshotHeaderBytes = sizeof(long) + SessionIdBytes + sizeof(long);

    public static MuxOutboundFrame Request(MuxRequest request) =>
        Json(MuxFrameKind.Request, request, MuxJsonContext.Default.MuxRequest);

    public static MuxOutboundFrame Response(MuxResponse response) =>
        Json(MuxFrameKind.Response, response, MuxJsonContext.Default.MuxResponse);

    public static MuxOutboundFrame Notification(MuxNotification notification) =>
        Json(MuxFrameKind.Notification, notification, MuxJsonContext.Default.MuxNotification);

    public static MuxOutboundFrame Json<T>(MuxFrameKind kind, T value, JsonTypeInfo<T> typeInfo)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        MuxOutboundFrame frame = MuxOutboundFrame.Rent(kind, json.Length);
        json.CopyTo(frame.Payload);
        return frame;
    }

    public static MuxOutboundFrame Input(Guid sessionId, ReadOnlySpan<byte> utf8)
    {
        MuxOutboundFrame frame = MuxOutboundFrame.Rent(MuxFrameKind.Input, SessionIdBytes + utf8.Length);
        Span<byte> p = frame.Payload;
        WriteGuid(p, sessionId);
        utf8.CopyTo(p[SessionIdBytes..]);
        return frame;
    }

    public static MuxOutboundFrame Output(Guid sessionId, long seq, ReadOnlySpan<byte> data)
    {
        MuxOutboundFrame frame = MuxOutboundFrame.Rent(MuxFrameKind.Output, OutputHeaderBytes + data.Length);
        Span<byte> p = frame.Payload;
        WriteGuid(p, sessionId);
        BinaryPrimitives.WriteInt64LittleEndian(p.Slice(SessionIdBytes, 8), seq);
        data.CopyTo(p[OutputHeaderBytes..]);
        return frame;
    }

    public static MuxOutboundFrame ResizeEvent(Guid sessionId, long seq, int cols, int rows)
    {
        MuxOutboundFrame frame = MuxOutboundFrame.Rent(MuxFrameKind.ResizeEvent, ResizeEventPayloadBytes);
        Span<byte> p = frame.Payload;
        WriteGuid(p, sessionId);
        BinaryPrimitives.WriteInt64LittleEndian(p.Slice(16, 8), seq);
        BinaryPrimitives.WriteInt32LittleEndian(p.Slice(24, 4), cols);
        BinaryPrimitives.WriteInt32LittleEndian(p.Slice(28, 4), rows);
        return frame;
    }

    public static MuxOutboundFrame Snapshot(long requestId, Guid sessionId, long seq, ReadOnlySpan<byte> snapshotJson)
    {
        MuxOutboundFrame frame = MuxOutboundFrame.Rent(MuxFrameKind.Snapshot, SnapshotHeaderBytes + snapshotJson.Length);
        Span<byte> p = frame.Payload;
        BinaryPrimitives.WriteInt64LittleEndian(p[..8], requestId);
        WriteGuid(p.Slice(8, SessionIdBytes), sessionId);
        BinaryPrimitives.WriteInt64LittleEndian(p.Slice(24, 8), seq);
        snapshotJson.CopyTo(p[SnapshotHeaderBytes..]);
        return frame;
    }

    public static bool TryParseInput(ReadOnlySpan<byte> payload, out Guid sessionId, out ReadOnlySpan<byte> utf8)
    {
        if (payload.Length < SessionIdBytes) { sessionId = default; utf8 = default; return false; }
        sessionId = new Guid(payload[..SessionIdBytes]);
        utf8 = payload[SessionIdBytes..];
        return true;
    }

    public static bool TryParseOutput(ReadOnlySpan<byte> payload, out Guid sessionId, out long seq, out ReadOnlySpan<byte> data)
    {
        if (payload.Length < OutputHeaderBytes) { sessionId = default; seq = 0; data = default; return false; }
        sessionId = new Guid(payload[..SessionIdBytes]);
        seq = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(SessionIdBytes, 8));
        data = payload[OutputHeaderBytes..];
        return true;
    }

    public static bool TryParseResizeEvent(ReadOnlySpan<byte> payload, out Guid sessionId, out long seq, out int cols, out int rows)
    {
        if (payload.Length != ResizeEventPayloadBytes) { sessionId = default; seq = 0; cols = 0; rows = 0; return false; }
        sessionId = new Guid(payload[..SessionIdBytes]);
        seq = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(16, 8));
        cols = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(24, 4));
        rows = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(28, 4));
        return true;
    }

    public static bool TryParseSnapshot(ReadOnlySpan<byte> payload, out long requestId, out Guid sessionId, out long seq, out ReadOnlySpan<byte> snapshotJson)
    {
        if (payload.Length < SnapshotHeaderBytes) { requestId = 0; sessionId = default; seq = 0; snapshotJson = default; return false; }
        requestId = BinaryPrimitives.ReadInt64LittleEndian(payload[..8]);
        sessionId = new Guid(payload.Slice(8, SessionIdBytes));
        seq = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(24, 8));
        snapshotJson = payload[SnapshotHeaderBytes..];
        return true;
    }

    /// <summary>Parses a JSON payload; malformed, null or incomplete (missing <c>required</c>) → protocol_error.</summary>
    public static T ParseJson<T>(ReadOnlySpan<byte> payload, JsonTypeInfo<T> typeInfo) where T : class
    {
        T? value;
        try
        {
            value = JsonSerializer.Deserialize(payload, typeInfo);
        }
        catch (JsonException ex)
        {
            throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"Malformed {typeof(T).Name}: {ex.Message}", ex);
        }

        return value ?? throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"{typeof(T).Name} payload was null.");
    }

    /// <summary>Parses request params / response results with the same rules as <see cref="ParseJson{T}"/>.</summary>
    public static T ParseParams<T>(JsonElement? element, JsonTypeInfo<T> typeInfo) where T : class
    {
        if (element is not { } e)
        {
            throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"{typeof(T).Name} is missing.");
        }

        T? value;
        try
        {
            value = JsonSerializer.Deserialize(e, typeInfo);
        }
        catch (JsonException ex)
        {
            throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"Malformed {typeof(T).Name}: {ex.Message}", ex);
        }

        return value ?? throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"{typeof(T).Name} was null.");
    }

    public static JsonElement ToElement<T>(T value, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.SerializeToElement(value, typeInfo);

    private static void WriteGuid(Span<byte> destination, Guid id)
    {
        if (!id.TryWriteBytes(destination))
        {
            throw new InvalidOperationException("Destination too small for a Guid.");
        }
    }
}
