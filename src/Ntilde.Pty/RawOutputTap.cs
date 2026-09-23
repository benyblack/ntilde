using System;
using System.Collections.Generic;

namespace Ntilde.Pty
{
    /// <summary>
    /// The shared implementation behind <see cref="ITerminalByteOutput"/>: fans raw output chunks out
    /// to subscribers, with first-subscriber replay.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Replay exists because <c>RustPtySession</c> starts reading in its constructor, so bytes can
    /// arrive before anyone had a chance to subscribe - the same reason the string path buffers for
    /// its first subscriber.
    /// </para>
    /// <para>
    /// Retention is not unconditional: a session whose string event gets a subscriber first (every
    /// GUI pane) calls <see cref="StopRetaining"/>, which drops what was kept and keeps nothing more.
    /// From then on a chunk nobody taps costs a lock and nothing else - no copy, no allocation.
    /// </para>
    /// <para>
    /// Handlers run under this tap's lock, on the producer thread, so they must not block for long
    /// (a bounded-queue <c>Add</c> that exerts back-pressure is fine; waiting on the thread that is
    /// unsubscribing is not). A throwing handler is logged and contained: this runs inside a read
    /// loop whose catch-all would otherwise end the session.
    /// </para>
    /// </remarks>
    public sealed class RawOutputTap
    {
        private readonly object _gate = new();
        private Action<ReadOnlyMemory<byte>>? _handler;
        private List<byte[]>? _retained = new();

        public void Subscribe(Action<ReadOnlyMemory<byte>>? handler)
        {
            if (handler is null) return;
            lock (_gate)
            {
                List<byte[]>? replay = _retained;
                _retained = null;
                _handler += handler;
                if (replay is null) return;
                foreach (byte[] chunk in replay)
                {
                    Invoke(handler, chunk);
                }
            }
        }

        public void Unsubscribe(Action<ReadOnlyMemory<byte>>? handler)
        {
            if (handler is null) return;
            lock (_gate)
            {
                _handler -= handler;
            }
        }

        /// <summary>Drops retained chunks and stops retaining. Idempotent.</summary>
        public void StopRetaining()
        {
            lock (_gate)
            {
                _retained = null;
            }
        }

        /// <summary>Publishes one chunk. Copies it only when someone will see it.</summary>
        public void Publish(ReadOnlySpan<byte> chunk)
        {
            if (chunk.IsEmpty) return;
            lock (_gate)
            {
                if (_handler is { } handler)
                {
                    Invoke(handler, chunk.ToArray());
                    return;
                }

                _retained?.Add(chunk.ToArray());
            }
        }

        /// <summary>
        /// Invokes each subscriber on its own. A multicast delegate invoked as one call stops at the
        /// first throw, so every later subscriber would silently miss the chunk - and a subscriber
        /// that misses a chunk (a mux's decoder) is desynchronised for good. The enumeration is
        /// allocation-free, so the common single-subscriber case costs nothing extra.
        /// </summary>
        private static void Invoke(Action<ReadOnlyMemory<byte>> handler, byte[] chunk)
        {
            foreach (Action<ReadOnlyMemory<byte>> subscriber in Delegate.EnumerateInvocationList(handler))
            {
                try
                {
                    subscriber(chunk);
                }
                catch (Exception ex)
                {
                    PtyLogger.Error($"[RawOutputTap] A raw output subscriber threw; the chunk is dropped for it: {ex}");
                }
            }
        }
    }
}
