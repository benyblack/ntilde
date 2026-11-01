using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Server;

public sealed class MuxHistoryPolicyValidationTests
{
    [Theory]
    [InlineData("spawn")]
    [InlineData("attach")]
    [InlineData("resize")]
    public async Task NegativeRetentionRequestsAreRefusedBeforeSessionMutation(string method)
    {
        using var host = new MuxTestHost();
        using var client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        using var raw = host.ConnectRaw();
        await raw.HelloAsync();
        var presentation = MuxTestHost.DefaultPresentation with { MaxHistory = -1 };
        if (method == "spawn") raw.Request(MuxMethods.Spawn, new SpawnParams { Command = "scripted", MaxHistory = -1 }, MuxJsonContext.Default.SpawnParams);
        else if (method == "attach") raw.Request(MuxMethods.Attach, new AttachParams { SessionId = id, Presentation = presentation }, MuxJsonContext.Default.AttachParams);
        else raw.Request(MuxMethods.Resize, new ResizeParams { SessionId = id, Cols = 81, Rows = 24, Presentation = presentation }, MuxJsonContext.Default.ResizeParams);
        Assert.Equal(MuxErrorCodes.ProtocolError, (await raw.ReadResponseAsync()).Error?.Code);
        await host.Mux(id).FlushAsync();
        Assert.Single(host.Factory.Requests);
        Assert.Equal(80, host.Mux(id).Buffer.Cols);
        Assert.Equal(10_000, host.Mux(id).HistoryRetentionRows);
        Assert.Equal(0, host.Mux(id).AttachedClients);
        raw.Request(MuxMethods.Ping, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty);
        Assert.Null((await raw.ReadResponseAsync()).Error);
    }

    [Fact]
    public async Task RetentionDoesNotRaiseTheSnapshotFrameLimit()
    {
        using var host = new MuxTestHost(new MuxServerOptions { MaxSnapshotBytes = 1 });
        using var client = await host.ConnectClientAsync();
        Guid id = await client.SpawnAsync(new SpawnParams { Command = "scripted", MaxHistory = 100_000 }, TestContext.Current.CancellationToken);
        using var raw = host.ConnectRaw();
        await raw.HelloAsync();
        raw.Request(MuxMethods.Attach, new AttachParams { SessionId = id, MaxScrollbackRows = 50_000, Presentation = MuxTestHost.DefaultPresentation with { MaxHistory = 100_000 } }, MuxJsonContext.Default.AttachParams);
        Assert.Equal(MuxErrorCodes.SnapshotTooLarge, (await raw.ReadResponseAsync()).Error?.Code);
        Assert.Equal(100_000, host.Mux(id).HistoryRetentionRows);
        Assert.Equal(0, host.Mux(id).AttachedClients);
    }
}
