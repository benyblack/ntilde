using Ntilde.Shell;
using Xunit;

namespace Ntilde.Tests.Shell;

// RAM audit, 2026-10-05: after five inline images an idle Ntilde sat at 456 MB private working set
// indefinitely; one full GC brought it to 198 MB. The GC only runs when something allocates, and an
// idle terminal barely does, so the garbage a burst leaves behind was never collected - or was swept
// by a background GC that kept the memory committed.
public sealed class IdleMemoryReclaimerTests
{
    private const long MB = 1024 * 1024;
    private const int CooldownTicks = 12;

    private readonly FakeRuntime _runtime = new();

    private IdleMemoryReclaimer NewReclaimer() =>
        new(() => _runtime.Read(), () => _runtime.Collect(),
            reclaimableThresholdBytes: 64 * MB, idleAllocationBudgetBytes: 4 * MB, cooldownTicks: CooldownTicks);

    [Fact]
    public void Collects_once_the_app_goes_quiet_with_a_large_reclaimable_heap()
    {
        var reclaimer = NewReclaimer();
        _runtime.Set(committed: 420 * MB, liveAfterLastFullGc: 60 * MB, allocated: 1_000 * MB);
        Assert.False(reclaimer.Tick()); // first sample: no allocation history yet

        _runtime.Allocated += 1 * MB;

        Assert.True(reclaimer.Tick());
        Assert.Equal(1, _runtime.Collections);
    }

    [Fact]
    public void Does_not_collect_while_the_app_is_still_allocating()
    {
        var reclaimer = NewReclaimer();
        _runtime.Set(committed: 420 * MB, liveAfterLastFullGc: 60 * MB, allocated: 1_000 * MB);
        reclaimer.Tick();

        _runtime.Allocated += 50 * MB; // output still streaming: the GC will run on its own

        Assert.False(reclaimer.Tick());
        Assert.Equal(0, _runtime.Collections);
    }

    [Fact]
    public void Does_not_collect_when_little_could_be_reclaimed()
    {
        var reclaimer = NewReclaimer();
        _runtime.Set(committed: 100 * MB, liveAfterLastFullGc: 60 * MB, allocated: 1_000 * MB);
        reclaimer.Tick();

        _runtime.Allocated += 1 * MB;

        Assert.False(reclaimer.Tick());
        Assert.Equal(0, _runtime.Collections);
    }

    [Fact]
    public void Does_not_collect_again_once_the_heap_is_back_to_its_live_size()
    {
        var reclaimer = NewReclaimer();
        _runtime.Set(committed: 420 * MB, liveAfterLastFullGc: 60 * MB, allocated: 1_000 * MB);
        reclaimer.Tick();
        _runtime.Allocated += 1 * MB;
        Assert.True(reclaimer.Tick());

        for (int i = 0; i < CooldownTicks * 3; i++)
        {
            _runtime.Allocated += 1 * MB;
            Assert.False(reclaimer.Tick());
        }

        Assert.Equal(1, _runtime.Collections);
    }

    [Fact]
    public void Waits_out_a_cooldown_before_collecting_again()
    {
        var reclaimer = NewReclaimer();
        _runtime.Set(committed: 420 * MB, liveAfterLastFullGc: 60 * MB, allocated: 1_000 * MB);
        reclaimer.Tick();
        _runtime.Allocated += 1 * MB;
        Assert.True(reclaimer.Tick());

        // Memory the GC keeps committed above the live set for its own reasons must not turn into
        // a full collection on every quiet tick.
        _runtime.Committed = 200 * MB;
        for (int i = 0; i < CooldownTicks; i++)
        {
            _runtime.Allocated += 1 * MB;
            Assert.False(reclaimer.Tick());
        }

        _runtime.Allocated += 1 * MB;
        Assert.True(reclaimer.Tick());
        Assert.Equal(2, _runtime.Collections);
    }

    [Fact]
    public void Reading_the_runtime_reports_the_heap()
    {
        var probe = IdleMemoryReclaimer.ReadRuntime();

        Assert.True(probe.CommittedBytes > 0);
        Assert.True(probe.TotalAllocatedBytes > 0);
        Assert.True(probe.LiveAfterLastFullGcBytes >= 0);
    }

    private sealed class FakeRuntime
    {
        public long Committed;
        public long LiveAfterLastFullGc;
        public long Allocated;
        public int Collections;

        public void Set(long committed, long liveAfterLastFullGc, long allocated)
        {
            Committed = committed;
            LiveAfterLastFullGc = liveAfterLastFullGc;
            Allocated = allocated;
        }

        public IdleMemoryReclaimer.Probe Read() => new(Committed, LiveAfterLastFullGc, Allocated);

        // A full compacting GC hands everything but the live set back and becomes the new baseline.
        public void Collect()
        {
            Collections++;
            Committed = LiveAfterLastFullGc;
        }
    }
}
