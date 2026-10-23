# Theme System v2 — Phase 1 (Foundation) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the per-window `Nt*` palette with one app-wide, theme-derived token set (including the Fluent stock-control palette), move Settings, Connection Manager and Agent Output markdown onto it, and land the three gating guards that stop new UI from bypassing it.

**Architecture:** A pure `ThemeTokens.Derive(TerminalTheme, windowOpacity)` computes ~63 named colors with one set of color math (`ThemeColorMath`). A static `ThemeService` (modeled on `UiScale`) installs them as mutable brushes in one merged dictionary in `Application.Resources`, mutates them in place on every theme change, writes Fluent's `ColorPaletteResources`, and sets `Application.RequestedThemeVariant`. XAML consumes `{DynamicResource Nt…}` keys from `ThemeKeys`. Architecture tests parse every `.axaml` (XDocument) and every App `.cs` (Roslyn semantic model) to block unknown token keys and new color literals (ratchet baseline).

**Tech Stack:** C# / .NET 10 (SDK 10.0.400), Avalonia 12.0.4 + Avalonia.Themes.Fluent 12.0.4, xunit v3 + Avalonia.Headless.XUnit, Microsoft.CodeAnalysis.CSharp 5.3.0 (test-only, new).

**Spec:** `docs/superpowers/specs/2026-09-23-theme-system-design.md` (commit 4520cf3). This plan implements spec §9 phase 1 only; phases 2–4 get their own plans after this one lands.

## Global Constraints

- Build and test only through the wrappers: `scripts/build.ps1 <dotnet args>` (PowerShell) or `scripts/build.sh <dotnet args>`. Never raw `dotnet` (it hangs under captured stdout — see `CLAUDE.md`).
- Run test projects individually for feedback. For `tests/Ntilde.App.Tests` always pass `--blame-hang-timeout 5m`, log to a file, and never run two App.Tests invocations concurrently.
- Avalonia stays at 12.0.4. `Microsoft.CodeAnalysis.CSharp` 5.3.0 is added as a test-only package, used by `tests/Ntilde.Architecture.Tests` only.
- Theme resource keys use the `Nt` prefix and exist only as constants in `Ntilde.Shell.Theming.ThemeKeys`. XAML references them with `{DynamicResource Nt…}`. C# references `ThemeKeys.X`, never a raw `"Nt…"` string.
- `ThemeService.Apply` runs on the UI thread only (`Dispatcher.UIThread.VerifyAccess()`). Every test that touches `Application.Current` resources is an `[AvaloniaFact]`, never a plain `[Fact]` (memory: a plain `[Fact]` calling `UiScale.Apply` once poisoned 29 unrelated headless tests).
- Derived colors are contrast-fixed. Phase 1 introduces no explicit theme fields (that is phase 4).
- `tests/Ntilde.Architecture.Tests` builds with `TreatWarningsAsErrors=true`. Guard code must be warning-free (use `StringComparison`, hoist constant arrays to `static readonly` per CA1861, use `CultureInfo.InvariantCulture` for parsing).
- Phase 1 leaves `MainWindow`'s own imperative theming (tabs, title bar, overlays, `ApplyThemeToDialogWindow`) untouched. That is phase 2. The only change visible in the main window is stock Fluent controls (menus, flyouts, tooltips, scrollbars) following the theme palette.
- PR target is the new `dev-review` branch cut from `main`, never `dev` or `main`.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. PR descriptions end with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.

## Review Focus

Five inputs the spec implies but no task's main tests exercise. Each has a test added to its owning task:

1. **A user theme with translucent `Background`/`Foreground`** (e.g. `"#80282A36"`). Expect opaque UI surfaces and text, never see-through panels. → Task 2, `Translucent_theme_colors_yield_opaque_surfaces_and_text`.
2. **Dragging the window-opacity slider** re-applies the theme on every tick. Expect no Fluent palette churn (no app-wide stock-control re-resolve per tick), with only `NtSidebarBg` moving. → Task 4, `Reapplying_with_only_a_new_window_opacity_leaves_the_fluent_palette_alone`.
3. **A broken theme whose foreground equals its background** (both `#000000`). Expect readable UI text and no exception. → Task 2, `An_unreadable_theme_foreground_is_lifted_for_ui_text`.
4. **Settings is open while the theme changes from somewhere else** (command palette "Theme: X", or Cancel restoring the saved theme). Expect Settings to follow live; today it re-themes only from its own combo box. → Task 6, `SettingsWindow_FollowsAThemeSwitchMadeElsewhere`.
5. **Stock control text on a light theme.** Expect Fluent's `SystemBaseHighColor` to be the theme foreground, not Fluent's default black. → Task 4, `Light_theme_stock_text_is_the_theme_foreground`.

---

## File Structure

**Create (production, `src/Ntilde.App/Shell/Theming/`, namespace `Ntilde.Shell.Theming`):**
- `ThemeColorMath.cs` — pure color math: dark/light, WCAG contrast, compositing, HSL, `SoftenAccent`, `EnsureContrast`, `MidpointHue`.
- `ThemeKeys.cs` — every token key constant plus `All`.
- `ThemeTokens.cs` — immutable token set plus `Derive(TerminalTheme, double)`.
- `FluentPaletteMapping.cs` — tokens → `ColorPaletteResources`.
- `ThemeService.cs` — `Install`, `Apply`, `ApplyTo`, `Current`, `Changed`, plus the private `ThemeTokenDictionary` marker type.

**Create (tests):**
- `tests/Ntilde.App.Tests/Theming/TestThemes.cs` — inline Dracula/Nord/GitHubLight fixtures.
- `tests/Ntilde.App.Tests/Theming/ThemeColorMathTests.cs`
- `tests/Ntilde.App.Tests/Theming/ThemeTokensTests.cs`
- `tests/Ntilde.App.Tests/Theming/ThemeContrastTests.cs`
- `tests/Ntilde.App.Tests/Theming/ThemeServiceTests.cs`
- `tests/Ntilde.App.Tests/Theming/ThemeStartupTests.cs`
- `tests/Ntilde.App.Tests/Theming/SettingsWindowThemeTests.cs`
- `tests/Ntilde.App.Tests/Theming/ConnectionManagerThemeTests.cs`
- `tests/Ntilde.App.Tests/Theming/MarkdownThemeTokenTests.cs`
- `tests/Ntilde.Architecture.Tests/ThemeLiterals/ThemeGuardFiles.cs` — repo paths, shipped XAML, App sources.
- `tests/Ntilde.Architecture.Tests/ThemeLiterals/ColorLiteralFinding.cs`
- `tests/Ntilde.Architecture.Tests/ThemeLiterals/XamlColorLiteralScanner.cs`
- `tests/Ntilde.Architecture.Tests/ThemeLiterals/CSharpColorLiteralScanner.cs`
- `tests/Ntilde.Architecture.Tests/ThemeLiterals/ThemeLiteralPolicy.cs` — token layer paths plus the permanent allowlist.
- `tests/Ntilde.Architecture.Tests/ThemeLiterals/ThemeLiteralScan.cs` — whole-repo scan plus baseline parse/format.
- `tests/Ntilde.Architecture.Tests/ThemeTokenKeyGuardTests.cs`
- `tests/Ntilde.Architecture.Tests/ColorLiteralScannerTests.cs`
- `tests/Ntilde.Architecture.Tests/ThemeLiteralGuardTests.cs`
- `tests/Ntilde.Architecture.Tests/ThemeLiteralBaseline.txt`

**Modify:**
- `src/Ntilde.App/App.axaml` (drop the forced `RequestedThemeVariant="Dark"`), `src/Ntilde.App/App.axaml.cs` (install tokens).
- `src/Ntilde.App/MainWindow.axaml.cs` (`ApplyThemeToUI` pushes to `ThemeService`; drop two Connection Manager `ApplyTheme` calls).
- `src/Ntilde.App/SettingsWindow.axaml`, `src/Ntilde.App/SettingsWindow.axaml.cs`.
- `src/Ntilde.App/Controls/ConnectionManager.axaml`, `.axaml.cs`, `src/Ntilde.App/ConnectionManagerWindow.axaml`, `.axaml.cs`.
- `src/Ntilde.App/AgentOutput/MarkdownTheme.cs`, `src/Ntilde.App/AgentOutput/MarkdownRenderer.cs` (doc comment only).
- `tests/Ntilde.App.Tests/Core/ThemeApplicationRegressionTests.cs` (drop the moved tests, rewrite the Settings card test).
- `tests/Ntilde.App.Tests/BuiltinThemeTests.cs` (`FindThemesDirectory` becomes `internal static`).
- `Directory.Packages.props`, `tests/Ntilde.Architecture.Tests/Ntilde.Architecture.Tests.csproj`.
- `docs/ThemeSystem.md`.

**Delete:** `src/Ntilde.App/Shell/ThemePaletteResources.cs`.

---

### Task 0: Lane and worktree

**Files:** none (git only)

- [ ] **Step 1: Confirm with the user before creating a remote branch**

Pushing `dev-review` to origin is outward-facing. Ask: "Create `dev-review` on origin from current `origin/main`?" Wait for a yes.

- [ ] **Step 2: Create the lane (after the yes)**

```bash
git fetch origin --prune
git ls-remote --heads origin dev-review
```
Expected: no output (the branch does not exist). Then:
```bash
git push origin origin/main:refs/heads/dev-review
```
Expected: `* [new branch] origin/main -> dev-review`.

- [ ] **Step 3: Create the phase worktree from the lane and bring the spec and plan**

Run these from the main checkout `D:\projects\nova2`. Other sessions share it, so do not switch its branch.
```bash
git fetch origin
git worktree add -b feat/theme-v2-foundation .worktrees/theme-v2-foundation origin/dev-review
git -C .worktrees/theme-v2-foundation cherry-pick origin/dev-review..docs/theme-system-spec
git -C .worktrees/theme-v2-foundation log --oneline -3
```
Expected: the top two commits are `docs(theme): …plan…` and `docs(theme): design spec for theme system v2`. All later tasks run inside `.worktrees/theme-v2-foundation`.

- [ ] **Step 4: Baseline build is green**

Run: `scripts/build.ps1 build Ntilde.sln`
Expected: `Build succeeded.` with 0 errors. If it fails, stop: the lane is broken before any change.

---

### Task 1: `ThemeColorMath` — one home for UI color math

**Files:**
- Create: `src/Ntilde.App/Shell/Theming/ThemeColorMath.cs`
- Test: `tests/Ntilde.App.Tests/Theming/ThemeColorMathTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces (all `public static` on `Ntilde.Shell.Theming.ThemeColorMath`):
  - `bool IsDark(Color)`, `double RelativeLuminance(Color)`, `double Contrast(Color foreground, Color background)`
  - `Color Composite(Color top, Color bottom)`, `Color WithAlpha(Color, byte)`, `Color Shift(Color, int delta)`
  - `Color SoftenAccent(Color)`, `Color ShiftLightness(Color, double delta)`
  - `Color EnsureContrast(Color color, IReadOnlyList<Color> surfaces, double target)`, `Color MidpointHue(Color a, Color b)`
  - `(double H, double S, double L) ToHsl(Color)`, `Color FromHsl(byte alpha, double h, double s, double l)`

- [ ] **Step 1: Write the failing tests**

`tests/Ntilde.App.Tests/Theming/ThemeColorMathTests.cs`:
```csharp
using Avalonia.Media;
using Ntilde.Shell.Theming;
using Xunit;

namespace Ntilde.Tests.Theming;

/// <summary>
/// Pure math, so plain [Fact]s: nothing here touches Application.Current (see the
/// headless-poisoning note in ThemeService).
/// </summary>
public class ThemeColorMathTests
{
    [Theory]
    [InlineData(0, 0, 0, true)]
    [InlineData(255, 255, 255, false)]
    [InlineData(127, 127, 127, true)]
    [InlineData(128, 128, 128, false)]
    public void IsDark_uses_the_perceived_luma_half_line(byte r, byte g, byte b, bool dark)
        => Assert.Equal(dark, ThemeColorMath.IsDark(Color.FromRgb(r, g, b)));

    [Fact]
    public void Contrast_matches_the_wcag_extremes()
    {
        Assert.Equal(21.0, ThemeColorMath.Contrast(Colors.White, Colors.Black), 2);
        Assert.Equal(1.0, ThemeColorMath.Contrast(Colors.Black, Colors.Black), 3);
    }

    [Fact]
    public void Contrast_composites_a_translucent_foreground_first()
    {
        // Fully transparent white over black renders as black: no contrast at all.
        Assert.Equal(1.0, ThemeColorMath.Contrast(Color.FromArgb(0, 255, 255, 255), Colors.Black), 3);
    }

    [Fact]
    public void Composite_blends_source_over()
    {
        var blended = ThemeColorMath.Composite(Color.FromArgb(0x80, 255, 255, 255), Colors.Black);
        Assert.Equal(Color.FromRgb(128, 128, 128), blended);
    }

    [Fact]
    public void SoftenAccent_PureBlue_LandsOnACalmerAccentWithHuePreserved()
    {
        // RGB(0,0,238): S clamps 1.0 -> 0.68, L (0.467) is inside the band, hue 240° preserved.
        var softened = ThemeColorMath.SoftenAccent(Color.FromRgb(0, 0, 238));
        Assert.Equal(Color.FromRgb(38, 38, 200), softened);
    }

    [Fact]
    public void SoftenAccent_AlreadyCalmAccent_IsUnchanged()
    {
        var calm = Color.FromRgb(0x62, 0x72, 0xa4);
        Assert.Equal(calm, ThemeColorMath.SoftenAccent(calm));
    }

    [Fact]
    public void EnsureContrast_returns_a_readable_color_unchanged()
    {
        var white = Colors.White;
        Assert.Equal(white, ThemeColorMath.EnsureContrast(white, [Colors.Black], 4.5));
    }

    [Fact]
    public void EnsureContrast_lightens_on_dark_surfaces_keeping_hue_and_saturation()
    {
        var dim = Color.FromRgb(0x33, 0x33, 0x33);
        var fixedColor = ThemeColorMath.EnsureContrast(dim, [Colors.Black], 4.5);

        Assert.True(ThemeColorMath.Contrast(fixedColor, Colors.Black) >= 4.5);
        Assert.True(ThemeColorMath.ToHsl(fixedColor).L > ThemeColorMath.ToHsl(dim).L);
        Assert.Equal(0.0, ThemeColorMath.ToHsl(fixedColor).S, 3);
    }

    [Fact]
    public void EnsureContrast_darkens_on_light_surfaces()
    {
        var pale = Color.FromRgb(0xDD, 0xDD, 0xDD);
        var fixedColor = ThemeColorMath.EnsureContrast(pale, [Colors.White], 4.5);

        Assert.True(ThemeColorMath.Contrast(fixedColor, Colors.White) >= 4.5);
        Assert.True(ThemeColorMath.ToHsl(fixedColor).L < ThemeColorMath.ToHsl(pale).L);
    }

    [Fact]
    public void MidpointHue_takes_the_shortest_arc()
    {
        var orange = ThemeColorMath.MidpointHue(Color.FromRgb(255, 0, 0), Color.FromRgb(255, 255, 0));
        Assert.Equal(30.0, ThemeColorMath.ToHsl(orange).H, 0);

        // 350° and 10° meet at 0°, not 180°.
        var wrap = ThemeColorMath.MidpointHue(
            ThemeColorMath.FromHsl(0xFF, 350, 1, 0.5),
            ThemeColorMath.FromHsl(0xFF, 10, 1, 0.5));
        double h = ThemeColorMath.ToHsl(wrap).H;
        Assert.True(h < 1.0 || h > 359.0, $"hue {h}");
    }

