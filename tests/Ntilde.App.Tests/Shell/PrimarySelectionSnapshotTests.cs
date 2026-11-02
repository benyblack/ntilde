using Ntilde.Shell.Clipboard;

namespace Ntilde.Tests.Shell;

public class PrimarySelectionSnapshotTests
{
    [Fact]
    public async Task Republish_and_borrowed_buffer_reuse_cannot_change_an_open_snapshot()
    {
        var formats = new Dictionary<string, uint> { ["text/plain"] = 1, ["application/test"] = 2 };
        var provider = new Dictionary<uint, byte[]> { [1] = [1, 2], [2] = [3, 4] };
        using var snapshot = PrimarySelectionSnapshot.Capture(formats, null, (atom, _) => provider[atom]);
        provider[1][0] = 99; provider[2] = [100]; // Same owner and targets, newer publication.
        Assert.Equal(new byte[] { 1, 2 }, await snapshot.ReadAsync("text/plain"));
        Assert.Equal(new byte[] { 3, 4 }, await snapshot.ReadAsync("application/test"));
        var returned = await snapshot.ReadAsync("text/plain"); returned![0] = 88;
        Assert.Equal(new byte[] { 1, 2 }, await snapshot.ReadAsync("text/plain"));
        snapshot.Dispose(); Assert.Null(await snapshot.ReadAsync("text/plain"));
    }

    [Fact]
    public void Listing_does_not_fetch_data_and_an_explicit_read_captures_only_requested_MIMEs()
    {
        var formats = new Dictionary<string, uint> { ["text/plain"] = 1, ["application/test"] = 2 };
        using var listing = PrimarySelectionSnapshot.Capture(formats, [], (_, _) => throw new InvalidOperationException("No consent for data"));
        Assert.Equal(2, listing.Types.Count);
        var reads = new List<uint>();
        using var data = PrimarySelectionSnapshot.Capture(formats, ["text/plain"], (atom, _) => { reads.Add(atom); return [1]; });
        Assert.Equal(new uint[] { 1 }, reads);
    }

    [Fact]
    public async Task Aggregate_and_per_item_budgets_are_checked_before_results_are_copied_or_returned()
    {
        var formats = new Dictionary<string, uint> { ["text/plain"] = 1, ["application/test"] = 2 };
        var budgets = new List<int>();
        using var snapshot = PrimarySelectionSnapshot.Capture(formats, null, (_, remaining) => { budgets.Add(remaining); return [1, 2]; }, maxBytes: 4);
        Assert.Equal(new[] { 4, 2 }, budgets);
        await Assert.ThrowsAsync<IOException>(() => snapshot.ReadAsync("text/plain", 1));
        Assert.Throws<IOException>(() => PrimarySelectionSnapshot.Capture(formats, null, (_, _) => [1, 2, 3], maxBytes: 4));
    }
}
