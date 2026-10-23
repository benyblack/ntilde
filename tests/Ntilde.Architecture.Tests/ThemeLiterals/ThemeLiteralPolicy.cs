namespace Ntilde.Architecture.Tests.ThemeLiterals;

/// <summary>Where colors may be spelled, and the few that are fixed on purpose.</summary>
internal static class ThemeLiteralPolicy
{
    // The token layer: derivation, importers (theme data in), JSON color converters (theme data on disk).
    private static readonly string[] TokenLayerPrefixes =
    [
        "src/Ntilde.App/Shell/Theming/",
        "src/Ntilde.App/Shell/ThemeImporters/",
    ];

    private static readonly string[] TokenLayerFiles =
    [
        "src/Ntilde.App/Shell/TermColorJsonConverter.cs",
        "src/Ntilde.App/Shell/JsonColorConverter.cs",
    ];

    public static bool IsTokenLayer(string relativePath)
        => TokenLayerPrefixes.Any(prefix => relativePath.StartsWith(prefix, StringComparison.Ordinal)) ||
           TokenLayerFiles.Contains(relativePath, StringComparer.Ordinal);

    /// <summary>
    /// Colors that are fixed on purpose (spec §2, "Fixed on purpose"). Each entry needs a reason,
    /// and an entry that no longer matches anything fails ThemeLiteralGuardTests. Add an entry only
    /// for these categories: platform convention, user-chosen identity colors, debug-only drawing.
    /// </summary>
    public static readonly IReadOnlyList<PermanentAllowance> Permanent =
    [
        new("src/Ntilde.App/App.axaml", Member: null, Text: "#ffe81123",
            Reason: "Caption close-button hover red: platform convention, identical on every theme."),
        new("src/Ntilde.App/App.axaml", Member: null, Text: "#fff1707a",
            Reason: "Caption close-button pressed red: platform convention, identical on every theme."),
        new("src/Ntilde.App/Controls/ConnectionManager.axaml.cs", Member: "GroupSwatches", Text: null,
            Reason: "Categorical swatches cycled through connection groups: identity colors, not theme chrome."),
        new("src/Ntilde.App/Controls/ConnectionManager.axaml.cs", Member: "Swatch", Text: null,
            Reason: "Default swatch of a group or tag node before GroupSwatches assigns one: the same identity-color family."),
        new("src/Ntilde.App/Shell/TerminalDrawOperation.cs", Member: "DrawDebugCellGrid", Text: null,
            Reason: "Debug-only drawing (cell grid overlay)."),
    ];

    public static bool IsPermanentlyAllowed(ColorLiteralFinding finding) => Permanent.Any(allowance => allowance.Matches(finding));
}

/// <summary>A fixed-on-purpose color: matched by file plus enclosing member (C#) or literal text (XAML).</summary>
internal sealed record PermanentAllowance(string File, string? Member, string? Text, string Reason)
{
    public bool Matches(ColorLiteralFinding finding)
        => string.Equals(File, finding.File, StringComparison.Ordinal) &&
           (Member is null || string.Equals(Member, finding.Member, StringComparison.Ordinal)) &&
           (Text is null || finding.Text.EndsWith("=" + Text, StringComparison.OrdinalIgnoreCase));
}
