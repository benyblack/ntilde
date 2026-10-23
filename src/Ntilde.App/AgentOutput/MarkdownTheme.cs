using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Ntilde.Shell.Theming;

namespace Ntilde.AgentOutput;

/// <summary>Resolved brush set for one render pass.</summary>
/// <remarks>
/// Extracted from <see cref="MarkdownRenderer"/> so fence-body handlers in sibling files can
/// take it. Resolves the app-wide theme tokens. Before theme v2 it asked for <c>Nt*</c> keys that
/// nothing in its tree defined, so it always fell back to a fixed dark palette.
/// </remarks>
internal sealed class MarkdownTheme
{
    internal required IBrush Foreground { get; init; }

    internal required IBrush Secondary { get; init; }

    internal required IBrush CodeBackground { get; init; }

    internal required IBrush PanelBackground { get; init; }

    internal required IBrush Hairline { get; init; }

    internal required IBrush Accent { get; init; }

    /// <summary>Diff addition lines.</summary>
    internal required IBrush Added { get; init; }

    /// <summary>Diff removal lines.</summary>
    internal required IBrush Removed { get; init; }

    /// <summary>Diff hunk headers.</summary>
    internal required IBrush Hunk { get; init; }

    internal static MarkdownTheme Resolve(StyledElement anchor)
    {
        return new MarkdownTheme
        {
            Foreground = Find(anchor, ThemeKeys.Fg),
            Secondary = Find(anchor, ThemeKeys.Fg3),
            CodeBackground = Find(anchor, ThemeKeys.PanelAlt),
            PanelBackground = Find(anchor, ThemeKeys.Panel),
            Hairline = Find(anchor, ThemeKeys.Hairline),
            Accent = Find(anchor, ThemeKeys.Link),
            Added = Find(anchor, ThemeKeys.DiffAdded),
            Removed = Find(anchor, ThemeKeys.DiffRemoved),
            Hunk = Find(anchor, ThemeKeys.DiffHunk),
        };
    }

    private static IBrush Find(StyledElement anchor, string key)
    {
        // ThemeService installs every token at app start, so a miss means the anchor is not in a
        // tree yet. Fall back to the current token colour rather than a hardcoded palette. An
        // unexpected non-brush override also degrades here instead of crashing.
        return anchor.TryFindResource(key, out object? value) && value is IBrush brush
            ? brush
            : new ImmutableSolidColorBrush(ThemeService.Current[key]);
    }
}
