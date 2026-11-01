using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.Storage;

namespace Ntilde.Platform.Tests.Ssh;

public sealed class JsonSshProfileStoreCoordinationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ntilde_profile_transactions_{Guid.NewGuid():N}");
    private string StorePath => Path.Combine(_root, "profiles.json");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public JsonSshProfileStoreCoordinationTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentMutationsReloadOnlyAfterTheTransactionLease(bool delete)
    {
        var seed = Profile("seed");
        var a = Profile("a");
        var b = Profile("b");
        new JsonSshProfileStore(StorePath).SaveProfile(seed);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var secondRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStore = new JsonSshProfileStore(StorePath, null, path =>
        {
            string snapshot = File.ReadAllText(path);
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5), Ct)) throw new TimeoutException("First transaction was not released.");
            return snapshot;
        }, File.Move);
        var secondStore = new JsonSshProfileStore(StorePath, null, path =>
        {
            secondRead.TrySetResult();
            return File.ReadAllText(path);
        }, File.Move, leaseContended: () => contended.TrySetResult());
        Task first = Task.Run(() =>
        {
            if (delete) firstStore.DeleteProfile(seed.Id);
            else firstStore.SaveProfile(a);
        }, Ct);
        Task second;
        bool readBeforeLease;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), Ct));
            second = Task.Run(() => secondStore.SaveProfile(b), Ct);
            Task observed = await Task.WhenAny(secondRead.Task, contended.Task).WaitAsync(TimeSpan.FromSeconds(5), Ct);
            readBeforeLease = observed == secondRead.Task;
        }
        finally { release.Set(); }
        await Task.WhenAll(first, second);

        var actual = new JsonSshProfileStore(StorePath).GetProfiles().Select(p => p.Name).Order(StringComparer.Ordinal);
        Assert.Equal(delete ? ["b"] : new[] { "a", "b", "seed" }, actual);
        Assert.False(readBeforeLease);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task StaleCorruptionReadCannotQuarantineAnotherWritersFreshStore()
    {
        const string corrupt = "{ corrupt profiles";
        File.WriteAllText(StorePath, corrupt);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int reads = 0;
        int quarantineCalls = 0;
        var reader = new JsonSshProfileStore(StorePath, null, path =>
        {
            string snapshot = File.ReadAllText(path);
            if (Interlocked.Increment(ref reads) == 1)
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5), Ct)) throw new TimeoutException("Corrupt snapshot was not released.");
            }
            return snapshot;
        }, (source, destination) =>
        {
            Interlocked.Increment(ref quarantineCalls);
            File.Move(source, destination + ".reader");
        });
        var saved = Profile("saved after recovery");
        Task<IReadOnlyList<SshProfile>> reading = Task.Run(reader.GetProfiles, Ct);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), Ct));
            new JsonSshProfileStore(StorePath).SaveProfile(saved);
        }
        finally { release.Set(); }
        var actual = await reading;

        Assert.Equal(saved.Id, Assert.Single(actual).Id);
        Assert.Equal(0, quarantineCalls);
        Assert.Equal(saved.Id, Assert.Single(new JsonSshProfileStore(StorePath).GetProfiles()).Id);
        Assert.Equal(corrupt, File.ReadAllText(Assert.Single(Directory.GetFiles(_root, "profiles.json.corrupt.*.json"))));
    }

    [Fact]
    public void ReadOnlyCorruptionRecoveryDoesNotMoveFilesWithoutTheLease()
    {
        const string corrupt = "{ corrupt profiles";
        File.WriteAllText(StorePath, corrupt);
        using var held = new FileStream(StorePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var store = new JsonSshProfileStore(StorePath, null, File.ReadAllText, File.Move,
            leaseTimeout: TimeSpan.FromMilliseconds(50));

        Assert.Empty(store.GetProfiles());

        Assert.Equal(corrupt, File.ReadAllText(StorePath));
        Assert.Empty(Directory.GetFiles(_root, "profiles.json.corrupt.*.json"));
    }

    private static SshProfile Profile(string name) => new() { Name = name, Host = "test.invalid", User = "test" };
}
