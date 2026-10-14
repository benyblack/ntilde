using System.Diagnostics;
using System.Globalization;
using System.Text;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Scenarios;

public sealed class AttachLatencyTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Measure_attach_latency_empty_and_with_10k_scrollback()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();

        var empty = Stopwatch.StartNew();
        Guid emptyId = await MuxTestHost.SpawnAsync(client);
        ClientPaneModel emptyPane = await MuxTestHost.AttachPaneAsync(client, emptyId);
        await host.SettleAsync(emptyId, client);
        empty.Stop();
        host.AssertPaneMatchesMux(emptyId, emptyPane, "empty");

        Guid bigId = await MuxTestHost.SpawnAsync(client);
        var text = new StringBuilder();
        for (int i = 0; i < 10_100; i++) text.Append(CultureInfo.InvariantCulture, $"scrollback line {i:D5} with some padding text\r\n");
        host.Fake(bigId).EmitInChunks(Encoding.UTF8.GetBytes(text.ToString()), chunkSize: 64 * 1024);
        await host.Mux(bigId).FlushAsync();

        var big = Stopwatch.StartNew();
        ClientPaneModel bigPane = await MuxTestHost.AttachPaneAsync(client, bigId, maxScrollbackRows: 10_000);
        await host.SettleAsync(bigId, client);
        big.Stop();
        host.AssertPaneMatchesMux(bigId, bigPane, "10k scrollback");

        // TerminalBuffer's scrollback is byte-budgeted (cols * maxHistory(10_000) * CellBytes) and
        // evicts a whole 64-row page at a time, so what it actually retains saw-tooths in
        // [9_937, 10_000] rather than landing on 10_000 exactly - a fact about Ntilde.VT, not the
        // mux (see the report's divergences). AssertPaneMatchesMux above is the exact-equality proof
        // that the pane converged on whatever the mux actually retained; this just confirms that
        // "actually retained" was still substantially the requested ~10k, not a much smaller number.
        Assert.InRange(bigPane.LastSnapshot!.ScrollbackRowCount, 9_900, 10_000);

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[mux-phase1] attach latency: empty session {empty.Elapsed.TotalMilliseconds:F1} ms (spawn -> equal); " +
            $"80x24 + 10k scrollback {big.Elapsed.TotalMilliseconds:F1} ms (attach -> equal), snapshot {bigPane.Session.LastSnapshotByteCount} bytes"));
    }
}
