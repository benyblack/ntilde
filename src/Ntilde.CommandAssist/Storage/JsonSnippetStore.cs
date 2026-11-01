using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        path => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    {
    }

    internal JsonSnippetStore(string filePath, Func<string, Stream> openRead, Func<string, Stream> createWrite)
    {
        _filePath = filePath;
        _openRead = openRead;
        _createWrite = createWrite;
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
            List<CommandSnippet> snippets = await LoadSnippetsUnsafeAsync(cancellationToken);
            snippets.RemoveAll(x => x.Id == snippetId);
            await SaveSnippetsUnsafeAsync(snippets, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
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

    private async Task SaveSnippetsUnsafeAsync(List<CommandSnippet> snippets, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = _filePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (Stream stream = _createWrite(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, snippets, CommandAssistJsonContext.Default.ListCommandSnippet, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            // Commit only after serialization and disposal succeed. No cancellation is reported after commit.
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(tempPath); }
            catch { /* Cleanup must not hide the original failure or cancellation. */ }
        }
    }
}
