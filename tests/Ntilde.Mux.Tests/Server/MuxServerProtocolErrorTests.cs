using System.Text;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Mux.Transport;

namespace Ntilde.Mux.Tests.Server;

public sealed class MuxServerProtocolErrorTests
{
    private static async Task<RawMuxConnection> ReadyAsync(MuxTestHost host)
    {
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();
        return raw;
    }

    private static async Task AssertClosedWithAsync(RawMuxConnection raw, string code)
    {
        MuxResponse response = await raw.ReadResponseAsync();
        Assert.Equal(0, response.Id);
        Assert.Equal(code, response.Error?.Code);
        Assert.True(await raw.IsClosedByPeerAsync());
    }

    [Fact]
    public async Task An_oversize_frame_header_is_refused_with_frame_too_large()
    {
        using var host = new MuxTestHost(new MuxServerOptions { MaxInboundFrameBytes = 1024, ForceConPtyFiltering = false });
        RawMuxConnection raw = await ReadyAsync(host);
        byte[] header = new byte[MuxProtocol.FrameHeaderBytes];
        MuxFrameCodec.WriteHeader(header, MuxFrameKind.Request, 1025);

        raw.WriteRaw(header);

        await AssertClosedWithAsync(raw, MuxErrorCodes.FrameTooLarge);
    }

    [Fact]
    public async Task Malformed_json_closes_the_connection_without_touching_any_buffer()
    {
        using var host = new MuxTestHost();
        RawMuxConnection spawner = await ReadyAsync(host);
        spawner.Request(MuxMethods.Spawn, new SpawnParams { Command = "scripted" }, MuxJsonContext.Default.SpawnParams);
        Guid id = MuxFrames.ParseParams((await spawner.ReadResponseAsync()).Result, MuxJsonContext.Default.SpawnResult).SessionId;
        host.Fake(id).Emit("before");
        await host.Mux(id).FlushAsync();
        string before = await host.Mux(id).InvokeAsync(() => host.Mux(id).CaptureSnapshot(100).Main.CellsBase64!);

        RawMuxConnection raw = await ReadyAsync(host);
        byte[] junk = Encoding.UTF8.GetBytes("{nope");
        byte[] header = new byte[MuxProtocol.FrameHeaderBytes];
        MuxFrameCodec.WriteHeader(header, MuxFrameKind.Request, junk.Length);
        raw.WriteRaw(header);
        raw.WriteRaw(junk);

        await AssertClosedWithAsync(raw, MuxErrorCodes.ProtocolError);
        string after = await host.Mux(id).InvokeAsync(() => host.Mux(id).CaptureSnapshot(100).Main.CellsBase64!);
        Assert.Equal(before, after);
        Assert.Equal(6, host.Mux(id).StreamPosition);
    }

    [Fact]
    public async Task A_missing_required_member_closes_the_connection()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = await ReadyAsync(host);
        raw.Send(MuxFrames.Request(new MuxRequest
        {
            Id = 5,
            Method = MuxMethods.Attach,
            Params = MuxFrames.ToElement(new SessionIdParams { SessionId = Guid.NewGuid() }, MuxJsonContext.Default.SessionIdParams),
        }));

