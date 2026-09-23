using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Server;

public sealed class MuxServerHandshakeTests
{
    [Fact]
    public async Task Overlapping_ranges_pick_the_highest_common_version()
    {
        using var host = new MuxTestHost(new MuxServerOptions { MinProtocolVersion = 1, MaxProtocolVersion = 3, ForceConPtyFiltering = true });
        RawMuxConnection raw = host.ConnectRaw();

        WelcomeResult welcome = await raw.HelloAsync(min: 2, max: 5);

        Assert.Equal(3, welcome.Version);
        Assert.True(welcome.ForceConPtyFiltering);
    }

    [Fact]
    public async Task Disjoint_ranges_are_refused_and_the_connection_closed()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();

        raw.Request(MuxMethods.Hello, new HelloParams { MinVersion = 2, MaxVersion = 3 }, MuxJsonContext.Default.HelloParams);
        MuxResponse response = await raw.ReadResponseAsync();

        Assert.Equal(MuxErrorCodes.VersionMismatch, response.Error?.Code);
        Assert.True(await raw.IsClosedByPeerAsync());
        await TestWait.UntilAsync(() => host.Server.ConnectionCount == 0, "the server forgets the connection");
    }

    [Fact]
    public async Task The_first_frame_must_be_hello()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();

        raw.Request(MuxMethods.Ping, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty);
        MuxResponse response = await raw.ReadResponseAsync();

        Assert.Equal(0, response.Id);
        Assert.Equal(MuxErrorCodes.ProtocolError, response.Error?.Code);
        Assert.True(await raw.IsClosedByPeerAsync());
    }

    [Fact]
    public async Task A_second_hello_is_a_protocol_error()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();

        raw.Request(MuxMethods.Hello, new HelloParams { MinVersion = 1, MaxVersion = 1 }, MuxJsonContext.Default.HelloParams);

        Assert.Equal(MuxErrorCodes.ProtocolError, (await raw.ReadResponseAsync()).Error?.Code);
        Assert.True(await raw.IsClosedByPeerAsync());
    }
}
