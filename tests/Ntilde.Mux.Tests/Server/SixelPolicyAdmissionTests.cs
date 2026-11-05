using System.Text.Json;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Server;

public sealed class SixelPolicyAdmissionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public async Task Future_peer_ceiling_negotiates_only_actual_supported_legacy(int offered)
    {
        using var host = new MuxTestHost();
        var raw = host.ConnectRaw();
        raw.Request(MuxMethods.Hello, new HelloParams { MinVersion = 1, MaxVersion = 2, MaxSixelRasterPolicy = offered }, MuxJsonContext.Default.HelloParams);
        var reply = await raw.ReadResponseAsync();
        Assert.Null(reply.Error);
        var welcome = MuxFrames.ParseParams(reply.Result, MuxJsonContext.Default.WelcomeResult);
        Assert.Equal(0, welcome.MaxSixelRasterPolicy);
        Assert.DoesNotContain("maxSixelRasterPolicy", reply.Result!.Value.GetRawText());
    }

    [Fact]
    public async Task Negative_peer_ceiling_is_refused_before_welcome()
    {
        using var host = new MuxTestHost();
        var raw = host.ConnectRaw();
        raw.Request(MuxMethods.Hello, new HelloParams { MinVersion = 1, MaxVersion = 2, MaxSixelRasterPolicy = -1 }, MuxJsonContext.Default.HelloParams);
        Assert.Equal(MuxErrorCodes.ProtocolError, (await raw.ReadResponseAsync()).Error?.Code);
        Assert.True(await raw.IsClosedByPeerAsync());
        Assert.Empty(host.Factory.Requests);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"1\"")]
    [InlineData("2147483648")]
    [InlineData("0.5")]
    [InlineData("1,\"requiredSixelRasterPolicy\":0")]
    public async Task Malformed_or_duplicate_requirement_is_refused_before_factory(string value)
    {
        using var host = new MuxTestHost();
        var raw = host.ConnectRaw();
        await raw.HelloAsync();
        Send(raw, MuxMethods.Spawn, $"{{\"command\":\"scripted\",\"cols\":80,\"rows\":24,\"requiredSixelRasterPolicy\":{value}}}");
        Assert.Equal(MuxErrorCodes.ProtocolError, (await raw.ReadResponseAsync()).Error?.Code);
        Assert.Empty(host.Factory.Requests);
    }

    [Fact]
    public async Task Refused_reattach_preserves_existing_legacy_subscription_and_geometry()
    {
        using var host = new MuxTestHost();
        var raw = host.ConnectRaw();
        await raw.HelloAsync();
        Guid id = host.Server.Spawn(new SpawnParams { Command = "scripted" });
        raw.Request(MuxMethods.Attach, new AttachParams { SessionId = id, Presentation = MuxTestHost.DefaultPresentation }, MuxJsonContext.Default.AttachParams);
        using (var initial = await raw.ReadAsync())
        {
            Assert.NotNull(initial);
            Assert.Equal(MuxFrameKind.Snapshot, initial.Kind);
        }
        await host.Mux(id).FlushAsync();
        Assert.Equal(1, host.Mux(id).AttachedClients);
        Send(raw, MuxMethods.Attach, $"{{\"sessionId\":\"{id}\",\"requiredSixelRasterPolicy\":1,\"presentation\":{{\"cols\":30,\"rows\":8}}}}");
        Assert.Equal(MuxErrorCodes.VersionMismatch, (await raw.ReadResponseAsync()).Error?.Code);
        host.Fake(id).Emit("still subscribed");
        await host.Mux(id).FlushAsync();
        using var output = await raw.ReadAsync();
        Assert.NotNull(output);
        Assert.Equal(MuxFrameKind.Output, output.Kind);
        Assert.Equal(1, host.Mux(id).AttachedClients);
        Assert.Equal((80, 24), (host.Mux(id).Buffer.Cols, host.Mux(id).Buffer.Rows));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public async Task Required_spawn_policy_is_refused_before_factory_creation(int policy)
    {
        using var host = new MuxTestHost();
        var raw = host.ConnectRaw();
        await raw.HelloAsync();
        Send(raw, MuxMethods.Spawn, $"{{\"command\":\"scripted\",\"cols\":80,\"rows\":24,\"requiredSixelRasterPolicy\":{policy}}}");
        var response = await raw.ReadResponseAsync();
        Assert.True(response.Error?.Code == MuxErrorCodes.VersionMismatch, response.Error?.Message ?? "no error");
        Assert.Empty(host.Factory.Requests);
    }

    [Theory]
    [InlineData(MuxMethods.Attach)]
    [InlineData(MuxMethods.ReadScreen)]
    public async Task Required_policy_is_refused_before_subscription_geometry_or_snapshot(string method)
    {
        using var host = new MuxTestHost();
        var raw = host.ConnectRaw();
        await raw.HelloAsync();
        Guid id = host.Server.Spawn(new SpawnParams { Command = "scripted" });
        string presentation = method == MuxMethods.Attach ? ",\"presentation\":{\"cols\":30,\"rows\":8}" : "";
        Send(raw, method, $"{{\"sessionId\":\"{id}\",\"requiredSixelRasterPolicy\":1{presentation}}}");
        using var reply = await raw.ReadAsync();
        Assert.NotNull(reply);
        Assert.Equal(MuxFrameKind.Response, reply.Kind);
        Assert.Equal(MuxErrorCodes.VersionMismatch, MuxFrames.ParseJson(reply.Payload, MuxJsonContext.Default.MuxResponse).Error?.Code);
        await host.Mux(id).FlushAsync();
        Assert.Equal(0, host.Mux(id).AttachedClients);
        Assert.Equal((80, 24), (host.Mux(id).Buffer.Cols, host.Mux(id).Buffer.Rows));
    }

    private static void Send(RawMuxConnection raw, string method, string json)
    {
        using var doc = JsonDocument.Parse(json);
        raw.Send(MuxFrames.Request(new MuxRequest { Id = 20, Method = method, Params = doc.RootElement.Clone() }));
    }
}
