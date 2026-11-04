using System.Text.Json;
using Ntilde.Replay;
using Ntilde.VT;

namespace Ntilde.Tests.ReplayTests;

[Trait("Category", "Replay")]
public sealed class ReplayValidatedStartTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Validated_preparation_precedes_line_and_byte_resumes(bool byteResume)
    {
        string path = Path.GetTempFileName();
        try
        {
            string header = JsonSerializer.Serialize(new ReplayHeader { Type = ReplayHeader.TypeToken }, ReplayJsonContext.Default.ReplayHeader);
            const string first = "{\"t\":0,\"type\":\"data\",\"d\":\"QQ==\"}";
            await File.WriteAllTextAsync(path, header + "\n" + first + "\n{\"t\":1,\"type\":\"data\",\"d\":\"Qg==\"}\n", TestContext.Current.CancellationToken);
            var order = new List<string>();
            var data = new List<byte>();
            await new ReplayRunner(path).RunWithResultAsync(bytes => { order.Add("data"); data.AddRange(bytes); return Task.CompletedTask; },
                options: new ReplayRunOptions
                {
                    StartAfterLineNumber = byteResume ? 0 : 2,
                    OnSkippedData = _ => order.Add("skip"),
                    OnValidatedStart = _ => { order.Add("prepare"); return Task.FromResult(true); },
                },
                startOffsetBytes: byteResume ? System.Text.Encoding.UTF8.GetByteCount(header + "\n" + first + "\n") : 0,
                ct: TestContext.Current.CancellationToken);
            Assert.Equal(new byte[] { (byte)'B' }, data);
            Assert.Equal(byteResume ? new[] { "prepare", "data" } : new[] { "prepare", "skip", "data" }, order);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preparation_precedes_header_and_data_callbacks_for_legacy_and_v2(bool legacy)
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, Recording(legacy), TestContext.Current.CancellationToken);
            var calls = new List<string>();
            await new ReplayRunner(path).RunWithResultAsync(_ => { calls.Add("data"); return Task.CompletedTask; },
                onResizeCallback: (_, _) => { calls.Add("resize"); return Task.CompletedTask; },
                onHeaderCallback: _ => { calls.Add("header"); return Task.CompletedTask; },
                options: new ReplayRunOptions { OnValidatedStart = token => { Assert.Equal(TestContext.Current.CancellationToken, token); calls.Add("prepare"); return Task.FromResult(true); } },
                ct: TestContext.Current.CancellationToken);
            Assert.Equal(legacy ? new[] { "prepare", "data" } : new[] { "prepare", "resize", "header", "data" }, calls);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_declined_start_delivers_no_callbacks_or_skipped_data_and_closes_the_file(bool legacy, bool resume)
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, Recording(legacy), TestContext.Current.CancellationToken);
            int calls = 0;
            var result = await new ReplayRunner(path).RunWithResultAsync(_ => { calls++; return Task.CompletedTask; },
                onResizeCallback: (_, _) => { calls++; return Task.CompletedTask; },
                onHeaderCallback: _ => { calls++; return Task.CompletedTask; },
                options: new ReplayRunOptions { StartAfterLineNumber = resume ? 2 : 0, OnSkippedData = _ => calls++, OnValidatedStart = _ => Task.FromResult(false) },
                ct: TestContext.Current.CancellationToken);
            Assert.Equal(0, calls);
            Assert.Equal(0, result.EventsProcessed);
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Callback_cancellation_or_failure_closes_the_reader_without_data(bool cancel)
    {
        string path = Path.GetTempFileName();
        using var cts = new CancellationTokenSource();
        try
        {
            await File.WriteAllTextAsync(path, Recording(false), TestContext.Current.CancellationToken);
            int calls = 0;
            var run = new ReplayRunner(path).RunWithResultAsync(_ => { calls++; return Task.CompletedTask; },
                options: new ReplayRunOptions
                {
                    OnValidatedStart = token =>
                {
                    if (!cancel) throw new InvalidOperationException("host declined unexpectedly");
                    cts.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(true);
                }
                }, ct: cts.Token);
            if (cancel) await run;
            else await Assert.ThrowsAsync<InvalidOperationException>(() => run);
            Assert.Equal(0, calls);
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task The_held_reader_preserves_live_append_during_validated_preparation()
    {
        string path = Path.GetTempFileName();
        try
        {
            string header = JsonSerializer.Serialize(new ReplayHeader { Type = ReplayHeader.TypeToken }, ReplayJsonContext.Default.ReplayHeader);
            await File.WriteAllTextAsync(path, header + "\n", TestContext.Current.CancellationToken);
            var received = new List<byte>();
            await new ReplayRunner(path).RunWithResultAsync(data => { received.AddRange(data); return Task.CompletedTask; },
                options: new ReplayRunOptions { OnValidatedStart = async token => { await File.AppendAllTextAsync(path, "{\"t\":0,\"type\":\"data\",\"d\":\"WA==\"}\n", token); return true; } },
                ct: TestContext.Current.CancellationToken);
            Assert.Equal(new byte[] { (byte)'X' }, received);
        }
        finally { File.Delete(path); }
    }

    private static string Recording(bool legacy) =>
        (legacy ? "" : JsonSerializer.Serialize(new ReplayHeader { Type = ReplayHeader.TypeToken }, ReplayJsonContext.Default.ReplayHeader) + "\n") +
        "{\"t\":0,\"type\":\"data\",\"d\":\"WA==\"}\n";
}
