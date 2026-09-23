using System.Text;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Pty;

namespace Ntilde.Mux.Tests.Client;

public sealed class MuxClientSessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// A stream built to hurt: SGR, cursor moves, a code point split across chunks, the alt screen,
    /// an OSC 8 link split mid-sequence, a scroll region, and a DA query the mux must answer.
    private static readonly byte[][] HostileChunks =
    [
        "plain "u8.ToArray(), "\x1b[1;31mred\x1b[0m\r\n"u8.ToArray(), [0xE2, 0x82], [0xAC, (byte)'\r', (byte)'\n'],
        "\x1b[5;10Hmoved"u8.ToArray(), "\x1b]8;id=a;https://ex"u8.ToArray(), "ample.com\x1b\\link\x1b]8;;\x1b\\"u8.ToArray(),
        "\x1b[?1049h"u8.ToArray(), "alt screen\x1b[2J\x1b[H"u8.ToArray(), "\x1b[?1049l"u8.ToArray(), "\x1b[3;20r"u8.ToArray(),
        "\x1b[c"u8.ToArray(), "\x1b[r\x1b[999;999H"u8.ToArray(), "end"u8.ToArray(),
    ];

    [Fact]
    public async Task Spawn_attach_stream_the_client_equals_the_mux_after_every_chunk()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(client, id);
        ScriptedTerminalSession fake = host.Fake(id);

        for (int i = 0; i < HostileChunks.Length; i++)
        {
            fake.Emit(HostileChunks[i]);
            await host.SettleAsync(id, client);
            host.AssertPaneMatchesMux(id, pane, $"after chunk {i}");
        }
    }

    [Fact]
    public async Task Attach_reports_the_snapshot_seq_and_StreamPosition_follows_the_stream()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        host.Fake(id).Emit([(byte)'a', 0xC3]); // ends mid code point: seq 1, tail 1
        await host.Mux(id).FlushAsync();

        MuxClientSession session = client.OpenSession(id, "scripted");
        var pane = new ClientPaneModel(session);
        long seq = await session.AttachAsync(100, MuxTestHost.DefaultPresentation, Ct);
        host.Fake(id).Emit([0xA9]);
        await host.SettleAsync(id, client);

        Assert.Equal(1, seq);
        Assert.Equal(["snapshot@1+1", "out:é"], pane.Events);
        Assert.Equal(3, session.StreamPosition);
    }

    [Fact]
    public async Task Input_round_trips_as_utf8()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        using MuxClientSession session = client.OpenSession(id, "scripted");

        session.SendInput("é\r");
        await client.PingAsync(Ct);

        string sent = Assert.Single(host.Fake(id).SentInput);
        Assert.Equal(new byte[] { 0xC3, 0xA9, 0x0D }, Encoding.UTF8.GetBytes(sent));
    }

    [Fact]
    public async Task The_exit_code_surfaces_once()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(client, id);
        var exits = new List<int>();
        pane.Session.OnExit += exits.Add;

        host.Fake(id).Exit(3);
        await host.SettleAsync(id, client);

        Assert.Equal([3], exits);
        Assert.Equal(3, pane.Session.ExitCode);
        Assert.False(pane.Session.IsProcessRunning);
        SessionSummary summary = Assert.Single(await client.ListSessionsAsync(Ct));
        Assert.Equal((false, (int?)3), (summary.Running, summary.ExitCode));
    }

    [Fact]
    public async Task Kill_propagates_Exited_and_removes_the_session()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(client, id);
        ScriptedTerminalSession fake = host.Fake(id);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        pane.Session.OnExit += code => exited.TrySetResult(code);

        await pane.Session.KillAsync(Ct);

        Assert.Equal(-1, await exited.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.True(fake.Disposed);
        Assert.Empty(await client.ListSessionsAsync(Ct));
    }

    [Fact]
    public async Task Capabilities_and_session_info()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        using MuxClientSession session = client.OpenSession(id, "scripted");

        // Deliberately typed as the interface: this exercises ITerminalSessionCapabilities as a
        // consumer would, not MuxClientSession directly.
#pragma warning disable CA1859
        ITerminalSessionCapabilities caps = session;
