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
}
