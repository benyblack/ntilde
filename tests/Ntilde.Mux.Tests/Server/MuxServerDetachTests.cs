using System.Text.Json.Serialization.Metadata;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Server;

public sealed class MuxServerDetachTests
{
    private static async Task<MuxResponse> CallAsync<T>(RawMuxConnection raw, string method, T p, JsonTypeInfo<T> info)
    {
        long id = raw.Request(method, p, info);
        MuxResponse response = await raw.ReadResponseAsync();
        Assert.Equal(id, response.Id);
        return response;
    }

    private static async Task<long> AttachAsync(RawMuxConnection raw, Guid session, MuxPresentation? presentation = null)
    {
        long id = raw.Request(MuxMethods.Attach, new AttachParams { SessionId = session, MaxScrollbackRows = 0, Presentation = presentation ?? MuxTestHost.DefaultPresentation },
            MuxJsonContext.Default.AttachParams);
        using MuxInboundFrame? frame = await raw.ReadAsync();
        Assert.Equal(MuxFrameKind.Snapshot, frame?.Kind);
        return id;
    }

    private static async Task<(MuxTestHost Host, MuxClient Spawner, Guid Id, RawMuxConnection Raw)> SetupAsync()
    {
        var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();
        return (host, spawner, id, raw);
    }

    [Fact]
    public async Task A_detach_naming_an_attach_older_than_the_latest_is_ignored_and_one_naming_the_latest_is_not()
    {
        (MuxTestHost host, MuxClient spawner, Guid id, RawMuxConnection raw) = await SetupAsync();
        using (host)
        {
            long first = await AttachAsync(raw, id);
            long second = await AttachAsync(raw, id);

            Assert.Null((await CallAsync(raw, MuxMethods.Detach, new DetachParams { SessionId = id, AttachRequestId = first }, MuxJsonContext.Default.DetachParams)).Error);
            await host.SettleAsync(id, spawner);
            Assert.Equal(1, host.Mux(id).AttachedClients); // attach #2's subscription survives

            Assert.Null((await CallAsync(raw, MuxMethods.Detach, new DetachParams { SessionId = id, AttachRequestId = second }, MuxJsonContext.Default.DetachParams)).Error);
            await host.SettleAsync(id, spawner);
            Assert.Equal(0, host.Mux(id).AttachedClients);
        }
    }

    [Fact]
    public async Task A_plain_detach_is_unconditional()
    {
        (MuxTestHost host, MuxClient spawner, Guid id, RawMuxConnection raw) = await SetupAsync();
        using (host)
        {
            await AttachAsync(raw, id);
            await AttachAsync(raw, id);

            Assert.Null((await CallAsync(raw, MuxMethods.Detach, new SessionIdParams { SessionId = id }, MuxJsonContext.Default.SessionIdParams)).Error);
            await host.SettleAsync(id, spawner);
            Assert.Equal(0, host.Mux(id).AttachedClients);
        }
    }

    [Fact]
    public async Task A_newer_attach_refused_before_it_reached_the_session_does_not_supersede_the_older_one()
    {
        // Attach #2 fails validation and never touches the subscription, so the detach undoing
        // attach #1 must still take effect - otherwise attach #1's subscription would leak.
        (MuxTestHost host, MuxClient spawner, Guid id, RawMuxConnection raw) = await SetupAsync();
        using (host)
        {
            long first = await AttachAsync(raw, id);
            MuxResponse refused = await CallAsync(raw, MuxMethods.Attach, new AttachParams
            {
                SessionId = id,
                MaxScrollbackRows = 0,
                Presentation = MuxTestHost.DefaultPresentation with { Cols = 0 },
            }, MuxJsonContext.Default.AttachParams);
            Assert.Equal(MuxErrorCodes.ProtocolError, refused.Error?.Code);

            Assert.Null((await CallAsync(raw, MuxMethods.Detach, new DetachParams { SessionId = id, AttachRequestId = first }, MuxJsonContext.Default.DetachParams)).Error);
            await host.SettleAsync(id, spawner);
            Assert.Equal(0, host.Mux(id).AttachedClients);
        }
    }
}
