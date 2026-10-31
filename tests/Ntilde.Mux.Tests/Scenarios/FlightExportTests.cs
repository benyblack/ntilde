using System.Text;
using Ntilde.Mux.Tests.Support;
using Ntilde.Replay;
using Ntilde.Mux.Contracts;

namespace Ntilde.Mux.Tests.Scenarios;

public sealed class FlightExportTests
{
    [Fact]
    public async Task Flight_recording_stays_active_until_the_last_connection_owner_disables_it()
    {
        using var host = new MuxTestHost();
        using var a = await host.ConnectClientAsync();
        using var b = await host.ConnectClientAsync();
        using var stranger = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(a);
        async Task Enable(MuxClient client) => _ = await client.RequestAsync(MuxMethods.EnableFlightRecording,
            new EnableFlightRecordingParams { SessionId = id, MaxBytes = 4096 }, MuxJsonContext.Default.EnableFlightRecordingParams, MuxJsonContext.Default.MuxEmpty, TestContext.Current.CancellationToken);
        async Task Disable(MuxClient client) => _ = await client.RequestAsync(MuxMethods.DisableFlightRecording,
            new SessionIdParams { SessionId = id }, MuxJsonContext.Default.SessionIdParams, MuxJsonContext.Default.MuxEmpty, TestContext.Current.CancellationToken);
        await Enable(a);
        await Enable(b);
        await Disable(stranger); // bypasses the client wrapper: the server also enforces ownership
        Assert.True(host.Fake(id).IsFlightRecording);
        await Disable(a);
        Assert.True(host.Fake(id).IsFlightRecording);
        await Disable(b);
        Assert.False(host.Fake(id).IsFlightRecording);
    }

    [Fact]
    public async Task Disconnect_releases_only_that_connections_flight_ownership()
    {
        using var host = new MuxTestHost();
        using var a = await host.ConnectClientAsync();
        using var b = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(a);
        foreach (var client in new[] { a, b })
            await client.RequestAsync(MuxMethods.EnableFlightRecording, new EnableFlightRecordingParams { SessionId = id, MaxBytes = 4096 },
                MuxJsonContext.Default.EnableFlightRecordingParams, MuxJsonContext.Default.MuxEmpty, TestContext.Current.CancellationToken);
        a.Dispose();
        await b.RequestAsync(MuxMethods.DisableFlightRecording, new SessionIdParams { SessionId = id }, MuxJsonContext.Default.SessionIdParams,
            MuxJsonContext.Default.MuxEmpty, TestContext.Current.CancellationToken);
        await TestWait.UntilAsync(() => !host.Fake(id).IsFlightRecording, "both connection owners were released");
    }