#pragma warning restore CA1859
        Assert.True(caps.AnswersDeviceQueries);
        Assert.True(caps.OrdersResizeInStream);
        // Set first: the read below also starts a background refresh, and a refresh that probed
        // before the flag flipped could land after the explicit one and overwrite it.
        host.Fake(id).HasActiveChildProcesses = true;
        Assert.False(session.HasActiveChildProcesses); // the cached initial value; never blocks

        SessionInfoResult info = await session.RefreshSessionInfoAsync(Ct);

        Assert.True(info.HasActiveChildProcesses);
        Assert.True(session.HasActiveChildProcesses);
    }

    [Fact]
    public async Task Resize_sends_a_request_and_raises_StreamResize_only_at_the_event()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(client, id);
        host.Fake(id).Emit("x");
        await host.SettleAsync(id, client);

        int resizeHandlerThread = -1;
        pane.Session.StreamResize += (_, _) => resizeHandlerThread = Environment.CurrentManagedThreadId;
        pane.Session.Resize(100, 30);
        await host.SettleAsync(id, client);

        // Exactly one resize, raised by the delivery thread (never by Resize() on the caller).
        Assert.Equal(["snapshot@0+0", "out:x", "resize:100x30"], pane.Events);
        Assert.NotEqual(Environment.CurrentManagedThreadId, resizeHandlerThread);
        host.AssertPaneMatchesMux(id, pane, "after resize");
    }

    [Fact]
    public async Task Stream_events_come_from_the_reader_thread_but_Disconnected_from_whoever_ends_the_connection()
    {
        // Pins the thread contract documented on MuxClientSession (Phase 2's pane marshals by it).
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        MuxClientSession session = client.OpenSession(id, "scripted");
        string? snapshotThread = null, outputThread = null;
        int disconnectedThread = -1, disconnectedCount = 0;
        session.SnapshotReceived += _ => snapshotThread = Thread.CurrentThread.Name;
        session.OnOutputReceived += _ => outputThread = Thread.CurrentThread.Name;
        session.Disconnected += _ => { disconnectedThread = Environment.CurrentManagedThreadId; disconnectedCount++; };

        await session.AttachAsync(100, MuxTestHost.DefaultPresentation, Ct);
        host.Fake(id).Emit("x");
        await host.SettleAsync(id, client);
        client.Dispose();

        Assert.Equal("MuxClientRead", snapshotThread);
        Assert.Equal("MuxClientRead", outputThread);
        Assert.Equal(Environment.CurrentManagedThreadId, disconnectedThread); // the Dispose caller, not the reader
        Assert.Equal(1, disconnectedCount);
    }

    [Fact]
    public async Task Output_with_a_seq_gap_disconnects_with_protocol_error()
    {
        using var fake = FakeMuxServerEnd.Create();
        Task<MuxClient> connect = MuxClient.ConnectAsync(fake.ClientEnd, new MuxClientOptions(), Ct);
        await fake.AcceptHelloAsync();
        using MuxClient client = await connect;
        Guid id = Guid.NewGuid();
        MuxClientSession session = client.OpenSession(id);
        var pane = new ClientPaneModel(session);

        Task<long> attach = session.AttachAsync(100, MuxTestHost.DefaultPresentation, Ct);
        MuxRequest request = await fake.ReadRequestAsync();
        fake.Raw.Send(MuxFrames.Snapshot(request.Id, id, 0, FakeMuxServerEnd.SnapshotJson()));
        await attach;
        fake.Raw.Send(MuxFrames.Output(id, 5, "gap"u8)); // expected 0

        await TestWait.UntilAsync(() => !client.IsConnected, "the client drops the connection");
        Assert.Equal(MuxErrorCodes.ProtocolError, client.DisconnectReason);
        Assert.DoesNotContain(pane.Events, e => e.StartsWith("out:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_malformed_snapshot_fails_the_attach_and_touches_no_buffer()
    {
        using var fake = FakeMuxServerEnd.Create();
        Task<MuxClient> connect = MuxClient.ConnectAsync(fake.ClientEnd, new MuxClientOptions(), Ct);
        await fake.AcceptHelloAsync();
        using MuxClient client = await connect;
        Guid id = Guid.NewGuid();
        var pane = new ClientPaneModel(client.OpenSession(id));

        Task<long> attach = pane.Session.AttachAsync(100, MuxTestHost.DefaultPresentation, Ct);
        MuxRequest request = await fake.ReadRequestAsync();
        fake.Raw.Send(MuxFrames.Snapshot(request.Id, id, 0, "{not a snapshot"u8));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => attach);
        Assert.True(ex is MuxProtocolException { Code: MuxErrorCodes.ProtocolError } or IOException, ex.ToString());
        Assert.Empty(pane.Events);
        await TestWait.UntilAsync(() => !client.IsConnected, "the client drops the connection");
    }

    [Fact]
    public async Task A_resize_event_over_the_clients_cell_ceiling_disconnects_with_protocol_error()
    {
        using var fake = FakeMuxServerEnd.Create();
        Task<MuxClient> connect = MuxClient.ConnectAsync(fake.ClientEnd, new MuxClientOptions(), Ct);
        await fake.AcceptHelloAsync();
        using MuxClient client = await connect;
        Guid id = Guid.NewGuid();
        MuxClientSession session = client.OpenSession(id, "scripted");
        var pane = new ClientPaneModel(session);

        Task<long> attach = session.AttachAsync(100, MuxTestHost.DefaultPresentation, Ct);
        MuxRequest request = await fake.ReadRequestAsync();
        fake.Raw.Send(MuxFrames.Snapshot(request.Id, id, 0, FakeMuxServerEnd.SnapshotJson()));
        await attach;

        // MaxCells only guards a snapshot's grid; an in-stream resize can demand the same oversize
        // buffer just as well, and there is no earlier point at which to refuse it.
        fake.Raw.Send(MuxFrames.ResizeEvent(id, 0, 100_000, 100_000));

        await TestWait.UntilAsync(() => !client.IsConnected, "the client drops the connection");
        Assert.Equal(MuxErrorCodes.ProtocolError, client.DisconnectReason);
        Assert.DoesNotContain(pane.Events, e => e.StartsWith("resize:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancelling_AttachAsync_drops_the_late_snapshot_and_detaches_instead_of_disconnecting()
    {
        using var fake = FakeMuxServerEnd.Create();
        Task<MuxClient> connect = MuxClient.ConnectAsync(fake.ClientEnd, new MuxClientOptions(), Ct);
        await fake.AcceptHelloAsync();
        using MuxClient client = await connect;
        Guid id = Guid.NewGuid();
        MuxClientSession session = client.OpenSession(id, "scripted");
        var pane = new ClientPaneModel(session);

        using var cts = new CancellationTokenSource();
        Task<long> attach = session.AttachAsync(100, MuxTestHost.DefaultPresentation, cts.Token);
        MuxRequest request = await fake.ReadRequestAsync();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attach);

        // The server had already committed to answering when the caller gave up; its (now unwanted)
        // snapshot can still arrive.
        fake.Raw.Send(MuxFrames.Snapshot(request.Id, id, 0, FakeMuxServerEnd.SnapshotJson()));

        // Reading the client's next frame proves its reader thread kept running (rather than tearing
        // the connection down over what would otherwise look like an unsolicited snapshot) and that
        // it cleaned up the subscription the server made on our behalf.
        MuxRequest detach = await fake.ReadRequestAsync();
        Assert.Equal(MuxMethods.Detach, detach.Method);
        // It names the attach it undoes, so the server can ignore it if a retry has superseded it.
        Assert.Equal(request.Id, MuxFrames.ParseParams(detach.Params, MuxJsonContext.Default.DetachParams).AttachRequestId);
        Assert.True(client.IsConnected);
        Assert.Empty(pane.Events);
    }

    [Fact]
    public async Task Cancelling_while_the_snapshot_is_already_being_delivered_reports_the_attach_that_happened()
    {
        // The sliver: the reader has claimed the snapshot (the pane is restoring it right now) when
        // the caller's token fires. Reporting a cancellation would contradict what the pane just
        // saw - SnapshotReceived raised, output about to stream - so the attach reports its outcome.
        using var fake = FakeMuxServerEnd.Create();
        Task<MuxClient> connect = MuxClient.ConnectAsync(fake.ClientEnd, new MuxClientOptions(), Ct);
        await fake.AcceptHelloAsync();
        using MuxClient client = await connect;
        Guid id = Guid.NewGuid();
        MuxClientSession session = client.OpenSession(id, "scripted");
        var pane = new ClientPaneModel(session);
        using var restoring = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        session.SnapshotReceived += _ => { restoring.Set(); release.Wait(Ct); };

        using var cts = new CancellationTokenSource();
        Task<long> attach = session.AttachAsync(100, MuxTestHost.DefaultPresentation, cts.Token);
        MuxRequest request = await fake.ReadRequestAsync();
        fake.Raw.Send(MuxFrames.Snapshot(request.Id, id, 0, FakeMuxServerEnd.SnapshotJson()));
        Assert.True(restoring.Wait(TimeSpan.FromSeconds(10), Ct), "the reader is delivering the snapshot");
        await cts.CancelAsync();
        release.Set();

        Assert.Equal(0, await attach.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        fake.Raw.Send(MuxFrames.Output(id, 0, "x"u8));
        await TestWait.UntilAsync(() => pane.Events.Contains("out:x"), "output reaches the attached pane");
        Assert.True(session.IsAttached);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task A_snapshot_with_a_negative_StreamSeq_fails_the_attach_and_disconnects()
    {
        using var fake = FakeMuxServerEnd.Create();
        Task<MuxClient> connect = MuxClient.ConnectAsync(fake.ClientEnd, new MuxClientOptions(), Ct);
        await fake.AcceptHelloAsync();
        using MuxClient client = await connect;
        Guid id = Guid.NewGuid();
        var pane = new ClientPaneModel(client.OpenSession(id));

        Task<long> attach = pane.Session.AttachAsync(100, MuxTestHost.DefaultPresentation, Ct);
        MuxRequest request = await fake.ReadRequestAsync();
        fake.Raw.Send(MuxFrames.Snapshot(request.Id, id, -1, FakeMuxServerEnd.SnapshotJson(streamSeq: -1)));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => attach);
        Assert.True(ex is MuxProtocolException { Code: MuxErrorCodes.ProtocolError } or IOException, ex.ToString());
        Assert.Empty(pane.Events);
        await TestWait.UntilAsync(() => !client.IsConnected, "the client drops the connection");
    }

    [Fact]
    public async Task A_snapshot_whose_StreamSeq_plus_tail_overflows_fails_the_attach_and_disconnects()
    {
        using var fake = FakeMuxServerEnd.Create();
        Task<MuxClient> connect = MuxClient.ConnectAsync(fake.ClientEnd, new MuxClientOptions(), Ct);
        await fake.AcceptHelloAsync();
        using MuxClient client = await connect;
        Guid id = Guid.NewGuid();
        var pane = new ClientPaneModel(client.OpenSession(id));

        Task<long> attach = pane.Session.AttachAsync(100, MuxTestHost.DefaultPresentation, Ct);
        MuxRequest request = await fake.ReadRequestAsync();
        byte[] json = FakeMuxServerEnd.SnapshotJson(streamSeq: long.MaxValue, tail: [0xC3]);
        fake.Raw.Send(MuxFrames.Snapshot(request.Id, id, long.MaxValue, json));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => attach);
        Assert.True(ex is MuxProtocolException { Code: MuxErrorCodes.ProtocolError } or IOException, ex.ToString());
        Assert.Empty(pane.Events);
        await TestWait.UntilAsync(() => !client.IsConnected, "the client drops the connection");
    }

    [Fact]
    public async Task A_plain_success_response_to_attach_with_no_snapshot_throws_protocol_error()
    {
        using var fake = FakeMuxServerEnd.Create();
        Task<MuxClient> connect = MuxClient.ConnectAsync(fake.ClientEnd, new MuxClientOptions(), Ct);
        await fake.AcceptHelloAsync();
        using MuxClient client = await connect;
        Guid id = Guid.NewGuid();
        MuxClientSession session = client.OpenSession(id, "scripted");
        var pane = new ClientPaneModel(session);

        Task<long> attach = session.AttachAsync(100, MuxTestHost.DefaultPresentation, Ct);
        MuxRequest request = await fake.ReadRequestAsync();
        fake.Reply(request.Id, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty); // a bare success reply, never a Snapshot

        var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => attach);
        Assert.Equal(MuxErrorCodes.ProtocolError, ex.Code);
        Assert.Empty(pane.Events);
        Assert.True(client.IsConnected); // the attach itself failed; the connection did not
    }
}
