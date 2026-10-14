namespace Ntilde.Shell.Mux;

/// <summary>The first-close dialog's two answers (spec R1): leave the local shells running, or end them.</summary>
internal enum MuxCloseChoice { Keep, Close }

/// <summary>
/// The remembered answer to the first-close dialog: a flag file under the app-data root, not a setting (R1).
/// Its content is <c>keep</c> or <c>close</c>. "Don't ask again" writes it; saving Settings with a changed
/// session persistence (or an Import/Restore that changes it) deletes it, so turning persistence back on asks
/// again. Deleting it by hand is safe: the next close with live shells asks again. Not backed up: it is a
/// machine-local answer, listed in <c>BackupCatalog.ExcludedRelativePaths</c>.
/// </summary>
internal sealed class MuxCloseChoiceStore
{
    public const string FileName = AppPaths.MuxCloseChoiceFileName;

    private const string KeepText = "keep";
    private const string CloseText = "close";

    private readonly string _directory;

    /// <summary>The store for the flag file under <paramref name="rootDirectory"/> (a test's scratch root).</summary>
    public MuxCloseChoiceStore(string rootDirectory)
        : this(rootDirectory, Path.Combine(rootDirectory, FileName))
    {
    }

    private MuxCloseChoiceStore(string directory, string filePath)
    {
        _directory = directory;
        FilePath = filePath;
    }

    /// <summary>
    /// The store at <see cref="AppPaths.MuxCloseChoiceFilePath"/>, resolved on each access rather than once: the root
    /// follows <c>NTILDE_APPDATA_ROOT</c>, and a store pinned to whichever root was current first could write into a
    /// developer's real profile from a test that had moved it.
    /// </summary>
    public static MuxCloseChoiceStore Default
    {
        get
        {
            string path = AppPaths.MuxCloseChoiceFilePath;
            return new MuxCloseChoiceStore(Path.GetDirectoryName(path)!, path);
        }
    }

    public string FilePath { get; }

    /// <summary>The remembered answer: <c>keep</c> or <c>close</c>, trimmed and ignoring case. Anything else, no file, or a read error is null (ask).</summary>
    public MuxCloseChoice? Read()
    {
        string text;
        try
        {
            if (!File.Exists(FilePath)) return null;
            text = File.ReadAllText(FilePath).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.Log($"[Mux] could not read the remembered close choice ({FilePath}): {ex.Message}");
            return null;
        }

        if (string.Equals(text, KeepText, StringComparison.OrdinalIgnoreCase)) return MuxCloseChoice.Keep;
        if (string.Equals(text, CloseText, StringComparison.OrdinalIgnoreCase)) return MuxCloseChoice.Close;
        return null;
    }

    /// <summary>Remembers <paramref name="choice"/>: written to a temp sibling, then moved over the file. An IO error is logged, not thrown.</summary>
    public void Remember(MuxCloseChoice choice)
    {
        string tmp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(tmp, choice == MuxCloseChoice.Close ? CloseText : KeepText);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.Log($"[Mux] could not remember the close choice ({FilePath}): {ex.Message}");
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Forgets the remembered answer, so the next close with live shells asks again. An IO error is logged, not thrown.</summary>
    public void Forget()
    {
        try
        {
            File.Delete(FilePath); // no-op when absent
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.Log($"[Mux] could not forget the remembered close choice ({FilePath}): {ex.Message}");
        }
    }
}
