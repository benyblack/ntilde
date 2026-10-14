using System.Text;
using Ntilde.Mux.Tests.Support;
using Ntilde.Replay;

namespace Ntilde.Mux.Tests.Scenarios;

public sealed class FlightExportTests
{
    [Fact]
    public async Task A_flight_export_crosses_the_wire_as_bytes_ReplayRunner_can_play()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        using MuxClientSession session = client.OpenSession(id, "scripted");

        session.EnableFlightRecording(1 << 20);
        await client.PingAsync(TestContext.Current.CancellationToken);
        host.Fake(id).Emit("hello\r\n");
        host.Fake(id).Emit("world");

        string path = Path.Combine(Path.GetTempPath(), $"mux-flight-{Guid.NewGuid():N}.rec");
        try
        {
            Assert.True(await Task.Run(() => session.TryExportFlightRecording(path, out _), TestContext.Current.CancellationToken));
            var replayed = new List<byte>();
            await new ReplayRunner(path).RunAsync(d => { replayed.AddRange(d); return Task.CompletedTask; }, ct: TestContext.Current.CancellationToken);
            Assert.Equal("hello\r\nworld", Encoding.UTF8.GetString(replayed.ToArray()));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
