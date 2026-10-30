using System.Text;
using Ntilde.Mux.Tests.Support;
using Ntilde.Replay;

namespace Ntilde.Mux.Tests.Scenarios;

public sealed class FlightExportTests
{
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
            Task stop = Task.Run(async () =>
            {
                session.StopRecording();
                await client.PingAsync(TestContext.Current.CancellationToken);
            }, TestContext.Current.CancellationToken);
            await TestWait.UntilAsync(() => stop.IsCompleted || mux.QueuedDataCount == 3, "stop either bypassed or queued behind output");
            bool stoppedBeforeParsing = stop.IsCompleted;
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
        host.Fake(id).OnFlightExport = () => exportThread = Thread.CurrentThread;
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
            Assert.True(await export);
            Assert.False(exportedBeforeParsing);
            Assert.Same(parseThread, exportThread);
            await mux.FlushAsync();
            Assert.Equal("worldlater", BufferSnapshot.Capture(mux.Buffer).Lines[1]);

            var replayed = new List<byte>();
            await new ReplayRunner(path).RunAsync(d => { replayed.AddRange(d); return Task.CompletedTask; }, ct: TestContext.Current.CancellationToken);
            Assert.Equal("hello\r\nworld", Encoding.UTF8.GetString(replayed.ToArray()));
        }
        finally
        {
            release.Set();
            File.Delete(path);
        }
    }
}
