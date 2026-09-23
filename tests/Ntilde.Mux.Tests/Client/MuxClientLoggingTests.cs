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
}
