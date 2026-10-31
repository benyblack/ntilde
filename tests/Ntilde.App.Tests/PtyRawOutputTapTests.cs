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
        public async Task Host_recording_keeps_raw_chunks_with_their_delivery_and_applied_metrics()
        {
            byte[] image = Encoding.UTF8.GetBytes("\x1b[24;1H\x1bPq!35~-~\x1b\\Z");
            var reads = new ScriptedReads([0xE2], [0x82, 0xAC], image);
            using var arrived = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            string path = System.IO.Path.GetTempFileName();
            try
            {
                using RustPtySession session = NewSession(reads);
                var live = new Ntilde.VT.TerminalBuffer(80, 24);
                var parser = new Ntilde.VT.AnsiParser(live);
                int deliveries = 0;
                session.SubscribeRecordingOutput((text, data) =>
                {
                    if (text.Contains("\x1bP", StringComparison.Ordinal))
                    {
                        arrived.Set();
                        Assert.True(release.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
                    }
                    session.RecordOutputAtHostBoundary(data);
                    parser.Process(text);
                    Interlocked.Increment(ref deliveries);
                });
                session.StartRecording(path);
                foreach (var gate in reads.Gates) gate.Release();
                Assert.True(arrived.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
                session.SetRecordingCellMetrics(12, 6);
                parser.SixelCellSizeOverride = new Ntilde.VT.SixelCellSize(12, 6);
                release.Set();
                await Ntilde.Mux.Tests.Support.TestWait.UntilAsync(() => Volatile.Read(ref deliveries) == 3, "all raw chunks reached their host boundary");
                session.StopRecording();
                var raw = new System.Collections.Generic.List<byte>();
                await new Ntilde.Replay.ReplayRunner(path).RunAsync(data => { raw.AddRange(data); return Task.CompletedTask; }, ct: TestContext.Current.CancellationToken);
                Assert.Equal(new byte[] { 0xE2, 0x82, 0xAC }.Concat(image), raw);
                using var stdout = new System.IO.StringWriter();
                using var stderr = new System.IO.StringWriter();
                Assert.Equal(0, ReplayCommand.Execute(["--replay", path], stdout, stderr));
                Assert.Equal(Ntilde.Replay.BufferSnapshot.Capture(live).ToFormattedString() + Environment.NewLine, stdout.ToString());
            }
            finally
            {
                release.Set();
                foreach (var gate in reads.Gates) { if (gate.CurrentCount == 0) gate.Release(); }
                System.IO.File.Delete(path);
            }
        }

        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task A_failed_output_consumer_rejects_queued_recording_activation()
        {
            var reads = new ScriptedReads("held"u8.ToArray(), "later"u8.ToArray());
            using var arrived = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            string path = System.IO.Path.GetTempFileName();
            try
            {
                using RustPtySession session = NewSession(reads);
                session.SubscribeRecordingOutput((_, _) =>
                {
                    arrived.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
                    throw new InvalidOperationException("simulated consumer failure");
                });
                try
                {
                    reads.Gates[0].Release();
                    Assert.True(arrived.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
                    Task start = Task.Run(() => session.StartRecording(path), TestContext.Current.CancellationToken);
                    await Ntilde.Mux.Tests.Support.TestWait.UntilAsync(() => start.IsCompleted || session.QueuedOutputCount == 1, "recording activation reached the held consumer");
                    release.Set();
                    await Assert.ThrowsAsync<ObjectDisposedException>(() => start.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
                    Assert.False(session.IsRecording);
                    Assert.Equal(0, new System.IO.FileInfo(path).Length);
                }
                finally { release.Set(); foreach (var gate in reads.Gates) { if (gate.CurrentCount == 0) gate.Release(); } }
            }
            finally { System.IO.File.Delete(path); }
        }

        [Theory]
        [InlineData("start")]
        [InlineData("stop")]
        [InlineData("enable")]
        [InlineData("disable")]
        [InlineData("export")]
        [Trait("Category", "PtySmoke")]
        public async Task Decoded_host_recording_lifecycle_crosses_queued_raw_output(string operation)
        {
            var reads = new ScriptedReads("held"u8.ToArray(), [0xE2], [0x82, 0xAC], "later"u8.ToArray());
            using var arrived = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            string path = System.IO.Path.GetTempFileName();
            try
            {
                using RustPtySession session = NewSession(reads);
                if (operation == "stop") session.StartRecording(path);
                if (operation is "disable" or "export") session.EnableFlightRecording(1 << 20);
                int deliveries = 0;
                session.SubscribeRecordingOutput((text, data) =>
                {
                    if (text == "held")
                    {
                        arrived.Set();
                        Assert.True(release.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
                    }
                    session.RecordOutputAtHostBoundary(data);
                    Interlocked.Increment(ref deliveries);
                });
                for (int i = 0; i < 3; i++) reads.Gates[i].Release();
                Assert.True(arrived.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
                await WaitAsync(reads.Entered[3]); // both UTF-8 chunks queued behind the held delivery
                Task change = Task.Run(() =>
                {
                    switch (operation)
                    {
                        case "start": session.StartRecording(path); break;
                        case "stop": session.StopRecording(); break;
                        case "enable": session.EnableFlightRecording(1 << 20); break;
                        case "disable": session.DisableFlightRecording(); break;
                        case "export": Assert.True(session.TryExportFlightRecording(path, out _)); break;
                    }
                }, TestContext.Current.CancellationToken);
                await Ntilde.Mux.Tests.Support.TestWait.UntilAsync(() => change.IsCompleted || session.QueuedOutputCount == 3, "the lifecycle request reached its output boundary");
                bool changedBeforeDelivery = change.IsCompleted;
                reads.Gates[3].Release(); // later bytes must stay after the requested boundary
                await WaitAsync(reads.Entered[4]);
                release.Set();
                await change;
                await Ntilde.Mux.Tests.Support.TestWait.UntilAsync(() => Volatile.Read(ref deliveries) == 4, "all raw chunks reached the host");
                Assert.False(changedBeforeDelivery);
                if (operation == "disable") { Assert.False(session.IsFlightRecording); return; }
                if (operation == "start") session.StopRecording();
                if (operation == "enable") Assert.True(session.TryExportFlightRecording(path, out _));
                var raw = new System.Collections.Generic.List<byte>();
                await new Ntilde.Replay.ReplayRunner(path).RunAsync(data => { raw.AddRange(data); return Task.CompletedTask; }, ct: TestContext.Current.CancellationToken);
                string expected = operation is "start" or "enable" ? "later" : "held€";
                Assert.Equal(Encoding.UTF8.GetBytes(expected), raw);
            }
            finally
            {
                release.Set();
                foreach (var gate in reads.Gates) { if (gate.CurrentCount == 0) gate.Release(); }
                System.IO.File.Delete(path);
            }
        }

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
