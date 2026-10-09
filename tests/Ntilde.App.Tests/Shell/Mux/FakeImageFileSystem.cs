using System.Security.Cryptography;
using System.Text;
using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Shell.Mux;

/// <summary>
/// An in-memory <see cref="IMuxImageFileSystem"/> for <see cref="MuxDaemonImage"/> and <see cref="MuxUninstall"/>: files
/// hold text, and every write is recorded in order.
/// </summary>
internal sealed class FakeImageFileSystem : IMuxImageFileSystem
{
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;
    private readonly Dictionary<string, string> _files = new(Cmp);
    private readonly HashSet<string> _dirs = new(Cmp);
    private readonly Dictionary<string, DateTime> _times = new(Cmp);

    public List<string> Operations { get; } = new();
    public List<(string Source, string Destination)> Copies { get; } = new();

    /// <summary>Files a process holds (a running image): deleting one, or a folder holding one, fails.</summary>
    public HashSet<string> InUse { get; } = new(Cmp);

    /// <summary>Folders that are junctions or symbolic links: what is "in" them is another folder's.</summary>
    public HashSet<string> ReparsePoints { get; } = new(Cmp);

    /// <summary>Every path a hash was asked for, in order.</summary>
    public List<string> Hashed { get; } = new();

    public Func<string, Exception?>? FailCopy { get; set; }
    public Action<string, string>? BeforeMove { get; set; }
    public Action<string>? BeforeDeleteFile { get; set; }

    /// <summary>A file or folder that cannot be deleted right now (an exiting process still holds it): the exception to throw.</summary>
    public Func<string, Exception?>? FailDelete { get; set; }

    public void AddFile(string path, string content)
    {
        CreateDirectory(Path.GetDirectoryName(path)!);
        _files[path] = content;
    }

    public void Remove(string path) => _files.Remove(path);

    public void SetLastWriteTimeUtc(string path, DateTime time) => _times[path] = time;

    public bool FileExists(string path) => _files.ContainsKey(path);

    public bool DirectoryExists(string path) => _dirs.Contains(path);

    public bool IsReparsePoint(string path) => ReparsePoints.Contains(path);

    public long GetFileLength(string path) =>
        _files.TryGetValue(path, out string? c) ? c.Length : throw new FileNotFoundException(path);

    public string Sha256(string path)
    {
        Hashed.Add(path);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ReadAllText(path))));
    }

    public IReadOnlyList<string> GetFiles(string directory, string searchPattern)
    {
        if (!DirectoryExists(directory)) throw new DirectoryNotFoundException(directory);
        Operations.Add($"list {directory}");
        string? extension = searchPattern == "*" ? null : searchPattern.TrimStart('*');
        return _files.Keys
            .Where(f => Cmp.Equals(Path.GetDirectoryName(f), directory) && (extension is null || f.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public IReadOnlyList<string> GetDirectories(string directory)
    {
        if (!DirectoryExists(directory)) throw new DirectoryNotFoundException(directory);
        return _dirs.Where(d => Cmp.Equals(Path.GetDirectoryName(d), directory)).ToList();
    }

    public DateTime GetLastWriteTimeUtc(string path) => _times.TryGetValue(path, out DateTime t) ? t : DateTime.UtcNow;

    public void CreateDirectory(string path)
    {
        for (string? d = path; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d)) _dirs.Add(d);
    }

    public void CopyFile(string source, string destination)
    {
        if (FailCopy?.Invoke(destination) is { } failure) throw failure;
        if (!_files.TryGetValue(source, out string? content)) throw new FileNotFoundException(source);
        if (!DirectoryExists(Path.GetDirectoryName(destination)!)) throw new DirectoryNotFoundException(destination);
        if (_files.ContainsKey(destination)) throw new IOException($"{destination} exists");
        _files[destination] = content;
        Copies.Add((source, destination));
        Operations.Add($"copy {destination}");
    }

    public string ReadAllText(string path) => _files.TryGetValue(path, out string? c) ? c : throw new FileNotFoundException(path);

    public void WriteAllText(string path, string contents)
    {
        if (!DirectoryExists(Path.GetDirectoryName(path)!)) throw new DirectoryNotFoundException(path);
        _files[path] = contents;
        Operations.Add($"write {path}");
    }

    public void MoveDirectory(string source, string destination)
    {
        BeforeMove?.Invoke(source, destination);
        if (!DirectoryExists(source)) throw new DirectoryNotFoundException(source);
        if (DirectoryExists(destination) || FileExists(destination)) throw new IOException($"Cannot create '{destination}' because a file or directory with the same name already exists.");
        foreach (string f in Under(_files.Keys, source)) { _files[destination + f[source.Length..]] = _files[f]; _files.Remove(f); }
        foreach (string d in Under(_dirs, source)) { _dirs.Add(destination + d[source.Length..]); _dirs.Remove(d); }
        _dirs.Remove(source);
        _dirs.Add(destination);
        Operations.Add($"move {source} -> {destination}");
    }

    public void DeleteFile(string path)
    {
        BeforeDeleteFile?.Invoke(path);
        Refuse(path);
        _files.Remove(path);
        Operations.Add($"delete {path}");
    }

    /// <summary>
    /// As Windows' recursive delete does: the entries in name order, files and folders alike, each folder's contents
    /// before itself; the first that cannot go stops it there, and what came before it is gone.
    /// </summary>
    public void DeleteDirectory(string path)
    {
        if (!DirectoryExists(path)) throw new DirectoryNotFoundException(path);
        DeleteTree(path);
        Operations.Add($"rmdir {path}");
    }

    private void DeleteTree(string directory)
    {
        List<string> entries = _files.Keys.Concat(_dirs)
            .Where(e => Cmp.Equals(Path.GetDirectoryName(e), directory))
            .OrderBy(e => Path.GetFileName(e), StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (string entry in entries)
        {
            if (_dirs.Contains(entry))
            {
                DeleteTree(entry);
            }
            else
            {
                Refuse(entry);
                _files.Remove(entry);
            }
        }

        Refuse(directory);
        _dirs.Remove(directory);
    }

    private void Refuse(string path)
    {
        if (InUse.Contains(path)) throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
        if (FailDelete?.Invoke(path) is { } failure) throw failure;
    }

    private static List<string> Under(IEnumerable<string> paths, string directory) =>
        paths.Where(p => p.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList();
}
