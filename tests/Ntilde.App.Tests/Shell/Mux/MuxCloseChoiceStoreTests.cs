using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Shell.Mux;

/// <summary>R1: the first-close dialog's remembered answer is a flag file under the app-data root, not a setting.</summary>
public sealed class MuxCloseChoiceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ntilde_close_choice_{Guid.NewGuid():N}");

    public MuxCloseChoiceStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string FlagPath => Path.Combine(_root, MuxCloseChoiceStore.FileName);

    [Fact]
    public void Nothing_remembered_reads_as_null()
    {
        Assert.Null(new MuxCloseChoiceStore(_root).Read());
    }

    [Theory]
    [InlineData(true, "keep")]
    [InlineData(false, "close")]
    public void Remember_read_and_forget_round_trip(bool keep, string content)
    {
        MuxCloseChoice choice = keep ? MuxCloseChoice.Keep : MuxCloseChoice.Close;
        var store = new MuxCloseChoiceStore(_root);

        store.Remember(choice);

        Assert.Equal(content, File.ReadAllText(FlagPath));
        Assert.Equal(choice, store.Read());
        Assert.Equal(choice, new MuxCloseChoiceStore(_root).Read()); // read from disk, not cached
        Assert.Equal([FlagPath], Directory.GetFiles(_root)); // no temp or .bak sibling left behind

        store.Forget();

        Assert.False(File.Exists(FlagPath));
        Assert.Null(store.Read());
    }

    [Fact]
    public void A_second_answer_replaces_the_first()
    {
        var store = new MuxCloseChoiceStore(_root);

        store.Remember(MuxCloseChoice.Keep);
        store.Remember(MuxCloseChoice.Close);

        Assert.Equal(MuxCloseChoice.Close, store.Read());
    }

    [Theory]
    [InlineData("  KEEP \r\n", true)]
    [InlineData("Close\n", false)]
    public void Reading_is_trimmed_and_ignores_case(string content, bool keep)
    {
        File.WriteAllText(FlagPath, content);

        Assert.Equal(keep ? MuxCloseChoice.Keep : MuxCloseChoice.Close, new MuxCloseChoiceStore(_root).Read());
    }

    [Theory]
    [InlineData("")]
    [InlineData("yes")]
    [InlineData("keep close")]
    [InlineData("detach")]
    public void Garbage_reads_as_null(string content)
    {
        File.WriteAllText(FlagPath, content);

        Assert.Null(new MuxCloseChoiceStore(_root).Read());
    }

    [Fact]
    public void Forgetting_with_nothing_remembered_is_harmless()
    {
        new MuxCloseChoiceStore(_root).Forget();

        Assert.False(File.Exists(FlagPath));
    }

    /// <summary>A root that cannot hold the file (here: it is a file itself) costs a log line, never an exception.</summary>
    [Fact]
    public void An_unwritable_root_does_not_throw()
    {
        string notADirectory = Path.Combine(_root, "a-file");
        File.WriteAllText(notADirectory, "x");
        var store = new MuxCloseChoiceStore(notADirectory);

        store.Remember(MuxCloseChoice.Close);
        store.Forget();

        Assert.Null(store.Read());
        Assert.Equal("x", File.ReadAllText(notADirectory));
    }

    [Fact]
    public void The_default_store_lives_at_the_app_data_root()
    {
        Assert.Equal("mux-close-choice", MuxCloseChoiceStore.FileName);
        Assert.Equal(Path.Combine(Ntilde.Shell.AppPaths.RootDirectory, MuxCloseChoiceStore.FileName), MuxCloseChoiceStore.Default.FilePath);
    }
}