        await AssertClosedWithAsync(raw, MuxErrorCodes.ProtocolError);
    }

    [Fact]
    public async Task An_unknown_frame_kind_closes_the_connection()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = await ReadyAsync(host);

        raw.WriteRaw([0x7F, 0, 0, 0, 0]);

        await AssertClosedWithAsync(raw, MuxErrorCodes.ProtocolError);
    }

    [Fact]
    public async Task A_client_may_not_send_Output()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = await ReadyAsync(host);

        raw.Send(MuxFrames.Output(Guid.NewGuid(), 0, "x"u8));

        await AssertClosedWithAsync(raw, MuxErrorCodes.ProtocolError);
    }

    [Fact]
    public async Task A_truncated_Input_closes_the_connection()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = await ReadyAsync(host);

        raw.WriteRaw([(byte)MuxFrameKind.Input, 3, 0, 0, 0, 1, 2, 3]);

        await AssertClosedWithAsync(raw, MuxErrorCodes.ProtocolError);
    }

    [Fact]
    public async Task An_oversize_method_string_is_clipped_and_does_not_crash_the_server()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw(); // no hello yet: the huge method is the first frame

        // System.Text.Json's default encoder HTML-escapes '<' to the 6-byte "<", so an 11 MiB
        // run of it echoed unclipped into an error message would build a response over the 64 MiB
        // frame limit. MuxOutboundFrame.Rent threw FrameTooLarge for that from inside ReadLoop's own
        // catch clause, which escaped the thread and crashed the process - the bug this test guards.
        const int hugeLength = 11 * 1024 * 1024;
        byte[] prefix = Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"");
        byte[] suffix = Encoding.UTF8.GetBytes("\"}");
        byte[] payload = new byte[prefix.Length + hugeLength + suffix.Length];
        prefix.CopyTo(payload, 0);
        Array.Fill(payload, (byte)'<', prefix.Length, hugeLength);
        suffix.CopyTo(payload, prefix.Length + hugeLength);

        byte[] header = new byte[MuxProtocol.FrameHeaderBytes];
        MuxFrameCodec.WriteHeader(header, MuxFrameKind.Request, payload.Length);
        raw.WriteRaw(header);
        raw.WriteRaw(payload);

        await AssertClosedWithAsync(raw, MuxErrorCodes.ProtocolError);

        // The process (and this server) must still be alive and serving other connections.
        using RawMuxConnection second = host.ConnectRaw();
        WelcomeResult welcome = await second.HelloAsync();
        Assert.Equal(1, welcome.Version);
    }

    [Fact]
    public async Task Disposing_the_server_aborts_a_connection_still_writing_a_final_frame()
    {
        // A real MuxServer (not MuxTestHost's default 1 MiB pipe) so the connection is tracked in
        // the server's own registry the way MuxServer.Dispose() looks it up, over a 4 KiB pipe.
        var factory = new ScriptedSessionFactory();
        using var server = new MuxServer(factory, new MuxServerOptions { ForceConPtyFiltering = false });
        (Stream clientEnd, Stream serverEnd) = InMemoryDuplexPipe.Create(4096);
        using var raw = new RawMuxConnection(clientEnd);
        server.AcceptConnection(serverEnd);
        await raw.HelloAsync();

        // Flood replies the test never reads: 300 pings' worth of small Response frames is far more
        // than the 4 KiB pipe holds, so the sender thread ends up blocked mid Stream.Write on one of
        // them once nothing drains the pipe from here on.
        for (int i = 0; i < 300; i++)
        {
            raw.Request(MuxMethods.Ping, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty);
        }

        // An unknown frame kind is a protocol error: the reader thread queues a final error frame
        // and exits immediately, while the sender thread is (very likely) still stuck writing an
        // earlier, unrelated reply to a peer that stopped reading entirely. There is no session to
        // poll for "the reader has definitely exited" here, so this settling delay is the one place
        // this test is not fully deterministic - see the task report for why that is acceptable.
        raw.WriteRaw([0x7F, 0, 0, 0, 0]);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        server.Dispose();

        // Probe by writing, not reading: a read would itself drain the pipe and could unblock the
        // stuck sender on its own, masking the bug this test exists to catch. Without the fix, the
        // reader thread's exit already told MuxServer the connection was closed, so Dispose's abort
        // loop never reaches this connection - its stream (and the client's) never closes, and this
        // probe write keeps succeeding for the full timeout instead of observing the server's read
        // side close.
        Assert.True(await ServerSideClosedAsync(raw, TimeSpan.FromSeconds(5)));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), "the client must see the connection close promptly");
    }

    /// <summary>
    /// True once a write from the client throws: the server disposed its end, closing its read side
    /// of the client-to-server pipe. Probing by writing (not reading) avoids the probe itself
    /// draining the outbound pipe and unblocking a stuck sender for the wrong reason.
    /// </summary>
    private static async Task<bool> ServerSideClosedAsync(RawMuxConnection raw, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                raw.WriteRaw([0, 0, 0, 0, 0]);
            }
            catch (IOException)
            {
                return true;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return false;
    }
}
