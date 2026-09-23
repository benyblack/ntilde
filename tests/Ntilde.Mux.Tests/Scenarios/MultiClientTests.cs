using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Scenarios;

public sealed class MultiClientTests
{
    [Fact]
    public async Task Latest_resize_wins_and_both_clients_equal_the_mux()
    {
        using var host = new MuxTestHost();
        MuxClient c1 = await host.ConnectClientAsync();
        MuxClient c2 = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(c1);
        ClientPaneModel p1 = await MuxTestHost.AttachPaneAsync(c1, id);
        ClientPaneModel p2 = await MuxTestHost.AttachPaneAsync(c2, id);
        host.Fake(id).Emit("some text\r\nmore text");

        p1.Session.Resize(100, 30);
        await host.SettleAsync(id, c1, c2);
        p2.Session.Resize(90, 20);
        await host.SettleAsync(id, c1, c2);

        Assert.Equal((90, 20), (host.Mux(id).Cols, host.Mux(id).Rows));
        Assert.Equal((90, 20), host.Fake(id).Resizes.Last());
        host.AssertPaneMatchesMux(id, p1, "client 1");
        host.AssertPaneMatchesMux(id, p2, "client 2");
    }

    [Fact]
    public async Task Attaching_resizes_to_the_attaching_client_and_the_other_client_follows_in_stream()
    {
        using var host = new MuxTestHost();
        MuxClient c1 = await host.ConnectClientAsync();
        MuxClient c2 = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(c1);
        ClientPaneModel p1 = await MuxTestHost.AttachPaneAsync(c1, id);

        ClientPaneModel p2 = await MuxTestHost.AttachPaneAsync(c2, id, MuxTestHost.DefaultPresentation with { Cols = 100, Rows = 30 });
        await host.SettleAsync(id, c1, c2);

        Assert.Contains("resize:100x30", p1.Events);
        host.AssertPaneMatchesMux(id, p1, "client 1");
        host.AssertPaneMatchesMux(id, p2, "client 2");
    }

    [Fact]
    public async Task The_latest_clients_presentation_answers_CSI_14_t_once()
    {
        using var host = new MuxTestHost();
        MuxClient c1 = await host.ConnectClientAsync();
        MuxClient c2 = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(c1);
        ClientPaneModel p1 = await MuxTestHost.AttachPaneAsync(c1, id);
        ClientPaneModel p2 = await MuxTestHost.AttachPaneAsync(c2, id);

        p2.Session.UpdatePresentation(new MuxPresentation { Cols = 90, Rows = 20, CellWidthPx = 9, CellHeightPx = 18 });
        await host.SettleAsync(id, c1, c2);
        host.Fake(id).Emit("\x1b[14t");
        await host.SettleAsync(id, c1, c2);

        Assert.Single(host.Fake(id).SentInput, s => s == "\x1b[4;360;810t");
    }

    [Fact]
    public async Task Device_queries_are_answered_exactly_once_with_two_clients_attached()
    {
        using var host = new MuxTestHost();
        MuxClient c1 = await host.ConnectClientAsync();
        MuxClient c2 = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(c1);
        ClientPaneModel p1 = await MuxTestHost.AttachPaneAsync(c1, id);
        ClientPaneModel p2 = await MuxTestHost.AttachPaneAsync(c2, id);

        host.Fake(id).Emit("\x1b[c");
        await host.SettleAsync(id, c1, c2);

        Assert.Single(host.Fake(id).SentInput, s => s.StartsWith("\x1b[?", StringComparison.Ordinal));
        Assert.NotEmpty(p1.Responses); // each pane's parser produced a reply ...
        Assert.NotEmpty(p2.Responses); // ... and nothing sent it
    }
}
