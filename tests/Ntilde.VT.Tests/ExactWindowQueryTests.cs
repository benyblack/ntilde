namespace Ntilde.VT.Tests;

public sealed class ExactWindowQueryTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(140, 40)]
    [InlineData(256, 128)]
    public void Character_geometry_reports_rows_then_columns_without_pixel_metrics(int cols, int rows)
    {
        var buffer = new TerminalBuffer(cols, rows);
        var responses = new List<string>();
        var parser = new AnsiParser(buffer) { OnResponse = responses.Add, CellWidth = float.NaN, CellHeight = float.PositiveInfinity };
        parser.Process("\x1b[18t");
        Assert.Equal($"\x1b[8;{rows};{cols}t", Assert.Single(responses));
    }

    [Fact]
    public void Character_geometry_uses_current_resized_buffer_and_requires_no_subscription()
    {
        var buffer = new TerminalBuffer(80, 24);
        var responses = new List<string>();
        var parser = new AnsiParser(buffer) { OnResponse = responses.Add };
        parser.Process("\x1b[18t");
        buffer.Resize(120, 31);
        parser.Process("\x1b[18t");
        Assert.Equal(new[] { "\x1b[8;24;80t", "\x1b[8;31;120t" }, responses);
        Assert.False(parser.InBandResizeReportsEnabled);
    }

    [Fact]
    public async Task Concurrent_resize_waits_for_the_existing_parser_batch_write_lock()
    {
        var buffer = new TerminalBuffer(80, 24);
        using var releaseReply = new ManualResetEventSlim();
        var enteredReply = new TaskCompletionSource<(string Reply, bool WriteLockHeld)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var parser = new AnsiParser(buffer)
        {
            OnResponse = reply =>
            {
                enteredReply.TrySetResult((reply, buffer.Lock.IsWriteLockHeld));
                releaseReply.Wait(TestContext.Current.CancellationToken);
            }
        };
        Task parsing = Task.Factory.StartNew(() => parser.Process("\x1b[18t"),
            TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task? resizing = null;
        try
        {
            (string reply, bool writeLockHeld) = await enteredReply.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.True(writeLockHeld);
            Assert.Equal("\x1b[8;24;80t", reply);
            resizing = Task.Factory.StartNew(() => buffer.Resize(120, 31),
                TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(SpinWait.SpinUntil(() => buffer.Lock.WaitingWriteCount == 1, TimeSpan.FromSeconds(30)),
                "Resize must wait on the parser's existing batch write lock.");
            Assert.False(resizing.IsCompleted);
        }
        finally
        {
            releaseReply.Set();
            await parsing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            if (resizing is not null) await resizing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        var responses = new List<string>();
        parser.OnResponse = responses.Add;
        parser.Process("\x1b[18t");
        Assert.Equal("\x1b[8;31;120t", Assert.Single(responses));
    }

    [Theory]
    [InlineData("\x1b[?18t")]
    [InlineData("\x1b[>18t")]
    [InlineData("\x1b[<18t")]
    [InlineData("\x1b[=18t")]
    [InlineData("\x1b[18$t")]
    [InlineData("\x1b[18 t")]
    [InlineData("\x1b[18;1t")]
    [InlineData("\x1b[18;0t")]
    [InlineData("\x1b[18;t")]
    [InlineData("\x1b[18:1t")]
    [InlineData("\x1b[18:t")]
    [InlineData("\x1b[18?1t")]
    [InlineData("\x1b[18 $t")]
    public void Other_spellings_cannot_alias_the_character_geometry_query(string request)
    {
        var responses = new List<string>();
        new AnsiParser(new TerminalBuffer(80, 24)) { OnResponse = responses.Add }.Process(request);
        Assert.Empty(responses);
    }

    [Fact]
    [Trait("Category", "Replay")]
    public void Every_input_split_preserves_query_framing_and_terminal_state()
    {
        const string request = "\x1b[18t";
        for (int split = 0; split <= request.Length; split++)
        {
            var buffer = new TerminalBuffer(9, 4);
            var responses = new List<string>();
            var parser = new AnsiParser(buffer) { OnResponse = responses.Add };
            parser.Process("\x1b[?1049h\x1b[2;3r\x1b[?6h\x1b[31;4mtext");
            TerminalStateSnapshot before = buffer.ExportState(0);
            parser.Process(request[..split]);
            if (split < request.Length) Assert.Empty(responses);
            parser.Process(request[split..]);
            Assert.Equal("\x1b[8;4;9t", Assert.Single(responses));
            TerminalStateSnapshot after = buffer.ExportState(0);
            Assert.Equal(TerminalStateSerializer.ToBytes(before), TerminalStateSerializer.ToBytes(after));
        }
    }
}