    [Theory]
    [InlineData(MuxMethods.StartRecording)]
    [InlineData(MuxMethods.StopRecording)]
    [InlineData(MuxMethods.EnableFlightRecording)]
    [InlineData(MuxMethods.DisableFlightRecording)]
    [InlineData(MuxMethods.ExportFlight)]
    public async Task Recording_requests_leave_the_connection_reader_free_while_the_target_parser_is_held(string method)
    {
        using var host = new MuxTestHost();
        using var client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        var mux = host.Mux(id);
        using var parked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task<bool> parking = mux.InvokeAsync(() => { parked.Set(); return release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); });
        Assert.True(parked.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        try
        {
            Task request = method switch
            {
                MuxMethods.StartRecording => client.RequestAsync(method, new StartRecordingParams { SessionId = id, Path = "scripted.rec" }, MuxJsonContext.Default.StartRecordingParams, MuxJsonContext.Default.MuxEmpty, TestContext.Current.CancellationToken),
                MuxMethods.EnableFlightRecording => client.RequestAsync(method, new EnableFlightRecordingParams { SessionId = id, MaxBytes = 4096 }, MuxJsonContext.Default.EnableFlightRecordingParams, MuxJsonContext.Default.MuxEmpty, TestContext.Current.CancellationToken),
                MuxMethods.ExportFlight => client.RequestAsync(method, new SessionIdParams { SessionId = id }, MuxJsonContext.Default.SessionIdParams, MuxJsonContext.Default.ExportFlightResult, TestContext.Current.CancellationToken),
                _ => client.RequestAsync(method, new SessionIdParams { SessionId = id }, MuxJsonContext.Default.SessionIdParams, MuxJsonContext.Default.MuxEmpty, TestContext.Current.CancellationToken),
            };
            await client.PingAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(request.IsCompleted);
            Assert.False(host.Fake(id).IsRecording);
            release.Set();
            Assert.True(await parking);
            await request;
            Assert.Equal(method == MuxMethods.StartRecording, host.Fake(id).IsRecording);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task Disabling_an_unowned_flight_recorder_does_not_wait_for_the_parser_or_clear_another_clients_ring()
    {
        using var host = new MuxTestHost();
        using var ownerClient = await host.ConnectClientAsync();
        using var otherClient = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(ownerClient);
        using MuxClientSession owner = ownerClient.OpenSession(id, "scripted");
        using MuxClientSession other = otherClient.OpenSession(id, "scripted");
        owner.EnableFlightRecording(1 << 20);
        await ownerClient.PingAsync(TestContext.Current.CancellationToken);
        var mux = host.Mux(id);
        using var parked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task<bool> parking = mux.InvokeAsync(() => { parked.Set(); return release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); });
        Assert.True(parked.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        try
        {
            other.DisableFlightRecording();
            await otherClient.PingAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(host.Fake(id).IsFlightRecording);
            Assert.True(owner.IsFlightRecording);
            Assert.False(other.IsFlightRecording);
        }
        finally { release.Set(); }
        Assert.True(await parking);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("enable")]
    [InlineData("disable")]
    public async Task Recording_lifecycle_requests_do_not_overtake_output_waiting_to_be_parsed(string operation)
    {
        using var host = new MuxTestHost();
        using var client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        using MuxClientSession session = client.OpenSession(id, "scripted");
        if (operation == "disable") session.EnableFlightRecording(1 << 20);
        await client.PingAsync(TestContext.Current.CancellationToken);
        var mux = host.Mux(id);
        await mux.FlushAsync();
        Thread? operationThread = null;
        string? lineAtOperation = null;
        Action observed = () => { operationThread = Thread.CurrentThread; lineAtOperation = BufferSnapshot.Capture(mux.Buffer).Lines[0]; };
        if (operation == "start") host.Fake(id).OnStartRecording = observed;
        else if (operation == "enable") host.Fake(id).OnEnableFlightRecording = observed;
        else host.Fake(id).OnDisableFlightRecording = observed;
        using var parked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Thread? parseThread = null;
        Task<bool> parking = mux.InvokeAsync(() => { parseThread = Thread.CurrentThread; parked.Set(); return release.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); });
        Assert.True(parked.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        try
        {
            host.Fake(id).Emit("before\r\n");
            Task change = Task.Run(async () =>
            {
                if (operation == "start") session.StartRecording("scripted.rec");
                else if (operation == "enable") session.EnableFlightRecording(1 << 20);
                else session.DisableFlightRecording();
                await client.PingAsync(TestContext.Current.CancellationToken);
            }, TestContext.Current.CancellationToken);
            await TestWait.UntilAsync(() => change.IsCompleted || mux.QueuedControlCount > 0 || mux.QueuedDataCount == 2, "the lifecycle request reached its queue");
            bool changedBeforeParsing = operationThread is not null;
            host.Fake(id).Emit("later");
            release.Set();
            Assert.True(await parking);
            await change;
            await mux.FlushAsync();
            Assert.False(changedBeforeParsing);
            Assert.Same(parseThread, operationThread);
            Assert.Equal("before", lineAtOperation);
            if (operation == "start")
                Assert.Equal("later", Encoding.UTF8.GetString(host.Fake(id).RecordedOutput.SelectMany(data => data).ToArray()));
            if (operation == "disable") Assert.False(host.Fake(id).IsFlightRecording);
            if (operation == "enable")
            {
                string path = Path.GetTempFileName();
                try
                {
                    Assert.True(await Task.Run(() => session.TryExportFlightRecording(path, out _), TestContext.Current.CancellationToken));
                    var bytes = new List<byte>();
                    await new ReplayRunner(path).RunAsync(data => { bytes.AddRange(data); return Task.CompletedTask; }, ct: TestContext.Current.CancellationToken);
                    Assert.Equal("later", Encoding.UTF8.GetString(bytes.ToArray()));
                }
                finally { File.Delete(path); }
            }
        }
        finally { release.Set(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_exited_session_can_still_export_its_retained_flight_recording(bool stopParser)
    {
        using var host = new MuxTestHost();
        using var client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        using MuxClientSession session = client.OpenSession(id, "scripted");
        session.EnableFlightRecording(1 << 20);
        await client.PingAsync(TestContext.Current.CancellationToken);
        host.Fake(id).Emit("last bytes");
        await host.Mux(id).FlushAsync();
        if (stopParser) host.Mux(id).Kill();
        else host.Fake(id).Exit(0);
        await host.Mux(id).FlushAsync();
        string path = Path.GetTempFileName();
        try
        {
            Assert.True(await Task.Run(() => session.TryExportFlightRecording(path, out _), TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            var replayed = new List<byte>();
            await new ReplayRunner(path).RunAsync(d => { replayed.AddRange(d); return Task.CompletedTask; }, ct: TestContext.Current.CancellationToken);
            Assert.Equal("last bytes", Encoding.UTF8.GetString(replayed.ToArray()));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Stopping_a_recording_waits_for_output_already_queued_to_the_parser()
    {
        using var host = new MuxTestHost();
        using var client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        using MuxClientSession session = client.OpenSession(id, "scripted");
        Thread? stopThread = null;
        string? lineAtStop = null;
        host.Fake(id).OnStopRecording = () =>
        {
            stopThread = Thread.CurrentThread;
            lineAtStop = BufferSnapshot.Capture(host.Mux(id).Buffer).Lines[1];
        };
        session.StartRecording("scripted.rec");
        await client.PingAsync(TestContext.Current.CancellationToken);
        var mux = host.Mux(id);
        await mux.FlushAsync();
        using var parked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Thread? parseThread = null;
        Task<bool> parking = mux.InvokeAsync(() => { parseThread = Thread.CurrentThread; parked.Set(); return release.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); });
        Assert.True(parked.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        try
        {
            host.Fake(id).Emit("hello\r\n");
            host.Fake(id).Emit("world");
            Task stop = client.RequestAsync(MuxMethods.StopRecording, new SessionIdParams { SessionId = id },
                MuxJsonContext.Default.SessionIdParams, MuxJsonContext.Default.MuxEmpty, TestContext.Current.CancellationToken);
            await TestWait.UntilAsync(() => stop.IsCompleted || mux.QueuedDataCount == 3, "stop either bypassed or queued behind output");
            bool stoppedBeforeParsing = stopThread is not null;
            host.Fake(id).Emit("later"); // queued after the stop boundary
            release.Set();
            Assert.True(await parking);
            await stop;
            Assert.False(stoppedBeforeParsing);
            Assert.Same(parseThread, stopThread);
            Assert.False(host.Fake(id).IsRecording);
            Assert.Equal("world", lineAtStop);
            await mux.FlushAsync();
            Assert.Equal("hello", Ntilde.Replay.BufferSnapshot.Capture(mux.Buffer).Lines[0]);
            Assert.Equal("worldlater", Ntilde.Replay.BufferSnapshot.Capture(mux.Buffer).Lines[1]);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task A_flight_export_crosses_the_wire_as_bytes_ReplayRunner_can_play()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        using MuxClientSession session = client.OpenSession(id, "scripted");

        Thread? exportThread = null;
        Thread? captureThread = null;
        using var writing = new ManualResetEventSlim();
        using var releaseWrite = new ManualResetEventSlim();
        host.Fake(id).OnFlightCapture = () => captureThread = Thread.CurrentThread;
        host.Fake(id).OnFlightExport = () =>
        {
            exportThread = Thread.CurrentThread;
            writing.Set();
            Assert.True(releaseWrite.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        };
        session.EnableFlightRecording(1 << 20);
        await client.PingAsync(TestContext.Current.CancellationToken);
        var mux = host.Mux(id);
        await mux.FlushAsync();
        using var parked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Thread? parseThread = null;
        Task<bool> parking = mux.InvokeAsync(() => { parseThread = Thread.CurrentThread; parked.Set(); return release.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); });
        Assert.True(parked.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        host.Fake(id).Emit("hello\r\n");
        host.Fake(id).Emit("world");

        string path = Path.Combine(Path.GetTempPath(), $"mux-flight-{Guid.NewGuid():N}.rec");
        try
        {
            Task<bool> export = Task.Run(() => session.TryExportFlightRecording(path, out _), TestContext.Current.CancellationToken);
            await TestWait.UntilAsync(() => export.IsCompleted || mux.QueuedDataCount == 3, "export either bypassed or queued behind the two output chunks");
            bool exportedBeforeParsing = export.IsCompleted;
            host.Fake(id).Emit("later"); // must remain outside this export even if parsing resumes quickly
            release.Set();
            Assert.True(await parking);
            Assert.True(writing.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            await mux.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal("worldlater", BufferSnapshot.Capture(mux.Buffer).Lines[1]);
            releaseWrite.Set();
            Assert.True(await export);
            Assert.False(exportedBeforeParsing);
            Assert.Same(parseThread, captureThread);
            Assert.NotSame(parseThread, exportThread);

            var replayed = new List<byte>();
            await new ReplayRunner(path).RunAsync(d => { replayed.AddRange(d); return Task.CompletedTask; }, ct: TestContext.Current.CancellationToken);
            Assert.Equal("hello\r\nworld", Encoding.UTF8.GetString(replayed.ToArray()));
        }
        finally
        {
            release.Set();
            releaseWrite.Set();
            File.Delete(path);
        }
    }
}
