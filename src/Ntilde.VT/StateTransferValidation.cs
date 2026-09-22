using System;

namespace Ntilde.VT
{
    /// <summary>
    /// The one validation policy every state-transfer import path follows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rule: clamp geometry and counts, reject malformed structure at the boundary.</b>
    /// </para>
    /// <para>
    /// A <see cref="TerminalStateSnapshot"/> is the only thing in Ntilde.VT that arrives from
    /// outside this process, so it is the only place the assembly has to decide what "untrusted"
    /// means. Phase 0 has no daemon and therefore no untrusted payload at all; the rule is written
    /// down now so that Phase 1 inherits a policy instead of four independent precedents - which
    /// is what the four import paths had before this type existed. One of them clamped stack
    /// length but not flag values, one checked its string caps, one clamped geometry but let a
    /// bad base64 blob throw halfway through a write lock, and one validated nothing at all.
    /// </para>
    /// <para>
    /// <b>Geometry and counts are clamped.</b> A row count, a column, a cursor position, a stack
    /// depth, a parameter-run length, a flag word: these are values where this build has its own
    /// limit and the honest answer to "the sender's is bigger" is to take as much as fits. The
    /// clamped result is still a coherent terminal, and refusing an attach because the peer's
    /// scrollback is longer would be worse than showing less of it. <see cref="ClampCount"/> and
    /// the call sites that use <c>Math.Clamp</c>/<c>Math.Min</c> directly are this half.
    /// </para>
    /// <para>
    /// <b>Structure is rejected.</b> Structure is anything whose value selects code rather than
    /// sizes it: an enum ordinal, an array index, a format version, a struct layout id, a base64
    /// blob, a <see cref="DateTime"/> tick count, a collection that must not be null. There is no
    /// defensible clamp for "state = 99" or "gl = 7" - every choice invents a stream position the
    /// sender never had - so these are refused, and refused with a message naming the field and
    /// the offending value.
    /// </para>
    /// <para>
    /// <b>Refuse before mutating.</b> Every check runs before the importing side takes its write
    /// lock and before it writes its first field. A half-applied buffer is worse than a rejected
    /// one: the rejected one is still the terminal the user was looking at, and the caller can
    /// report the failure and carry on. That is why the base64 blobs are decoded up front rather
    /// than lazily inside the import, even though decoding is the expensive part of the payload.
    /// </para>
    /// </remarks>
    internal static class StateTransferValidation
    {
        /// <summary>The exception every rejection throws, so callers can catch one type.</summary>
        internal static InvalidOperationException Reject(string what, string detail) =>
            new($"Terminal state import refused: {what} ({detail}).");

        /// <summary>Rejects a null collection or string that the import would dereference.</summary>
        internal static T RequirePresent<T>(T? value, string what) where T : class =>
            value ?? throw Reject(what, "missing");

        /// <summary>Rejects a value outside an inclusive range. For indices and ordinals only.</summary>
        internal static void RequireInRange(int value, int min, int max, string what)
        {
            if (value < min || value > max)
            {
                throw Reject(what, $"{value} is outside {min}..{max}");
            }
        }

        /// <summary>The counts half of the rule: take as much as this build can hold.</summary>
        internal static int ClampCount(int value, int max) => Math.Clamp(value, 0, max);

        /// <summary>
        /// Decodes a base64 cell blob, or rejects it. <see cref="Convert.FromBase64String"/> throws
        /// <see cref="FormatException"/> on malformed input, which is the wrong type and - far
        /// worse - was previously thrown from inside the importing write lock.
        /// </summary>
        internal static byte[]? DecodeBlob(string? base64, string what)
        {
            if (string.IsNullOrEmpty(base64))
            {
                return null;
            }

            // Upper bound: four base64 chars carry at most three bytes, and the slack covers a
            // final unpadded group. TryFromBase64String tolerates embedded whitespace, which only
            // ever makes the real output smaller than this.
            var decoded = new byte[(base64.Length / 4 * 3) + 3];

            if (!Convert.TryFromBase64String(base64, decoded, out int written))
            {
                throw Reject(what, $"not valid base64 ({base64.Length} chars)");
            }

            return decoded.Length == written ? decoded : decoded[..written];
        }

        /// <summary>
        /// Turns a tick count into a UTC <see cref="DateTime"/>, or rejects it. The constructor
        /// throws <see cref="ArgumentOutOfRangeException"/> out of range, which - like the base64
        /// decode - used to happen mid-import with half the buffer already replaced.
        /// </summary>
        internal static DateTime UtcFromTicks(long ticks, string what)
        {
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            {
                throw Reject(what, $"tick count {ticks} is not a representable DateTime");
            }

            return new DateTime(ticks, DateTimeKind.Utc);
        }
    }
}
