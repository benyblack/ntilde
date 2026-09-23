using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ntilde.Pty;
using Xunit;

namespace Ntilde.Tests
{
    /// <summary>
    /// RustPtySession's raw-byte tap: the bytes are the ones pty_read returned, in read order, and
    /// first-subscriber replay stops once a string subscriber exists.
    /// </summary>
    [Collection(PtyRealShellCollection.Name)]
    public class PtyRawOutputTapTests
    {
        /// Returns chunk i once gate i is released; signals Entered[i] when read i begins, which
        /// proves read i-1 was fully processed (published) - the loop is single-threaded.
        private sealed class ScriptedReads
        {
            private readonly byte[][] _chunks;
            private int _next;

            public ScriptedReads(params byte[][] chunks)
            {
                _chunks = chunks;
                Gates = chunks.Select(_ => new SemaphoreSlim(0)).ToArray();
                Entered = Enumerable.Range(0, chunks.Length + 1).Select(_ => new SemaphoreSlim(0)).ToArray();
            }

            public SemaphoreSlim[] Gates { get; }
            public SemaphoreSlim[] Entered { get; }

            public int Read(RustPtySession.PtySafeHandle handle, byte[] buffer, int length)
            {
                int i = _next++;
                Entered[Math.Min(i, Entered.Length - 1)].Release();
                if (i >= _chunks.Length)
                {
                    Thread.Sleep(50);
                    return 0; // EOF once the script is spent
                }

                Gates[i].Wait(TimeSpan.FromSeconds(30));
                _chunks[i].CopyTo(buffer, 0);
                return _chunks[i].Length;
            }
        }

        private static RustPtySession NewSession(ScriptedReads reads) =>
            new RustPtySession(
                ShellHelper.GetDefaultShell(), 80, 24, args: null, cwd: null,
                skipPowerShellPostLaunchInit: true, environmentOverrides: null,
                readFromPty: reads.Read);

        private static async Task WaitAsync(SemaphoreSlim s) =>
            Assert.True(await s.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "scripted read never started");

        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task Bytes_read_before_the_first_raw_subscriber_are_replayed_then_live_bytes_follow_in_order()
        {
            var reads = new ScriptedReads("AB"u8.ToArray(), [0xC3], [0xA9, (byte)'C', (byte)'D']);
            using RustPtySession session = NewSession(reads);
            var raw = new ConcurrentQueue<byte[]>();

            reads.Gates[0].Release();
            await WaitAsync(reads.Entered[1]); // chunk 0 published - into retention, nobody subscribed
            session.OnRawOutputReceived += m => raw.Enqueue(m.ToArray());
            reads.Gates[1].Release();
            reads.Gates[2].Release();
            await WaitAsync(reads.Entered[3]);

            Assert.Equal(
                new[] { "AB"u8.ToArray(), new byte[] { 0xC3 }, new byte[] { 0xA9, (byte)'C', (byte)'D' } },
                raw.ToArray());
        }

        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task A_string_subscriber_that_arrives_first_ends_raw_retention()
        {
            var reads = new ScriptedReads("early"u8.ToArray(), "late"u8.ToArray());
            using RustPtySession session = NewSession(reads);
            var raw = new ConcurrentQueue<byte[]>();
            var text = new ConcurrentQueue<string>();

            session.OnOutputReceived += text.Enqueue;
            reads.Gates[0].Release();
            await WaitAsync(reads.Entered[1]);
            session.OnRawOutputReceived += m => raw.Enqueue(m.ToArray());
            reads.Gates[1].Release();
            await WaitAsync(reads.Entered[2]);

            Assert.Equal(new[] { "late"u8.ToArray() }, raw.ToArray());
        }
    }
}
