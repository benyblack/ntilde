using Ntilde.Mux.Tests.Support;
using Ntilde.VT;
using Ntilde.VT.Tests.StateTransfer;
using Xunit.Sdk;

namespace Ntilde.Mux.Tests.Scenarios;

public sealed class ResizeOrderingTests
{
    [Fact]
    public async Task Resizes_interleaved_with_flowing_output_converge()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(client, id);
        (int Cols, int Rows)[] sizes = [(100, 30), (40, 10), (132, 43), (80, 24), (61, 17)];

        for (int i = 0; i < 200; i++)
        {
            host.Fake(id).Emit($"\x1b[{(i % 20) + 1};{(i % 70) + 1}Hline {i} \x1b[3{i % 8}mcolour\x1b[0m wrapping text that is long enough to wrap\r\n");
            if (i % 40 == 7) pane.Session.Resize(sizes[i / 40].Cols, sizes[i / 40].Rows);
        }

        await host.SettleAsync(id, client);
        host.AssertPaneMatchesMux(id, pane, "resize storm");
    }

    /// <summary>
    /// The resize arrives exactly between the output it separates - and a client that applied it
    /// when it asked (eagerly) would end up somewhere else. Proven by replaying the recorded
    /// events both ways from the same snapshot.
    /// </summary>
    [Fact]
    public async Task StreamResize_sits_between_the_output_it_separates_and_eager_resizing_diverges()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(client, id);
        const string A = "\x1b[1;80HX"; // X in the last column of an 80-wide row
        const string B = "Y";

        host.Fake(id).Emit(A);
        await host.SettleAsync(id, client);
        pane.Session.Resize(40, 24);
        await host.SettleAsync(id, client);
        host.Fake(id).Emit(B);
        await host.SettleAsync(id, client);

        Assert.Equal(["snapshot@0+0", "out:" + A, "resize:40x24", "out:" + B], pane.Events);
        host.AssertPaneMatchesMux(id, pane, "in-stream");

        HeadlessTerminalSession mux = host.Mux(id);
        (TerminalBuffer inStream, AnsiParser inStreamParser) = Replay(pane.LastSnapshot!, eager: false, A, B);
        (TerminalBuffer eager, AnsiParser eagerParser) = Replay(pane.LastSnapshot!, eager: true, A, B);

        TerminalStateAssert.AssertEquivalent("[in-stream replay]", mux.Buffer, mux.Parser, inStream, inStreamParser);
        Assert.ThrowsAny<XunitException>(() =>
            TerminalStateAssert.AssertEquivalent("[eager replay]", mux.Buffer, mux.Parser, eager, eagerParser));
    }

    private static (TerminalBuffer, AnsiParser) Replay(TerminalStateSnapshot snapshot, bool eager, string a, string b)
    {
        TerminalStateSnapshot copy = TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(snapshot));
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = null };
        TerminalStateTransfer.Restore(buffer, parser, copy);
        if (eager) buffer.Resize(40, 24);
        parser.Process(a);
        if (!eager) buffer.Resize(40, 24);
        parser.Process(b);
        return (buffer, parser);
    }
}
