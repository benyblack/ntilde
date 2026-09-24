using Ntilde.Mux.Tests.Support;
using Ntilde.Pty;

namespace Ntilde.Mux.Tests.Headless;

public sealed class HeadlessInputWriterTests
{
    [Fact]
    public async Task A_child_that_stops_reading_stdin_does_not_block_input_to_another_session_on_the_same_connection()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid stuck = await MuxTestHost.SpawnAsync(client);
        Guid healthy = await MuxTestHost.SpawnAsync(client);
        using var gate = new ManualResetEventSlim(false);
        host.Fake(stuck).SendInputGate = gate;          // SendInput blocks until set

        using MuxClientSession a = client.OpenSession(stuck);
        using MuxClientSession b = client.OpenSession(healthy);
        a.SendInput("wedged");
        b.SendInput("hello");

        await TestWait.UntilAsync(() => host.Fake(healthy).SentInput.Contains("hello"),
            "input to the healthy session arrives while the other session's writer is blocked");
        await client.PingAsync(TestContext.Current.CancellationToken); // the connection reader is not stuck either
        gate.Set();
        await TestWait.UntilAsync(() => host.Fake(stuck).SentInput.Contains("wedged"), "the wedged input lands once unblocked");
    }

    [Fact]
    public async Task Input_order_is_preserved_per_session()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        using MuxClientSession s = client.OpenSession(id);
        for (int i = 0; i < 200; i++) s.SendInput(i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",");
        string expected = string.Concat(Enumerable.Range(0, 200).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ","));
        await TestWait.UntilAsync(() => string.Concat(host.Fake(id).SentInput) == expected, "all 200 inputs arrive in order");
    }

    [Fact]
    public async Task Input_beyond_the_byte_cap_is_dropped_not_queued_forever()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        using var gate = new ManualResetEventSlim(false);
        host.Fake(id).SendInputGate = gate;
        HeadlessTerminalSession mux = host.Mux(id);
        mux.MaxQueuedInputBytesForTest = 1024;
        mux.SendInput("first");                              // taken by the writer, which then blocks
        await TestWait.UntilAsync(() => mux.QueuedInputBytes == 0, "the writer took the first item");
        mux.SendInput(new string('x', 2000));                // over the cap: dropped
        Assert.Equal(0, mux.QueuedInputBytes);
        mux.SendInput("second");                             // under the cap: still queued behind the blocked write
        Assert.Equal("second".Length * sizeof(char), mux.QueuedInputBytes);
        gate.Set();
        await TestWait.UntilAsync(() => string.Concat(host.Fake(id).SentInput) == "firstsecond", "only the capped item was dropped");
    }

    [Fact]
    public async Task A_throwing_SendInput_does_not_kill_the_input_writer()
    {
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var host = new MuxTestHost(new MuxServerOptions { ForceConPtyFiltering = false, Log = logs.Enqueue });
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        HeadlessTerminalSession mux = host.Mux(id);
        ScriptedTerminalSession fake = host.Fake(id);

        fake.ThrowOnSendInput = true;
        mux.SendInput("lost");
        await TestWait.UntilAsync(() => logs.Any(l => l.Contains("SendInput failed", StringComparison.Ordinal)), "the child's SendInput threw on the writer thread");
        fake.ThrowOnSendInput = false;
        mux.SendInput("kept");
        await TestWait.UntilAsync(() => fake.SentInput.Contains("kept"), "the writer survived the throw");
        Assert.True(mux.IsInputThreadAliveForTest);
    }

    [Fact]
    public void Dispose_stops_the_input_writer()
    {
        var fake = new ScriptedTerminalSession(new TerminalSessionRequest("scripted", string.Empty, string.Empty, 80, 24, null, false, null));
        var mux = new HeadlessTerminalSession(Guid.NewGuid(), fake, new HeadlessSessionOptions { ForceConPtyFiltering = false });
        mux.SendInput("ok");
        Assert.True(SpinWait.SpinUntil(() => fake.SentInput.Contains("ok"), TimeSpan.FromSeconds(10)));
        Assert.True(mux.IsInputThreadAliveForTest);

        mux.Dispose();
        Assert.False(mux.IsInputThreadAliveForTest, "the input thread outlived Dispose");
    }

    [Fact]
    public void A_constructor_that_fails_after_starting_the_input_writer_stops_it()
    {
        Guid id = Guid.NewGuid();
        Thread? started = null;
        void OnStarted(Guid sessionId, Thread thread)
        {
            if (sessionId == id) started = thread;
        }

        HeadlessTerminalSession.InputThreadStartedForTest += OnStarted;
        try
        {
            var fake = new ScriptedTerminalSession(new TerminalSessionRequest("scripted", string.Empty, string.Empty, 80, 24, null, false, null))
            {
                ThrowOnRawSubscribe = true,
            };
            Assert.Throws<InvalidOperationException>(() =>
                new HeadlessTerminalSession(id, fake, new HeadlessSessionOptions { ForceConPtyFiltering = false }));
        }
        finally
        {
            HeadlessTerminalSession.InputThreadStartedForTest -= OnStarted;
        }

        Assert.NotNull(started);
        Assert.False(started.IsAlive, "the input thread outlived the failed constructor");
    }
}
