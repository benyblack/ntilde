using Ntilde.Mux.Transport;

namespace Ntilde.Mux.Tests.Transport;

public sealed class InMemoryTransportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Bytes_arrive_in_order_across_ring_wrap_around()
    {
        (Stream a, Stream b) = InMemoryDuplexPipe.Create(capacityBytes: 8);
        byte[] sent = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();

        Task writer = Task.Run(() => a.Write(sent), Ct);
        byte[] received = new byte[sent.Length];
        await Task.Run(() => b.ReadExactly(received), Ct).WaitAsync(Timeout, Ct);
        await writer.WaitAsync(Timeout, Ct);

        Assert.Equal(sent, received);
    }

    [Fact]
    public async Task Read_returns_zero_after_the_peer_closes_and_the_pipe_drains()
    {
        (Stream a, Stream b) = InMemoryDuplexPipe.Create(64);
        a.Write("xy"u8);
        a.Dispose();

        byte[] buffer = new byte[16];
        Assert.Equal(2, b.Read(buffer));
        Assert.Equal(0, await Task.Run(() => b.Read(buffer), Ct).WaitAsync(Timeout, Ct));
    }

    [Fact]
    public async Task Disposing_an_end_unblocks_its_own_blocked_read()
    {
        (Stream a, _) = InMemoryDuplexPipe.Create(64);
        Task<int> read = Task.Run(() => a.Read(new byte[4]), Ct);
        await Task.Delay(50, Ct);

        a.Dispose();

        Assert.Equal(0, await read.WaitAsync(Timeout, Ct));
    }

    [Fact]
    public async Task Disposing_an_end_unblocks_its_own_blocked_write()
    {
        (Stream a, _) = InMemoryDuplexPipe.Create(4);
        Task write = Task.Run(() => a.Write(new byte[64]), Ct);
        await Task.Delay(50, Ct);

        a.Dispose();

        await Assert.ThrowsAsync<IOException>(() => write.WaitAsync(Timeout, Ct));
    }

    [Fact]
    public void Writing_to_a_peer_that_closed_throws()
    {
        (Stream a, Stream b) = InMemoryDuplexPipe.Create(64);
        b.Dispose();
        Assert.Throws<IOException>(() => a.Write("x"u8));
    }

    [Fact]
    public async Task Listener_pairs_Connect_with_Accept_and_bytes_flow_both_ways()
    {
        using var listener = new InMemoryMuxListener();
        using Stream client = listener.Connect();
        using Stream server = (await Task.Run(() => listener.Accept(Ct), Ct).WaitAsync(Timeout, Ct))!;

        client.Write("ping"u8);
        byte[] got = new byte[4];
        server.ReadExactly(got);
        server.Write("pong"u8);
        byte[] back = new byte[4];
        client.ReadExactly(back);

        Assert.Equal("ping"u8.ToArray(), got);
        Assert.Equal("pong"u8.ToArray(), back);
    }

    [Fact]
    public async Task Accept_returns_null_once_the_listener_is_disposed()
    {
        var listener = new InMemoryMuxListener();
        Task<Stream?> accept = Task.Run(() => listener.Accept(Ct), Ct);
        await Task.Delay(50, Ct);

        listener.Dispose();

        Assert.Null(await accept.WaitAsync(Timeout, Ct));
    }
}
