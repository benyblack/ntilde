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
