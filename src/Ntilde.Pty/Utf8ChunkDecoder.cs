using System;
using System.Buffers;
using System.Text;
using System.Text.Unicode;

namespace Ntilde.Pty
{
    /// <summary>
    /// Stateful UTF-8 → UTF-16 chunk decoder with an <em>inspectable</em> pending tail.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Behaviourally identical to <c>Encoding.UTF8.GetDecoder()</c> used with
    /// <c>flush: false</c>, which is what it replaces on both live output paths
    /// (<c>RustPtySession.ReadLoop</c>, <c>NativeSshSession.EmitOutput</c>). Identical is the
    /// requirement, not an aspiration: a difference of one U+FFFD changes what
    /// <c>AnsiParser</c> sees, which changes the screen. <c>Utf8ChunkDecoderTests</c> pins that
    /// differentially, across every split offset of every corpus input including malformed ones.
    /// </para>
    /// <para>
    /// The reason for a bespoke type is the one thing <see cref="Decoder"/> will not tell you:
    /// how many bytes it is holding back. The multiplexer snapshot has to record a byte offset
    /// into the session's output stream plus the partial code point straddling it, so an
    /// attaching pane can resume decoding at exactly the right place. <see cref="ConsumedBytes"/>
    /// and <see cref="PendingTail"/> are that pair.
    /// </para>
    /// <para>
    /// Allocation-free per chunk: the carry lives in a fixed 3-byte field and the splice buffer
    /// is stack-allocated, so the read loop's per-chunk cost is unchanged.
    /// </para>
    /// </remarks>
    public sealed class Utf8ChunkDecoder
    {
        /// <summary>
        /// Longest incomplete-but-valid UTF-8 prefix: a 4-byte sequence missing its last byte.
        /// </summary>
        private const int MaxTailLength = 3;

        /// <summary>
        /// Carry plus enough of the next chunk to finish any sequence the carry could start
        /// (3 + 4). Sized so one splice always decides the carry rather than looping.
        /// </summary>
        private const int SpliceLength = MaxTailLength + 4;

        private readonly byte[] _tail = new byte[MaxTailLength];
        private int _tailLength;
        private long _consumedBytes;

        /// <summary>
        /// Input bytes this decoder has finished with — either decoded into output, or discarded
        /// by <see cref="Reset"/>. Bytes still held in <see cref="PendingTail"/> are NOT counted,
        /// so the offset of the next byte the caller should feed is
        /// <c>ConsumedBytes + PendingTail.Length</c>. Monotonic for the life of the instance.
        /// </summary>
        public long ConsumedBytes => _consumedBytes;

        /// <summary>
        /// The incomplete-but-valid UTF-8 prefix held back from the last <see cref="Decode"/>,
        /// at most <see cref="MaxTailLength"/> bytes. Empty when nothing is pending.
        /// </summary>
        public ReadOnlySpan<byte> PendingTail => _tail.AsSpan(0, _tailLength);

        /// <summary>
        /// Upper bound on the chars a <see cref="Decode"/> of <paramref name="byteCount"/> bytes
        /// can emit. Matches <c>Encoding.UTF8.GetMaxCharCount</c>: the carry contributes at most
        /// one char, because a held-back prefix is by construction a single maximal subpart and
        /// therefore at most one U+FFFD.
        /// </summary>
        public static int GetMaxCharCount(int byteCount) => Encoding.UTF8.GetMaxCharCount(byteCount);

        /// <summary>
        /// Drops the pending tail. Use when the byte stream itself broke (a failed read), where
        /// the held-back prefix will never be completed. The discarded bytes are added to
        /// <see cref="ConsumedBytes"/>: they are gone from the stream, and the counter's contract
        /// is "bytes this decoder will never look at again".
        /// </summary>
        public void Reset()
        {
            _consumedBytes += _tailLength;
            _tailLength = 0;
        }

        /// <summary>
        /// Decodes <paramref name="input"/>, appending to <paramref name="destination"/> and
        /// returning the number of chars written. <paramref name="destination"/> must be at
        /// least <see cref="GetMaxCharCount"/> of <paramref name="input"/>'s length.
        /// </summary>
        public int Decode(ReadOnlySpan<byte> input, Span<char> destination)
        {
            if (destination.Length < GetMaxCharCount(input.Length))
            {
                throw new ArgumentException(
                    $"Destination of {destination.Length} chars is too small for {input.Length} bytes; " +
                    $"needs {GetMaxCharCount(input.Length)}. Size it with Utf8ChunkDecoder.GetMaxCharCount.",
                    nameof(destination));
            }

            int written = 0;

            if (_tailLength > 0)
            {
                written += DrainCarry(ref input, destination);
            }

            if (!input.IsEmpty)
            {
                OperationStatus status = Utf8.ToUtf16(
                    input,
                    destination[written..],
                    out int bytesRead,
                    out int charsWritten,
                    replaceInvalidSequences: true,
                    isFinalBlock: false);

                // replaceInvalidSequences means InvalidData is impossible, and the destination is
                // pre-sized, so DestinationTooSmall is a caller bug we already rejected above.
                // Done or NeedMoreData are the only outcomes; both are handled identically.
                _ = status;

                written += charsWritten;
                _consumedBytes += bytesRead;

                int leftover = input.Length - bytesRead;
                if (leftover > 0)
                {
                    input[bytesRead..].CopyTo(_tail);
                    _tailLength = leftover;
                }
            }

            return written;
        }

        /// <summary>
        /// Splices the carried prefix with the head of <paramref name="input"/> and decodes what
        /// that completes, advancing <paramref name="input"/> past the bytes it took.
        /// </summary>
        private int DrainCarry(ref ReadOnlySpan<byte> input, Span<char> destination)
        {
            Span<byte> splice = stackalloc byte[SpliceLength];
            int carryLength = _tailLength;

            _tail.AsSpan(0, carryLength).CopyTo(splice);
            int taken = Math.Min(input.Length, SpliceLength - carryLength);
            input[..taken].CopyTo(splice[carryLength..]);
            int spliceLength = carryLength + taken;

            Utf8.ToUtf16(
                splice[..spliceLength],
                destination,
                out int bytesRead,
                out int charsWritten,
                replaceInvalidSequences: true,
                isFinalBlock: false);

            if (bytesRead < carryLength)
            {
                // Nothing completed: the whole splice is still one incomplete prefix, which can
                // only happen when `input` ran out. A prefix is at most 4 bytes, so the leftover
                // fits the carry; if it ever does not, the invariant this type rests on is wrong
                // and silence would corrupt the stream offset.
                int stillPending = spliceLength - bytesRead;
                if (stillPending > MaxTailLength)
                {
                    throw new InvalidOperationException(
                        $"UTF-8 carry of {stillPending} bytes exceeds the {MaxTailLength}-byte maximum " +
                        "for an incomplete sequence; the decoder's splice invariant is broken.");
                }

                splice[bytesRead..spliceLength].CopyTo(_tail);
                _tailLength = stillPending;
                _consumedBytes += bytesRead;
                input = input[taken..];
                return charsWritten;
            }

            _tailLength = 0;
            _consumedBytes += bytesRead;
            input = input[(bytesRead - carryLength)..];
            return charsWritten;
        }
    }
}
