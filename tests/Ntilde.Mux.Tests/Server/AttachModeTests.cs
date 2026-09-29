using System.Collections.Concurrent;
using System.Text;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Server;

/// <summary>Phase 3 spec §3: attach modes are decided inside the attach item on the session's parse thread.</summary>
public sealed class AttachModeTests
{
    private static async Task<RawMuxConnection> ConnectV2Async(MuxTestHost host)
    {
        RawMuxConnection raw = host.ConnectRaw();
        WelcomeResult welcome = await raw.HelloAsync(min: 1, max: 2);
        Assert.Equal(2, welcome.Version);
        return raw;
    }

    private static long SendAttach(RawMuxConnection raw, Guid id, string? mode, MuxPresentation? presentation = null) =>
        raw.Request(MuxMethods.Attach, new AttachParams
        {
            SessionId = id,
            MaxScrollbackRows = 0,
            Presentation = presentation ?? MuxTestHost.DefaultPresentation,
            Mode = mode,
        }, MuxJsonContext.Default.AttachParams);

    /// <summary>"snapshot", "ok", or the error code of the reply to <paramref name="requestId"/>; skips stream frames and notifications.</summary>
    private static async Task<string> ReadOutcomeAsync(RawMuxConnection raw, long requestId)
    {
        while (true)
        {
            using MuxInboundFrame frame = await raw.ReadAsync() ?? throw new EndOfStreamException("The server closed the connection.");
            if (frame.Kind == MuxFrameKind.Snapshot)
            {
                Assert.True(MuxFrames.TryParseSnapshot(frame.Payload, out long rid, out _, out _, out _));
                if (rid == requestId) return "snapshot";
            }
            else if (frame.Kind == MuxFrameKind.Response)
            {
                MuxResponse r = MuxFrames.ParseJson(frame.Payload, MuxJsonContext.Default.MuxResponse);
                if (r.Id == requestId) return r.Error?.Code ?? "ok";
            }
        }
    }

    /// <summary>
    /// The pinned race (review note 4): two real connections, each with its own server reader thread,
    /// both get their attach item into the session's control queue while the parse thread is parked,
    /// so the decision can only be made on the parse thread. Sequential awaited attaches would test
    /// the connection instead.
    /// </summary>
    [Fact]
    public async Task IfUnattached_is_decided_on_the_parse_thread()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        RawMuxConnection a = await ConnectV2Async(host);
        RawMuxConnection b = await ConnectV2Async(host);
        HeadlessTerminalSession mux = host.Mux(id);
        using var gate = new ManualResetEventSlim(false);

        Task<int> parked = mux.InvokeAsync(() => { gate.Wait(TimeSpan.FromSeconds(30)); return 0; });
        long ra, rb;
        try
        {
            await TestWait.UntilAsync(() => mux.QueuedControlCount == 0, "the parse thread took the parking item");
            ra = SendAttach(a, id, MuxAttachModes.IfUnattached);
            rb = SendAttach(b, id, MuxAttachModes.IfUnattached);
            await TestWait.UntilAsync(() => mux.QueuedControlCount == 2, "both attaches wait behind the parked parse thread");
            Assert.Equal(0, mux.AttachedClients);
        }
        finally
        {
            gate.Set();
        }

        await parked;

