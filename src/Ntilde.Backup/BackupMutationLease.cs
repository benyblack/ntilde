using System.Diagnostics;

namespace Ntilde.Backup;

/// <summary>Joins the stable sidecar protocol of the stores whose data an import changes.</summary>
internal sealed class BackupMutationLease : IDisposable
{
    private readonly List<FileStream> _streams = [];

    internal static BackupMutationLease Acquire(string root, IReadOnlyCollection<BackupCategory> categories, TimeSpan timeout, Action<string>? contended)
    {
        var lease = new BackupMutationLease();
        var elapsed = Stopwatch.StartNew();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        string[] paths = BackupCatalog.Entries
            .Where(entry => categories.Contains(entry.Category) && (entry.BundlePath is "connections/profiles.json" or "command-assist/snippets.json"))
            .Select(entry => Path.GetFullPath(BackupCatalog.ResolveSource(root, entry) + ".lock"))
            .Distinct(comparer).OrderBy(path => path, comparer).ToArray();
        try
        {
            foreach (string path in paths)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                bool reported = false;
                while (true)
                {
                    try
                    {
                        // Never truncate or replace these files: peers must keep locking the same inode.
                        lease._streams.Add(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1));
                        break;
                    }
                    catch (IOException error) when (IsSharingViolation(error))
                    {
                        if (!reported) { reported = true; contended?.Invoke(path); }
                        if (elapsed.Elapsed >= timeout)
                            throw new IOException("A selected configuration store is busy. Retry after the other operation completes.", error);
                        Thread.Sleep(20);
                    }
                }
            }
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static bool IsSharingViolation(IOException error) => OperatingSystem.IsWindows()
        ? (error.HResult & 0xffff) is 32 or 33
        : error.HResult is 11 or 35;

    public void Dispose()
    {
        for (int i = _streams.Count - 1; i >= 0; i--) _streams[i].Dispose();
        _streams.Clear();
    }
}
