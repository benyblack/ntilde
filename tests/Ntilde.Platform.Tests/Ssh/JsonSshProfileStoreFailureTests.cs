using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.Storage;

namespace Ntilde.Platform.Tests.Ssh;

public sealed class JsonSshProfileStoreFailureTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ReadFailure_BlocksMutation_PreservesBytes_AndAllowsRetry(bool delete, bool denied)
    {
        using var fixture = new StoreFixture();
        bool failRead = true;
        Exception failure = denied ? new UnauthorizedAccessException("Injected access denial")
            : new IOException("Injected transient read failure");
        var store = new JsonSshProfileStore(fixture.Path, null,
            path => failRead ? throw failure : File.ReadAllText(path), File.Move);

        // Listings retain their tolerant fallback, but cannot authorize a later write.
        Assert.Empty(store.GetProfiles());
        Assert.Null(store.GetProfile(fixture.First.Id));
        var error = Assert.Throws<IOException>(() => Mutate(store, fixture, delete));
        Assert.Same(failure, error.InnerException);
        Assert.Contains(fixture.Path, error.Message, StringComparison.Ordinal);
        Assert.Equal(fixture.OriginalBytes, File.ReadAllBytes(fixture.Path));
        Assert.Single(Directory.GetFiles(fixture.Root));

        failRead = false;
        Mutate(store, fixture, delete);
        var profiles = new JsonSshProfileStore(fixture.Path).GetProfiles();
        if (delete) Assert.Empty(profiles);
        else
        {
            Assert.Equal(2, profiles.Count);
            Assert.Contains(profiles, p => p.Id == fixture.First.Id);
            Assert.Contains(profiles, p => p.Id == fixture.Second.Id);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExclusivelyLockedStore_BlocksMutation_WithoutReplacingTheFile(bool delete)
    {
        using var fixture = new StoreFixture();
        var store = new JsonSshProfileStore(fixture.Path);
        using (var held = new FileStream(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => Mutate(store, fixture, delete));
        }
        Assert.Equal(fixture.OriginalBytes, File.ReadAllBytes(fixture.Path));
        Assert.Equal(fixture.First.Id, Assert.Single(store.GetProfiles()).Id);
        Assert.Single(Directory.GetFiles(fixture.Root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedQuarantine_BlocksMutation_AndPreservesCorruptBytes(bool delete)
    {
        using var fixture = new StoreFixture();
        const string corrupt = "{ corrupt profiles";
        File.WriteAllText(fixture.Path, corrupt);
        var failure = new IOException("Injected quarantine move failure");
        var store = new JsonSshProfileStore(fixture.Path, null, File.ReadAllText,
            (_, _) => throw failure);

        Assert.Empty(store.GetProfiles());
        var error = Assert.Throws<IOException>(() => Mutate(store, fixture, delete));
        Assert.Same(failure, error.InnerException);
        Assert.Equal(corrupt, File.ReadAllText(fixture.Path));
        Assert.Single(Directory.GetFiles(fixture.Root));
    }

    [Theory]
    [InlineData("{ corrupt profiles")]
    [InlineData("null")]
    public void SuccessfulQuarantine_PreservesCorruptContents_BeforeCreatingReplacement(string corrupt)
    {
        using var fixture = new StoreFixture();
        File.WriteAllText(fixture.Path, corrupt);
        var store = new JsonSshProfileStore(fixture.Path);
        store.SaveProfile(fixture.Second);

        string backup = Assert.Single(Directory.GetFiles(fixture.Root, "profiles.json.corrupt.*.json"));
        Assert.Equal(corrupt, File.ReadAllText(backup));
        Assert.Equal(fixture.Second.Id, Assert.Single(store.GetProfiles()).Id);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp"));
    }

    [Fact]
    public void MissingStore_AllowsCreation_AndDeleteDoesNotCreateAFile()
    {
        using var fixture = new StoreFixture();
        string missing = System.IO.Path.Combine(fixture.Root, "missing", "profiles.json");
        var store = new JsonSshProfileStore(missing);
        Assert.Empty(store.GetProfiles());
        Assert.False(store.DeleteProfile(fixture.First.Id));
        Assert.False(File.Exists(missing));
        store.SaveProfile(fixture.Second);
        Assert.Equal(fixture.Second.Id, Assert.Single(store.GetProfiles()).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Save_DoesNotReplaceAStoreThatAppearsAfterEmptyRecovery(bool corruptRecovery)
    {
        using var fixture = new StoreFixture();
        if (corruptRecovery) File.WriteAllText(fixture.Path, "{ corrupt profiles");
        else File.Delete(fixture.Path);

        string Read(string path)
        {
            if (corruptRecovery) return File.ReadAllText(path);
            // Simulate another writer creating its store immediately after our open
            // observed absence, before our read-modify-write reaches persistence.
            File.WriteAllBytes(path, fixture.OriginalBytes);
            throw new FileNotFoundException("Observed absence before another writer created the file.", path);
        }
        void Quarantine(string source, string destination)
        {
            File.Move(source, destination);
            File.WriteAllBytes(source, fixture.OriginalBytes);
        }
        var store = new JsonSshProfileStore(fixture.Path, null, Read, Quarantine);
        Assert.Throws<IOException>(() => store.SaveProfile(fixture.Second));
        Assert.Equal(fixture.OriginalBytes, File.ReadAllBytes(fixture.Path));
        Assert.Equal(fixture.First.Id, Assert.Single(new JsonSshProfileStore(fixture.Path).GetProfiles()).Id);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp"));
    }

    private static void Mutate(JsonSshProfileStore store, StoreFixture fixture, bool delete)
    {
        if (delete) Assert.True(store.DeleteProfile(fixture.First.Id));
        else store.SaveProfile(fixture.Second);
    }

    private sealed class StoreFixture : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ntilde_ssh_failure_{Guid.NewGuid():N}");
        public string Path => System.IO.Path.Combine(Root, "profiles.json");
        public SshProfile First { get; } = new() { Name = "first", Host = "first.example" };
        public SshProfile Second { get; } = new() { Name = "second", Host = "second.example" };
        public byte[] OriginalBytes { get; }

        public StoreFixture()
        {
            Directory.CreateDirectory(Root);
            new JsonSshProfileStore(Path).SaveProfile(First);
            OriginalBytes = File.ReadAllBytes(Path);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
