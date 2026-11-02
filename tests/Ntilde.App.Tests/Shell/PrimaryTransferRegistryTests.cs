using Ntilde.Shell.Clipboard;

namespace Ntilde.Tests.Shell;

public class PrimaryTransferRegistryTests
{
    private sealed class Clock : TimeProvider
    {
        public long Now;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Now;
    }

    [Fact]
    public void Persistent_requestor_windows_do_not_accumulate_subscriptions()
    {
        var subscribed = new HashSet<uint>();
        var registry = new PrimaryTransferRegistry((window, enabled) =>
        {
            if (enabled) Assert.True(subscribed.Add(window));
            else Assert.True(subscribed.Remove(window));
        }, _ => false);
        for (uint window = 1; window <= 1000; window++)
        {
            Assert.True(registry.TryStart(window, 1, 2, [1, 2, 3]));
            Assert.Single(subscribed);
            registry.Retire(Assert.IsType<PrimaryOutgoingTransfer>(registry.Find(window, 1)));
            Assert.Empty(subscribed); Assert.Equal(0, registry.SubscriptionCount);
        }
    }

    [Fact]
    public void Last_stream_releases_the_mask_but_local_read_windows_keep_their_own_events()
    {
        var changes = new List<(uint, bool)>();
        var registry = new PrimaryTransferRegistry((window, enabled) => changes.Add((window, enabled)), window => window == 99);
        Assert.True(registry.TryStart(1, 10, 2, [1]));
        Assert.True(registry.TryStart(1, 11, 2, [1]));
        registry.Retire(registry.Find(1, 10)!);
        Assert.Equal(new[] { (1u, true) }, changes);
        registry.Retire(registry.Find(1, 11)!);
        Assert.Equal(new[] { (1u, true), (1u, false) }, changes);
        Assert.True(registry.TryStart(99, 10, 2, [1]));
        registry.Retire(registry.Find(99, 10)!);
        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public void Expired_and_destroyed_streams_release_registry_resources_without_touching_a_destroyed_window()
    {
        var changes = new List<(uint, bool)>(); var clock = new Clock();
        var registry = new PrimaryTransferRegistry((window, enabled) => changes.Add((window, enabled)), _ => false, clock);
        Assert.True(registry.TryStart(1, 1, 2, [1]));
        Assert.True(registry.TryStart(2, 1, 2, [1]));
        registry.Destroyed(2); clock.Now = 10000; registry.Expire();
        Assert.Null(registry.Find(1, 1)); Assert.Null(registry.Find(2, 1));
        Assert.Equal(new[] { (1u, true), (2u, true), (1u, false) }, changes);
        Assert.Equal(0, registry.SubscriptionCount);
    }

    [Fact]
    public void Registry_caps_streams_and_rejects_duplicate_properties_without_extra_subscriptions()
    {
        var subscribed = new HashSet<uint>();
        var registry = new PrimaryTransferRegistry((window, enabled) => { if (enabled) subscribed.Add(window); else subscribed.Remove(window); }, _ => false);
        for (uint window = 1; window <= 16; window++) Assert.True(registry.TryStart(window, 1, 2, [1]));
        Assert.False(registry.TryStart(1, 1, 2, [1]));
        Assert.False(registry.TryStart(17, 1, 2, [1]));
        Assert.Equal(16, subscribed.Count);
    }

    [Fact]
    public void Payload_budget_is_enforced_before_the_stream_count_limit_and_returns_after_retirement()
    {
        var changes = new List<(uint, bool)>();
        var registry = new PrimaryTransferRegistry((window, enabled) => changes.Add((window, enabled)), _ => false);
        byte[] shared = new byte[16 * 1024 * 1024];
        for (uint window = 1; window <= 8; window++) Assert.True(registry.TryStart(window, 1, 2, shared));
        Assert.False(registry.TryStart(9, 1, 2, [1]));
        Assert.Equal(8, registry.SubscriptionCount);
        registry.Retire(registry.Find(1, 1)!);
        Assert.True(registry.TryStart(9, 1, 2, [1]));
        Assert.Equal(new[] { (1u, false), (9u, true) }, changes.TakeLast(2));
    }

    [Fact]
    public void Stale_clear_after_reacquisition_preserves_new_data_and_genuine_loss_preserves_inflight_payload()
    {
        var ownership = new PrimarySelectionOwnership();
        byte[] old = [1], current = [2, 3];
        ownership.Publish(new() { [10] = old }, 100);
        ownership.Publish(new() { [10] = current }, 200);
        ownership.SelectionCleared(currentOwner: 7, ownWindow: 7);
        Assert.Same(current, ownership.Values[10]); Assert.Equal(200u, ownership.Timestamp);
        var registry = new PrimaryTransferRegistry((_, _) => { }, _ => false);
        Assert.True(registry.TryStart(99, 1, 10, current));
        ownership.SelectionCleared(currentOwner: 8, ownWindow: 7);
        Assert.Empty(ownership.Values);
        Assert.Equal(new byte[] { 2, 3 }, registry.Find(99, 1)!.Payload);
    }
    [Fact]
    public void Republished_selection_rejects_requests_from_before_the_current_interval()
    {
        var ownership = new PrimarySelectionOwnership();
        ownership.Publish(new() { [10] = [1] }, 100);
        Assert.True(ownership.AcceptsRequest(150));
        ownership.Publish(new() { [10] = [2] }, 200);
        Assert.False(ownership.AcceptsRequest(150));
        Assert.True(ownership.AcceptsRequest(200));
        Assert.True(ownership.AcceptsRequest(201));
        Assert.True(ownership.AcceptsRequest(0)); // X11 CurrentTime is accepted for interoperable clients.
        ownership.Clear();
        Assert.False(ownership.AcceptsRequest(0));
    }

    [Fact]
    public void Request_timestamp_order_uses_X11_modular_32_bit_time()
    {
        var ownership = new PrimarySelectionOwnership();
        ownership.Publish(new() { [10] = [1] }, uint.MaxValue - 10);
        Assert.True(ownership.AcceptsRequest(5));
        Assert.False(ownership.AcceptsRequest(uint.MaxValue - 11));
        ownership.Publish(new() { [10] = [2] }, 10);
        Assert.False(ownership.AcceptsRequest(uint.MaxValue - 5));
    }
    [Fact]
    public void Responsive_streams_survive_the_old_start_deadline_but_idle_streams_expire()
    {
        var clock = new Clock();
        var registry = new PrimaryTransferRegistry((_, _) => { }, _ => false, clock);
        Assert.True(registry.TryStart(1, 1, 2, [1]));
        var stream = registry.Find(1, 1)!;
        for (int step = 1; step <= 5; step++)
        {
            clock.Now = step * 9000;
            registry.Expire();
            Assert.Same(stream, registry.Find(1, 1));
            registry.Progress(stream);
        }
        clock.Now += 10000;
        registry.Expire();
        Assert.Null(registry.Find(1, 1));
    }

    [Fact]
    public void Progress_cannot_retain_a_stream_beyond_the_overall_limit()
    {
        var clock = new Clock();
        var registry = new PrimaryTransferRegistry((_, _) => { }, _ => false, clock);
        Assert.True(registry.TryStart(1, 1, 2, [1]));
        var stream = registry.Find(1, 1)!;
        for (int step = 1; step <= 6; step++)
        {
            clock.Now = step * 9000;
            registry.Expire(); registry.Progress(stream);
        }
        clock.Now = 60000;
        registry.Expire();
        Assert.Null(registry.Find(1, 1));
    }

    [Fact]
    public void Repeated_aborted_local_reads_release_stream_slots_without_foreign_subscriptions()
    {
        var registry = new PrimaryTransferRegistry((_, _) => throw new InvalidOperationException("local window subscription"), _ => true);
        for (uint window = 1; window <= 1000; window++)
        {
            Assert.True(registry.TryStart(window, 1, 2, [1]));
            Assert.True(registry.TryStart(window, 3, 2, [1]));
            registry.Destroyed(window); // Convert's finally calls this before destroying its receive window.
            Assert.Null(registry.Find(window, 1)); Assert.Null(registry.Find(window, 3));
        }
        Assert.Equal(0, registry.SubscriptionCount);
    }
}
