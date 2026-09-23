using Ntilde.Mux.Tests.Support;
using Ntilde.VT.Tests.StateTransfer;

namespace Ntilde.Mux.Tests.Scenarios;

/// <summary>
/// The Phase 0 corpus, attached mid-stream through the wire, at cuts inside escape sequences and
/// inside UTF-8 code points, under both ConPTY filtering modes.
/// </summary>
public sealed class MidStreamAttachTests
{
    public static TheoryData<string, bool> Streams()
    {
        var data = new TheoryData<string, bool>();
        foreach ((string name, _) in ParityCorpus.All())
        {
            data.Add(name, false);
            data.Add(name, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Streams))]
    public async Task Attaching_mid_stream_converges_on_the_mux(string name, bool forceConPtyFiltering)
    {
        byte[] corpus = ParityCorpus.All().Single(c => c.Name == name).Bytes;
        foreach (int cut in StreamCuts.Interesting(corpus))
        {
            using var host = new MuxTestHost(new MuxServerOptions { ForceConPtyFiltering = forceConPtyFiltering });
            MuxClient client = await host.ConnectClientAsync();
            Guid id = await MuxTestHost.SpawnAsync(client);
            host.Fake(id).EmitInChunks(corpus.AsSpan(0, cut), chunkSize: 7);
            await host.Mux(id).FlushAsync();

            ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(client, id);
            host.Fake(id).EmitInChunks(corpus.AsSpan(cut), chunkSize: 7);
            await host.SettleAsync(id, client);

            host.AssertPaneMatchesMux(id, pane, $"{name} @ {cut}, conpty={forceConPtyFiltering}");
        }
    }

    [Fact]
    public async Task An_attach_inside_a_code_point_carries_the_pending_bytes()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        host.Fake(id).Emit([0xF0, 0x9F]); // first half of U+1F600
        await host.Mux(id).FlushAsync();

        ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(client, id);
        host.Fake(id).Emit([0x98, 0x80]);
        await host.SettleAsync(id, client);

        Assert.Equal(["snapshot@0+2", "out:\U0001F600"], pane.Events);
        host.AssertPaneMatchesMux(id, pane, "emoji split across the attach");
    }
}
