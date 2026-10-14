using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace Ntilde.Settings;

/// <summary>One searchable thing: a page, or a setting row on it.</summary>
/// <param name="Details">A row's text behind "More": searched, but not shown in a result.</param>
public sealed record SettingsSearchEntry(
    SettingsPage Page,
    string PageTitle,
    string? Group,
    string? RowId,
    string Title,
    string? Description,
    string? Keywords,
    string? Details = null);

/// <summary>A match, with the part of the title to emphasise (-1 when the title holds no query word).</summary>
/// <param name="MatchedOnlyInDetails">A query word is found only in the row's Details, so a jump opens them.</param>
public sealed record SettingsSearchResult(
    SettingsSearchEntry Entry,
    int TitleMatchStart,
    int TitleMatchLength,
    bool MatchedOnlyInDetails = false);

/// <summary>
/// The settings search (spec §5). It is read from the pages' SettingRows, so a new row is
/// searchable with no extra step.
/// </summary>
/// <remarks>
/// Shortcut bindings, profiles, snippets, title-bar actions and the theme editor are not rows, and
/// are deliberately not indexed in v1; their pages are found by the page keywords in
/// <see cref="SettingsPageRegistry"/>.
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
            entries.Add(new SettingsSearchEntry(descriptor.Page, descriptor.Title, null, null, descriptor.Title, null, descriptor.Keywords));
            foreach (SettingRow row in view.GetLogicalDescendants().OfType<SettingRow>())
            {
                if (string.IsNullOrWhiteSpace(row.Title))
                {
                    continue;
                }

                string? group = row.FindLogicalAncestorOfType<SettingGroup>()?.Header;
                entries.Add(new SettingsSearchEntry(descriptor.Page, descriptor.Title, group, row.RowId, row.Title, row.Description, row.Keywords, row.Details));
            }
        }

        return new SettingsSearchIndex(entries);
    }

    /// <summary>
    /// Case-insensitive search. The trimmed query is split into words at white space, and an entry
    /// matches when every word is somewhere in its title, description, details, keywords, group or
    /// page title. Entries whose title holds every word come first, then the rest, each in index
    /// order. A word is otherwise literal, so "9;4" matches as typed.
    /// </summary>
    /// <remarks>
    /// The title emphasis is the whole query where the title holds it as typed, otherwise the first
    /// query word the title holds.
    /// </remarks>
    public IReadOnlyList<SettingsSearchResult> Search(string? query)
    {
        string trimmed = (query ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return Array.Empty<SettingsSearchResult>();
        }

        string[] words = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var titleMatches = new List<SettingsSearchResult>();
        var otherMatches = new List<SettingsSearchResult>();
        foreach (SettingsSearchEntry entry in _entries)
        {
            if (!words.All(word => ContainsOutsideDetails(entry, word) || Contains(entry.Details, word)))
            {
                continue;
            }

            bool matchedOnlyInDetails = words.Any(word => !ContainsOutsideDetails(entry, word));
            int whole = entry.Title.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase);
            if (whole >= 0)
            {
                titleMatches.Add(new SettingsSearchResult(entry, whole, trimmed.Length, matchedOnlyInDetails));
                continue;
            }

            bool everyWordInTitle = words.All(word => Contains(entry.Title, word));
            string? shown = words.FirstOrDefault(word => Contains(entry.Title, word));
            int start = shown is null ? -1 : entry.Title.IndexOf(shown, StringComparison.OrdinalIgnoreCase);
            var result = new SettingsSearchResult(entry, start, shown?.Length ?? 0, matchedOnlyInDetails);
            (everyWordInTitle ? titleMatches : otherMatches).Add(result);
        }

        return titleMatches.Concat(otherMatches).ToList();
    }

    private static bool ContainsOutsideDetails(SettingsSearchEntry entry, string word)
        => Contains(entry.Title, word) || Contains(entry.Description, word) || Contains(entry.Keywords, word)
           || Contains(entry.Group, word) || Contains(entry.PageTitle, word);

    private static bool Contains(string? text, string word)
        => text is not null && text.Contains(word, StringComparison.OrdinalIgnoreCase);
}
