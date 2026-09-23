using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Scenarios;

public sealed class AttachUnderLoadTests
{
    [Fact]
    public async Task Attaching_a_second_session_while_the_first_has_output_queued_keeps_the_connection()
    {
        // One GUI connection, several sessions: at start-up the panes attach while earlier panes'
        // output is still queued. A snapshot bigger than the remaining (or the whole) stream budget
        // must not disconnect the GUI as client_too_slow.
        const long budget = 64 * 1024;
        using var host = new MuxTestHost(new MuxServerOptions { ClientSendBudgetBytes = budget, ForceConPtyFiltering = false }, pipeCapacityBytes: 4 * 1024);
        MuxClient spawner = await host.ConnectClientAsync();
        Guid a = await MuxTestHost.SpawnAsync(spawner);
        Guid b = await MuxTestHost.SpawnAsync(spawner);
        for (int i = 0; i < 500; i++) host.Fake(b).Emit($"history line {i:D4} ........................................\r\n");
        await host.Mux(b).FlushAsync();

        RawMuxConnection gui = host.ConnectRaw();
        await gui.HelloAsync();
        gui.Request(MuxMethods.Attach, new AttachParams { SessionId = a, MaxScrollbackRows = 0, Presentation = MuxTestHost.DefaultPresentation },
            MuxJsonContext.Default.AttachParams);
        using (MuxInboundFrame? snapshotA = await gui.ReadAsync()) Assert.Equal(MuxFrameKind.Snapshot, snapshotA?.Kind);

        // The GUI is busy: 40 KiB of A's output piles up in its server-side queue (the pipe holds 4 KiB).
        byte[] chunk = new byte[1024];
        Array.Fill(chunk, (byte)'a');
        for (int i = 0; i < 40; i++) host.Fake(a).Emit(chunk);
        await host.Mux(a).FlushAsync();

        long attachB = gui.Request(MuxMethods.Attach, new AttachParams { SessionId = b, MaxScrollbackRows = 1_000, Presentation = MuxTestHost.DefaultPresentation },
            MuxJsonContext.Default.AttachParams);

        // Still not reading: let the parse thread offer B's snapshot while A's output is queued.
        // Either it is accepted (B gains a subscriber) or the connection is dropped.
        await TestWait.UntilAsync(() => host.Mux(b).AttachedClients == 1 || host.Server.ConnectionCount < 2,
            "the server has decided on B's snapshot");

        // Now it reads again: A's output, then B's snapshot, and the connection is still there.
        long outputBytes = 0;
        int snapshotBytes = -1;
        while (snapshotBytes < 0)
        {
            using MuxInboundFrame? frame = await gui.ReadAsync();
            Assert.True(frame is not null, "the server closed the connection (client_too_slow)");
            if (frame.Kind == MuxFrameKind.Output)
            {
                outputBytes += frame.Payload.Length - MuxFrames.OutputHeaderBytes;
            }
            else
            {
                Assert.Equal(MuxFrameKind.Snapshot, frame.Kind);
                Assert.True(MuxFrames.TryParseSnapshot(frame.Payload, out long requestId, out Guid sessionId, out _, out ReadOnlySpan<byte> json));
                Assert.Equal((attachB, b), (requestId, sessionId));
                snapshotBytes = json.Length;
            }
        }

        Assert.Equal(40 * 1024, outputBytes);
        Assert.True(snapshotBytes > budget, $"the test needs B's snapshot ({snapshotBytes} bytes) to exceed the whole {budget}-byte budget");
        gui.Request(MuxMethods.Ping, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty);
        Assert.Null((await gui.ReadResponseAsync()).Error);
        Assert.Equal(1, host.Mux(b).AttachedClients);
    }
}
