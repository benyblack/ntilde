using System.Text;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Pty;
using Ntilde.Replay;
using Ntilde.VT;
using Ntilde.VT.Tests.StateTransfer;

namespace Ntilde.Mux.Tests.Headless;

public sealed class HeadlessTerminalSessionTests
{
    private static readonly MuxPresentation Presentation80x24 = new() { Cols = 80, Rows = 24, CellWidthPx = 10, CellHeightPx = 20 };

    private static (HeadlessTerminalSession Mux, ScriptedTerminalSession Fake) NewSession(int cols = 80, int rows = 24)
    {
        var fake = new ScriptedTerminalSession(new TerminalSessionRequest(
            "scripted", string.Empty, string.Empty, cols, rows, null, false, null));
        var mux = new HeadlessTerminalSession(Guid.NewGuid(), fake, new HeadlessSessionOptions
        {
            Command = "scripted",
            Title = "t",
            Cols = cols,
            Rows = rows,
            ForceConPtyFiltering = false,
        });
        return (mux, fake);
    }

    private static string ScreenText(TerminalBuffer buffer) =>
        string.Join('\n', BufferSnapshot.Capture(buffer).Lines).TrimEnd();

    public static TheoryData<string> CorpusNames()
    {
        var data = new TheoryData<string>();
        foreach ((string name, _) in ParityCorpus.All()) data.Add(name);
        return data;
    }