        string[] outcomes = [await ReadOutcomeAsync(a, ra), await ReadOutcomeAsync(b, rb)];
        Assert.Single(outcomes, o => o == "snapshot");
        Assert.Single(outcomes, o => o == MuxErrorCodes.SessionAttached);
        Assert.Equal(1, mux.AttachedClients);
    }

    [Fact]
    public async Task IfUnattached_races_have_exactly_one_winner()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        for (int i = 0; i < 50; i++)
        {
            Guid id = await MuxTestHost.SpawnAsync(spawner);
            RawMuxConnection a = await ConnectV2Async(host);
            RawMuxConnection b = await ConnectV2Async(host);
            long ra = 0, rb = 0;
            Parallel.Invoke(
                () => ra = SendAttach(a, id, MuxAttachModes.IfUnattached),
                () => rb = SendAttach(b, id, MuxAttachModes.IfUnattached));

            string[] outcomes = [await ReadOutcomeAsync(a, ra), await ReadOutcomeAsync(b, rb)];
            Assert.Single(outcomes, o => o == "snapshot");
            Assert.Single(outcomes, o => o == MuxErrorCodes.SessionAttached);
            a.Dispose();
            b.Dispose();
        }
    }

    [Fact]
    public async Task A_read_only_observer_does_not_block_IfUnattached_and_the_holder_may_reattach()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        RawMuxConnection observer = await ConnectV2Async(host);
        RawMuxConnection gui = await ConnectV2Async(host);
        RawMuxConnection other = await ConnectV2Async(host);

        Assert.Equal("snapshot", await ReadOutcomeAsync(observer, SendAttach(observer, id, MuxAttachModes.ReadOnly)));
        Assert.Equal("snapshot", await ReadOutcomeAsync(gui, SendAttach(gui, id, MuxAttachModes.IfUnattached)));
        Assert.Equal("snapshot", await ReadOutcomeAsync(gui, SendAttach(gui, id, MuxAttachModes.IfUnattached)));  // re-attach by the holder
        Assert.Equal(MuxErrorCodes.SessionAttached, await ReadOutcomeAsync(other, SendAttach(other, id, MuxAttachModes.IfUnattached)));
        Assert.Equal(2, host.Mux(id).AttachedClients);
    }

    [Fact]
    public async Task ReadOnly_attach_does_not_resize_and_its_input_and_resize_are_dropped()
    {
        var log = new ConcurrentQueue<string>();
        using var host = new MuxTestHost(new MuxServerOptions { ForceConPtyFiltering = false, Log = log.Enqueue });
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);                        // 80x24
        RawMuxConnection ro = await ConnectV2Async(host);

        long attach = SendAttach(ro, id, MuxAttachModes.ReadOnly, MuxTestHost.DefaultPresentation with { Cols = 100, Rows = 30 });
        Assert.Equal("snapshot", await ReadOutcomeAsync(ro, attach));
        Assert.Equal((80, 24), (host.Mux(id).Cols, host.Mux(id).Rows));      // a read-only attach does not resize

        ro.Send(MuxFrames.Input(id, Encoding.UTF8.GetBytes("typed")));
        ro.Send(MuxFrames.Input(id, Encoding.UTF8.GetBytes("again")));
        long resize = ro.Request(MuxMethods.Resize, new ResizeParams { SessionId = id, Cols = 90, Rows = 20 }, MuxJsonContext.Default.ResizeParams);
        Assert.Equal("ok", await ReadOutcomeAsync(ro, resize));               // answered, never an error
        await host.Mux(id).InvokeAsync(() => 0);                              // any posted resize has run
        await TestWait.UntilAsync(() => host.Mux(id).QueuedInputBytes == 0, "the input writer is idle");

        Assert.Empty(host.Fake(id).SentInput);
        Assert.Empty(host.Fake(id).Resizes);
        Assert.Equal((80, 24), (host.Mux(id).Cols, host.Mux(id).Rows));
        Assert.Single(log, l => l.Contains("read-only", StringComparison.Ordinal) && l.Contains("input", StringComparison.Ordinal));
        Assert.Single(log, l => l.Contains("read-only", StringComparison.Ordinal) && l.Contains("resize", StringComparison.Ordinal));

        // An interactive attach on the same connection lifts it.
        Assert.Equal("snapshot", await ReadOutcomeAsync(ro, SendAttach(ro, id, null)));
        ro.Send(MuxFrames.Input(id, Encoding.UTF8.GetBytes("now")));
        await TestWait.UntilAsync(() => host.Fake(id).SentInput.Contains("now"), "input flows after an interactive attach");
    }

    [Fact]
    public async Task An_unknown_mode_is_a_request_error_and_the_connection_stays_open()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        RawMuxConnection raw = await ConnectV2Async(host);

        Assert.Equal(MuxErrorCodes.ProtocolError, await ReadOutcomeAsync(raw, SendAttach(raw, id, "bogus")));
        long ping = raw.Request(MuxMethods.Ping, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty);
        Assert.Equal("ok", await ReadOutcomeAsync(raw, ping));
        Assert.Equal(0, host.Mux(id).AttachedClients);
    }

    [Fact]
    public async Task A_user_detach_is_recorded_until_the_next_attach()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        RawMuxConnection a = await ConnectV2Async(host);
        RawMuxConnection b = await ConnectV2Async(host);
        Assert.Equal("snapshot", await ReadOutcomeAsync(a, SendAttach(a, id, null)));
        Assert.Equal("snapshot", await ReadOutcomeAsync(b, SendAttach(b, id, null)));
        long UserDetach(RawMuxConnection raw) =>
            raw.Request(MuxMethods.Detach, new DetachParams { SessionId = id, UserDetached = true }, MuxJsonContext.Default.DetachParams);

        // A user detach that leaves another client attached changes nothing ...
        Assert.Equal("ok", await ReadOutcomeAsync(a, UserDetach(a)));
        await host.Mux(id).InvokeAsync(() => 0);
        Assert.False(host.Mux(id).DetachedByUser);

        // ... the detach that empties the session decides.
        Assert.Equal("ok", await ReadOutcomeAsync(b, UserDetach(b)));
        await host.Mux(id).InvokeAsync(() => 0);
        Assert.True(host.Mux(id).DetachedByUser);
        Assert.True(Assert.Single(await spawner.ListSessionsAsync(TestContext.Current.CancellationToken), s => s.SessionId == id).DetachedByUser);

        // A later attach clears it, and a connection closing afterwards (not a user detach) leaves it clear.
        Assert.Equal("snapshot", await ReadOutcomeAsync(a, SendAttach(a, id, null)));
        await host.Mux(id).InvokeAsync(() => 0);
        Assert.False(host.Mux(id).DetachedByUser);
        a.Dispose();
        await TestWait.UntilAsync(() => host.Mux(id).AttachedClients == 0, "the closed connection was detached");
        Assert.False(host.Mux(id).DetachedByUser);
    }

    [Fact]
    public async Task A_user_detached_running_shell_still_blocks_idle_exit()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        RawMuxConnection a = await ConnectV2Async(host);
        Assert.Equal("snapshot", await ReadOutcomeAsync(a, SendAttach(a, id, null)));
        Assert.Equal("ok", await ReadOutcomeAsync(a, a.Request(MuxMethods.Detach, new DetachParams { SessionId = id, UserDetached = true }, MuxJsonContext.Default.DetachParams)));
        spawner.Dispose();
        a.Dispose();
        await TestWait.UntilAsync(() => host.Server.ConnectionCount == 0, "every connection closed");
        await host.Mux(id).InvokeAsync(() => 0); // the detach item has run: the "ok" only says it was posted

        Assert.True(host.Mux(id).DetachedByUser);
        Assert.False(host.Server.TryBeginIdleShutdown()); // idle exit is unaffected: a running shell keeps the daemon
    }
}
