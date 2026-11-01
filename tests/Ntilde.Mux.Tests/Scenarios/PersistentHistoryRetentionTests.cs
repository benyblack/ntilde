using System.Globalization;
using System.Text;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Mux.Transport;
using Ntilde.VT;

namespace Ntilde.Mux.Tests.Scenarios;

public sealed class PersistentHistoryRetentionTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ConfiguredHistorySurvivesDaemonRetentionAcrossDetachAndReattach()
    {
        const int requestedHistory = 100_000, lines = 25_000;
        string root = Path.Combine(Path.GetTempPath(), "nmxhist" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var factory = new ScriptedSessionFactory();
            using var server = new MuxServer(factory, new MuxServerOptions { ForceConPtyFiltering = false });
            string endpoint = MuxDiscovery.GetDefaultEndpoint(root);
            server.Start(MuxListeners.Create(endpoint));
            using var client = await ConnectAsync(endpoint);
            Guid id = await MuxTestHost.SpawnAsync(client);
            Assert.True(server.TryGetSession(id, out var daemon));
            var live = await AttachAsync(client, id, requestedHistory);
            var text = new StringBuilder();
            for (int i = 0; i < lines; i++) text.Append(CultureInfo.InvariantCulture, $"retention line {i:D5}\r\n");
            ((ScriptedTerminalSession)daemon!.Inner).EmitInChunks(Encoding.UTF8.GetBytes(text.ToString()), 64 * 1024);
            await SettleAsync(daemon, client);
            Assert.Equal(lines + 1, live.Buffer.TotalLines);
            Assert.Equal("retention line 00000", ReadRow(live.Buffer, 0));
            int liveRows = live.Buffer.Scrollback.Count;
            int retainedRows = daemon.Buffer.Scrollback.Count;
            string retainedFirst = ReadRow(daemon.Buffer, 0);
            live.Session.Detach(userDetached: false);
            await SettleAsync(daemon, client);
            Assert.Equal(0, daemon.AttachedClients);
            using var reconnected = await ConnectAsync(endpoint);
            var restored = await AttachAsync(reconnected, id, requestedHistory);
            await SettleAsync(daemon, reconnected);
            output.WriteLine($"configured={requestedHistory}; emitted={lines}; liveHistory={liveRows}; daemonMaxHistory={daemon.Buffer.MaxHistory}; retainedHistory={retainedRows}; retainedFirst={retainedFirst}; restoredHistory={restored.Buffer.Scrollback.Count}; restoredFirst={ReadRow(restored.Buffer, 0)}; snapshotRows={restored.LastSnapshot!.ScrollbackRowCount}");
            Assert.Single(restored.Buffer.FindMatches("retention line 24999", false, true));
            Assert.InRange(restored.LastSnapshot.ScrollbackRowCount, 0, 20_000);
            Assert.Equal(liveRows, retainedRows);
            Assert.Equal("retention line 00000", retainedFirst);
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<MuxClient> ConnectAsync(string endpoint)
    {
        var ct = TestContext.Current.CancellationToken;
        Stream stream = await Task.Run(() => MuxEndpointConnector.Connect(endpoint, TimeSpan.FromSeconds(5)), ct);
        return await MuxClient.ConnectAsync(stream, cancellationToken: ct);
    }

    private static async Task<ClientPaneModel> AttachAsync(MuxClient client, Guid id, int history)
    {
        var session = client.OpenSession(id, "scripted");
        var pane = new ClientPaneModel(session);
        pane.Buffer.MaxHistory = history;
        await session.AttachAsync(Math.Clamp(history, 0, 50_000), MuxTestHost.DefaultPresentation with { MaxHistory = history }, TestContext.Current.CancellationToken);
        return pane;
    }

    private static async Task SettleAsync(HeadlessTerminalSession session, MuxClient client)
    {
        await client.PingAsync(TestContext.Current.CancellationToken);
        await session.FlushAsync();
        await client.PingAsync(TestContext.Current.CancellationToken);
    }

    private static string ReadRow(TerminalBuffer buffer, int row)
    {
        buffer.Lock.EnterReadLock();
        try { return new string(Enumerable.Range(0, buffer.Cols).Select(col => buffer.GetCellAbsolute(col, row).Character).ToArray()).TrimEnd(); }
        finally { buffer.Lock.ExitReadLock(); }
    }
}
