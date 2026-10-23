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
}
