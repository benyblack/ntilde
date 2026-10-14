using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace Ntilde.Settings;

/// <summary>One searchable thing: a page, or a setting row on it.</summary>
public sealed record SettingsSearchEntry(
    SettingsPage Page,
    string PageTitle,
    string? Group,
    string? RowId,
    string Title,
    string? Description,
    string? Keywords);

/// <summary>A match, with where the query sits in the title for highlighting (-1 when it matched elsewhere).</summary>
public sealed record SettingsSearchResult(SettingsSearchEntry Entry, int TitleMatchStart, int TitleMatchLength);

/// <summary>
/// The settings search (spec §5). It is read from the pages' SettingRows, so a new row is
/// searchable with no extra step.
/// </summary>
/// <remarks>
/// Shortcut bindings, profiles, snippets, title-bar actions and the theme editor are not rows, and
/// are deliberately not indexed in v1.
/// </remarks>
public sealed class SettingsSearchIndex
{
    private readonly IReadOnlyList<SettingsSearchEntry> _entries;

    public SettingsSearchIndex(IReadOnlyList<SettingsSearchEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = entries;
    }

    public IReadOnlyList<SettingsSearchEntry> Entries => _entries;

    /// <summary>One entry per page, then one per titled SettingRow on it, in registry and on-screen order.</summary>
    public static SettingsSearchIndex Build(IEnumerable<(SettingsPageDescriptor Descriptor, Control View)> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var entries = new List<SettingsSearchEntry>();
        foreach (var (descriptor, view) in pages)
        {
            entries.Add(new SettingsSearchEntry(descriptor.Page, descriptor.Title, null, null, descriptor.Title, null, null));
            foreach (SettingRow row in view.GetLogicalDescendants().OfType<SettingRow>())
            {
                if (string.IsNullOrWhiteSpace(row.Title))
                {
                    continue;
                }

                string? group = row.FindLogicalAncestorOfType<SettingGroup>()?.Header;
                entries.Add(new SettingsSearchEntry(descriptor.Page, descriptor.Title, group, row.RowId, row.Title, row.Description, row.Keywords));
            }
        }

        return new SettingsSearchIndex(entries);
    }

    /// <summary>
    /// Case-insensitive substring match over title, description, keywords, group and page title.
    /// Title matches come first, then the rest, each in index order. The query is trimmed and
    /// otherwise literal, so "9;4" matches as typed.
    /// </summary>
    public IReadOnlyList<SettingsSearchResult> Search(string? query)
    {
        string trimmed = (query ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return Array.Empty<SettingsSearchResult>();
        }

        var titleMatches = new List<SettingsSearchResult>();
        var otherMatches = new List<SettingsSearchResult>();
        foreach (SettingsSearchEntry entry in _entries)
        {
            int at = entry.Title.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                titleMatches.Add(new SettingsSearchResult(entry, at, trimmed.Length));
            }
            else if (Contains(entry.Description, trimmed) || Contains(entry.Keywords, trimmed)
                     || Contains(entry.Group, trimmed) || Contains(entry.PageTitle, trimmed))
            {
                otherMatches.Add(new SettingsSearchResult(entry, -1, 0));
            }
        }

        return titleMatches.Concat(otherMatches).ToList();
    }

    private static bool Contains(string? text, string query)
        => text is not null && text.Contains(query, StringComparison.OrdinalIgnoreCase);
}
