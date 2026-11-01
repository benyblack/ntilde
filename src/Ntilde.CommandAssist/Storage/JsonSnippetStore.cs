using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ntilde.CommandAssist.Domain;
using Ntilde.CommandAssist.Models;

namespace Ntilde.CommandAssist.Storage;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Its only disposable is a SemaphoreSlim gate, which holds no handle unless AvailableWaitHandle is read " +
                    "(nothing here reads it), and disposing it under a caller still waiting would fault that caller.")]
public sealed class JsonSnippetStore : ISnippetStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<string, Stream> _openRead;
    private readonly Func<string, Stream> _createWrite;

    public JsonSnippetStore(string filePath) : this(filePath,
        path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete),
        null)
    {
    }

    internal JsonSnippetStore(string filePath, Func<string, Stream> openRead, Func<string, Stream>? createWrite)
    {
        _filePath = Path.GetFullPath(filePath);
        _openRead = openRead;
        _createWrite = createWrite ?? CreateTemporaryFile;
    }

    public async Task<IReadOnlyList<CommandSnippet>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadSnippetsUnsafeAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertAsync(CommandSnippet snippet, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var lease = await EnterMutationAsync(cancellationToken);
            List<CommandSnippet> snippets = await LoadSnippetsUnsafeAsync(cancellationToken);
            int existingIndex = snippets.FindIndex(x => x.Id == snippet.Id);
            if (existingIndex >= 0)
            {
                snippets[existingIndex] = snippet;
            }
            else
            {
                snippets.Add(snippet);
            }

            snippets = snippets
                .OrderByDescending(x => x.IsPinned)
                .ThenBy(x => x.Name, System.StringComparer.OrdinalIgnoreCase)
                .ToList();

            await SaveSnippetsUnsafeAsync(snippets, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string snippetId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var lease = await EnterMutationAsync(cancellationToken);
            List<CommandSnippet> snippets = await LoadSnippetsUnsafeAsync(cancellationToken);
            snippets.RemoveAll(x => x.Id == snippetId);
            await SaveSnippetsUnsafeAsync(snippets, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HistoryFileLease> EnterMutationAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        HistoryFileLease lease = await HistoryFileLease.AcquireAsync(_filePath + ".lock", cancellationToken);
        try
        {
            SweepOrphanedTemporaryFiles(cancellationToken);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private void SweepOrphanedTemporaryFiles(CancellationToken cancellationToken)
    {
        string prefix = Path.GetFileName(_filePath) + ".leased.";
        try
        {
            foreach (string candidate in Directory.EnumerateFiles(Path.GetDirectoryName(_filePath)!, prefix + "*.tmp"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                TryDeleteOrphan(candidate, prefix);
            }
        }
        catch (IOException) { /* Retry cleanup on a later mutation. */ }
        catch (UnauthorizedAccessException) { /* A known file can be writable without directory-list permission. */ }
    }

    private static void TryDeleteOrphan(string path, string prefix)
    {
        string name = Path.GetFileName(path);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!name.StartsWith(prefix, comparison) || !name.EndsWith(".tmp", comparison) ||
            name.Length <= prefix.Length + 4 || !Guid.TryParseExact(name[prefix.Length..^4], "N", out _)) return;
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return;
            // Only the leased naming scheme is owned here. The transaction lease excludes its writers;
            // an exclusive probe also leaves files alone while another process has them open.
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
            File.Delete(path);
        }
        catch (IOException) { /* An open or temporarily unavailable file is retried later. */ }
        catch (UnauthorizedAccessException) { /* Never let orphan cleanup block a valid save. */ }
    }

    private async Task<List<CommandSnippet>> LoadSnippetsUnsafeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream;
        try
        {
            stream = _openRead(_filePath);
        }
        catch (FileNotFoundException)
        {
            return new List<CommandSnippet>();
        }
        catch (DirectoryNotFoundException)
        {
            return new List<CommandSnippet>();
        }

        await using (stream)
        {
            return await JsonSerializer.DeserializeAsync(stream, CommandAssistJsonContext.Default.ListCommandSnippet, cancellationToken)
                ?? throw new JsonException("The snippet file must contain a JSON array.");
        }
    }

    private FileStream CreateTemporaryFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            FileSecurity? security;
            try { security = new FileInfo(_filePath).GetAccessControl(AccessControlSections.Access); }
            catch (FileNotFoundException) { security = null; }
            catch (DirectoryNotFoundException) { security = null; }
            if (security != null)
                return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Read | FileSystemRights.Write,
                    FileShare.None, 4096, FileOptions.None, security);
        }
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    private UnixFileMode? ReadExistingUnixMode()
    {
        if (OperatingSystem.IsWindows()) return null;
        try { return File.GetUnixFileMode(_filePath); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private async Task SaveSnippetsUnsafeAsync(List<CommandSnippet> snippets, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        UnixFileMode? mode = ReadExistingUnixMode();
        string tempPath = _filePath + $".leased.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (Stream stream = _createWrite(tempPath))
            {
                // Restrict the temporary file before any saved command text reaches it.
                if (!OperatingSystem.IsWindows() && mode is { } unixMode) File.SetUnixFileMode(tempPath, unixMode);
                await JsonSerializer.SerializeAsync(stream, snippets, CommandAssistJsonContext.Default.ListCommandSnippet, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            // Commit only after serialization and disposal succeed. No cancellation is reported after commit.
            cancellationToken.ThrowIfCancellationRequested();
            // Replace retains destination metadata on Windows; a new file must not overwrite a peer.
            if (File.Exists(_filePath)) File.Replace(tempPath, _filePath, destinationBackupFileName: null);
            else File.Move(tempPath, _filePath);
        }
        finally
        {
            try { File.Delete(tempPath); }
            catch { /* Cleanup must not hide the original failure or cancellation. */ }
        }
    }
}
