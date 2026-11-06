using System.Text.Json;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.VT;
using Ntilde.VT.Tests;

namespace Ntilde.Mux.Tests.Server;

/// <summary>
/// #838: the image policy is negotiated as the SIXEL raster policy is. A session's policy is fixed when it is
/// spawned, and a peer that does not support it, or names another, is refused before anything takes effect.
/// </summary>
public sealed class ImagePolicyAdmissionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(int.MaxValue, PersistentImagePolicy.EnabledVersion)]
    public async Task The_welcome_offers_the_lower_of_the_peer_ceiling_and_this_build(int offered, int negotiated)
    {
        using var host = new MuxTestHost();
        var raw = host.ConnectRaw();
        raw.Request(MuxMethods.Hello, new HelloParams { MinVersion = 1, MaxVersion = 2, MaxImagePolicy = offered }, MuxJsonContext.Default.HelloParams);
        var reply = await raw.ReadResponseAsync();
        Assert.Null(reply.Error);
        Assert.Equal(negotiated, MuxFrames.ParseParams(reply.Result, MuxJsonContext.Default.WelcomeResult).MaxImagePolicy);
        Assert.Equal(negotiated != 0, reply.Result!.Value.GetRawText().Contains("maxImagePolicy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_negative_image_ceiling_is_refused_before_the_welcome()
    {
        using var host = new MuxTestHost();
        var raw = host.ConnectRaw();
        raw.Request(MuxMethods.Hello, new HelloParams { MinVersion = 1, MaxVersion = 2, MaxImagePolicy = -1 }, MuxJsonContext.Default.HelloParams);
        Assert.Equal(MuxErrorCodes.ProtocolError, (await raw.ReadResponseAsync()).Error?.Code);
        Assert.True(await raw.IsClosedByPeerAsync());
    }

    [Theory]
    [InlineData(0, 1)] // the connection offered none
    [InlineData(1, 2)] // past this build
    [InlineData(1, -1)]
    public async Task A_spawn_policy_the_connection_did_not_negotiate_is_refused_before_the_factory(int ceiling, int required)
    {
        using var host = new MuxTestHost();
        var raw = host.ConnectRaw();
        await HelloAsync(raw, ceiling);
        Send(raw, MuxMethods.Spawn, $"{{\"command\":\"scripted\",\"cols\":80,\"rows\":24,\"requiredImagePolicy\":{required}}}");
        var response = await raw.ReadResponseAsync();
        Assert.True(response.Error?.Code == MuxErrorCodes.VersionMismatch, response.Error?.Message ?? "no error");
        Assert.Empty(host.Factory.Requests);
    }

    [Theory]
    [InlineData(1, 0)] // an older window cannot attach to a session that carries images
    [InlineData(0, 1)] // nor can a window claim images on a SIXEL-only session
    public async Task An_attach_must_name_the_sessions_image_policy(int sessionPolicy, int attachPolicy)
    {
        using var host = new MuxTestHost();
        var raw = host.ConnectRaw();
        await HelloAsync(raw, PersistentImagePolicy.EnabledVersion);
        Guid id = host.Server.Spawn(new SpawnParams { Command = "scripted", RequiredImagePolicy = sessionPolicy });
        raw.Request(MuxMethods.Attach, new AttachParams { SessionId = id, Presentation = MuxTestHost.DefaultPresentation, RequiredImagePolicy = attachPolicy },
            MuxJsonContext.Default.AttachParams);
        Assert.Equal(MuxErrorCodes.VersionMismatch, (await raw.ReadResponseAsync()).Error?.Code);
        await host.Mux(id).FlushAsync();
        Assert.Equal(0, host.Mux(id).AttachedClients);
    }

    [Fact]
    public async Task The_session_list_names_each_sessions_image_policy()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync(new MuxClientOptions { MaxImagePolicy = PersistentImagePolicy.EnabledVersion });
        Assert.Equal(PersistentImagePolicy.EnabledVersion, client.MaxImagePolicy);
        Guid images = await client.SpawnAsync(new SpawnParams { Command = "scripted", Cols = 80, Rows = 24, RequiredImagePolicy = 1 }, Ct);
        Guid sixelOnly = await MuxTestHost.SpawnAsync(client);

        IReadOnlyList<SessionSummary> sessions = await client.ListSessionsAsync(Ct);

        Assert.Equal(1, sessions.Single(s => s.SessionId == images).RequiredImagePolicy);
        Assert.Equal(0, sessions.Single(s => s.SessionId == sixelOnly).RequiredImagePolicy);
        Assert.Equal(1, host.Mux(images).RequiredImagePolicy);
    }

    [Fact]
    public async Task A_read_of_the_screen_carries_no_Kitty_or_iTerm2_images()
    {
        // readScreen serves text readers (the agent host, the CLI): the daemon keeps the images, the read leaves them out.
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync(new MuxClientOptions { MaxImagePolicy = PersistentImagePolicy.EnabledVersion });
        Guid id = await client.SpawnAsync(new SpawnParams { Command = "scripted", Cols = 80, Rows = 24, RequiredImagePolicy = 1 }, Ct);
        host.Fake(id).Emit("before\x1b_Ga=T,i=1,f=100;" + TestImages.PngBase64(20, 20) + "\x1b\\\r\n"
            + "\x1b]1337;File=inline=1;width=4:" + TestImages.PngBase64(10, 10) + "\aafter");
        await host.Mux(id).FlushAsync();
        Assert.Equal(2, host.Mux(id).Buffer.Images.Count);

        MuxScreenRead read = Assert.IsType<MuxScreenRead>(await client.ReadScreenAsync(id, 0, Ct));

        Assert.Null(read.Snapshot.KittyImages);
        Assert.Null(read.Snapshot.KittyPlacements);
        Assert.Null(read.Snapshot.ITermImages);
        Assert.Equal(2, host.Mux(id).Buffer.Images.Count);
    }

    private static async Task HelloAsync(RawMuxConnection raw, int imageCeiling)
    {
        raw.Request(MuxMethods.Hello, new HelloParams { MinVersion = 1, MaxVersion = 2, MaxImagePolicy = imageCeiling }, MuxJsonContext.Default.HelloParams);
        Assert.Null((await raw.ReadResponseAsync()).Error);
    }

    private static void Send(RawMuxConnection raw, string method, string json)
    {
        using var doc = JsonDocument.Parse(json);
        raw.Send(MuxFrames.Request(new MuxRequest { Id = 20, Method = method, Params = doc.RootElement.Clone() }));
    }
}
