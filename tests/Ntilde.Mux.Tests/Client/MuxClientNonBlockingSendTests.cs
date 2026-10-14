using System.Collections.Concurrent;
using System.Text;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Client;

/// <summary>
/// Phase 5, ruling R6: no send on a <see cref="MuxClient"/> blocks its caller, however long the link stalls. The UI
/// thread sends input, resizes, detaches and kills; a full send queue used to hold it until the link drained or was
/// dropped. Frames still reach the wire in the order of their calls, and a link that holds more than
/// <see cref="MuxClientOptions.MaxOverflowBytes"/> is given up.
/// </summary>
public sealed class MuxClientNonBlockingSendTests
{
    private static readonly TimeSpan Patient = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A client over <paramref name="daemon"/>, which answers the hello (v2) and then stops reading, with the client's
    /// send queue full behind it (<see cref="FullSendQueue"/>).
    /// </summary>
    private static async Task<MuxClient> ConnectOverAStalledLinkAsync(FakeMuxServerEnd daemon, MuxClientOptions? options = null)
    {
        Task<MuxClient> connect = MuxClient.ConnectAsync(daemon.ClientEnd, options, Ct);
        await daemon.AcceptHelloAsync(MuxProtocol.SessionEventsVersion);
        MuxClient client = await connect.WaitAsync(Patient, Ct);
        await FullSendQueue.FillAsync(client);
        return client;
    }

