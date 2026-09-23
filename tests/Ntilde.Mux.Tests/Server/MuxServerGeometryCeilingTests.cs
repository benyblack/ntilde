using System.Text.Json.Serialization.Metadata;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Server;

/// <summary>
/// Peer-controlled geometry reaches <c>TerminalBuffer</c>, which allocates eagerly, so the server
/// refuses anything over <see cref="MuxServerOptions.MaxCells"/> / <see cref="MuxServerOptions.MaxDimension"/>
/// as a request error - before it can OOM the daemon or be broadcast to clients that would drop
/// their connection over it.
/// </summary>
public sealed class MuxServerGeometryCeilingTests
{
    private static async Task<MuxResponse> CallAsync<T>(RawMuxConnection raw, string method, T p, JsonTypeInfo<T> info)
    {
        long id = raw.Request(method, p, info);
        MuxResponse response = await raw.ReadResponseAsync();
        Assert.Equal(id, response.Id);
        return response;
    }

    private static async Task AssertStillOpenAsync(RawMuxConnection raw) =>
        Assert.Null((await CallAsync(raw, MuxMethods.Ping, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty)).Error);

    [Theory]
    [InlineData(50_000, 50_000)] // 2.5e9 cells: tens of GB if it ever reached the buffer
    [InlineData(2_000, 600)]     // every dimension is fine, the product (1.2M cells) is not
    [InlineData(12_000, 2)]      // few cells, one dimension over
    public async Task A_resize_over_the_ceiling_is_refused_and_nobody_is_disconnected(int cols, int rows)
    {
        using var host = new MuxTestHost();
        MuxClient watcher = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(watcher);
        ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(watcher, id);
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();

        MuxResponse r = await CallAsync(raw, MuxMethods.Resize, new ResizeParams { SessionId = id, Cols = cols, Rows = rows }, MuxJsonContext.Default.ResizeParams);

        Assert.Equal(MuxErrorCodes.ProtocolError, r.Error?.Code);
        await AssertStillOpenAsync(raw);
        host.Fake(id).Emit("after");
        await host.SettleAsync(id, watcher);
        Assert.Equal((80, 24), (host.Mux(id).Cols, host.Mux(id).Rows));
        Assert.Empty(host.Fake(id).Resizes);
        Assert.True(watcher.IsConnected, watcher.DisconnectReason);
        Assert.DoesNotContain(pane.Events, e => e.StartsWith("resize:", StringComparison.Ordinal));
        host.AssertPaneMatchesMux(id, pane, "the other client");
    }

    [Fact]
    public async Task A_resize_whose_presentation_is_over_the_ceiling_is_refused()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);

        MuxResponse r = await CallAsync(raw, MuxMethods.Resize, new ResizeParams
        {
            SessionId = id,
            Cols = 80,
            Rows = 24,
            Presentation = MuxTestHost.DefaultPresentation with { Cols = 2_000, Rows = 600 },
        }, MuxJsonContext.Default.ResizeParams);

        Assert.Equal(MuxErrorCodes.ProtocolError, r.Error?.Code);
        await AssertStillOpenAsync(raw);
    }

    [Fact]
    public async Task An_attach_whose_presentation_is_over_the_ceiling_is_refused_and_not_subscribed()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();

        MuxResponse r = await CallAsync(raw, MuxMethods.Attach, new AttachParams
        {
            SessionId = id,
            MaxScrollbackRows = 0,
            Presentation = MuxTestHost.DefaultPresentation with { Cols = 2_000, Rows = 600 },
        }, MuxJsonContext.Default.AttachParams);

        Assert.Equal(MuxErrorCodes.ProtocolError, r.Error?.Code);
        await AssertStillOpenAsync(raw);
        await host.SettleAsync(id, spawner);
        Assert.Equal(0, host.Mux(id).AttachedClients);
        Assert.Equal((80, 24), (host.Mux(id).Cols, host.Mux(id).Rows));
    }

    [Theory]
    [InlineData(2_000, 600)]
    [InlineData(12_000, 2)]
    public async Task A_spawn_over_the_ceiling_is_refused_before_the_factory_runs(int cols, int rows)
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();

        MuxResponse r = await CallAsync(raw, MuxMethods.Spawn, new SpawnParams { Command = "scripted", Cols = cols, Rows = rows }, MuxJsonContext.Default.SpawnParams);

        Assert.Equal(MuxErrorCodes.ProtocolError, r.Error?.Code);
        Assert.Empty(host.Factory.Requests);
        Assert.Empty(host.Server.GetSessionIds());
        await AssertStillOpenAsync(raw);
    }

    [Fact]
    public async Task The_ceiling_is_configurable()
    {
        using var host = new MuxTestHost(new MuxServerOptions { ForceConPtyFiltering = false, MaxCells = 100 * 30, MaxDimension = 100 });
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();

        MuxResponse ok = await CallAsync(raw, MuxMethods.Spawn, new SpawnParams { Command = "scripted", Cols = 100, Rows = 30 }, MuxJsonContext.Default.SpawnParams);
        MuxResponse tooWide = await CallAsync(raw, MuxMethods.Spawn, new SpawnParams { Command = "scripted", Cols = 101, Rows = 1 }, MuxJsonContext.Default.SpawnParams);
        MuxResponse tooMany = await CallAsync(raw, MuxMethods.Spawn, new SpawnParams { Command = "scripted", Cols = 100, Rows = 31 }, MuxJsonContext.Default.SpawnParams);

        Assert.Null(ok.Error);
        Assert.Equal(MuxErrorCodes.ProtocolError, tooWide.Error?.Code);
        Assert.Equal(MuxErrorCodes.ProtocolError, tooMany.Error?.Code);
        Assert.Single(host.Server.GetSessionIds());
    }

    [Fact]
    public async Task A_session_the_mux_fails_to_wrap_is_disposed_and_reported_as_spawn_failed()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();
        host.Factory.ThrowOnSubscribeNext = true;

        MuxResponse r = await CallAsync(raw, MuxMethods.Spawn, new SpawnParams { Command = "scripted" }, MuxJsonContext.Default.SpawnParams);

        Assert.Equal(MuxErrorCodes.SpawnFailed, r.Error?.Code);
        Assert.True(host.Factory.LastScriptedSession!.Disposed);
        Assert.Empty(host.Server.GetSessionIds());
        await AssertStillOpenAsync(raw);
    }
}
