using System.Text.Json;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Client;

public sealed class PresentationOnlyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsUpdatesUseOnlyTheAdvertisedExtension(bool supported)
    {
        using var peer = FakeMuxServerEnd.Create();
        var connecting = MuxClient.ConnectAsync(peer.ClientEnd, cancellationToken: TestContext.Current.CancellationToken);
        var hello = await peer.ReadRequestAsync();
        peer.Reply(hello.Id, new WelcomeResult { Version = 1, SupportsPresentationOnly = supported }, MuxJsonContext.Default.WelcomeResult);
        using var client = await connecting;
        Assert.Equal(supported, client.SupportsPresentationOnly);
        using var session = client.OpenSession(Guid.NewGuid());

        session.UpdatePresentationSettings(MuxTestHost.DefaultPresentation with { MaxHistory = 60_000 });

        var request = await peer.ReadRequestAsync();
        Assert.Equal(MuxMethods.Resize, request.Method);
        var resize = MuxFrames.ParseParams(request.Params, MuxJsonContext.Default.ResizeParams);
        Assert.Equal(supported, resize.PresentationOnly);
        Assert.Equal(60_000, resize.Presentation!.MaxHistory);
        if (!supported) Assert.DoesNotContain("presentationOnly", request.Params.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyWireShapesOmitTheExtension()
    {
        string welcome = JsonSerializer.Serialize(new WelcomeResult { Version = 1 }, MuxJsonContext.Default.WelcomeResult);
        string resize = JsonSerializer.Serialize(new ResizeParams { Cols = 80, Rows = 24 }, MuxJsonContext.Default.ResizeParams);
        Assert.DoesNotContain("supportsPresentationOnly", welcome, StringComparison.Ordinal);
        Assert.DoesNotContain("presentationOnly", resize, StringComparison.Ordinal);
        Assert.False(JsonSerializer.Deserialize(welcome, MuxJsonContext.Default.WelcomeResult)!.SupportsPresentationOnly);
        Assert.False(JsonSerializer.Deserialize(resize, MuxJsonContext.Default.ResizeParams)!.PresentationOnly);
    }

    [Fact]
    public async Task PresentationOnlyUpdateRaisesRetentionWithoutRevertingGeometry()
    {
        using var host = new MuxTestHost();
        using var client = await host.ConnectClientAsync();
        Assert.True(client.SupportsPresentationOnly);
        Guid id = await MuxTestHost.SpawnAsync(client);
        using var session = client.OpenSession(id);
        await session.AttachAsync(0, MuxTestHost.DefaultPresentation, TestContext.Current.CancellationToken);
        host.Mux(id).PostResize(100, 30, null);
        await host.SettleAsync(id, client);

        session.UpdatePresentationSettings(MuxTestHost.DefaultPresentation with { MaxHistory = 60_000 });
        await client.PingAsync(TestContext.Current.CancellationToken);
        await host.Mux(id).FlushAsync();

        Assert.Equal(100, host.Mux(id).Buffer.Cols);
        Assert.Equal(30, host.Mux(id).Buffer.Rows);
        Assert.Equal(60_000, host.Mux(id).HistoryRetentionRows);
    }
}
