using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.VT;

namespace Ntilde.Mux.Tests.Headless;

public sealed class HeadlessSixelScrollingTests
{
    private const string Image = "\x1bPq!30~-~\x1b\\";

    [Fact]
    public async Task Recording_starts_with_authoritative_metrics_and_follows_font_updates()
    {
        using var host = new MuxTestHost();
        using var client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        var presentation = MuxTestHost.DefaultPresentation with { CellWidthPx = 12, CellHeightPx = 6, SupportsSixelScrolling = true };
        var pane = await MuxTestHost.AttachPaneAsync(client, id, presentation);
        pane.Session.StartRecording("scripted.rec");
        await TestWait.UntilAsync(() => pane.Session.IsRecording, "recording was acknowledged");
        Assert.Equal(new Ntilde.Replay.RecordingCellMetrics(12, 6), host.Fake(id).RecordingCellMetricsAtStart);
        pane.Session.UpdatePresentation(presentation with { CellHeightPx = 12 });
        await host.SettleAsync(id, client);
        Assert.Equal(new Ntilde.Replay.RecordingCellMetrics(12, 12), host.Fake(id).CurrentRecordingCellMetrics);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Presentation_updates_can_opt_out_of_SIXEL_notifications(int version)
    {
        using var host = new MuxTestHost();
        using var client = await host.ConnectClientAsync(new MuxClientOptions { MinProtocolVersion = version, MaxProtocolVersion = version });
        using var ownerClient = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        var presentation = MuxTestHost.DefaultPresentation with { SupportsSixelScrolling = true };
        var pane = await MuxTestHost.AttachPaneAsync(client, id, presentation);
        var owner = await MuxTestHost.AttachPaneAsync(ownerClient, id, presentation);
        int updates = 0;
        pane.Session.SixelCellSizeChanged += (_, _) => Interlocked.Increment(ref updates);
        await host.SettleAsync(id, client, ownerClient);
        pane.Session.UpdatePresentation(presentation with { SupportsSixelScrolling = false });
        await host.SettleAsync(id, client, ownerClient);
        int before = Volatile.Read(ref updates);

        owner.Session.UpdatePresentation(presentation with { CellHeightPx = 6 });
        await TestWait.UntilAsync(() => owner.Parser.SixelCellHeightPx == 6, "the opted-in peer received the changed metrics");
        host.Fake(id).Emit(Image);
        await host.SettleAsync(id, client, ownerClient);

        Assert.Equal(before, Volatile.Read(ref updates));
        Assert.True(client.IsConnected);
        host.AssertPaneMatchesMux(id, owner, "the remaining opted-in peer stays synchronized");
    }

    [Fact]
    public async Task Reattaching_without_SIXEL_support_removes_the_previous_subscription()
    {
        using var host = new MuxTestHost();
        using var client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        var sink = new RecordingFrameSink();
        var mux = host.Mux(id);
        var presentation = MuxTestHost.DefaultPresentation with { SupportsSixelScrolling = true };
        mux.PostAttach(sink, 1, 0, presentation, MuxProtocol.MaxFrameBytes);
        await mux.FlushAsync();
        mux.PostAttach(sink, 2, 0, presentation with { SupportsSixelScrolling = false }, MuxProtocol.MaxFrameBytes);
        await mux.FlushAsync();
        int before = sink.Frames.Count(f => f.IsNotification(MuxMethods.SixelCellSize));

        mux.PostResize(80, 24, presentation with { CellHeightPx = 6 });
        host.Fake(id).Emit(Image);
        await mux.FlushAsync();

        Assert.Equal(6, mux.Parser.SixelCellHeightPx);
        Assert.Equal(before, sink.Frames.Count(f => f.IsNotification(MuxMethods.SixelCellSize)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_metric_delivery_stops_only_that_session_before_later_output(bool faultHandlerThrows)
    {
        using var host = new MuxTestHost();
        using var client = await host.ConnectClientAsync();
        Guid brokenId = await MuxTestHost.SpawnAsync(client);
        Guid healthyId = await MuxTestHost.SpawnAsync(client);
        var presentation = MuxTestHost.DefaultPresentation with { SupportsSixelScrolling = true };
        var broken = await MuxTestHost.AttachPaneAsync(client, brokenId, presentation);
        var healthy = await MuxTestHost.AttachPaneAsync(client, healthyId, presentation);
        int faults = 0;
        broken.Session.SixelCellSizeChanged += (_, _) => throw new InvalidOperationException("metric handler failed");
        broken.Session.Faulted += _ =>
        {
            Interlocked.Increment(ref faults);
            if (faultHandlerThrows) throw new InvalidOperationException("fault handler failed");
        };

        broken.Session.UpdatePresentation(presentation with { CellHeightPx = 6 });
        await TestWait.UntilAsync(() => Volatile.Read(ref faults) == 1, "the affected session faulted");
        host.Fake(brokenId).Emit("unseen");
        host.Fake(healthyId).Emit("still live");
        await host.SettleAsync(brokenId, client);
        await host.SettleAsync(healthyId, client);

        Assert.True(client.IsConnected);
        Assert.False(broken.Session.IsAttached);
        await TestWait.UntilAsync(() => host.Mux(brokenId).AttachedClients == 0, "the daemon released the failed subscription");
        Assert.DoesNotContain(broken.Events, e => e.StartsWith("out:", StringComparison.Ordinal));
        Assert.Contains("out:still live", healthy.Events);
        host.AssertPaneMatchesMux(healthyId, healthy, "sibling survived metric callback failure");
    }

    [Theory]
    [InlineData(1, 12, 1)]
    [InlineData(2, 12, 1)]
    [InlineData(1, 5.9999995f, 3)]
    [InlineData(2, 5.9999995f, 3)]
    public async Task The_daemon_and_clients_share_SIXEL_layout_before_and_after_metric_changes(int version, float newHeight, int secondRows)
    {
        using var host = new MuxTestHost();
        using var client = await host.ConnectClientAsync(new MuxClientOptions { MinProtocolVersion = version, MaxProtocolVersion = version });
        Guid id = await client.SpawnAsync(new SpawnParams { Command = "scripted", Cols = 10, Rows = 6 }, TestContext.Current.CancellationToken);
        using MuxClientSession session = client.OpenSession(id, "scripted");
        var pane = new ClientPaneModel(session);
        pane.Parser.CellWidth = 3; // Local font/DIP metrics must not decide the shared layout.
        pane.Parser.CellHeight = 3;
        var presentation = new MuxPresentation { Cols = 10, Rows = 6, CellWidthPx = 20, CellHeightPx = 6, SupportsSixelScrolling = true };
        await session.AttachAsync(0, presentation, TestContext.Current.CancellationToken);
        Assert.Equal(6, pane.Parser.SixelCellHeightPx);
        host.Fake(id).Emit("\x1b[6;5H" + Image + "\x1b[6n");
        await TestWait.UntilAsync(() => pane.Buffer.TotalLines == 8, "the GUI scrolled the same two image rows as the headless daemon");
        await host.Mux(id).FlushAsync();
        Assert.Equal(host.Mux(id).Buffer.TotalLines, pane.Buffer.TotalLines);
        Assert.Equal((0, 5), (pane.Buffer.CursorCol, pane.Buffer.CursorRow));
        await TestWait.UntilAsync(() => host.Fake(id).SentInput.Contains("\x1b[6;1R"), "the daemon answers CPR from the same position");

        session.UpdatePresentation(presentation with { CellHeightPx = newHeight });
        await TestWait.UntilAsync(() => pane.Parser.SixelCellHeightPx == newHeight, "the ordered metric notification reached the client");
        host.Fake(id).Emit(Image);
        await TestWait.UntilAsync(() => pane.Buffer.TotalLines == 8 + secondRows, "the next image uses the new height, including a fractional boundary change");
        Assert.Equal(host.Mux(id).Buffer.TotalLines, pane.Buffer.TotalLines);
        Assert.Equal(3, pane.Parser.CellHeight); // SIXEL did not mutate general local font metrics.
        if (version == 1) Assert.Null(session.AttachedClients);
    }

    [Fact]
    public async Task Snapshot_and_partial_DCS_tail_restore_display_mode_and_cell_size()
    {
        using var host = new MuxTestHost();
        using var client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        using MuxClientSession session = client.OpenSession(id, "scripted");
        var pane = new ClientPaneModel(session);
        var presentation = MuxTestHost.DefaultPresentation with { CellHeightPx = 6, SupportsSixelScrolling = true };
        await session.AttachAsync(0, presentation, TestContext.Current.CancellationToken);
        host.Fake(id).Emit("\x1b[?80h\x1b[4;3H\x1bPq!30~");
        await host.Mux(id).FlushAsync();
        var snapshot = await host.Mux(id).InvokeAsync(() => host.Mux(id).CaptureSnapshot(0));
        var restored = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(restored);
        TerminalStateTransfer.Restore(restored, parser, snapshot);
        parser.Process("-~\x1b\\");
        host.Fake(id).Emit("-~\x1b\\");
        await host.Mux(id).FlushAsync();
        Assert.Equal((2, 3), (restored.CursorCol, restored.CursorRow));
        Assert.Equal(host.Mux(id).Buffer.CursorRow, restored.CursorRow);
        Assert.True(restored.Modes.IsSixelDisplayMode);
        Assert.Equal(6, parser.SixelCellHeightPx);
    }
    [Fact]
    public async Task Pending_metric_bursts_coalesce_and_observers_do_not_override_them()
    {
        using var host = new MuxTestHost();
        using var client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        var gui = new RecordingFrameSink();
        var observer = new RecordingFrameSink();
        var legacy = new RecordingFrameSink();
        var presentation = MuxTestHost.DefaultPresentation with { SupportsSixelScrolling = true };
        var mux = host.Mux(id);
        mux.PostAttach(gui, 1, 0, presentation, MuxProtocol.MaxFrameBytes);
        mux.PostAttach(observer, 2, 0, presentation with { CellHeightPx = 999 }, MuxProtocol.MaxFrameBytes, MuxAttachMode.ReadOnly);
        mux.PostAttach(legacy, 3, 0, MuxTestHost.DefaultPresentation, MuxProtocol.MaxFrameBytes, MuxAttachMode.ReadOnly);
        await mux.FlushAsync();
        int initial = gui.Frames.Count(f => f.IsNotification(MuxMethods.SixelCellSize));
        using var parked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task<int> waiting = mux.InvokeAsync(() => { parked.Set(); release.Wait(TimeSpan.FromSeconds(30)); return 0; });
        Assert.True(parked.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        try
        {
            for (int i = 0; i < 500; i++) mux.PostResize(gui, 80, 24, presentation with { CellHeightPx = 6 + (i % 2) });
            Assert.Equal(1, mux.QueuedControlCount);
            host.Fake(id).Emit(Image);
        }
        finally { release.Set(); }
        await waiting;
        await mux.FlushAsync();
        Assert.Equal(initial + 1, gui.Frames.Count(f => f.IsNotification(MuxMethods.SixelCellSize)));
        var frames = gui.Frames;
        Assert.True(frames[^2].IsNotification(MuxMethods.SixelCellSize));
        Assert.Equal(MuxFrameKind.Output, frames[^1].Kind);
        SixelCellSizeNotification size = MuxFrames.ParseParams(MuxFrames.ParseJson(frames[^2].Payload, MuxJsonContext.Default.MuxNotification).Params, MuxJsonContext.Default.SixelCellSizeNotification);
        Assert.Equal(7, size.CellHeightPx);
        Assert.Contains(observer.Frames, f => f.IsNotification(MuxMethods.SixelCellSize));
        Assert.DoesNotContain(legacy.Frames, f => f.IsNotification(MuxMethods.SixelCellSize));
    }

    [Theory]
    [InlineData(null, null, 3)]
    [InlineData(20f, 6f, 4)]
    [InlineData(20f, 12f, 3)]
    [InlineData(20f, 3f, 6)]
    public async Task Initial_metrics_apply_to_constructor_time_SIXEL_before_any_attach(float? width, float? height, int expectedRow)
    {
        using var host = new MuxTestHost();
        host.Factory.StartupOutput = "\x1b[2;1H" + Image + "\x1b[6n";
        using var client = await host.ConnectClientAsync();
        Guid id = await client.SpawnAsync(new SpawnParams
        {
            Command = "scripted",
            Cols = 10,
            Rows = 6,
            CellWidthPx = width,
            CellHeightPx = height,
        }, TestContext.Current.CancellationToken);
        await host.Mux(id).FlushAsync();
        Assert.Equal(0, host.Mux(id).AttachedClients);
        await TestWait.UntilAsync(() => !host.Fake(id).SentInput.IsEmpty, "the first startup query was answered before attach");
        Assert.Equal($"\x1b[{expectedRow};1R", Assert.Single(host.Fake(id).SentInput));
        var request = Assert.Single(host.Factory.Requests);
        Assert.Equal(height, request.CellHeightPx);
        Assert.Equal(width, request.CellWidthPx);
        Assert.Equal(width ?? 10, host.Mux(id).Parser.SixelCellWidthPx);
    }

    [Theory]
    [InlineData(null, 6f)]
    [InlineData(10f, null)]
    [InlineData(0f, 6f)]
    [InlineData(10f, -1f)]
    public async Task Invalid_initial_metric_pairs_are_refused_before_a_child_is_started(float? width, float? height)
    {
        using var host = new MuxTestHost();
        using var client = await host.ConnectClientAsync();
        var error = await Assert.ThrowsAsync<MuxProtocolException>(() => client.SpawnAsync(new SpawnParams
        {
            Command = "scripted",
            CellWidthPx = width,
            CellHeightPx = height,
        }, TestContext.Current.CancellationToken));
        Assert.Equal(MuxErrorCodes.ProtocolError, error.Code);
        Assert.Empty(host.Factory.Requests);
    }

}