    [Fact]
    public void Shift_clamps_each_channel()
    {
        Assert.Equal(Color.FromRgb(255, 20, 10), ThemeColorMath.Shift(Color.FromRgb(250, 10, 0), 10));
        Assert.Equal(Color.FromRgb(0, 0, 5), ThemeColorMath.Shift(Color.FromRgb(5, 3, 15), -10));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `scripts/build.ps1 test tests/Ntilde.App.Tests/Ntilde.App.Tests.csproj --filter "FullyQualifiedName~Ntilde.Tests.Theming.ThemeColorMathTests"`
Expected: build FAILS with `CS0234`/`CS0246` — `Ntilde.Shell.Theming` / `ThemeColorMath` does not exist.

- [ ] **Step 3: Write the implementation**

`src/Ntilde.App/Shell/Theming/ThemeColorMath.cs`:
```csharp
using System;
using System.Collections.Generic;
using Avalonia.Media;

namespace Ntilde.Shell.Theming;

/// <summary>
/// The one home for UI color math: light/dark classification, WCAG contrast, alpha compositing
/// and HSL adjustments. Before theme v2 the same luminance formula lived in five places with
/// three different thresholds. Everything here is a pure function over <see cref="Color"/> - no
/// Application, no resources - so plain unit tests and the shipped-theme contrast test can call
/// it without a UI thread.
/// </summary>
public static class ThemeColorMath
{
    /// <summary>
    /// Perceived-luma classification: 0.299R + 0.587G + 0.114B below half is dark. This is the
    /// pre-v2 rule, kept so no shipped theme changes sides.
    /// </summary>
    public static bool IsDark(Color color)
        => (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0 < 0.5;

    /// <summary>WCAG 2.x relative luminance of the RGB channels (alpha ignored).</summary>
    public static double RelativeLuminance(Color color)
        => 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);

    /// <summary>
    /// WCAG contrast ratio (1..21). A translucent <paramref name="foreground"/> is composited over
    /// the (forced opaque) <paramref name="background"/> first, because that is what renders.
    /// </summary>
    public static double Contrast(Color foreground, Color background)
    {
        var surface = WithAlpha(background, 0xFF);
        double a = RelativeLuminance(Composite(foreground, surface));
        double b = RelativeLuminance(surface);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>Source-over composite of <paramref name="top"/> onto <paramref name="bottom"/>; the result is opaque.</summary>
    public static Color Composite(Color top, Color bottom)
    {
        double alpha = top.A / 255.0;
        return Color.FromArgb(0xFF, Mix(top.R, bottom.R, alpha), Mix(top.G, bottom.G, alpha), Mix(top.B, bottom.B, alpha));
    }

    public static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>Adds <paramref name="delta"/> to each RGB channel, clamped: the surface-elevation step.</summary>
    public static Color Shift(Color color, int delta)
        => Color.FromArgb(color.A, Clamp(color.R + delta), Clamp(color.G + delta), Clamp(color.B + delta));

    /// <summary>
    /// Pulls a color into a range that works as UI chrome: saturation capped at 0.68 and lightness
    /// held between 0.45 and 0.62. Colors already inside those caps pass through unchanged; extreme
    /// ones like the Default theme's RGB(0,0,238) land on a comfortable indigo-blue. Hue is never
    /// shifted, so the result keeps the theme's identity. (Moved from ThemePaletteResources.)
    /// </summary>
    public static Color SoftenAccent(Color color)
    {
        var (h, s, l) = ToHsl(color);
        return FromHsl(color.A, h, Math.Min(s, 0.68), Math.Clamp(l, 0.45, 0.62));
    }

    public static Color ShiftLightness(Color color, double delta)
    {
        var (h, s, l) = ToHsl(color);
        return FromHsl(color.A, h, s, Math.Clamp(l + delta, 0.0, 1.0));
    }

    /// <summary>
    /// Returns <paramref name="color"/> unchanged if it already reaches <paramref name="target"/>
    /// against every surface. Otherwise it moves HSL lightness in 0.01 steps away from the
    /// surfaces (lighter when the first surface is dark, darker when it is light) until the target
    /// holds or lightness runs out. Hue, saturation and alpha are preserved. Only derived colors go
    /// through this; an explicit theme value is never rewritten.
    /// </summary>
    public static Color EnsureContrast(Color color, IReadOnlyList<Color> surfaces, double target)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        if (surfaces.Count == 0 || MeetsTarget(color, surfaces, target))
        {
            return color;
        }

        double step = IsDark(surfaces[0]) ? 0.01 : -0.01;
        var (h, s, l) = ToHsl(color);
        var candidate = color;
        for (int i = 0; i < 100; i++)
        {
            l = Math.Clamp(l + step, 0.0, 1.0);
            candidate = FromHsl(color.A, h, s, l);
            if (MeetsTarget(candidate, surfaces, target) || l <= 0.0 || l >= 1.0)
            {
                break;
            }
        }

        return candidate;
    }

    /// <summary>
    /// A color whose hue sits midway between <paramref name="a"/> and <paramref name="b"/> on the
    /// shortest arc, with their mean saturation and lightness (e.g. the "agent wrote" orange
    /// between warning yellow and error red).
    /// </summary>
    public static Color MidpointHue(Color a, Color b)
    {
        var (ha, sa, la) = ToHsl(a);
        var (hb, sb, lb) = ToHsl(b);
        double arc = ((hb - ha + 540.0) % 360.0) - 180.0;
        double h = (ha + arc / 2.0 + 360.0) % 360.0;
        return FromHsl(0xFF, h, (sa + sb) / 2.0, (la + lb) / 2.0);
    }

    public static (double H, double S, double L) ToHsl(Color c)
    {
        // Achromatic and channel-dominance comparisons run on the source bytes: they are exact
        // integer checks, immune to the float-equality warning the scaled doubles would raise.
        byte maxByte = Math.Max(c.R, Math.Max(c.G, c.B));
        byte minByte = Math.Min(c.R, Math.Min(c.G, c.B));

        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = maxByte / 255.0, min = minByte / 255.0;
        double l = (max + min) / 2.0;

        if (maxByte == minByte)
        {
            return (0.0, 0.0, l);
        }

        double d = max - min;
        double s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
        double h;
        if (maxByte == c.R)
        {
            h = (g - b) / d + (g < b ? 6.0 : 0.0);
        }
        else if (maxByte == c.G)
        {
            h = (b - r) / d + 2.0;
        }
        else
        {
            h = (r - g) / d + 4.0;
        }

        return (h * 60.0, s, l);
    }

    public static Color FromHsl(byte alpha, double h, double s, double l)
    {
        double c = (1.0 - Math.Abs(2.0 * l - 1.0)) * s;
        double hPrime = h / 60.0;
        double x = c * (1.0 - Math.Abs(hPrime % 2.0 - 1.0));
        double r = 0, g = 0, b = 0;
        if (hPrime < 1) { r = c; g = x; }
        else if (hPrime < 2) { r = x; g = c; }
        else if (hPrime < 3) { g = c; b = x; }
        else if (hPrime < 4) { g = x; b = c; }
        else if (hPrime < 5) { r = x; b = c; }
        else { r = c; b = x; }

        double m = l - c / 2.0;
        return Color.FromArgb(
            alpha,
            (byte)Math.Round(Math.Clamp(r + m, 0.0, 1.0) * 255.0),
            (byte)Math.Round(Math.Clamp(g + m, 0.0, 1.0) * 255.0),
            (byte)Math.Round(Math.Clamp(b + m, 0.0, 1.0) * 255.0));
    }

    private static bool MeetsTarget(Color color, IReadOnlyList<Color> surfaces, double target)
    {
        foreach (var surface in surfaces)
        {
            if (Contrast(color, surface) < target)
            {
                return false;
            }
        }

        return true;
    }

    private static double Linear(byte channel)
    {
        double c = channel / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static byte Mix(byte top, byte bottom, double alpha)
        => (byte)Math.Round(top * alpha + bottom * (1.0 - alpha));

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `scripts/build.ps1 test tests/Ntilde.App.Tests/Ntilde.App.Tests.csproj --filter "FullyQualifiedName~Ntilde.Tests.Theming.ThemeColorMathTests"`
Expected: PASS, 14 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Ntilde.App/Shell/Theming/ThemeColorMath.cs tests/Ntilde.App.Tests/Theming/ThemeColorMathTests.cs
git commit -m "feat(theme): ThemeColorMath, one home for UI color math" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: `ThemeKeys` and `ThemeTokens.Derive`

**Files:**
- Create: `src/Ntilde.App/Shell/Theming/ThemeKeys.cs`, `src/Ntilde.App/Shell/Theming/ThemeTokens.cs`
- Test: `tests/Ntilde.App.Tests/Theming/TestThemes.cs`, `tests/Ntilde.App.Tests/Theming/ThemeTokensTests.cs`

**Interfaces:**
- Consumes: `ThemeColorMath` (Task 1), `Ntilde.VT.TerminalTheme`, `Ntilde.Shell.TermColorHelper.ToAvaloniaColor(TermColor)`.
- Produces:
  - `public static class ThemeKeys` with 63 `public const string` keys (listed below) and `public static IReadOnlyList<string> All`.
  - `public sealed class ThemeTokens`:
    - `static ThemeTokens Derive(TerminalTheme theme, double windowOpacity)`
    - `bool IsDark`, `IReadOnlyDictionary<string, Color> All`, `Color this[string key]`
    - `const double TextContrast = 4.5`, `SecondaryTextContrast = 3.0`, `IndicatorContrast = 3.0`
  - Test helper `Ntilde.Tests.Theming.TestThemes`: `Dracula`, `Nord`, `GitHubLight` (`TerminalTheme` properties returning fresh instances).

- [ ] **Step 1: Write the test fixtures and failing tests**

`tests/Ntilde.App.Tests/Theming/TestThemes.cs`:
```csharp
using Ntilde.VT;

namespace Ntilde.Tests.Theming;

/// <summary>
/// Inline theme fixtures, so theme tests do not depend on which files the test's theme directory
/// holds. Values match the shipped JSON for the colors the tests read.
/// </summary>
internal static class TestThemes
{
    public static TerminalTheme Dracula => new()
    {
        Name = "Dracula",
        Background = TermColor.FromRgb(0x28, 0x2A, 0x36),
        Foreground = TermColor.FromRgb(0xF8, 0xF8, 0xF2),
        Blue = TermColor.FromRgb(0xBD, 0x93, 0xF9),
    };

    public static TerminalTheme Nord => new()
    {
        Name = "Nord",
        Background = TermColor.FromRgb(0x2E, 0x34, 0x40),
        Foreground = TermColor.FromRgb(0xD8, 0xDE, 0xE9),
        Blue = TermColor.FromRgb(0x81, 0xA1, 0xC1),
    };

    public static TerminalTheme GitHubLight => new()
    {
        Name = "GitHub Light",
        Background = TermColor.FromRgb(0xFF, 0xFF, 0xFF),
        Foreground = TermColor.FromRgb(0x24, 0x29, 0x2F),
        Blue = TermColor.FromRgb(0x09, 0x69, 0xDA),
    };
}
```

`tests/Ntilde.App.Tests/Theming/ThemeTokensTests.cs`:
```csharp
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `scripts/build.ps1 test tests/Ntilde.App.Tests/Ntilde.App.Tests.csproj --filter "FullyQualifiedName~Ntilde.Tests.Theming.ThemeTokensTests"`
Expected: build FAILS — `ThemeKeys` / `ThemeTokens` do not exist.

- [ ] **Step 3: Write `ThemeKeys`**

`src/Ntilde.App/Shell/Theming/ThemeKeys.cs`:
```csharp
using System.Collections.Generic;

namespace Ntilde.Shell.Theming;

/// <summary>
/// Resource keys of every UI color token. XAML references them as
/// <c>{DynamicResource NtX}</c>. C# uses these constants: the architecture guard rejects raw
/// <c>"Nt…"</c> strings outside <c>Shell/Theming</c>, and rejects any <c>Nt…</c> key in XAML that
/// is not listed here. The values come from <see cref="ThemeTokens.Derive"/> and are installed
/// app-wide by <see cref="ThemeService"/>.
/// </summary>
public static class ThemeKeys
{
    // Surfaces
    public const string WindowBg = "NtWindowBg";
    public const string ChromeBg = "NtChromeBg";
    public const string SidebarBg = "NtSidebarBg";
    public const string Panel = "NtPanel";
    public const string PanelAlt = "NtPanelAlt";
    public const string Overlay = "NtOverlay";
    public const string Scrim = "NtScrim";

    // Lines
    public const string Hairline = "NtHairline";
    public const string HairlineStrong = "NtHairlineStrong";
    public const string SidebarDivider = "NtSidebarDivider";
    public const string GripHover = "NtGripHover";

    // Text
    public const string Fg = "NtFg";
    public const string Fg2 = "NtFg2";
    public const string Fg3 = "NtFg3";
    public const string Fg4 = "NtFg4";

    // Interaction fills (foreground at low alpha, so they show on light and dark themes alike)
    public const string HoverFill = "NtHoverFill";
    public const string PressedFill = "NtPressedFill";
    public const string SelectedFill = "NtSelectedFill";

    // Accent
    public const string Accent = "NtAccent";
    public const string AccentHover = "NtAccentHover";
    public const string AccentPressed = "NtAccentPressed";
    public const string AccentFg = "NtAccentFg";
    public const string AccentDim = "NtAccentDim";
    public const string AccentFaint = "NtAccentFaint";
    public const string AccentBorder = "NtAccentBorder";

    // Status: each with a tinted background (Dim) and a border
    public const string Success = "NtSuccess";
    public const string SuccessDim = "NtSuccessDim";
    public const string SuccessBorder = "NtSuccessBorder";
    public const string Warning = "NtWarning";
    public const string WarningDim = "NtWarningDim";
    public const string WarningBorder = "NtWarningBorder";
    public const string Error = "NtError";
    public const string ErrorDim = "NtErrorDim";
    public const string ErrorBorder = "NtErrorBorder";
    public const string Info = "NtInfo";
    public const string InfoDim = "NtInfoDim";
    public const string InfoBorder = "NtInfoBorder";
    public const string Attention = "NtAttention";
    public const string AttentionDim = "NtAttentionDim";
    public const string AttentionBorder = "NtAttentionBorder";

    // Semantic aliases: separate keys so a surface's meaning survives a mapping change
    public const string AgentWrote = "NtAgentWrote";
    public const string AgentWatched = "NtAgentWatched";
    public const string AgentIdle = "NtAgentIdle";
    public const string DiffAdded = "NtDiffAdded";
    public const string DiffRemoved = "NtDiffRemoved";
    public const string DiffHunk = "NtDiffHunk";
    public const string Recording = "NtRecording";
    public const string Link = "NtLink";
    public const string LinkHover = "NtLinkHover";

    // Tab strip (both orientations) and the vertical sidebar
    public const string TabFg = "NtTabFg";
    public const string TabFgSelected = "NtTabFgSelected";
    public const string TabHoverFill = "NtTabHoverFill";
    public const string TabSelectedFill = "NtTabSelectedFill";
    public const string TabIndicator = "NtTabIndicator";
    public const string TabPreviewFg = "NtTabPreviewFg";
    public const string TabWorking = "NtTabWorking";
    public const string TabAttention = "NtTabAttention";
    public const string TabAgentWrote = "NtTabAgentWrote";
    public const string TabAgentWatched = "NtTabAgentWatched";
    public const string TabChipActivity = "NtTabChipActivity";
    public const string TabChipAttention = "NtTabChipAttention";
    public const string TabChipAgentWrote = "NtTabChipAgentWrote";
    public const string TabChipAgentWatched = "NtTabChipAgentWatched";

    public static IReadOnlyList<string> All { get; } =
    [
        WindowBg, ChromeBg, SidebarBg, Panel, PanelAlt, Overlay, Scrim,
        Hairline, HairlineStrong, SidebarDivider, GripHover,
        Fg, Fg2, Fg3, Fg4,
        HoverFill, PressedFill, SelectedFill,
        Accent, AccentHover, AccentPressed, AccentFg, AccentDim, AccentFaint, AccentBorder,
        Success, SuccessDim, SuccessBorder, Warning, WarningDim, WarningBorder,
        Error, ErrorDim, ErrorBorder, Info, InfoDim, InfoBorder,
        Attention, AttentionDim, AttentionBorder,
        AgentWrote, AgentWatched, AgentIdle, DiffAdded, DiffRemoved, DiffHunk, Recording, Link, LinkHover,
        TabFg, TabFgSelected, TabHoverFill, TabSelectedFill, TabIndicator, TabPreviewFg,
        TabWorking, TabAttention, TabAgentWrote, TabAgentWatched,
        TabChipActivity, TabChipAttention, TabChipAgentWrote, TabChipAgentWatched,
    ];
}
```

- [ ] **Step 4: Write `ThemeTokens`**

`src/Ntilde.App/Shell/Theming/ThemeTokens.cs`:
```csharp
using System;
using System.Collections.Generic;
using Avalonia.Media;
using Ntilde.VT;
using static Ntilde.Shell.Theming.ThemeColorMath;

namespace Ntilde.Shell.Theming;

/// <summary>
/// The full set of UI color tokens for one theme: an immutable key → color map keyed by
/// <see cref="ThemeKeys"/>. <see cref="Derive"/> is the only producer, and is pure.
/// </summary>
public sealed class ThemeTokens
{
    /// <summary>Minimum contrast for UI text (WCAG AA, normal text).</summary>
    public const double TextContrast = 4.5;

    /// <summary>Minimum contrast for secondary/hint text.</summary>
    public const double SecondaryTextContrast = 3.0;

    /// <summary>Minimum contrast for non-text indicators (status dots, bars).</summary>
    public const double IndicatorContrast = 3.0;

    // Text-on-accent candidates: white, or the dark navy the Settings XAML always used.
    private static readonly Color LightInk = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private static readonly Color DarkInk = Color.FromRgb(0x0A, 0x16, 0x22);

    private readonly Dictionary<string, Color> _colors;

    private ThemeTokens(Dictionary<string, Color> colors, bool isDark)
    {
        _colors = colors;
        IsDark = isDark;
    }

    /// <summary>Whether the theme background classifies as dark (drives the Fluent variant).</summary>
    public bool IsDark { get; }

    public IReadOnlyDictionary<string, Color> All => _colors;

    public Color this[string key] => _colors[key];

    /// <summary>
    /// Derives every token from the theme's existing 20 colors. Surfaces and text are
    /// contrast-fixed against each other. <paramref name="windowOpacity"/> only affects
    /// <see cref="ThemeKeys.SidebarBg"/>, the one surface that lets a background image show through.
    /// </summary>
    public static ThemeTokens Derive(TerminalTheme theme, double windowOpacity)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var c = new Dictionary<string, Color>(StringComparer.Ordinal);

        var bg = Opaque(theme.Background);
        var themeFg = Opaque(theme.Foreground);
        bool dark = IsDark(bg);

        // Surfaces elevate lighter on dark themes and darker on light ones (the pre-v2 rule). A
        // mid-tone background can make that direction push text under 4.5:1 on panels; flip then.
        int direction = dark ? 1 : -1;
        double usual = Contrast(themeFg, Shift(bg, 18 * direction));
        if (usual < TextContrast && Contrast(themeFg, Shift(bg, -18 * direction)) > usual)
        {
            direction = -direction;
        }

        c[ThemeKeys.WindowBg] = bg;
        c[ThemeKeys.ChromeBg] = Shift(bg, 10 * direction);
        c[ThemeKeys.Panel] = Shift(bg, 18 * direction);
        c[ThemeKeys.PanelAlt] = Shift(bg, 6 * direction);
        c[ThemeKeys.Overlay] = Shift(bg, 24 * direction);
        c[ThemeKeys.Scrim] = Color.FromArgb(dark ? (byte)0x80 : (byte)0x40, 0, 0, 0);
        c[ThemeKeys.SidebarBg] = WithAlpha(c[ThemeKeys.ChromeBg], (byte)Math.Round(255.0 * Math.Clamp(windowOpacity, 0.0, 1.0)));
        c[ThemeKeys.Hairline] = Shift(bg, 28 * direction);
        c[ThemeKeys.HairlineStrong] = Shift(bg, 40 * direction);

        Color[] textSurfaces = [c[ThemeKeys.WindowBg], c[ThemeKeys.ChromeBg], c[ThemeKeys.Panel], c[ThemeKeys.PanelAlt], c[ThemeKeys.Overlay]];
        var fg = EnsureContrast(themeFg, textSurfaces, TextContrast);
        c[ThemeKeys.Fg] = fg;
        c[ThemeKeys.Fg2] = WithAlpha(fg, 0xC8);
        c[ThemeKeys.Fg3] = WithAlpha(fg, 0x9A);
        c[ThemeKeys.Fg4] = WithAlpha(fg, 0x6E);

        c[ThemeKeys.HoverFill] = WithAlpha(fg, 0x14);
        c[ThemeKeys.SelectedFill] = WithAlpha(fg, 0x26);
        c[ThemeKeys.PressedFill] = WithAlpha(fg, 0x30);

        // Accent: soften the ANSI blue for chrome use, pick the better-contrasting ink, and if even
        // that ink misses 4.5:1, move the accent away from it.
        var accent = SoftenAccent(Opaque(theme.Blue));
        var accentFg = Contrast(LightInk, accent) >= Contrast(DarkInk, accent) ? LightInk : DarkInk;
        accent = EnsureContrast(accent, [accentFg], TextContrast);
        c[ThemeKeys.Accent] = accent;
        c[ThemeKeys.AccentFg] = accentFg;
        c[ThemeKeys.AccentHover] = ShiftLightness(accent, 0.08);
        c[ThemeKeys.AccentPressed] = ShiftLightness(accent, -0.06);
        c[ThemeKeys.AccentDim] = WithAlpha(accent, 0x24);
        c[ThemeKeys.AccentFaint] = WithAlpha(accent, 0x15);
        c[ThemeKeys.AccentBorder] = WithAlpha(accent, 0x4D);

        // Status: softened ANSI, readable as text on every panel-like surface.
        Color[] statusSurfaces = [c[ThemeKeys.Panel], c[ThemeKeys.WindowBg], c[ThemeKeys.Overlay]];
        Color Status(TermColor ansi) => EnsureContrast(SoftenAccent(Opaque(ansi)), statusSurfaces, TextContrast);
        var success = Status(theme.Green);
        var warning = Status(theme.Yellow);
        var error = Status(theme.Red);
        var info = Status(theme.Cyan);
        SetStatus(c, ThemeKeys.Success, ThemeKeys.SuccessDim, ThemeKeys.SuccessBorder, success);
        SetStatus(c, ThemeKeys.Warning, ThemeKeys.WarningDim, ThemeKeys.WarningBorder, warning);
        SetStatus(c, ThemeKeys.Error, ThemeKeys.ErrorDim, ThemeKeys.ErrorBorder, error);
        SetStatus(c, ThemeKeys.Info, ThemeKeys.InfoDim, ThemeKeys.InfoBorder, info);
        SetStatus(c, ThemeKeys.Attention, ThemeKeys.AttentionDim, ThemeKeys.AttentionBorder, warning);

        // Aliases. AgentWrote gets its own orange between warning and error: the sidebar dot must
        // tell "agent wrote" apart from "attention" (spec §4), so it is never merged into Warning.
        var agentWrote = EnsureContrast(MidpointHue(warning, error), statusSurfaces, TextContrast);
        c[ThemeKeys.AgentWrote] = agentWrote;
        c[ThemeKeys.AgentWatched] = info;
        c[ThemeKeys.AgentIdle] = c[ThemeKeys.Fg4];
        c[ThemeKeys.DiffAdded] = success;
        c[ThemeKeys.DiffRemoved] = error;
        c[ThemeKeys.DiffHunk] = warning;
        c[ThemeKeys.Recording] = error;
        c[ThemeKeys.Link] = accent;
        c[ThemeKeys.LinkHover] = c[ThemeKeys.AccentHover];

        // Tab strip. The horizontal strip sits on the title bar (ChromeBg); the vertical sidebar's
        // opaque tone is the same ChromeBg, so one surface list covers both orientations.
        Color[] strip = [c[ThemeKeys.ChromeBg]];
        c[ThemeKeys.TabFg] = c[ThemeKeys.Fg2];
        c[ThemeKeys.TabFgSelected] = fg;
        c[ThemeKeys.TabHoverFill] = c[ThemeKeys.HoverFill];
        c[ThemeKeys.TabSelectedFill] = c[ThemeKeys.SelectedFill];
        c[ThemeKeys.TabIndicator] = accent;
        c[ThemeKeys.TabPreviewFg] = EnsureContrast(Composite(c[ThemeKeys.Fg3], c[ThemeKeys.ChromeBg]), strip, TextContrast);
        c[ThemeKeys.TabWorking] = EnsureContrast(accent, strip, IndicatorContrast);
        c[ThemeKeys.TabAttention] = EnsureContrast(warning, strip, IndicatorContrast);
        c[ThemeKeys.TabAgentWrote] = EnsureContrast(agentWrote, strip, IndicatorContrast);
        c[ThemeKeys.TabAgentWatched] = EnsureContrast(info, strip, IndicatorContrast);
        c[ThemeKeys.TabChipActivity] = c[ThemeKeys.TabPreviewFg];
        c[ThemeKeys.TabChipAttention] = EnsureContrast(warning, strip, TextContrast);
        c[ThemeKeys.TabChipAgentWrote] = EnsureContrast(agentWrote, strip, TextContrast);
        c[ThemeKeys.TabChipAgentWatched] = EnsureContrast(info, strip, TextContrast);
        c[ThemeKeys.SidebarDivider] = c[ThemeKeys.Hairline];
        c[ThemeKeys.GripHover] = c[ThemeKeys.AccentBorder];

        return new ThemeTokens(c, dark);
    }

    private static Color Opaque(TermColor color) => WithAlpha(color.ToAvaloniaColor(), 0xFF);

    private static void SetStatus(Dictionary<string, Color> c, string key, string dimKey, string borderKey, Color color)
    {
        c[key] = color;
        c[dimKey] = WithAlpha(color, 0x24);
        c[borderKey] = WithAlpha(color, 0x4D);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `scripts/build.ps1 test tests/Ntilde.App.Tests/Ntilde.App.Tests.csproj --filter "FullyQualifiedName~Ntilde.Tests.Theming.ThemeTokensTests"`
Expected: PASS, 12 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Ntilde.App/Shell/Theming/ThemeKeys.cs src/Ntilde.App/Shell/Theming/ThemeTokens.cs tests/Ntilde.App.Tests/Theming/TestThemes.cs tests/Ntilde.App.Tests/Theming/ThemeTokensTests.cs
git commit -m "feat(theme): ThemeKeys and ThemeTokens.Derive, the app-wide token vocabulary" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Contrast test over every shipped theme and stress cases

**Files:**
- Modify: `tests/Ntilde.App.Tests/BuiltinThemeTests.cs:84` (`private static string FindThemesDirectory()` → `internal static string FindThemesDirectory()`)
- Create: `tests/Ntilde.App.Tests/Theming/ThemeContrastTests.cs`

**Interfaces:**
- Consumes: `ThemeTokens.Derive`, `ThemeTokens.TextContrast/SecondaryTextContrast/IndicatorContrast`, `ThemeColorMath.Contrast/Composite`, `BuiltinThemeTests.FindThemesDirectory()`, `AppJsonContext.Default.TerminalTheme`.
- Produces: the gating contrast test (no API).

- [ ] **Step 1: Make the themes-directory helper reusable**

In `tests/Ntilde.App.Tests/BuiltinThemeTests.cs`, change line 84 from
```csharp
        private static string FindThemesDirectory()
```
to
```csharp
        internal static string FindThemesDirectory()
```

- [ ] **Step 2: Write the contrast test**

`tests/Ntilde.App.Tests/Theming/ThemeContrastTests.cs`:
```csharp
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
```

- [ ] **Step 3: Run it**

Run: `scripts/build.ps1 test tests/Ntilde.App.Tests/Ntilde.App.Tests.csproj --filter "FullyQualifiedName~Ntilde.Tests.Theming.ThemeContrastTests"`
Expected: PASS, 90 cases (5 theories × 18 themes).

If a case fails: the message names the pair and theme. Fix `ThemeTokens.Derive` (e.g. add the failing surface to that token's `EnsureContrast` surface list) and re-run Task 2's tests too. Never lower a threshold. Record the fix in the commit message.

- [ ] **Step 4: Commit**

```bash
git add tests/Ntilde.App.Tests/BuiltinThemeTests.cs tests/Ntilde.App.Tests/Theming/ThemeContrastTests.cs
git commit -m "test(theme): gating contrast test over shipped and stress themes" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: `ThemeService` and the Fluent palette

**Files:**
- Create: `src/Ntilde.App/Shell/Theming/FluentPaletteMapping.cs`, `src/Ntilde.App/Shell/Theming/ThemeService.cs`
- Test: `tests/Ntilde.App.Tests/Theming/ThemeServiceTests.cs`

**Interfaces:**
- Consumes: `ThemeTokens.Derive`, `ThemeKeys`, `ThemeColorMath.WithAlpha/Composite`.
- Produces (`public static class ThemeService`):
  - `ThemeTokens Current { get; }`
  - `event EventHandler<ThemeTokens>? Changed`
  - `void Install(Application app, TerminalTheme theme, double windowOpacity)` — call from `Application.Initialize`.
  - `void Apply(TerminalTheme theme, double windowOpacity)` — UI thread only.
  - `void ApplyTo(IResourceDictionary scope, ThemeTokens tokens)` — mutate-or-add a `SolidColorBrush` per key.
- Produces (`internal static class FluentPaletteMapping`): `void Write(ColorPaletteResources, ThemeTokens)`, `bool SameInputs(ThemeTokens, ThemeTokens)`.

- [ ] **Step 1: Write the failing tests**

`tests/Ntilde.App.Tests/Theming/ThemeServiceTests.cs`:
```csharp
using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Ntilde.Shell.Theming;
using Xunit;

namespace Ntilde.Tests.Theming;

/// <summary>
/// Every test touches Application.Current resources, so every test is an [AvaloniaFact]. A plain
/// [Fact] here would run off the session thread and poison the headless run.
/// </summary>
public class ThemeServiceTests
{
    [AvaloniaFact]
    public void Apply_mutates_the_installed_brushes_in_place()
    {
        ThemeService.Apply(TestThemes.Dracula, 1.0);
        var panel = AppBrush(ThemeKeys.Panel);

        ThemeService.Apply(TestThemes.GitHubLight, 1.0);

        Assert.Same(panel, AppBrush(ThemeKeys.Panel));
        Assert.Equal(ThemeTokens.Derive(TestThemes.GitHubLight, 1.0)[ThemeKeys.Panel], panel.Color);
    }

    [AvaloniaFact]
    public void Apply_sets_the_application_variant_from_the_background()
    {
        ThemeService.Apply(TestThemes.GitHubLight, 1.0);
        Assert.Equal(ThemeVariant.Light, Application.Current!.RequestedThemeVariant);

        ThemeService.Apply(TestThemes.Dracula, 1.0);
        Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
    }

    [AvaloniaFact]
    public void Apply_raises_Changed_once_with_the_new_tokens()
    {
        ThemeTokens? seen = null;
        int calls = 0;
        void Handler(object? sender, ThemeTokens tokens) { calls++; seen = tokens; }
        ThemeService.Changed += Handler;
        try
        {
            ThemeService.Apply(TestThemes.Nord, 1.0);
        }
        finally
        {
            ThemeService.Changed -= Handler;
        }

        Assert.Equal(1, calls);
        Assert.Same(ThemeService.Current, seen);
        Assert.Equal(ThemeTokens.Derive(TestThemes.Nord, 1.0)[ThemeKeys.Accent], seen![ThemeKeys.Accent]);
    }

    [AvaloniaFact]
    public async Task Apply_off_the_ui_thread_throws()
    {
        var error = await Task.Run(() => Record.Exception(() => ThemeService.Apply(TestThemes.Nord, 1.0)));
        Assert.IsType<InvalidOperationException>(error);
    }

    [AvaloniaFact]
    public void ApplyTo_writes_only_the_given_scope()
    {
        ThemeService.Apply(TestThemes.Dracula, 1.0);
        var appPanel = AppBrush(ThemeKeys.Panel).Color;
        var scope = new ResourceDictionary();
        var light = ThemeTokens.Derive(TestThemes.GitHubLight, 1.0);

        ThemeService.ApplyTo(scope, light);

        Assert.Equal(light[ThemeKeys.Panel], Assert.IsType<SolidColorBrush>(scope[ThemeKeys.Panel]).Color);
        Assert.Equal(appPanel, AppBrush(ThemeKeys.Panel).Color);
    }

    /// <summary>
    /// The spike the design depends on: in Avalonia 12.0.4 only an Accent change makes
    /// ColorPaletteResources notify, so a same-variant switch (dark → dark) reaches stock controls
    /// only through FluentPaletteMapping's Accent reset.
    /// </summary>
    [AvaloniaFact]
    public void Stock_fluent_brushes_follow_a_same_variant_theme_switch()
    {
        var border = new Border();
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("SystemControlBackgroundChromeMediumLowBrush"));
        var window = new Window { Content = border };
        window.Show();
        try
        {
            ThemeService.Apply(TestThemes.Dracula, 1.0);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ThemeTokens.Derive(TestThemes.Dracula, 1.0)[ThemeKeys.Overlay], ((ISolidColorBrush)border.Background!).Color);

            ThemeService.Apply(TestThemes.Nord, 1.0);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ThemeTokens.Derive(TestThemes.Nord, 1.0)[ThemeKeys.Overlay], ((ISolidColorBrush)border.Background!).Color);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Fluent_accent_is_the_theme_accent()
    {
        ThemeService.Apply(TestThemes.Nord, 1.0);

        Assert.True(Application.Current!.TryFindResource("SystemAccentColor", ThemeVariant.Dark, out var value));
        Assert.Equal(ThemeTokens.Derive(TestThemes.Nord, 1.0)[ThemeKeys.Accent], Assert.IsType<Color>(value));
    }

    // Review Focus 5
    [AvaloniaFact]
    public void Light_theme_stock_text_is_the_theme_foreground()
    {
        ThemeService.Apply(TestThemes.GitHubLight, 1.0);

        Assert.True(Application.Current!.TryFindResource("SystemBaseHighColor", ThemeVariant.Light, out var value));
        Assert.Equal(ThemeTokens.Derive(TestThemes.GitHubLight, 1.0)[ThemeKeys.Fg], Assert.IsType<Color>(value));
    }

    // Review Focus 2
    [AvaloniaFact]
    public void Reapplying_with_only_a_new_window_opacity_leaves_the_fluent_palette_alone()
    {
        var fluent = Application.Current!.Styles.OfType<FluentTheme>().Single();
        ThemeService.Apply(TestThemes.Dracula, 1.0);
        var palette = fluent.Palettes[ThemeVariant.Dark];
        int accentChanges = 0;
        void Handler(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ColorPaletteResources.AccentProperty) accentChanges++;
        }

        palette.PropertyChanged += Handler;
        try
        {
            ThemeService.Apply(TestThemes.Dracula, 0.6);
        }
        finally
        {
            palette.PropertyChanged -= Handler;
        }

        Assert.Equal(0, accentChanges);
        Assert.Equal(153, AppBrush(ThemeKeys.SidebarBg).Color.A);
    }

    private static SolidColorBrush AppBrush(string key)
    {
        Assert.True(Application.Current!.TryFindResource(key, out var value), key);
        return Assert.IsType<SolidColorBrush>(value);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `scripts/build.ps1 test tests/Ntilde.App.Tests/Ntilde.App.Tests.csproj --filter "FullyQualifiedName~Ntilde.Tests.Theming.ThemeServiceTests" --blame-hang-timeout 5m`
Expected: build FAILS — `ThemeService` does not exist.

- [ ] **Step 3: Write `FluentPaletteMapping`**

`src/Ntilde.App/Shell/Theming/FluentPaletteMapping.cs`:
```csharp
using System.Linq;
using Avalonia.Themes.Fluent;
using static Ntilde.Shell.Theming.ThemeColorMath;

namespace Ntilde.Shell.Theming;

/// <summary>
/// Maps tokens onto Fluent's per-variant palette, so stock controls (ComboBox dropdowns, context
/// menus, tooltips, text boxes, scrollbars) take the theme's tones instead of stock grey. The
/// ChromeBlack* colors are left at Fluent's defaults: they are shadow and overlay blacks, the
/// same on every theme.
/// </summary>
internal static class FluentPaletteMapping
{
    // Tokens the palette reads. Apply skips the palette when none of them changed: the
    // window-opacity slider re-applies on every tick and moves only SidebarBg.
    private static readonly string[] SourceKeys =
    [
        ThemeKeys.WindowBg, ThemeKeys.ChromeBg, ThemeKeys.Panel, ThemeKeys.Overlay,
        ThemeKeys.Hairline, ThemeKeys.HairlineStrong,
        ThemeKeys.Fg, ThemeKeys.Fg2, ThemeKeys.Fg3, ThemeKeys.Fg4,
        ThemeKeys.HoverFill, ThemeKeys.PressedFill, ThemeKeys.Error, ThemeKeys.Accent,
    ];

    public static bool SameInputs(ThemeTokens a, ThemeTokens b)
        => a.IsDark == b.IsDark && SourceKeys.All(key => a[key] == b[key]);

    public static void Write(ColorPaletteResources palette, ThemeTokens t)
    {
        var bg = t[ThemeKeys.WindowBg];
        var fg = t[ThemeKeys.Fg];

        palette.RegionColor = bg;
        palette.AltHigh = bg;
        palette.AltMediumHigh = WithAlpha(bg, 0xCC);
        palette.AltMedium = WithAlpha(bg, 0x99);
        palette.AltMediumLow = WithAlpha(bg, 0x66);
        palette.AltLow = WithAlpha(bg, 0x33);
        palette.BaseHigh = fg;
        palette.BaseMediumHigh = WithAlpha(fg, 0xCC);
        palette.BaseMedium = WithAlpha(fg, 0x99);
        palette.BaseMediumLow = WithAlpha(fg, 0x66);
        palette.BaseLow = WithAlpha(fg, 0x33);
        palette.ChromeAltLow = Composite(t[ThemeKeys.Fg2], bg);
        palette.ChromeDisabledHigh = t[ThemeKeys.Hairline];
        palette.ChromeDisabledLow = Composite(t[ThemeKeys.Fg4], bg);
        palette.ChromeGray = Composite(t[ThemeKeys.Fg3], bg);
        palette.ChromeHigh = t[ThemeKeys.HairlineStrong];
        palette.ChromeLow = t[ThemeKeys.ChromeBg];
        palette.ChromeMedium = t[ThemeKeys.Panel];
        palette.ChromeMediumLow = t[ThemeKeys.Overlay];
        palette.ChromeWhite = fg;
        palette.ListLow = t[ThemeKeys.HoverFill];
        palette.ListMedium = t[ThemeKeys.PressedFill];
        palette.ErrorText = t[ThemeKeys.Error];

        // Accent LAST, and via default. In Avalonia 12.0.4 every property above stores its value
        // (ColorPaletteResources.SetColor) without raising ResourcesChanged. Only an Accent change
        // does. Resetting and re-setting it guarantees one notification carries everything above,
        // even when two themes share an accent.
        palette.Accent = default;
        palette.Accent = t[ThemeKeys.Accent];
    }
}
```

- [ ] **Step 4: Write `ThemeService`**

`src/Ntilde.App/Shell/Theming/ThemeService.cs`:
```csharp
using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Ntilde.VT;

namespace Ntilde.Shell.Theming;

/// <summary>
/// App-wide UI theming. It installs one dictionary of token brushes into
/// <c>Application.Resources</c>, and on every theme change mutates those brushes in place, writes
/// Fluent's palette and sets the application's theme variant. Every window, dialog and control
/// resolves <c>{DynamicResource Nt…}</c> up the tree to that dictionary, so none of them wire
/// anything per window. (Before v2 only Settings and Connection Manager followed the theme, each
/// through its own private copy.) Same shape as <see cref="UiScale"/>.
/// </summary>
public static class ThemeService
{
    private static readonly ThemeVariant[] Variants = [ThemeVariant.Light, ThemeVariant.Dark];

    /// <summary>The tokens most recently applied (Default theme before the first apply).</summary>
    public static ThemeTokens Current { get; private set; } = ThemeTokens.Derive(new TerminalTheme(), 1.0);

    /// <summary>Raised after every <see cref="Apply"/> with the new tokens.</summary>
    public static event EventHandler<ThemeTokens>? Changed;

    /// <summary>
    /// Creates the token dictionary and both Fluent palettes. Call from
    /// <c>Application.Initialize</c>, after the XAML load (so the FluentTheme exists) and before
    /// any window is constructed, so every token resolves from the first layout pass.
    /// </summary>
    public static void Install(Application app, TerminalTheme theme, double windowOpacity)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(theme);

        var dictionary = FindTokenDictionary(app);
        if (dictionary is null)
        {
            dictionary = new ThemeTokenDictionary();
            app.Resources.MergedDictionaries.Add(dictionary);
        }

        if (FindFluent(app) is { } fluent)
        {
            // Both variants get a palette up front, so later applies only mutate an existing one.
            foreach (var variant in Variants)
            {
                if (!fluent.Palettes.ContainsKey(variant))
                {
                    fluent.Palettes[variant] = new ColorPaletteResources();
                }
            }
        }

        var tokens = ThemeTokens.Derive(theme, windowOpacity);
        Write(app, dictionary, previous: null, tokens);
        Current = tokens;
    }

    /// <summary>
    /// Applies <paramref name="theme"/> to every open and future window. Safe without a running
    /// <see cref="Application"/>: then only <see cref="Current"/> and <see cref="Changed"/> update.
    /// </summary>
    public static void Apply(TerminalTheme theme, double windowOpacity)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var tokens = ThemeTokens.Derive(theme, windowOpacity);

        if (Application.Current is { } app)
        {
            // Same fail-fast as UiScale.Apply. Off the UI thread, mutating application resources does
            // not throw here; it poisons Avalonia's media context for every window that lays out
            // afterwards (the headless suite once lost 29 unrelated tests that way).
            Dispatcher.UIThread.VerifyAccess();

            if (FindTokenDictionary(app) is { } dictionary)
            {
                Write(app, dictionary, Current, tokens);
            }
            else
            {
                Install(app, theme, windowOpacity);
            }
        }

        Current = tokens;
        Changed?.Invoke(null, tokens);
    }

