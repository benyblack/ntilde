using Ntilde.AgentHost.Contracts;
using Ntilde.VT;
using Ntilde.VT.Links;

namespace Ntilde.AgentHost;

/// <summary>Bounded App-owned projection. Never queues the UI behind a parser write or reads a control under the buffer lock.</summary>
internal static class AgentPaneMetadataCapture
{
    internal const int MaxCells = 8192;
    internal const int MaxRuns = 128;
    internal const int MaxTextChars = 32768;
    internal const int MaxFieldChars = 4096;

    internal static bool TextFits(IEnumerable<string?> fields)
    {
        int total = 0;
        foreach (string? field in fields)
        {
            if (field?.Length > MaxFieldChars) return false;
            total += field?.Length ?? 0;
            if (total > MaxTextChars) return false;
        }
        return true;
    }

    internal static bool TryRead(TerminalBuffer buffer, out PaneBufferMetadata? result)
    {
        result = null;
        if (!buffer.Lock.TryEnterReadLock(TimeSpan.Zero)) return false;
        try
        {
            int rows = buffer.Rows, cols = buffer.Cols;
            if (rows <= 0 || cols <= 0 || (long)rows * cols > MaxCells) return false;
            var links = ReadLinks(buffer, rows, cols);
            if (links == null) return false;
            result = new PaneBufferMetadata
            {
                Rows = rows,
                Cols = cols,
                CursorRow = buffer.CursorRow,
                CursorCol = buffer.CursorCol,
                AlternateScreen = buffer.IsAltScreenActive,
                ScrollbackLines = buffer.Scrollback.Count,
                HistoryGeneration = buffer.Scrollback.Generation,
                RowsEvicted = buffer.Scrollback.TotalRowsEvicted,
                AcceptingCommandInput = buffer.IsAcceptingCommandInput,
                CommandStart = Mark(buffer.CommandStartMark),
                OutputStart = Mark(buffer.CommandOutputStartMark),
                Links = links,
            };
            return true;
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    private static PaneHyperlinkRun[]? ReadLinks(TerminalBuffer buffer, int rows, int cols)
    {
        int first = buffer.IsAltScreenActive ? 0 : buffer.Scrollback.Count;
        var links = new List<PaneHyperlinkRun>();
        var identities = new Dictionary<Hyperlink, int>(ReferenceEqualityComparer.Instance);
        int textChars = 0;
        for (int row = 0; row < rows; row++)
        {
            int col = 0;
            while (col < cols)
            {
                var link = buffer.GetHyperlinkIdentityAbsolute(col, first + row);
                if (link == null) { col++; continue; }
                int start = col;
                col = RunEnd(buffer, link, first + row, col, cols);
                if (links.Count >= MaxRuns || link.Uri.Length > HyperlinkRegistry.MaxUriLength || link.Id?.Length > HyperlinkRegistry.MaxIdLength) return null;
                textChars += link.Uri.Length + (link.Id?.Length ?? 0);
                if (textChars > MaxTextChars) return null;
                links.Add(new PaneHyperlinkRun
                {
                    Row = row,
                    Col = start,
                    Length = col - start,
                    Identity = Identity(identities, link),
                    Uri = link.Uri,
                    Id = link.Id
                });
            }
        }
        return links.ToArray();
    }

    private static int RunEnd(TerminalBuffer buffer, Hyperlink link, int row, int start, int cols)
    {
        int end = start + 1;
        while (end < cols && ReferenceEquals(link, buffer.GetHyperlinkIdentityAbsolute(end, row))) end++;
        return end;
    }

    private static int Identity(Dictionary<Hyperlink, int> identities, Hyperlink link)
    {
        if (identities.TryGetValue(link, out int existing)) return existing;
        int value = identities.Count;
        identities.Add(link, value);
        return value;
    }

    private static PaneShellMark? Mark(ShellIntegrationMark? mark) => mark is { } value ? new PaneShellMark
    {
        Row = value.Row,
        Col = value.Column,
        AbsoluteRow = value.AbsoluteRow,
        Generation = value.Generation,
        AlternateScreen = value.IsAltScreen,
    } : null;
}
