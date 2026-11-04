using Ntilde.Mux.Tests.Support;
using Ntilde.VT;

namespace Ntilde.Mux.Tests.Headless;

/// <summary>DECSCNM (?5, #831) in a shared session: an attach snapshot carries it, and the stream toggles it after.</summary>
public sealed class HeadlessReverseVideoTests
{
    [Fact]
    public async Task Attach_snapshot_carries_reverse_video_and_the_live_stream_toggles_it()
    {
        using var host = new MuxTestHost();
        using var client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        host.Fake(id).Emit("\u001b[?5hflash");
        await host.Mux(id).FlushAsync();
        Assert.True(await host.Mux(id).InvokeAsync(() => host.Mux(id).Buffer.Modes.IsReverseVideo));

        using var session = client.OpenSession(id, "scripted");
        var pane = new ClientPaneModel(session);
        await session.AttachAsync(0, MuxTestHost.DefaultPresentation, TestContext.Current.CancellationToken);
        await TestWait.UntilAsync(() => pane.LastSnapshot != null, "the attach snapshot reached the client");
        Assert.True(pane.LastSnapshot!.Modes.IsReverseVideo);
        await TestWait.UntilAsync(() => pane.Buffer.Modes.IsReverseVideo, "the restored pane presents reverse video");

        host.Fake(id).Emit("\u001b[?5l");
        await host.Mux(id).FlushAsync();
        await TestWait.UntilAsync(() => !pane.Buffer.Modes.IsReverseVideo, "the streamed reset reached the client");
    }
}
