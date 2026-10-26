using System;
using System.Linq;
using System.Reflection;
using Avalonia.Media;
using Ntilde.Shell;
using Ntilde.Shell.Theming;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.Theming;

/// <summary>Derive is pure: plain [Fact]s only (no Application.Current).</summary>
public class ThemeTokensTests
{
    [Fact]
    public void Derive_sets_every_key_and_nothing_else()
    {
        var tokens = ThemeTokens.Derive(TestThemes.Dracula, 1.0);
        Assert.Equal(ThemeKeys.All.OrderBy(k => k, StringComparer.Ordinal), tokens.All.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void ThemeKeys_All_lists_every_key_constant_once()
    {
        var constants = typeof(ThemeKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.Equal(constants.Count, ThemeKeys.All.Count);
        Assert.Equal(constants.OrderBy(k => k, StringComparer.Ordinal), ThemeKeys.All.OrderBy(k => k, StringComparer.Ordinal));
        Assert.All(ThemeKeys.All, k => Assert.StartsWith("Nt", k, StringComparison.Ordinal));
    }

    [Fact]
    public void Accent_is_the_softened_ansi_blue_not_the_raw_one()
    {
        var theme = new TerminalTheme { Name = "T", Background = TermColor.Black, Blue = TermColor.FromRgb(0, 0, 238) };
        var tokens = ThemeTokens.Derive(theme, 1.0);

        Assert.Equal(Color.FromRgb(38, 38, 200), tokens[ThemeKeys.Accent]);
        Assert.Equal(tokens[ThemeKeys.Accent], tokens[ThemeKeys.TabIndicator]);
        Assert.Equal(tokens[ThemeKeys.Accent], tokens[ThemeKeys.Link]);
    }

    [Fact]
    public void Accent_ink_is_white_on_dark_accents_and_navy_on_light_ones()
    {
        var darkAccent = ThemeTokens.Derive(new TerminalTheme { Name = "Dark", Blue = TermColor.FromRgb(0, 0, 238) }, 1.0);
        Assert.Equal(Colors.White, darkAccent[ThemeKeys.AccentFg]);

        var lightAccent = ThemeTokens.Derive(new TerminalTheme { Name = "Light", Blue = TermColor.FromRgb(0x61, 0xaf, 0xef) }, 1.0);
        Assert.Equal(Color.Parse("#0a1622"), lightAccent[ThemeKeys.AccentFg]);
    }

    [Fact]
    public void Surfaces_rise_on_dark_themes_and_sink_on_light_ones()
    {
        var dark = ThemeTokens.Derive(TestThemes.Dracula, 1.0);
        Assert.True(dark.IsDark);
        Assert.Equal(ThemeColorMath.Shift(dark[ThemeKeys.WindowBg], 18), dark[ThemeKeys.Panel]);

        var light = ThemeTokens.Derive(TestThemes.GitHubLight, 1.0);
        Assert.False(light.IsDark);
        Assert.Equal(ThemeColorMath.Shift(light[ThemeKeys.WindowBg], -18), light[ThemeKeys.Panel]);
    }

    [Fact]
    public void A_mid_tone_background_flips_elevation_to_keep_text_readable()
    {
        // #808080 counts as light (luma 0.502), so panels would normally sink toward the black
        // text and drop below 4.5:1. Derive flips them upward instead.
        var theme = new TerminalTheme { Name = "Grey", Background = TermColor.FromRgb(0x80, 0x80, 0x80), Foreground = TermColor.Black };
        var tokens = ThemeTokens.Derive(theme, 1.0);

        Assert.True(tokens[ThemeKeys.Panel].R > 0x80);
        Assert.True(ThemeColorMath.Contrast(tokens[ThemeKeys.Fg], tokens[ThemeKeys.Panel]) >= ThemeTokens.TextContrast);
    }

    [Fact]
    public void Attention_matches_warning_and_agent_wrote_is_its_own_color()
    {
        var tokens = ThemeTokens.Derive(TestThemes.Dracula, 1.0);

        Assert.Equal(tokens[ThemeKeys.Warning], tokens[ThemeKeys.Attention]);
        Assert.NotEqual(tokens[ThemeKeys.Warning], tokens[ThemeKeys.AgentWrote]);
        Assert.NotEqual(tokens[ThemeKeys.Error], tokens[ThemeKeys.AgentWrote]);
        Assert.Equal(tokens[ThemeKeys.Info], tokens[ThemeKeys.AgentWatched]);
    }

    [Fact]
    public void Sidebar_background_carries_the_window_opacity()
    {
        var tokens = ThemeTokens.Derive(TestThemes.Dracula, 0.5);

        Assert.Equal(128, tokens[ThemeKeys.SidebarBg].A);
        Assert.Equal(ThemeColorMath.WithAlpha(tokens[ThemeKeys.ChromeBg], 128), tokens[ThemeKeys.SidebarBg]);
        Assert.Equal(0xFF, tokens[ThemeKeys.ChromeBg].A);
    }

    [Fact]
    public void A_readable_theme_foreground_is_used_verbatim()
    {
        var tokens = ThemeTokens.Derive(TestThemes.Dracula, 1.0);
        Assert.Equal(Color.FromRgb(0xF8, 0xF8, 0xF2), tokens[ThemeKeys.Fg]);
    }

    // Review Focus 3: a broken theme must still give readable UI text, and must not throw.
    [Fact]
    public void An_unreadable_theme_foreground_is_lifted_for_ui_text()
    {
        var theme = new TerminalTheme { Name = "Broken", Background = TermColor.Black, Foreground = TermColor.Black };
        var tokens = ThemeTokens.Derive(theme, 1.0);

        foreach (var surface in new[] { ThemeKeys.WindowBg, ThemeKeys.ChromeBg, ThemeKeys.Panel, ThemeKeys.PanelAlt, ThemeKeys.Overlay })
        {
            Assert.True(ThemeColorMath.Contrast(tokens[ThemeKeys.Fg], tokens[surface]) >= ThemeTokens.TextContrast, surface);
        }
    }

    // Review Focus 1: translucent user-theme colors must not make the chrome see-through.
    [Fact]
    public void Translucent_theme_colors_yield_opaque_surfaces_and_text()
    {
        var theme = new TerminalTheme
        {
            Name = "Glass",
            Background = new TermColor(0x28, 0x2A, 0x36, 0x80),
            Foreground = new TermColor(0xF8, 0xF8, 0xF2, 0x40),
        };
        var tokens = ThemeTokens.Derive(theme, 1.0);

        foreach (var key in new[] { ThemeKeys.WindowBg, ThemeKeys.ChromeBg, ThemeKeys.Panel, ThemeKeys.Overlay, ThemeKeys.Fg })
        {
            Assert.Equal(0xFF, tokens[key].A);
        }
    }

    [Fact]
    public void Interaction_fills_are_the_foreground_at_fixed_alphas()
    {
        var tokens = ThemeTokens.Derive(TestThemes.Dracula, 1.0);
        var fg = tokens[ThemeKeys.Fg];

        Assert.Equal(ThemeColorMath.WithAlpha(fg, 0x14), tokens[ThemeKeys.HoverFill]);
        Assert.Equal(ThemeColorMath.WithAlpha(fg, 0x26), tokens[ThemeKeys.SelectedFill]);
        Assert.Equal(ThemeColorMath.WithAlpha(fg, 0x30), tokens[ThemeKeys.PressedFill]);
    }
}
