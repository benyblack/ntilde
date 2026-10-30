using System;
using System.Threading;
using Ntilde.Rendering;

namespace Ntilde.Shell
{
    /// <summary>
    /// A <see cref="TerminalView"/>'s glyph atlas and row-picture cache, disposed as soon as nothing can draw with
    /// them any more (#644).
    /// </summary>
    /// <remarks>
    /// The view holds them until its pane is disposed (<see cref="TerminalView.Retire"/>), and each draw operation
    /// built from them holds a lease until the compositor disposes it, which can be after the view is gone: the
    /// render thread may still be drawing the view's last frame. Whichever lets go last disposes them. Before, a
    /// closed pane's atlases waited for the finalizers of its Skia objects.
    /// </remarks>
    internal sealed class TerminalRenderCaches
    {
        private int _holders = 1; // the view's hold, then one per lease
        private bool _released; // the view let go; UI thread

        public TerminalRenderCaches(GlyphCache glyphs, RowImageCache rows)
        {
            Glyphs = glyphs;
            Rows = rows;
        }

        public GlyphCache Glyphs { get; }

        public RowImageCache Rows { get; }

        /// <summary>True once the caches are disposed.</summary>
        public bool IsDisposed => Volatile.Read(ref _holders) == 0;

        /// <summary>
        /// A lease for a draw operation, which disposes it when it is done with the caches; <c>null</c> once the view
        /// has let them go. A retired view can still be drawn before it leaves the visual tree, and draws without
        /// them: the lease is the only way to the caches for drawing. UI thread.
        /// </summary>
        public Lease? TryLease()
        {
            if (_released) return null;
            Interlocked.Increment(ref _holders); // the view's hold keeps the count above zero here
            return new Lease(this);
        }

        /// <summary>The view lets go of the caches, for good. UI thread; a second call does nothing.</summary>
        public void Release()
        {
            if (_released) return;
            _released = true;
            Unhold();
        }

        private void Unhold()
        {
            if (Interlocked.Decrement(ref _holders) == 0)
            {
                Glyphs.Dispose();
                Rows.Dispose();
            }
        }

        /// <summary>A draw operation's hold on the caches, which it draws with until it disposes the lease.</summary>
        public sealed class Lease : IDisposable
        {
            private TerminalRenderCaches? _caches;

            internal Lease(TerminalRenderCaches caches)
            {
                _caches = caches;
                Glyphs = caches.Glyphs;
                Rows = caches.Rows;
            }

            public GlyphCache Glyphs { get; }

            public RowImageCache Rows { get; }

            // Once, from whichever thread disposes the draw operation.
            public void Dispose() => Interlocked.Exchange(ref _caches, null)?.Unhold();
        }
    }
}
