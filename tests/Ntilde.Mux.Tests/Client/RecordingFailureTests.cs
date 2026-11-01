using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Replay;

namespace Ntilde.Mux.Tests.Client;

public sealed class RecordingFailureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MidRecordingFailureCrossesDaemonBoundaryWithoutEndingTerminal(bool attached)
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        ClientPaneModel? pane = attached ? await MuxTestHost.AttachPaneAsync(client, id) : null;
        MuxClientSession session = pane?.Session ?? client.OpenSession(id);
        var failed = new TaskCompletionSource<RecordingFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.RecordingFailed += _ => throw new InvalidOperationException("advisory observer failed");
        session.RecordingFailed += failure => failed.TrySetResult(failure);
        await session.StartRecordingAsync("test.nrec");
        Assert.True(session.IsRecording);
        host.Fake(id).FailRecording(RecordingFailureKind.Overflow);
        RecordingFailure error = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(RecordingFailureKind.Overflow, error.Kind);
        Assert.False(session.IsRecording);
        host.Fake(id).Emit("terminal still alive");
        await host.SettleAsync(id, client);
        Assert.True(client.IsConnected);
        if (pane is not null) Assert.Contains(pane.Events, e => e.Contains("terminal still alive", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAndStopAwaitDiskOutsideParserBoundary()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(client, id);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = host.Fake(id);
        fake.RecordingReady = ready.Task;
        fake.RecordingFlushed = flushed.Task;
        fake.OnStartRecording = () => started.TrySetResult();
        fake.OnStopRecording = () => stopped.TrySetResult();
        try
        {
            Task start = pane.Session.StartRecordingAsync("test.nrec");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.False(start.IsCompleted);
            Assert.False(pane.Session.IsRecording);
            fake.Emit("while opening");
            await host.SettleAsync(id, client);
            ready.TrySetResult();
            await start;
            Assert.True(pane.Session.IsRecording);
            Task stop = pane.Session.StopRecordingAsync();
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.False(stop.IsCompleted);
            fake.Emit("while flushing");
            await host.SettleAsync(id, client);
            flushed.TrySetException(new RecordingException(new RecordingFailure(RecordingFailureKind.Write, "flush failed")));
            Assert.Equal(RecordingFailureKind.Write, (await Assert.ThrowsAsync<RecordingException>(() => stop)).Failure.Kind);
            Assert.False(pane.Session.IsRecording);
            Assert.True(client.IsConnected);
        }
        finally { ready.TrySetResult(); flushed.TrySetResult(); }
    }

    [Fact]
    public async Task OpenFailureKeepsItsTypedKindAcrossRpc()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        host.Fake(id).RecordingReady = Task.FromException(new RecordingException(new RecordingFailure(RecordingFailureKind.Open, "access denied")));
        using MuxClientSession session = client.OpenSession(id);
        Assert.Equal(RecordingFailureKind.Open, (await Assert.ThrowsAsync<RecordingException>(() => session.StartRecordingAsync("bad.nrec"))).Failure.Kind);
        Assert.False(session.IsRecording);
        Assert.True(client.IsConnected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoricalFailureWarnsWithoutStoppingNewRecording(bool longMessage)
    {
        using var fake = FakeMuxServerEnd.Create();
        Task<MuxClient> connecting = MuxClient.ConnectAsync(fake.ClientEnd, new MuxClientOptions(), Ct);
        await fake.AcceptHelloAsync();
        using MuxClient client = await connecting;
        using MuxClientSession session = client.OpenSession(Guid.NewGuid());
        Guid old = Guid.NewGuid();
        Guid current = Guid.NewGuid();
        foreach (Guid id in new[] { old, current })
        {
            Task start = session.StartRecordingAsync("test.nrec");
            MuxRequest request = await fake.ReadRequestAsync();
            fake.Reply(request.Id, new StartRecordingResult { RecordingId = id }, MuxJsonContext.Default.StartRecordingResult);
            await start;
        }
        var observed = new TaskCompletionSource<RecordingFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
        int warnings = 0;
        session.RecordingFailed += failure => { Interlocked.Increment(ref warnings); observed.TrySetResult(failure); };
        string message = longMessage ? new string('x', 10000) : "old disk full";
        void SendFailure() => fake.Raw.Send(MuxFrames.Notification(new MuxNotification
        {
            Method = MuxMethods.RecordingFailed,
            Params = MuxFrames.ToElement(new RecordingFailedNotification
            {
                SessionId = session.Id,
                RecordingId = old,
                Kind = "Write",
                Message = "old disk full"
            }, MuxJsonContext.Default.RecordingFailedNotification)
        }));
        SendFailure();
        RecordingFailure failure = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(old, failure.RecordingId);
        Assert.InRange(failure.Message.Length, 1, 513);
        SendFailure(); // A duplicate cannot flood advisory callbacks.
        Task ping = client.PingAsync(Ct);
        MuxRequest pingRequest = await fake.ReadRequestAsync();
        fake.Reply(pingRequest.Id, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty);
        await ping;
        Assert.Equal(1, warnings);
        Assert.True(session.IsRecording);
        Assert.Equal(current, session.RecordingId);
    }

    [Fact]
    public async Task LateOldFaultCannotInvalidateNewStartAndEarlyCurrentFaultCannotResurrectIt()
    {
        using var fake = FakeMuxServerEnd.Create();
        Task<MuxClient> connecting = MuxClient.ConnectAsync(fake.ClientEnd, new MuxClientOptions(), Ct);
        await fake.AcceptHelloAsync();
        using MuxClient client = await connecting;
        using MuxClientSession session = client.OpenSession(Guid.NewGuid());
        Guid old = Guid.NewGuid();
        Guid current = Guid.NewGuid();
        Task start = session.StartRecordingAsync("one.nrec");
        MuxRequest request = await fake.ReadRequestAsync();
        SendFailure(old);
        fake.Reply(request.Id, new StartRecordingResult { RecordingId = current }, MuxJsonContext.Default.StartRecordingResult);
        await start;
        Assert.True(session.IsRecording);
        SendFailure(current);
        // Ping's reply is an ordered barrier after the failure notification.
        Task ping = client.PingAsync(Ct);
        MuxRequest pingRequest = await fake.ReadRequestAsync();
        fake.Reply(pingRequest.Id, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty);
        await ping;
        Assert.False(session.IsRecording);
        Guid next = Guid.NewGuid();
        Task nextStart = session.StartRecordingAsync("two.nrec");
        MuxRequest nextRequest = await fake.ReadRequestAsync();
        SendFailure(next);
        SendFailure(old); // A delayed old fault must not erase the early failure of this pending start.
        fake.Reply(nextRequest.Id, new StartRecordingResult { RecordingId = next }, MuxJsonContext.Default.StartRecordingResult);
        await Assert.ThrowsAsync<RecordingException>(() => nextStart);
        Assert.False(session.IsRecording);

        void SendFailure(Guid recordingId) => fake.Raw.Send(MuxFrames.Notification(new MuxNotification
        {
            Method = MuxMethods.RecordingFailed,
            Params = MuxFrames.ToElement(new RecordingFailedNotification
            {
                SessionId = session.Id,
                RecordingId = recordingId,
                Kind = "Write",
                Message = "disk full"
            }, MuxJsonContext.Default.RecordingFailedNotification)
        }));
    }
}
