using Ntilde.VT;

namespace Ntilde.VT.Tests;

public sealed class NativeSixelBudgetTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(257, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 67108865)]
    public void Test_ceilings_can_only_reduce_the_production_budget(int placements, long bytes)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new NativeSixelBudget(placements, bytes));

    [Fact]
    public void Admission_reserves_pixels_and_fixed_scratch_before_work()
    {
        var budget = new NativeSixelBudget();
        var allocation = Assert.IsType<NativeSixelAllocation>(budget.TryReserve(16, 18, 100));
        Assert.Equal((1, 16L * 18 * 6 + 16384), budget.Usage);
        budget.CompleteDecode(allocation);
        Assert.Equal((1, 16L * 18 * 4), budget.Usage);
        budget.CompleteDecode(allocation);
        Assert.Equal((1, 16L * 18 * 4), budget.Usage);
        budget.CompleteDisposal(allocation);
        Assert.Equal((0, 0L), budget.Usage);
        budget.CompleteDisposal(allocation);
        Assert.Equal((0, 0L), budget.Usage);
    }

    [Fact]
    public void Scratch_and_retained_pixels_both_block_admission_until_their_owner_releases_them()
    {
        const long work = 16L * 18 * 6 + 16384;
        var budget = new NativeSixelBudget(maxBytes: work);
        var allocation = Assert.IsType<NativeSixelAllocation>(budget.TryReserve(16, 18, 100));
        Assert.Null(budget.TryReserve(1, 1, 1));
        budget.CompleteDecode(allocation);
        Assert.Null(budget.TryReserve(16, 18, 100));
        budget.CompleteDisposal(allocation);
        Assert.NotNull(budget.TryReserve(16, 18, 100));
    }

    [Fact]
    public void Shared_handle_aliases_charge_placements_separately_without_double_charging_pixels()
    {
        var budget = new NativeSixelBudget(maxPlacements: 2);
        var allocation = Assert.IsType<NativeSixelAllocation>(budget.TryReserve(16, 18, 100));
        long bytes = budget.Usage.Bytes;
        Assert.True(budget.TryAdmitPlacement(allocation));
        Assert.Equal((1, bytes), budget.Usage);
        Assert.True(budget.TryAdmitPlacement(allocation));
        Assert.Equal((2, bytes), budget.Usage);
        Assert.False(budget.TryAdmitPlacement(allocation));
        Assert.Null(budget.TryReserve(1, 1, 1));
        budget.CompleteDisposal(allocation);
        Assert.Equal((0, 0L), budget.Usage);
        Assert.False(budget.TryAdmitPlacement(allocation));
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(int.MaxValue, 1, 1)]
    [InlineData(1, int.MaxValue, 1)]
    [InlineData(1, 1, 0)]
    [InlineData(1, 1, int.MaxValue)]
    public void Invalid_or_overflowing_work_is_refused_without_a_charge(int width, int height, int chars)
    {
        var budget = new NativeSixelBudget();
        Assert.Null(budget.TryReserve(width, height, chars));
        Assert.Equal((0, 0L), budget.Usage);
    }

    [Fact]
    public void Default_byte_limit_includes_peak_work_even_when_the_final_bitmap_alone_would_fit()
    {
        var budget = new NativeSixelBudget();
        var allocated = new List<NativeSixelAllocation>();
        for (int i = 0; i < 3; i++)
        {
            var allocation = Assert.IsType<NativeSixelAllocation>(budget.TryReserve(2000, 2000, 10));
            allocated.Add(allocation);
            Assert.True(budget.Usage.Bytes <= NativeSixelBudget.MaxBytes);
            budget.CompleteDecode(allocation);
        }
        Assert.Equal((3, 48_000_000L), budget.Usage);
        Assert.Null(budget.TryReserve(2000, 2000, 10)); // Final pixels fit; peak scratch does not.
        budget.CompleteDisposal(allocated[0]);
        var next = Assert.IsType<NativeSixelAllocation>(budget.TryReserve(2000, 2000, 10));
        Assert.True(budget.Usage.Bytes <= NativeSixelBudget.MaxBytes);
        foreach (var allocation in allocated) budget.CompleteDisposal(allocation);
        budget.CompleteDisposal(next);
        Assert.Equal((0, 0L), budget.Usage);
    }

    [Fact]
    public void Foreign_accounting_cannot_release_or_admit_another_owners_reservation()
    {
        var owner = new NativeSixelBudget();
        var other = new NativeSixelBudget();
        var allocation = Assert.IsType<NativeSixelAllocation>(owner.TryReserve(1, 6, 2));
        var before = owner.Usage;
        Assert.Throws<InvalidOperationException>(() => other.CompleteDecode(allocation));
        Assert.Throws<InvalidOperationException>(() => other.CompleteDisposal(allocation));
        Assert.Throws<InvalidOperationException>(() => other.TryAdmitPlacement(allocation));
        Assert.Equal(before, owner.Usage);
        Assert.Equal((0, 0L), other.Usage);
    }

    [Fact]
    public async Task Concurrent_admissions_never_oversubscribe_either_limit()
    {
        var budget = new NativeSixelBudget(maxPlacements: 8);
        var reservations = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => budget.TryReserve(100, 100, 1), TestContext.Current.CancellationToken)));
        Assert.Equal(8, reservations.Count(a => a is not null));
        Assert.Equal(8, budget.Usage.Placements);
        foreach (var allocation in reservations.OfType<NativeSixelAllocation>()) budget.CompleteDisposal(allocation);
        Assert.Equal((0, 0L), budget.Usage);
    }
}