    [Fact]
    public async Task Every_send_returns_promptly_while_the_outbound_queue_is_full()
    {
        using var daemon = FakeMuxServerEnd.Create(FullSendQueue.PipeCapacityBytes);
        using MuxClient client = await ConnectOverAStalledLinkAsync(daemon);
        MuxClientSession session = client.OpenSession(Guid.NewGuid());
        Task? kill = null;

        Assert.True(await FullSendQueue.ReturnsPromptlyAsync(() => { session.SendInput("x"); return true; }), "SendInput waited on the stalled link");
        Assert.True(await FullSendQueue.ReturnsPromptlyAsync(() => { session.Resize(100, 30); return true; }), "Resize waited on the stalled link");
        Assert.True(await FullSendQueue.ReturnsPromptlyAsync(() => { session.Detach(userDetached: true); return true; }), "Detach waited on the stalled link");
        Assert.True(await FullSendQueue.ReturnsPromptlyAsync(() => { session.Kill(); return true; }), "Kill waited on the stalled link");
        // A request: only the call has to return at once. Its task completes when the daemon answers, which it does not.
        Assert.True(await FullSendQueue.ReturnsPromptlyAsync(() => { kill = client.KillAsync(Guid.NewGuid(), Ct); return true; }), "KillAsync waited on the stalled link");

        Assert.False(kill!.IsCompleted);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task Frames_reach_the_wire_in_call_order_after_a_stall()
    {
        using var daemon = FakeMuxServerEnd.Create(FullSendQueue.PipeCapacityBytes);
        using MuxClient client = await ConnectOverAStalledLinkAsync(daemon);
        Guid s1 = Guid.NewGuid();
        Guid s2 = Guid.NewGuid();
        MuxClientSession session = client.OpenSession(s1);

        // Posts and a request, interleaved: every one of them waits behind the stall, in one line.
        Assert.True(await FullSendQueue.ReturnsPromptlyAsync(() =>
        {
            session.SendInput("a");
            _ = client.KillAsync(s2, Ct);
            session.SendInput("b");
            session.Resize(100, 30);
            session.Detach(userDetached: true);
            return true;
        }), "a send waited on the stalled link");

        List<string> wire = await Task.Run(() => ReadUntilDetach(daemon), Ct).WaitAsync(Patient, Ct);

        // The filler first, all of it (one large frame, then a queue's worth), then the five frames as they were called.
        int filler = 1 + MuxClient.OutboundCapacity;
        Assert.All(wire.Take(filler), line => Assert.Equal("filler", line));
        Assert.Equal(
            [$"input {s1} a", $"request kill {s2}", $"input {s1} b", $"request resize {s1} 100x30", $"request detach {s1} user"],
            wire.Skip(filler));
    }

    [Fact]
    public async Task A_send_overflow_past_the_cap_disconnects_the_client()
    {
        using var daemon = FakeMuxServerEnd.Create(FullSendQueue.PipeCapacityBytes);
        var log = new ConcurrentQueue<string>();
        using MuxClient client = await ConnectOverAStalledLinkAsync(daemon, new MuxClientOptions { MaxOverflowBytes = 64 * 1024, Log = log.Enqueue });
        Guid id = Guid.NewGuid();
        string kib = new('k', 1024);
        Task? pending = null;

        // A request queued behind the stall, then 100 KiB of input: more than the link may hold.
        Assert.True(await FullSendQueue.ReturnsPromptlyAsync(() =>
        {
            pending = client.PingAsync(Ct);
            for (int i = 0; i < 100; i++) client.SendInput(id, kib);
            return true;
        }), "a send waited on the stalled link");

        await TestWait.UntilAsync(() => !client.IsConnected, "the overflow dropped the connection", TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<IOException>(() => pending!.WaitAsync(Patient, Ct));
        Assert.Equal("send overflow", client.DisconnectReason);
        Assert.Contains(log, line => line.Contains("outbound overflow past 65536 bytes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sends_after_disconnect_are_dropped_without_throwing()
    {
        using var daemon = FakeMuxServerEnd.Create();
        Task<MuxClient> connect = MuxClient.ConnectAsync(daemon.ClientEnd, null, Ct);
        await daemon.AcceptHelloAsync(MuxProtocol.SessionEventsVersion);
        using MuxClient client = await connect.WaitAsync(Patient, Ct);
        MuxClientSession session = client.OpenSession(Guid.NewGuid());

        daemon.Raw.Dispose();   // the daemon goes away
        await TestWait.UntilAsync(() => !client.IsConnected, "the client saw the daemon go");

        session.SendInput("x");
        session.Resize(100, 30);
        session.Kill();
        session.Detach(userDetached: true);
        await Assert.ThrowsAsync<IOException>(() => client.KillAsync(Guid.NewGuid(), Ct));
    }

    /// <summary>
    /// The daemon reads again: every frame up to and including the first detach request, one line each. The input
    /// <see cref="FullSendQueue.FillAsync"/> sends (session <see cref="Guid.Empty"/>) reads as <c>filler</c>.
    /// </summary>
    private static List<string> ReadUntilDetach(FakeMuxServerEnd daemon)
    {
        var wire = new List<string>();
        while (true)
        {
            using MuxInboundFrame frame = MuxFrameReader.Read(daemon.Raw.Stream) ?? throw new EndOfStreamException("The client closed before it sent a detach.");
            switch (frame.Kind)
            {
                case MuxFrameKind.Input:
                    Assert.True(MuxFrames.TryParseInput(frame.Payload, out Guid id, out ReadOnlySpan<byte> text));
                    wire.Add(id == Guid.Empty ? "filler" : $"input {id} {Encoding.UTF8.GetString(text)}");
                    break;
                case MuxFrameKind.Request:
                    MuxRequest request = MuxFrames.ParseJson(frame.Payload, MuxJsonContext.Default.MuxRequest);
                    wire.Add(Describe(request));
                    if (request.Method == MuxMethods.Detach) return wire;
                    break;
                default:
                    wire.Add(frame.Kind.ToString());
                    break;
            }
        }
    }

    private static string Describe(MuxRequest request)
    {
        switch (request.Method)
        {
            case MuxMethods.Kill:
                return $"request kill {MuxFrames.ParseParams(request.Params, MuxJsonContext.Default.SessionIdParams).SessionId}";
            case MuxMethods.Resize:
                ResizeParams resize = MuxFrames.ParseParams(request.Params, MuxJsonContext.Default.ResizeParams);
                return $"request resize {resize.SessionId} {resize.Cols}x{resize.Rows}";
            case MuxMethods.Detach:
                DetachParams detach = MuxFrames.ParseParams(request.Params, MuxJsonContext.Default.DetachParams);
                return $"request detach {detach.SessionId}{(detach.UserDetached == true ? " user" : string.Empty)}";
            default:
                return $"request {request.Method}";
        }
    }
}
