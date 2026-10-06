using System;
using System.Runtime;
using System.Threading;

namespace Ntilde.Shell
{
    /// <summary>
    /// Runs one full, compacting garbage collection once the app goes quiet after a burst that
    /// left a lot of garbage behind - inline images, a reflow of a long scrollback, a flood of
    /// output.
    /// </summary>
    /// <remarks>
    /// The GC only runs when something allocates, and an idle terminal barely does, so that garbage
    /// otherwise stays resident indefinitely: measured on a release build, five inline images left
    /// an idle window at 456 MB private working set, and a single full GC brought it to 198 MB.
    /// "Quiet" is judged by the allocation rate rather than input, so a streaming command keeps
    /// deferring the collection (the GC is running on its own then anyway), and the threshold keeps
    /// it to one blocking collection per burst instead of a periodic cost.
    /// </remarks>
    public sealed class IdleMemoryReclaimer
    {
        /// <summary>One sample of the runtime's heap counters.</summary>
        public readonly record struct Probe(long HeapBytes, long LiveAfterLastFullGcBytes, long TotalAllocatedBytes);

        private readonly Func<Probe> _probe;
        private readonly Action _collect;
        private readonly long _reclaimableThresholdBytes;
        private readonly long _idleAllocationBudgetBytes;
        private long? _lastAllocatedBytes;

        public IdleMemoryReclaimer(Func<Probe> probe, Action collect, long reclaimableThresholdBytes, long idleAllocationBudgetBytes)
        {
            _probe = probe ?? throw new ArgumentNullException(nameof(probe));
            _collect = collect ?? throw new ArgumentNullException(nameof(collect));
            _reclaimableThresholdBytes = reclaimableThresholdBytes;
            _idleAllocationBudgetBytes = idleAllocationBudgetBytes;
        }

        /// <summary>
        /// Called on a fixed interval. Collects when less than the idle budget was allocated since
        /// the previous tick and the heap holds at least the threshold more than the last full
        /// collection left live. Returns whether it collected.
        /// </summary>
        public bool Tick()
        {
            Probe sample = _probe();
            long? previous = _lastAllocatedBytes;
            _lastAllocatedBytes = sample.TotalAllocatedBytes;
            if (previous is null) return false;

            bool quiet = sample.TotalAllocatedBytes - previous.Value <= _idleAllocationBudgetBytes;
            long reclaimable = sample.HeapBytes - sample.LiveAfterLastFullGcBytes;
            if (!quiet || reclaimable < _reclaimableThresholdBytes) return false;

            _collect();
            return true;
        }

        /// <summary>
        /// Current heap size (dead objects included until a GC sweeps them), the heap size the most
        /// recent full collection left, and the process's running allocation total.
        /// </summary>
        public static Probe ReadRuntime()
        {
            var blocking = GC.GetGCMemoryInfo(GCKind.FullBlocking);
            var background = GC.GetGCMemoryInfo(GCKind.Background);
            var lastFull = blocking.Index >= background.Index ? blocking : background;
            return new Probe(
                GC.GetTotalMemory(forceFullCollection: false),
                lastFull.Index == 0 ? 0 : lastFull.HeapSizeBytes,
                GC.GetTotalAllocatedBytes(precise: false));
        }

        /// <summary>
        /// Full blocking collection that compacts the large object heap too (images and reflow
        /// scratch live there) and hands the freed memory back to the OS.
        /// </summary>
        public static void CollectAggressively()
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }

        private static Timer? s_timer;

        /// <summary>
        /// Starts the process-wide reclaimer on a thread-pool timer. Collecting off the UI thread is
        /// fine: a blocking GC suspends every managed thread wherever it is started from.
        /// </summary>
        public static void Start()
        {
            if (s_timer != null) return;
            var reclaimer = new IdleMemoryReclaimer(
                ReadRuntime,
                CollectAggressively,
                reclaimableThresholdBytes: 64L * 1024 * 1024,
                idleAllocationBudgetBytes: 4L * 1024 * 1024);
            var interval = TimeSpan.FromSeconds(5);
            s_timer = new Timer(_ =>
            {
                try { reclaimer.Tick(); }
                catch (Exception ex) { AppLogger.Log($"[IdleMemoryReclaimer] tick failed: {ex.Message}"); }
            }, null, interval, interval);
        }
    }
}
