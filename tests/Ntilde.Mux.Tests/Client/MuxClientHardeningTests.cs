using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Client;

public sealed class MuxClientHardeningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task IsRecording_turns_true_only_once_the_server_has_started_recording()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        using MuxClientSession session = client.OpenSession(id, "scripted");

        session.StartRecording(Path.Combine(Path.GetTempPath(), "mux-rec-ok.nrec"));

        await TestWait.UntilAsync(() => session.IsRecording, "the confirmed recording shows as active");
        Assert.True(host.Fake(id).IsRecording);
    }

    [Fact]
    public async Task A_recording_the_server_could_not_start_never_shows_as_active()
    {
        // Disk full, permission denied: the server cannot open the path. The client must not report
        // a recording that does not exist - a pane's recording indicator would lie.
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        host.Fake(id).ThrowOnStartRecording = true;
        using MuxClientSession session = client.OpenSession(id, "scripted");

        session.StartRecording("Z:\\denied\\rec.nrec");
        await client.PingAsync(Ct);   // the start request has been answered (with an error)
        await Task.Delay(100, Ct);    // and its continuation has had time to run

        Assert.False(session.IsRecording);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task A_snapshot_racing_Dispose_does_not_reattach_the_disposed_session()
    {
        // Dispose lands between DeliverSnapshot's disposed check and the moment it publishes the
        // attached offset. Publishing anyway resurrected the session (IsAttached true again) and
        // raised SnapshotReceived into a pane that had already been torn down.
        using var fake = FakeMuxServerEnd.Create();
        Task<MuxClient> connect = MuxClient.ConnectAsync(fake.ClientEnd, new MuxClientOptions(), Ct);
        await fake.AcceptHelloAsync();
        using MuxClient client = await connect;
        Guid id = Guid.NewGuid();
        MuxClientSession session = client.OpenSession(id);
        var pane = new ClientPaneModel(session);
        session.BeforeSnapshotPublishedForTest = session.Dispose; // the race, made deterministic

        Task<long> attach = session.AttachAsync(100, MuxTestHost.DefaultPresentation, Ct);
        MuxRequest request = await fake.ReadRequestAsync();
        fake.Raw.Send(MuxFrames.Snapshot(request.Id, id, 0, FakeMuxServerEnd.SnapshotJson()));
        await Record.ExceptionAsync(() => attach.WaitAsync(TimeSpan.FromSeconds(5), Ct)); // settles either way

        Assert.False(session.IsAttached);
        Assert.Empty(pane.Events);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task An_output_offset_that_would_overflow_disconnects_instead_of_wrapping()
    {
        // A malformed server attaches at StreamSeq == long.MaxValue, then sends output there. The
        // unchecked sum wrapped negative, after which every frame was ignored as if detached -
        // silence, where the continuity rule demands a protocol disconnect.
        using var fake = FakeMuxServerEnd.Create();
        Task<MuxClient> connect = MuxClient.ConnectAsync(fake.ClientEnd, new MuxClientOptions(), Ct);
        await fake.AcceptHelloAsync();
        using MuxClient client = await connect;
        Guid id = Guid.NewGuid();
        var pane = new ClientPaneModel(client.OpenSession(id));

        Task<long> attach = pane.Session.AttachAsync(100, MuxTestHost.DefaultPresentation, Ct);
        MuxRequest request = await fake.ReadRequestAsync();
        fake.Raw.Send(MuxFrames.Snapshot(request.Id, id, long.MaxValue, FakeMuxServerEnd.SnapshotJson(streamSeq: long.MaxValue)));
        Assert.Equal(long.MaxValue, await attach);

        fake.Raw.Send(MuxFrames.Output(id, long.MaxValue, "x"u8));

        await TestWait.UntilAsync(() => !client.IsConnected, "the client drops the connection");
        Assert.Equal(MuxErrorCodes.ProtocolError, client.DisconnectReason);
    }
}
