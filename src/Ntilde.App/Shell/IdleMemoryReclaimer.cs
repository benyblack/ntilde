using System;
using System.Runtime;
using System.Threading;

namespace Ntilde.Shell
{
    /// <summary>
    /// Runs one full, compacting garbage collection once the app goes quiet after a burst that
    /// left a lot of memory behind - inline images, a reflow of a long scrollback, a flood of
    /// output, closed tabs.
    /// </summary>
    /// <remarks>
    /// The GC only runs when something allocates, and an idle terminal barely does, so that memory
    /// otherwise stays resident indefinitely: measured on a release build, five inline images left
    /// an idle window at 456 MB private working set, and a single full GC brought it to 198 MB. A
    /// background GC that does run sweeps the garbage but keeps the memory committed, which is why
    /// this compares committed memory rather than live bytes against what the last full collection
    /// left. "Quiet" is judged by the allocation rate rather than input, so a streaming command
    /// keeps deferring the collection, and a cooldown caps it at one blocking collection per
    /// <c>cooldownTicks</c> however the heap behaves.
    /// </remarks>
    public sealed class IdleMemoryReclaimer
    {
        /// <summary>One sample of the runtime's heap counters.</summary>
        public readonly record struct Probe(long CommittedBytes, long LiveAfterLastFullGcBytes, long TotalAllocatedBytes);

        private readonly Func<Probe> _probe;
        private readonly Action _collect;
        private readonly long _reclaimableThresholdBytes;
        private readonly long _idleAllocationBudgetBytes;
        private readonly int _cooldownTicks;
        private long? _lastAllocatedBytes;
        private int _ticksUntilAllowed;

        public IdleMemoryReclaimer(Func<Probe> probe, Action collect, long reclaimableThresholdBytes, long idleAllocationBudgetBytes, int cooldownTicks)
        {
            _probe = probe ?? throw new ArgumentNullException(nameof(probe));
            _collect = collect ?? throw new ArgumentNullException(nameof(collect));
            _reclaimableThresholdBytes = reclaimableThresholdBytes;
            _idleAllocationBudgetBytes = idleAllocationBudgetBytes;
            _cooldownTicks = cooldownTicks;
        }

        /// <summary>
        /// Called on a fixed interval. Collects when less than the idle budget was allocated since
        /// the previous tick, the heap holds at least the threshold more committed memory than the
        /// last full collection left live, and the cooldown since the previous collection is over.
        /// Returns whether it collected.
        /// </summary>
        public bool Tick()
        {
            Probe sample = _probe();
            long? previous = _lastAllocatedBytes;
            _lastAllocatedBytes = sample.TotalAllocatedBytes;
            if (_ticksUntilAllowed > 0)
            {
                _ticksUntilAllowed--;
                return false;
            }
            if (previous is null) return false;

            bool quiet = sample.TotalAllocatedBytes - previous.Value <= _idleAllocationBudgetBytes;
            long reclaimable = sample.CommittedBytes - sample.LiveAfterLastFullGcBytes;
            if (!quiet || reclaimable < _reclaimableThresholdBytes) return false;

            _collect();
            _ticksUntilAllowed = _cooldownTicks;
            return true;
        }

        /// <summary>
        /// Memory the GC heap holds committed (at least everything allocated since the last GC),
        /// the bytes the most recent full collection left alive, and the process's running
        /// allocation total.
        /// </summary>
        public static Probe ReadRuntime()
        {
            var lastGc = GC.GetGCMemoryInfo(GCKind.Any);
            var blocking = GC.GetGCMemoryInfo(GCKind.FullBlocking);
            var background = GC.GetGCMemoryInfo(GCKind.Background);
            var lastFull = blocking.Index >= background.Index ? blocking : background;
            return new Probe(
                Math.Max(lastGc.TotalCommittedBytes, GC.GetTotalMemory(forceFullCollection: false)),
                lastFull.Index == 0 ? 0 : lastFull.HeapSizeBytes - lastFull.FragmentedBytes,
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
        /// Starts the process-wide reclaimer on a thread-pool timer: sampled every 5 s, collecting
        /// at most once a minute. Collecting off the UI thread is fine - a blocking GC suspends
        /// every managed thread wherever it is started from.
        /// </summary>
        public static void Start()
        {
            if (s_timer != null) return;
            var reclaimer = new IdleMemoryReclaimer(
                ReadRuntime,
                CollectAndLog,
                reclaimableThresholdBytes: 64L * 1024 * 1024,
                idleAllocationBudgetBytes: 4L * 1024 * 1024,
                cooldownTicks: 12);
            var interval = TimeSpan.FromSeconds(5);
            s_timer = new Timer(_ =>
            {
                try { reclaimer.Tick(); }
                catch (Exception ex) { AppLogger.Log($"[IdleMemoryReclaimer] tick failed: {ex.Message}"); }
            }, null, interval, interval);
        }

        private static void CollectAndLog()
        {
            long before = ReadRuntime().CommittedBytes;
            CollectAggressively();
            long after = GC.GetGCMemoryInfo(GCKind.Any).TotalCommittedBytes;
            AppLogger.Log($"[IdleMemoryReclaimer] idle collection: GC heap committed {before / (1024 * 1024)} MB -> {after / (1024 * 1024)} MB");
        }
    }
}
