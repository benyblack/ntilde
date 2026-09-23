using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Scenarios;

public sealed class SlowClientTests
{
    [Fact]
    public async Task A_client_that_stops_reading_is_disconnected_and_nobody_else_notices()
    {
        using var host = new MuxTestHost(new MuxServerOptions { ClientSendBudgetBytes = 256 * 1024, ForceConPtyFiltering = false }, pipeCapacityBytes: 16 * 1024);
        var closed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Server.ConnectionClosed += c => closed.TrySetResult(c.CloseReason);
        MuxClient healthy = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(healthy);
        ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(healthy, id);

        RawMuxConnection slow = host.ConnectRaw();
        await slow.HelloAsync();
        slow.Request(MuxMethods.Attach, new AttachParams { SessionId = id, MaxScrollbackRows = 0, Presentation = MuxTestHost.DefaultPresentation },
            MuxJsonContext.Default.AttachParams);
        using (MuxInboundFrame? snapshot = await slow.ReadAsync()) Assert.Equal(MuxFrameKind.Snapshot, snapshot?.Kind);
        // ... and now it never reads again.

        byte[] chunk = new byte[4096];
        Array.Fill(chunk, (byte)'x');
        const int chunks = 512; // 2 MiB
        for (int i = 0; i < chunks; i++) host.Fake(id).Emit(chunk);

        Assert.Equal(MuxErrorCodes.ClientTooSlow, await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await host.SettleAsync(id, healthy);
        Assert.Equal(chunks * 4096L, host.Mux(id).StreamPosition);   // the parse thread never waited
        Assert.Equal(1, host.Mux(id).AttachedClients);
        host.AssertPaneMatchesMux(id, pane, "the healthy client");
    }

    [Fact]
    public async Task A_throwing_logger_on_the_too_slow_path_does_not_fault_the_session()
    {
        // The client_too_slow abort runs on the session's parse thread (Broadcast -> TryEnqueue ->
        // Abort), outside any try: a logger that throws there must not take the session down.
        using var host = new MuxTestHost(new MuxServerOptions
        {
            ClientSendBudgetBytes = 256 * 1024,
            ForceConPtyFiltering = false,
            Log = m =>
            {
                if (m.Contains(MuxErrorCodes.ClientTooSlow, StringComparison.Ordinal)) throw new InvalidOperationException("scripted logger failure");
            },
        }, pipeCapacityBytes: 16 * 1024);
        var closed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Server.ConnectionClosed += c => closed.TrySetResult(c.CloseReason);
        MuxClient healthy = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(healthy);
        ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(healthy, id);

        RawMuxConnection slow = host.ConnectRaw();
        await slow.HelloAsync();
        slow.Request(MuxMethods.Attach, new AttachParams { SessionId = id, MaxScrollbackRows = 0, Presentation = MuxTestHost.DefaultPresentation },
            MuxJsonContext.Default.AttachParams);
        using (MuxInboundFrame? snapshot = await slow.ReadAsync()) Assert.Equal(MuxFrameKind.Snapshot, snapshot?.Kind);

        byte[] chunk = new byte[4096];
        Array.Fill(chunk, (byte)'y');
        for (int i = 0; i < 512; i++) host.Fake(id).Emit(chunk);

        Assert.Equal(MuxErrorCodes.ClientTooSlow, await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        host.Fake(id).Emit("still alive");
        await host.SettleAsync(id, healthy);
        Assert.False(host.Mux(id).IsFaulted);
        Assert.Equal(1, host.Mux(id).AttachedClients);
        host.AssertPaneMatchesMux(id, pane, "the healthy client");
    }
}