    /// <summary>
    /// Writes <paramref name="tokens"/> into <paramref name="scope"/>: an existing brush is
    /// recolored in place (so every holder of it updates), a missing one is added. Used for the app
    /// dictionary and for local scopes (a theme preview, a profile-themed pane).
    /// </summary>
    public static void ApplyTo(IResourceDictionary scope, ThemeTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(tokens);

        foreach (var (key, color) in tokens.All)
        {
            if (scope.TryGetValue(key, out var existing) && existing is SolidColorBrush brush)
            {
                brush.Color = color;
            }
            else
            {
                scope[key] = new SolidColorBrush(color);
            }
        }
    }

    private static void Write(Application app, ThemeTokenDictionary dictionary, ThemeTokens? previous, ThemeTokens tokens)
    {
        ApplyTo(dictionary, tokens);

        var variant = tokens.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        if (FindFluent(app) is { } fluent
            && fluent.Palettes.TryGetValue(variant, out var palette)
            && (previous is null || !FluentPaletteMapping.SameInputs(previous, tokens)))
        {
            FluentPaletteMapping.Write(palette, tokens);
        }

        // Palette first, variant second: a variant flip re-resolves every Fluent resource and must
        // find the new palette already in place.
        app.RequestedThemeVariant = variant;
    }

