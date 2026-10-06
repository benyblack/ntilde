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

    // Closing a tab frees its scrollback and atlases, but that only shows in the heap counters after
    // a full GC: the last one ran while the tab was open and counted it live. Measured: two closed
    // tabs left 84 MB that a forced GC returned and the threshold alone never would.
    [Fact]
    public void A_requested_collection_runs_on_the_next_quiet_tick_below_the_threshold()
    {
        var reclaimer = NewReclaimer();
        _runtime.Set(committed: 105 * MB, liveAfterLastFullGc: 90 * MB, allocated: 1_000 * MB);
        reclaimer.Tick();

        reclaimer.RequestCollection();
        _runtime.Allocated += 1 * MB;

        Assert.True(reclaimer.Tick());
        Assert.Equal(1, _runtime.Collections);
    }

    [Fact]
    public void A_requested_collection_still_waits_for_the_app_to_go_quiet()
    {
        var reclaimer = NewReclaimer();
        _runtime.Set(committed: 105 * MB, liveAfterLastFullGc: 90 * MB, allocated: 1_000 * MB);
        reclaimer.Tick();
        reclaimer.RequestCollection();

        _runtime.Allocated += 50 * MB;
        Assert.False(reclaimer.Tick());

        _runtime.Allocated += 1 * MB;
        Assert.True(reclaimer.Tick());
        Assert.Equal(1, _runtime.Collections);
    }

    [Fact]
    public void A_request_is_satisfied_by_a_single_collection()
    {
        var reclaimer = NewReclaimer();
        _runtime.Set(committed: 105 * MB, liveAfterLastFullGc: 90 * MB, allocated: 1_000 * MB);
        reclaimer.Tick();
        reclaimer.RequestCollection();
        _runtime.Allocated += 1 * MB;
        Assert.True(reclaimer.Tick());

        _runtime.Committed = 105 * MB;
        for (int i = 0; i < CooldownTicks * 3; i++)
        {
            _runtime.Allocated += 1 * MB;
            reclaimer.Tick();
        }

        Assert.Equal(1, _runtime.Collections);
    }

    // Measured: restoring 15 tabs ran a 97 ms collection that freed 15 MB of 263 - with no full GC
    // yet there is no live baseline, and growing scrollback looked like garbage.
    [Fact]
    public void Does_not_collect_on_threshold_before_any_full_gc_has_run()
    {
        var reclaimer = NewReclaimer();
        _runtime.Set(committed: 263 * MB, liveAfterLastFullGc: 0, allocated: 1_000 * MB);
        _runtime.FullGcHasRun = false;
        reclaimer.Tick();

        _runtime.Allocated += 1 * MB;

        Assert.False(reclaimer.Tick());
        Assert.Equal(0, _runtime.Collections);
    }

    [Fact]
    public void A_request_is_honoured_before_any_full_gc_has_run()
    {
        var reclaimer = NewReclaimer();
        _runtime.Set(committed: 263 * MB, liveAfterLastFullGc: 0, allocated: 1_000 * MB);
        _runtime.FullGcHasRun = false;
        reclaimer.Tick();
        reclaimer.RequestCollection();

        _runtime.Allocated += 1 * MB;

        Assert.True(reclaimer.Tick());
    }

    // Measured: closing 14 of 15 tabs waited ~50 s for memory back because a collection a minute
    // earlier had started the cooldown. A close is a deliberate drop of state, not heuristics.
    [Fact]
    public void A_request_does_not_wait_out_the_cooldown()
    {
        var reclaimer = NewReclaimer();
        _runtime.Set(committed: 420 * MB, liveAfterLastFullGc: 60 * MB, allocated: 1_000 * MB);
        reclaimer.Tick();
        _runtime.Allocated += 1 * MB;
        Assert.True(reclaimer.Tick()); // starts the cooldown

        reclaimer.RequestCollection();
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
        public bool FullGcHasRun = true;
        public int Collections;

        public void Set(long committed, long liveAfterLastFullGc, long allocated)
        {
            Committed = committed;
            LiveAfterLastFullGc = liveAfterLastFullGc;
            Allocated = allocated;
        }

        public IdleMemoryReclaimer.Probe Read() => new(Committed, LiveAfterLastFullGc, Allocated, FullGcHasRun);

        // A full compacting GC hands everything but the live set back and becomes the new baseline.
        public void Collect()
        {
            Collections++;
            Committed = LiveAfterLastFullGc;
            FullGcHasRun = true;
        }
    }
}
