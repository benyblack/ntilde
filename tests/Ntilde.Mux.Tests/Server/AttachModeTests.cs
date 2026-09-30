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

    private static long SendAttach(RawMuxConnection raw, Guid id, string? mode, MuxPresentation? presentation = null, int maxScrollbackRows = 0) =>
        raw.Request(MuxMethods.Attach, new AttachParams
        {
            SessionId = id,
            MaxScrollbackRows = maxScrollbackRows,
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
        Assert.Single(log, l => l.Contains("dropping its input", StringComparison.Ordinal));
        Assert.Single(log, l => l.Contains("dropping its resize", StringComparison.Ordinal));

        // An interactive attach on the same connection lifts it.
        Assert.Equal("snapshot", await ReadOutcomeAsync(ro, SendAttach(ro, id, null)));
        ro.Send(MuxFrames.Input(id, Encoding.UTF8.GetBytes("now")));
        await TestWait.UntilAsync(() => host.Fake(id).SentInput.Contains("now"), "input flows after an interactive attach");
    }

    /// <summary>Review fix 1a: the connection learns its read-only state from the attach's outcome, not from the request.</summary>
    [Fact]
    public async Task A_refused_IfUnattached_upgrade_leaves_the_observer_read_only()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        RawMuxConnection gui = await ConnectV2Async(host);
        RawMuxConnection observer = await ConnectV2Async(host);
        Assert.Equal("snapshot", await ReadOutcomeAsync(gui, SendAttach(gui, id, null)));
        Assert.Equal("snapshot", await ReadOutcomeAsync(observer, SendAttach(observer, id, MuxAttachModes.ReadOnly)));

        Assert.Equal(MuxErrorCodes.SessionAttached, await ReadOutcomeAsync(observer, SendAttach(observer, id, MuxAttachModes.IfUnattached)));
        observer.Send(MuxFrames.Input(id, Encoding.UTF8.GetBytes("typed")));
        long resize = observer.Request(MuxMethods.Resize, new ResizeParams { SessionId = id, Cols = 90, Rows = 20 }, MuxJsonContext.Default.ResizeParams);
        Assert.Equal("ok", await ReadOutcomeAsync(observer, resize));
        await host.Mux(id).InvokeAsync(() => 0);
        await TestWait.UntilAsync(() => host.Mux(id).QueuedInputBytes == 0, "the input writer is idle");

        Assert.Empty(host.Fake(id).SentInput);
        Assert.Empty(host.Fake(id).Resizes);
        Assert.Equal((80, 24), (host.Mux(id).Cols, host.Mux(id).Rows));
        Assert.Equal(2, host.Mux(id).AttachedClients);
    }

    /// <summary>Review fix 1b: a read-only re-attach that fails leaves an interactive client interactive.</summary>
    [Fact]
    public async Task A_failed_read_only_reattach_leaves_the_client_interactive()
    {
        using var host = new MuxTestHost(new MuxServerOptions { ForceConPtyFiltering = false, MaxSnapshotBytes = 256 * 1024 });
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        host.Fake(id).Emit(string.Concat(Enumerable.Repeat(new string('x', 79) + "\r\n", 3000))); // scrollback far past the ceiling
        await host.Mux(id).FlushAsync();
        RawMuxConnection raw = await ConnectV2Async(host);

        Assert.Equal("snapshot", await ReadOutcomeAsync(raw, SendAttach(raw, id, null)));   // the screen alone fits
        Assert.Equal(MuxErrorCodes.SnapshotTooLarge, await ReadOutcomeAsync(raw, SendAttach(raw, id, MuxAttachModes.ReadOnly, maxScrollbackRows: 20_000)));

        raw.Send(MuxFrames.Input(id, Encoding.UTF8.GetBytes("still")));
        long resize = raw.Request(MuxMethods.Resize, new ResizeParams { SessionId = id, Cols = 90, Rows = 20 }, MuxJsonContext.Default.ResizeParams);
        Assert.Equal("ok", await ReadOutcomeAsync(raw, resize));
        await TestWait.UntilAsync(() => host.Fake(id).SentInput.Contains("still"), "input still flows");
        await TestWait.UntilAsync(() => host.Fake(id).Resizes.Contains((90, 20)), "resizes still apply");
        Assert.Equal(1, host.Mux(id).AttachedClients);
    }

    /// <summary>Review ruling 2: only an attach that took effect supersedes an older one.</summary>
    [Fact]
    public async Task A_detach_naming_an_attach_is_not_superseded_by_a_newer_refused_attach()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        RawMuxConnection a = await ConnectV2Async(host);
        RawMuxConnection b = await ConnectV2Async(host);
        long first = SendAttach(a, id, null);
        Assert.Equal("snapshot", await ReadOutcomeAsync(a, first));
        Assert.Equal("snapshot", await ReadOutcomeAsync(b, SendAttach(b, id, null)));
        Assert.Equal(MuxErrorCodes.SessionAttached, await ReadOutcomeAsync(a, SendAttach(a, id, MuxAttachModes.IfUnattached)));

        long detach = a.Request(MuxMethods.Detach, new DetachParams { SessionId = id, AttachRequestId = first }, MuxJsonContext.Default.DetachParams);
        Assert.Equal("ok", await ReadOutcomeAsync(a, detach));
        await host.Mux(id).InvokeAsync(() => 0);
        Assert.Equal(1, host.Mux(id).AttachedClients); // a's subscription from `first` is gone; b's remains
    }

    /// <summary>
    /// Review fix round 2: the sink learns its new read-only state before it is handed the snapshot,
    /// so a client reacting to the snapshot never has its next input judged by the old state.
    /// </summary>
    [Fact]
    public async Task The_subscription_state_is_reported_before_the_snapshot_is_enqueued()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        HeadlessTerminalSession mux = host.Mux(id);
        var sink = new OrderRecordingSink();
        int maxBytes = MuxProtocol.MaxFrameBytes - MuxFrames.SnapshotHeaderBytes;

        mux.PostAttach(sink, 1, 0, MuxTestHost.DefaultPresentation, maxBytes, MuxAttachMode.ReadOnly);
        await mux.InvokeAsync(() => 0);
        Assert.Equal(new[] { "state:True:True", "Snapshot:1" }, sink.Events.Take(2));

        sink.Events.Clear();
        mux.PostAttach(sink, 2, 0, MuxTestHost.DefaultPresentation, maxBytes, MuxAttachMode.Shared);
        await mux.InvokeAsync(() => 0);
        Assert.Equal(new[] { "state:True:False", "Snapshot:2" }, sink.Events.Take(2));
    }

    private sealed class OrderRecordingSink : IMuxFrameSink
    {
        public ConcurrentQueue<string> Events { get; } = new();

        public bool TryEnqueue(MuxOutboundFrame frame)
        {
            if (frame.Kind == MuxFrameKind.Snapshot)
            {
                Assert.True(MuxFrames.TryParseSnapshot(frame.Bytes[MuxProtocol.FrameHeaderBytes..], out long rid, out _, out _, out _));
                Events.Enqueue($"Snapshot:{rid}");
            }

            return true;
        }

        public void OnSubscriptionState(Guid sessionId, bool subscribed, bool readOnly) => Events.Enqueue($"state:{subscribed}:{readOnly}");
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

    /// <summary>Final review: listSessions tells a read-only peek apart from a client that shows the shell (orphan adoption needs it).</summary>
    [Fact]
    public async Task ListSessions_reports_interactive_clients_without_read_only_observers()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        RawMuxConnection gui = await ConnectV2Async(host);
        RawMuxConnection peek = await ConnectV2Async(host);
        async Task<(int Attached, int Interactive)> ListedAsync()
        {
            await host.Mux(id).InvokeAsync(() => 0);
            SessionSummary s = Assert.Single(await spawner.ListSessionsAsync(TestContext.Current.CancellationToken), s => s.SessionId == id);
            return (s.AttachedClients, s.InteractiveClients);
        }

        Assert.Equal("snapshot", await ReadOutcomeAsync(peek, SendAttach(peek, id, MuxAttachModes.ReadOnly)));
        Assert.Equal((1, 0), await ListedAsync());

        Assert.Equal("snapshot", await ReadOutcomeAsync(gui, SendAttach(gui, id, null)));
        Assert.Equal((2, 1), await ListedAsync());

        Assert.Equal("snapshot", await ReadOutcomeAsync(peek, SendAttach(peek, id, null))); // the peek re-attaches interactive
        Assert.Equal((2, 2), await ListedAsync());
    }

    /// <summary>Review ruling 3: peeking with --read-only must not undo a deliberate detach, neither by attaching nor by leaving.</summary>
    [Fact]
    public async Task A_read_only_peek_does_not_clear_a_user_detach()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        RawMuxConnection gui = await ConnectV2Async(host);
        RawMuxConnection peek = await ConnectV2Async(host);
        Assert.Equal("snapshot", await ReadOutcomeAsync(gui, SendAttach(gui, id, null)));
        Assert.Equal("ok", await ReadOutcomeAsync(gui, gui.Request(MuxMethods.Detach, new DetachParams { SessionId = id, UserDetached = true }, MuxJsonContext.Default.DetachParams)));
        await host.Mux(id).InvokeAsync(() => 0);
        Assert.True(host.Mux(id).DetachedByUser);

        Assert.Equal("snapshot", await ReadOutcomeAsync(peek, SendAttach(peek, id, MuxAttachModes.ReadOnly)));
        await host.Mux(id).InvokeAsync(() => 0);
        Assert.True(host.Mux(id).DetachedByUser);

        peek.Dispose();
        await TestWait.UntilAsync(() => host.Mux(id).AttachedClients == 0, "the peek's connection was detached");
        await host.Mux(id).InvokeAsync(() => 0);
        Assert.True(host.Mux(id).DetachedByUser);

        Assert.Equal("snapshot", await ReadOutcomeAsync(gui, SendAttach(gui, id, null)));   // an interactive attach clears it
        await host.Mux(id).InvokeAsync(() => 0);
        Assert.False(host.Mux(id).DetachedByUser);
    }
}
