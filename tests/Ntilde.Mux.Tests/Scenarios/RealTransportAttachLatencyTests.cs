using System.Diagnostics;
using System.Globalization;
using System.Text;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Mux.Transport;

namespace Ntilde.Mux.Tests.Scenarios;

/// <summary>
/// <see cref="AttachLatencyTests"/> over the production transport (a current-user named pipe on
/// Windows, a Unix socket in a 0700 directory elsewhere) instead of the in-memory pipe, so the
/// numbers include real framing, kernel buffers and the listener's accept path.
/// </summary>
public sealed class RealTransportAttachLatencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nmxrt" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<IDisposable> _owned = new();
    private MuxServer? _server;

    public void Dispose()
    {
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _server?.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Measure_attach_latency_over_the_real_transport_empty_and_with_10k_scrollback()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_root);
        string endpoint = MuxDiscovery.GetDefaultEndpoint(_root); // unique per test: the root hash is in it
        _server = new MuxServer(new ScriptedSessionFactory(), new MuxServerOptions { ForceConPtyFiltering = false });
        _server.Start(MuxListeners.Create(endpoint));

        Stream stream = await Task.Run(() => MuxEndpointConnector.Connect(endpoint, TimeSpan.FromSeconds(5)), ct);
        MuxClient client = await MuxClient.ConnectAsync(stream, cancellationToken: ct);
        _owned.Add(client);

        var empty = Stopwatch.StartNew();
        Guid emptyId = await MuxTestHost.SpawnAsync(client);
        ClientPaneModel emptyPane = await MuxTestHost.AttachPaneAsync(client, emptyId);
        await SettleAsync(emptyId, client);
        empty.Stop();
        AssertPaneMatchesMux(emptyId, emptyPane, "empty, real transport");

        Guid bigId = await MuxTestHost.SpawnAsync(client);
        var text = new StringBuilder();
        for (int i = 0; i < 10_100; i++) text.Append(CultureInfo.InvariantCulture, $"scrollback line {i:D5} with some padding text\r\n");
        Fake(bigId).EmitInChunks(Encoding.UTF8.GetBytes(text.ToString()), chunkSize: 64 * 1024);
        await Mux(bigId).FlushAsync();

        var big = Stopwatch.StartNew();
        ClientPaneModel bigPane = await MuxTestHost.AttachPaneAsync(client, bigId, maxScrollbackRows: 10_000);
        await SettleAsync(bigId, client);
        big.Stop();
        AssertPaneMatchesMux(bigId, bigPane, "10k scrollback, real transport");
        // Same byte-budgeted saw-tooth as the in-memory test (see AttachLatencyTests).
        Assert.InRange(bigPane.LastSnapshot!.ScrollbackRowCount, 9_900, 10_000);

        // Generous ceilings only: this is a measurement, not a performance gate.
        Assert.True(empty.Elapsed < TimeSpan.FromSeconds(5), $"empty attach took {empty.Elapsed}");
        Assert.True(big.Elapsed < TimeSpan.FromSeconds(5), $"10k attach took {big.Elapsed}");

        string line = string.Create(CultureInfo.InvariantCulture,
            $"[mux-phase2] real transport attach latency ({(OperatingSystem.IsWindows() ? "named pipe" : "unix socket")}): " +
            $"empty session {empty.Elapsed.TotalMilliseconds:F1} ms (spawn -> equal); " +
            $"80x24 + 10k scrollback {big.Elapsed.TotalMilliseconds:F1} ms (attach -> equal), snapshot {bigPane.Session.LastSnapshotByteCount} bytes");
        TestContext.Current.TestOutputHelper?.WriteLine(line);
    }

    private HeadlessTerminalSession Mux(Guid id) =>
        _server!.TryGetSession(id, out HeadlessTerminalSession? session) ? session : throw new InvalidOperationException($"No session {id}.");

    private ScriptedTerminalSession Fake(Guid id) => (ScriptedTerminalSession)Mux(id).Inner;

    /// <summary>The <see cref="MuxTestHost.SettleAsync"/> quiesce, against this test's server.</summary>
    private async Task SettleAsync(Guid sessionId, MuxClient client)
    {
        await client.PingAsync(TestContext.Current.CancellationToken);
        await Mux(sessionId).FlushAsync();
        await client.PingAsync(TestContext.Current.CancellationToken);
    }

    private void AssertPaneMatchesMux(Guid sessionId, ClientPaneModel pane, string because)
    {
        HeadlessTerminalSession mux = Mux(sessionId);
        Assert.Equal(mux.StreamPosition, pane.Session.StreamPosition);
        Ntilde.VT.Tests.StateTransfer.TerminalStateAssert.AssertEquivalent(
            $"[{because}]", mux.Buffer, mux.Parser, pane.Buffer, pane.Parser);
    }
}
