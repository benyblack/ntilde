using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Ntilde.McpServer.Tools;
using Ntilde.Shell;                    // AppJsonContext
using Ntilde.Shell.Theming;            // ThemeTokens, ThemeKeys
using Ntilde.VT;                       // TerminalTheme, TermColor

namespace Ntilde.McpServer.Tests;

/// <summary>
/// Theme v2's optional colors in the MCP theme tools, plus the two guards that keep the server's
/// hand-mirrored theme knowledge honest: the field lists against <see cref="TerminalTheme"/>, and
/// the contrast warning's panel against <see cref="ThemeTokens"/>.
/// </summary>
public class ThemeToolsOptionalColorTests
{
    private const string Dracula = """
        {
          "Name": "Dracula",
          "Foreground": "#F8F8F2", "Background": "#282A36", "CursorColor": "#F8F8F2",
          "Black": "#21222C", "Red": "#FF5555", "Green": "#50FA7B", "Yellow": "#F1FA8C",
          "Blue": "#BD93F9", "Magenta": "#FF79C6", "Cyan": "#8BE9FD", "White": "#F8F8F2",
          "BrightBlack": "#6272A4", "BrightRed": "#FF6E6E", "BrightGreen": "#69FF94",
          "BrightYellow": "#FFFFA5", "BrightBlue": "#D6ACFF", "BrightMagenta": "#FF92DF",
          "BrightCyan": "#A4FFFF", "BrightWhite": "#FFFFFF"
        }
        """;

    private static string With(string fields) => Dracula.Replace("\"Name\": \"Dracula\",", $"\"Name\": \"Dracula\", {fields},");

    /// <summary>
    /// Every <see cref="TermColor"/> property is required and every <see cref="Nullable{TermColor}"/>
    /// one optional. A color added to the theme without the validator learning it would be reported
    /// to theme authors as an unknown field.
    /// </summary>
    [Fact]
    public void Field_lists_are_exactly_the_themes_colors()
    {
        var properties = typeof(TerminalTheme).GetProperties();
        string[] Named(Type type) => properties.Where(p => p.PropertyType == type).Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(Named(typeof(TermColor)), ThemeTools.ColorFields.Order(StringComparer.Ordinal));
        Assert.Equal(Named(typeof(TermColor?)), ThemeTools.OptionalColorFields.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Optional_colors_are_known_and_validated()
    {
        var result = ThemeTools.ValidateThemeJson(With("\"SelectionBackground\": \"#44475A\", \"CursorText\": null"));

        Assert.StartsWith("VALID", result);
        Assert.DoesNotContain("Unknown field", result);
    }

    [Fact]
    public void A_malformed_optional_color_is_an_error()
    {
        var result = ThemeTools.ValidateThemeJson(With("\"SearchMatch\": \"yellowish\""));

        Assert.StartsWith("INVALID", result);
        Assert.Contains("Optional field 'SearchMatch' has an invalid color value", result);
    }

    [Fact]
    public void An_explicit_accent_that_will_not_read_on_panels_is_warned_about_not_rejected()
    {
        // Dracula's panel is #3A3C48; a near-identical accent cannot carry text on it.
        var result = ThemeTools.ValidateThemeJson(With("\"Accent\": \"#40424E\", \"Success\": \"#50FA7B\""));

        Assert.StartsWith("VALID", result);
        Assert.Contains("Optional field 'Accent' has", result);
        Assert.DoesNotContain("Optional field 'Success' has", result);
    }

    [Fact]
    public void Eight_digit_values_are_measured_as_AARRGGBB()
    {
        // Opaque alpha first, then Dracula's green: reads fine. Read as RRGGBBAA it would be a dark teal.
        var result = ThemeTools.ValidateThemeJson(With("\"Success\": \"#FF50FA7B\""));

        Assert.DoesNotContain("Optional field 'Success' has", result);
    }

    [Fact]
    public void Schema_lists_the_optional_colors_as_derived()
    {
        var schema = ThemeTools.GetThemeSchema();

        Assert.Contains("derived when omitted", schema);
        foreach (var field in ThemeTools.OptionalColorFields)
        {
            Assert.Contains(field, schema);
        }
    }

    /// <summary>
    /// The contrast warning measures against the panel the app derives. The server cannot call
    /// <see cref="ThemeTokens"/>, so it mirrors the rule; this pins the mirror for every shipped theme.
    /// </summary>
    [Fact]
    public void Mirrored_panel_matches_the_apps_for_every_shipped_theme()
    {
        string themes = Path.Combine(RepoRoot(), "src", "Ntilde.App", "themes");
        var files = Directory.GetFiles(themes, "*.json");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var theme = JsonSerializer.Deserialize(File.ReadAllText(file), AppJsonContext.Default.TerminalTheme)!;
            var expected = ThemeTokens.Derive(theme, 1.0)[ThemeKeys.Panel];

            var panel = ThemeTools.DerivePanel(
                ((byte)theme.Background.R, (byte)theme.Background.G, (byte)theme.Background.B),
                ((byte)theme.Foreground.R, (byte)theme.Foreground.G, (byte)theme.Foreground.B));

            Assert.True(
                (expected.R, expected.G, expected.B) == panel,
                $"{Path.GetFileName(file)}: app panel #{expected.R:X2}{expected.G:X2}{expected.B:X2}, mirror #{panel.R:X2}{panel.G:X2}{panel.B:X2}.");
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Ntilde.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate 'Ntilde.sln' above the test output directory.");
    }
}
