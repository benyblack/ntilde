using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Client;

public sealed class MuxClientLoggingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The reader thread logs from inside its catch clauses. A host logger that throws there
    /// (disk full, closed sink) would escape the thread's entry point and take the whole process
    /// down, instead of just disconnecting this one client.
    /// </summary>
    [Fact]
    public async Task A_throwing_logger_cannot_turn_a_protocol_error_into_a_process_crash()
    {
        int logCalls = 0;
        using var fake = FakeMuxServerEnd.Create();
        Task<MuxClient> connect = MuxClient.ConnectAsync(fake.ClientEnd, new MuxClientOptions
        {
            Log = _ =>
            {
                Interlocked.Increment(ref logCalls);
                throw new IOException("the log sink is full");
            },
        }, Ct);
        await fake.AcceptHelloAsync();
        using MuxClient client = await connect;

        // An Output frame shorter than its fixed header: a protocol error the reader must log.
        fake.Raw.WriteRaw([(byte)MuxFrameKind.Output, 3, 0, 0, 0, 1, 2, 3]);

        await TestWait.UntilAsync(() => !client.IsConnected, "the client drops the connection");
        Assert.Equal(MuxErrorCodes.ProtocolError, client.DisconnectReason);
        Assert.True(Volatile.Read(ref logCalls) >= 1, "the logger was reached (and threw)");
    }

    /// <summary>
    /// Disconnected handlers are host code, raised from whichever thread ends the connection - the
    /// reader, the sender (a failed write) or the Dispose caller. One that throws must neither
    /// escape that thread (a crash on the sender) nor stop the others being told.
    /// </summary>
    [Fact]
    public async Task A_throwing_Disconnected_handler_does_not_stop_the_others_or_escape()
    {
        using var fake = FakeMuxServerEnd.Create();
        Task<MuxClient> connect = MuxClient.ConnectAsync(fake.ClientEnd, new MuxClientOptions(), Ct);
        await fake.AcceptHelloAsync();
        MuxClient client = await connect;
        MuxClientSession first = client.OpenSession(Guid.NewGuid());
        MuxClientSession second = client.OpenSession(Guid.NewGuid());
        var told = new List<string>();
        first.Disconnected += _ => { lock (told) told.Add("first"); throw new InvalidOperationException("session handler bug"); };
        second.Disconnected += _ => { lock (told) told.Add("second"); throw new InvalidOperationException("session handler bug"); };
        client.Disconnected += _ => { lock (told) told.Add("client"); throw new InvalidOperationException("client handler bug"); };

        Exception? escaped = Record.Exception(client.Dispose); // Dispose runs OnDisconnected on this thread

        Assert.Null(escaped);
        lock (told)
        {
            Assert.Equal(["client", "first", "second"], told.Order(StringComparer.Ordinal).ToArray());
        }
    }

    [Fact]
    public async Task TryExportFlightRecording_returns_false_for_a_path_it_cannot_write()
    {
        // The interface is try-style and the Rust/native-SSH implementations return false on path
        // failures; the mux client must not be the one that throws at its caller instead.
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        using MuxClientSession session = client.OpenSession(id, "scripted");
        session.EnableFlightRecording(1 << 20);
        await client.PingAsync(Ct);
        host.Fake(id).Emit("some output");

        string unwritable = "bad\0path.rec"; // an embedded NUL is refused by the file APIs on every OS
        bool exported = await Task.Run(() => session.TryExportFlightRecording(unwritable, out _), Ct);

        Assert.False(exported);
    }
}
