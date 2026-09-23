using System.Text;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

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
}
