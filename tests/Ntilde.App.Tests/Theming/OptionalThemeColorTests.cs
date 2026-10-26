using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Avalonia.Media;
using Ntilde.Shell;
using Ntilde.Shell.Theming;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.Theming;

/// <summary>
/// Theme v2's ten optional colors (spec §1): absent means derived, so an existing theme file loads
/// and saves exactly as before; present means used verbatim; malformed means derived, never a theme
/// that fails to load or a transparent color.
/// </summary>
public class OptionalThemeColorTests
{
    private static readonly string[] OptionalFields =
    [
        nameof(TerminalTheme.SelectionBackground), nameof(TerminalTheme.SelectionForeground), nameof(TerminalTheme.CursorText),
        nameof(TerminalTheme.SearchMatch), nameof(TerminalTheme.SearchMatchActive), nameof(TerminalTheme.Accent),
        nameof(TerminalTheme.Success), nameof(TerminalTheme.Warning), nameof(TerminalTheme.Error), nameof(TerminalTheme.Info),
    ];

    public static TheoryData<string> ShippedThemes()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(BuiltinThemeTests.FindThemesDirectory(), "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            data.Add(Path.GetFileNameWithoutExtension(file));
        }

        return data;
    }

    /// <summary>
    /// What a save wrote before the optional fields existed: Name and the 20 colors, in declaration
    /// order. A save of a theme without optional colors must still be exactly that, byte for byte.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShippedThemes))]
    public void A_theme_without_optional_colors_saves_exactly_as_before(string name)
    {
        var theme = Load(name);

        string saved = JsonSerializer.Serialize(theme, AppJsonContext.Default.TerminalTheme);

        Assert.Equal(PreV2Save(theme), saved);
    }

    [Fact]
    public void All_ten_round_trip()
    {
        var theme = new TerminalTheme { Name = "full" };
        for (int i = 0; i < OptionalFields.Length; i++)
        {
            typeof(TerminalTheme).GetProperty(OptionalFields[i])!.SetValue(theme, (TermColor?)TermColor.FromRgb((byte)(i * 20), 0x40, 0x80));
        }

        string json = JsonSerializer.Serialize(theme, AppJsonContext.Default.TerminalTheme);
        var back = JsonSerializer.Deserialize(json, AppJsonContext.Default.TerminalTheme)!;

        foreach (var field in OptionalFields)
        {
            var property = typeof(TerminalTheme).GetProperty(field)!;
            Assert.Equal(property.GetValue(theme), property.GetValue(back));
        }

        Assert.Equal(json, JsonSerializer.Serialize(back, AppJsonContext.Default.TerminalTheme));
    }

    [Theory]
    [InlineData("\"not a color\"")]
    [InlineData("\"\"")]
    [InlineData("42")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{ \"X\": 1 }")]
    [InlineData("null")]
    public void A_malformed_or_null_optional_color_reads_as_derived(string value)
    {
        string json = $$"""{ "Name": "bad", "Foreground": "#FFFFFF", "SelectionBackground": {{value}}, "Accent": "#336699" }""";

        var theme = JsonSerializer.Deserialize(json, AppJsonContext.Default.TerminalTheme)!;

        Assert.Null(theme.SelectionBackground);
        // The rest of the theme still loads: one bad entry costs that color only.
        Assert.Equal(TermColor.FromRgb(0x33, 0x66, 0x99), theme.Accent);
        Assert.Equal(TermColor.White, theme.Foreground);
    }

    /// <summary>A fully transparent object is a color like any other, not a malformed value.</summary>
    [Fact]
    public void A_transparent_object_color_is_kept()
    {
        const string json = """{ "Name": "clear", "SearchMatch": { "R": 0, "G": 0, "B": 0, "A": 0 } }""";

        var theme = JsonSerializer.Deserialize(json, AppJsonContext.Default.TerminalTheme)!;

        Assert.Equal(TermColor.Transparent, theme.SearchMatch);
    }

    [Fact]
    public void Clone_copies_the_optional_colors()
    {
        var theme = new TerminalTheme { SelectionBackground = TermColor.FromRgb(1, 2, 3), Info = TermColor.FromRgb(4, 5, 6) };

        var clone = theme.Clone();

        Assert.Equal(theme.SelectionBackground, clone.SelectionBackground);
        Assert.Equal(theme.Info, clone.Info);
        Assert.Null(clone.Accent);
    }

    [Fact]
    public void Explicit_accent_and_status_colors_are_used_verbatim()
    {
        // Deliberately poor choices: derived colors would be contrast-fixed, explicit ones are not.
        var theme = new TerminalTheme
        {
            Accent = TermColor.FromRgb(0x20, 0x20, 0x20),
            Success = TermColor.FromRgb(0x01, 0x02, 0x03),
            Warning = TermColor.FromRgb(0x04, 0x05, 0x06),
            Error = TermColor.FromRgb(0x07, 0x08, 0x09),
            Info = TermColor.FromRgb(0x0A, 0x0B, 0x0C),
        };

        var tokens = ThemeTokens.Derive(theme, 1.0);

        Assert.Equal(Color.FromRgb(0x20, 0x20, 0x20), tokens[ThemeKeys.Accent]);
        Assert.Equal(Color.FromRgb(0x01, 0x02, 0x03), tokens[ThemeKeys.Success]);
        Assert.Equal(Color.FromRgb(0x04, 0x05, 0x06), tokens[ThemeKeys.Warning]);
        Assert.Equal(Color.FromRgb(0x07, 0x08, 0x09), tokens[ThemeKeys.Error]);
        Assert.Equal(Color.FromRgb(0x0A, 0x0B, 0x0C), tokens[ThemeKeys.Info]);
    }

    [Fact]
    public void Render_colors_derive_from_the_tokens_when_absent()
    {
        var theme = Load("Dracula");
        var tokens = ThemeTokens.Derive(theme, 1.0);

        var colors = TerminalRenderColors.Derive(theme);

        Assert.Equal(ThemeColorMath.WithAlpha(tokens[ThemeKeys.Accent], 0x66), colors.Selection);
        Assert.Null(colors.SelectionForeground);
        Assert.Equal(Color.FromRgb((byte)theme.Background.R, (byte)theme.Background.G, (byte)theme.Background.B), colors.CursorText);
        Assert.Equal(ThemeColorMath.WithAlpha(tokens[ThemeKeys.Warning], 0x64), colors.SearchMatch);
        Assert.Equal(
            ThemeColorMath.WithAlpha(ThemeColorMath.MidpointHue(tokens[ThemeKeys.Warning], tokens[ThemeKeys.Error]), 0x96),
            colors.SearchMatchActive);
    }

    [Fact]
    public void Explicit_render_colors_are_used_verbatim()
    {
        var theme = Load("Dracula");
        theme.SelectionBackground = TermColor.FromRgb(0x44, 0x47, 0x5A);
        theme.SelectionForeground = TermColor.FromRgb(0xF8, 0xF8, 0xF2);
        theme.CursorText = TermColor.FromRgb(0x28, 0x2A, 0x36);
        theme.SearchMatch = TermColor.FromArgb(0x80, 0xFF, 0xB8, 0x6C);
        theme.SearchMatchActive = TermColor.FromRgb(0xFF, 0x79, 0xC6);

        var colors = TerminalRenderColors.Derive(theme);

        Assert.Equal(Color.FromRgb(0x44, 0x47, 0x5A), colors.Selection);
        Assert.Equal(Color.FromRgb(0xF8, 0xF8, 0xF2), colors.SelectionForeground);
        Assert.Equal(Color.FromRgb(0x28, 0x2A, 0x36), colors.CursorText);
        Assert.Equal(Color.FromArgb(0x80, 0xFF, 0xB8, 0x6C), colors.SearchMatch);
        Assert.Equal(Color.FromRgb(0xFF, 0x79, 0xC6), colors.SearchMatchActive);
    }

    [Fact]
    public void A_low_contrast_theme_gets_a_readable_selected_text_color()
    {
        var theme = Load("SolarizedLight");

        var colors = TerminalRenderColors.Derive(theme);

        var selection = ThemeColorMath.Composite(colors.Selection, Color.FromRgb((byte)theme.Background.R, (byte)theme.Background.G, (byte)theme.Background.B));
        Assert.NotNull(colors.SelectionForeground);
        Assert.True(ThemeColorMath.Contrast(colors.SelectionForeground!.Value, selection) >= ThemeTokens.TextContrast);
    }

    [Fact]
    public void The_render_color_cache_follows_a_theme_edited_in_place()
    {
        var theme = new TerminalTheme();
        var before = TerminalRenderColors.For(theme);
        Assert.Equal(before, TerminalRenderColors.For(theme));

        theme.SelectionBackground = TermColor.FromRgb(0x12, 0x34, 0x56);

        Assert.Equal(Color.FromRgb(0x12, 0x34, 0x56), TerminalRenderColors.For(theme).Selection);
    }

    private static TerminalTheme Load(string name)
    {
        string path = Path.Combine(BuiltinThemeTests.FindThemesDirectory(), name + ".json");
        return JsonSerializer.Deserialize(File.ReadAllText(path), AppJsonContext.Default.TerminalTheme)!;
    }

    private static string PreV2Save(TerminalTheme theme)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("Name", theme.Name);
            writer.WriteString("Foreground", theme.Foreground.ToHex());
            writer.WriteString("Background", theme.Background.ToHex());
            writer.WriteString("CursorColor", theme.CursorColor.ToHex());
            string[] names = ["Black", "Red", "Green", "Yellow", "Blue", "Magenta", "Cyan", "White"];
            for (int i = 0; i < 8; i++)
            {
                writer.WriteString(names[i], theme.GetAnsiColor(i, bright: false).ToHex());
            }

            for (int i = 0; i < 8; i++)
            {
                writer.WriteString("Bright" + names[i], theme.GetAnsiColor(i, bright: true).ToHex());
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
