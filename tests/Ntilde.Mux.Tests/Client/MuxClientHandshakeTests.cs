using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Client;

public sealed class MuxClientHandshakeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Overlapping_ranges_negotiate_the_highest_common_version_and_report_filtering()
    {
        using var host = new MuxTestHost(new MuxServerOptions { MinProtocolVersion = 1, MaxProtocolVersion = 3, ForceConPtyFiltering = true });
        MuxClient client = await host.ConnectClientAsync(new MuxClientOptions { MinProtocolVersion = 2, MaxProtocolVersion = 5 });

        Assert.Equal(3, client.ProtocolVersion);
        Assert.True(client.ForceConPtyFiltering);
    }

    [Fact]
    public async Task ServerVersion_is_the_servers_AppVersion()
    {
        using var host = new MuxTestHost(new MuxServerOptions { ForceConPtyFiltering = false, AppVersion = "9.9.9" });
        MuxClient client = await host.ConnectClientAsync();

        Assert.Equal("9.9.9", client.ServerVersion);
    }

    [Fact]
    public async Task ServerVersion_is_null_against_a_v1_server_built_without_one()
    {
        using var host = new MuxTestHost(new MuxServerOptions { MaxProtocolVersion = 1, ForceConPtyFiltering = false });
        MuxClient client = await host.ConnectClientAsync();

        Assert.Null(client.ServerVersion);
    }

    [Fact]
    public async Task Disjoint_ranges_throw_version_mismatch_and_the_server_drops_the_connection()
    {
        using var host = new MuxTestHost();
        var ex = await Assert.ThrowsAsync<MuxProtocolException>(() =>
            MuxClient.ConnectAsync(host.Listener.Connect(), new MuxClientOptions { MinProtocolVersion = 3, MaxProtocolVersion = 4 }, Ct));

        Assert.Equal(MuxErrorCodes.VersionMismatch, ex.Code);
        await TestWait.UntilAsync(() => host.Server.ConnectionCount == 0, "the server drops the connection");
    }

    [Fact]
    public async Task A_client_offering_2_to_3_negotiates_2_with_the_default_server()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync(new MuxClientOptions { MinProtocolVersion = 2, MaxProtocolVersion = 3 });

        Assert.Equal(2, client.ProtocolVersion);   // the old "disjoint" input is now a v2 connection
    }

    [Fact]
    public async Task A_welcome_outside_the_clients_range_is_refused()
    {
        using var fake = FakeMuxServerEnd.Create();
        Task<MuxClient> connect = MuxClient.ConnectAsync(fake.ClientEnd, new MuxClientOptions(), Ct);
        await fake.AcceptHelloAsync(version: 9);

        var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => connect);
        Assert.Equal(MuxErrorCodes.VersionMismatch, ex.Code);
    }
}
