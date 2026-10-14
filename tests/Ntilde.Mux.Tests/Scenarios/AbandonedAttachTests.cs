using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Scenarios;

/// <summary>
/// An attach the caller gave up on still gets its snapshot (the server had committed), and the
/// client undoes that subscription with a detach. That detach must never undo a newer attach for
/// the same session that the caller made in the meantime (a retry).
/// </summary>
public sealed class AbandonedAttachTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)] // the same MuxClientSession retries
    [InlineData(true)]  // the caller disposes it and retries with a fresh one for the same session id
    public async Task A_late_snapshot_from_a_cancelled_attach_does_not_detach_the_retry(bool freshSession)
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        host.Fake(id).Emit("before ");
        await host.Mux(id).FlushAsync();

        // Park the parse thread so both attaches are queued on the server before either answers:
        // snapshot #1 then reaches the client only after attach #2 is already on the wire.
        using var parked = new ManualResetEventSlim();
        Task<int> park = host.Mux(id).InvokeAsync(() => { parked.Wait(Ct); return 0; });

        MuxClientSession first = client.OpenSession(id, "scripted");
        using (var cts = new CancellationTokenSource())
        {
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.AttachAsync(100, MuxTestHost.DefaultPresentation, cts.Token));
        }

        MuxClientSession retry = first;
        if (freshSession)
        {
            first.Dispose();
            retry = client.OpenSession(id, "scripted");
        }

        var pane = new ClientPaneModel(retry);
        Task<long> attach = retry.AttachAsync(100, MuxTestHost.DefaultPresentation, Ct);
        await client.PingAsync(Ct); // the server has dispatched both attaches to the parked thread
        parked.Set();
        await park;
        await attach;

        // Snapshot #1 has arrived (before #2) and been handled; the retried pane must still stream.
        host.Fake(id).Emit("after");
        await host.SettleAsync(id, client);

        Assert.True(client.IsConnected, client.DisconnectReason);
        Assert.Equal(1, host.Mux(id).AttachedClients);
        Assert.Contains("out:after", pane.Events);
        host.AssertPaneMatchesMux(id, pane, "the retried pane");
    }

    [Fact]
    public async Task A_late_snapshot_with_no_newer_attach_still_detaches()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        using var parked = new ManualResetEventSlim();
        Task<int> park = host.Mux(id).InvokeAsync(() => { parked.Wait(Ct); return 0; });

        MuxClientSession session = client.OpenSession(id, "scripted");
        using (var cts = new CancellationTokenSource())
        {
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.AttachAsync(100, MuxTestHost.DefaultPresentation, cts.Token));
        }

        await client.PingAsync(Ct);
        parked.Set();
        await park;
        await host.SettleAsync(id, client); // attach #1 ran and its snapshot reached the client ...
        await host.SettleAsync(id, client); // ... and the detach the client sent for it has run

        Assert.Equal(0, host.Mux(id).AttachedClients);
        Assert.True(client.IsConnected);
    }
}
