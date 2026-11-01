using System.Diagnostics;
using System.Globalization;
using Ntilde.CommandAssist.Models;
using Ntilde.CommandAssist.Storage;

namespace Ntilde.Tests.CommandAssist;

public sealed class JsonlHistoryProcessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ntilde_history_process_{Guid.NewGuid():N}");
    private string HistoryPath => Path.Combine(_root, "history.jsonl");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public JsonlHistoryProcessTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task TwoProcesses_RefreshBeforeCompaction_ObserveClear_AndPreserveHistoryAfterRestart()
    {
        await using var a = await Worker.StartAsync(HistoryPath);
        await using var b = await Worker.StartAsync(HistoryPath);
        var observed = new List<string>();
        await a.AppendAsync("one", 1);
        observed.Add(await b.SendAsync("recent"));
        await a.AppendAsync("two", 2);
        observed.Add(await b.SendAsync("cap\t2"));
        await a.SendAsync("clear");
        observed.Add(await b.SendAsync("recent"));
        await b.AppendAsync("three", 3);
        observed.Add(await b.SendAsync("cap\t3"));

        await a.DisposeAsync();
        await using var restarted = await Worker.StartAsync(HistoryPath);
        observed.Add(await restarted.SendAsync("recent"));
        await restarted.AppendAsync("four", 4);
        await b.AppendAsync("five", 5);
        observed.Add(await restarted.SendAsync("cap\t2"));
        observed.Add(await b.SendAsync("recent"));

        string[] expected = ["ids\tone", "ids\ttwo\tone", "ids", "ids\tthree", "ids\tthree", "ids\tfive\tfour", "ids\tfive\tfour"];
        Assert.True(expected.SequenceEqual(observed), "Cross-process observations:\n" + string.Join("\n", observed));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task ConcurrentProcessAppends_AreAllVisibleToBothCachedReaders()
    {
        await using var a = await Worker.StartAsync(HistoryPath);
        await using var b = await Worker.StartAsync(HistoryPath);
        for (int i = 0; i < 20; i++)
        {
            await Task.WhenAll(a.AppendAsync($"a-{i}", 2 * i), b.AppendAsync($"b-{i}", 2 * i + 1));
        }
        string[] first = (await a.SendAsync("recent")).Split('\t')[1..];
        string[] second = (await b.SendAsync("recent")).Split('\t')[1..];
        Assert.Equal(40, first.Length);
        Assert.Equal(40, first.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task WaitingForAnotherProcess_CanBeCancelled_AndItsDeathReleasesTheLease()
    {
        var store = new JsonlHistoryStore(HistoryPath, 50);
        await store.AppendAsync(Entry("first"), Ct);
        await using var holder = await Worker.StartAsync(HistoryPath, holdLock: true);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.AppendAsync(Entry("cancelled"), cancel.Token));
        await holder.CrashAsync();
        await store.AppendAsync(Entry("after-crash"), Ct);
        Assert.Equal(2, (await store.GetRecentAsync(10, Ct)).Count);
        Assert.DoesNotContain(await store.GetRecentAsync(10, Ct), entry => entry.Id == "cancelled");
    }

    private static CommandHistoryEntry Entry(string id) => new(
        id, "echo " + id, DateTimeOffset.UtcNow, "bash", null, null, null, null, null,
        false, false, CommandCaptureSource.ShellIntegration, null);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Worker : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _stderr;
        private bool _disposed;
        private bool _crashed;

        private Worker(Process process)
        {
            _process = process;
            _stderr = process.StandardError.ReadToEndAsync();
        }

        internal static async Task<Worker> StartAsync(string historyPath, bool holdLock = false)
        {
            string dll = Path.Combine(AppContext.BaseDirectory, "HistoryWorker", "Ntilde.CommandAssist.TestHost.dll");
            Assert.True(File.Exists(dll), $"History test worker was not staged: {dll}");
            string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            var start = new ProcessStartInfo(dotnet)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add(dll);
            start.ArgumentList.Add(historyPath);
            if (holdLock) start.ArgumentList.Add("--hold-lock");
            var process = new Process { StartInfo = start };
            try
            {
                Assert.True(process.Start());
                var worker = new Worker(process);
                try
                {
                    Assert.Equal("ready", await worker.ReadAsync());
                    return worker;
                }
                catch
                {
                    await worker.DisposeAsync();
                    throw;
                }
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }

        internal Task<string> AppendAsync(string id, long seconds)
            => SendAsync($"append\t{id}\t{(1_700_000_000 + seconds).ToString(CultureInfo.InvariantCulture)}\techo {id}");

        internal async Task<string> SendAsync(string command)
        {
            await _process.StandardInput.WriteLineAsync(command.AsMemory(), Ct);
            await _process.StandardInput.FlushAsync(Ct);
            return await ReadAsync();
        }

        private async Task<string> ReadAsync()
        {
            string? line = await _process.StandardOutput.ReadLineAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
            if (line != null) return line;
            throw new InvalidOperationException("History worker ended without a response: " + await _stderr.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        }

        internal async Task CrashAsync()
        {
            _crashed = true;
            if (!_process.HasExited)
            {
                try { _process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* Exited between the check and kill. */ }
            }
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (!_process.HasExited)
                {
                    _process.StandardInput.Close();
                    try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (TimeoutException) { await CrashAsync(); }
                }
                string errors = await _stderr.WaitAsync(TimeSpan.FromSeconds(5));
                if (!_crashed) Assert.True(_process.ExitCode == 0, errors);
            }
            finally { _process.Dispose(); }
        }
    }
}
