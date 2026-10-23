using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Ntilde.Shell;
using Ntilde.Shell.Theming;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.Theming;

/// <summary>
/// Gating: every shipped theme, plus synthetic worst cases, must yield readable chrome. A failure
/// here means the derivation (ThemeTokens.Derive) is wrong for that theme. Fix Derive; never lower
/// a threshold to make a theme pass. Explicit theme fields (phase 4) will be checked here too.
/// </summary>
public class ThemeContrastTests
{
    private static readonly Dictionary<string, Func<TerminalTheme>> StressThemes = new(StringComparer.Ordinal)
    {
        ["stress:white"] = () => new TerminalTheme { Name = "stress:white", Background = TermColor.White, Foreground = TermColor.Black },
        ["stress:black"] = () => new TerminalTheme { Name = "stress:black", Background = TermColor.Black, Foreground = TermColor.White },
        ["stress:mid-grey"] = () => new TerminalTheme { Name = "stress:mid-grey", Background = TermColor.FromRgb(0x80, 0x80, 0x80), Foreground = TermColor.Black },
        ["stress:default"] = () => new TerminalTheme(),
        ["stress:blue-is-cyan"] = () =>
        {
            var theme = new TerminalTheme { Name = "stress:blue-is-cyan" };
            theme.Cyan = theme.Blue;
            return theme;
        },
    };

    private static readonly string[] TextSurfaces =
        [ThemeKeys.WindowBg, ThemeKeys.ChromeBg, ThemeKeys.SidebarBg, ThemeKeys.Panel, ThemeKeys.PanelAlt, ThemeKeys.Overlay];

    private static readonly string[] StatusKeys = [ThemeKeys.Success, ThemeKeys.Warning, ThemeKeys.Error, ThemeKeys.Info];

    private static readonly string[] StatusSurfaces = [ThemeKeys.Panel, ThemeKeys.WindowBg, ThemeKeys.Overlay];

    private static readonly string[] Dots = [ThemeKeys.TabWorking, ThemeKeys.TabAttention, ThemeKeys.TabAgentWrote, ThemeKeys.TabAgentWatched];

    private static readonly string[] Chips =
        [ThemeKeys.TabChipActivity, ThemeKeys.TabChipAttention, ThemeKeys.TabChipAgentWrote, ThemeKeys.TabChipAgentWatched];

    public static TheoryData<string> Themes
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var file in Directory.EnumerateFiles(BuiltinThemeTests.FindThemesDirectory(), "*.json")
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                data.Add(Path.GetFileNameWithoutExtension(file));
            }

            foreach (var name in StressThemes.Keys)
            {
                data.Add(name);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Text_reads_on_every_surface(string theme)
    {
        var t = Tokens(theme);
        foreach (var surface in TextSurfaces)
        {
            AssertContrast(t, ThemeKeys.Fg, surface, ThemeTokens.TextContrast);
        }

        AssertContrast(t, ThemeKeys.TabPreviewFg, ThemeKeys.SidebarBg, ThemeTokens.TextContrast);
        AssertContrast(t, ThemeKeys.Fg3, ThemeKeys.Panel, ThemeTokens.SecondaryTextContrast);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Accent_ink_reads_on_the_accent(string theme)
        => AssertContrast(Tokens(theme), ThemeKeys.AccentFg, ThemeKeys.Accent, ThemeTokens.TextContrast);

    [Theory]
    [MemberData(nameof(Themes))]
    public void Status_text_reads_on_panels(string theme)
    {
        var t = Tokens(theme);
        foreach (var status in StatusKeys)
        {
            foreach (var surface in StatusSurfaces)
            {
                AssertContrast(t, status, surface, ThemeTokens.TextContrast);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Tab_dots_and_chips_read_on_the_strip(string theme)
    {
        var t = Tokens(theme);
        foreach (var dot in Dots)
        {
            AssertContrast(t, dot, ThemeKeys.ChromeBg, ThemeTokens.IndicatorContrast);
            AssertContrast(t, dot, ThemeKeys.SidebarBg, ThemeTokens.IndicatorContrast);
        }

        foreach (var chip in Chips)
        {
            AssertContrast(t, chip, ThemeKeys.SidebarBg, ThemeTokens.TextContrast);
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Row_fills_are_visible_and_distinct(string theme)
    {
        var t = Tokens(theme);
        var surface = t[ThemeKeys.ChromeBg];
        var hover = ThemeColorMath.Composite(t[ThemeKeys.TabHoverFill], surface);
        var selected = ThemeColorMath.Composite(t[ThemeKeys.TabSelectedFill], surface);

        Assert.True(ThemeColorMath.Contrast(hover, surface) >= 1.1, $"{theme}: hover fill invisible ({ThemeColorMath.Contrast(hover, surface):F2}:1)");
        Assert.True(ThemeColorMath.Contrast(selected, surface) >= 1.1, $"{theme}: selected fill invisible");
        Assert.True(ThemeColorMath.Contrast(selected, hover) >= 1.1, $"{theme}: selected and hover fills look alike ({ThemeColorMath.Contrast(selected, hover):F2}:1)");
    }

    private static ThemeTokens Tokens(string name) => ThemeTokens.Derive(Load(name), windowOpacity: 1.0);

    private static TerminalTheme Load(string name)
    {
        if (StressThemes.TryGetValue(name, out var make))
        {
            return make();
        }

        string path = Path.Combine(BuiltinThemeTests.FindThemesDirectory(), name + ".json");
        return JsonSerializer.Deserialize(File.ReadAllText(path), AppJsonContext.Default.TerminalTheme)
               ?? throw new InvalidOperationException($"{path} did not deserialize");
    }

    private static void AssertContrast(ThemeTokens t, string foreground, string surface, double minimum)
    {
        double ratio = ThemeColorMath.Contrast(t[foreground], t[surface]);
        Assert.True(ratio >= minimum, $"{foreground} on {surface}: {ratio:F2}:1, needs {minimum}:1");
    }
}