    private static ThemeTokenDictionary? FindTokenDictionary(Application app)
        => app.Resources.MergedDictionaries.OfType<ThemeTokenDictionary>().FirstOrDefault();

    private static FluentTheme? FindFluent(Application app)
        => app.Styles.OfType<FluentTheme>().FirstOrDefault();

    /// <summary>Marker type so the service finds its own dictionary among the merged ones.</summary>
    private sealed class ThemeTokenDictionary : ResourceDictionary
    {
    }
}
```

- [ ] **Step 5: Run the tests**

`ThemeService.Apply` installs on first use when the app has no token dictionary, so these tests pass before Task 5 wires `Install` into `App.Initialize`.

Run: `scripts/build.ps1 test tests/Ntilde.App.Tests/Ntilde.App.Tests.csproj --filter "FullyQualifiedName~Ntilde.Tests.Theming.ThemeServiceTests" --blame-hang-timeout 5m`
Expected: PASS, 9 tests.

**Decision point — `Stock_fluent_brushes_follow_a_same_variant_theme_switch`:**
- **PASS:** continue.
- **FAIL on its second assertion only:** in-place palette mutation does not reach stock brushes. In `ThemeService.Write`, replace the `FluentPaletteMapping.Write(palette, tokens);` call with
  ```csharp
              var replacement = new ColorPaletteResources();
              FluentPaletteMapping.Write(replacement, tokens);
              fluent.Palettes[variant] = replacement;
  ```
  Then re-run. Swapping the palette object goes through the collection's owner add/remove path.
- **Still FAIL:** stop. Report to the user that the Fluent-palette part of the approved design (spec §3 step 5) cannot be met on Avalonia 12.0.4. Propose keeping only the variant switch for stock controls. Do not continue past this task without their answer.

- [ ] **Step 6: Commit**

```bash
git add src/Ntilde.App/Shell/Theming/FluentPaletteMapping.cs src/Ntilde.App/Shell/Theming/ThemeService.cs tests/Ntilde.App.Tests/Theming/ThemeServiceTests.cs
git commit -m "feat(theme): ThemeService installs app-wide tokens and the Fluent palette" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Install at startup; `MainWindow` pushes the active theme

**Files:**
- Modify: `src/Ntilde.App/App.axaml:6-7`, `src/Ntilde.App/App.axaml.cs:13-16`, `src/Ntilde.App/MainWindow.axaml.cs` (`ApplyThemeToUI`, ~line 6672)
- Test: `tests/Ntilde.App.Tests/Theming/ThemeStartupTests.cs`

**Interfaces:**
- Consumes: `ThemeService.Install/Apply`, `ThemeKeys`.
- Produces: tokens exist in every `App` instance from `Initialize` on (production and headless tests); every `ApplyThemeToUI` call (startup, palette "Theme:", Settings preview/opacity/background image/save/cancel) re-applies app tokens.

- [ ] **Step 1: Write the failing tests**

`tests/Ntilde.App.Tests/Theming/ThemeStartupTests.cs`:
```csharp
using System;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Ntilde.Shell;
using Ntilde.Shell.Theming;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.Theming;

public sealed class ThemeStartupTests : IDisposable
{
    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

    [AvaloniaFact]
    public void App_installs_theme_tokens_before_any_window_exists()
    {
        Assert.True(Application.Current!.Resources.MergedDictionaries.Count > 0);
        Assert.True(Application.Current.TryFindResource(ThemeKeys.Panel, out var value));
        Assert.IsType<SolidColorBrush>(value);
    }

    [AvaloniaFact]
    public void MainWindow_theme_refresh_pushes_the_active_theme_to_the_app()
    {
        var window = TestMainWindowFactory.Create();
        var settings = GetSettings(window);
        // Mutating the cached ActiveTheme keeps this independent of the test theme directory.
        settings.ActiveTheme.Background = TermColor.FromRgb(245, 242, 233);

        InvokePrivate(window, "ApplyThemeToUI");

        Assert.Equal(ThemeVariant.Light, Application.Current!.RequestedThemeVariant);
        Assert.True(Application.Current.TryFindResource(ThemeKeys.WindowBg, out var value));
        Assert.Equal(Color.FromRgb(245, 242, 233), Assert.IsType<SolidColorBrush>(value).Color);
    }

    private static TerminalSettings GetSettings(Ntilde.MainWindow window)
    {
        var field = typeof(Ntilde.MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (TerminalSettings)field!.GetValue(window)!;
    }

    private static void InvokePrivate(Ntilde.MainWindow window, string name)
    {
        var method = typeof(Ntilde.MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(window, Array.Empty<object>());
    }
}
```

- [ ] **Step 2: Run to verify the second test fails**

Run: `scripts/build.ps1 test tests/Ntilde.App.Tests/Ntilde.App.Tests.csproj --filter "FullyQualifiedName~Ntilde.Tests.Theming.ThemeStartupTests" --blame-hang-timeout 5m`
Expected: `App_installs_theme_tokens_before_any_window_exists` FAILS (`TryFindResource` false), and `MainWindow_theme_refresh…` FAILS (the variant stays at the window-level override and the app has no `NtWindowBg`).

- [ ] **Step 3: Install in `App.Initialize` and drop the forced variant**

`src/Ntilde.App/App.axaml` lines 1-7 become:
```xml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:core="clr-namespace:Ntilde.Shell"
             xmlns:ui="clr-namespace:Ntilde.UI"
             x:Class="Ntilde.App">
             <!-- The theme variant is set by ThemeService from the active terminal theme. -->
```

`src/Ntilde.App/App.axaml.cs`: add `using Ntilde.Shell.Theming;` next to the other usings, and replace `Initialize` with:
```csharp
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Tokens must exist before any window is built. The Default theme is a placeholder:
        // MainWindow's first ApplyThemeToUI applies the saved theme before the window shows.
        ThemeService.Install(this, new TerminalTheme(), windowOpacity: 1.0);
    }
```

- [ ] **Step 4: `ApplyThemeToUI` pushes to the service**

In `src/Ntilde.App/MainWindow.axaml.cs` add `using Ntilde.Shell.Theming;` to the usings, then change the start of `ApplyThemeToUI` from
```csharp
        private void ApplyThemeToUI()
        {
            var theme = _settings.ActiveTheme;
```
to
```csharp
        private void ApplyThemeToUI()
        {
            var theme = _settings.ActiveTheme;

            // App-wide tokens, the Fluent palette and the variant come first. Every DynamicResource
            // consumer (Settings, Connection Manager, Agent Output, stock controls) updates from this
            // one call. The imperative per-control code below moves onto tokens in phase 2.
            ThemeService.Apply(theme, _settings.WindowOpacity);
```

- [ ] **Step 5: Run the tests**

