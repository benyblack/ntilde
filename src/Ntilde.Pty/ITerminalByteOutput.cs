using System;

namespace Ntilde.Pty
{
    /// <summary>
    /// Optional companion to <see cref="ITerminalSession"/>: the session's output as raw bytes,
    /// before any UTF-8 decoding.
    /// </summary>
    /// <remarks>
    /// Exists for the multiplexer, whose headless session runs its own <see cref="Utf8ChunkDecoder"/>
    /// over these bytes so that its <c>ConsumedBytes</c>/<c>PendingTail</c> are the stream position a
    /// snapshot is tagged with. Raised on the session's producer thread, in the same order as the
    /// recorders' <c>RecordChunk</c> and as <see cref="ITerminalIO.OnOutputReceived"/>. Every chunk is
    /// a fresh array the producer never touches again; with more than one subscriber they share it,
    /// so treat it as read-only. Implemented through <see cref="RawOutputTap"/>, which replays chunks
    /// published before the first subscriber (see there for when that retention stops).
    /// </remarks>
    public interface ITerminalByteOutput
    {
        event Action<ReadOnlyMemory<byte>>? OnRawOutputReceived;
    }
}
