using System.Text.Json;
using Ntilde.CommandAssist.Models;
using Ntilde.CommandAssist.Storage;

namespace Ntilde.Tests.CommandAssist;

public sealed class JsonSnippetStoreFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ntilde_snippet_failures_{Guid.NewGuid():N}");

    public JsonSnippetStoreFailureTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(false, "[{ interrupted snippet document")]
    [InlineData(true, "[{ interrupted snippet document")]
    [InlineData(false, "null")]
    [InlineData(true, "null")]
    public async Task MalformedJsonIsNotReplacedByAMutation(bool remove, string original)
    {
        string path = Path.Combine(_root, "snippets.json");
        await File.WriteAllTextAsync(path, original, TestContext.Current.CancellationToken);
        var store = new JsonSnippetStore(path);

        Exception? failure = await Record.ExceptionAsync(() => MutateAsync(store, remove, TestContext.Current.CancellationToken));

        Assert.Equal(original, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.IsType<JsonException>(failure);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CancellationDuringReadOrWritePreservesTheOriginalFile(bool remove, bool duringWrite)
    {
        var (path, original) = await SeedAsync();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var store = new JsonSnippetStore(path,
            source =>
            {
                if (!duringWrite) cancel.Cancel();
                return File.OpenRead(source);
            },
            destination =>
            {
                var stream = File.Create(destination);
                if (duringWrite) cancel.Cancel();
                return stream;
            });

        Exception? failure = await Record.ExceptionAsync(() => MutateAsync(store, remove, cancel.Token));

        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        Assert.Single(await new JsonSnippetStore(path).GetAllAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadFailureCannotStartAMutation(bool remove)
    {
        var (path, original) = await SeedAsync();
        var store = new JsonSnippetStore(path, _ => throw new IOException("Injected read failure"), File.Create);

        Exception? failure = await Record.ExceptionAsync(() => MutateAsync(store, remove, TestContext.Current.CancellationToken));

        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.IsType<IOException>(failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialWriteFailurePreservesTheOriginalAndCleansUp(bool remove)
    {
        var (path, original) = await SeedAsync();
        var store = new JsonSnippetStore(path, File.OpenRead, destination => new PartialFailureStream(destination));

        Exception? failure = await Record.ExceptionAsync(() => MutateAsync(store, remove, TestContext.Current.CancellationToken));

        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.IsType<IOException>(failure);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task GetAllDoesNotHideCancellationDuringRead()
    {
        var (path, _) = await SeedAsync();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var store = new JsonSnippetStore(path, source =>
        {
            cancel.Cancel();
            return File.OpenRead(source);
        }, File.Create);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetAllAsync(cancel.Token));
    }

    [Fact]
    public async Task SuccessfulSavePreservesRestrictiveUnixPermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix permissions only.");
        }
        else
        {
            var (path, _) = await SeedAsync();
            UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            File.SetUnixFileMode(path, mode);

            await new JsonSnippetStore(path).UpsertAsync(Snippet("new"), TestContext.Current.CancellationToken);

            Assert.Equal(mode, File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public async Task TemporaryFileIsRestrictedBeforeSerializingCommands()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix permissions only.");
        }
        else
        {
            var (path, _) = await SeedAsync();
            UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            File.SetUnixFileMode(path, mode);
            bool observedWrite = false;
            var store = new JsonSnippetStore(path, File.OpenRead, destination =>
            {
                var stream = new ObservedWriteStream(destination, () =>
                {
                    if (!OperatingSystem.IsWindows()) Assert.Equal(mode, File.GetUnixFileMode(destination));
                    observedWrite = true;
                });
                // Force an unsafe initial mode, independent of the test process's umask.
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, mode | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                return stream;
            });

            await store.UpsertAsync(Snippet("new"), TestContext.Current.CancellationToken);

            Assert.True(observedWrite);
            Assert.Equal(mode, File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public async Task WindowsReplacementFailurePreservesTheOriginalAndCleansUp()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows file-sharing semantics only.");
        }
        else
        {
            var (path, original) = await SeedAsync();
            // Reading still works, but this external reader denies deletion/replacement.
            using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            bool writeStarted = false;
            var store = new JsonSnippetStore(path, File.OpenRead, destination =>
            {
                writeStarted = true;
                return File.Create(destination);
            });

            Exception? failure = await Record.ExceptionAsync(() => store.UpsertAsync(Snippet("new"), TestContext.Current.CancellationToken));

            Assert.True(writeStarted);
            Assert.IsAssignableFrom<IOException>(failure);
            Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        }
    }

    private async Task<(string Path, byte[] Bytes)> SeedAsync()
    {
        string path = Path.Combine(_root, "snippets.json");
        await new JsonSnippetStore(path).UpsertAsync(Snippet("old"), TestContext.Current.CancellationToken);
        return (path, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    private static CommandSnippet Snippet(string id) =>
        new(id, id, $"echo {id}", null, null, null, true, DateTimeOffset.UnixEpoch, null);

    private static Task MutateAsync(JsonSnippetStore store, bool remove, CancellationToken token) =>
        remove ? store.RemoveAsync("old", token) : store.UpsertAsync(Snippet("new"), token);

    private sealed class ObservedWriteStream(string path, Action observe) : FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            observe();
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class PartialFailureStream(string path) : FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer[..Math.Min(8, buffer.Length)], cancellationToken);
            throw new IOException("Injected failure after writing part of the document");
        }
    }
}
