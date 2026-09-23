using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Mux.Transport;

namespace Ntilde.Mux.Tests.Server;

public sealed class MuxServerSendBudgetTests
{
    [Fact]
    public async Task Overflowing_the_budget_disconnects_as_client_too_slow_without_blocking_the_caller()
    {
        using var server = new MuxServer(new ScriptedSessionFactory(), new MuxServerOptions { ClientSendBudgetBytes = 64 * 1024 });
        (Stream clientEnd, Stream serverEnd) = InMemoryDuplexPipe.Create(4096); // the client never reads
        using var _ = clientEnd;
        var connection = new MuxServerConnection(server, serverEnd);
        connection.Start();

        int accepted = 0;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (accepted < 1000)
        {
            MuxOutboundFrame frame = MuxFrames.Output(Guid.NewGuid(), 0, new byte[1024]);
            bool ok = connection.TryEnqueue(frame);
            frame.Release();
            if (!ok) break;
            accepted++;
        }

        Assert.InRange(accepted, 60, 80); // 64 KiB budget + what fits in the 4 KiB pipe
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2), "TryEnqueue must never block");
        await TestWait.UntilAsync(() => connection.CloseReason is not null, "the connection is closed");
        Assert.Equal(MuxErrorCodes.ClientTooSlow, connection.CloseReason);
    }

    [Fact]
    public void One_frame_larger_than_the_budget_is_accepted_when_nothing_is_queued()
    {
        using var server = new MuxServer(new ScriptedSessionFactory(), new MuxServerOptions { ClientSendBudgetBytes = 1024 });
        (Stream clientEnd, Stream serverEnd) = InMemoryDuplexPipe.Create(4096);
        using var _ = clientEnd;
        var connection = new MuxServerConnection(server, serverEnd);
        connection.Start();

        MuxOutboundFrame big = MuxFrames.Output(Guid.NewGuid(), 0, new byte[10 * 1024]);
        MuxOutboundFrame second = MuxFrames.Output(Guid.NewGuid(), 0, new byte[10 * 1024]);
        try
        {
            Assert.True(connection.TryEnqueue(big));     // a snapshot on a fresh attach
            Assert.False(connection.TryEnqueue(second)); // the first is still in flight: over budget
        }
        finally
        {
            big.Release();
            second.Release();
        }
    }

    [Fact]
    public async Task A_snapshot_is_not_charged_to_the_stream_budget()
    {
        using var server = new MuxServer(new ScriptedSessionFactory(), new MuxServerOptions { ClientSendBudgetBytes = 16 * 1024 });
        (Stream clientEnd, Stream serverEnd) = InMemoryDuplexPipe.Create(4096); // not read until the end
        using var _ = clientEnd;
        var connection = new MuxServerConnection(server, serverEnd);
        connection.Start();

        var frames = new List<MuxOutboundFrame>();
        try
        {
            for (int i = 0; i < 12; i++) frames.Add(MuxFrames.Output(Guid.NewGuid(), 0, new byte[1024]));
            frames.Add(MuxFrames.Snapshot(1, Guid.NewGuid(), 0, new byte[64 * 1024])); // 4x the whole budget
            frames.Add(MuxFrames.Output(Guid.NewGuid(), 0, new byte[1024]));           // stream frames still fit after it

            foreach (MuxOutboundFrame f in frames) Assert.True(connection.TryEnqueue(f), $"{f.Kind} refused");
        }
        finally
        {
            foreach (MuxOutboundFrame f in frames) f.Release();
        }

        // Everything arrives, in order, and the connection is still up.
        int outputs = 0, snapshots = 0;
        while (outputs + snapshots < 14)
        {
            using MuxInboundFrame frame = (await Task.Run(() => MuxFrameReader.Read(clientEnd), TestContext.Current.CancellationToken))!;
            if (frame.Kind == MuxFrameKind.Snapshot) { Assert.Equal(12, outputs); snapshots++; }
            else outputs++;
        }

        Assert.Equal((13, 1), (outputs, snapshots));
        Assert.Null(connection.CloseReason);
    }

    [Fact]
    public void Queued_snapshots_are_bounded_by_MaxQueuedSnapshotBytes()
    {
        using var server = new MuxServer(new ScriptedSessionFactory(), new MuxServerOptions { MaxQueuedSnapshotBytes = 100 * 1024 });
        (Stream clientEnd, Stream serverEnd) = InMemoryDuplexPipe.Create(4096); // the client never reads
        using var _ = clientEnd;
        var connection = new MuxServerConnection(server, serverEnd);
        connection.Start();

        MuxOutboundFrame first = MuxFrames.Snapshot(1, Guid.NewGuid(), 0, new byte[150 * 1024]); // alone: over the bound, still taken
        MuxOutboundFrame second = MuxFrames.Snapshot(2, Guid.NewGuid(), 0, new byte[10 * 1024]);
        try
        {
            Assert.True(connection.TryEnqueue(first));
            Assert.False(connection.TryEnqueue(second)); // the first is still queued or in flight
        }
        finally
        {
            first.Release();
            second.Release();
        }

        Assert.Equal(MuxErrorCodes.ClientTooSlow, connection.CloseReason);
    }
}
