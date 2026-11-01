using System.Diagnostics;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.Storage;

namespace Ntilde.Platform.Tests.Ssh;

public sealed class JsonSshProfileStoreProcessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ntilde_profile_process_{Guid.NewGuid():N}");
    private string StorePath => Path.Combine(_root, "profiles.json");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public JsonSshProfileStoreProcessTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task ConcurrentProcessesPreserveAllSuccessfulSavesAndDeletionAcrossRestart()
    {
        await using var a = await Worker.StartAsync(StorePath);
        await using var b = await Worker.StartAsync(StorePath);
        var ids = Enumerable.Range(0, 40).Select(_ => Guid.NewGuid()).ToArray();
        for (int i = 0; i < 20; i++)
        {
            await Task.WhenAll(a.SendAsync($"save\t{ids[2 * i]:N}\ta-{i}"), b.SendAsync($"save\t{ids[2 * i + 1]:N}\tb-{i}"));
        }
        string expected = "ids" + string.Concat(ids.Select(id => "\t" + id.ToString("N")).Order(StringComparer.Ordinal));
        Assert.Equal(expected, await a.SendAsync("list"));
        Assert.Equal(expected, await b.SendAsync("list"));
        Assert.Equal("ok", await a.SendAsync($"delete\t{ids[0]:N}"));
        Guid added = Guid.NewGuid();
        Assert.Equal("ok", await b.SendAsync($"save\t{added:N}\tafter-delete"));
        string remaining = await b.SendAsync("list");
        Assert.DoesNotContain(ids[0].ToString("N"), remaining.Split('\t')[1..]);
        Assert.Equal(40, remaining.Split('\t')[1..].Length);
        await a.DisposeAsync();
        await using var restarted = await Worker.StartAsync(StorePath);
        Assert.Equal(remaining, await restarted.SendAsync("list"));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        Assert.Empty(File.ReadAllBytes(StorePath + ".lock"));
    }

    [Fact]
    public async Task AForeignProcessLeasePrecedesTheReadAndItsDeathUnblocksTheMutation()
    {
        var seed = new SshProfile { Name = "seed", Host = "test.invalid" };
        new JsonSshProfileStore(StorePath).SaveProfile(seed);
        var contended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        var store = new JsonSshProfileStore(StorePath, null, path =>
        {
            Interlocked.Increment(ref reads);
            return File.ReadAllText(path);
        }, File.Move, leaseContended: () => contended.TrySetResult());
        await using var holder = await Worker.StartAsync(StorePath, holdLock: true);
        var added = new SshProfile { Name = "after-crash", Host = "test.invalid" };
        Task mutation = Task.Run(() => store.SaveProfile(added), Ct);
        bool observedContention;
        int readsBeforeRelease;
        try
        {
            Task observed = await Task.WhenAny(mutation, contended.Task).WaitAsync(TimeSpan.FromSeconds(5), Ct);
            observedContention = observed == contended.Task;
            readsBeforeRelease = Volatile.Read(ref reads);
            Assert.Equal(seed.Id, Assert.Single(new JsonSshProfileStore(StorePath).GetProfiles()).Id);
        }
        finally { await holder.CrashAsync(); }
        await mutation;

        Assert.True(observedContention);
        Assert.Equal(0, readsBeforeRelease);
        Assert.Equal(2, store.GetProfiles().Count);
    }

    [Fact]
    public async Task BusySynchronousMutationFailsBeforeReadingAndLeavesBytesIntact()
    {
        var seed = new SshProfile { Name = "seed", Host = "test.invalid" };
        new JsonSshProfileStore(StorePath).SaveProfile(seed);
        byte[] original = await File.ReadAllBytesAsync(StorePath, Ct);
        int reads = 0;
        var store = new JsonSshProfileStore(StorePath, null, path =>
        {
            Interlocked.Increment(ref reads);
            return File.ReadAllText(path);
        }, File.Move, leaseTimeout: TimeSpan.FromMilliseconds(100));
        await using var holder = await Worker.StartAsync(StorePath, holdLock: true);

        var error = Assert.Throws<IOException>(() => store.DeleteProfile(seed.Id));

        Assert.Contains(StorePath, error.Message, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref reads));
        Assert.Equal(original, await File.ReadAllBytesAsync(StorePath, Ct));
        Assert.Equal(seed.Id, Assert.Single(store.GetProfiles()).Id);
        await holder.CrashAsync();
        Assert.True(store.DeleteProfile(seed.Id));
        Assert.Empty(store.GetProfiles());
    }

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

        internal static async Task<Worker> StartAsync(string path, bool holdLock = false)
        {
            string dll = Path.Combine(AppContext.BaseDirectory, "ProfileWorker", "Ntilde.Platform.TestHost.dll");
            Assert.True(File.Exists(dll), $"Profile test worker was not staged: {dll}");
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(dll);
            start.ArgumentList.Add(path);
            if (holdLock) start.ArgumentList.Add("--hold-lock");
            var process = new Process { StartInfo = start };
            try
            {
                Assert.True(process.Start());
                var worker = new Worker(process);
                try { Assert.Equal("ready", await worker.ReadAsync()); return worker; }
                catch { await worker.DisposeAsync(); throw; }
            }
            catch { process.Dispose(); throw; }
        }

        internal async Task<string> SendAsync(string command)
        {
            await _process.StandardInput.WriteLineAsync(command.AsMemory(), Ct);
            await _process.StandardInput.FlushAsync(Ct);
            return await ReadAsync();
        }

        private async Task<string> ReadAsync()
        {
            string? line = await _process.StandardOutput.ReadLineAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
            return line ?? throw new InvalidOperationException("Profile worker ended without a response: " + await _stderr.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        }

        internal async Task CrashAsync()
        {
            _crashed = true;
            if (!_process.HasExited)
            {
                try { _process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* Exited between check and kill. */ }
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
