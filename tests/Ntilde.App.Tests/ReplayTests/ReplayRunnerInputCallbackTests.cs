using System.Text.Json;
using Ntilde.Replay;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.ReplayTests;

public sealed class ReplayRunnerInputCallbackTests
{
    [Theory]
    [InlineData("QQ==", null, "A")]
    [InlineData("QQ==", "fallback", "A")]
    [InlineData("%%%", "fallback", "fallback")]
    [InlineData(null, "fallback", "fallback")]
    public async Task Input_callback_failure_propagates_once_without_processing_later_events(string? data, string? legacy, string expected)
    {
        string path = Path.GetTempFileName();
        try
        {
            await WriteReplay(path, data, legacy);
            var seen = new List<string>();
            var failure = new IOException("synthetic input callback failure");
            int times = 0;
            int laterData = 0;
            IOException actual = await Assert.ThrowsAsync<IOException>(() => new ReplayRunner(path).RunWithResultAsync(
                _ => { laterData++; return Task.CompletedTask; },
                onInputCallback: input => { seen.Add(input); return Task.FromException(failure); },
                onTimeUpdate: _ => { times++; return Task.CompletedTask; },
                options: new ReplayRunOptions { PlaybackMode = ReplayPlaybackMode.Virtual },
                ct: TestContext.Current.CancellationToken));
            Assert.Same(failure, actual);
            Assert.Equal(new[] { expected }, seen);
            Assert.Equal(0, times);
            Assert.Equal(0, laterData);
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("QQ==", "fallback", "A")]
    [InlineData("%%%", "fallback", "fallback")]
    [InlineData("%%%", "", null)]
    [InlineData(null, "fallback", "fallback")]
    [InlineData(null, null, null)]
    [InlineData(" ", "fallback", "")]
    public async Task Input_selection_preserves_valid_decoding_and_genuine_legacy_fallback(string? data, string? legacy, string? expected)
    {
        string path = Path.GetTempFileName();
        try
        {
            await WriteReplay(path, data, legacy);
            var seen = new List<string>();
            ReplayRunResult result = await new ReplayRunner(path).RunWithResultAsync(_ => Task.CompletedTask,
                onInputCallback: input => { seen.Add(input); return Task.CompletedTask; },
                options: new ReplayRunOptions { PlaybackMode = ReplayPlaybackMode.Virtual },
                ct: TestContext.Current.CancellationToken);
            Assert.Equal(expected is null ? Array.Empty<string>() : new[] { expected }, seen);
            Assert.Equal(2, result.EventsProcessed);
            Assert.False(result.Truncated);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Task WriteReplay(string path, string? data, string? legacy) =>
        File.WriteAllLinesAsync(path, new[]
        {
            JsonSerializer.Serialize(new ReplayHeader { Type = ReplayHeader.TypeToken, Cols = 80, Rows = 24 }, ReplayJsonContext.Default.ReplayHeader),
            JsonSerializer.Serialize(new ReplayEvent { Type = "input", Data = data, Input = legacy }, ReplayJsonContext.Default.ReplayEvent),
            "{\"t\":1,\"type\":\"data\",\"d\":\"Qg==\"}"
        }, TestContext.Current.CancellationToken);
}
