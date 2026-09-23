using System;
using System.Linq;
using System.Threading.Tasks;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Transport;
using Ntilde.Pty;
using Ntilde.Replay;
using Ntilde.Shell;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests
{
    /// <summary>
    /// The one end-to-end check with a real PTY: the production session factory spawns a real
    /// shell under the mux, and an attached client sees what the shell prints.
    /// </summary>
    [Collection(PtyRealShellCollection.Name)]
    public class MuxRealShellSmokeTests
    {
        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task A_real_shell_spawned_through_the_mux_reaches_an_attached_client()
        {
            var ct = TestContext.Current.CancellationToken;
            string shell = ShellHelper.GetDefaultShell();
            var listener = new InMemoryMuxListener();
            using var server = new MuxServer(DefaultTerminalSessionFactory.Instance, new MuxServerOptions());
            server.Start(listener);
            using MuxClient client = await MuxClient.ConnectAsync(listener.Connect(), cancellationToken: ct);

            Guid id = await client.SpawnAsync(new SpawnParams
            {
                Command = shell,
                Cols = 80,
                Rows = 24,
                SkipPowerShellPostLaunchInit = true,
                Title = "smoke",
            }, ct);
            using MuxClientSession session = client.OpenSession(id, shell);
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer, session.ForceConPtyFiltering) { ImageDecoder = null };
            object gate = new();
            session.SnapshotReceived += s => { lock (gate) TerminalStateTransfer.Restore(buffer, parser, s); };
            session.OnOutputReceived += t => { lock (gate) parser.Process(t); };
            session.StreamResize += (c, r) => { lock (gate) buffer.Resize(c, r); };
            await session.AttachAsync(1000, new MuxPresentation { Cols = 80, Rows = 24, CellWidthPx = 9, CellHeightPx = 18 }, ct);

            string Screen() { lock (gate) return string.Join('\n', BufferSnapshot.Capture(buffer).Lines); }

            // Let the shell draw its prompt before typing (early input can be eaten by line editors).
            await WaitUntilAsync(() => Screen().Trim().Length > 0, TimeSpan.FromSeconds(30));
            await Task.Delay(500, ct);
            session.SendInput("echo ntilde-mux-smoke\r");

            // The typed command and its output: the marker appears twice.
            await WaitUntilAsync(() => CountOf(Screen(), "ntilde-mux-smoke") >= 2, TimeSpan.FromSeconds(30));
            Assert.True(Assert.Single(await client.ListSessionsAsync(ct)).Running);

            await session.KillAsync(ct);
            Assert.Empty(await client.ListSessionsAsync(ct));
        }

        private static int CountOf(string haystack, string needle)
        {
            int count = 0;
            for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }

        private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting for the shell through the mux.");
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }
    }
}