    [Fact]
    public async Task Parses_the_tapped_bytes_on_its_own_dedicated_thread()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using (mux)
        {
            fake.Emit("hi");
            await mux.FlushAsync();

            Assert.StartsWith("hi", ScreenText(mux.Buffer), StringComparison.Ordinal);
            (string? name, bool pool) = await mux.InvokeAsync(() => (Thread.CurrentThread.Name, Thread.CurrentThread.IsThreadPoolThread));
            Assert.StartsWith("MuxParse-", name, StringComparison.Ordinal);
            Assert.False(pool);
            Assert.Equal(2, mux.StreamPosition);
        }
    }

    [Theory]
    [MemberData(nameof(CorpusNames))]
    public async Task A_captured_snapshot_plus_the_tail_equals_the_mux(string name)
    {
        byte[] corpus = ParityCorpus.All().Single(c => c.Name == name).Bytes;
        foreach (int cut in StreamCuts.Interesting(corpus))
        {
            (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
            using (mux)
            {
                fake.EmitInChunks(corpus.AsSpan(0, cut), chunkSize: 5);
                // Control items (InvokeAsync) outrank data on the parse thread: without this
                // barrier the snapshot can be captured before some or all of the just-emitted
                // chunks are processed, racily. See the same pattern in the resize test below.
                await mux.FlushAsync();
                TerminalStateSnapshot snapshot = await mux.InvokeAsync(() => mux.CaptureSnapshot(int.MaxValue));
                Assert.Equal(cut, snapshot.StreamSeq + (snapshot.DecoderTail?.Length ?? 0));

                // An independent client: restore, then decode tail + rest with its own decoder.
                var buffer = new TerminalBuffer(80, 24);
                var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = null };
                TerminalStateTransfer.Restore(buffer, parser, snapshot);
                var decoder = new Utf8ChunkDecoder();
                char[] chars = new char[Utf8ChunkDecoder.GetMaxCharCount(corpus.Length + 4)];
                decoder.Decode(snapshot.DecoderTail ?? [], chars);
                int n = decoder.Decode(corpus.AsSpan(cut), chars);
                if (n > 0) parser.Process(new string(chars, 0, n));

                fake.EmitInChunks(corpus.AsSpan(cut), chunkSize: 7);
                await mux.FlushAsync();

                TerminalStateAssert.AssertEquivalent($"[{name} @ {cut}]", mux.Buffer, mux.Parser, buffer, parser);
            }
        }
    }

    [Fact]
    public async Task Device_queries_are_answered_by_writing_to_the_session()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using (mux)
        {
            fake.Emit("\x1b[c");
            await mux.FlushAsync();

            string reply = Assert.Single(fake.SentInput);
            Assert.StartsWith("\x1b[?", reply, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_resize_is_recorded_at_the_current_offset_and_reaches_the_child_after_the_buffer()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using (mux)
        {
            int colsWhenChildResized = -1;
            fake.OnResizeCalled = (_, _) => colsWhenChildResized = mux.Buffer.Cols; // runs on the parse thread
            var sink = new RecordingFrameSink();
            mux.PostAttach(sink, requestId: 1, maxScrollbackRows: 100, Presentation80x24, MuxProtocol.MaxFrameBytes);

            fake.Emit("abc");
            await mux.FlushAsync();          // control items outrank data: without this the resize could run first
            mux.PostResize(40, 10, presentation: null);
            await mux.InvokeAsync(() => 0);  // the resize has run
            fake.Emit("d");
            await mux.FlushAsync();

            Assert.Equal(["Snapshot@0", "Output@0:abc", "Resize@3:40x10", "Output@3:d"], sink.Described);
            Assert.Equal([(40, 10)], fake.Resizes.ToArray());
            Assert.Equal(40, colsWhenChildResized);
            Assert.Equal((40, 10), (mux.Cols, mux.Rows));
        }
    }

    [Fact]
    public async Task A_resize_to_the_current_size_emits_no_event()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using (mux)
        {
            var sink = new RecordingFrameSink();
            mux.PostAttach(sink, 1, 100, Presentation80x24, MuxProtocol.MaxFrameBytes);
            mux.PostResize(80, 24, presentation: null);
            await mux.FlushAsync();

            Assert.Equal(["Snapshot@0"], sink.Described);
            Assert.Empty(fake.Resizes);
        }
    }

    [Fact]
    public async Task Resize_sends_the_in_band_report_when_mode_2048_is_on()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using (mux)
        {
            fake.Emit("\x1b[?2048h");
            await mux.FlushAsync(); // mode on before the resize is dequeued
            mux.PostResize(100, 30, Presentation80x24 with { Cols = 100, Rows = 30, CellWidthPx = 8, CellHeightPx = 16 });
            await mux.FlushAsync();

            Assert.Contains("\x1b[48;30;100;480;800t", fake.SentInput);
        }
    }

    [Fact]
    public async Task Presentation_reaches_the_parser_so_CSI_14_t_describes_the_client()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using (mux)
        {
            mux.PostResize(90, 20, new MuxPresentation { Cols = 90, Rows = 20, CellWidthPx = 9, CellHeightPx = 18 });
            fake.Emit("\x1b[14t");
            await mux.FlushAsync();

            Assert.Contains("\x1b[4;360;810t", fake.SentInput);
        }
    }

    [Fact]
    public async Task Exit_is_delivered_after_every_byte_already_tapped()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using (mux)
        {
            var sink = new RecordingFrameSink();
            mux.PostAttach(sink, 1, 100, Presentation80x24, MuxProtocol.MaxFrameBytes);
            fake.Emit("tail");
            fake.Exit(7);
            await mux.FlushAsync();

            Assert.Equal(["Snapshot@0", "Output@0:tail", "Exited:7"], sink.Described);
            Assert.True(mux.IsExited);
            Assert.Equal(7, mux.ExitCode);
        }
    }

    [Fact]
    public async Task Attaching_to_an_exited_session_is_followed_by_its_exit()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using (mux)
        {
            fake.Exit(4);
            await mux.FlushAsync();
            var sink = new RecordingFrameSink();
            mux.PostAttach(sink, 1, 100, Presentation80x24, MuxProtocol.MaxFrameBytes);
            await mux.FlushAsync();

            Assert.Equal(["Snapshot@0", "Exited:4"], sink.Described);
        }
    }

    [Fact]
    public async Task An_attach_is_answered_even_when_the_childs_resize_throws()
    {
        // The attach resizes the session to the attaching client's size. If the child's Resize
        // throws (a native transport gone bad), the attach must still be answered - the buffer and
        // every client have already moved to the new size, so the snapshot is still the truth -
        // rather than leave the client waiting out its whole request timeout.
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using (mux)
        {
            fake.Emit("before");
            await mux.FlushAsync(); // control outranks data: parse "before" first
            fake.ThrowOnResize = true;
            var sink = new RecordingFrameSink();

            mux.PostAttach(sink, 3, 100, Presentation80x24 with { Cols = 100, Rows = 30 }, MuxProtocol.MaxFrameBytes);
            await mux.FlushAsync();

            Assert.Equal(["Snapshot@6"], sink.Described);
            Assert.Equal((100, 30), (mux.Cols, mux.Rows));
            Assert.False(mux.IsFaulted);

            // ... and the session keeps working: a later resize failing the same way is survived too.
            mux.PostResize(90, 20, presentation: null);
            fake.Emit(" after");
            await mux.FlushAsync();
            Assert.Equal(["Snapshot@6", "Resize@6:90x20", "Output@6: after"], sink.Described);
        }
    }

    [Fact]
    public async Task A_flood_of_resize_requests_coalesces_into_one_pending_resize()
    {
        // Resize requests expect no reply, so nothing throttles a peer that sends them faster than
        // the parse thread can apply them. Only the latest size matters (latest wins), so pending
        // resizes must collapse into one work item instead of piling up without bound.
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using (mux)
        {
            using var release = new ManualResetEventSlim();
            Task<int> parked = mux.InvokeAsync(() => { release.Wait(TimeSpan.FromSeconds(30)); return 0; });
            await TestWait.UntilAsync(() => mux.QueuedControlCount == 0, "the parse thread is parked inside the invoke");

            for (int i = 0; i < 10_000; i++)
            {
                mux.PostResize(100 + (i % 2), 30, presentation: null);
            }

            mux.PostResize(90, 20, new MuxPresentation { Cols = 90, Rows = 20, CellWidthPx = 9, CellHeightPx = 18 });
            Assert.True(mux.QueuedControlCount <= 1, $"{mux.QueuedControlCount} control items queued for 10,001 resizes");

            release.Set();
            await parked;
            fake.Emit("\x1b[14t");
            await mux.FlushAsync();

            Assert.Equal((90, 20), (mux.Cols, mux.Rows));
            Assert.Equal([(90, 20)], fake.Resizes.ToArray());          // the child learned the final size, once
            Assert.Contains("\x1b[4;360;810t", fake.SentInput);         // the coalesced presentation still applied
        }
    }

    [Fact]
    public async Task A_child_that_exited_before_the_mux_subscribed_is_reported_exited()
    {
        // The factory hands over an already-running session; a short command can be gone before
        // the constructor subscribes to OnExit, and RustPtySession does not replay OnExit. The mux
        // must not report that session as running forever.
        var fake = new ScriptedTerminalSession(new TerminalSessionRequest("scripted", string.Empty, string.Empty, 80, 24, null, false, null));
        fake.Emit("last words"); // raw output before any subscriber: dropped by this fake, that's fine
        fake.Exit(9);

        using var mux = new HeadlessTerminalSession(Guid.NewGuid(), fake, new HeadlessSessionOptions { Cols = 80, Rows = 24, ForceConPtyFiltering = false });
        await mux.FlushAsync();

        Assert.True(mux.IsExited);
        Assert.Equal(9, mux.ExitCode);
    }

    [Fact]
    public async Task A_failed_reattach_keeps_the_stream_that_was_already_working()
    {
        // An attached client asks for a fresh snapshot, and this one is refused (the scrollback has
        // grown past the limit). Its existing subscription must survive: the client keeps its old
        // position and would otherwise wait forever for output that is no longer sent.
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using (mux)
        {
            var sink = new RecordingFrameSink();
            mux.PostAttach(sink, 1, 100, Presentation80x24, MuxProtocol.MaxFrameBytes);
            await mux.FlushAsync();

            mux.PostAttach(sink, 2, 100, Presentation80x24, maxSnapshotBytes: 16);
            fake.Emit("still streaming");
            await mux.FlushAsync();

            Assert.Equal(["Snapshot@0", "Error:snapshot_too_large", "Output@0:still streaming"], sink.Described);
            Assert.Equal(1, mux.AttachedClients);
        }
    }

    [Fact]
    public async Task A_successful_reattach_does_not_subscribe_the_sink_twice()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using (mux)
        {
            var sink = new RecordingFrameSink();
            mux.PostAttach(sink, 1, 100, Presentation80x24, MuxProtocol.MaxFrameBytes);
            mux.PostAttach(sink, 2, 100, Presentation80x24, MuxProtocol.MaxFrameBytes);
            fake.Emit("x");
            await mux.FlushAsync();

            Assert.Equal(["Snapshot@0", "Snapshot@0", "Output@0:x"], sink.Described);
            Assert.Equal(1, mux.AttachedClients);
        }
    }

    [Fact]
    public async Task An_oversize_snapshot_is_refused_and_the_sink_is_not_subscribed()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using (mux)
        {
            var sink = new RecordingFrameSink();
            mux.PostAttach(sink, 5, 100, Presentation80x24, maxSnapshotBytes: 16);
            fake.Emit("x");
            await mux.FlushAsync();

            Assert.Equal(["Error:snapshot_too_large"], sink.Described);
            Assert.Equal(0, mux.AttachedClients);
        }
    }

    [Fact]
    public async Task A_snapshot_too_big_for_any_frame_is_answered_even_when_the_caller_allows_more()
    {
        // A caller-supplied limit above what one frame can carry used to reach MuxFrames.Snapshot,
        // which threw inside the attach action: no reply at all, and the client waited out its
        // 30 s timeout. The frame build must fail safe, with an error reply.
        const int cols = 2_500, rows = 1_500; // 3.75M cells, base64 of the raw cells: well past 64 MiB
        (HeadlessTerminalSession mux, _) = NewSession(cols, rows);
        using (mux)
        {
            var sink = new RecordingFrameSink();
            mux.PostAttach(sink, 7, 0, Presentation80x24 with { Cols = cols, Rows = rows }, maxSnapshotBytes: int.MaxValue);
            await mux.FlushAsync();
            Assert.Equal(1, await mux.InvokeAsync(() => 1)); // the attach action has run

            Assert.Equal(["Error:snapshot_too_large"], sink.Described);
            Assert.Equal(0, mux.AttachedClients);
        }
    }

    [Fact]
    public async Task A_sink_that_refuses_a_frame_is_dropped()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using (mux)
        {
            var sink = new RecordingFrameSink();
            mux.PostAttach(sink, 1, 100, Presentation80x24, MuxProtocol.MaxFrameBytes);
            await mux.FlushAsync();
            Assert.Equal(1, mux.AttachedClients);

            sink.Accept = false;
            fake.Emit("x");
            await mux.FlushAsync();

            Assert.Equal(0, mux.AttachedClients);
        }
    }

    [Fact]
    public async Task A_throwing_parser_marks_the_session_faulted_instead_of_killing_the_host()
    {
        // OnResponse -> session.SendInput runs inside Process, so a throwing SendInput is a parser
        // failure as far as the parse thread can tell. (If AnsiParser ever starts containing
        // callback exceptions itself, this test fails and needs a different trigger.)
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using (mux)
        {
            fake.ThrowOnSendInput = true;
            fake.Emit("\x1b[c");
            await mux.FlushAsync();

            Assert.True(mux.IsFaulted);
            Assert.Equal(1, await mux.InvokeAsync(() => 1)); // the parse thread survived
            var sink = new RecordingFrameSink();
            mux.PostAttach(sink, 9, 100, Presentation80x24, MuxProtocol.MaxFrameBytes);
            await mux.FlushAsync();
            Assert.Equal(["Error:internal_error"], sink.Described);
        }
    }

    [Fact]
    public async Task A_fault_is_announced_to_every_subscriber_who_are_then_dropped_and_hear_nothing_more()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using (mux)
        {
            var first = new RecordingFrameSink();
            var second = new RecordingFrameSink();
            mux.PostAttach(first, 1, 100, Presentation80x24, MuxProtocol.MaxFrameBytes);
            mux.PostAttach(second, 2, 100, Presentation80x24, MuxProtocol.MaxFrameBytes);
            fake.Emit("ok");
            await mux.FlushAsync();

            fake.ThrowOnSendInput = true;
            fake.Emit("\x1b[c");
            await mux.FlushAsync();
            mux.PostResize(100, 30, null); // the offset moved on: a ResizeEvent now would be a gap
            fake.Emit("more");
            await mux.FlushAsync();
            Assert.Equal(1, await mux.InvokeAsync(() => 1));

            Assert.Equal(["Snapshot@0", "Output@0:ok", "Faulted"], first.Described);
            Assert.Equal(["Snapshot@0", "Output@0:ok", "Faulted"], second.Described);
            Assert.Equal(0, mux.AttachedClients);
            Assert.Equal((100, 30), (mux.Cols, mux.Rows)); // the child still gets its resize
            Assert.Equal((100, 30), fake.Resizes.Last());
        }
    }

    [Fact]
    public async Task Kill_disposes_the_child_and_announces_the_exit_once()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        var sink = new RecordingFrameSink();
        mux.PostAttach(sink, 1, 100, Presentation80x24, MuxProtocol.MaxFrameBytes);
        await mux.FlushAsync();

        mux.Kill();
        await TestWait.UntilAsync(() => sink.Described.Contains("Exited:-1"), "the kill is announced");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.True(fake.Disposed);
        Assert.Single(sink.Described, d => d.StartsWith("Exited", StringComparison.Ordinal));
        Assert.True(mux.IsExited);
        mux.Dispose();
    }

    [Fact]
    public async Task Dispose_with_a_producer_blocked_on_a_full_queue_does_not_deadlock()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using var release = new ManualResetEventSlim();
        Task<int> parked = mux.InvokeAsync(() => { release.Wait(TimeSpan.FromSeconds(30)); return 0; });
        Task producer = Task.Run(() =>
        {
            for (int i = 0; i < HeadlessTerminalSession.DataQueueCapacity + 50; i++) fake.Emit("x");
        }, TestContext.Current.CancellationToken);
        await TestWait.UntilAsync(() => mux.QueuedDataCount == HeadlessTerminalSession.DataQueueCapacity, "the data queue is full");

        Task dispose = Task.Run(mux.Dispose, TestContext.Current.CancellationToken);
        await producer.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        release.Set();
        await dispose.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await parked.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // The parse thread must actually be gone, not merely have had Dispose() return around a
        // Join timeout: a fresh request should be refused promptly, never left hanging.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => mux.InvokeAsync(() => 0))
            .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Kill_with_a_producer_blocked_on_a_full_queue_does_not_deadlock()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using var release = new ManualResetEventSlim();
        Task<int> parked = mux.InvokeAsync(() => { release.Wait(TimeSpan.FromSeconds(30)); return 0; });
        // Far beyond capacity (unlike the Dispose test's +50): Kill's own posted exit has to reach
        // the queue too (see below), and only a producer that is still actively refilling - not
        // one that finished shortly after the drain starts - keeps the tap's lock genuinely
        // contended for long enough to overlap the terminal exit being processed.
        Task producer = Task.Run(() =>
        {
            for (int i = 0; i < HeadlessTerminalSession.DataQueueCapacity + 20_000; i++) fake.Emit("x");
        }, TestContext.Current.CancellationToken);
        await TestWait.UntilAsync(() => mux.QueuedDataCount == HeadlessTerminalSession.DataQueueCapacity, "the data queue is full");

        // Kill (not Dispose): the terminal exit is processed on the parse thread itself
        // (ProcessExit), which is the code path Dispose does not exercise. Kill's own attempt to
        // post the child's exit (via _session.Dispose() -> Exit -> OnExit) must ALSO be stuck
        // behind the full queue before the parked invoke is released - otherwise the drain can
        // race ahead of Kill ever reaching the queue, and the test would prove nothing. Disposed
        // flips true synchronously at the top of ScriptedTerminalSession.Dispose(), just before
        // Exit(-1) reaches the blocking Add, so waiting for it pins Kill in the traffic jam too.
        Task kill = Task.Run(mux.Kill, TestContext.Current.CancellationToken);
        await TestWait.UntilAsync(() => fake.Disposed, "Kill has started disposing the child");

        release.Set();
        await producer.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await kill.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await parked.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        mux.Dispose();
    }

    [Fact]
    public async Task An_attach_after_kill_is_answered_with_session_exited_instead_of_silence()
    {
        (HeadlessTerminalSession mux, _) = NewSession();
        using (mux)
        {
            mux.Kill();
            await TestWait.UntilAsync(() => mux.IsExited, "the kill exit has been processed");

            var sink = new RecordingFrameSink();
            mux.PostAttach(sink, 42, 100, Presentation80x24, MuxProtocol.MaxFrameBytes);
            await TestWait.UntilAsync(() => sink.Described.Count > 0, "the attach is answered");

            Assert.Equal(["Error:session_exited"], sink.Described);
            Assert.Equal(0, mux.AttachedClients);
        }
    }

    [Fact]
    public void A_session_without_the_byte_tap_is_refused()
    {
        Assert.Throws<ArgumentException>(() =>
            new HeadlessTerminalSession(Guid.NewGuid(), new NoTapTerminalSession(), new HeadlessSessionOptions()));
    }

    [Fact]
    public async Task Flight_export_returns_replayable_bytes()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using (mux)
        {
            fake.EnableFlightRecording(1 << 20);
            fake.Emit("hello\r\n");
            fake.Emit("world");
            ExportFlightResult result = mux.ExportFlight();

            Assert.True(result.Exported);
            string path = Path.Combine(Path.GetTempPath(), $"mux-flight-test-{Guid.NewGuid():N}.rec");
            try
            {
                await File.WriteAllBytesAsync(path, result.Bytes!, TestContext.Current.CancellationToken);
                var replayed = new List<byte>();
                await new ReplayRunner(path).RunAsync(data => { replayed.AddRange(data); return Task.CompletedTask; }, ct: TestContext.Current.CancellationToken);
                Assert.Equal("hello\r\nworld", Encoding.UTF8.GetString(replayed.ToArray()));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
