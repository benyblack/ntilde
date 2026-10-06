using Ntilde.Shell;
using Xunit;

namespace Ntilde.Tests.Shell;

// RAM audit, 2026-10-05: after five inline images an idle Ntilde sat at 456 MB private working set
// indefinitely; one full GC brought it to 198 MB. The GC only runs when something allocates, and an
// idle terminal barely does, so the garbage a burst leaves behind was never collected.
public sealed class IdleMemoryReclaimerTests
{
    private const long MB = 1024 * 1024;

    private readonly FakeRuntime _runtime = new();

    private IdleMemoryReclaimer NewReclaimer() =>
        new(() => _runtime.Read(), () => _runtime.Collect(), reclaimableThresholdBytes: 64 * MB, idleAllocationBudgetBytes: 4 * MB);

    [Fact]
    public void Collects_once_the_app_goes_quiet_with_a_large_reclaimable_heap()
    {
        var reclaimer = NewReclaimer();
        _runtime.Set(heap: 420 * MB, liveAfterLastFullGc: 60 * MB, allocated: 1_000 * MB);
        Assert.False(reclaimer.Tick()); // first sample: no allocation history yet

        _runtime.Allocated += 1 * MB;

        Assert.True(reclaimer.Tick());
        Assert.Equal(1, _runtime.Collections);
    }

    [Fact]
    public void Does_not_collect_while_the_app_is_still_allocating()
    {
        var reclaimer = NewReclaimer();
        _runtime.Set(heap: 420 * MB, liveAfterLastFullGc: 60 * MB, allocated: 1_000 * MB);
        reclaimer.Tick();

        _runtime.Allocated += 50 * MB; // output still streaming: the GC will run on its own

        Assert.False(reclaimer.Tick());
        Assert.Equal(0, _runtime.Collections);
    }

    [Fact]
    public void Does_not_collect_when_little_could_be_reclaimed()
    {
        var reclaimer = NewReclaimer();
        _runtime.Set(heap: 100 * MB, liveAfterLastFullGc: 60 * MB, allocated: 1_000 * MB);
        reclaimer.Tick();

        _runtime.Allocated += 1 * MB;

        Assert.False(reclaimer.Tick());
        Assert.Equal(0, _runtime.Collections);
    }

    [Fact]
    public void Does_not_collect_again_once_the_heap_is_back_to_its_live_size()
    {
        var reclaimer = NewReclaimer();
        _runtime.Set(heap: 420 * MB, liveAfterLastFullGc: 60 * MB, allocated: 1_000 * MB);
        reclaimer.Tick();
        _runtime.Allocated += 1 * MB;
        Assert.True(reclaimer.Tick());

        for (int i = 0; i < 5; i++)
        {
            _runtime.Allocated += 1 * MB;
            Assert.False(reclaimer.Tick());
        }

        Assert.Equal(1, _runtime.Collections);
    }

    [Fact]
    public void Reading_the_runtime_reports_a_live_heap()
    {
        var probe = IdleMemoryReclaimer.ReadRuntime();

        Assert.True(probe.HeapBytes > 0);
        Assert.True(probe.TotalAllocatedBytes > 0);
        Assert.True(probe.LiveAfterLastFullGcBytes >= 0);
    }

    private sealed class FakeRuntime
    {
        public long Heap;
        public long LiveAfterLastFullGc;
        public long Allocated;
        public int Collections;

        public void Set(long heap, long liveAfterLastFullGc, long allocated)
        {
            Heap = heap;
            LiveAfterLastFullGc = liveAfterLastFullGc;
            Allocated = allocated;
        }

        public IdleMemoryReclaimer.Probe Read() => new(Heap, LiveAfterLastFullGc, Allocated);

        // A full GC leaves only the live set behind and becomes the new baseline.
        public void Collect()
        {
            Collections++;
            Heap = LiveAfterLastFullGc;
        }
    }
}