Run: `scripts/build.ps1 test tests/Ntilde.App.Tests/Ntilde.App.Tests.csproj --filter "FullyQualifiedName~Ntilde.Tests.Theming" --blame-hang-timeout 5m`
Expected: PASS (all Theming tests so far, including Task 4's).

- [ ] **Step 6: Commit**

```bash
git add src/Ntilde.App/App.axaml src/Ntilde.App/App.axaml.cs src/Ntilde.App/MainWindow.axaml.cs tests/Ntilde.App.Tests/Theming/ThemeStartupTests.cs
git commit -m "feat(theme): install tokens at startup and push every theme refresh app-wide" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Settings window on app tokens

**Files:**
- Modify: `src/Ntilde.App/SettingsWindow.axaml` (lines 8-9, 12-30, 155-164, 236, 433-448, every `{StaticResource Nt…}`), `src/Ntilde.App/SettingsWindow.axaml.cs` (lines 1061-1062, 1923, 2063-2068, 2079-2080, 2288-2289, 2298, 2886-2887, 3229-3254)
- Modify: `tests/Ntilde.App.Tests/Core/ThemeApplicationRegressionTests.cs:104-131`
- Test: `tests/Ntilde.App.Tests/Theming/SettingsWindowThemeTests.cs`

**Interfaces:**
- Consumes: `ThemeKeys`, `ThemeService.Apply`.
- Produces: `SettingsWindow` declares no local color tokens. `SettingsWindow.ApplyTheme(TerminalTheme?)` (private) becomes `ThemeService.Apply(theme ?? _settings.ActiveTheme, _settings.WindowOpacity)`.

- [ ] **Step 1: Write the failing tests**

`tests/Ntilde.App.Tests/Theming/SettingsWindowThemeTests.cs`:
```csharp
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Ntilde.Shell.Theming;
using Xunit;

namespace Ntilde.Tests.Theming;

public class SettingsWindowThemeTests
{
    [AvaloniaFact]
    public void SettingsWindow_DeclaresNoLocalThemeTokens()
    {
        var window = new Ntilde.SettingsWindow();
        var localKeys = window.Resources.Keys.OfType<string>().ToList();
        Assert.DoesNotContain(localKeys, key => ThemeKeys.All.Contains(key));
    }

    // Review Focus 4: a switch made elsewhere (palette command, Cancel) must reach an open Settings.
    [AvaloniaFact]
    public void SettingsWindow_FollowsAThemeSwitchMadeElsewhere()
    {
        var window = new Ntilde.SettingsWindow();
        window.Show();
        try
        {
            ThemeService.Apply(TestThemes.GitHubLight, 1.0);
            Dispatcher.UIThread.RunJobs();

            var expected = ThemeTokens.Derive(TestThemes.GitHubLight, 1.0);
            Assert.Equal(expected[ThemeKeys.WindowBg], ((ISolidColorBrush)window.Background!).Color);
            Assert.Equal(expected[ThemeKeys.Fg], ((ISolidColorBrush)window.Foreground!).Color);
        }
        finally
        {
            window.Close();
        }
    }
}
```

In `tests/Ntilde.App.Tests/Core/ThemeApplicationRegressionTests.cs`, replace the test `SettingsWindow_CardRows_UseTheWindowPaletteBrushes` (lines 104-131) with:
```csharp
    /// <summary>
    /// Both code-built card kinds must hold the app's token brush instances, which ThemeService
    /// recolors in place. Holding hardcoded hex (the regression) or a copy means light themes render
    /// dark navy cards.
    /// </summary>
    [AvaloniaFact]
    public void SettingsWindow_CardRows_UseTheAppTokenBrushes()
    {
        var window = new Ntilde.SettingsWindow();

        var panelBrush = Assert.IsType<SolidColorBrush>(window.FindResource(ThemeKeys.Panel));
        var hairlineBrush = Assert.IsType<SolidColorBrush>(window.FindResource(ThemeKeys.Hairline));
        Assert.Same(panelBrush, Avalonia.Application.Current!.FindResource(ThemeKeys.Panel));

        var titleBarRows = window.FindControl<StackPanel>("TitleBarItemsPanel")!.Children.OfType<Border>().ToList();
        Assert.NotEmpty(titleBarRows);
        foreach (var row in titleBarRows)
        {
            Assert.Same(panelBrush, row.Background);
            Assert.Same(hairlineBrush, row.BorderBrush);
        }

        var shortcutRows = window.FindControl<StackPanel>("ShortcutBindingsPanel")!.Children.OfType<Border>().ToList();
        Assert.NotEmpty(shortcutRows);
        foreach (var row in shortcutRows)
        {
            Assert.Same(panelBrush, row.Background);
            Assert.Same(hairlineBrush, row.BorderBrush);
        }
    }
```
and add `using Ntilde.Shell.Theming;` to that file's usings.

- [ ] **Step 2: Run to verify they fail**

Run: `scripts/build.ps1 test tests/Ntilde.App.Tests/Ntilde.App.Tests.csproj --filter "FullyQualifiedName~SettingsWindowThemeTests|FullyQualifiedName~SettingsWindow_CardRows_UseTheAppTokenBrushes" --blame-hang-timeout 5m`
Expected: `SettingsWindow_DeclaresNoLocalThemeTokens` FAILS (the window still declares `NtPanel` …). `SettingsWindow_CardRows_UseTheAppTokenBrushes` FAILS on `Assert.Same(panelBrush, Application…)`. `SettingsWindow_FollowsAThemeSwitchMadeElsewhere` FAILS (the window background is a locally set theme brush).

- [ ] **Step 3: XAML — window colors, drop the local block, rename keys, go dynamic**

In `src/Ntilde.App/SettingsWindow.axaml`:

1. Lines 8-9:
```xml
        Background="{DynamicResource NtWindowBg}"
        Foreground="{DynamicResource NtFg}"
```
2. In `<Window.Resources>`, delete the 18 `<SolidColorBrush x:Key="Nt…"/>` lines (13-30). Keep `<FontFamily x:Key="NtMono">…</FontFamily>`.
3. Danger button (lines 154-165) becomes:
```xml
        <Style Selector="Button.Danger">
            <Setter Property="Background" Value="{DynamicResource NtErrorDim}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource NtErrorBorder}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Foreground" Value="{DynamicResource NtError}"/>
            <Setter Property="CornerRadius" Value="5"/>
            <Setter Property="Padding" Value="14,6"/>
            <Setter Property="FontSize" Value="13"/>
        </Style>
        <Style Selector="Button.Danger:pointerover /template/ ContentPresenter">
            <Setter Property="Background" Value="{DynamicResource NtErrorBorder}"/>
        </Style>
```
4. Toggle knob (line 236): `Fill="White"` → `Fill="{DynamicResource NtAccentFg}"`.
5. Theme-editor swatch placeholders (lines 433-448, `EditSwatch0`…`EditSwatch15`): `Background="Black"` → `Background="Transparent"`. Code repaints them when the editor opens (`SettingsWindow.axaml.cs:3194`).
6. Rename and convert every token reference. Run this in the PowerShell tool from the worktree root:
```powershell
$f = 'src/Ntilde.App/SettingsWindow.axaml'
$x = Get-Content $f -Raw
$x = $x -replace '\{StaticResource NtBlueDim\}', '{DynamicResource NtAccentDim}' `
        -replace '\{StaticResource NtBlueFaint\}', '{DynamicResource NtAccentFaint}' `
        -replace '\{StaticResource NtBlueBorder\}', '{DynamicResource NtAccentBorder}' `
        -replace '\{StaticResource NtBlue\}', '{DynamicResource NtAccent}' `
        -replace '\{StaticResource NtGreen\}', '{DynamicResource NtSuccess}' `
        -replace '\{StaticResource NtYellow\}', '{DynamicResource NtWarning}' `
        -replace '\{StaticResource NtRed\}', '{DynamicResource NtError}' `
        -replace '\{StaticResource (Nt(?!Mono\b)[A-Za-z0-9]+)\}', '{DynamicResource $1}'
[IO.File]::WriteAllText((Resolve-Path $f), $x)
Select-String -Path $f -Pattern 'StaticResource Nt(?!Mono)|NtBlue|NtGreen|NtYellow|NtRed'
```
Expected: the final `Select-String` prints nothing.

- [ ] **Step 4: Code — keys, status brushes, `ApplyTheme`**

In `src/Ntilde.App/SettingsWindow.axaml.cs` add `using Ntilde.Shell.Theming;`, then:

1. Lines 1061-1062:
```csharp
                    ? (IBrush?)this.FindResource(ThemeKeys.Success)
                    : (IBrush?)this.FindResource(ThemeKeys.Error);
```
2. Line 1923:
```csharp
                    Fill = ThemeResourceBrush(isListening ? ThemeKeys.Success : ThemeKeys.Fg4),
```
3. Lines 2079-2080, 2288-2289 and 2886-2887 (three card builders):
```csharp
                Background = ThemeResourceBrush(ThemeKeys.Panel),
                BorderBrush = ThemeResourceBrush(ThemeKeys.Hairline),
```
4. Line 2298:
```csharp
                Foreground = ThemeResourceBrush(ThemeKeys.Error),
```
5. Rewrite the doc comment above `ThemeResourceBrush` (around line 2063) so it names the app tokens:
```csharp
        /// <summary>
        /// Resolves an app-wide theme token (see <see cref="ThemeKeys"/>). Rows built in code hold
        /// the brush INSTANCE, and ThemeService recolors that instance in place, so they follow live
        /// theme switches without being rebuilt.
        /// </summary>
```
6. Replace `ApplyTheme` and `UpdatePaletteResources` (lines 3229-3254) with:
```csharp
        /// <summary>
        /// Applies <paramref name="theme"/> (default: the active theme) app-wide. This window has no
        /// private palette any more; it follows ThemeService like every other surface, which is also
        /// what lets it track a switch made from the command palette while it is open.
        /// </summary>
        private void ApplyTheme(TerminalTheme? theme = null)
        {
            ThemeService.Apply(theme ?? _settings.ActiveTheme, _settings.WindowOpacity);
        }
```

- [ ] **Step 5: Run the tests**

Run: `scripts/build.ps1 test tests/Ntilde.App.Tests/Ntilde.App.Tests.csproj --filter "FullyQualifiedName~SettingsWindow|FullyQualifiedName~Ntilde.Tests.Theming" --blame-hang-timeout 5m`
Expected: PASS. This includes every existing `SettingsWindow*` test class (backup, fonts, profiles, shortcuts, title bar, UI scale).

- [ ] **Step 6: Commit**

```bash
git add src/Ntilde.App/SettingsWindow.axaml src/Ntilde.App/SettingsWindow.axaml.cs tests/Ntilde.App.Tests/Theming/SettingsWindowThemeTests.cs tests/Ntilde.App.Tests/Core/ThemeApplicationRegressionTests.cs
git commit -m "refactor(settings): follow app-wide theme tokens instead of a private palette" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Connection Manager on app tokens

**Files:**
- Modify: `src/Ntilde.App/Controls/ConnectionManager.axaml` (lines 12-13, 17-36, 99, 102, 121, 156, 172, 193, 207, 213, 218, every `{StaticResource Nt…}`), `src/Ntilde.App/Controls/ConnectionManager.axaml.cs` (lines 129-133, 686-689), `src/Ntilde.App/ConnectionManagerWindow.axaml:9-10`, `src/Ntilde.App/ConnectionManagerWindow.axaml.cs:29-30`, `src/Ntilde.App/MainWindow.axaml.cs` (~lines 395 and 6739)
- Test: `tests/Ntilde.App.Tests/Theming/ConnectionManagerThemeTests.cs`

**Interfaces:**
- Consumes: `ThemeKeys`, `ThemeService.Apply`.
- Produces: removes `ConnectionManager.ApplyTheme(TerminalTheme)` and `ConnectionManagerWindow.ApplyTheme(TerminalTheme)`. The compiler finds any remaining caller.

- [ ] **Step 1: Write the failing test**

`tests/Ntilde.App.Tests/Theming/ConnectionManagerThemeTests.cs`:
```csharp
using System.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Ntilde.Shell.Theming;
using Xunit;

namespace Ntilde.Tests.Theming;

public class ConnectionManagerThemeTests
{
    [AvaloniaFact]
    public void ConnectionManager_FollowsAThemeSwitchWithoutAnApplyThemeCall()
    {
        var window = new Ntilde.ConnectionManagerWindow();
        window.Show();
        try
        {
            ThemeService.Apply(TestThemes.GitHubLight, 1.0);
            Dispatcher.UIThread.RunJobs();

            var manager = window.Manager!;
            var expected = ThemeTokens.Derive(TestThemes.GitHubLight, 1.0);
            Assert.Equal(expected[ThemeKeys.WindowBg], ((ISolidColorBrush)manager.Background!).Color);
            Assert.Equal(expected[ThemeKeys.WindowBg], ((ISolidColorBrush)window.Background!).Color);
            Assert.DoesNotContain(manager.Resources.Keys.OfType<string>(), key => ThemeKeys.All.Contains(key));
        }
        finally
        {
            window.Close();
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `scripts/build.ps1 test tests/Ntilde.App.Tests/Ntilde.App.Tests.csproj --filter "FullyQualifiedName~ConnectionManagerThemeTests" --blame-hang-timeout 5m`
Expected: FAIL — the manager background is the hardcoded `#16181d` and the manager declares local `Nt*` keys.

- [ ] **Step 3: XAML**

`src/Ntilde.App/Controls/ConnectionManager.axaml`:
1. Lines 12-13 → `Background="{DynamicResource NtWindowBg}"` and `Foreground="{DynamicResource NtFg}"`.
2. Delete the 20 `<SolidColorBrush x:Key="Nt…"/>` lines (17-36). Keep `<FontFamily x:Key="NtMono">`.
3. Hover and pressed literals:
   - line 99 `Value="#2a2f38"` → `Value="{DynamicResource NtHoverFill}"`
   - line 102 `Value="#353b46"` → `Value="{DynamicResource NtPressedFill}"`
   - line 121 `Value="#2a2f38"` → `Value="{DynamicResource NtHoverFill}"`
   - lines 156, 172, 193 `Value="#1f2730"` → `Value="{DynamicResource NtHoverFill}"`
   - lines 207, 213 `Value="#002a2f38"` → `Value="Transparent"`
   - line 218 `Value="#1d2129"` → `Value="{DynamicResource NtHoverFill}"`
4. Run in the PowerShell tool:
```powershell
$f = 'src/Ntilde.App/Controls/ConnectionManager.axaml'
$x = Get-Content $f -Raw
$x = $x -replace '\{StaticResource NtBlueDim\}', '{DynamicResource NtAccentDim}' `
        -replace '\{StaticResource NtBlueFaint\}', '{DynamicResource NtAccentFaint}' `
        -replace '\{StaticResource NtBlueBorder\}', '{DynamicResource NtAccentBorder}' `
        -replace '\{StaticResource NtBlue\}', '{DynamicResource NtAccent}' `
        -replace '\{StaticResource NtGreen\}', '{DynamicResource NtSuccess}' `
        -replace '\{StaticResource NtYellow\}', '{DynamicResource NtWarning}' `
        -replace '\{StaticResource NtRed\}', '{DynamicResource NtError}' `
        -replace '\{StaticResource (Nt(?!Mono\b)[A-Za-z0-9]+)\}', '{DynamicResource $1}'
[IO.File]::WriteAllText((Resolve-Path $f), $x)
Select-String -Path $f -Pattern 'StaticResource Nt(?!Mono)|NtBlue|NtGreen|NtYellow|NtRed|NtMagenta|#[0-9A-Fa-f]{6}'
```
Expected: no output.

`src/Ntilde.App/ConnectionManagerWindow.axaml` lines 9-10 → `Background="{DynamicResource NtWindowBg}"` and `Foreground="{DynamicResource NtFg}"`.

- [ ] **Step 4: Code — remove the per-window apply**

- `src/Ntilde.App/Controls/ConnectionManager.axaml.cs`: delete the `ApplyTheme` method with its comment (lines 129-133) and the `UpdatePaletteResources` method (lines 686-689).
- `src/Ntilde.App/ConnectionManagerWindow.axaml.cs`: delete lines 29-30 (the `ApplyTheme` pass-through and its summary).
- `src/Ntilde.App/MainWindow.axaml.cs`: delete `connManager.ApplyTheme(_settings.ActiveTheme);` (~line 395, in `OpenConnectionManagerAsync`) and `_connectionManagerWindow?.ApplyTheme(theme);` (last line of `ApplyThemeToUI`, ~line 6739).
- Remove any `using Ntilde.VT;` that becomes unused in `ConnectionManagerWindow.axaml.cs` (`scripts/build.ps1 build src/Ntilde.App` warns IDE0005).

- [ ] **Step 5: Run the tests and build**

Run: `scripts/build.ps1 build src/Ntilde.App/Ntilde.App.csproj`
Expected: `Build succeeded.`, 0 errors (no remaining `ApplyTheme` caller).
Run: `scripts/build.ps1 test tests/Ntilde.App.Tests/Ntilde.App.Tests.csproj --filter "FullyQualifiedName~ConnectionManager" --blame-hang-timeout 5m`
Expected: PASS (new test plus existing Connection Manager tests).

- [ ] **Step 6: Commit**

```bash
git add src/Ntilde.App/Controls/ConnectionManager.axaml src/Ntilde.App/Controls/ConnectionManager.axaml.cs src/Ntilde.App/ConnectionManagerWindow.axaml src/Ntilde.App/ConnectionManagerWindow.axaml.cs src/Ntilde.App/MainWindow.axaml.cs tests/Ntilde.App.Tests/Theming/ConnectionManagerThemeTests.cs
git commit -m "refactor(connections): follow app-wide theme tokens; drop the per-window ApplyTheme" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Agent Output markdown on tokens; delete `ThemePaletteResources`

**Files:**
- Modify: `src/Ntilde.App/AgentOutput/MarkdownTheme.cs`, `src/Ntilde.App/AgentOutput/MarkdownRenderer.cs:40` (doc comment), `tests/Ntilde.App.Tests/Core/ThemeApplicationRegressionTests.cs`
- Delete: `src/Ntilde.App/Shell/ThemePaletteResources.cs`
- Test: `tests/Ntilde.App.Tests/Theming/MarkdownThemeTokenTests.cs`

**Interfaces:**
- Consumes: `ThemeKeys`, `ThemeService.Current`.
- Produces: `MarkdownTheme.Resolve(StyledElement)` keeps its signature. It now returns app token brushes, and falls back to `ThemeService.Current` colors when the anchor is not in a tree.

- [ ] **Step 1: Write the failing test**

`tests/Ntilde.App.Tests/Theming/MarkdownThemeTokenTests.cs`:
```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Ntilde.AgentOutput;
using Ntilde.Shell.Theming;
using Xunit;

namespace Ntilde.Tests.Theming;

public class MarkdownThemeTokenTests
{
    [AvaloniaFact]
    public void Markdown_resolves_the_app_tokens_it_used_to_miss()
    {
        var anchor = new Border();
        var window = new Window { Content = anchor };
        window.Show();
        try
        {
            var theme = MarkdownTheme.Resolve(anchor);

            Assert.Same(Application.Current!.FindResource(ThemeKeys.Link), theme.Accent);
            Assert.Same(Application.Current.FindResource(ThemeKeys.DiffAdded), theme.Added);
            Assert.Same(Application.Current.FindResource(ThemeKeys.PanelAlt), theme.CodeBackground);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_detached_anchor_falls_back_to_the_current_token_colors()
    {
        ThemeService.Apply(TestThemes.Nord, 1.0);
        var theme = MarkdownTheme.Resolve(new Border());

        Assert.Equal(ThemeService.Current[ThemeKeys.Link], ((ISolidColorBrush)theme.Accent).Color);
    }
}
```
A detached `Border` has no parent and no `StylingParent`. Avalonia's `TryFindResource` walks `StylingParent` links, and only a `TopLevel` links to the `Application`, so a detached lookup misses and takes the fallback.

- [ ] **Step 2: Run to verify it fails**

Run: `scripts/build.ps1 test tests/Ntilde.App.Tests/Ntilde.App.Tests.csproj --filter "FullyQualifiedName~MarkdownThemeTokenTests" --blame-hang-timeout 5m`
Expected: FAIL — `theme.Accent` is the fixed fallback brush (`MarkdownTheme` looks up `NtBlue`, which no longer exists).

- [ ] **Step 3: Rewrite `MarkdownTheme`**

Replace `src/Ntilde.App/AgentOutput/MarkdownTheme.cs` with:
```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Ntilde.Shell.Theming;

namespace Ntilde.AgentOutput;

/// <summary>Resolved brush set for one render pass.</summary>
/// <remarks>
/// Extracted from <see cref="MarkdownRenderer"/> so fence-body handlers in sibling files can take
/// it. Resolves the app-wide theme tokens. Before theme v2 it asked for Nt* keys that nothing in
/// its tree defined, so it always fell back to a fixed dark palette.
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
        // tree yet. Fall back to the current token color rather than a hardcoded palette. An
        // unexpected non-brush override also degrades here instead of crashing.
        return anchor.TryFindResource(key, out object? value) && value is IBrush brush
            ? brush
            : new ImmutableSolidColorBrush(ThemeService.Current[key]);
    }
}
```

In `src/Ntilde.App/AgentOutput/MarkdownRenderer.cs` line 40, change the doc text `<c>Nt*</c> theme brushes (see <c>ThemePaletteResources</c>) and falls back to the same fixed` so it reads `<c>Nt*</c> theme tokens (see <c>ThemeService</c>)`. Rewrite the rest of that sentence so it no longer claims a fixed fallback palette.

- [ ] **Step 4: Delete `ThemePaletteResources` and its moved tests**

- Delete `src/Ntilde.App/Shell/ThemePaletteResources.cs`.
- In `tests/Ntilde.App.Tests/Core/ThemeApplicationRegressionTests.cs`, delete these five tests: `SoftenAccent_PureBlue_LandsOnACalmerAccentWithHuePreserved`, `SoftenAccent_AlreadyCalmAccent_IsUnchanged`, `Apply_AccentBrushes_TrackTheSoftenedBlueNotTheRawAnsiBlue`, `Apply_AccentForeground_ContrastsTheAccent`, `Apply_AcrossThemeSwitches_KeepsBrushInstancesStable`. They now live in `ThemeColorMathTests`, `ThemeTokensTests` and `ThemeServiceTests`. Also delete the unused `PureBlue` property. Rewrite the class summary's first bullet to say the Settings cards hold the app-wide `NtPanel`/`NtHairline` token instances that `ThemeService` recolors in place. Rewrite its second bullet to say the accent softening now lives in `ThemeColorMath.SoftenAccent`.

- [ ] **Step 5: Build and run**

Run: `scripts/build.ps1 build Ntilde.sln`
Expected: `Build succeeded.`, 0 errors (no reference to `ThemePaletteResources` remains).
Run: `scripts/build.ps1 test tests/Ntilde.App.Tests/Ntilde.App.Tests.csproj --filter "FullyQualifiedName~MarkdownTheme|FullyQualifiedName~AgentOutput|FullyQualifiedName~ThemeApplicationRegressionTests|FullyQualifiedName~Ntilde.Tests.Theming" --blame-hang-timeout 5m`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A src/Ntilde.App/AgentOutput src/Ntilde.App/Shell/ThemePaletteResources.cs tests/Ntilde.App.Tests/Core/ThemeApplicationRegressionTests.cs tests/Ntilde.App.Tests/Theming/MarkdownThemeTokenTests.cs
git commit -m "fix(agent-output): markdown resolves the theme tokens it always missed; drop ThemePaletteResources" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Roslyn in Architecture.Tests, and the unknown-token guard

**Files:**
- Modify: `Directory.Packages.props` (Architecture testing group), `tests/Ntilde.Architecture.Tests/Ntilde.Architecture.Tests.csproj`
- Create: `tests/Ntilde.Architecture.Tests/ThemeLiterals/ThemeGuardFiles.cs`, `tests/Ntilde.Architecture.Tests/ThemeLiterals/ThemeLiteralPolicy.cs` (token-layer paths only in this task), `tests/Ntilde.Architecture.Tests/ThemeTokenKeyGuardTests.cs`

**Interfaces:**
- Consumes: `Ntilde.Shell.Theming.ThemeKeys.All` (public), `Ntilde.Shell.UiScale.TransformResourceKey` (public const).
- Produces:
  - `internal static class ThemeGuardFiles`: `string RepoRoot()`, `IReadOnlyList<string> ShippedXaml()`, `IReadOnlyList<string> AppSources()`, `string Absolute(string relative)`. Paths are repo-relative with `/`.
  - `internal static class ThemeLiteralPolicy`: `bool IsTokenLayer(string relativePath)`. Task 11 adds the allowlist.

- [ ] **Step 1: Add the package**

`Directory.Packages.props` — under `<!-- Architecture testing … -->` add:
```xml
    <!-- Test-only: the theme literal/token guards parse the App's C# with Roslyn's semantic model
         (resolved symbols, not spellings). Matches SDK 10.0.400; CentralPackageTransitivePinning
         also moves BenchmarkDotNet's transitive 4.14.0 up to this version. -->
    <PackageVersion Include="Microsoft.CodeAnalysis.CSharp" Version="5.3.0" />
```
`tests/Ntilde.Architecture.Tests/Ntilde.Architecture.Tests.csproj` — in the first `<ItemGroup>` add:
```xml
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" />
```

- [ ] **Step 2: Shared file enumeration and token-layer policy**

`tests/Ntilde.Architecture.Tests/ThemeLiterals/ThemeGuardFiles.cs`:
```csharp
namespace Ntilde.Architecture.Tests.ThemeLiterals;

/// <summary>Repo-relative ('/'-separated) file sets the theme guards scan.</summary>
internal static class ThemeGuardFiles
{
    private const string AppSourceRoot = "src/Ntilde.App/";

    // Same walk-up as CompiledBindingTests.RepoRoot(): the test binary's depth below the repo root
    // differs between a local run and CI.
    public static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Ntilde.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }

    public static string Absolute(string relative) => Path.Combine(RepoRoot(), relative);

    /// <summary>Every .axaml under src/, excluding generated copies in obj/ and bin/.</summary>
    public static IReadOnlyList<string> ShippedXaml() => Enumerate("src", "*.axaml");

    /// <summary>Every .cs of the App project (assembly "Ntilde").</summary>
    public static IReadOnlyList<string> AppSources() => Enumerate(AppSourceRoot.TrimEnd('/'), "*.cs");

    private static List<string> Enumerate(string relativeDirectory, string pattern)
    {
        var root = RepoRoot();
        return Directory.EnumerateFiles(Path.Combine(root, relativeDirectory), pattern, SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(relative => !relative.Contains("/obj/", StringComparison.Ordinal) &&
                               !relative.Contains("/bin/", StringComparison.Ordinal))
            .OrderBy(relative => relative, StringComparer.Ordinal)
            .ToList();
    }
}
```

`tests/Ntilde.Architecture.Tests/ThemeLiterals/ThemeLiteralPolicy.cs`:
```csharp
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
```

- [ ] **Step 3: Write the guard (tests first — the synthetic ones drive the parser)**

`tests/Ntilde.Architecture.Tests/ThemeTokenKeyGuardTests.cs`:
```csharp
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Ntilde.Architecture.Tests.ThemeLiterals;
using Ntilde.Shell;
using Ntilde.Shell.Theming;

namespace Ntilde.Architecture.Tests;

/// <summary>
/// A DynamicResource to a key nobody defines renders nothing: no exception, just an invisible
/// border or text. These tests make every Nt* key reference resolvable by construction.
/// </summary>
/// <remarks>
/// Reads the XAML as XML and the markup-extension grammar, not raw text (memory:
/// guards-parse-dont-match-spellings). Single quotes, spacing, and the ResourceKey= and element
/// forms all reach the same parser.
/// </remarks>
public partial class ThemeTokenKeyGuardTests
{
    private const string Remedy =
        "Theme colors come only from ThemeKeys (src/Ntilde.App/Shell/Theming/ThemeKeys.cs), referenced as " +
        "{DynamicResource NtX} in XAML and ThemeKeys.X in C#. See docs/ThemeSystem.md, \"UI color tokens\". Offenders: ";

    private static readonly HashSet<string> AppKeys = new(ThemeKeys.All, StringComparer.Ordinal) { UiScale.TransformResourceKey };

    [Fact]
    public void Every_Nt_resource_reference_in_xaml_names_a_theme_token_or_a_local_resource()
    {
        var offenders = new List<string>();
        foreach (var file in ThemeGuardFiles.ShippedXaml())
        {
            var document = XDocument.Parse(File.ReadAllText(ThemeGuardFiles.Absolute(file)), LoadOptions.SetLineInfo);
            var local = LocalKeys(document);
            foreach (var (key, line) in ReferencedTokenKeys(document))
            {
                if (!AppKeys.Contains(key) && !local.Contains(key))
                {
                    offenders.Add($"{file}:{line} {key}");
                }
            }
        }

        Assert.True(offenders.Count == 0, Remedy + string.Join(", ", offenders));
    }

    [Fact]
    public void No_xaml_file_shadows_a_theme_token()
    {
        var offenders = new List<string>();
        foreach (var file in ThemeGuardFiles.ShippedXaml())
        {
            var document = XDocument.Parse(File.ReadAllText(ThemeGuardFiles.Absolute(file)));
            offenders.AddRange(LocalKeys(document).Where(ThemeKeys.All.Contains).Select(key => $"{file} redeclares {key}"));
        }

        Assert.True(offenders.Count == 0,
            "A local x:Key with a token's name silently shadows the app-wide token for that subtree. " + Remedy + string.Join(", ", offenders));
    }

    [Fact]
    public void No_Nt_key_is_spelled_as_a_string_outside_the_theming_layer()
    {
        var offenders = new List<string>();
        foreach (var file in ThemeGuardFiles.AppSources().Where(f => !ThemeLiteralPolicy.IsTokenLayer(f)))
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(ThemeGuardFiles.Absolute(file))).GetRoot();
            foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
            {
                if (!literal.IsKind(SyntaxKind.StringLiteralExpression)) continue;

                var text = literal.Token.ValueText;
                bool isUiScaleDefinition = string.Equals(text, UiScale.TransformResourceKey, StringComparison.Ordinal) &&
                                           file.EndsWith("/Shell/UiScale.cs", StringComparison.Ordinal);
                if (TokenKeyPattern().IsMatch(text) && !isUiScaleDefinition)
                {
                    int line = literal.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    offenders.Add($"{file}:{line} \"{text}\"");
                }
            }
        }

        Assert.True(offenders.Count == 0, Remedy + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("<Border Background=\"{DynamicResource NtNope}\"/>")]
    [InlineData("<Border Background='{StaticResource NtNope}'/>")]
    [InlineData("<Border Background=\"{ DynamicResource   NtNope }\"/>")]
    [InlineData("<Border Background=\"{DynamicResource ResourceKey=NtNope}\"/>")]
    [InlineData("<Border><Border.Background><DynamicResource ResourceKey=\"NtNope\"/></Border.Background></Border>")]
    [InlineData("<Style Selector=\"Border\"><Setter Property=\"Background\" Value=\"{DynamicResource NtNope}\"/></Style>")]
    public void Every_reference_form_is_seen(string fragment)
        => Assert.Contains(ReferencedTokenKeys(Wrap(fragment)), reference => reference.Key == "NtNope");

    [Fact]
    public void Non_token_resources_are_ignored()
        => Assert.Empty(ReferencedTokenKeys(Wrap("<Button Theme=\"{StaticResource NtildeDrawnCaptionButton}\" Background=\"{DynamicResource SystemRegionBrush}\"/>")));

    internal static IEnumerable<(string Key, int Line)> ReferencedTokenKeys(XDocument document)
    {
        foreach (var element in document.Descendants())
        {
            bool isResourceElement = element.Name.LocalName is "DynamicResource" or "StaticResource";
            foreach (var attribute in element.Attributes())
            {
                int line = ((IXmlLineInfo)attribute).HasLineInfo() ? ((IXmlLineInfo)attribute).LineNumber : 0;
                if (isResourceElement && attribute.Name.LocalName == "ResourceKey" && TokenKeyPattern().IsMatch(attribute.Value.Trim()))
                {
                    yield return (attribute.Value.Trim(), line);
                }

                foreach (Match match in MarkupReference().Matches(attribute.Value))
                {
                    var key = match.Groups[1].Value;
                    if (TokenKeyPattern().IsMatch(key))
                    {
                        yield return (key, line);
                    }
                }
            }
        }
    }

    private static HashSet<string> LocalKeys(XDocument document)
        => document.Descendants().Attributes()
            .Where(attribute => attribute.Name.LocalName == "Key")
            .Select(attribute => attribute.Value.Trim())
            .ToHashSet(StringComparer.Ordinal);

    private static XDocument Wrap(string fragment)
        => XDocument.Parse(
            "<Root xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" + fragment + "</Root>",
            LoadOptions.SetLineInfo);

    [GeneratedRegex(@"\{\s*(?:DynamicResource|StaticResource)\s+(?:ResourceKey\s*=\s*)?([A-Za-z0-9_.]+)")]
    private static partial Regex MarkupReference();

    [GeneratedRegex(@"^Nt[A-Z][A-Za-z0-9]*$")]
    private static partial Regex TokenKeyPattern();
}
```

- [ ] **Step 4: Run it**

Run: `scripts/build.ps1 test tests/Ntilde.Architecture.Tests/Ntilde.Architecture.Tests.csproj --filter "FullyQualifiedName~ThemeTokenKeyGuardTests"`
Expected: PASS, 10 tests. Tasks 6–8 already moved every reference onto `ThemeKeys` and `NtMono` is declared locally. If `Every_Nt_resource_reference…` fails, the listed key is a Task 6/7 rename miss: fix the XAML, never add the key to `AppKeys`.

To prove the guard bites, temporarily change one `NtAccent` in `SettingsWindow.axaml` to `NtAcent`, re-run, and confirm the failure names `src/Ntilde.App/SettingsWindow.axaml:<line> NtAcent`. Then revert.

- [ ] **Step 5: Confirm the Roslyn pin did not break Benchmarks**

Run: `scripts/build.ps1 build tests/Ntilde.Benchmarks/Ntilde.Benchmarks.csproj`
Expected: `Build succeeded.`

- [ ] **Step 6: Commit**

```bash
git add Directory.Packages.props tests/Ntilde.Architecture.Tests/Ntilde.Architecture.Tests.csproj tests/Ntilde.Architecture.Tests/ThemeLiterals/ThemeGuardFiles.cs tests/Ntilde.Architecture.Tests/ThemeLiterals/ThemeLiteralPolicy.cs tests/Ntilde.Architecture.Tests/ThemeTokenKeyGuardTests.cs
git commit -m "test(arch): guard every Nt* resource reference against unknown or shadowed tokens" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: Color-literal scanners (XAML and C#)

**Files:**
- Create: `tests/Ntilde.Architecture.Tests/ThemeLiterals/ColorLiteralFinding.cs`, `.../XamlColorLiteralScanner.cs`, `.../CSharpColorLiteralScanner.cs`
- Test: `tests/Ntilde.Architecture.Tests/ColorLiteralScannerTests.cs`

**Interfaces:**
- Consumes: `Avalonia.Media.Color.TryParse`, Roslyn.
- Produces:
  - `internal sealed record ColorLiteralFinding(string File, int Line, string Member, string Text)`. `Member` is the enclosing C# member name ("" for XAML). `Text` is `"<attribute>=<token>"` for XAML or `"<rule>: <expression>"` for C#.
  - `internal static class XamlColorLiteralScanner`: `IReadOnlyList<ColorLiteralFinding> Scan(string relativePath, string xaml)`, `bool IsColorLiteral(string)`.
  - `internal static class CSharpColorLiteralScanner`: `IReadOnlyList<ColorLiteralFinding> Scan(IReadOnlyList<(string Path, string Source)> sources, Func<string, bool> shouldReport)`. It throws `InvalidOperationException` on any syntax error (fail closed).

- [ ] **Step 1: Write the failing synthetic tests**

`tests/Ntilde.Architecture.Tests/ColorLiteralScannerTests.cs`:
```csharp
using Ntilde.Architecture.Tests.ThemeLiterals;

namespace Ntilde.Architecture.Tests;

/// <summary>
/// Every spelling of a hardcoded color the scanners must catch, and the look-alikes they must not.
/// These are the scanners' own contract; ThemeLiteralGuardTests applies them to the repo.
/// </summary>
public class ColorLiteralScannerTests
{
    private const string XamlOpen = "<Root xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">";

    [Theory]
    [InlineData("<Border Background=\"#252526\"/>", 1)]
    [InlineData("<Border Background='#252526'/>", 1)]
    [InlineData("<TextBlock Foreground=\"White\"/>", 1)]
    [InlineData("<Border Background=\"rgb(1,2,3)\"/>", 1)]
    [InlineData("<Style><Setter Property=\"Background\" Value=\"#30FFFFFF\"/></Style>", 1)]
    [InlineData("<SolidColorBrush x:Key=\"TextControlBackground\">#1a1a1a</SolidColorBrush>", 1)]
    [InlineData("<GradientStop Color=\"#FF0000\"/>", 1)]
    [InlineData("<Border Background=\"#002a2f38\"/>", 1)]
    [InlineData("<TextBlock Foreground=\"{Binding Tint, FallbackValue=#FF0000}\"/>", 1)]
    [InlineData("<Border Background=\"Transparent\"/>", 0)]
    [InlineData("<Border Background=\"{DynamicResource NtPanel}\"/>", 0)]
    [InlineData("<Border ToolTip.Tip=\"Black\"/>", 0)]
    [InlineData("<TextBlock Text=\"Red\"/>", 0)]
    [InlineData("<Style Selector=\"Button#SettingsBtn:pointerover\"/>", 0)]
    [InlineData("<Border Width=\"1080\" Opacity=\"0.6\" Margin=\"0,50,20,0\"/>", 0)]
    public void Xaml_rules(string fragment, int expected)
        => Assert.Equal(expected, XamlColorLiteralScanner.Scan("src/Probe.axaml", XamlOpen + fragment + "</Root>").Count);

    [Theory]
    [InlineData("var b = Avalonia.Media.Brushes.White;", 1)]
    [InlineData("var c = Avalonia.Media.Colors.Red;", 1)]
    [InlineData("var c = Avalonia.Media.Color.Parse(\"#112233\");", 1)]
    [InlineData("var c = Avalonia.Media.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF);", 1)]
    [InlineData("Avalonia.Media.Color c = new(255, 1, 2, 3);", 1)]
    [InlineData("var s = new SkiaSharp.SKColor(51, 153, 255, 100);", 1)]
    [InlineData("SkiaSharp.SKColor s = 0xFF336699;", 1)]
    [InlineData("var s = (SkiaSharp.SKColor)0xFF336699;", 1)]
    [InlineData("var t = Ntilde.VT.TermColor.FromRgb(1, 2, 3);", 1)]
    [InlineData("var t = Ntilde.VT.TermColor.Black;", 1)]
    [InlineData("string[] palette = { \"#e06c75\", \"#98c379\" };", 2)]
    [InlineData("var b = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.Red);", 1)]
    [InlineData("var b = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Avalonia.Media.Color.FromRgb(0xF3, 0xF6, 0xFA));", 1)]
    [InlineData("var b = Avalonia.Media.Brushes.Transparent;", 0)]
    [InlineData("var s = SkiaSharp.SKColors.Empty;", 0)]
    [InlineData("byte r = 1; var c = Avalonia.Media.Color.FromRgb(r, 2, 3);", 0)]
    [InlineData("var k = \"#region\";", 0)]
    [InlineData("var n = 0xFF336699;", 0)]
    public void Csharp_rules(string statement, int expected)
    {
        var source = "class Probe { void M() { " + statement + " } }";
        Assert.Equal(expected, CSharpColorLiteralScanner.Scan([("src/Ntilde.App/Probe.cs", source)], _ => true).Count);
    }

    [Fact]
    public void Using_static_does_not_hide_a_named_color()
    {
        const string source = "using static Avalonia.Media.Colors; class Probe { object M() => White; }";
        Assert.Single(CSharpColorLiteralScanner.Scan([("src/Ntilde.App/Probe.cs", source)], _ => true));
    }

    [Fact]
    public void Findings_name_their_enclosing_member()
    {
        const string source = "class Probe { private static readonly string[] GroupSwatches = { \"#e06c75\" }; }";
        var finding = Assert.Single(CSharpColorLiteralScanner.Scan([("src/Ntilde.App/Probe.cs", source)], _ => true));
        Assert.Equal("GroupSwatches", finding.Member);
        Assert.Equal(1, finding.Line);
    }

    [Fact]
    public void Unparseable_source_fails_closed()
        => Assert.Throws<InvalidOperationException>(() =>
            CSharpColorLiteralScanner.Scan([("src/Ntilde.App/Broken.cs", "class {")], _ => true));

    [Fact]
    public void Unreported_files_still_bind_but_yield_nothing()
    {
        const string source = "class Probe { object M() => Avalonia.Media.Brushes.White; }";
        Assert.Empty(CSharpColorLiteralScanner.Scan([("src/Ntilde.App/Shell/Theming/Probe.cs", source)], _ => false));
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `scripts/build.ps1 test tests/Ntilde.Architecture.Tests/Ntilde.Architecture.Tests.csproj --filter "FullyQualifiedName~ColorLiteralScannerTests"`
Expected: build FAILS — the scanner types do not exist.

- [ ] **Step 3: Finding record and XAML scanner**

`tests/Ntilde.Architecture.Tests/ThemeLiterals/ColorLiteralFinding.cs`:
```csharp
namespace Ntilde.Architecture.Tests.ThemeLiterals;

/// <summary>One hardcoded color. <see cref="Member"/> is the enclosing C# member ("" for XAML).</summary>
internal sealed record ColorLiteralFinding(string File, int Line, string Member, string Text)
{
    public override string ToString() => $"{File}:{Line} {Text}";
}
```

`tests/Ntilde.Architecture.Tests/ThemeLiterals/XamlColorLiteralScanner.cs`:
```csharp
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Avalonia.Media;

namespace Ntilde.Architecture.Tests.ThemeLiterals;

/// <summary>
/// Finds hardcoded colors in XAML by parsing it: every attribute value, markup-extension argument
/// and leaf element text that Avalonia's own Color.TryParse accepts (hex, named, rgb()/hsl()).
/// </summary>
/// <remarks>
/// Fail-closed. The only exemptions are attributes that cannot hold a color. A missing exemption
/// costs a false failure; an over-broad one would cost a missed literal. "Transparent" is exempt
/// because it is not a color choice.
/// </remarks>
internal static partial class XamlColorLiteralScanner
{
    private static readonly HashSet<string> NonColorAttributes = new(StringComparer.Ordinal)
    {
        "Text", "Content", "Header", "Title", "ToolTip.Tip", "PlaceholderText", "Watermark",
        "Name", "Key", "Class", "Classes", "Uid", "Selector", "Property", "FontFamily", "Data",
        "Tag", "AutomationProperties.Name", "AutomationProperties.HelpText",
    };

    public static IReadOnlyList<ColorLiteralFinding> Scan(string relativePath, string xaml)
    {
        var findings = new List<ColorLiteralFinding>();
        var document = XDocument.Parse(xaml, LoadOptions.SetLineInfo);

        foreach (var element in document.Descendants())
        {
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration || NonColorAttributes.Contains(attribute.Name.LocalName))
                {
                    continue;
                }

                foreach (var token in ColorTokens(attribute.Value))
                {
                    findings.Add(new ColorLiteralFinding(relativePath, LineOf(attribute), "", $"{element.Name.LocalName}.{attribute.Name.LocalName}={token}"));
                }
            }

            if (!element.HasElements)
            {
                foreach (var token in ColorTokens(element.Value))
                {
                    findings.Add(new ColorLiteralFinding(relativePath, LineOf(element), "", $"{element.Name.LocalName}={token}"));
                }
            }
        }

        return findings;
    }

    public static bool IsColorLiteral(string candidate)
        => candidate.Length > 0 &&
           !string.Equals(candidate, "Transparent", StringComparison.OrdinalIgnoreCase) &&
           Color.TryParse(candidate, out _);

    private static IEnumerable<string> ColorTokens(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            yield break;
        }

        if (trimmed.StartsWith('{'))
        {
            // A markup extension: any argument can carry a literal (FallbackValue=#FF0000).
            foreach (var token in MarkupSeparators().Split(trimmed))
            {
                if (IsColorLiteral(token))
                {
                    yield return token;
                }
            }

            yield break;
        }

        if (IsColorLiteral(trimmed))
        {
            yield return trimmed;
        }
    }

    private static int LineOf(XObject node)
        => node is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;

    [GeneratedRegex(@"[\s,={}]+")]
    private static partial Regex MarkupSeparators();
}
```

- [ ] **Step 4: C# scanner**

`tests/Ntilde.Architecture.Tests/ThemeLiterals/CSharpColorLiteralScanner.cs`:
```csharp
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Ntilde.Architecture.Tests.ThemeLiterals;

/// <summary>
/// Finds hardcoded colors in the App's C# by RESOLVED SYMBOL, not spelling, using Roslyn's
/// semantic model over the App's own sources. It catches target-typed new(...), using static,
/// aliases and implicit conversions. The rules:
/// <list type="number">
/// <item>any static member of Colors / Brushes / SKColors, and static color-typed members of the
/// color types (TermColor.Black), except Transparent/Empty;</item>
/// <item>constructing a color type (Color, SKColor, TermColor, SolidColorBrush,
/// ImmutableSolidColorBrush, HslColor, HsvColor, SKColorF) from all-constant arguments;</item>
/// <item>a static factory on a color type returning a color type, called with all-constant
/// arguments (Parse, FromRgb, FromArgb, …);</item>
/// <item>a constant cast or implicitly converted to a color type (SKColor s = 0xFF336699);</item>
/// <item>a "#…" string literal Avalonia parses as a color (palettes kept as strings).</item>
/// </list>
/// Computed colors (non-constant arguments) are not findings: derivation is the token layer's job,
/// and constant spelling is what a token replaces.
/// </summary>
internal static class CSharpColorLiteralScanner
{
    private static readonly string[] ColorTypeNames =
    [
        "Avalonia.Media.Color", "Avalonia.Media.HslColor", "Avalonia.Media.HsvColor",
        "Avalonia.Media.SolidColorBrush", "Avalonia.Media.Immutable.ImmutableSolidColorBrush",
        "SkiaSharp.SKColor", "SkiaSharp.SKColorF", "Ntilde.VT.TermColor",
    ];

    private static readonly string[] PaletteTypeNames = ["Avalonia.Media.Colors", "Avalonia.Media.Brushes", "SkiaSharp.SKColors"];

    // Types the scan is blind without; resolving none of them would silently pass everything.
    private static readonly string[] RequiredTypeNames =
        ["Avalonia.Media.Color", "Avalonia.Media.Colors", "Avalonia.Media.Brushes", "SkiaSharp.SKColor", "Ntilde.VT.TermColor"];

    private static readonly HashSet<string> NoColorMembers = new(StringComparer.Ordinal) { "Transparent", "Empty" };

    // The App builds with ImplicitUsings (Directory.Build.props); mirror Microsoft.NET.Sdk's set.
    private const string ImplicitUsings =
        "global using global::System; global using global::System.Collections.Generic; global using global::System.IO; " +
        "global using global::System.Linq; global using global::System.Net.Http; global using global::System.Threading; " +
        "global using global::System.Threading.Tasks;";

    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(LoadReferences);

    public static IReadOnlyList<ColorLiteralFinding> Scan(IReadOnlyList<(string Path, string Source)> sources, Func<string, bool> shouldReport)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var trees = sources.Select(s => CSharpSyntaxTree.ParseText(s.Source, parseOptions, s.Path)).ToList();

        var syntaxErrors = trees.SelectMany(t => t.GetDiagnostics())
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
        if (syntaxErrors.Count > 0)
        {
            throw new InvalidOperationException(
                "The color-literal guard cannot vouch for code it cannot parse (fail closed). Upgrade " +
                "Microsoft.CodeAnalysis.CSharp in Directory.Packages.props if the syntax is new: " +
                string.Join(Environment.NewLine, syntaxErrors));
        }

        trees.Add(CSharpSyntaxTree.ParseText(ImplicitUsings, parseOptions, "ImplicitUsings.g.cs"));
        var compilation = CSharpCompilation.Create(
            "Ntilde",
            trees,
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));

        var missing = RequiredTypeNames.Where(name => compilation.GetTypeByMetadataName(name) is null).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException("The color-literal guard is blind: unresolved " + string.Join(", ", missing));
        }

        var colorTypes = Resolve(compilation, ColorTypeNames);
        var paletteTypes = Resolve(compilation, PaletteTypeNames);
        var memberNames = paletteTypes.Concat(colorTypes)
            .SelectMany(t => t.GetMembers())
            .Where(m => m.IsStatic)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        var findings = new List<ColorLiteralFinding>();
        foreach (var tree in trees)
        {
            if (shouldReport(tree.FilePath))
            {
                findings.AddRange(ScanTree(tree, compilation.GetSemanticModel(tree), colorTypes, paletteTypes, memberNames));
            }
        }

        return findings;
    }

    private static List<ColorLiteralFinding> ScanTree(
        SyntaxTree tree,
        SemanticModel model,
        HashSet<INamedTypeSymbol> colorTypes,
        HashSet<INamedTypeSymbol> paletteTypes,
        HashSet<string> memberNames)
    {
        var findings = new List<ColorLiteralFinding>();
        var flagged = new List<TextSpan>();
        var root = tree.GetRoot();

        bool IsColorType(ITypeSymbol? type) => type is INamedTypeSymbol named && colorTypes.Contains(named.OriginalDefinition);

        void Flag(SyntaxNode node, string rule)
        {
            flagged.Add(node.Span);
            int line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            findings.Add(new ColorLiteralFinding(tree.FilePath, line, EnclosingMember(node), $"{rule}: {Normalize(node)}"));
        }

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case BaseObjectCreationExpressionSyntax creation
                    when model.GetOperation(creation) is IObjectCreationOperation op &&
                         IsColorType(op.Type) && op.Arguments.Length > 0 &&
                         op.Arguments.All(a => a.Value.ConstantValue.HasValue):
                    Flag(creation, "constant construction");
                    break;

                case InvocationExpressionSyntax invocation
                    when model.GetOperation(invocation) is IInvocationOperation op &&
                         op.TargetMethod.IsStatic && IsColorType(op.TargetMethod.ContainingType) &&
                         IsColorType(op.TargetMethod.ReturnType) && op.Arguments.Length > 0 &&
                         op.Arguments.All(a => a.Value.ConstantValue.HasValue):
                    Flag(invocation, "constant factory");
                    break;

                case IdentifierNameSyntax identifier
                    when memberNames.Contains(identifier.Identifier.ValueText) &&
                         !NoColorMembers.Contains(identifier.Identifier.ValueText) &&
                         model.GetSymbolInfo(identifier).Symbol is { IsStatic: true } symbol &&
                         symbol is IPropertySymbol or IFieldSymbol &&
                         (paletteTypes.Contains(symbol.ContainingType.OriginalDefinition) ||
                          (IsColorType(symbol.ContainingType) && IsColorType(MemberType(symbol)))):
                    Flag(identifier.Parent is MemberAccessExpressionSyntax access && access.Name == identifier ? access : identifier, "named color");
                    break;

                case CastExpressionSyntax cast
                    when IsColorType(model.GetTypeInfo(cast).Type) && model.GetConstantValue(cast.Expression).HasValue:
                    Flag(cast, "constant conversion");
                    break;
            }
        }

        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (flagged.Any(span => span.Contains(literal.Span)))
            {
                continue;
            }

            if (literal.IsKind(SyntaxKind.StringLiteralExpression) &&
                literal.Token.ValueText.StartsWith('#') &&
                XamlColorLiteralScanner.IsColorLiteral(literal.Token.ValueText))
            {
                Flag(literal, "color string");
            }
            else if (literal.IsKind(SyntaxKind.NumericLiteralExpression))
            {
                var info = model.GetTypeInfo(literal);
                if (IsColorType(info.ConvertedType) && !IsColorType(info.Type))
                {
                    Flag(literal, "constant conversion");
                }
            }
        }

        return findings;
    }

    private static ITypeSymbol? MemberType(ISymbol symbol) => symbol switch
    {
        IPropertySymbol property => property.Type,
        IFieldSymbol field => field.Type,
        _ => null,
    };

    private static string EnclosingMember(SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case MethodDeclarationSyntax method: return method.Identifier.ValueText;
                case ConstructorDeclarationSyntax constructor: return constructor.Identifier.ValueText;
                case PropertyDeclarationSyntax property: return property.Identifier.ValueText;
                case FieldDeclarationSyntax field: return field.Declaration.Variables[0].Identifier.ValueText;
                case EventDeclarationSyntax @event: return @event.Identifier.ValueText;
                case IndexerDeclarationSyntax: return "this[]";
            }
        }

        return "";
    }

    private static string Normalize(SyntaxNode node) => string.Join(' ', node.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static HashSet<INamedTypeSymbol> Resolve(Compilation compilation, IEnumerable<string> names)
    {
        var set = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var name in names)
        {
            if (compilation.GetTypeByMetadataName(name) is { } type)
            {
                set.Add(type);
            }
        }

        return set;
    }

    private static IReadOnlyList<MetadataReference> LoadReferences()
    {
        var trusted = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var local = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll");

        // "Ntilde" is the App assembly: the compilation IS that assembly, built from source.
        return trusted.Concat(local)
            .Where(path => !string.Equals(Path.GetFileNameWithoutExtension(path), "Ntilde", StringComparison.OrdinalIgnoreCase))
            .GroupBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Where(IsManagedAssembly)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
    }

    private static bool IsManagedAssembly(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            return reader.HasMetadata && reader.GetMetadataReader().IsAssembly;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `scripts/build.ps1 test tests/Ntilde.Architecture.Tests/Ntilde.Architecture.Tests.csproj --filter "FullyQualifiedName~ColorLiteralScannerTests"`
Expected: PASS, 37 tests (15 XAML + 18 C# cases + 4 facts), and the build has 0 warnings (the project has `TreatWarningsAsErrors`).

If a C# case fails, print the scan's findings in the assertion message to see which rule over- or under-fires. Fix the rule, not the case. The cases are the contract.

- [ ] **Step 6: Commit**

```bash
git add tests/Ntilde.Architecture.Tests/ThemeLiterals/ColorLiteralFinding.cs tests/Ntilde.Architecture.Tests/ThemeLiterals/XamlColorLiteralScanner.cs tests/Ntilde.Architecture.Tests/ThemeLiterals/CSharpColorLiteralScanner.cs tests/Ntilde.Architecture.Tests/ColorLiteralScannerTests.cs
git commit -m "test(arch): XAML and Roslyn scanners for hardcoded colors" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 11: The ratchet — baseline, permanent allowlist, gating guard

**Files:**
- Modify: `tests/Ntilde.Architecture.Tests/ThemeLiterals/ThemeLiteralPolicy.cs` (add the allowlist)
- Create: `tests/Ntilde.Architecture.Tests/ThemeLiterals/ThemeLiteralScan.cs`, `tests/Ntilde.Architecture.Tests/ThemeLiteralGuardTests.cs`, `tests/Ntilde.Architecture.Tests/ThemeLiteralBaseline.txt`

**Interfaces:**
- Consumes: both scanners (Task 10), `ThemeGuardFiles`, `ThemeLiteralPolicy.IsTokenLayer`.
- Produces:
  - `internal sealed record PermanentAllowance(string File, string? Member, string? Text, string Reason)` with `bool Matches(ColorLiteralFinding)`.
  - `ThemeLiteralPolicy.Permanent` (list) and `ThemeLiteralPolicy.IsPermanentlyAllowed(ColorLiteralFinding)`.
  - `ThemeLiteralScan.ScanRepository()`, `ThemeLiteralScan.ParseBaseline(IEnumerable<string>)`, `ThemeLiteralScan.FormatBaseline(IReadOnlyDictionary<string,int>)`.

- [ ] **Step 1: Add the permanent allowlist**

Replace `tests/Ntilde.Architecture.Tests/ThemeLiterals/ThemeLiteralPolicy.cs` with:
```csharp
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
```

- [ ] **Step 2: Whole-repo scan and baseline format**

`tests/Ntilde.Architecture.Tests/ThemeLiterals/ThemeLiteralScan.cs`:
```csharp
using System.Globalization;
using System.Text;

namespace Ntilde.Architecture.Tests.ThemeLiterals;

internal static class ThemeLiteralScan
{
    public const string BaselinePath = "tests/Ntilde.Architecture.Tests/ThemeLiteralBaseline.txt";

    private const string BaselineHeader =
        "# Hardcoded colors outside the theme token layer, per file (see ThemeLiteralGuardTests).\n" +
        "# This file only ever shrinks: migrate a surface to ThemeKeys tokens, then lower its count.\n" +
        "# Regenerate from the guard's failure message; never raise a count by hand.\n";

    // Two guard tests read the scan; a semantic compile of the whole App runs once per test process.
    private static readonly Lazy<IReadOnlyList<ColorLiteralFinding>> Cached = new(Scan);

    /// <summary>Every finding in shipped XAML and App C#, excluding the token layer.</summary>
    public static IReadOnlyList<ColorLiteralFinding> ScanRepository() => Cached.Value;

    private static IReadOnlyList<ColorLiteralFinding> Scan()
    {
        var findings = new List<ColorLiteralFinding>();
        foreach (var file in ThemeGuardFiles.ShippedXaml())
        {
            findings.AddRange(XamlColorLiteralScanner.Scan(file, File.ReadAllText(ThemeGuardFiles.Absolute(file))));
        }

        // The whole App compiles together so symbols resolve; only non-token-layer files report.
        var sources = ThemeGuardFiles.AppSources()
            .Select(file => (Path: file, Source: File.ReadAllText(ThemeGuardFiles.Absolute(file))))
            .ToList();
        var appFiles = sources.Select(s => s.Path).ToHashSet(StringComparer.Ordinal);
        findings.AddRange(CSharpColorLiteralScanner.Scan(sources, path => appFiles.Contains(path) && !ThemeLiteralPolicy.IsTokenLayer(path)));
        return findings;
    }

    public static Dictionary<string, int> ParseBaseline(IEnumerable<string> lines)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
            counts[parts[1]] = int.Parse(parts[0], CultureInfo.InvariantCulture);
        }

        return counts;
    }

    public static string FormatBaseline(IReadOnlyDictionary<string, int> counts)
    {
        var text = new StringBuilder(BaselineHeader);
        foreach (var (file, count) in counts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            text.Append(count.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(file).Append('\n');
        }

        return text.ToString();
    }
}
```

- [ ] **Step 3: The gating guard**

`tests/Ntilde.Architecture.Tests/ThemeLiteralGuardTests.cs`:
```csharp
using Ntilde.Architecture.Tests.ThemeLiterals;

namespace Ntilde.Architecture.Tests;

/// <summary>
/// Gating ratchet. Hardcoded colors outside the token layer may only disappear. Other sessions
/// commit to main while the theme migration runs, so this blocks new literals from day one, not
/// only after the last surface is migrated.
/// </summary>
public class ThemeLiteralGuardTests
{
    private const string Remedy =
        "Use a theme token instead of a color literal: {DynamicResource NtX} in XAML, ThemeKeys.X in C# " +
        "(src/Ntilde.App/Shell/Theming/ThemeKeys.cs; docs/ThemeSystem.md, \"UI color tokens\"). " +
        "A color fixed on purpose (platform convention, user-chosen identity, debug-only) goes in " +
        "ThemeLiteralPolicy.Permanent with a reason.";

    [Fact]
    public void Color_literals_outside_the_token_layer_only_ever_shrink()
    {
        var findings = ThemeLiteralScan.ScanRepository().Where(f => !ThemeLiteralPolicy.IsPermanentlyAllowed(f)).ToList();
        var actual = findings.GroupBy(f => f.File).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var baseline = ThemeLiteralScan.ParseBaseline(File.ReadAllLines(ThemeGuardFiles.Absolute(ThemeLiteralScan.BaselinePath)));

        var grown = actual.Where(kv => kv.Value > baseline.GetValueOrDefault(kv.Key)).Select(kv => kv.Key).ToList();
        var shrunk = baseline.Where(kv => actual.GetValueOrDefault(kv.Key) < kv.Value).Select(kv => kv.Key).ToList();

        var message = new System.Text.StringBuilder();
        if (grown.Count > 0)
        {
            message.AppendLine("New hardcoded colors. " + Remedy);
            foreach (var file in grown)
            {
                message.AppendLine($"  {file}: {actual[file]} (baseline {baseline.GetValueOrDefault(file)})");
                foreach (var finding in findings.Where(f => f.File == file))
                {
                    message.AppendLine($"    {finding}");
                }
            }
        }

        if (shrunk.Count > 0)
        {
            message.AppendLine("Fewer literals than the baseline records. Lower it: replace " + ThemeLiteralScan.BaselinePath + " with:");
        }

        if (grown.Count > 0 || shrunk.Count > 0)
        {
            message.AppendLine("----- regenerated baseline -----");
            message.Append(ThemeLiteralScan.FormatBaseline(actual));
        }

        Assert.True(grown.Count == 0 && shrunk.Count == 0, message.ToString());
    }

    [Fact]
    public void Every_permanent_allowance_still_matches_something()
    {
        var findings = ThemeLiteralScan.ScanRepository();
        var stale = ThemeLiteralPolicy.Permanent.Where(allowance => !findings.Any(allowance.Matches)).ToList();
        Assert.True(stale.Count == 0,
            "Permanent allowances that match nothing (remove them): " +
            string.Join("; ", stale.Select(a => $"{a.File} {a.Member ?? a.Text}")));
    }

    [Fact]
    public void Baseline_never_lists_a_token_layer_file()
    {
        var baseline = ThemeLiteralScan.ParseBaseline(File.ReadAllLines(ThemeGuardFiles.Absolute(ThemeLiteralScan.BaselinePath)));
        Assert.DoesNotContain(baseline.Keys, ThemeLiteralPolicy.IsTokenLayer);
    }
}
```

- [ ] **Step 4: Generate the initial baseline — and review it before accepting**

Create `tests/Ntilde.Architecture.Tests/ThemeLiteralBaseline.txt` containing only the three `#` header lines from `ThemeLiteralScan.BaselineHeader`.

Run: `scripts/build.ps1 test tests/Ntilde.Architecture.Tests/Ntilde.Architecture.Tests.csproj --filter "FullyQualifiedName~ThemeLiteralGuardTests" 2>&1 | Tee-Object C:\Users\behna\AppData\Local\Temp\claude\D--projects-nova2\2abf2a21-a599-4ed7-a28a-8ad2fa54c0bd\scratchpad\baseline-run.txt`
Expected: `Color_literals_outside_the_token_layer_only_ever_shrink` FAILS and lists every finding. The other two tests PASS.

Review the listed findings before accepting them:
- **`src/Ntilde.App/SettingsWindow.axaml` / `.axaml.cs`:** only theme-editor preview findings (`Background="#111"` on the preview area; `Brushes.Black`/`Brushes.White` in the preview label). Phase 4 moves the editor. Anything else is a Task 6 miss: fix it now.
- **`src/Ntilde.App/Controls/ConnectionManager.axaml` / `.axaml.cs` / `ConnectionManagerWindow.axaml`:** zero findings. Anything listed is a Task 7 miss: fix it now.
- **Look-alikes:** if a finding is not a color (an attribute whose value happens to be a color name), add that attribute's local name to `XamlColorLiteralScanner.NonColorAttributes`, and add an `[InlineData]` with expected 0 to `ColorLiteralScannerTests.Xaml_rules` naming it.
- **Everything else** (MainWindow, TerminalPane, App.axaml tab styles, overlays, Command Assist, Agent Output chrome, Remote Files, Transfer, Replay, renderer): expected. Phases 2–4 migrate these.

Then paste everything below `----- regenerated baseline -----` from the failure message into `ThemeLiteralBaseline.txt`, replacing its contents.

- [ ] **Step 5: Run the guard and prove it bites**

Run: `scripts/build.ps1 test tests/Ntilde.Architecture.Tests/Ntilde.Architecture.Tests.csproj --filter "FullyQualifiedName~ThemeLiteralGuardTests"`
Expected: PASS, 3 tests.

Prove it:
- add `<Border Background="#123456"/>` inside any panel in `src/Ntilde.App/Controls/ConnectionManager.axaml`, re-run, and confirm it FAILS naming `ConnectionManager.axaml:<line> Border.Background=#123456`; revert;
- raise one baseline count by 1, re-run, and confirm it FAILS with "Fewer literals than the baseline records"; revert.

- [ ] **Step 6: Run the whole Architecture suite**

Run: `scripts/build.ps1 test tests/Ntilde.Architecture.Tests/Ntilde.Architecture.Tests.csproj`
Expected: PASS for all tests, including the pre-existing `CompiledBindingTests`, `NamespaceAlignmentTests` (the new `Ntilde.Shell.Theming` namespace sits in the App's `Shell` bucket) and `LayeringTests`.

- [ ] **Step 7: Commit**

```bash
git add tests/Ntilde.Architecture.Tests/ThemeLiterals/ThemeLiteralPolicy.cs tests/Ntilde.Architecture.Tests/ThemeLiterals/ThemeLiteralScan.cs tests/Ntilde.Architecture.Tests/ThemeLiteralGuardTests.cs tests/Ntilde.Architecture.Tests/ThemeLiteralBaseline.txt
git commit -m "test(arch): gating ratchet on hardcoded colors outside the theme token layer" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 12: Docs, full verification, PR

**Files:**
- Modify: `docs/ThemeSystem.md`

**Interfaces:**
- Consumes: everything above.
- Produces: the "UI color tokens" section that both guards' failure messages point to, and the phase 1 PR into `dev-review`.

- [ ] **Step 1: Document the tokens**

In `docs/ThemeSystem.md`:
1. Replace the "Key Files" list with the real paths: `src/Ntilde.VT/TerminalTheme.cs`, `src/Ntilde.App/Shell/ThemeManager.cs`, `src/Ntilde.App/Shell/TermColorJsonConverter.cs`, `src/Ntilde.App/Shell/Theming/*`, `src/Ntilde.App/SettingsWindow.axaml`, `src/Ntilde.App/themes/*.json`.
2. Replace the "Dynamic Theming" section with:
```markdown
## UI color tokens

The UI does not read theme colors directly. `ThemeTokens.Derive` (`src/Ntilde.App/Shell/Theming/`)
turns the active `TerminalTheme` into about 60 named UI colors: surfaces, text, interaction fills,
accent, status, tab-strip states. `ThemeService` installs them once into `Application.Resources` and
recolors them in place on every theme change, together with Fluent's stock-control palette and the
light/dark variant.

**Adding UI that follows the theme:**

- XAML: `{DynamicResource NtPanel}` (keys: `ThemeKeys.cs`). Never a hex or named color.
- C#: `this.FindResource(ThemeKeys.Panel)`, or bind with
  `control.Bind(Border.BackgroundProperty, control.GetResourceObservable(ThemeKeys.Panel))`.
  Never `Brushes.White`, `Color.Parse("#…")` or a `"#…"` string.
- No local copies: declaring `x:Key="NtPanel"` in a window shadows the app token.

**Guards (Architecture.Tests, gating):**

- `ThemeTokenKeyGuardTests`: every `Nt*` key used must exist in `ThemeKeys`.
- `ThemeLiteralGuardTests`: hardcoded colors outside `Shell/Theming` may only disappear
  (`ThemeLiteralBaseline.txt` is a shrinking ratchet). Colors fixed on purpose go in
  `ThemeLiteralPolicy.Permanent` with a reason.
- `ThemeContrastTests` (App.Tests): every shipped theme must yield readable chrome. When it fails,
  fix the derivation, never the threshold.

Theme v2 is landing in phases; see `docs/superpowers/specs/2026-09-23-theme-system-design.md`.
```
3. In "Adding a New Theme", delete step 2's "A restart is required" line: themes reload when Settings opens (`ThemeManager.ReloadThemes`). The rest of the file is rewritten in phase 4.

- [ ] **Step 2: Full verification**

Run each separately and wait for each to finish; never run two App.Tests at once.
1. `scripts/build.ps1 build Ntilde.sln` → `Build succeeded.`, 0 errors.
2. `scripts/build.ps1 test tests/Ntilde.Architecture.Tests/Ntilde.Architecture.Tests.csproj` → all PASS.
3. `scripts/build.ps1 test tests/Ntilde.App.Tests/Ntilde.App.Tests.csproj --blame-hang-timeout 5m 2>&1 | Tee-Object C:\Users\behna\AppData\Local\Temp\claude\D--projects-nova2\2abf2a21-a599-4ed7-a28a-8ad2fa54c0bd\scratchpad\apptests-full.txt` → all PASS. Compare the executed-test count against the last green run (~3766 plus this plan's ~150 new cases).
4. `scripts/build.ps1 build tests/Ntilde.Benchmarks/Ntilde.Benchmarks.csproj` → `Build succeeded.`
5. AOT check (release builds are NativeAOT; reflection breaks only there). This mirrors `.github/workflows/release.yml`'s `publish_aot` step. In the PowerShell tool:
   ```powershell
   $env:SKIP_RUST_NATIVE_BUILD = '1'
   scripts/build.ps1 publish src/Ntilde.App/Ntilde.App.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:SkipCliShim=true -o artifacts/publish/win-x64 2>&1 | Tee-Object C:\Users\behna\AppData\Local\Temp\claude\D--projects-nova2\2abf2a21-a599-4ed7-a28a-8ad2fa54c0bd\scratchpad\aot.txt
   Select-String -Path C:\Users\behna\AppData\Local\Temp\claude\D--projects-nova2\2abf2a21-a599-4ed7-a28a-8ad2fa54c0bd\scratchpad\aot.txt -Pattern 'IL2026|IL3050'
   ```
   Expected: the publish succeeds, and no IL2026/IL3050 line mentions `Theming`, `ThemeService` or `FluentPaletteMapping`.

If an existing App.Tests test fails because it pinned a Fluent default color (a pixel test now sees the token palette), check whether the new color is the token color for that test's theme. If it is, update the expectation to the token color and name the test in the commit message. If it is not, it is a real bug: fix it. Use superpowers:systematic-debugging.

- [ ] **Step 3: Manual smoke checklist (the user runs this; SendKeys automation is unreliable here)**

Give the user these steps with a Debug build (`scripts/build.ps1 build src/Ntilde.App`, then run `src/Ntilde.App/bin/Debug/net10.0/Ntilde.exe`):
1. Settings → Appearance → pick **GitHub Light**. Expect: the Settings window turns light immediately. Sidebar, cards, toggles, the danger button and dropdown lists are all light and readable.
2. With Settings open, pick **Dracula**. Expect: Settings turns dark live. Hover a Pill button and the danger button: the hover tints are visible. Then switch back to **GitHub Light** and open Settings → Backup → Restore…: the Restore dialog is light too. It was stock-dark on every theme before.
3. Close Settings. Open the command palette and run "Theme: GitHub Light". Open Settings again: it is light. Open the Connection Manager: it is light, row hovers are visible, and there is no dark flash.
4. Right-click a tab and hover a scrollbar: the context menu and scrollbar use the theme's tones, not stock grey.
5. Open Agent Output in a pane with an agent diff: added, removed and hunk lines use the theme's green, red and yellow.
6. Known and expected in phase 1: tabs, the title bar, the command palette, toasts and pane overlays still look as before (phase 2–3).

- [ ] **Step 4: Commit docs, push, open the PR**

```bash
git add docs/ThemeSystem.md
git commit -m "docs(theme): UI color tokens section and real key-file paths" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push -u origin feat/theme-v2-foundation
gh pr create --base dev-review --head feat/theme-v2-foundation --title "Theme v2 phase 1: app-wide theme tokens and gating guards" --body-file C:\Users\behna\AppData\Local\Temp\claude\D--projects-nova2\2abf2a21-a599-4ed7-a28a-8ad2fa54c0bd\scratchpad\pr-body.md
```
Write `pr-body.md` first. It covers:
- what changed: the token set, `ThemeService`, the Fluent palette, Settings and Connection Manager and markdown migrated, three guards;
- what visibly changes: those windows on light themes, and stock controls following the theme;
- what deliberately does not change yet: the main window's own chrome;
- the verification run: test counts from Step 2;
- the manual smoke results reported by the user.

End it with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.

Then bind the PR with the ccd_pr tools, and read CI and bot reviews through them; memory notes Greptile and Gemini auto-review on open. Do not enable auto-merge.
