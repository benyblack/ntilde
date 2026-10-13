# Settings Window Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild the Settings window as a thin frame that hosts ten topic pages behind a small contract. It shares one palette and one style file with the connection manager, and adds a theme-editor sub-page, a grouped title-bar editor and settings search. It ships as five PRs to `main`.

**Architecture:**
- **Frame.** `SettingsWindow` keeps the sidebar, a `PageHost` panel and the Save / Cancel footer. The sidebar is built from `SettingsPageRegistry`. `PageHost` holds one `UserControl` per page for the window's whole life, and each page implements `ISettingsPage`.
- **Shared state.** Pages share one `SettingsSession`, which holds the working settings, the shortcut and title-bar drafts, the injected stores and a preview sink. The window re-raises the sink's previews as its existing public events, so `MainWindow` does not change shape.
- **Palette and styles.** Brushes live in `Settings/NtPalette.axaml` and styles in `Settings/NtStyles.axaml`. `ThemePaletteResources.Apply` recolours the brushes in place through merged dictionaries.

**Tech stack:**
- C# on .NET 10, with Avalonia 12.1.3: compiled XAML, compiled bindings by default, NativeAOT for release builds.
- xUnit v3 with Avalonia.Headless.XUnit (`[AvaloniaFact]`, `[AvaloniaTheory]`).
- SkiaSharp, for the pixel comparisons.

**Spec:** `docs/superpowers/specs/2026-10-13-settings-window-redesign-design.md`. Read it alongside this plan. Where the two disagree, the next section says which one wins and why.

---

## Corrections to the spec found while planning

The spec was written before every line was read. These seven points are where reading the code changed the plan. The user should approve them along with the plan.

1. **Local profiles do not keep their SSH fields today.** `BuildLocalProfilesForEditor` (`SettingsWindow.axaml.cs:1729`) copies 13 editor-owned fields into fresh `TerminalProfile` objects, and `NormalizeSettingsProfilesForSave` does the same on Save.
   - So every Settings Save already resets the other fields of a local profile to their defaults: `SshHost`, `SshPort`, `SshUser`, `SshKeyPath`, `IdentityFilePath`, `UseSshAgent`, `JumpHostProfileId` and `Forwards`, plus `SshBackendKind`, `RemoteShellKind`, `ShellOverride`, `CursorStyle`, `CursorBlink`, `DefaultRemoteDir`, `DefaultLocalDir`, `Password`, `Icon` and `LastUsed`.
   - The spec's second round-trip case ("SSH fields survive") would therefore fail on the current code.
   - This plan pins today's behaviour instead (Task 2.1). That still proves that removing the SSH panel changes nothing.
   - **Not fixed by this plan.** No UI sets those fields, so only hand-edited `settings.json` values are lost: a per-profile cursor style, or a `ShellOverride`. Fix it as a separate change, by updating the original profile objects instead of rebuilding them.
2. **The connection manager's five button styles are not duplicates.** They share selectors with Settings but use different values: 12 px text, 10,5 padding, 30 px icon buttons, and a text-only Danger.
   - Including `NtStyles` would also restyle every TextBox, ComboBox and Slider in the connection manager, because those Settings styles have no class.
   - So in PR 1 the connection manager adopts **`NtPalette` only** and keeps its own styles. This keeps PR 1's promise of no visible change, which Task 1.5 checks pixel by pixel. Theme v2 can unify the two densities later.
3. **`SettingRow` and `SettingGroup` land in PR 3, not PR 2.** PR 2 is a one-to-one move that keeps today's markup, so nothing would use them there.
   - PR 2 still ships jump-to-row, through an attached property, `SettingsNavigation.RowId`, that any control can carry.
   - In PR 3, `SettingRow.RowId` becomes that same property, so one search covers both.
4. **The page host is a `Panel` holding every page, not a `ContentControl`.** Only the current page is visible.
   - Pages that are not on screen must stay in the window's logical tree, as today's `TabControl` items are. Otherwise `FindResource` returns null for the rows they build in code, and their `DynamicResource` references never resolve.
   - Invisible pages are not measured or rendered.
5. **The palette and styles are `x:Class` types (`new NtPalette()`, `<settings:NtStyles/>`), not `ResourceInclude` / `StyleInclude` URIs.**
   - Loading by URI goes through the runtime loader and trips IL2026 in the NativeAOT build. `UI/UiScaleWindowTheme.axaml.cs` documents the same constraint.
   - A class also guarantees a fresh brush instance for each surface.
6. **The Profiles page's override-theme combo no longer repaints the Appearance preview.**
   - Today, choosing a profile override theme paints the theme preview on the Appearance tab, which is not on screen at the time (`SettingsWindow.axaml.cs:456`). Nobody could see it.
   - After the split it would be a hidden cross-page side effect, so it is dropped.
7. **Saving a theme repaints Settings with the saved theme (a bug fix in PR 3).**
   - Today, Save theme ends with `ApplyTheme()`, which repaints with the theme named in `settings.json`, not the one being edited (`SettingsWindow.axaml.cs:385`).
   - Example: if the user selected Nord, edited it and saved, the window repainted in the theme that was stored when Settings opened.

---

## Global Constraints

- **Build and test only through the wrappers.** Use `scripts/build.ps1` from PowerShell or `scripts/build.sh` from Bash, never raw `dotnet` (see `CLAUDE.md`). Run one project per call.
- **App.Tests runs as the two CI lanes, one at a time, with full logs.** Lane filters (copy them exactly):
  - main lane: `--filter "Category!=Replay&Category!=RenderMetrics&Category!=PtySmoke&Category!=Stress&Category!=GoldenSharedPng&Lane!=PlatformBoot"`
  - boot lane: `--filter "Lane=PlatformBoot&Category!=GoldenSharedPng"`
  - Always add `--blame-hang-timeout 5m --blame-hang-dump-type mini`, and redirect output to a log file.
  - A targeted run must also add `&Lane!=PlatformBoot`, for example `--filter "FullyQualifiedName~Settings&Lane!=PlatformBoot"`.
- **`[AvaloniaFact]` / `[AvaloniaTheory]` for anything that constructs a control, a window, XAML or `NtPalette`.** A plain `[Fact]` is allowed only for code with no Avalonia objects (`SettingsSession`, search ranking over plain records), or a bare `ResourceDictionary` and brushes, as `ThemeApplicationRegressionTests` already does.
- **Every `.axaml` under `src/Ntilde.App/Settings/` uses `{DynamicResource Nt…}`, never `StaticResource`.** A page loads its XAML before it joins the window, so a `StaticResource` there cannot see the palette. From PR 2, `SettingsXamlResourceTests` enforces this. The window's own XAML may keep `StaticResource`, because it loads with its resources already present.
- **No new colour literals.** The existing ones stay for theme v2: the Danger button's `#3a1a1d` / `#5c2329` / `#4a2024`, the preview's `#111`, the toggle knob's `White`, and the window's own `#16181d` / `#e6e8ec`.
- **Stored values never change.** Every `TerminalSettings` field the window writes keeps the exact stored string or number. The round-trip test (Task 2.1) is the guard and must stay green through PRs 2–5 without edits.
- **Readable combo labels go in `Content`, stored values in `Tag`.** Load and Collect go through `Settings/ComboTags.cs` (PR 3).
- **Compiled bindings only.** No `x:CompileBindings="False"`, and no `ResourceInclude` / `StyleInclude` by URI.
- **Copy rules:**
  - Moved text keeps its wording.
  - A description longer than two lines moves into `Details` behind "More", split at a sentence boundary.
  - New strings use sentence case and plain words.
- **Git:**
  - Work in `D:\projects\nova2\.worktrees\settings-redesign`.
  - Never run a bare `git stash`. Make a WIP commit instead.
  - Each PR gets its own branch, cut from `origin/main` after the previous PR has merged.
  - Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
  - PR descriptions end with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- **Ask the user before every push, PR and merge.** Merging is manual: check CI, then run `gh pr merge <n> --merge --match-head-commit <full 40-char sha>`. Do not use `--delete-branch` from inside the worktree.
- **GUI checks are manual.** Give the user smoke-test steps; never drive the real app with SendKeys.

## Review Focus

These are the five things a user is most likely to hit that the spec implies but no feature test would catch. Each has a test in the task named after it.

1. **A `settings.json` holding a value outside a combo's set.** Examples: an older or hand-edited `BackgroundImageStretch: "uniform"`, `BlurEffect: "Transparent"`, `AgentIndicatorTabRollup: "writesonly"` or `TabStripOrientation: "vertical"`. Settings must open with today's fallback item selected, case-insensitively only where today is, and never throw (Task 3.2).
2. **Ctrl/Cmd+S while an edit is still uncommitted**, such as a number typed into Font size that has not lost focus yet. It must save the value on screen, not the previous one (Task 2.11).
3. **Arrow keys in the sidebar across a group header**, for example ↓ from Shortcuts. They must land on the next page (Command Assist), never stick on a header and never select one (Task 2.11).
4. **A Save rejected by a duplicate shortcut, then fixed and saved again.** Edits made on other pages before the rejected Save must still be written, and the rejected Save must have changed nothing (Task 2.10).
5. **The window at its 780 × 520 minimum with the theme editor open.** Both editor columns must be fully visible. The window keeps 780 logical px at any interface scale, so 200% needs no separate case (Task 3.6).

---

## File map

**New in `src/Ntilde.App/Settings/` (namespace `Ntilde.Settings`):**

| File | PR | Responsibility |
|---|---|---|
| `NtPalette.axaml(.cs)` | 1 | `Nt*` brushes + `NtMono`; one instance per merge |
| `NtStyles.axaml(.cs)` | 1 | Shared Settings styles (`DynamicResource` only); grows in PRs 2–4 |
| `SettingsPage.cs` | 2 | `SettingsPage` enum, `SettingsTarget` record |
| `ISettingsPage.cs` | 2 | Page contract, `SettingsValidationError` |
| `SettingsSession.cs` | 2 | Working settings, drafts, stores, events, font choices |
| `SettingsPreviewSink.cs` | 2 | Live-preview calls the window re-raises |
| `SettingsNavigation.cs` | 2 | `RowId` attached property, row flash |
| `SettingsResourceExtensions.cs` | 2 | `FindPaletteBrush` for rows built in code |
| `SettingsPageRegistry.cs` | 2 | `SettingsPageDescriptor`, sidebar order |
| `SettingRow.cs`, `SettingGroup.cs` | 3 | Row and group controls (templates in `NtStyles`) |
| `ComboTags.cs` | 3 | Tag/Content combo helpers |
| `SettingsRowChrome.cs` | 3 | Hairline chrome for rows built in code |
| `IHasSubPage.cs` | 3 | Lets the frame close a sub-page before revealing a row |
| `SettingsSearchIndex.cs` | 5 | Index + ranking |

**New in `src/Ntilde.App/Settings/Pages/` (namespace `Ntilde.Settings.Pages`):** `AppearancePage`, `ProfilesPage`, `ShortcutsPage`, `CommandAssistPage`, `AgentAccessPage`, `SshPage`, `BackupPage` (PR 2); `GeneralPage`, `TerminalPage`, `TabsAndTitleBarPage` (PR 3). Each is `.axaml` + `.axaml.cs`.

**Modified:** `Shell/ThemePaletteResources.cs` (PR 1), `Controls/ConnectionManager.axaml` (PR 1), `SettingsWindow.axaml(.cs)` (PRs 1, 2, 5), `MainWindow.axaml.cs` (PRs 2, 3).

**Deleted:** `SettingsSection.cs` (PR 2).

**Tests (`tests/Ntilde.App.Tests/Core/`, namespace `Ntilde.Tests.Core`):**
- New: `SettingsTestRoot`, `SettingsScreenshotHarness`, `SettingsPaletteTests` (PR 1); `SettingsWindowRoundTripTests`, `SettingsSessionTests`, `SettingsNavigationTests`, `SettingsPageTests`, `SettingsWindowFrameTests`, `SettingsWindowKeyboardTests`, `MainWindowSettingsTargetTests` (PR 2); `SettingRowTests`, `ComboTagsTests`, `SettingsThemeEditorTests` (PR 3); `SettingsTitleBarEditorTests` (PR 4); `SettingsSearchIndexTests`, `SettingsWindowSearchTests` (PR 5).
- Rewritten: the eight files listed in the spec.
- `tests/Ntilde.Architecture.Tests/SettingsXamlResourceTests.cs` (PR 2).

---

## Conventions used in every task

- **`$W` is the worktree:** `D:\projects\nova2\.worktrees\settings-redesign`. Every command runs from there. Use the PowerShell tool for `scripts/build.ps1`, and Git Bash for `git`.
- **`ORIG`** means the code at `ee60cc2`: `git show ee60cc2:src/Ntilde.App/SettingsWindow.axaml.cs` and `git show ee60cc2:src/Ntilde.App/SettingsWindow.axaml`. Every source line number in Part 2 refers to `ORIG`. PR 1 does not touch `SettingsWindow.axaml.cs`, and it leaves the tab bodies of `SettingsWindow.axaml` unchanged.
- **Targeted test run (PowerShell):**
  ```powershell
  scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~<ClassName>&Lane!=PlatformBoot" --blame-hang-timeout 5m --blame-hang-dump-type mini *> "$env:TEMP\settings-targeted.log"; Select-String -Path "$env:TEMP\settings-targeted.log" -Pattern "Passed!|Failed!|\[FAIL\]|error CS"
  ```
- **Full gate (PowerShell)** at the end of every PR, run one command at a time:
  ```powershell
  scripts/build.ps1 build src/Ntilde.App *> "$env:TEMP\settings-build.log"; Select-String -Path "$env:TEMP\settings-build.log" -Pattern "error|Build succeeded"
  scripts/build.ps1 test tests/Ntilde.App.Tests --filter "Category!=Replay&Category!=RenderMetrics&Category!=PtySmoke&Category!=Stress&Category!=GoldenSharedPng&Lane!=PlatformBoot" --blame-hang-timeout 5m --blame-hang-dump-type mini *> "$env:TEMP\settings-apptests-main.log"; Select-String -Path "$env:TEMP\settings-apptests-main.log" -Pattern "Passed!|Failed!|\[FAIL\]"
  scripts/build.ps1 test tests/Ntilde.App.Tests --filter "Lane=PlatformBoot&Category!=GoldenSharedPng" --blame-hang-timeout 5m --blame-hang-dump-type mini *> "$env:TEMP\settings-apptests-boot.log"; Select-String -Path "$env:TEMP\settings-apptests-boot.log" -Pattern "Passed!|Failed!|\[FAIL\]"
  scripts/build.ps1 test tests/Ntilde.Architecture.Tests *> "$env:TEMP\settings-arch.log"; Select-String -Path "$env:TEMP\settings-arch.log" -Pattern "Passed!|Failed!|\[FAIL\]"
  ```
  Expected: `Build succeeded`, and every run reports `Passed!` with `Failed: 0`. A `[FAIL]` line means a red run; read the log before blaming flakiness.
- **Screenshots (PowerShell)**, written to a fresh directory:
  ```powershell
  $env:NTILDE_SETTINGS_SHOTS = "$env:TEMP\settings-shots\<name>"; scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~SettingsScreenshotHarness&Lane!=PlatformBoot" *> "$env:TEMP\settings-shots.log"; Remove-Item Env:NTILDE_SETTINGS_SHOTS
  ```
  Set `NTILDE_SETTINGS_SHOTS_BASELINE` as well to compare against an earlier directory pixel by pixel.

---

# Part 1 — PR 1: Shared palette and styles (no visible change)

**Outcome:**
- `NtPalette` and `NtStyles` exist.
- The Settings window uses both, and the connection manager uses `NtPalette`.
- `ThemePaletteResources.Apply` recolours brushes that live in merged dictionaries.
- Screenshots of both surfaces are pixel-identical before and after.

## Task 1.0: Prepare the PR 1 branch

**Files:** none.

- [ ] **Step 1: Bring the branch up to date.** Branch `feat/settings-redesign` holds the spec commit (0bf6293) and this plan's commit, and nothing else. Rebase it onto the latest `main`:

```bash
cd /d/projects/nova2/.worktrees/settings-redesign
git fetch origin
git status --short            # expect: nothing
git log --oneline origin/main..HEAD   # expect: only the two docs commits
git rebase origin/main
```

Expected: the rebase succeeds with no conflicts, because only docs are on the branch.

- [ ] **Step 2: Confirm that the Settings sources still match the line numbers this plan uses.**

```bash
git diff --stat ee60cc2 origin/main -- src/Ntilde.App/SettingsWindow.axaml src/Ntilde.App/SettingsWindow.axaml.cs src/Ntilde.App/Controls/ConnectionManager.axaml src/Ntilde.App/Shell/ThemePaletteResources.cs src/Ntilde.App/MainWindow.axaml.cs
```

Expected: no output for the four Settings and palette files. `MainWindow.axaml.cs` may show changes from other work. If so, find `OpenSettings` and its callers by name, not by line number, in Task 2.10.

**If any Settings file shows changes:** stop and tell the user. The line ranges in Part 2 would be stale.

## Task 1.1: Test root helper and screenshot harness (baseline capture)

**Files:**
- Create: `tests/Ntilde.App.Tests/Core/SettingsTestRoot.cs`
- Create: `tests/Ntilde.App.Tests/Core/SettingsScreenshotHarness.cs`

**Interfaces:**
- Produces `SettingsTestRoot`, used by every later Settings test:
  - `static SettingsTestRoot Empty()`, `static SettingsTestRoot WithSettings(string json)`
  - `BackupTestTree Tree`, `string Root`
  - `void CopyShippedTheme(string fileName)`, `JsonDocument ReadSettings()`
  - `static string RepoRoot()`
- Produces `SettingsScreenshotHarness`. It does nothing unless `NTILDE_SETTINGS_SHOTS` is set, and it compares pixel by pixel when `NTILDE_SETTINGS_SHOTS_BASELINE` is also set.

- [ ] **Step 1: Write `SettingsTestRoot`.**

```csharp
using System.Text.Json;
using Ntilde.Tests.Backup;

namespace Ntilde.Tests.Core;

/// <summary>
/// A temporary app-data root for one Settings test. Points NTILDE_APPDATA_ROOT at a fresh
/// <see cref="BackupTestTree"/> for its lifetime, so a <see cref="Ntilde.SettingsWindow"/> built
/// inside it never reads or writes the developer's real profile.
/// </summary>
internal sealed class SettingsTestRoot : IDisposable
{
    private const string RootVariable = "NTILDE_APPDATA_ROOT";

    private readonly string? _previousRoot;

    private SettingsTestRoot(BackupTestTree tree)
    {
        Tree = tree;
        _previousRoot = Environment.GetEnvironmentVariable(RootVariable);
        Environment.SetEnvironmentVariable(RootVariable, tree.Root);
    }

    public BackupTestTree Tree { get; }

    public string Root => Tree.Root;

    public static SettingsTestRoot Empty() => new(BackupTestTree.CreateEmpty());

    public static SettingsTestRoot WithSettings(string settingsJson)
    {
        var root = Empty();
        root.Tree.WriteFile("settings.json", settingsJson);
        return root;
    }

    /// <summary>Copies one of the themes the app ships (src/Ntilde.App/themes) into this root.</summary>
    public void CopyShippedTheme(string fileName)
    {
        string source = Path.Combine(RepoRoot(), "src", "Ntilde.App", "themes", fileName);
        Tree.WriteFile(Path.Combine("themes", fileName), File.ReadAllText(source));
    }

    public JsonDocument ReadSettings() => Tree.ReadJson("settings.json");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RootVariable, _previousRoot);
        Tree.Dispose();
    }

    internal static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ntilde.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root from the test output path.");
    }
}
```

- [ ] **Step 2: Write the harness.** The PR 1 version walks the `TabControl`. Task 2.13 switches it to the page registry.

```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ntilde.Controls;
using Ntilde.Shell;
using SkiaSharp;

namespace Ntilde.Tests.Core;

/// <summary>
/// Writes headless PNGs of the Settings window and the connection manager for the settings
/// redesign's before/after comparisons and PR screenshots. Does nothing unless
/// NTILDE_SETTINGS_SHOTS names an output directory, so CI never runs it.
/// </summary>
/// <remarks>
/// With NTILDE_SETTINGS_SHOTS_BASELINE also set to an earlier run's directory, every image is
/// compared pixel by pixel with the file of the same name there, and the test fails naming each
/// image that differs. That is how a "no visible change" PR proves it.
/// </remarks>
public sealed class SettingsScreenshotHarness
{
    private const string OutputVariable = "NTILDE_SETTINGS_SHOTS";
    private const string BaselineVariable = "NTILDE_SETTINGS_SHOTS_BASELINE";

    [AvaloniaTheory]
    [InlineData("Default", "dark")]
    [InlineData("GitHub Light", "light")]
    public void Capture(string themeName, string slug)
    {
        string? output = Environment.GetEnvironmentVariable(OutputVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(output), $"Set {OutputVariable} to a directory to capture Settings screenshots.");
        Directory.CreateDirectory(output!);

        using var root = SettingsTestRoot.WithSettings($$"""{"ThemeName":"{{themeName}}"}""");
        root.CopyShippedTheme("GitHubLight.json");

        var written = new List<string>();
        CaptureSettings(output!, slug, written);
        CaptureConnectionManager(output!, slug, themeName, written);
        CompareWithBaseline(written);
    }

    private static void CaptureSettings(string output, string slug, List<string> written)
    {
        var window = new Ntilde.SettingsWindow();
        try
        {
            window.Show();
            var tabs = window.FindControl<TabControl>("MainTabs")!;
            for (int i = 0; i < tabs.ItemCount; i++)
            {
                tabs.SelectedIndex = i;
                string header = ((TabItem)tabs.Items[i]!).Header!.ToString()!.Replace(' ', '-');
                written.AddRange(CaptureScrolled(window, output, $"settings-{slug}-{i}-{header}"));
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static void CaptureConnectionManager(string output, string slug, string themeName, List<string> written)
    {
        var manager = new ConnectionManager();
        manager.ApplyTheme(new ThemeManager().GetTheme(themeName));
        var host = new Window { Width = 1080, Height = 720, Content = manager };
        try
        {
            host.Show();
            written.Add(Save(host, output, $"connections-{slug}.png"));
        }
        finally
        {
            host.Close();
        }
    }

    /// <summary>
    /// Captures the page in viewport-sized steps down its main scroller, so content below the
    /// fold is compared too.
    /// </summary>
    private static List<string> CaptureScrolled(Window window, string output, string name)
    {
        Dispatcher.UIThread.RunJobs();
        ScrollViewer? scroller = window.GetVisualDescendants()
            .OfType<ScrollViewer>()
            .Where(sv => sv.IsEffectivelyVisible && sv.Extent.Height > sv.Viewport.Height)
            .OrderByDescending(sv => sv.Viewport.Width * sv.Viewport.Height)
            .FirstOrDefault();

        if (scroller is null)
        {
            return [Save(window, output, $"{name}.png")];
        }

        var paths = new List<string>();
        int part = 0;
        for (double y = 0; ; y += scroller.Viewport.Height)
        {
            scroller.Offset = new Vector(0, Math.Min(y, scroller.Extent.Height - scroller.Viewport.Height));
            paths.Add(Save(window, output, $"{name}-{part++}.png"));
            if (y + scroller.Viewport.Height >= scroller.Extent.Height)
            {
                break;
            }
        }

        scroller.Offset = default;
        return paths;
    }

    internal static string Save(TopLevel topLevel, string output, string fileName)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = topLevel.CaptureRenderedFrame();
        Assert.NotNull(frame);
        string path = Path.Combine(output, fileName);
        frame!.Save(path);
        return path;
    }

    internal static void CompareWithBaseline(IReadOnlyList<string> written)
    {
        string? baseline = Environment.GetEnvironmentVariable(BaselineVariable);
        if (string.IsNullOrWhiteSpace(baseline))
        {
            return;
        }

        var differences = new List<string>();
        foreach (string path in written)
        {
            string name = Path.GetFileName(path);
            string before = Path.Combine(baseline, name);
            if (!File.Exists(before))
            {
                differences.Add($"{name}: no baseline image");
                continue;
            }

            using SKBitmap a = SKBitmap.Decode(before);
            using SKBitmap b = SKBitmap.Decode(path);
            if (a.Width != b.Width || a.Height != b.Height)
            {
                differences.Add($"{name}: size {a.Width}x{a.Height} -> {b.Width}x{b.Height}");
                continue;
            }

            int changed = 0;
            for (int y = 0; y < a.Height; y++)
            {
                for (int x = 0; x < a.Width; x++)
                {
                    if (a.GetPixel(x, y) != b.GetPixel(x, y))
                    {
                        changed++;
                    }
                }
            }

            if (changed > 0)
            {
                differences.Add($"{name}: {changed} pixels differ");
            }
        }

        Assert.True(differences.Count == 0, "Screenshots differ from the baseline:\n" + string.Join("\n", differences));
    }
}
```

- [ ] **Step 3: Check that the harness skips in a normal run and captures when asked.**

```powershell
scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~SettingsScreenshotHarness&Lane!=PlatformBoot" *> "$env:TEMP\settings-shots.log"; Select-String -Path "$env:TEMP\settings-shots.log" -Pattern "Passed!|Failed!|Skipped|\[FAIL\]"
```

Expected: 2 skipped, 0 failed.

```powershell
$env:NTILDE_SETTINGS_SHOTS = "$env:TEMP\settings-shots\pr1-before"; scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~SettingsScreenshotHarness&Lane!=PlatformBoot" *> "$env:TEMP\settings-shots.log"; Remove-Item Env:NTILDE_SETTINGS_SHOTS; Get-ChildItem "$env:TEMP\settings-shots\pr1-before" | Measure-Object
```

Expected: the run passes. The directory holds several PNGs per tab for dark and light, plus `connections-dark.png` and `connections-light.png`. Open two of them to check they show the window, not a blank frame. **This directory is the baseline for Task 1.5. Do not regenerate it after Task 1.2.**

- [ ] **Step 4: Commit.**

```bash
git add tests/Ntilde.App.Tests/Core/SettingsTestRoot.cs tests/Ntilde.App.Tests/Core/SettingsScreenshotHarness.cs
git commit -m "test(settings): temp app-data root helper and headless screenshot harness" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

## Task 1.2: `ThemePaletteResources.Apply` resolves through merged dictionaries

**Files:**
- Modify: `src/Ntilde.App/Shell/ThemePaletteResources.cs:46-56` (`UpdateBrush`)
- Create: `tests/Ntilde.App.Tests/Core/SettingsPaletteTests.cs`

**Interfaces:**
- `ThemePaletteResources.Apply(IResourceDictionary, TerminalTheme)` keeps its signature. After this task it recolours a brush found anywhere `TryGetResource` can see, and adds an own entry only when no brush exists under that key.

- [ ] **Step 1: Write the failing test.**

```csharp
using Avalonia.Controls;
using Avalonia.Media;
using Ntilde.Shell;
using Ntilde.VT;

namespace Ntilde.Tests.Core;

/// <summary>
/// The Nt* palette moves out of each window into a merged dictionary (Settings/NtPalette.axaml).
/// These tests pin what that move must not break: a theme switch still recolours the brush
/// instances the controls hold, and each surface still owns its own instances.
/// </summary>
public sealed class SettingsPaletteTests
{
    /// <summary>A light theme whose NtPanel works out to RGB(227,224,215): background shifted by -18.</summary>
    internal static TerminalTheme LightTheme() => new()
    {
        Name = "Palette test light",
        Background = TermColor.FromRgb(245, 242, 233),
        Foreground = TermColor.FromRgb(20, 20, 20),
        Blue = TermColor.FromRgb(0x03, 0x66, 0xd6),
    };

    internal static readonly Color LightPanel = Color.FromRgb(227, 224, 215);

    /// <summary>
    /// Apply used to read <c>resources[key]</c>, which sees only the dictionary's own entries. For a
    /// brush living in a merged dictionary it then added a second brush, leaving every control that
    /// held the merged one on the old colour, with no error anywhere.
    /// </summary>
    [Fact]
    public void Apply_RecolorsABrushThatLivesInAMergedDictionary()
    {
        var held = new SolidColorBrush(Colors.Magenta);
        var merged = new ResourceDictionary { ["NtPanel"] = held };
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(merged);

        ThemePaletteResources.Apply(resources, LightTheme());

        Assert.Equal(LightPanel, held.Color);
        Assert.False(resources.ContainsKey("NtPanel"), "Apply shadowed the merged brush with a new one.");
    }
}
```

- [ ] **Step 2: Run it and watch it fail.** Use the targeted run with `<ClassName>` = `SettingsPaletteTests`.

Expected: FAIL. `held.Color` is still Magenta, or the `ContainsKey` assert fires.

- [ ] **Step 3: Fix `UpdateBrush`.** Replace the method body at `ThemePaletteResources.cs:46-56`:

```csharp
    private static void UpdateBrush(IResourceDictionary resources, string key, Color color)
    {
        // TryGetResource, not resources[key]: the indexer reads only this dictionary's own
        // entries, so a brush in a merged dictionary (Settings/NtPalette.axaml) would be missed, a
        // second brush added here, and every control holding the merged one would stop recolouring
        // without any error.
        if (resources.TryGetResource(key, null, out object? existing) && existing is SolidColorBrush brush)
        {
            brush.Color = color;
        }
        else
        {
            resources[key] = new SolidColorBrush(color);
        }
    }
```

- [ ] **Step 4: Run the new test and the existing palette tests.** Use the targeted run twice, with `SettingsPaletteTests`, then `ThemeApplicationRegressionTests`.

Expected: both PASS. The existing `Apply_*` tests use plain dictionaries, where `TryGetResource` finds the own entries just as the indexer did.

- [ ] **Step 5: Commit.**

```bash
git add src/Ntilde.App/Shell/ThemePaletteResources.cs tests/Ntilde.App.Tests/Core/SettingsPaletteTests.cs
git commit -m "fix(theme): recolour palette brushes that live in merged dictionaries" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

## Task 1.3: `NtPalette` and `NtStyles`; the Settings window adopts both

**Files:**
- Create: `src/Ntilde.App/Settings/NtPalette.axaml`, `src/Ntilde.App/Settings/NtPalette.axaml.cs`
- Create: `src/Ntilde.App/Settings/NtStyles.axaml`, `src/Ntilde.App/Settings/NtStyles.axaml.cs`
- Modify: `src/Ntilde.App/SettingsWindow.axaml:1-271` (resources and styles only; the body from line 273 is untouched)
- Modify: `tests/Ntilde.App.Tests/Core/ThemeApplicationRegressionTests.cs` (`SettingsWindow_CardRows_UseTheWindowPaletteBrushes`)
- Test: `tests/Ntilde.App.Tests/Core/SettingsPaletteTests.cs`

**Interfaces:**
- Produces `Ntilde.Settings.NtPalette : ResourceDictionary` (public, `new NtPalette()` loads a fresh set of brushes) and `Ntilde.Settings.NtStyles : Styles` (public).
- XAML namespace: `xmlns:settings="clr-namespace:Ntilde.Settings"`.

- [ ] **Step 1: Write the failing tests.** Append them to `SettingsPaletteTests`, and add `using Avalonia.Headless.XUnit;`, `using Avalonia.LogicalTree;`, `using Avalonia.Threading;` and `using Ntilde.Settings;` at the top.

```csharp
    [AvaloniaFact]
    public void NtPalette_HoldsEveryKeyApplyWrites()
    {
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(new NtPalette());

        ThemePaletteResources.Apply(resources, LightTheme());

        // Any key Apply writes that the palette lacks would land here as a new own entry.
        Assert.Empty(resources);
    }

    [AvaloniaFact]
    public void NtPalette_EachInstanceOwnsItsBrushes()
    {
        var first = new NtPalette();
        var second = new NtPalette();

        foreach (object key in first.Keys)
        {
            if (first[key] is SolidColorBrush brush)
            {
                Assert.NotSame(brush, second[key]);
            }
        }
    }

    [AvaloniaFact]
    public void NtPalette_KeepsTheColoursBothSurfacesShippedWith()
    {
        var palette = new NtPalette();

        Assert.Equal(Color.Parse("#16181d"), ((SolidColorBrush)palette["NtWindowBg"]!).Color);
        Assert.Equal(Color.Parse("#353b46"), ((SolidColorBrush)palette["NtHairlineStrong"]!).Color);
        Assert.Equal(Color.Parse("#2461afef"), ((SolidColorBrush)palette["NtBlueDim"]!).Color);
        Assert.Equal(Color.Parse("#c678dd"), ((SolidColorBrush)palette["NtMagenta"]!).Color);
        Assert.IsType<FontFamily>(palette["NtMono"]);
    }

    [AvaloniaFact]
    public void SettingsWindow_ControlsHoldTheMergedPaletteBrushes_AndRecolourWithThem()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Nothing shadows the merged palette. (The window's own dictionary is not empty:
            // UiScale.PinScale writes NtUiScaleTransform there to hold the window's scale.)
            foreach (object key in new NtPalette().Keys)
            {
                Assert.False(window.Resources.ContainsKey(key), $"{key} is shadowed by an own entry.");
            }

            var panel = Assert.IsType<SolidColorBrush>(window.FindResource("NtPanel"));
            var cancel = window.FindControl<Button>("BtnCancel")!; // Pill: Background = NtPanel via NtStyles
            Assert.Same(panel, cancel.Background);

            var chrome = Assert.IsType<SolidColorBrush>(window.FindResource("NtChromeBg"));
            var footer = cancel.GetLogicalAncestors().OfType<Border>().First(); // {StaticResource NtChromeBg}
            Assert.Same(chrome, footer.Background);

            // What SettingsWindow.UpdatePaletteResources runs on a theme switch.
            ThemePaletteResources.Apply(window.Resources, LightTheme());

            Assert.Equal(LightPanel, panel.Color);
            Assert.Same(panel, cancel.Background);
        }
        finally
        {
            window.Close();
        }
    }
```

- [ ] **Step 2: Run them and watch them fail.** Use the targeted run with `SettingsPaletteTests`.

Expected: a compile error, `The type or namespace name 'NtPalette' could not be found`.

- [ ] **Step 3: Create `NtPalette`.**

`src/Ntilde.App/Settings/NtPalette.axaml`:

```xml
<!-- The Nt* chrome palette shared by the Settings window and the connection manager.

     Each surface merges its OWN instance (new NtPalette()), so recolouring one never touches the
     other. ThemePaletteResources.Apply recolours these brushes in place on a theme switch, which is
     why it resolves keys through merged dictionaries (TryGetResource) rather than reading the
     owning dictionary directly.

     A class rather than a ResourceInclude URI: loading by URI goes through the runtime loader and
     trips IL2026 in the NativeAOT release build (see UI/UiScaleWindowTheme.axaml.cs).

     These are the defaults both surfaces shipped with before the move; theme v2 swaps them for
     app tokens in this one file. -->
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    x:Class="Ntilde.Settings.NtPalette">
    <SolidColorBrush x:Key="NtWindowBg"        Color="#16181d"/>
    <SolidColorBrush x:Key="NtChromeBg"        Color="#1c1f26"/>
    <SolidColorBrush x:Key="NtPanel"           Color="#23272f"/>
    <SolidColorBrush x:Key="NtPanelAlt"        Color="#1a1d23"/>
    <SolidColorBrush x:Key="NtHairline"        Color="#2a2f38"/>
    <SolidColorBrush x:Key="NtHairlineStrong"  Color="#353b46"/>
    <SolidColorBrush x:Key="NtFg"              Color="#e6e8ec"/>
    <SolidColorBrush x:Key="NtFg2"             Color="#b4bac4"/>
    <SolidColorBrush x:Key="NtFg3"             Color="#8b93a1"/>
    <SolidColorBrush x:Key="NtFg4"             Color="#5c6370"/>
    <SolidColorBrush x:Key="NtBlue"            Color="#61afef"/>
    <SolidColorBrush x:Key="NtBlueDim"         Color="#2461afef"/>
    <SolidColorBrush x:Key="NtBlueFaint"       Color="#1561afef"/>
    <SolidColorBrush x:Key="NtBlueBorder"      Color="#4d61afef"/>
    <SolidColorBrush x:Key="NtAccentHover"     Color="#7cc1f5"/>
    <SolidColorBrush x:Key="NtAccentPressed"   Color="#4f9bde"/>
    <SolidColorBrush x:Key="NtAccentFg"        Color="#0a1622"/>
    <SolidColorBrush x:Key="NtGreen"           Color="#98c379"/>
    <SolidColorBrush x:Key="NtYellow"          Color="#e5c07b"/>
    <SolidColorBrush x:Key="NtRed"             Color="#e06c75"/>
    <SolidColorBrush x:Key="NtMagenta"         Color="#c678dd"/>
    <FontFamily x:Key="NtMono">JetBrains Mono, Cascadia Mono, Consolas, Menlo, monospace</FontFamily>
</ResourceDictionary>
```

`src/Ntilde.App/Settings/NtPalette.axaml.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Ntilde.Settings;

/// <summary>Code-behind for <c>NtPalette.axaml</c>. Each instance loads its own brushes.</summary>
public partial class NtPalette : ResourceDictionary
{
    public NtPalette()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
```

- [ ] **Step 4: Create `NtStyles`.** These are the styles from `SettingsWindow.axaml:53-270`. The `TabControl#MainTabs` block at 35-51 stays in the window. Every `StaticResource` becomes `DynamicResource`, and every literal stays as it is.

`src/Ntilde.App/Settings/NtStyles.axaml`:

```xml
<!-- Shared Settings styles. Every palette reference is a DynamicResource: a page control loads its
     XAML in its own constructor, before it joins the window, and a StaticResource there cannot see
     the window's palette. The colour literals below (#3a1a1d, #5c2329, #4a2024, White) are theme
     v2's to replace with tokens. -->
<Styles xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        x:Class="Ntilde.Settings.NtStyles">

    <!-- Section header (uppercase, dim) -->
    <Style Selector="TextBlock.SectionHeader">
        <Setter Property="FontSize" Value="10"/>
        <Setter Property="FontWeight" Value="SemiBold"/>
        <Setter Property="Foreground" Value="{DynamicResource NtFg3}"/>
        <Setter Property="LetterSpacing" Value="1.2"/>
    </Style>

    <!-- Page title + subtitle -->
    <Style Selector="TextBlock.H1">
        <Setter Property="FontSize" Value="22"/>
        <Setter Property="FontWeight" Value="SemiBold"/>
        <Setter Property="Foreground" Value="{DynamicResource NtFg}"/>
    </Style>
    <Style Selector="TextBlock.Subtitle">
        <Setter Property="FontSize" Value="13"/>
        <Setter Property="Foreground" Value="{DynamicResource NtFg3}"/>
    </Style>

    <!-- Row label / description -->
    <Style Selector="TextBlock.RowLabel">
        <Setter Property="FontSize" Value="13"/>
        <Setter Property="Foreground" Value="{DynamicResource NtFg}"/>
    </Style>
    <Style Selector="TextBlock.RowDesc">
        <Setter Property="FontSize" Value="12"/>
        <Setter Property="Foreground" Value="{DynamicResource NtFg3}"/>
        <Setter Property="TextWrapping" Value="Wrap"/>
    </Style>

    <!-- Inputs (TextBox, NumericUpDown) -->
    <Style Selector="TextBox">
        <Setter Property="Background" Value="{DynamicResource NtPanelAlt}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource NtHairline}"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="CornerRadius" Value="5"/>
        <Setter Property="Padding" Value="10,6"/>
        <Setter Property="Foreground" Value="{DynamicResource NtFg}"/>
        <Setter Property="CaretBrush" Value="{DynamicResource NtBlue}"/>
        <Setter Property="SelectionBrush" Value="{DynamicResource NtBlueDim}"/>
        <Setter Property="FontSize" Value="13"/>
        <Setter Property="MinHeight" Value="32"/>
    </Style>
    <Style Selector="TextBox:focus /template/ Border#PART_BorderElement">
        <Setter Property="BorderBrush" Value="{DynamicResource NtBlue}"/>
    </Style>
    <Style Selector="NumericUpDown">
        <Setter Property="Background" Value="{DynamicResource NtPanelAlt}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource NtHairline}"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="CornerRadius" Value="5"/>
        <Setter Property="Foreground" Value="{DynamicResource NtFg}"/>
        <Setter Property="FontSize" Value="13"/>
    </Style>

    <!-- ComboBox -->
    <Style Selector="ComboBox">
        <Setter Property="Background" Value="{DynamicResource NtPanelAlt}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource NtHairline}"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="CornerRadius" Value="5"/>
        <Setter Property="Padding" Value="10,4"/>
        <Setter Property="Foreground" Value="{DynamicResource NtFg}"/>
        <Setter Property="FontSize" Value="13"/>
        <Setter Property="MinHeight" Value="32"/>
    </Style>

    <!-- Primary action button -->
    <Style Selector="Button.Primary">
        <Setter Property="Background" Value="{DynamicResource NtBlue}"/>
        <Setter Property="Foreground" Value="{DynamicResource NtAccentFg}"/>
        <Setter Property="FontWeight" Value="SemiBold"/>
        <Setter Property="FontSize" Value="13"/>
        <Setter Property="CornerRadius" Value="5"/>
        <Setter Property="Padding" Value="16,7"/>
        <Setter Property="BorderThickness" Value="0"/>
        <Setter Property="HorizontalContentAlignment" Value="Center"/>
        <Setter Property="VerticalContentAlignment" Value="Center"/>
    </Style>
    <Style Selector="Button.Primary:pointerover /template/ ContentPresenter">
        <Setter Property="Background" Value="{DynamicResource NtAccentHover}"/>
    </Style>
    <Style Selector="Button.Primary:pressed /template/ ContentPresenter">
        <Setter Property="Background" Value="{DynamicResource NtAccentPressed}"/>
    </Style>

    <!-- Secondary pill button -->
    <Style Selector="Button.Pill">
        <Setter Property="Background" Value="{DynamicResource NtPanel}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource NtHairline}"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="CornerRadius" Value="5"/>
        <Setter Property="Padding" Value="14,6"/>
        <Setter Property="FontSize" Value="13"/>
        <Setter Property="Foreground" Value="{DynamicResource NtFg}"/>
    </Style>
    <Style Selector="Button.Pill:pointerover /template/ ContentPresenter">
        <Setter Property="Background" Value="{DynamicResource NtHairline}"/>
    </Style>

    <!-- Danger button (delete) -->
    <Style Selector="Button.Danger">
        <Setter Property="Background" Value="#3a1a1d"/>
        <Setter Property="BorderBrush" Value="#5c2329"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="Foreground" Value="{DynamicResource NtRed}"/>
        <Setter Property="CornerRadius" Value="5"/>
        <Setter Property="Padding" Value="14,6"/>
        <Setter Property="FontSize" Value="13"/>
    </Style>
    <Style Selector="Button.Danger:pointerover /template/ ContentPresenter">
        <Setter Property="Background" Value="#4a2024"/>
    </Style>

    <!-- Icon-only ghost button -->
    <Style Selector="Button.IconBtn">
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="BorderThickness" Value="0"/>
        <Setter Property="Padding" Value="6"/>
        <Setter Property="CornerRadius" Value="4"/>
        <Setter Property="Foreground" Value="{DynamicResource NtFg3}"/>
        <Setter Property="Width" Value="32"/>
        <Setter Property="Height" Value="32"/>
        <Setter Property="HorizontalContentAlignment" Value="Center"/>
        <Setter Property="VerticalContentAlignment" Value="Center"/>
    </Style>
    <Style Selector="Button.IconBtn:pointerover">
        <Setter Property="Foreground" Value="{DynamicResource NtFg}"/>
    </Style>
    <Style Selector="Button.IconBtn:pointerover /template/ ContentPresenter">
        <Setter Property="Background" Value="{DynamicResource NtHairline}"/>
    </Style>

    <!-- Sidebar (ListBox + items) -->
    <Style Selector="ListBox.SideNav">
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="BorderThickness" Value="0"/>
        <Setter Property="Padding" Value="0"/>
    </Style>
    <Style Selector="ListBox.SideNav ListBoxItem">
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="BorderThickness" Value="2,0,0,0"/>
        <Setter Property="BorderBrush" Value="Transparent"/>
        <Setter Property="Padding" Value="10,7"/>
        <Setter Property="Margin" Value="6,1"/>
        <Setter Property="CornerRadius" Value="5"/>
        <Setter Property="Foreground" Value="{DynamicResource NtFg2}"/>
        <Setter Property="FontSize" Value="13"/>
    </Style>
    <Style Selector="ListBox.SideNav ListBoxItem:pointerover /template/ ContentPresenter">
        <Setter Property="Background" Value="{DynamicResource NtPanelAlt}"/>
    </Style>
    <Style Selector="ListBox.SideNav ListBoxItem:selected /template/ ContentPresenter">
        <Setter Property="Background" Value="{DynamicResource NtBlueDim}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource NtBlue}"/>
        <Setter Property="BorderThickness" Value="2,0,0,0"/>
    </Style>
    <Style Selector="ListBox.SideNav ListBoxItem:selected">
        <Setter Property="Foreground" Value="{DynamicResource NtFg}"/>
    </Style>

    <!-- Toggle-pill restyle for CheckBoxes that should look like toggles.
         CheckBox type is preserved so the code-behind keeps finding them. -->
    <Style Selector="CheckBox.Toggle">
        <Setter Property="Padding" Value="0"/>
        <Setter Property="Foreground" Value="{DynamicResource NtFg}"/>
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="Template">
            <ControlTemplate>
                <Grid ColumnDefinitions="*,Auto">
                    <ContentPresenter Grid.Column="0"
                                      Content="{TemplateBinding Content}"
                                      VerticalAlignment="Center"
                                      Foreground="{TemplateBinding Foreground}"
                                      IsVisible="{Binding $parent[CheckBox].Content, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"/>
                    <Border Grid.Column="1"
                            Name="PART_Track"
                            Width="34" Height="20"
                            CornerRadius="10"
                            Background="{DynamicResource NtHairlineStrong}"
                            Margin="8,0,0,0">
                        <Ellipse Name="PART_Knob"
                                 Width="16" Height="16"
                                 Fill="White"
                                 HorizontalAlignment="Left"
                                 Margin="2,0,0,0"/>
                    </Border>
                </Grid>
            </ControlTemplate>
        </Setter>
    </Style>
    <Style Selector="CheckBox.Toggle:checked /template/ Border#PART_Track">
        <Setter Property="Background" Value="{DynamicResource NtBlue}"/>
    </Style>
    <Style Selector="CheckBox.Toggle:checked /template/ Ellipse#PART_Knob">
        <Setter Property="HorizontalAlignment" Value="Right"/>
        <Setter Property="Margin" Value="0,0,2,0"/>
    </Style>

    <!-- Slider -->
    <Style Selector="Slider">
        <Setter Property="Foreground" Value="{DynamicResource NtBlue}"/>
        <Setter Property="Background" Value="{DynamicResource NtHairlineStrong}"/>
        <Setter Property="MinHeight" Value="22"/>
    </Style>

    <!-- RadioButton -->
    <Style Selector="RadioButton">
        <Setter Property="Foreground" Value="{DynamicResource NtFg}"/>
        <Setter Property="FontSize" Value="13"/>
    </Style>

    <!-- Separator -->
    <Style Selector="Separator">
        <Setter Property="Background" Value="{DynamicResource NtHairline}"/>
        <Setter Property="Height" Value="1"/>
        <Setter Property="Margin" Value="0,4"/>
    </Style>
</Styles>
```

`src/Ntilde.App/Settings/NtStyles.axaml.cs`:

```csharp
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Ntilde.Settings;

/// <summary>Code-behind for <c>NtStyles.axaml</c>, the shared Settings styles.</summary>
public partial class NtStyles : Styles
{
    public NtStyles()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
```

- [ ] **Step 5: Point the Settings window at them.** In `SettingsWindow.axaml`, replace lines 1-271 (everything before `<Grid RowDefinitions="*,Auto">`) with the block below. Lines 36-51 are copied verbatim.

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:settings="clr-namespace:Ntilde.Settings"
        x:Class="Ntilde.SettingsWindow"
        Title="Settings"
        Width="880" Height="620"
        MinWidth="780" MinHeight="520"
        WindowStartupLocation="CenterOwner"
        Background="#16181d"
        Foreground="#e6e8ec"
        FontFamily="Inter, Segoe UI, system-ui, sans-serif">

    <Window.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <settings:NtPalette/>
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Window.Resources>

    <Window.Styles>
        <settings:NtStyles/>

        <!-- Hide the TabControl's header strip — sidebar drives selection. -->
        <Style Selector="TabControl#MainTabs">
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="BorderThickness" Value="0"/>
            <Setter Property="Padding" Value="0"/>
        </Style>
        <Style Selector="TabControl#MainTabs /template/ ItemsPresenter#PART_ItemsPresenter">
            <Setter Property="IsVisible" Value="False"/>
        </Style>
        <Style Selector="TabControl#MainTabs > TabItem">
            <Setter Property="Height" Value="0"/>
            <Setter Property="MinHeight" Value="0"/>
            <Setter Property="Padding" Value="0"/>
            <Setter Property="Margin" Value="0"/>
            <Setter Property="Opacity" Value="0"/>
            <Setter Property="IsHitTestVisible" Value="False"/>
        </Style>
    </Window.Styles>

```

- [ ] **Step 6: Update the card-row test, which reads the window's own dictionary.** In `ThemeApplicationRegressionTests.SettingsWindow_CardRows_UseTheWindowPaletteBrushes`, replace the two lookups:

```csharp
        var panelBrush = Assert.IsType<SolidColorBrush>(window.FindResource("NtPanel"));
        var hairlineBrush = Assert.IsType<SolidColorBrush>(window.FindResource("NtHairline"));
```

- [ ] **Step 7: Build, then run the palette and theme tests.** Use the targeted run with `SettingsPaletteTests`, then `ThemeApplicationRegressionTests`, then `SettingsWindow` (which covers every Settings window test class).

Expected: all PASS.

- [ ] **Step 8: Commit.**

```bash
git add src/Ntilde.App/Settings/ src/Ntilde.App/SettingsWindow.axaml tests/Ntilde.App.Tests/Core/SettingsPaletteTests.cs tests/Ntilde.App.Tests/Core/ThemeApplicationRegressionTests.cs
git commit -m "refactor(settings): move the Nt palette and styles into shared NtPalette/NtStyles" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

## Task 1.4: The connection manager adopts `NtPalette`

**Files:**
- Modify: `src/Ntilde.App/Controls/ConnectionManager.axaml:1-39`
- Test: `tests/Ntilde.App.Tests/Core/SettingsPaletteTests.cs`

**Interfaces:** consumes `NtPalette` from Task 1.3. The connection manager keeps its own `UserControl.Styles` unchanged (see Correction 2).

- [ ] **Step 1: Write the failing tests.** Append them to `SettingsPaletteTests`, and add `using Ntilde.Controls;`.

```csharp
    [AvaloniaFact]
    public void SettingsWindowAndConnectionManager_DoNotShareBrushes()
    {
        using var root = SettingsTestRoot.Empty();
        var settings = new Ntilde.SettingsWindow();
        var manager = new ConnectionManager();

        Assert.NotSame(settings.FindResource("NtPanel"), manager.FindResource("NtPanel"));

        manager.ApplyTheme(LightTheme());

        Assert.Equal(LightPanel, ((SolidColorBrush)manager.FindResource("NtPanel")!).Color);
        Assert.NotEqual(LightPanel, ((SolidColorBrush)settings.FindResource("NtPanel")!).Color);
    }

    [AvaloniaFact]
    public void ConnectionManager_ControlsHoldItsPaletteBrushes_AndRecolourWithThem()
    {
        var manager = new ConnectionManager();

        Assert.Empty(manager.Resources); // the palette is merged, not copied in
        var windowBg = Assert.IsType<SolidColorBrush>(manager.FindResource("NtWindowBg"));
        var rootGrid = Assert.IsType<Grid>(manager.Content); // Background="{StaticResource NtWindowBg}"
        Assert.Same(windowBg, rootGrid.Background);

        manager.ApplyTheme(LightTheme());

        Assert.Equal(Color.FromRgb(245, 242, 233), windowBg.Color);
        Assert.Same(windowBg, rootGrid.Background);
    }
```

- [ ] **Step 2: Run them and watch them fail.** Use the targeted run with `SettingsPaletteTests`.

Expected: `ConnectionManager_ControlsHoldItsPaletteBrushes…` fails on `Assert.Empty(manager.Resources)`, because the 21 brushes are still own entries. The other test may already pass, since the two surfaces held separate instances before too. That is fine: it pins the property.

- [ ] **Step 3: Replace `ConnectionManager.axaml` lines 1-39.** That is the root element's opening tag through `</UserControl.Resources>`. The only changes are the new `xmlns:settings` and the merged dictionary in place of the brush list.

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             xmlns:sshvm="clr-namespace:Ntilde.ViewModels.Ssh"
             xmlns:local="clr-namespace:Ntilde.Controls"
             xmlns:settings="clr-namespace:Ntilde.Settings"
             x:Class="Ntilde.Controls.ConnectionManager"
             mc:Ignorable="d"
             d:DesignWidth="1080" d:DesignHeight="720"
             HorizontalAlignment="Stretch"
             VerticalAlignment="Stretch"
             Background="#16181d"
             Foreground="#e6e8ec"
             FontFamily="Inter, Segoe UI, system-ui, sans-serif">

    <!-- The palette is shared with Settings (Settings/NtPalette.axaml); this control merges its own
         instance so a theme switch recolours each surface independently. Its styles below stay
         local: they are a denser variant (12 px buttons, 30 px icon buttons), not copies. -->
    <UserControl.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <settings:NtPalette/>
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </UserControl.Resources>
```

- [ ] **Step 4: Run the palette tests and the connection manager's own tests.** Use the targeted run with `SettingsPaletteTests`, then `ConnectionManager`.

Expected: all PASS.

- [ ] **Step 5: Commit.**

```bash
git add src/Ntilde.App/Controls/ConnectionManager.axaml tests/Ntilde.App.Tests/Core/SettingsPaletteTests.cs
git commit -m "refactor(connections): merge the shared NtPalette instead of a copied brush list" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

## Task 1.5: Prove no visible change, run the gate, open PR 1

**Files:** none.

- [ ] **Step 1: Capture again and compare with the Task 1.1 baseline.**

```powershell
$env:NTILDE_SETTINGS_SHOTS = "$env:TEMP\settings-shots\pr1-after"; $env:NTILDE_SETTINGS_SHOTS_BASELINE = "$env:TEMP\settings-shots\pr1-before"; scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~SettingsScreenshotHarness&Lane!=PlatformBoot" *> "$env:TEMP\settings-shots.log"; Remove-Item Env:NTILDE_SETTINGS_SHOTS, Env:NTILDE_SETTINGS_SHOTS_BASELINE; Select-String -Path "$env:TEMP\settings-shots.log" -Pattern "Passed!|Failed!|differ"
```

Expected: `Passed!`, with no "pixels differ" lines.

**If any image differs:** open the before and after pair and find the setter that changed. Usual suspects: a `DynamicResource` that resolved to a different key, or a style that moved order. Fix it and capture again. Do not move the baseline.

- [ ] **Step 2: Run the full gate** (Conventions). Expected: all green.

- [ ] **Step 3: Stop and ask the user before pushing.** Then push, and open the PR once they agree:

```bash
git push -u origin feat/settings-redesign
gh pr create --base main --title "Settings redesign 1/5: shared palette and styles" --body-file - <<'EOF'
First of five PRs for the settings window redesign (spec and plan included in this PR under docs/superpowers/).

**What changes**
- `Settings/NtPalette.axaml`: the Nt* brushes in one place. The Settings window and the connection manager each merge their own instance.
- `Settings/NtStyles.axaml`: the Settings styles, now referencing the palette with `DynamicResource` so page controls (PR 2) can use them before they join the window.
- `ThemePaletteResources.Apply` now finds brushes in merged dictionaries. Before, it would have added a second brush and left every control on the old colour.
- The connection manager keeps its own (denser) styles; only the palette is shared.

**What does not change**
Nothing visible. Headless screenshots of every Settings tab (scrolled through) and of the connection manager, in a dark and a light theme, are pixel-identical before and after.

**Tests**
`SettingsPaletteTests`: merged-dictionary recolour, one brush set per surface, controls hold the palette instances, shipped colours kept. Two-lane App.Tests run and Architecture tests green locally.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
```

- [ ] **Step 4: Merge only on the user's word,** once CI is green: `gh pr merge <n> --merge --match-head-commit $(git rev-parse HEAD)`. Then update the memory file `settings-redesign-state.md` to say PR 1 merged, with its merge SHA.

---

# Part 2 — PR 2: The frame and the one-to-one page move

**Outcome:**
- `SettingsWindow` is a frame. Today's seven tabs are seven `ISettingsPage` controls, with the same markup and the same behaviour.
- Navigation runs on `SettingsPage` and `SettingsTarget`, and the sidebar is one list.
- New behaviour: Ctrl/Cmd+S saves, Save validates before writing, and the sidebar footer text is gone.
- The round-trip test written against the old window passes unchanged against the new one.

## Move rules (read with every page task in this part)

Page tasks copy code out of `ORIG` (`git show ee60cc2:src/Ntilde.App/SettingsWindow.axaml.cs`) and apply these rules. Copy comments verbatim. **If you hand a page task to a subagent, paste this section into its brief.**

| In the moved code | Becomes |
|---|---|
| `_settings` | `Session.Settings` |
| `_shortcutDraftBindings` (read) | `Session.ShortcutDraft` |
| `_shortcutDraftBindings = X;` | `Session.ReplaceShortcutDraft(X);` |
| `_titleBarDraft` | `Session.TitleBarDraft` |
| `ThemeResourceBrush("Nt…")` | `this.FindPaletteBrush("Nt…")` |
| `CommandAssistHistoryStore` / `CommandAssistSnippetStore` | `Session.HistoryStore` / `Session.SnippetStore` |
| `OnOpacityChanged?.Invoke(v)` | `Session.Preview.Opacity(v)` |
| `OnBlurChanged?.Invoke(v)` | `Session.Preview.Blur(v)` |
| `OnBgImageChanged?.Invoke(p, o, s)` | `Session.Preview.BackgroundImage(p, o, s)` |
| `OnFontChanged?.Invoke(f)` | `Session.Preview.Font(f)` |
| `OnFontSizeChanged?.Invoke(s)` | `Session.Preview.FontSize(s)` |
| `OnUiScaleChanged?.Invoke(s)` | `Session.Preview.UiScale(s)` |
| `OnThemeChanged?.Invoke(n)` | `Session.Preview.Theme(n)` |
| `ApplyTheme(theme);` / `ApplyTheme();` | `Session.Preview.RepaintWindow(theme);` / `Session.Preview.RepaintWindow(null);` |
| `OnCommandAssistHistoryCleared?.Invoke();` | `Session.RaiseHistoryCleared();` |
| `OnCommandAssistSnippetsChanged?.Invoke();` | `Session.RaiseSnippetsChanged();` |
| `RebuildTitleBarRows();` **inside Shortcuts code** | `Session.RaiseShortcutsChanged();` |
| `this.StorageProvider` | `TopLevel.GetTopLevel(this)?.StorageProvider` (return early when null) |
| `await dialog.ShowDialog(this);` | `if (TopLevel.GetTopLevel(this) is not Window owner) return <default>;` then `await dialog.ShowDialog(owner);` |
| XAML `{StaticResource Nt…}` | `{DynamicResource Nt…}` (the XAML script below does this) |

Every page follows the same skeleton:

```csharp
private SettingsSession? _session;
private SettingsSession Session => _session ?? throw new InvalidOperationException($"{GetType().Name} used before Load.");
public SettingsPage Page => SettingsPage.<Value>;
public Control View => this;
```

**Using block.** Copy `ORIG` lines 1-25 into each page file, then add `using Ntilde.Settings;`. Unused usings are only warnings, so it is safe to delete the ones the IDE greys out.

**Check after each page task.** `git grep` must print nothing. These are the window-only names that must not survive a move:

```bash
git grep -nE '_settings[.]|_shortcutDraftBindings|_titleBarDraft|ThemeResourceBrush|On(Opacity|Blur|BgImage|Font|FontSize|UiScale|Theme)Changed|OnCommandAssist|CommandAssist(History|Snippet)Store|StaticResource' -- src/Ntilde.App/Settings/Pages/
```

**Page XAML script.** Save it as `$TEMP/settings-page-xaml.sh` and `source` it once per shell. It copies a tab body out of `ORIG`, removes 16 spaces of indentation, switches `StaticResource` to `DynamicResource`, and wraps the result in a `UserControl`:

```bash
orig_xaml() {
  git show ee60cc2:src/Ntilde.App/SettingsWindow.axaml | sed -n "$1,$2p" \
    | sed -e 's/^                //' -e 's/{StaticResource /{DynamicResource /g'
}
write_page_xaml() {   # $1 = class name, $2..$3 = ORIG line range of the tab body
  mkdir -p src/Ntilde.App/Settings/Pages
  {
    printf '%s\n' '<UserControl xmlns="https://github.com/avaloniaui"'
    printf '%s\n' '             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"'
    printf '%s\n' '             xmlns:settings="clr-namespace:Ntilde.Settings"'
    printf '%s\n' "             x:Class=\"Ntilde.Settings.Pages.$1\">"
    orig_xaml "$2" "$3"
    printf '%s\n' '</UserControl>'
  } > "src/Ntilde.App/Settings/Pages/$1.axaml"
}
```

Tab body ranges in `ORIG` `SettingsWindow.axaml`. Each range is the tab's single child, from its opening tag to its closing tag:

| Page | Lines |
|---|---|
| Appearance | 375-708 |
| Profiles | 713-885 |
| Shortcuts | 889-914 |
| Command Assist | 919-1002 |
| Agent Access | 1007-1053 |
| SSH | 1058-1076 |
| Backup | 1081-1107 |

## Task 2.0: Branch for PR 2

**Files:** none.

- [ ] **Step 1: Cut the branch.** PR 1 must have merged first.

```bash
cd /d/projects/nova2/.worktrees/settings-redesign
git fetch origin
git switch -c feat/settings-redesign-frame origin/main
```

- [ ] **Step 2: Check that the old sources match `ORIG`.**

```bash
git diff --stat ee60cc2 HEAD -- src/Ntilde.App/SettingsWindow.axaml.cs
git diff ee60cc2 HEAD -- src/Ntilde.App/SettingsWindow.axaml | grep '^[-+]' | grep -v '^[-+][-+]' | head -5
```

Expected:
- No output for the `.cs` file.
- For the `.axaml`, only PR 1's resources and styles hunk (lines 1-271 of `ORIG`).

**If either shows anything else:** another PR changed Settings in the meantime. Port that change into the matching page as you move it, and say so in the PR body.

## Task 2.1: Round-trip tests, written against the old window

Both tests must **pass on the current code**. They record today's behaviour, and the restructure must keep them green without edits.

**Files:**
- Create: `tests/Ntilde.App.Tests/Core/SettingsWindowRoundTripTests.cs`

**Interfaces:** consumes `SettingsTestRoot` (Task 1.1). Uses only `new Ntilde.SettingsWindow()` and the `BtnSave` button, both of which survive the restructure.

- [ ] **Step 1: Write the tests.**

```csharp
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Ntilde.Shell;
using Ntilde.Shell.TitleBar;

namespace Ntilde.Tests.Core;

/// <summary>
/// Save without touching anything must leave settings.json as it was. This guards the settings
/// redesign's page split: a field dropped in the move, or a readable label written in place of a
/// stored value, shows up here as a diff.
/// </summary>
/// <remarks>
/// Written against the old single-window Settings (PR 2's first commit) and kept unchanged through
/// every later PR, so it compares the new window with the old one rather than with itself.
/// </remarks>
public sealed class SettingsWindowRoundTripTests
{
    private const string ProfileId = "22222222-2222-2222-2222-222222222222";

    /// <summary>Every TerminalSettings field the Settings window reads and writes.</summary>
    private static readonly string[] WindowOwnedFields =
    [
        "FontSize", "MaxHistory", "FontFamily", "ThemeName", "WindowOpacity", "BlurEffect", "UiScale",
        "TabStripOrientation", "EnableLigatures", "EnableComplexShaping", "AgentIndicatorTabRollup",
        "Keybindings", "TitleBarItems", "TitleBarOrder", "BackgroundImagePath", "BackgroundImageOpacity",
        "BackgroundImageStretch", "CommandAssistEnabled", "CommandAssistHistoryEnabled",
        "CommandAssistPassiveBubbleEnabled", "CommandAssistShellIntegrationEnabled",
        "ExperimentalNativeSshEnabled", "AgentAccessObserveEnabled", "AgentReplayExportEnabled",
        "AgentAccessActEnabled", "LongCommandNotificationsEnabled", "Osc9NotificationsEnabled",
        "Osc9ProgressReportingEnabled", "AutomaticUpdateChecks", "Profiles", "DefaultProfileId",
    ];

    private static readonly Dictionary<string, string> SeedTitleBarItems = new() { ["find"] = "Pinned", ["connections"] = "Hidden" };

    private static readonly List<string> SeedTitleBarOrder = ["new_tab", "find", "open_tab_list", "settings"];

    private static string NativeCommand => OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash";

    /// <summary>Every window-owned field away from its default, and every value one the window can show.</summary>
    private static string SeedJson() => $$"""
        {
          "FontSize": 17,
          "MaxHistory": 25000,
          "FontFamily": "RoundTrip Mono",
          "ThemeName": "GitHub Light",
          "WindowOpacity": 0.75,
          "BlurEffect": "Mica",
          "UiScale": 1.25,
          "TabStripOrientation": "Vertical",
          "EnableLigatures": true,
          "EnableComplexShaping": false,
          "AgentIndicatorTabRollup": "All",
          "Keybindings": { "settings": "Ctrl+Alt+Shift+F11" },
          "TitleBarItems": { "find": "Pinned", "connections": "Hidden" },
          "TitleBarOrder": [ "new_tab", "find", "open_tab_list", "settings" ],
          "BackgroundImagePath": "/roundtrip/background.png",
          "BackgroundImageOpacity": 0.35,
          "BackgroundImageStretch": "Uniform",
          "CommandAssistEnabled": true,
          "CommandAssistHistoryEnabled": false,
          "CommandAssistPassiveBubbleEnabled": false,
          "CommandAssistShellIntegrationEnabled": false,
          "ExperimentalNativeSshEnabled": false,
          "AgentAccessObserveEnabled": true,
          "AgentReplayExportEnabled": true,
          "AgentAccessActEnabled": true,
          "LongCommandNotificationsEnabled": true,
          "Osc9NotificationsEnabled": true,
          "Osc9ProgressReportingEnabled": false,
          "AutomaticUpdateChecks": false,
          "DefaultProfileId": "{{ProfileId}}",
          "Profiles": [ {
            "Id": "{{ProfileId}}", "Name": "Round trip", "Command": "{{NativeCommand}}", "Arguments": "-l",
            "StartingDirectory": "/work/roundtrip", "FontFamily": "Profile Mono", "FontSize": 13,
            "ThemeName": "Dracula", "EnableLigatures": true, "Group": "Work", "Notes": "keep me",
            "AccentColor": "#ff8800", "Tags": [ "a", "b" ]
          } ]
        }
        """;

    [AvaloniaFact]
    public void SaveWithoutTouchingAnything_LeavesEverySettingAsItWas()
    {
        using var root = SettingsTestRoot.WithSettings(SeedJson());
        root.CopyShippedTheme("GitHubLight.json");
        AssertTitleBarSeedIsCanonical();

        // What a plain load and save writes: the file the window must reproduce.
        TerminalSettings.Load().Save();
        using JsonDocument expected = root.ReadSettings();
        AssertEveryWindowOwnedFieldIsNonDefault(expected.RootElement);

        var window = new Ntilde.SettingsWindow();
        window.FindControl<Button>("BtnSave")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        using JsonDocument actual = root.ReadSettings();
        AssertJsonEquivalent(expected.RootElement, actual.RootElement, "$");
    }

    /// <summary>
    /// Pins today's behaviour for the profile fields the editor does not own.
    /// BuildLocalProfilesForEditor copies 13 fields, so any Settings Save resets everything else on
    /// a local profile, the SSH fields included. Pinned so that removing the never-visible SSH panel
    /// (PR 3) provably changes nothing. Keeping those fields is a separate fix that will update this
    /// test (plan, "Corrections to the spec", item 1).
    /// </summary>
    [AvaloniaFact]
    public void SaveWithoutTouchingAnything_KeepsTheEditorsProfileFields_AndResetsTheRestAsBefore()
    {
        string seed = $$"""
            {
              "DefaultProfileId": "{{ProfileId}}",
              "Profiles": [ {
                "Id": "{{ProfileId}}", "Name": "Legacy", "Command": "{{NativeCommand}}", "Group": "Work",
                "SshHost": "legacy.example", "SshPort": 2222, "SshUser": "ops", "UseSshAgent": false,
                "IdentityFilePath": "/keys/id", "CursorStyle": "Block"
              } ]
            }
            """;
        using var root = SettingsTestRoot.WithSettings(seed);

        var window = new Ntilde.SettingsWindow();
        window.FindControl<Button>("BtnSave")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        using JsonDocument saved = root.ReadSettings();
        JsonElement profile = saved.RootElement.GetProperty("Profiles").EnumerateArray()
            .Single(p => p.GetProperty("Id").GetString() == ProfileId);
        Assert.Equal("Legacy", profile.GetProperty("Name").GetString());
        Assert.Equal("Work", profile.GetProperty("Group").GetString());
        Assert.Equal("", profile.GetProperty("SshHost").GetString());
        Assert.Equal(22, profile.GetProperty("SshPort").GetInt32());
        Assert.Equal("", profile.GetProperty("SshUser").GetString());
        Assert.True(profile.GetProperty("UseSshAgent").GetBoolean());
        Assert.True(IsNullOrMissing(profile, "IdentityFilePath"));
        Assert.True(IsNullOrMissing(profile, "CursorStyle"));
    }

    private static bool IsNullOrMissing(JsonElement element, string name)
        => !element.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null;

    /// <summary>
    /// The window writes back the resolver's normalized order. The seed must already be in that
    /// form, or the test would flag the normalization instead of a regression.
    /// </summary>
    private static void AssertTitleBarSeedIsCanonical()
    {
        TitleBarLayout layout = TitleBarLayoutResolver.Resolve(SeedTitleBarItems, SeedTitleBarOrder, null);
        Assert.Equal(SeedTitleBarOrder, layout.Pinned.Select(entry => entry.Id));
    }

    /// <summary>A field left at its default could be dropped and the comparison would still pass.</summary>
    private static void AssertEveryWindowOwnedFieldIsNonDefault(JsonElement saved)
    {
        string defaultsJson = JsonSerializer.Serialize(new TerminalSettings(), AppJsonContext.Default.TerminalSettings);
        using JsonDocument defaults = JsonDocument.Parse(defaultsJson);
        foreach (string field in WindowOwnedFields)
        {
            Assert.True(
                defaults.RootElement.GetProperty(field).GetRawText() != saved.GetProperty(field).GetRawText(),
                $"The seed leaves {field} at its default.");
        }
    }

    private static void AssertJsonEquivalent(JsonElement expected, JsonElement actual, string path)
    {
        Assert.True(expected.ValueKind == actual.ValueKind, $"{path}: {expected.ValueKind} became {actual.ValueKind}");
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var expectedNames = expected.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
                var actualNames = actual.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
                Assert.True(expectedNames.SequenceEqual(actualNames), $"{path}: properties {string.Join(",", expectedNames)} became {string.Join(",", actualNames)}");
                foreach (string name in expectedNames)
                {
                    AssertJsonEquivalent(expected.GetProperty(name), actual.GetProperty(name), $"{path}.{name}");
                }

                break;
            case JsonValueKind.Array:
                Assert.True(expected.GetArrayLength() == actual.GetArrayLength(), $"{path}: length {expected.GetArrayLength()} became {actual.GetArrayLength()}");
                for (int i = 0; i < expected.GetArrayLength(); i++)
                {
                    AssertJsonEquivalent(expected[i], actual[i], $"{path}[{i}]");
                }

                break;
            case JsonValueKind.Number:
                // Sliders snap to their tick, so floating-point noise is allowed and nothing more.
                Assert.True(Math.Abs(expected.GetDouble() - actual.GetDouble()) < 1e-9, $"{path}: {expected.GetRawText()} became {actual.GetRawText()}");
                break;
            default:
                Assert.True(expected.GetRawText() == actual.GetRawText(), $"{path}: {expected.GetRawText()} became {actual.GetRawText()}");
                break;
        }
    }
}
```

- [ ] **Step 2: Run them against the old window.** Use the targeted run with `SettingsWindowRoundTripTests`.

Expected: both PASS.

**If the first test fails:** read the failing path. The seed probably uses a form the window normalizes, for example a title-bar order or a theme name the combo cannot show. Adjust the seed to the normalized form and note it in the test. Never adjust the comparison.

**If the second test fails because `SshLegacyProfileMigrationService` touched the profile:** read that service and adjust the seed so the profile stays `Local`.

- [ ] **Step 3: Commit.**

```bash
git add tests/Ntilde.App.Tests/Core/SettingsWindowRoundTripTests.cs
git commit -m "test(settings): round-trip Save on the current window before the page split" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

## Task 2.2: The page contract, the session and the preview sink

**Files:**
- Create: `src/Ntilde.App/Settings/SettingsPage.cs`, `ISettingsPage.cs`, `SettingsPreviewSink.cs`, `SettingsSession.cs`
- Create: `tests/Ntilde.App.Tests/Core/SettingsSessionTests.cs`

**Interfaces (all public, namespace `Ntilde.Settings`):**
- `enum SettingsPage { Appearance, Profiles, Shortcuts, CommandAssist, AgentAccess, Ssh, Backup }`. PR 3 adds `General`, `Terminal` and `TabsAndTitleBar`.
- `sealed record SettingsTarget(SettingsPage Page, string? RowId = null)`
- `interface ISettingsPage`, with members `Page`, `View`, `Load(SettingsSession)`, `Validate()` and `Collect(TerminalSettings)`.
- `sealed record SettingsValidationError(SettingsPage Page, string? RowId, string Message)`
- `SettingsPreviewSink`:
  - methods `Opacity(double)`, `Blur(string)`, `BackgroundImage(string, double, string)`, `Font(string)`, `FontSize(double)`, `UiScale(double)`, `Theme(string)`, `RepaintWindow(TerminalTheme?)`;
  - one matching event each: `OpacityChanged`, `BlurChanged`, `BackgroundImageChanged`, `FontChanged`, `FontSizeChanged`, `UiScaleChanged`, `ThemeChanged`, `RepaintRequested`.
- `SettingsSession(TerminalSettings, Func<IEnumerable<string>>? systemFontFamilies = null)`:
  - properties `Settings`, `ShortcutDraft`, `TitleBarDraft`, `FontChoices`, `Preview`, `HistoryStore`, `SnippetStore`;
  - events `ShortcutsChanged`, `ThemesChanged`, `HistoryCleared`, `SnippetsChanged`, each raised by its `Raise…()`;
  - `ReplaceShortcutDraft`, `Reload`, `CommitDrafts`, `ReloadAfterExternalChangeAsync`;
  - internal: `SetExternalReloadHandler`, `BuildFontFamilyChoices`, `CollectFontFamilyChoices`.

- [ ] **Step 1: Write the failing tests.**

```csharp
using Ntilde.Settings;
using Ntilde.Shell;
using Ntilde.Shell.TitleBar;

namespace Ntilde.Tests.Core;

public sealed class SettingsSessionTests
{
    private static readonly Func<IEnumerable<string>> NoSystemFonts = () => Array.Empty<string>();

    [Fact]
    public void Constructing_SeedsBothDraftsFromTheSettings_WithoutSharingThem()
    {
        var settings = new TerminalSettings
        {
            Keybindings = new() { ["settings"] = "Ctrl+Alt+Shift+F11" },
            TitleBarItems = new() { ["find"] = "Pinned" },
        };

        var session = new SettingsSession(settings, NoSystemFonts);

        Assert.Equal("Ctrl+Alt+Shift+F11", session.ShortcutDraft["SETTINGS"]); // case-insensitive
        Assert.NotSame(settings.Keybindings, session.ShortcutDraft);
        Assert.Equal(TitleBarItemState.Pinned, session.TitleBarDraft.GetState("find"));
    }

    [Fact]
    public void CommitDrafts_WritesKeybindingsAndTitleBarDeltas()
    {
        var session = new SettingsSession(new TerminalSettings(), NoSystemFonts);
        session.ShortcutDraft["settings"] = "Ctrl+Alt+Shift+F11";
        Assert.True(session.TitleBarDraft.TrySetState("find", TitleBarItemState.Pinned));
        var target = new TerminalSettings();

        session.CommitDrafts(target);

        Assert.Equal("Ctrl+Alt+Shift+F11", target.Keybindings["settings"]);
        Assert.Equal("Pinned", target.TitleBarItems["find"]);
        Assert.Contains("find", target.TitleBarOrder);
    }

    [Fact]
    public void Reload_SwapsTheSettings_AndDiscardsPendingDraftEdits()
    {
        var session = new SettingsSession(new TerminalSettings(), NoSystemFonts);
        session.ShortcutDraft["settings"] = "Ctrl+Alt+Shift+F11";
        var fresh = new TerminalSettings { FontSize = 21 };

        session.Reload(fresh);

        Assert.Same(fresh, session.Settings);
        Assert.False(session.ShortcutDraft.ContainsKey("settings"));
    }

    [Fact]
    public void FontChoices_IncludeTheGlobalFontAndEveryLocalProfileOverride_EvenWhenNotInstalled()
    {
        var settings = new TerminalSettings { FontFamily = "Global Mono" };
        settings.Profiles.Add(new TerminalProfile { Name = "a", FontFamily = "Profile One" });
        settings.Profiles.Add(new TerminalProfile { Name = "b", FontFamily = "Profile Two" });
        settings.Profiles.Add(new TerminalProfile { Name = "ssh", Type = ConnectionType.SSH, FontFamily = "Remote Only" });

        var session = new SettingsSession(settings, () => ["Installed Mono"]);

        Assert.Contains("Installed Mono", session.FontChoices);
        Assert.Contains("Global Mono", session.FontChoices);
        Assert.Contains("Profile One", session.FontChoices);
        Assert.Contains("Profile Two", session.FontChoices);
        Assert.DoesNotContain("Remote Only", session.FontChoices);
    }

    [Fact]
    public void RaiseMethods_ReachTheirSubscribers()
    {
        var session = new SettingsSession(new TerminalSettings(), NoSystemFonts);
        int shortcuts = 0, themes = 0, history = 0, snippets = 0;
        session.ShortcutsChanged += () => shortcuts++;
        session.ThemesChanged += () => themes++;
        session.HistoryCleared += () => history++;
        session.SnippetsChanged += () => snippets++;

        session.RaiseShortcutsChanged();
        session.RaiseThemesChanged();
        session.RaiseHistoryCleared();
        session.RaiseSnippetsChanged();

        Assert.Equal((1, 1, 1, 1), (shortcuts, themes, history, snippets));
    }

    [Fact]
    public async Task ReloadAfterExternalChangeAsync_RunsTheHandlerTheFrameRegistered()
    {
        var session = new SettingsSession(new TerminalSettings(), NoSystemFonts);
        int reloads = 0;
        session.SetExternalReloadHandler(() => { reloads++; return Task.CompletedTask; });

        await session.ReloadAfterExternalChangeAsync();

        Assert.Equal(1, reloads);
    }

    [Fact]
    public void PreviewSink_RaisesTheMatchingEvent()
    {
        var sink = new SettingsPreviewSink();
        string? theme = null;
        double? scale = null;
        sink.ThemeChanged += name => theme = name;
        sink.UiScaleChanged += value => scale = value;

        sink.Theme("Nord");
        sink.UiScale(1.5);

        Assert.Equal("Nord", theme);
        Assert.Equal(1.5, scale);
    }
}
```

- [ ] **Step 2: Run them and watch them fail.** Use the targeted run with `SettingsSessionTests`. Expected: compile errors, because the types are missing.

- [ ] **Step 3: Write the types.**

`src/Ntilde.App/Settings/SettingsPage.cs`:

```csharp
namespace Ntilde.Settings;

/// <summary>
/// One page of the Settings window. Declaration order means nothing: the sidebar order, groups,
/// titles and glyphs come from <see cref="SettingsPageRegistry"/>.
/// </summary>
public enum SettingsPage
{
    Appearance,
    Profiles,
    Shortcuts,
    CommandAssist,
    AgentAccess,
    Ssh,
    Backup,
}

/// <summary>
/// Where Settings opens: a page, and optionally the row on it to scroll to and highlight (see
/// <see cref="SettingsNavigation.RowIdProperty"/>). The window's only "where to open" input.
/// </summary>
public sealed record SettingsTarget(SettingsPage Page, string? RowId = null);
```

`src/Ntilde.App/Settings/ISettingsPage.cs`:

```csharp
using Avalonia.Controls;
using Ntilde.Shell;

namespace Ntilde.Settings;

/// <summary>
/// The contract between the Settings frame and one page. A page is created once per window,
/// stays in it for the window's whole life, and is loaded again in place after a backup import or
/// restore replaced configuration on disk.
/// </summary>
public interface ISettingsPage
{
    SettingsPage Page { get; }

    /// <summary>The page's root control, hosted by the frame.</summary>
    Control View { get; }

    /// <summary>Fills the controls from <paramref name="session"/>. Runs at open and after an external reload.</summary>
    void Load(SettingsSession session);

    /// <summary>
    /// Checks the page before Save writes anything. Null means valid. A page that refuses shows its
    /// own message inline; the frame then navigates to the error's page and row.
    /// </summary>
    SettingsValidationError? Validate();

    /// <summary>Writes the page's controls into <paramref name="target"/>. Runs only on Save, after every page validated.</summary>
    void Collect(TerminalSettings target);
}

/// <summary>Why Save was refused, and where to take the user to fix it.</summary>
public sealed record SettingsValidationError(SettingsPage Page, string? RowId, string Message);
```

`src/Ntilde.App/Settings/SettingsPreviewSink.cs`:

```csharp
using System;
using Ntilde.Shell;

namespace Ntilde.Settings;

/// <summary>
/// Where pages send live previews. The Settings window re-raises them as its own public events
/// (OnOpacityChanged and the rest), which MainWindow already subscribes to, and repaints itself
/// for <see cref="RepaintWindow"/>.
/// </summary>
public sealed class SettingsPreviewSink
{
    public event Action<double>? OpacityChanged;

    public event Action<string>? BlurChanged;

    public event Action<string, double, string>? BackgroundImageChanged;

    public event Action<string>? FontChanged;

    public event Action<double>? FontSizeChanged;

    public event Action<double>? UiScaleChanged;

    public event Action<string>? ThemeChanged;

    public event Action<TerminalTheme?>? RepaintRequested;

    public void Opacity(double value) => OpacityChanged?.Invoke(value);

    public void Blur(string effect) => BlurChanged?.Invoke(effect);

    public void BackgroundImage(string path, double opacity, string stretch) => BackgroundImageChanged?.Invoke(path, opacity, stretch);

    public void Font(string family) => FontChanged?.Invoke(family);

    public void FontSize(double size) => FontSizeChanged?.Invoke(size);

    /// <summary>The value is already clamped.</summary>
    public void UiScale(double scale) => UiScaleChanged?.Invoke(scale);

    public void Theme(string name) => ThemeChanged?.Invoke(name);

    /// <summary>Repaints the Settings window's own palette with <paramref name="theme"/>, or the active theme when null.</summary>
    public void RepaintWindow(TerminalTheme? theme) => RepaintRequested?.Invoke(theme);
}
```

`src/Ntilde.App/Settings/SettingsSession.cs`. The body of `BuildFontFamilyChoices` is `ORIG` 1676-1706, moved verbatim, together with its `<param>` doc at 1668-1675.

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ntilde.CommandAssist.Domain;
using Ntilde.Platform;
using Ntilde.Shell;
using Ntilde.Shell.TitleBar;
using Ntilde.VT;

namespace Ntilde.Settings;

/// <summary>
/// What every page of one open Settings window shares: the working settings, the drafts Save
/// commits, the live stores MainWindow injects, and the preview sink. One per window.
/// </summary>
public sealed class SettingsSession
{
    private readonly Func<IEnumerable<string>> _systemFontFamilies;
    private Func<Task>? _externalReload;

    public SettingsSession(TerminalSettings settings, Func<IEnumerable<string>>? systemFontFamilies = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _systemFontFamilies = systemFontFamilies ?? (() => SkiaSharp.SKFontManager.Default.FontFamilies);
        Settings = settings;
        ResetFromSettings();
    }

    /// <summary>The working settings. Pages change it only in <see cref="ISettingsPage.Collect"/>; Save writes it.</summary>
    public TerminalSettings Settings { get; private set; }

    /// <summary>Pending keybindings, committed on Save. Case-insensitive, like <see cref="TerminalSettings.Keybindings"/>.</summary>
    public Dictionary<string, string> ShortcutDraft { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pending title-bar placement, committed on Save as deltas.</summary>
    public TitleBarDraftState TitleBarDraft { get; } = new();

    /// <summary>
    /// Font families to offer: installed, bundled, the global font and every local profile's
    /// override, even when not installed here, so a configured font is always selectable.
    /// </summary>
    public IReadOnlyList<string> FontChoices { get; private set; } = Array.Empty<string>();

    public SettingsPreviewSink Preview { get; } = new();

    /// <summary>
    /// The live history store MainWindow injects, so "Clear history" acts on the instance panes
    /// append to. Null for a window opened elsewhere (tests, design time).
    /// </summary>
    public IHistoryStore? HistoryStore { get; set; }

    /// <summary>The live snippet store MainWindow injects. Null for a window opened elsewhere.</summary>
    public ISnippetStore? SnippetStore { get; set; }

    /// <summary>Raised after a shortcut draft edit or reset; pages that print chords refresh.</summary>
    public event Action? ShortcutsChanged;

    /// <summary>Raised after a theme was saved, deleted or imported; pages that list themes refresh.</summary>
    public event Action? ThemesChanged;

    /// <summary>Raised after command history was actually cleared.</summary>
    public event Action? HistoryCleared;

    /// <summary>Raised after a snippet was added, edited or deleted, or the snippet file was replaced.</summary>
    public event Action? SnippetsChanged;

    public void ReplaceShortcutDraft(Dictionary<string, string> draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ShortcutDraft = draft;
    }

    public void RaiseShortcutsChanged() => ShortcutsChanged?.Invoke();

    public void RaiseThemesChanged() => ThemesChanged?.Invoke();

    public void RaiseHistoryCleared() => HistoryCleared?.Invoke();

    public void RaiseSnippetsChanged() => SnippetsChanged?.Invoke();

    /// <summary>The Backup page calls this after a successful import or restore; the frame reloads every page.</summary>
    public Task ReloadAfterExternalChangeAsync() => _externalReload?.Invoke() ?? Task.CompletedTask;

    internal void SetExternalReloadHandler(Func<Task> handler) => _externalReload = handler;

    /// <summary>Swaps in freshly loaded settings and rebuilds both drafts and the font choices, discarding pending draft edits.</summary>
    public void Reload(TerminalSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings = settings;
        ResetFromSettings();
    }

    /// <summary>
    /// Writes the drafts into <paramref name="target"/>. Title-bar values are deltas only: an id at
    /// its catalog default is omitted, so a future catalog change reaches existing users.
    /// </summary>
    public void CommitDrafts(TerminalSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Keybindings = new Dictionary<string, string>(ShortcutDraft, StringComparer.OrdinalIgnoreCase);
        target.TitleBarItems = TitleBarDraft.BuildSaveDelta();
        target.TitleBarOrder = TitleBarDraft.BuildSaveOrder();
    }

    private void ResetFromSettings()
    {
        ShortcutDraft = new Dictionary<string, string>(Settings.Keybindings, StringComparer.OrdinalIgnoreCase);

        // Seeded from the resolver's output: it alone normalizes hand-edited ids ("Find" vs
        // "find"), and a second reader here would drift from it.
        TitleBarDraft.SeedFrom(TitleBarLayoutResolver.Resolve(Settings.TitleBarItems, Settings.TitleBarOrder, null));

        FontChoices = CollectFontFamilyChoices(
            _systemFontFamilies(),
            Settings.FontFamily,
            Settings.Profiles.Where(profile => profile.Type == ConnectionType.Local).Select(profile => profile.FontFamily));
    }

    // ORIG 1668-1706 goes here verbatim: the <param> doc and BuildFontFamilyChoices itself.

    /// <summary>
    /// <see cref="BuildFontFamilyChoices"/>, guaranteeing every name in
    /// <paramref name="additionalFontFamilies"/> as well (blank names are ignored).
    /// </summary>
    internal static List<string> CollectFontFamilyChoices(
        IEnumerable<string> systemFonts,
        string? configuredFontFamily,
        IEnumerable<string?> additionalFontFamilies)
    {
        var names = new SortedSet<string>(BuildFontFamilyChoices(systemFonts, configuredFontFamily), StringComparer.OrdinalIgnoreCase);
        foreach (string? family in additionalFontFamilies)
        {
            if (!string.IsNullOrWhiteSpace(family))
            {
                names.Add(family);
            }
        }

        return names.ToList();
    }
}
```

- [ ] **Step 4: Run the tests.** Use the targeted run with `SettingsSessionTests`. Expected: PASS.

- [ ] **Step 5: Commit.**

```bash
git add src/Ntilde.App/Settings/SettingsPage.cs src/Ntilde.App/Settings/ISettingsPage.cs src/Ntilde.App/Settings/SettingsPreviewSink.cs src/Ntilde.App/Settings/SettingsSession.cs tests/Ntilde.App.Tests/Core/SettingsSessionTests.cs
git commit -m "feat(settings): page contract, shared session and preview sink" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

## Task 2.3: Row ids, the row flash and palette lookup for rows built in code

**Files:**
- Create: `src/Ntilde.App/Settings/SettingsNavigation.cs`, `src/Ntilde.App/Settings/SettingsResourceExtensions.cs`
- Create: `tests/Ntilde.App.Tests/Core/SettingsNavigationTests.cs`

**Interfaces:**
- `public static class SettingsNavigation`:
  - `RowIdProperty` (attached, `string?`), with `GetRowId(Control)` and `SetRowId(Control, string?)`;
  - `FlashDuration` (1.5 s);
  - `void Flash(Control)`.
- `internal static IBrush? FindPaletteBrush(this Control, string key)`
- XAML usage: `settings:SettingsNavigation.RowId="title-bar"`.

- [ ] **Step 1: Write the failing tests.**

```csharp
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Ntilde.Settings;

namespace Ntilde.Tests.Core;

public sealed class SettingsNavigationTests
{
    [AvaloniaFact]
    public void RowId_IsAnAttachedPropertyAnyControlCanCarry()
    {
        var block = new TextBlock();

        SettingsNavigation.SetRowId(block, "title-bar");

        Assert.Equal("title-bar", SettingsNavigation.GetRowId(block));
    }

    [AvaloniaFact]
    public void Flash_HighlightsWithThePaletteBrush_WithoutReplacingTheControlsOwnBackground()
    {
        var dim = new SolidColorBrush(Colors.Blue);
        var row = new Border { Background = Brushes.Red };
        var host = new Window { Content = row };
        host.Resources["NtBlueDim"] = dim;
        try
        {
            host.Show();

            SettingsNavigation.Flash(row);

            Assert.Same(dim, row.Background);
            Assert.Same(Brushes.Red, row.GetBaseValue(Border.BackgroundProperty).Value);
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public void Flash_OnAControlWithoutABackground_DoesNothing()
    {
        var image = new Image();
        var host = new Window { Content = image };
        host.Resources["NtBlueDim"] = new SolidColorBrush(Colors.Blue);

        Assert.Null(Record.Exception(() => SettingsNavigation.Flash(image)));
    }

    [AvaloniaFact]
    public void FindPaletteBrush_ReturnsTheInstanceTheWindowHolds()
    {
        var brush = new SolidColorBrush(Colors.Green);
        var child = new Border();
        var host = new Window { Content = child };
        host.Resources["NtPanel"] = brush;

        Assert.Same(brush, child.FindPaletteBrush("NtPanel"));
    }
}
```

- [ ] **Step 2: Run them and watch them fail.** Use the targeted run with `SettingsNavigationTests`. Expected: compile errors.

- [ ] **Step 3: Write the two files.**

`src/Ntilde.App/Settings/SettingsNavigation.cs`:

```csharp
using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Threading;

namespace Ntilde.Settings;

/// <summary>
/// Row ids for deep links and search, and the short highlight a jump ends with.
/// </summary>
/// <remarks>
/// An attached property rather than a property of one row type, so any control can be a jump
/// target: the PR 2 move keeps today's markup, and SettingRow (PR 3) exposes this same property as
/// its RowId.
/// </remarks>
public static class SettingsNavigation
{
    public static readonly AttachedProperty<string?> RowIdProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("RowId", typeof(SettingsNavigation));

    /// <summary>How long a jumped-to row stays highlighted, fade included.</summary>
    public static readonly TimeSpan FlashDuration = TimeSpan.FromMilliseconds(1500);

    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(400);

    public static string? GetRowId(Control control) => control.GetValue(RowIdProperty);

    public static void SetRowId(Control control, string? value) => control.SetValue(RowIdProperty, value);

    /// <summary>
    /// Highlights <paramref name="target"/> with the palette's NtBlueDim, then fades back. The
    /// highlight is held at animation priority, so it neither replaces nor loses the control's own
    /// local or styled background. No-op for a control without a background.
    /// </summary>
    public static void Flash(Control target)
    {
        ArgumentNullException.ThrowIfNull(target);
        StyledProperty<IBrush?>? background = BackgroundPropertyOf(target);
        if (background is null || target.FindResource("NtBlueDim") is not IBrush highlight)
        {
            return;
        }

        Transitions? original = target.Transitions;
        IDisposable? held = target.SetValue(background, highlight, BindingPriority.Animation);
        DispatcherTimer.RunOnce(
            () =>
            {
                target.Transitions = new Transitions { new BrushTransition { Property = background, Duration = FadeDuration } };
                held?.Dispose();
                DispatcherTimer.RunOnce(() => target.Transitions = original, FadeDuration);
            },
            FlashDuration - FadeDuration);
    }

    internal static StyledProperty<IBrush?>? BackgroundPropertyOf(Control control) => control switch
    {
        TemplatedControl => TemplatedControl.BackgroundProperty,
        Panel => Panel.BackgroundProperty,
        Border => Border.BackgroundProperty,
        TextBlock => TextBlock.BackgroundProperty,
        _ => null,
    };
}
```

`src/Ntilde.App/Settings/SettingsResourceExtensions.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Media;

namespace Ntilde.Settings;

internal static class SettingsResourceExtensions
{
    /// <summary>
    /// One of the window's Nt* palette brushes. Rows built in code must hold the brush INSTANCE so
    /// ThemePaletteResources' in-place recolours reach them; a copied colour freezes on the first
    /// theme, which is what left the title-bar cards dark navy under light themes.
    /// </summary>
    internal static IBrush? FindPaletteBrush(this Control control, string key) => control.FindResource(key) as IBrush;
}
```

- [ ] **Step 4: Run the tests.** Use the targeted run with `SettingsNavigationTests`. Expected: PASS.

**If `SetValue(..., BindingPriority.Animation)` does not compile** because this Avalonia version returns `void`: use `target.Bind(background, Observable.Return<IBrush?>(highlight), BindingPriority.Animation)`, which returns `IDisposable`. Keep the `held?.Dispose()` shape.

- [ ] **Step 5: Commit.**

```bash
git add src/Ntilde.App/Settings/SettingsNavigation.cs src/Ntilde.App/Settings/SettingsResourceExtensions.cs tests/Ntilde.App.Tests/Core/SettingsNavigationTests.cs
git commit -m "feat(settings): row ids, row highlight and palette lookup for code-built rows" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

## Task 2.4: The Agent Access and SSH pages

**Files:**
- Create: `src/Ntilde.App/Settings/Pages/AgentAccessPage.axaml(.cs)`, `src/Ntilde.App/Settings/Pages/SshPage.axaml(.cs)`
- Create: `tests/Ntilde.App.Tests/Core/SettingsPageTests.cs`

**Interfaces:**
- `public partial class AgentAccessPage : UserControl, ISettingsPage` and `public partial class SshPage : UserControl, ISettingsPage`.
- Both have a parameterless constructor.
- Neither validates, so `Validate()` returns `null`.

- [ ] **Step 1: Write the failing tests.**

```csharp
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Ntilde.Settings;
using Ntilde.Settings.Pages;
using Ntilde.Shell;

namespace Ntilde.Tests.Core;

/// <summary>
/// Each page on its own: Load shows the session's values and Collect writes them back. The
/// window-level round trip (SettingsWindowRoundTripTests) checks the same thing for all pages at
/// once; these tests point at the page that broke.
/// </summary>
public sealed class SettingsPageTests
{
    internal static readonly Func<IEnumerable<string>> NoSystemFonts = () => Array.Empty<string>();

    internal static SettingsSession SessionFor(TerminalSettings settings) => new(settings, NoSystemFonts);

    [AvaloniaFact]
    public void AgentAccessPage_LoadsAndCollectsItsFourSettings()
    {
        var page = new AgentAccessPage();
        page.Load(SessionFor(new TerminalSettings
        {
            AgentAccessObserveEnabled = true,
            AgentReplayExportEnabled = true,
            AgentAccessActEnabled = true,
            AgentIndicatorTabRollup = "All",
        }));
        var target = new TerminalSettings();

        page.Collect(target);

        Assert.True(target.AgentAccessObserveEnabled);
        Assert.True(target.AgentReplayExportEnabled);
        Assert.True(target.AgentAccessActEnabled);
        Assert.Equal("All", target.AgentIndicatorTabRollup);
        Assert.Null(page.Validate());
    }

    [AvaloniaFact]
    public void AgentAccessPage_UnknownRollup_FallsBackToTheFirstItem_AsBefore()
    {
        var page = new AgentAccessPage();
        page.Load(SessionFor(new TerminalSettings { AgentIndicatorTabRollup = "Sometimes" }));
        var target = new TerminalSettings();

        page.Collect(target);

        Assert.Equal("WritesOnly", target.AgentIndicatorTabRollup);
    }

    [AvaloniaFact]
    public void SshPage_LoadsAndCollectsTheNativeBackendToggle()
    {
        var page = new SshPage();
        page.Load(SessionFor(new TerminalSettings { ExperimentalNativeSshEnabled = false }));
        var target = new TerminalSettings { ExperimentalNativeSshEnabled = true };

        page.Collect(target);

        Assert.False(target.ExperimentalNativeSshEnabled);
    }
}
```

- [ ] **Step 2: Run them and watch them fail.** Use the targeted run with `SettingsPageTests`. Expected: compile errors.

- [ ] **Step 3: Generate the XAML.** Use the Move rules script:

```bash
source "$TEMP/settings-page-xaml.sh"
write_page_xaml AgentAccessPage 1007 1053
write_page_xaml SshPage 1058 1076
```

- [ ] **Step 4: Write the code-behind.**

`AgentAccessPage.axaml.cs`:

```csharp
using System;
using System.Linq;
using Avalonia.Controls;
using Ntilde.Shell;

namespace Ntilde.Settings.Pages;

/// <summary>Agent Access: what AI agents on this machine may read or do.</summary>
public partial class AgentAccessPage : UserControl, ISettingsPage
{
    public AgentAccessPage()
    {
        InitializeComponent();
    }

    public SettingsPage Page => SettingsPage.AgentAccess;

    public Control View => this;

    public void Load(SettingsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        TerminalSettings settings = session.Settings;
        Toggle("AgentAccessObserveToggle").IsChecked = settings.AgentAccessObserveEnabled;
        Toggle("AgentReplayExportToggle").IsChecked = settings.AgentReplayExportEnabled;
        Toggle("AgentAccessActToggle").IsChecked = settings.AgentAccessActEnabled;

        var rollup = this.FindControl<ComboBox>("AgentIndicatorTabRollupList")!;
        rollup.SelectedItem = rollup.Items
            .Cast<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Content?.ToString(), settings.AgentIndicatorTabRollup, StringComparison.Ordinal));
        if (rollup.SelectedItem == null)
        {
            rollup.SelectedIndex = 0;
        }
    }

    public SettingsValidationError? Validate() => null;

    public void Collect(TerminalSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.AgentAccessObserveEnabled = Toggle("AgentAccessObserveToggle").IsChecked == true;
        target.AgentReplayExportEnabled = Toggle("AgentReplayExportToggle").IsChecked == true;
        target.AgentAccessActEnabled = Toggle("AgentAccessActToggle").IsChecked == true;
        if (this.FindControl<ComboBox>("AgentIndicatorTabRollupList")!.SelectedItem is ComboBoxItem rollupItem)
        {
            target.AgentIndicatorTabRollup = rollupItem.Content?.ToString() ?? "WritesOnly";
        }
    }

    private CheckBox Toggle(string name) => this.FindControl<CheckBox>(name)!;
}
```

`SshPage.axaml.cs`:

```csharp
using System;
using Avalonia.Controls;
using Ntilde.Shell;

namespace Ntilde.Settings.Pages;

/// <summary>SSH: global SSH behaviour. Per-connection options live in the connection manager.</summary>
public partial class SshPage : UserControl, ISettingsPage
{
    public SshPage()
    {
        InitializeComponent();
    }

    public SettingsPage Page => SettingsPage.Ssh;

    public Control View => this;

    public void Load(SettingsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        this.FindControl<CheckBox>("NativeSshToggle")!.IsChecked = session.Settings.ExperimentalNativeSshEnabled;
    }

    public SettingsValidationError? Validate() => null;

    public void Collect(TerminalSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.ExperimentalNativeSshEnabled = this.FindControl<CheckBox>("NativeSshToggle")!.IsChecked == true;
    }
}
```

- [ ] **Step 5: Run the tests.** Use the targeted run with `SettingsPageTests`. Expected: PASS.

- [ ] **Step 6: Commit.**

```bash
git add src/Ntilde.App/Settings/Pages/ tests/Ntilde.App.Tests/Core/SettingsPageTests.cs
git commit -m "feat(settings): Agent Access and SSH pages" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

## Task 2.5: The Backup page

**Files:**
- Create: `src/Ntilde.App/Settings/Pages/BackupPage.axaml(.cs)`
- Test: `tests/Ntilde.App.Tests/Core/SettingsPageTests.cs`

**Interfaces:**
- `public partial class BackupPage`, with these internal members:
  - `sealed record SnapshotRow(string Id, string Display, SnapshotReason Reason, DateTimeOffset CreatedUtc)`;
  - property `Func<SnapshotRow, Task<bool>>? RestoreConfirmationOverride { get; set; }`;
  - `static (string Headline, string Body) BuildRestoreConfirmationText(SnapshotRow)`;
  - `static string BuildImportModeBodyText(BundleInspection)`.
- After a successful import or restore it calls `Session.ReloadAfterExternalChangeAsync()`.

- [ ] **Step 1: Write the failing tests.** Append them to `SettingsPageTests`, and add `using Ntilde.Backup;` and `using Ntilde.Tests.Backup;`.

```csharp
    [AvaloniaFact]
    public void BackupPage_Load_ListsTheSnapshotsOnDisk()
    {
        using var root = SettingsTestRoot.Empty();
        Assert.NotNull(new BackupService(root.Root).Snapshot(SnapshotReason.Auto));
        var page = new BackupPage();

        page.Load(SessionFor(new TerminalSettings()));

        var rows = page.FindControl<ListBox>("SnapshotList")!.ItemsSource!.Cast<BackupPage.SnapshotRow>().ToArray();
        Assert.Single(rows);
        Assert.Contains("automatic", rows[0].Display);
    }

    [AvaloniaFact]
    public void BackupPage_StatesPasswordsAreNotIncluded_AndMustBeReEntered()
    {
        using var root = SettingsTestRoot.Empty();
        var page = new BackupPage();

        Assert.Contains(
            page.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text),
            text => text is not null
                && text.Contains("never included", StringComparison.OrdinalIgnoreCase)
                && text.Contains("re-enter", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BackupPage_RestoreConfirmationText_NamesTheSnapshotAndExplainsTheUndo()
    {
        var created = new DateTimeOffset(2026, 10, 13, 9, 30, 0, TimeSpan.Zero);
        var row = new BackupPage.SnapshotRow("id", "display", SnapshotReason.Auto, created);

        var (headline, body) = BackupPage.BuildRestoreConfirmationText(row);

        Assert.Contains(created.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), headline);
        Assert.Contains("automatic", headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("taken first", body, StringComparison.OrdinalIgnoreCase);
    }
```

- [ ] **Step 2: Run them and watch them fail.** Use the targeted run with `SettingsPageTests`. Expected: compile errors.

- [ ] **Step 3: Generate the XAML.** `write_page_xaml BackupPage 1081 1107`

- [ ] **Step 4: Write `BackupPage.axaml.cs`.** The class has the skeleton shown below. Moved members, from `ORIG`, with the Move rules applied:
  - `RestoreConfirmationOverride` (1030-1041) becomes an **auto-property** (`{ get; set; }`). Its doc comment moves too, with "this window" changed to "this page".
  - `WireBackupSection` (1047-1216) splits up:
    - The local functions `SetStatus` (1057-1064) and `RefreshSnapshots` (1066-1091) become private methods.
    - The `service` local becomes the `_service` field.
    - The `ItemTemplate` setup (1093-1100) and the three click handlers (1104-1215) go into the constructor.
    - The initial `RefreshSnapshots();` call (1102) moves to `Load`.
    - In the Import and Restore handlers, `await ReloadSettingsAfterExternalChangeAsync();` becomes `await Session.ReloadAfterExternalChangeAsync();`.
  - `ReasonLabel` (1423-1429) and `SnapshotRow` (1431) move as they are.
  - `BuildRestoreConfirmationText` (1433-1456) and `BuildImportModeBodyText` (1517-1548) move with `private` changed to `internal`.
  - `ConfirmRestoreAsync` (1458-1515) and `PromptForImportModeAsync` (1550-1614) move with the ShowDialog owner rule: return `false` and `null` respectively when there is no owner window.

```csharp
public partial class BackupPage : UserControl, ISettingsPage
{
    private readonly BackupService _service = new(AppPaths.RootDirectory, log: AppLogger.Log);
    private SettingsSession? _session;

    public BackupPage()
    {
        InitializeComponent();
        // ORIG 1051-1055 (control lookups), the ItemTemplate block (1093-1100) and the three
        // click handlers (1104-1215), with the edits listed above.
    }

    public SettingsPage Page => SettingsPage.Backup;

    public Control View => this;

    private SettingsSession Session => _session ?? throw new InvalidOperationException("BackupPage used before Load.");

    public void Load(SettingsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;

        // Here rather than in the constructor: SetStatus colours the message with the palette, and
        // the page has the window's resources only once it has joined the window.
        RefreshSnapshots();
    }

    public SettingsValidationError? Validate() => null;

    public void Collect(TerminalSettings target)
    {
        // Nothing to collect: export, import and restore act immediately.
    }

    // SetStatus, RefreshSnapshots, RestoreConfirmationOverride, ReasonLabel, SnapshotRow,
    // BuildRestoreConfirmationText, ConfirmRestoreAsync, BuildImportModeBodyText and
    // PromptForImportModeAsync, as listed above.
}
```

- [ ] **Step 5: Run the tests and the Move-rules `git grep`.** Use the targeted run with `SettingsPageTests`. Expected: PASS, and no grep output.

- [ ] **Step 6: Commit.**

```bash
git add src/Ntilde.App/Settings/Pages/ tests/Ntilde.App.Tests/Core/SettingsPageTests.cs
git commit -m "feat(settings): Backup & Restore page" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

## Task 2.6: The Shortcuts page

**Files:**
- Create: `src/Ntilde.App/Settings/Pages/ShortcutsPage.axaml(.cs)`
- Test: `tests/Ntilde.App.Tests/Core/SettingsPageTests.cs`

**Interfaces:**
- `public partial class ShortcutsPage`, with internal members:
  - `const string CaptureClass = "ShortcutCapture"`, a class on every binding editor `TextBox`. The frame's Ctrl/Cmd+S leaves those editors alone.
  - `static IReadOnlyList<ShortcutCatalogEntry> FilterShortcutCatalogEntries(string)`, moved.
- `Validate()` returns `SettingsValidationError(SettingsPage.Shortcuts, null, "Resolve duplicate shortcuts before saving.")` and shows that message inline when the draft has a conflict.
- Every draft edit or reset calls `Session.RaiseShortcutsChanged()`.

- [ ] **Step 1: Write the failing tests.** Append them to `SettingsPageTests`, and add `using Avalonia.Interactivity;` and `using Ntilde.Shell.Shortcuts;`. The two `internal static` helpers are reused by later test classes.

```csharp
    [AvaloniaFact]
    public void ShortcutsPage_WithADuplicateBinding_RefusesToValidate_AndSaysWhy()
    {
        var session = SessionFor(new TerminalSettings());
        var app = ShortcutCatalog.GetEntries()
            .Where(e => e.Scope == ShortcutScope.App && !string.IsNullOrEmpty(e.DefaultBinding))
            .ToList();
        session.ShortcutDraft[app[1].CommandId] = app[0].DefaultBinding;
        Assert.False(ShortcutBindingResolver.Resolve(ShortcutCatalog.GetDefinitions(), session.ShortcutDraft).IsValid);
        var page = new ShortcutsPage();
        page.Load(session);

        SettingsValidationError? error = page.Validate();

        Assert.NotNull(error);
        Assert.Equal(SettingsPage.Shortcuts, error!.Page);
        var message = page.FindControl<TextBlock>("ShortcutValidationMessage")!;
        Assert.True(message.IsVisible);
        Assert.Equal("Resolve duplicate shortcuts before saving.", message.Text);
    }

    [AvaloniaFact]
    public void ShortcutsPage_RecordingAChord_UpdatesTheDraft_AndTellsOtherPages()
    {
        var session = SessionFor(new TerminalSettings());
        var page = new ShortcutsPage();
        page.Load(session);
        int changes = 0;
        session.ShortcutsChanged += () => changes++;

        TextBox editor = BindingEditorFor(page, "Settings");
        PressChord(editor, Key.F9, KeyModifiers.Alt);

        string expected = ShortcutMatcher.Format(Key.F9, KeyModifiers.Alt);
        Assert.Equal(expected, editor.Text);
        Assert.Equal(expected, session.ShortcutDraft["settings"]);
        Assert.Equal(1, changes);
        Assert.Contains(ShortcutsPage.CaptureClass, editor.Classes);
    }

    [AvaloniaFact]
    public void ShortcutsPage_Reset_RemovesTheOverride_AndTellsOtherPages()
    {
        var session = SessionFor(new TerminalSettings { Keybindings = new() { ["settings"] = "Alt+F9" } });
        var page = new ShortcutsPage();
        page.Load(session);
        int changes = 0;
        session.ShortcutsChanged += () => changes++;

        Button reset = BindingEditorFor(page, "Settings").GetLogicalParent()!
            .GetLogicalChildren().OfType<Button>().Single(b => (string?)b.Content == "Reset");
        reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.False(session.ShortcutDraft.ContainsKey("settings"));
        Assert.Equal(1, changes);
    }

    /// <summary>The binding editor on the Shortcuts page row titled <paramref name="title"/>.</summary>
    internal static TextBox BindingEditorFor(ShortcutsPage page, string title)
    {
        var panel = page.FindControl<StackPanel>("ShortcutBindingsPanel")!;
        Border row = panel.Children.OfType<Border>().Single(b => b.GetLogicalDescendants()
            .OfType<TextBlock>().Any(t => t.Classes.Contains("RowLabel") && t.Text == title));
        return row.GetLogicalDescendants().OfType<TextBox>().Single();
    }

    internal static void PressChord(Control target, Key key, KeyModifiers modifiers)
        => target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = target });
```

- [ ] **Step 2: Run them and watch them fail.** Use the targeted run with `SettingsPageTests`. Expected: compile errors.

- [ ] **Step 3: Generate the XAML.** `write_page_xaml ShortcutsPage 889 914`

- [ ] **Step 4: Write `ShortcutsPage.axaml.cs`.** Moved members, from `ORIG`, with the Move rules applied:
  - `FilterShortcutCatalogEntries` (2003-2021) moves as it is; it is already `internal static`.
  - From 2320-2559: `PopulateShortcutBindingsPanel`, `CreateShortcutBindingRow`, `HandleShortcutEditorKeyDown`, `IsModifierKey`, `GetEffectiveShortcutBinding`, `FormatScopeLabel`, `ShowShortcutValidationMessage` and `ClearShortcutValidationMessage`.
  - In `CreateShortcutBindingRow`, add `bindingEditor.Classes.Add(CaptureClass);` right after the editor is built (after 2386).
  - The Reset handler's `RebuildTitleBarRows();` (2405) and the key handler's `RebuildTitleBarRows();` (2510) become `Session.RaiseShortcutsChanged();`. Rewrite the comment above each, because the target is now an event: "Pages that print shortcuts (title bar rows, Command Assist descriptions) listen for this; without it they keep showing the old binding until Settings reopens."
  - `InitializeShortcutEditor` (2303-2318) is replaced by the constructor and `Load` below.

```csharp
public partial class ShortcutsPage : UserControl, ISettingsPage
{
    /// <summary>
    /// Class on every binding editor. The frame's Ctrl/Cmd+S skips a focused editor so the chord
    /// can be recorded as a binding.
    /// </summary>
    internal const string CaptureClass = "ShortcutCapture";

    private const string RowDescStyleClass = "RowDesc";

    private SettingsSession? _session;

    public ShortcutsPage()
    {
        InitializeComponent();
        var search = this.FindControl<TextBox>("ShortcutSearchInput")!;
        search.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && _session != null)
            {
                PopulateShortcutBindingsPanel(search.Text ?? "");
            }
        };
    }

    public SettingsPage Page => SettingsPage.Shortcuts;

    public Control View => this;

    private SettingsSession Session => _session ?? throw new InvalidOperationException("ShortcutsPage used before Load.");

    public void Load(SettingsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        PopulateShortcutBindingsPanel(this.FindControl<TextBox>("ShortcutSearchInput")!.Text ?? "");
    }

    public SettingsValidationError? Validate()
    {
        ShortcutBindingResolution resolution = ShortcutBindingResolver.Resolve(ShortcutCatalog.GetDefinitions(), Session.ShortcutDraft);
        if (resolution.IsValid)
        {
            return null;
        }

        const string message = "Resolve duplicate shortcuts before saving.";
        ShowShortcutValidationMessage(message);
        return new SettingsValidationError(SettingsPage.Shortcuts, null, message);
    }

    public void Collect(TerminalSettings target)
    {
        // Nothing to collect: keybindings are committed from the session's draft (SettingsSession.CommitDrafts).
    }

    // Moved members as listed above.
}
```

- [ ] **Step 5: Run the tests and the Move-rules `git grep`.** Expected: PASS, and no grep output.

- [ ] **Step 6: Commit.**

```bash
git add src/Ntilde.App/Settings/Pages/ tests/Ntilde.App.Tests/Core/SettingsPageTests.cs
git commit -m "feat(settings): Shortcuts page validating through the shared draft" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

## Task 2.7: The Command Assist page

**Files:**
- Create: `src/Ntilde.App/Settings/Pages/CommandAssistPage.axaml(.cs)`
- Test: `tests/Ntilde.App.Tests/Core/SettingsPageTests.cs`

**Interfaces:**
- `public partial class CommandAssistPage`, with internal members:
  - `Task ReloadSnippetsAsync()`, renamed from `ReloadCommandAssistSnippetsAsync`. The frame calls it when the window opens and after an external reload.
  - `static string SubstituteChords(string, params (string Written, string Effective)[])`, moved.
- It listens to `session.ShortcutsChanged` to refresh the chord names in its descriptions.

- [ ] **Step 1: Write the failing tests.** Append them to `SettingsPageTests`.

```csharp
    [AvaloniaFact]
    public void CommandAssistPage_LoadsAndCollectsItsFourToggles()
    {
        var page = new CommandAssistPage();
        page.Load(SessionFor(new TerminalSettings
        {
            CommandAssistEnabled = true,
            CommandAssistPassiveBubbleEnabled = false,
            CommandAssistHistoryEnabled = false,
            CommandAssistShellIntegrationEnabled = false,
        }));
        var target = new TerminalSettings();

        page.Collect(target);

        Assert.True(target.CommandAssistEnabled);
        Assert.False(target.CommandAssistPassiveBubbleEnabled);
        Assert.False(target.CommandAssistHistoryEnabled);
        Assert.False(target.CommandAssistShellIntegrationEnabled);
    }

    [AvaloniaFact]
    public void CommandAssistPage_NamesTheEffectiveChords_AndFollowsLaterRebinds()
    {
        var session = SessionFor(new TerminalSettings { Keybindings = new() { ["command_assist_history"] = "Alt+F8" } });
        var page = new CommandAssistPage();
        page.Load(session);
        var history = page.FindControl<TextBlock>("CommandAssistHistoryDesc")!;

        Assert.Contains("Alt+F8", history.Text);
        Assert.DoesNotContain("Ctrl+R", history.Text);

        session.ShortcutDraft["command_assist_history"] = "Alt+F7";
        session.RaiseShortcutsChanged();

        Assert.Contains("Alt+F7", history.Text);
    }
```

- [ ] **Step 2: Run them and watch them fail.** Expected: compile errors.

- [ ] **Step 3: Generate the XAML.** `write_page_xaml CommandAssistPage 919 1002`

- [ ] **Step 4: Write `CommandAssistPage.axaml.cs`.** Moved members, from `ORIG`, with the Move rules applied:
  - Fields `_snippetsEmptyHint` (49), `_isClearCommandAssistHistoryArmed` (91) and `_snippetEditor` (110).
  - `UpdateCommandAssistShortcutText` (2038-2070), `SubstituteChords` (2072-2105) and `FormatSnippetsEmptyHint` (2107-2108).
  - Everything from 2562 to 3056: the remote shell integration row, Clear history, the snippet manager, and `ShowRemoteShellIntegrationStatus`.
  - In `WireCommandAssistSnippetsRow`, **delete** `Opened += async (_, _) => await ReloadCommandAssistSnippetsAsync();` (2779). The frame calls `ReloadSnippetsAsync` on `Opened` instead. Rewrite the first `<para>` of its doc comment to say so.
  - Rename `ReloadCommandAssistSnippetsAsync` to `ReloadSnippetsAsync` and make it `internal`.

```csharp
public partial class CommandAssistPage : UserControl, ISettingsPage
{
    private const string RowDescStyleClass = "RowDesc";

    private SettingsSession? _session;

    // ORIG fields 49, 91 and 110 go here.

    public CommandAssistPage()
    {
        InitializeComponent();
        WireRemoteShellIntegrationRow();
        WireClearCommandAssistHistoryRow();
        WireCommandAssistSnippetsRow();
    }

    public SettingsPage Page => SettingsPage.CommandAssist;

    public Control View => this;

    private SettingsSession Session => _session ?? throw new InvalidOperationException("CommandAssistPage used before Load.");

    public void Load(SettingsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!ReferenceEquals(_session, session))
        {
            if (_session != null)
            {
                _session.ShortcutsChanged -= UpdateCommandAssistShortcutText;
            }

            session.ShortcutsChanged += UpdateCommandAssistShortcutText;
            _session = session;
        }

        TerminalSettings settings = session.Settings;
        Toggle("CommandAssistToggle").IsChecked = settings.CommandAssistEnabled;
        Toggle("CommandAssistPassiveBubbleToggle").IsChecked = settings.CommandAssistPassiveBubbleEnabled;
        Toggle("CommandAssistHistoryToggle").IsChecked = settings.CommandAssistHistoryEnabled;
        Toggle("CommandAssistShellIntegrationToggle").IsChecked = settings.CommandAssistShellIntegrationEnabled;
        UpdateCommandAssistShortcutText();
    }

    public SettingsValidationError? Validate() => null;

    public void Collect(TerminalSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.CommandAssistEnabled = Toggle("CommandAssistToggle").IsChecked == true;
        target.CommandAssistPassiveBubbleEnabled = Toggle("CommandAssistPassiveBubbleToggle").IsChecked == true;
        target.CommandAssistHistoryEnabled = Toggle("CommandAssistHistoryToggle").IsChecked == true;
        target.CommandAssistShellIntegrationEnabled = Toggle("CommandAssistShellIntegrationToggle").IsChecked == true;
    }

    private CheckBox Toggle(string name) => this.FindControl<CheckBox>(name)!;

    // Moved members as listed above.
}
```

- [ ] **Step 5: Run the tests and the Move-rules `git grep`.** Expected: PASS, and no grep output.

- [ ] **Step 6: Commit.**

```bash
git add src/Ntilde.App/Settings/Pages/ tests/Ntilde.App.Tests/Core/SettingsPageTests.cs
git commit -m "feat(settings): Command Assist page" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

## Task 2.8: The Shells & Profiles page

**Files:**
- Create: `src/Ntilde.App/Settings/Pages/ProfilesPage.axaml(.cs)`
- Test: `tests/Ntilde.App.Tests/Core/SettingsPageTests.cs`

**Interfaces:**
- `public partial class ProfilesPage : UserControl, ISettingsPage, IDisposable`. `Dispose` stops the 2-second forwards timer. The frame disposes every disposable page when the window closes.
- Internal statics, moved: `BuildLocalProfilesForEditor`, `NormalizeSettingsProfilesForSave` and `ResolveDefaultLocalProfileId`.
- It listens to `session.ThemesChanged` and repopulates its override-theme combo.

- [ ] **Step 1: Write the failing tests.** Append them to `SettingsPageTests`.

```csharp
    private const string LocalProfileId = "33333333-3333-3333-3333-333333333333";

    private static TerminalSettings SettingsWithOneLocalAndOneSshProfile() => new()
    {
        Profiles =
        [
            new TerminalProfile { Id = Guid.Parse(LocalProfileId), Name = "Local", Command = "cmd.exe" },
            new TerminalProfile { Name = "Box", Type = ConnectionType.SSH, SshHost = "box" },
        ],
        DefaultProfileId = Guid.Parse(LocalProfileId),
    };

    [AvaloniaFact]
    public void ProfilesPage_ShowsOnlyLocalProfiles_AndCollectWritesThemBack()
    {
        var page = new ProfilesPage();
        page.Load(SessionFor(SettingsWithOneLocalAndOneSshProfile()));
        var target = new TerminalSettings();

        page.Collect(target);

        Assert.Single(page.FindControl<ListBox>("ProfilesListBox")!.Items);
        TerminalProfile only = Assert.Single(target.Profiles);
        Assert.Equal("Local", only.Name);
        Assert.Equal(Guid.Parse(LocalProfileId), target.DefaultProfileId);
        page.Dispose();
    }

    [AvaloniaFact]
    public void ProfilesPage_AfterAReload_EditsLandOnTheFreshProfile()
    {
        var page = new ProfilesPage();
        page.Load(SessionFor(SettingsWithOneLocalAndOneSshProfile()));
        var list = page.FindControl<ListBox>("ProfilesListBox")!;
        list.SelectedIndex = 0;

        page.Load(SessionFor(SettingsWithOneLocalAndOneSshProfile())); // what an import/restore reload does
        var name = page.FindControl<TextBox>("ProfileNameInput")!;
        name.Text = "Renamed after reload";
        name.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.D, Source = name });
        var target = new TerminalSettings();
        page.Collect(target);

        Assert.Equal("Renamed after reload", Assert.Single(target.Profiles).Name);
        page.Dispose();
    }

    [AvaloniaFact]
    public void ProfilesPage_OverrideFontList_OffersTheSessionsFontChoices()
    {
        var settings = SettingsWithOneLocalAndOneSshProfile();
        settings.FontFamily = "Session Mono";
        var page = new ProfilesPage();

        page.Load(SessionFor(settings));

        Assert.Contains(
            page.FindControl<ComboBox>("OverrideFontList")!.Items.OfType<ComboBoxItem>(),
            item => (string?)item.Content == "Session Mono");
        page.Dispose();
    }
```

- [ ] **Step 2: Run them and watch them fail.** Expected: compile errors.

- [ ] **Step 3: Generate the XAML.** `write_page_xaml ProfilesPage 713 885`

- [ ] **Step 4: Write `ProfilesPage.axaml.cs`.** Moved members, from `ORIG`, with the Move rules applied:
  - Fields `_selectedProfile` and `_profilesList` (46-47), and `_statusTimer` (66).
  - **Constructor wiring:**
    - 495-564 (the profile list, Add, Delete, Import from Windows Terminal);
    - 570-580 (Set as default) and 582-592 (editor inputs);
    - 594-666 (connection type and the SSH inputs: these move as they are, and PR 3 deletes them);
    - 668-761 and 771-799 (overrides; skip 763-769, an empty `ligatureToggle` handler);
    - 956 (`btnAddRule`).
    - Start each wiring method with the `this.FindControl` lookups it uses, copied from 223-259. The build names any you missed.
  - **The 2-second timer:** 482-487 goes into the constructor. The `Closed` stop at 489-493 becomes `Dispose()`.
  - **Methods:**
    - 1357-1421 (`RepointSelectedProfileAfterReload`, `ClearProfileEditorFields`);
    - 1729-1791 (the three statics; `internal static` as they are);
    - 1793-1886 (`PopulateProfilesList`, `RefreshProfileListItem`, `SwitchSelectedProfile`);
    - 1888-2001 (jump host and port-forward helpers, which move as they are; PR 3 deletes them).
  - The SSH key browse at 641 uses the `StorageProvider` Move rule.
  - **Do not move 456-466**, the override-theme handler that repainted the Appearance preview (Correction 6).

```csharp
public partial class ProfilesPage : UserControl, ISettingsPage, IDisposable
{
    private SettingsSession? _session;

    // ORIG fields 46-47 and 66 go here.

    public ProfilesPage()
    {
        InitializeComponent();
        // Wiring as listed above, then the timer (482-487).
    }

    public SettingsPage Page => SettingsPage.Profiles;

    public Control View => this;

    private SettingsSession Session => _session ?? throw new InvalidOperationException("ProfilesPage used before Load.");

    public void Load(SettingsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!ReferenceEquals(_session, session))
        {
            if (_session != null)
            {
                _session.ThemesChanged -= PopulateOverrideThemes;
            }

            session.ThemesChanged += PopulateOverrideThemes;
        }

        // Captured before the rebuild: _selectedProfile points into the list about to be replaced,
        // and its Id is all that still identifies "the same" profile afterwards.
        Guid? previousSelectedProfileId = _selectedProfile?.Id;
        _session = session;

        // Settings editor is local-profiles only; SSH connections are managed in Connection Manager.
        _profilesList = BuildLocalProfilesForEditor(session.Settings.Profiles);
        session.Settings.DefaultProfileId = ResolveDefaultLocalProfileId(session.Settings.DefaultProfileId, _profilesList);

        PopulateOverrideFonts();
        PopulateOverrideThemes();
        PopulateProfilesList();
        RepointSelectedProfileAfterReload(previousSelectedProfileId);
    }

    public SettingsValidationError? Validate() => null;

    public void Collect(TerminalSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);

        // Sync local profiles list back to settings (SSH connections are store-backed separately).
        target.Profiles = NormalizeSettingsProfilesForSave(_profilesList);
        target.DefaultProfileId = ResolveDefaultLocalProfileId(target.DefaultProfileId, target.Profiles);
    }

    public void Dispose()
    {
        _statusTimer?.Stop();
        _statusTimer = null;
        GC.SuppressFinalize(this);
    }

    private void PopulateOverrideFonts()
    {
        var overrideFontList = this.FindControl<ComboBox>("OverrideFontList")!;
        overrideFontList.Items.Clear();
        foreach (string family in Session.FontChoices)
        {
            overrideFontList.Items.Add(new ComboBoxItem { Content = family });
        }
    }

    private void PopulateOverrideThemes()
    {
        Session.Settings.ThemeManager.LoadThemes();
        var overrideThemeList = this.FindControl<ComboBox>("OverrideThemeList")!;
        overrideThemeList.Items.Clear();
        foreach (string theme in Session.Settings.ThemeManager.GetAvailableThemes().OrderBy(t => t))
        {
            overrideThemeList.Items.Add(new ComboBoxItem { Content = theme });
        }
    }

    // Moved members as listed above.
}
```

- [ ] **Step 5: Run the tests and the Move-rules `git grep`.** Expected: PASS, and no grep output.

- [ ] **Step 6: Commit.**

```bash
git add src/Ntilde.App/Settings/Pages/ tests/Ntilde.App.Tests/Core/SettingsPageTests.cs
git commit -m "feat(settings): Shells & Profiles page" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

## Task 2.9: The Appearance page

**Files:**
- Create: `src/Ntilde.App/Settings/Pages/AppearancePage.axaml(.cs)`
- Test: `tests/Ntilde.App.Tests/Core/SettingsPageTests.cs`

**Interfaces:**
- `public partial class AppearancePage`, which owns everything on today's Appearance tab.
- It listens to `session.ShortcutsChanged`, to rebuild the title-bar rows.
- It raises `Session.RaiseThemesChanged()` after a theme import, save or delete, and previews through `Session.Preview`.
- The TITLE BAR header carries `settings:SettingsNavigation.RowId="title-bar"`. `MainWindow`'s Customize Title Bar target uses that id until PR 3.

- [ ] **Step 1: Write the failing tests.** Append them to `SettingsPageTests`, and add `using Ntilde.Shell.TitleBar;`.

```csharp
    [AvaloniaFact]
    public void AppearancePage_CollectsEveryFieldItLoaded()
    {
        using var root = SettingsTestRoot.Empty();
        root.CopyShippedTheme("GitHubLight.json");
        var seed = new TerminalSettings
        {
            FontSize = 17, MaxHistory = 25000, FontFamily = "Page Mono", ThemeName = "GitHub Light",
            WindowOpacity = 0.75, BlurEffect = "Mica", UiScale = 1.25, TabStripOrientation = "Vertical",
            EnableLigatures = true, EnableComplexShaping = false, BackgroundImagePath = "/img.png",
            BackgroundImageOpacity = 0.35, BackgroundImageStretch = "Uniform",
            LongCommandNotificationsEnabled = true, Osc9NotificationsEnabled = true,
            Osc9ProgressReportingEnabled = false, AutomaticUpdateChecks = false,
        };
        var page = new AppearancePage();
        page.Load(new SettingsSession(seed, () => ["Page Mono"]));
        var target = new TerminalSettings();

        page.Collect(target);

        Assert.Equal(17, target.FontSize);
        Assert.Equal(25000, target.MaxHistory);
        Assert.Equal("Page Mono", target.FontFamily);
        Assert.Equal("GitHub Light", target.ThemeName);
        Assert.Equal(0.75, target.WindowOpacity, precision: 9);
        Assert.Equal("Mica", target.BlurEffect);
        Assert.Equal(1.25, target.UiScale, precision: 9);
        Assert.Equal("Vertical", target.TabStripOrientation);
        Assert.True(target.EnableLigatures);
        Assert.False(target.EnableComplexShaping);
        Assert.Equal("/img.png", target.BackgroundImagePath);
        Assert.Equal(0.35, target.BackgroundImageOpacity, precision: 9);
        Assert.Equal("Uniform", target.BackgroundImageStretch);
        Assert.True(target.LongCommandNotificationsEnabled);
        Assert.True(target.Osc9NotificationsEnabled);
        Assert.False(target.Osc9ProgressReportingEnabled);
        Assert.False(target.AutomaticUpdateChecks);
    }

    [AvaloniaFact]
    public void AppearancePage_RebuildsTheTitleBarRowsWhenShortcutsChange()
    {
        using var root = SettingsTestRoot.Empty();
        var session = SessionFor(new TerminalSettings());
        var page = new AppearancePage();
        page.Load(session);

        session.ShortcutDraft["settings"] = "Alt+F9";
        session.RaiseShortcutsChanged();

        Assert.Contains(
            page.FindControl<StackPanel>("TitleBarItemsPanel")!.GetLogicalDescendants().OfType<TextBlock>(),
            t => t.Text == "Alt+F9");
    }

    [AvaloniaFact]
    public void AppearancePage_SelectingATheme_PreviewsAndRepaintsThroughTheSession()
    {
        using var root = SettingsTestRoot.Empty();
        root.CopyShippedTheme("GitHubLight.json");
        var session = SessionFor(new TerminalSettings());
        var page = new AppearancePage();
        page.Load(session);
        string? previewed = null;
        TerminalTheme? repainted = null;
        session.Preview.ThemeChanged += name => previewed = name;
        session.Preview.RepaintRequested += theme => repainted = theme;

        var themes = page.FindControl<ComboBox>("ThemeList")!;
        themes.SelectedItem = themes.Items.OfType<ComboBoxItem>().Single(i => (string?)i.Content == "GitHub Light");

        Assert.Equal("GitHub Light", previewed);
        Assert.Equal("GitHub Light", repainted?.Name);
    }
```

- [ ] **Step 2: Run them and watch them fail.** Expected: compile errors.

- [ ] **Step 3: Generate the XAML, then add the row id.**

```bash
write_page_xaml AppearancePage 375 708
```

In the generated file, give the `TitleBarSectionHeader` TextBlock the attached id. Its line becomes:

```xml
                <TextBlock Name="TitleBarSectionHeader" settings:SettingsNavigation.RowId="title-bar" Classes="SectionHeader" Text="TITLE BAR" Margin="0,24,0,14"/>
```

- [ ] **Step 4: Write `AppearancePage.axaml.cs`.** Moved members, from `ORIG`, with the Move rules applied:
  - Field `_editingTheme` (67).
  - **`WireThemeSection()`:**
    - lines 262-273 (lookups) and 275-454: theme import, edit, new, close, save and delete, the swatches, and the colour inputs;
    - then 824-836 (the `themeList` handler, whose `ApplyTheme(theme)` becomes `Session.Preview.RepaintWindow(theme)`);
    - add `var themeList = this.FindControl<ComboBox>("ThemeList");` at the top;
    - after each `PopulateThemes();` in import (299), save (362) and delete (400), add `Session.RaiseThemesChanged();`;
    - **do not move 456-466** (Correction 6).
  - **`WireFontRows()`:** 801-822.
  - **`WireWindowRows()`:** 838-954. The local function `TriggerBgUpdate` (839-845) becomes a private method.
  - **Methods:**
    - `RebuildTitleBarRows` (2110-2134), **minus** its first line `UpdateCommandAssistShortcutText();` (2112). That page now listens for itself.
    - `CreateTitleBarRow`, `MoveDraftPinned`, `ShowTitleBarValidationMessage` and `ClearTitleBarValidationMessage` (2145-2301).
    - `UpdateThemePreview`, `OpenThemeEditor`, `UpdateEditorSwatches` and `OpenSwatchFlyout` (3216-3315).
    - `FormatUiScale` (3344).

```csharp
public partial class AppearancePage : UserControl, ISettingsPage
{
    // Shared style-class name (see NtStyles' "TextBlock.RowDesc" selector), used by the row
    // builders below; a const keeps the literal from drifting away from the selector.
    private const string RowDescStyleClass = "RowDesc";

    private SettingsSession? _session;

    // ORIG field 67 (_editingTheme) goes here.

    public AppearancePage()
    {
        InitializeComponent();
        WireThemeSection();
        WireFontRows();
        WireWindowRows();
    }

    public SettingsPage Page => SettingsPage.Appearance;

    public Control View => this;

    private SettingsSession Session => _session ?? throw new InvalidOperationException("AppearancePage used before Load.");

    public void Load(SettingsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!ReferenceEquals(_session, session))
        {
            if (_session != null)
            {
                _session.ShortcutsChanged -= RebuildTitleBarRows;
            }

            session.ShortcutsChanged += RebuildTitleBarRows;
        }

        _session = session;
        PopulateFonts();
        PopulateThemes();
        LoadControls();
        RebuildTitleBarRows();

        // LoadControls' theme selection usually paints the preview via SelectionChanged, but only
        // when an item matches ThemeName - a name the manager normalizes (legacy "Default (Dark)")
        // or a since-deleted theme leaves the combo empty and the preview stuck on its hardcoded
        // dark background. Paint it from the active theme directly so it is always current.
        UpdateThemePreview(session.Settings.ActiveTheme, "Main");
    }

    public SettingsValidationError? Validate() => null;

    public void Collect(TerminalSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);

        target.FontSize = (double)(this.FindControl<NumericUpDown>("FontSizeInput")!.Value ?? 14);
        target.MaxHistory = (int)(this.FindControl<NumericUpDown>("ScrollbackInput")!.Value ?? 10000);
        target.WindowOpacity = this.FindControl<Slider>("WindowOpacitySlider")!.Value;
        target.UiScale = UiScale.Clamp(this.FindControl<Slider>("UiScaleSlider")!.Value);
        target.EnableLigatures = Toggle("LigatureToggle").IsChecked == true;
        target.EnableComplexShaping = Toggle("ComplexShapingToggle").IsChecked == true;
        target.LongCommandNotificationsEnabled = Toggle("LongCommandNotificationsToggle").IsChecked == true;
        target.Osc9NotificationsEnabled = Toggle("Osc9NotificationsToggle").IsChecked == true;
        target.Osc9ProgressReportingEnabled = Toggle("Osc9ProgressReportingToggle").IsChecked == true;
        target.AutomaticUpdateChecks = Toggle("AutomaticUpdateChecksToggle").IsChecked == true;

        if (SelectedItem("FontList") is { } fontItem)
        {
            target.FontFamily = fontItem.Content?.ToString() ?? BundledFontCatalog.DefaultTerminalFontFamily;
        }

        if (SelectedItem("ThemeList") is { } themeItem)
        {
            target.ThemeName = themeItem.Content?.ToString() ?? "Default";
        }

        if (SelectedItem("BlurList") is { } blurItem)
        {
            target.BlurEffect = blurItem.Content?.ToString() ?? "Acrylic";
        }

        if (SelectedItem("TabOrientationList") is { } orientationItem)
        {
            target.TabStripOrientation = orientationItem.Content?.ToString() ?? "Horizontal";
        }

        target.BackgroundImagePath = this.FindControl<TextBox>("BgImagePathInput")!.Text ?? "";
        target.BackgroundImageOpacity = this.FindControl<Slider>("BgImageOpacitySlider")!.Value;
        if (SelectedItem("BgImageStretchList") is { } stretchItem)
        {
            target.BackgroundImageStretch = stretchItem.Content?.ToString() ?? "UniformToFill";
        }
    }

    private void PopulateFonts()
    {
        var fontList = this.FindControl<ComboBox>("FontList")!;
        fontList.Items.Clear();
        foreach (string family in Session.FontChoices)
        {
            fontList.Items.Add(new ComboBoxItem { Content = family });
        }
    }

    private void PopulateThemes()
    {
        Session.Settings.ThemeManager.LoadThemes();
        var themeList = this.FindControl<ComboBox>("ThemeList")!;
        themeList.Items.Clear();
        foreach (string theme in Session.Settings.ThemeManager.GetAvailableThemes().OrderBy(t => t))
        {
            themeList.Items.Add(new ComboBoxItem { Content = theme });
        }
    }

    /// <summary>The Appearance half of the old LoadCurrentSettings, value for value.</summary>
    private void LoadControls()
    {
        TerminalSettings settings = Session.Settings;

        Toggle("LongCommandNotificationsToggle").IsChecked = settings.LongCommandNotificationsEnabled;
        Toggle("Osc9NotificationsToggle").IsChecked = settings.Osc9NotificationsEnabled;
        Toggle("Osc9ProgressReportingToggle").IsChecked = settings.Osc9ProgressReportingEnabled;
        Toggle("AutomaticUpdateChecksToggle").IsChecked = settings.AutomaticUpdateChecks;
        this.FindControl<NumericUpDown>("FontSizeInput")!.Value = (decimal)settings.FontSize;
        this.FindControl<NumericUpDown>("ScrollbackInput")!.Value = settings.MaxHistory;

        this.FindControl<Slider>("WindowOpacitySlider")!.Value = settings.WindowOpacity;
        this.FindControl<TextBlock>("OpacityValueDisplay")!.Text = $"{(int)(settings.WindowOpacity * 100)}%";

        var uiScaleSlider = this.FindControl<Slider>("UiScaleSlider")!;
        uiScaleSlider.Value = UiScale.Clamp(settings.UiScale);
        this.FindControl<TextBlock>("UiScaleValueDisplay")!.Text = FormatUiScale(uiScaleSlider.Value);

        Toggle("LigatureToggle").IsChecked = settings.EnableLigatures;
        Toggle("ComplexShapingToggle").IsChecked = settings.EnableComplexShaping;

        this.FindControl<TextBox>("BgImagePathInput")!.Text = settings.BackgroundImagePath;
        this.FindControl<Slider>("BgImageOpacitySlider")!.Value = settings.BackgroundImageOpacity;
        this.FindControl<TextBlock>("BgImageOpacityDisplay")!.Text = $"{(int)(settings.BackgroundImageOpacity * 100)}%";

        SelectByContent("FontList", settings.FontFamily, StringComparison.Ordinal, fallbackIndex: null);

        // Legacy settings store "Default (Dark)" while the combo lists the manager's canonical
        // "Default" (ThemeManager.GetTheme maps between them). Normalize that one alias rather than
        // resolving through GetTheme: for a name GetTheme cannot find it falls back to the Default
        // theme OBJECT, whose .Name would match the "Default" item and silently rewrite the stored
        // name on Save. When nothing matches, the selection stays empty and Collect skips the theme
        // field, preserving the stored name.
        string themeName = settings.ThemeName == "Default (Dark)" ? "Default" : settings.ThemeName;
        SelectByContent("ThemeList", themeName, StringComparison.Ordinal, fallbackIndex: null);

        SelectByContent("BlurList", settings.BlurEffect, StringComparison.Ordinal, fallbackIndex: 0);
        SelectByContent("TabOrientationList", settings.TabStripOrientation, StringComparison.OrdinalIgnoreCase, fallbackIndex: 0);
        SelectByContent("BgImageStretchList", settings.BackgroundImageStretch, StringComparison.Ordinal, fallbackIndex: 3); // UniformToFill
    }

    /// <summary>
    /// Selects the item whose text equals <paramref name="value"/>. With no match, falls back to
    /// <paramref name="fallbackIndex"/> only when nothing is selected yet, exactly as the old loops did.
    /// </summary>
    private void SelectByContent(string comboName, string? value, StringComparison comparison, int? fallbackIndex)
    {
        var combo = this.FindControl<ComboBox>(comboName)!;
        ComboBoxItem? match = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Content?.ToString(), value, comparison));
        if (match != null)
        {
            combo.SelectedItem = match;
        }
        else if (fallbackIndex is int index && combo.SelectedItem == null && combo.ItemCount > 0)
        {
            combo.SelectedIndex = index;
        }
    }

    private ComboBoxItem? SelectedItem(string comboName) => this.FindControl<ComboBox>(comboName)!.SelectedItem as ComboBoxItem;

    private CheckBox Toggle(string name) => this.FindControl<CheckBox>(name)!;

    // Moved members as listed above.
}
```

- [ ] **Step 5: Run the tests and the Move-rules `git grep`.** Expected: PASS, and no grep output.

- [ ] **Step 6: Commit.**

```bash
git add src/Ntilde.App/Settings/Pages/ tests/Ntilde.App.Tests/Core/SettingsPageTests.cs
git commit -m "feat(settings): Appearance page" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

## Task 2.10: The frame, the registry, `MainWindow` entry points and the test migration

This task swaps the old window for the frame. The build stays red until every step through Step 6 is done, so do them in one sitting and commit once at the end.

**Files:**
- Create: `src/Ntilde.App/Settings/SettingsPageRegistry.cs`
- Replace: `src/Ntilde.App/SettingsWindow.axaml`, `src/Ntilde.App/SettingsWindow.axaml.cs`
- Delete: `src/Ntilde.App/SettingsSection.cs`
- Modify: `src/Ntilde.App/Settings/NtStyles.axaml` (the sidebar group and glyph styles)
- Modify: `src/Ntilde.App/MainWindow.axaml.cs`. Find these by name: `OpenSettings`, `OpenSettingsToBackupPage`, `CustomizeTitleBarSettingsTarget`, `ApplySettingsWindowResult`, and the five callers.
- Delete: `tests/Ntilde.App.Tests/Core/SettingsWindowSelectBackupPageTests.cs`, `SettingsWindowTitleBarSectionTests.cs`, `MainWindowCustomizeTitleBarSettingsTargetTests.cs`
- Modify: `SettingsWindowBackupSectionTests.cs`, `SettingsWindowTitleBarShortcutRefreshTests.cs`, `ThemeApplicationRegressionTests.cs`, `SettingsWindowUiScaleTests.cs`, `MainWindowBackupPaletteTests.cs`, `SettingsWindowFontChoicesTests.cs`, `SettingsWindowProfileSeparationTests.cs`, `SettingsWindowShortcutFilteringTests.cs`, `SettingsWindowShortcutTextTests.cs`
- Create: `tests/Ntilde.App.Tests/Core/SettingsWindowFrameTests.cs`, `tests/Ntilde.App.Tests/Core/MainWindowSettingsTargetTests.cs`

**Interfaces:**
- `SettingsPageDescriptor(SettingsPage Page, string Group, string Title, string Glyph, Func<ISettingsPage> Create, bool MonoGlyph = false)`.
- `SettingsPageRegistry`: `DefaultPage` (Appearance in PR 2, General from PR 3), `Pages`, `Get(SettingsPage)`.
- `SettingsWindow`:
  - public: `SettingsWindow()`, `SettingsWindow(SettingsTarget)`, `Settings`, and the seven `On…Changed` events;
  - internal: `Session`, `CurrentPage`, `ConfigurationReplacedExternally`, `CommandAssistHistoryStore`, `CommandAssistSnippetStore`, `OnCommandAssistHistoryCleared`, `OnCommandAssistSnippetsChanged`, `RestoreConfirmationOverride`;
  - internal: `GetPage<T>()`, `GetPage(SettingsPage)`, `NavItem(SettingsPage)`, `NavigateTo(SettingsTarget)`, `RevealRow(SettingsPage, string)`, `ReloadSettingsAfterExternalChangeAsync()`.
- `MainWindow`, internal: `DefaultSettingsTarget()`, `static CustomizeTitleBarSettingsTarget()`, `static ManageProfilesSettingsTarget()`, `static BackupSettingsTarget()`.
- `SettingsRowIds.TitleBar = "title-bar"`, a const in `SettingsPageRegistry.cs` until PR 3 removes it.

- [ ] **Step 1: Write the registry.** Create `src/Ntilde.App/Settings/SettingsPageRegistry.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Ntilde.Settings.Pages;

namespace Ntilde.Settings;

/// <summary>A sidebar entry: which page, under which group, with what title and glyph, and how to build it.</summary>
public sealed record SettingsPageDescriptor(
    SettingsPage Page,
    string Group,
    string Title,
    string Glyph,
    Func<ISettingsPage> Create,
    bool MonoGlyph = false);

/// <summary>
/// The Settings pages in sidebar order. The sidebar, navigation and search are all driven from this
/// list, so a new page is one line here plus its control.
/// </summary>
public static class SettingsPageRegistry
{
    /// <summary>Where Settings opens when nothing asks for a page and none was visited in this run.</summary>
    public const SettingsPage DefaultPage = SettingsPage.Appearance;

    public static IReadOnlyList<SettingsPageDescriptor> Pages { get; } =
    [
        new(SettingsPage.Appearance, "INTERFACE", "Appearance", "◐", () => new AppearancePage()),
        new(SettingsPage.Profiles, "INTERFACE", "Shells & Profiles", ">_", () => new ProfilesPage(), MonoGlyph: true),
        new(SettingsPage.Shortcuts, "INTERFACE", "Shortcuts", "⌘", () => new ShortcutsPage()),
        new(SettingsPage.CommandAssist, "ASSISTANT", "Command Assist", "❯", () => new CommandAssistPage(), MonoGlyph: true),
        new(SettingsPage.AgentAccess, "ASSISTANT", "Agent Access", "◌", () => new AgentAccessPage()),
        new(SettingsPage.Ssh, "CONNECTION", "SSH", "⇄", () => new SshPage()),
        new(SettingsPage.Backup, "DATA", "Backup & Restore", "⇩", () => new BackupPage()),
    ];

    public static SettingsPageDescriptor Get(SettingsPage page) => Pages.Single(descriptor => descriptor.Page == page);
}

/// <summary>Row ids that code (not just XAML) refers to.</summary>
public static class SettingsRowIds
{
    /// <summary>The TITLE BAR section on Appearance; Customize Title Bar… opens here until PR 3 gives it a page.</summary>
    public const string TitleBar = "title-bar";
}
```

- [ ] **Step 2: Add the sidebar styles to `NtStyles.axaml`.** Insert them right after the `ListBox.SideNav ListBoxItem:selected` style:

```xml
    <!-- Sidebar group headers: disabled items, so they are never a selection or a focus stop. -->
    <Style Selector="ListBox.SideNav ListBoxItem.NavGroup">
        <Setter Property="Padding" Value="8,18,8,8"/>
        <Setter Property="Margin" Value="6,0"/>
    </Style>
    <Style Selector="ListBox.SideNav ListBoxItem.NavGroup.First">
        <Setter Property="Padding" Value="8,0,8,8"/>
    </Style>
    <Style Selector="ListBox.SideNav ListBoxItem.NavGroup:disabled /template/ ContentPresenter">
        <Setter Property="Background" Value="Transparent"/>
    </Style>

    <!-- Sidebar glyphs: dim, and accent-coloured only on the selected page. -->
    <Style Selector="TextBlock.NavGlyph">
        <Setter Property="FontSize" Value="13"/>
        <Setter Property="Foreground" Value="{DynamicResource NtFg3}"/>
        <Setter Property="VerticalAlignment" Value="Center"/>
    </Style>
    <Style Selector="TextBlock.NavGlyph.Mono">
        <Setter Property="FontFamily" Value="{DynamicResource NtMono}"/>
        <Setter Property="FontSize" Value="12"/>
    </Style>
    <Style Selector="ListBox.SideNav ListBoxItem:selected TextBlock.NavGlyph">
        <Setter Property="Foreground" Value="{DynamicResource NtBlue}"/>
    </Style>
```

- [ ] **Step 3: Replace `SettingsWindow.axaml`.** The `TabControl` and its styles go. The sidebar's "Changes apply live. Hit ⌘S…" block goes, because the footer already says it. Cancel gets its tooltip.

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:settings="clr-namespace:Ntilde.Settings"
        x:Class="Ntilde.SettingsWindow"
        Title="Settings"
        Width="880" Height="620"
        MinWidth="780" MinHeight="520"
        WindowStartupLocation="CenterOwner"
        Background="#16181d"
        Foreground="#e6e8ec"
        FontFamily="Inter, Segoe UI, system-ui, sans-serif">

    <Window.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <settings:NtPalette/>
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Window.Resources>

    <Window.Styles>
        <settings:NtStyles/>
    </Window.Styles>

    <Grid RowDefinitions="*,Auto">

        <!-- ╭───────── Body: sidebar + pages ─────────╮ -->
        <Grid Grid.Row="0" ColumnDefinitions="208,*">

            <Border Grid.Column="0"
                    Background="{StaticResource NtChromeBg}"
                    BorderBrush="{StaticResource NtHairline}"
                    BorderThickness="0,0,1,0">
                <!-- Items are built from SettingsPageRegistry in code. -->
                <ListBox Name="SideNav"
                         Classes="SideNav"
                         SelectionMode="Single"
                         Padding="0,16"
                         AutomationProperties.Name="Settings pages"/>
            </Border>

            <!-- Every page lives here for the window's lifetime; only the current one is visible.
                 Keeping them all in the tree is what lets FindResource and DynamicResource work for
                 pages that are not on screen - Save collects from all of them. -->
            <Panel Name="PageHost" Grid.Column="1"/>
        </Grid>

        <!-- ╭───────── Footer ─────────╮ -->
        <Border Grid.Row="1"
                Background="{StaticResource NtChromeBg}"
                BorderBrush="{StaticResource NtHairline}"
                BorderThickness="0,1,0,0"
                Padding="16,10">
            <Grid ColumnDefinitions="*,Auto,Auto">
                <TextBlock Grid.Column="0"
                           Text="Changes apply live."
                           Foreground="{StaticResource NtFg3}"
                           FontSize="12"
                           VerticalAlignment="Center"/>
                <Button Name="BtnCancel" Grid.Column="1" Classes="Pill" Content="Cancel" Width="100" Margin="0,0,8,0"
                        ToolTip.Tip="Reverts preview changes. Themes, snippets, history and backups are saved as you change them."/>
                <Button Name="BtnSave" Grid.Column="2" Classes="Primary" Content="Save" Width="100"/>
            </Grid>
        </Border>
    </Grid>
</Window>
```

- [ ] **Step 4: Replace `SettingsWindow.axaml.cs`.** Copy doc comments from `ORIG` where marked.

```csharp
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Ntilde.CommandAssist.Domain;
using Ntilde.Services.Ssh;
using Ntilde.Settings;
using Ntilde.Settings.Pages;
using Ntilde.Shell;
using Ntilde.VT;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Ntilde
{
    /// <summary>
    /// The Settings frame. It holds a sidebar built from <see cref="SettingsPageRegistry"/>, every
    /// page for the window's lifetime, and the Save / Cancel footer. Pages own their controls behind
    /// <see cref="ISettingsPage"/> and share one <see cref="SettingsSession"/>.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly SettingsSession _session;
        private readonly IReadOnlyList<ISettingsPage> _pages;
        private readonly Dictionary<SettingsPage, ListBoxItem> _navItems = new();
        private readonly ListBox _sideNav;
        private readonly Button _btnSave;
        private SettingsPage _currentPage;
        private bool _syncingNav;

        public TerminalSettings Settings => _session.Settings; // Expose for main window to grab without reloading disk

        // ORIG 34-43: the F1 doc comment for ConfigurationReplacedExternally, verbatim.
        internal bool ConfigurationReplacedExternally { get; private set; }

        internal SettingsSession Session => _session;

        /// <summary>The page on screen. MainWindow remembers it so Ctrl+, reopens where the user left off.</summary>
        internal SettingsPage CurrentPage => _currentPage;

        public event Action<double>? OnOpacityChanged;
        public event Action<string>? OnBlurChanged;
        public event Action<string, double, string>? OnBgImageChanged;
        public event Action<string>? OnFontChanged;
        public event Action<double>? OnFontSizeChanged;
        /// <summary>Live preview for Appearance > Window > Interface scale; the value is already clamped.</summary>
        public event Action<double>? OnUiScaleChanged;
        public event Action<string>? OnThemeChanged;

        // ORIG 69-77: the history-store doc comment, verbatim.
        internal IHistoryStore? CommandAssistHistoryStore
        {
            get => _session.HistoryStore;
            set => _session.HistoryStore = value;
        }

        // ORIG 80-87: the history-cleared event doc comment, verbatim.
        internal event Action? OnCommandAssistHistoryCleared;

        // ORIG 93-100: the snippet-store doc comment, verbatim.
        internal ISnippetStore? CommandAssistSnippetStore
        {
            get => _session.SnippetStore;
            set => _session.SnippetStore = value;
        }

        // ORIG 103-106: the snippets-changed event doc comment, verbatim.
        internal event Action? OnCommandAssistSnippetsChanged;

        /// <summary>Test seam for the restore confirmation; see <see cref="BackupPage.RestoreConfirmationOverride"/>.</summary>
        internal Func<BackupPage.SnapshotRow, Task<bool>>? RestoreConfirmationOverride
        {
            get => GetPage<BackupPage>().RestoreConfirmationOverride;
            set => GetPage<BackupPage>().RestoreConfirmationOverride = value;
        }

        public SettingsWindow() : this(new SettingsTarget(SettingsPageRegistry.DefaultPage)) { }

        public SettingsWindow(SettingsTarget target)
        {
            ArgumentNullException.ThrowIfNull(target);
            InitializeComponent();
            // ORIG 127-134: the comment explaining the pinned scale, verbatim.
            UiScale.PinScale(this, UiScale.Current);
            UiScale.FitWindow(this);

            var settings = TerminalSettings.Load();
            if (new SshLegacyProfileMigrationService().MigrateLegacyProfiles(settings))
            {
                settings.Save();
            }

            _session = new SettingsSession(settings);
            _session.SetExternalReloadHandler(ReloadSettingsAfterExternalChangeAsync);
            ApplyTheme();

            _sideNav = this.FindControl<ListBox>("SideNav")!;
            _btnSave = this.FindControl<Button>("BtnSave")!;

            // Every page joins the window now and stays: Save collects from all of them, and a page
            // that is not on screen still needs the window's resources for the rows it builds in code.
            var host = this.FindControl<Panel>("PageHost")!;
            _pages = SettingsPageRegistry.Pages.Select(descriptor => descriptor.Create()).ToList();
            foreach (ISettingsPage page in _pages)
            {
                page.View.IsVisible = false;
                host.Children.Add(page.View);
            }

            foreach (ISettingsPage page in _pages)
            {
                page.Load(_session);
            }

            // After the first Load on purpose: loading sets every control, and those changes are
            // not previews. (MainWindow subscribes after construction, so only the window's own
            // repaint is affected; it runs once, below, as ApplyTheme already did above.)
            WireSessionEvents();
            BuildSidebar();

            _btnSave.Click += (_, _) => SaveAndClose();
            this.FindControl<Button>("BtnCancel")!.Click += (_, _) => Close();

            // The snippet store arrives by property assignment after construction, so the list is
            // read once the window opens (the old WireCommandAssistSnippetsRow did the same).
            Opened += async (_, _) => await GetPage<CommandAssistPage>().ReloadSnippetsAsync();
            Closed += (_, _) =>
            {
                foreach (ISettingsPage page in _pages)
                {
                    (page as IDisposable)?.Dispose();
                }
            };

            NavigateTo(target);
        }

        internal T GetPage<T>() where T : class, ISettingsPage => _pages.OfType<T>().Single();

        internal ISettingsPage GetPage(SettingsPage page) => _pages.Single(candidate => candidate.Page == page);

        internal ListBoxItem NavItem(SettingsPage page) => _navItems[page];

        /// <summary>
        /// Shows <paramref name="target"/>'s page and, when it names a row, scrolls to the row and
        /// highlights it. Deep links, validation and search all come through here.
        /// </summary>
        internal void NavigateTo(SettingsTarget target)
        {
            ArgumentNullException.ThrowIfNull(target);
            ShowPage(target.Page);
            if (target.RowId is not { } rowId)
            {
                return;
            }

            // BringIntoView needs real layout. A window that is not open yet has none, so wait for
            // Opened; an open one only needs the pass this page switch just scheduled.
            if (IsVisible)
            {
                Dispatcher.UIThread.Post(() => RevealRow(target.Page, rowId), DispatcherPriority.Loaded);
                return;
            }

            void RevealOnceOpened(object? sender, EventArgs e)
            {
                Opened -= RevealOnceOpened;
                Dispatcher.UIThread.Post(() => RevealRow(target.Page, rowId), DispatcherPriority.Loaded);
            }

            Opened += RevealOnceOpened;
        }

        /// <summary>Scrolls the row carrying <paramref name="rowId"/> into view and highlights it. False when it is not on screen.</summary>
        internal bool RevealRow(SettingsPage page, string rowId)
        {
            if (page != _currentPage)
            {
                return false; // the user moved on before layout ran
            }

            Control? row = GetPage(page).View.GetLogicalDescendants()
                .OfType<Control>()
                .FirstOrDefault(control => string.Equals(SettingsNavigation.GetRowId(control), rowId, StringComparison.Ordinal));
            if (row is null)
            {
                return false;
            }

            row.BringIntoView();
            SettingsNavigation.Flash(row);
            return true;
        }

        private void ShowPage(SettingsPage page)
        {
            foreach (ISettingsPage candidate in _pages)
            {
                candidate.View.IsVisible = candidate.Page == page;
            }

            _currentPage = page;
            _syncingNav = true;
            try
            {
                _sideNav.SelectedItem = _navItems[page];
            }
            finally
            {
                _syncingNav = false;
            }
        }

        private void BuildSidebar()
        {
            string? group = null;
            foreach (SettingsPageDescriptor descriptor in SettingsPageRegistry.Pages)
            {
                if (!string.Equals(descriptor.Group, group, StringComparison.Ordinal))
                {
                    var header = new ListBoxItem
                    {
                        IsEnabled = false,
                        Content = new TextBlock { Text = descriptor.Group, Classes = { "SectionHeader" } },
                    };
                    header.Classes.Add("NavGroup");
                    if (group is null)
                    {
                        header.Classes.Add("First");
                    }

                    _sideNav.Items.Add(header);
                    group = descriptor.Group;
                }

                var glyph = new TextBlock { Text = descriptor.Glyph, Classes = { "NavGlyph" } };
                if (descriptor.MonoGlyph)
                {
                    glyph.Classes.Add("Mono");
                }

                var title = new TextBlock
                {
                    Text = descriptor.Title,
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(title, 1);

                var item = new ListBoxItem
                {
                    Tag = descriptor.Page,
                    Content = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*"), Children = { glyph, title } },
                };
                AutomationProperties.SetName(item, descriptor.Title);
                _navItems[descriptor.Page] = item;
                _sideNav.Items.Add(item);
            }

            _sideNav.SelectionChanged += OnSideNavSelectionChanged;
        }

        private void OnSideNavSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_syncingNav)
            {
                return;
            }

            if (_sideNav.SelectedItem is ListBoxItem { Tag: SettingsPage page })
            {
                ShowPage(page);
                return;
            }

            // A group header got selected (from code, or a keyboard path that reached it). Headers
            // are never a destination: carry on in the direction of travel to the next page.
            int from = _sideNav.Items.IndexOf(_navItems[_currentPage]);
            int to = _sideNav.SelectedIndex;
            int step = to > from ? 1 : -1;
            for (int i = to; i >= 0 && i < _sideNav.ItemCount; i += step)
            {
                if (_sideNav.Items[i] is ListBoxItem { Tag: SettingsPage next })
                {
                    ShowPage(next);
                    return;
                }
            }

            ShowPage(_currentPage);
        }

        private void WireSessionEvents()
        {
            SettingsPreviewSink preview = _session.Preview;
            preview.OpacityChanged += value => OnOpacityChanged?.Invoke(value);
            preview.BlurChanged += value => OnBlurChanged?.Invoke(value);
            preview.BackgroundImageChanged += (path, opacity, stretch) => OnBgImageChanged?.Invoke(path, opacity, stretch);
            preview.FontChanged += family => OnFontChanged?.Invoke(family);
            preview.FontSizeChanged += size => OnFontSizeChanged?.Invoke(size);
            preview.UiScaleChanged += scale => OnUiScaleChanged?.Invoke(scale);
            preview.ThemeChanged += name => OnThemeChanged?.Invoke(name);
            preview.RepaintRequested += theme => ApplyTheme(theme);
            _session.HistoryCleared += () => OnCommandAssistHistoryCleared?.Invoke();
            _session.SnippetsChanged += () => OnCommandAssistSnippetsChanged?.Invoke();
        }

        /// <summary>
        /// Validates every page before anything is written, so a refused Save leaves the in-memory
        /// settings untouched (the old window wrote every field first and validated shortcuts last).
        /// </summary>
        private void SaveAndClose()
        {
            foreach (ISettingsPage page in _pages)
            {
                if (page.Validate() is { } error)
                {
                    NavigateTo(new SettingsTarget(error.Page, error.RowId));
                    return;
                }
            }

            TerminalSettings target = _session.Settings;
            foreach (ISettingsPage page in _pages)
            {
                page.Collect(target);
            }

            _session.CommitDrafts(target);
            target.Save();
            Close(true); // Return true to indicate saved
        }

        // ORIG 1218-1242: the I1 doc comment, verbatim except for these two edits:
        //   - replace its "Mirrors the exact sequence the constructor itself runs" sentence with
        //     "Reloads the session, then runs every page's Load in registry order";
        //   - change its cref to SaveAndClose into plain text.
        internal async Task ReloadSettingsAfterExternalChangeAsync()
        {
            // F1: record that configuration was replaced externally so MainWindow.OpenSettings can
            // adopt the reload no matter how this dialog eventually closes.
            ConfigurationReplacedExternally = true;

            _session.Reload(TerminalSettings.Load());
            foreach (ISettingsPage page in _pages)
            {
                page.Load(_session);
            }

            // ORIG 1301-1314, the snippet reload comment, verbatim.
            await GetPage<CommandAssistPage>().ReloadSnippetsAsync();

            // ORIG 1317-1330, the Codex P2 comment, verbatim.
            _session.RaiseSnippetsChanged();
        }

        private void ApplyTheme(TerminalTheme? theme = null)
        {
            theme ??= _session.Settings.ActiveTheme;

            var contrastColor = theme.GetContrastForeground();
            ThemePaletteResources.Apply(Resources, theme);

            Background = new SolidColorBrush(theme.Background.ToAvaloniaColor());
            Foreground = new SolidColorBrush(contrastColor.ToAvaloniaColor());

            // Set the window theme variant so standard controls (ComboBox, ScrollBar, etc.) adapt
            RequestedThemeVariant = contrastColor == TermColor.Black ? ThemeVariant.Light : ThemeVariant.Dark;
        }
    }
}
```

Then delete `src/Ntilde.App/SettingsSection.cs`.

- [ ] **Step 5: Update `MainWindow.axaml.cs`.** Add `using Ntilde.Settings;`, then make these changes.

1. Next to the other Settings fields (for example above `OpenSettings`), add:

```csharp
        /// <summary>The Settings page on screen when Settings last closed, this run only. Ctrl+, reopens there.</summary>
        private SettingsPage? _lastSettingsPage;
```

2. Replace `CustomizeTitleBarSettingsTarget`, `OpenSettingsToBackupPage` and the head of `OpenSettings` (from `private static (int TabIndex, SettingsSection Section) CustomizeTitleBarSettingsTarget()` through `sw.SelectBackupPage(); }`) with the block below. Keep the existing XML doc of `CustomizeTitleBarSettingsTarget`, edited to say "target" instead of "(tab index, section)".

```csharp
        internal static SettingsTarget CustomizeTitleBarSettingsTarget()
            => new(SettingsPage.Appearance, SettingsRowIds.TitleBar);

        /// <summary>Where "Manage Profiles…" opens Settings.</summary>
        internal static SettingsTarget ManageProfilesSettingsTarget() => new(SettingsPage.Profiles);

        /// <summary>Where the three Backup palette entries open Settings.</summary>
        internal static SettingsTarget BackupSettingsTarget() => new(SettingsPage.Backup);

        /// <summary>Where Ctrl+, the palette's "Settings" and the title-bar button open Settings: the page last shown in this run, else the default.</summary>
        internal SettingsTarget DefaultSettingsTarget() => new(_lastSettingsPage ?? SettingsPageRegistry.DefaultPage);

        // Keep the existing OpenSettingsToBackupPage doc comment, with its last two sentences
        // replaced by: "The page is named by BackupSettingsTarget, not an index."
        private void OpenSettingsToBackupPage()
        {
            _ = OpenSettings(BackupSettingsTarget());
        }

        private async Task OpenSettings(SettingsTarget target)
        {
            var sw = new SettingsWindow(target);
```

   The rest of `OpenSettings` stays exactly as it is.

3. At the top of `ApplySettingsWindowResult`, before the `if (saved || …)`, add:

```csharp
            // Ctrl+, reopens where the user left off, whichever way the dialog closed.
            _lastSettingsPage = sw.CurrentPage;
```

4. Update the callers:
   - `await OpenSettings(1);` (Manage Profiles) becomes `await OpenSettings(ManageProfilesSettingsTarget());`.
   - In the Customize Title Bar handler, the two lines `var (tabIndex, section) = CustomizeTitleBarSettingsTarget(); await OpenSettings(tabIndex, section: section);` become `await OpenSettings(CustomizeTitleBarSettingsTarget());`. Trim the comment above them to its first sentence.
   - The three `OpenSettings(0)` calls (the Ctrl+, handler, the palette "Settings" command, and `["settings"]` in the title-bar action map) become `OpenSettings(DefaultSettingsTarget())`.

- [ ] **Step 6: Migrate the tests.**

**(a) Delete** `SettingsWindowSelectBackupPageTests.cs`, `SettingsWindowTitleBarSectionTests.cs` and `MainWindowCustomizeTitleBarSettingsTargetTests.cs`. Their coverage moves into the two new files in (h) and (i).

**(b) `SettingsWindowBackupSectionTests.cs`:**
- Add `using Ntilde.Settings.Pages;`.
- Delete the four navigation tests: `BackupTab_IsLastTab_AndDataNavHasOneItem`, `AllNavItems_SelectDistinctTabs_AndSyncRoundTripsCleanly`, `SyncSidebarFromTabs_EveryTabIndex_SelectsExactlyOneSidebarItem` and `ClickingDataNav_SelectsTheBackupTab_AndOtherSidebarItemsStillSelectTheirOwnPage`. `SettingsWindowFrameTests` covers navigation.
- Rewrite the class summary to describe the Backup page alone.
- Then make these replacements in every remaining test:

| Old | New |
|---|---|
| `window.FindControl<…>("BtnBackupExport"/"BtnBackupImport"/"BackupStatusText"/"SnapshotList"/"BtnRestoreSnapshot")` | `window.GetPage<BackupPage>().FindControl<…>(…)` |
| `Assert.NotNull(window.FindControl<ListBox>("DataNav"));` | delete the line |
| `window.FindControl<ListBox>("ProfilesListBox")`, `window.FindControl<TextBox>("ProfileNameInput")` | `window.GetPage<ProfilesPage>().FindControl<…>(…)` |
| `window.FindControl<StackPanel>("CommandAssistSnippetsPanel")` (in `SnippetRowNames`) | `window.GetPage<CommandAssistPage>().FindControl<StackPanel>("CommandAssistSnippetsPanel")` |
| `window.GetLogicalDescendants()` (password copy test) | `window.GetPage<BackupPage>().GetLogicalDescendants()`, and drop the comment about tab 6's visual tree |
| body of `InvokeReloadCommandAssistSnippetsAsync` | `await window.GetPage<CommandAssistPage>().ReloadSnippetsAsync();` |
| body of `InvokeReloadAfterExternalChangeAsync` | `await window.ReloadSettingsAfterExternalChangeAsync();` (and drop "Reflection" from its doc) |
| reflection on `BuildRestoreConfirmationText` | `var (headline, body) = BackupPage.BuildRestoreConfirmationText((BackupPage.SnapshotRow)row);` |
| reflection on `BuildImportModeBodyText` | `string withSnippetsText = BackupPage.BuildImportModeBodyText(withSnippets);` (same for `withoutSnippets`) |

- `BtnSave` stays `window.FindControl<Button>("BtnSave")`.
- `RestoreConfirmationOverride` in object initializers stays as it is.
- Remove `using System.Reflection;` if nothing uses it any more.

**(c) `SettingsWindowTitleBarShortcutRefreshTests.cs`.** Replace the class body with the code below. It covers both the session path and the real Shortcuts-page path.

```csharp
public sealed class SettingsWindowTitleBarShortcutRefreshTests
{
    [AvaloniaFact]
    public void ShortcutsChanged_RefreshesTheTitleBarRows_AndKeepsAnEarlierPlacementChange()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        SettingsSession session = window.Session;

        // "find" defaults to Overflow, so pinning it is a real change made before the rebind.
        Assert.True(session.TitleBarDraft.TrySetState("find", TitleBarItemState.Pinned));
        session.ShortcutDraft["settings"] = "Ctrl+Alt+S";
        session.RaiseShortcutsChanged();

        var panel = TitleBarRowsPanel(window);
        var (title, shortcut) = FindRowByTitle(panel, "Settings");
        Assert.Equal("Settings", title.Text);
        Assert.Equal("Ctrl+Alt+S", shortcut.Text);
        Assert.Equal(TitleBarItemState.Pinned, session.TitleBarDraft.GetState("find"));
        Assert.Equal("Find in Terminal", FindRowByTitle(panel, "Find in Terminal").Title.Text);
    }

    [AvaloniaFact]
    public void RebindingOnTheShortcutsPage_UpdatesTheTitleBarRowsAndTheCommandAssistText()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        var shortcuts = window.GetPage<ShortcutsPage>();

        SettingsPageTests.PressChord(SettingsPageTests.BindingEditorFor(shortcuts, "Settings"), Key.F9, KeyModifiers.Alt);
        SettingsPageTests.PressChord(SettingsPageTests.BindingEditorFor(shortcuts, "Command Assist History"), Key.F8, KeyModifiers.Alt);

        Assert.Equal(ShortcutMatcher.Format(Key.F9, KeyModifiers.Alt), FindRowByTitle(TitleBarRowsPanel(window), "Settings").Shortcut.Text);
        string history = window.GetPage<CommandAssistPage>().FindControl<TextBlock>("CommandAssistHistoryDesc")!.Text!;
        Assert.Contains(ShortcutMatcher.Format(Key.F8, KeyModifiers.Alt), history);
        Assert.DoesNotContain("Ctrl+R", history);
    }

    /// <summary>Where the title-bar rows live. PR 3 moves them to Tabs & title bar; PR 4 rebuilds them.</summary>
    private static StackPanel TitleBarRowsPanel(Ntilde.SettingsWindow window)
        => window.GetPage<AppearancePage>().FindControl<StackPanel>("TitleBarItemsPanel")!;

    // FindRowByTitle stays exactly as it was.
}
```

Update its usings to: `Avalonia.Controls`, `Avalonia.Headless.XUnit`, `Avalonia.Input`, `Ntilde.Settings`, `Ntilde.Settings.Pages`, `Ntilde.Shell.Shortcuts`, `Ntilde.Shell.TitleBar`. Rewrite the class summary in two sentences: shortcut edits reach every page that prints a chord through `SettingsSession.ShortcutsChanged`, and a placement change survives the rebuild.

**(d) `ThemeApplicationRegressionTests.SettingsWindow_CardRows_UseTheWindowPaletteBrushes`.**
- `window.FindControl<StackPanel>("TitleBarItemsPanel")` becomes `window.GetPage<AppearancePage>().FindControl<StackPanel>("TitleBarItemsPanel")`.
- `window.FindControl<StackPanel>("ShortcutBindingsPanel")` becomes `window.GetPage<ShortcutsPage>().FindControl<StackPanel>("ShortcutBindingsPanel")`.
- Wrap the test in `using var root = SettingsTestRoot.Empty();`, and add `using Ntilde.Settings.Pages;`.

**(e) `SettingsWindowUiScaleTests`:** in both `MeasureScale(settings.FindControl<TabControl>("MainTabs")!, settings)` calls, `TabControl`/`"MainTabs"` becomes `Panel`/`"PageHost"`.

**(f) `MainWindowBackupPaletteTests.ImportReplace_ThenCancelSettings_ThenOrdinarySave_KeepsTheImportedConfiguration`:** replace the four reflection lines with `await settingsWindow.ReloadSettingsAfterExternalChangeAsync();`. Change "same reflection seam" in its doc to "same seam".

**(g) Static-helper tests: change the type name only.**

| File | Old | New |
|---|---|---|
| `SettingsWindowFontChoicesTests` | `SettingsWindow.BuildFontFamilyChoices` | `SettingsSession.BuildFontFamilyChoices` |
| `SettingsWindowProfileSeparationTests` | `SettingsWindow.BuildLocalProfilesForEditor` / `NormalizeSettingsProfilesForSave` / `ResolveDefaultLocalProfileId` | `ProfilesPage.…` |
| `SettingsWindowShortcutFilteringTests` | `SettingsWindow.FilterShortcutCatalogEntries` | `ShortcutsPage.FilterShortcutCatalogEntries` |
| `SettingsWindowShortcutTextTests` | `SettingsWindow.SubstituteChords` | `CommandAssistPage.SubstituteChords` |

Add `using Ntilde.Settings;` or `using Ntilde.Settings.Pages;` as each needs.

**(h) Create `SettingsWindowFrameTests.cs`:**

```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ntilde.Settings;
using Ntilde.Settings.Pages;
using Ntilde.Shell.Shortcuts;

namespace Ntilde.Tests.Core;

/// <summary>
/// The frame: registry, sidebar, page switching, deep links and Save's validate-first order.
/// Replaces the old tab-offset tests: with one list built from the registry there are no offsets
/// left to get wrong, so what is pinned here is the registry contract itself.
/// </summary>
public sealed class SettingsWindowFrameTests
{
    [Fact]
    public void Registry_HasExactlyOneDescriptorPerPage()
    {
        foreach (SettingsPage page in Enum.GetValues<SettingsPage>())
        {
            Assert.Single(SettingsPageRegistry.Pages, descriptor => descriptor.Page == page);
        }

        Assert.Equal(Enum.GetValues<SettingsPage>().Length, SettingsPageRegistry.Pages.Count);
    }

    [AvaloniaFact]
    public void Sidebar_ListsGroupsAndPagesInRegistryOrder()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        string[] expected =
        [
            "INTERFACE", "Appearance", "Shells & Profiles", "Shortcuts",
            "ASSISTANT", "Command Assist", "Agent Access",
            "CONNECTION", "SSH",
            "DATA", "Backup & Restore",
        ];

        Assert.Equal(expected, SidebarLabels(window));
    }

    [AvaloniaFact]
    public void Sidebar_GroupHeadersAreNeverSelected()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Shortcuts));
        var nav = window.FindControl<ListBox>("SideNav")!;
        int shortcuts = nav.Items.IndexOf(window.NavItem(SettingsPage.Shortcuts));

        nav.SelectedIndex = shortcuts + 1; // the ASSISTANT header, travelling down
        Assert.Equal(SettingsPage.CommandAssist, window.CurrentPage);

        nav.SelectedIndex = nav.Items.IndexOf(window.NavItem(SettingsPage.CommandAssist)) - 1; // the same header, travelling up
        Assert.Equal(SettingsPage.Shortcuts, window.CurrentPage);

        Assert.All(nav.Items.OfType<ListBoxItem>().Where(item => item.Tag is not SettingsPage), header => Assert.False(header.IsEnabled));
        Assert.Same(window.NavItem(SettingsPage.Shortcuts), nav.SelectedItem);
    }

    [AvaloniaFact]
    public void SelectingANavItem_ShowsExactlyThatPage()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        var nav = window.FindControl<ListBox>("SideNav")!;

        foreach (SettingsPageDescriptor descriptor in SettingsPageRegistry.Pages)
        {
            nav.SelectedItem = window.NavItem(descriptor.Page);

            Assert.Equal(descriptor.Page, window.CurrentPage);
            foreach (SettingsPageDescriptor other in SettingsPageRegistry.Pages)
            {
                Assert.Equal(other.Page == descriptor.Page, window.GetPage(other.Page).View.IsVisible);
            }
        }
    }

    [AvaloniaFact]
    public void Constructing_WithATarget_OpensThatPage()
    {
        using var root = SettingsTestRoot.Empty();
        foreach (SettingsPage page in Enum.GetValues<SettingsPage>())
        {
            var window = new Ntilde.SettingsWindow(new SettingsTarget(page));

            Assert.Equal(page, window.CurrentPage);
            Assert.Same(window.NavItem(page), window.FindControl<ListBox>("SideNav")!.SelectedItem);
        }
    }

    [AvaloniaFact]
    public void ParameterlessConstructor_OpensTheDefaultPage()
    {
        using var root = SettingsTestRoot.Empty();

        Assert.Equal(SettingsPageRegistry.DefaultPage, new Ntilde.SettingsWindow().CurrentPage);
    }

    /// <summary>
    /// The deep-link effect itself: with the row scrolled out of view first, revealing it brings it
    /// back into the viewport and starts the highlight. (At the default size the title-bar row
    /// already sits inside the first viewport, so without scrolling away this would pass even if
    /// the scroll did nothing.)
    /// </summary>
    [AvaloniaFact]
    public void RevealRow_ScrollsTheRowIntoView_AndHighlightsIt()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Appearance));
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var header = window.GetPage<AppearancePage>().FindControl<TextBlock>("TitleBarSectionHeader")!;
            ScrollViewer scroller = header.GetVisualAncestors().OfType<ScrollViewer>().First();
            double top = header.Bounds.Top, bottom = header.Bounds.Bottom;
            scroller.Offset = new Vector(0, scroller.Extent.Height);
            Dispatcher.UIThread.RunJobs();
            Assert.True(scroller.Offset.Y > bottom + 100, "Setup did not scroll the row out of view.");

            Assert.True(window.RevealRow(SettingsPage.Appearance, SettingsRowIds.TitleBar));
            Dispatcher.UIThread.RunJobs();

            Assert.InRange(scroller.Offset.Y, top - 40, bottom + 40);
            Assert.Same(window.FindResource("NtBlueDim"), header.Background);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Constructing_WithARowTarget_HighlightsTheRowOnceOpen()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Appearance, SettingsRowIds.TitleBar));
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var header = window.GetPage<AppearancePage>().FindControl<TextBlock>("TitleBarSectionHeader")!;
            Assert.Same(window.FindResource("NtBlueDim"), header.Background);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Review focus 4. A duplicate shortcut refuses Save, shows Shortcuts and writes nothing.
    /// Once the user fixes the duplicate, the next Save writes the edit they had made elsewhere.
    /// </summary>
    [AvaloniaFact]
    public void DuplicateShortcut_BlocksSaveWithoutWriting_AndTheFixedSaveKeepsEarlierEdits()
    {
        using var root = SettingsTestRoot.WithSettings("""{"FontSize":14}""");
        var window = new Ntilde.SettingsWindow();
        var app = ShortcutCatalog.GetEntries()
            .Where(e => e.Scope == ShortcutScope.App && !string.IsNullOrEmpty(e.DefaultBinding))
            .ToList();
        window.Session.ShortcutDraft[app[1].CommandId] = app[0].DefaultBinding;
        FontSizeInput(window).Value = 19;

        ClickSave(window);

        Assert.Equal(SettingsPage.Shortcuts, window.CurrentPage);
        Assert.True(window.GetPage<ShortcutsPage>().FindControl<TextBlock>("ShortcutValidationMessage")!.IsVisible);
        Assert.Equal(14, window.Session.Settings.FontSize); // nothing collected before validation
        using (var unchanged = root.ReadSettings())
        {
            Assert.Equal(14, unchanged.RootElement.GetProperty("FontSize").GetInt32());
        }

        window.Session.ShortcutDraft.Remove(app[1].CommandId);
        ClickSave(window);

        using var saved = root.ReadSettings();
        Assert.Equal(19, saved.RootElement.GetProperty("FontSize").GetInt32());
    }

    /// <summary>Where the font size lives. PR 3 moves it to the Terminal page.</summary>
    internal static NumericUpDown FontSizeInput(Ntilde.SettingsWindow window)
        => window.GetPage<AppearancePage>().FindControl<NumericUpDown>("FontSizeInput")!;

    internal static void ClickSave(Ntilde.SettingsWindow window)
        => window.FindControl<Button>("BtnSave")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    internal static List<string> SidebarLabels(Ntilde.SettingsWindow window)
        => window.FindControl<ListBox>("SideNav")!.Items.OfType<ListBoxItem>()
            .Select(item => item.Content switch
            {
                TextBlock header => header.Text!,
                Grid row => row.Children.OfType<TextBlock>().Last().Text!,
                _ => "?",
            })
            .ToList();
}
```

**(i) Create `MainWindowSettingsTargetTests.cs`:**

```csharp
using Avalonia.Headless.XUnit;
using Ntilde.Settings;

namespace Ntilde.Tests.Core;

/// <summary>
/// Where each MainWindow entry point opens Settings, and the remembered last page. The targets are
/// plain methods because the entry points themselves end in Window.ShowDialog, which never returns
/// in the headless host (see MainWindowShellExitTests).
/// </summary>
public sealed class MainWindowSettingsTargetTests : IDisposable, IClassFixture<TestAppDataRoot>
{
    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

    [AvaloniaFact] // touching MainWindow's statics from a plain [Fact] can poison the headless run
    public void EntryPoints_OpenTheirPages()
    {
        Assert.Equal(new SettingsTarget(SettingsPage.Profiles), Ntilde.MainWindow.ManageProfilesSettingsTarget());
        Assert.Equal(new SettingsTarget(SettingsPage.Backup), Ntilde.MainWindow.BackupSettingsTarget());
        Assert.Equal(new SettingsTarget(SettingsPage.Appearance, SettingsRowIds.TitleBar), Ntilde.MainWindow.CustomizeTitleBarSettingsTarget());
    }

    [AvaloniaFact]
    public void DefaultTarget_IsTheRegistryDefault_UntilSettingsHasClosedOnce()
    {
        var window = TestMainWindowFactory.Create();

        Assert.Equal(new SettingsTarget(SettingsPageRegistry.DefaultPage), window.DefaultSettingsTarget());
    }

    [AvaloniaFact]
    public void DefaultTarget_IsThePageSettingsLastClosedOn_WhicheverWayItClosed()
    {
        var window = TestMainWindowFactory.Create();
        var snapshot = new Ntilde.MainWindow.PreviewSnapshot(1.0, "Acrylic", "", 0.5, "UniformToFill", "Consolas", 14, "Default", 1.0);

        window.ApplySettingsWindowResult(new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Shortcuts)), saved: false, snapshot);
        Assert.Equal(SettingsPage.Shortcuts, window.DefaultSettingsTarget().Page);

        window.ApplySettingsWindowResult(new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Ssh)), saved: true, snapshot);
        Assert.Equal(SettingsPage.Ssh, window.DefaultSettingsTarget().Page);
    }
}
```

- [ ] **Step 7: Build, then run every Settings and MainWindow test.** Use the targeted run twice: `<ClassName>` = `Settings`, then `MainWindow`.

Expected: all PASS, **including `SettingsWindowRoundTripTests` with no edits**.

**If the round trip fails:** the path in the message names the field. Find the page that owns it and compare its `Load` and `Collect` with `ORIG` `LoadCurrentSettings` (3058-3214) and `SaveAndClose` (3346-3443).

- [ ] **Step 8: Check that nothing still uses the old API.**

```bash
git grep -nE 'SettingsSection|SelectBackupPage|MainTabs|InterfaceNav|AssistantNav|ConnectionNav|DataNav|ScrollToTitleBarSection|OpenSettings\([0-9]' -- src tests
```

Expected: no output. (Specs and plans under `docs/` may still mention these names.)

- [ ] **Step 9: Commit.**

```bash
git add -A src/Ntilde.App tests/Ntilde.App.Tests
git commit -m "refactor(settings): Settings window becomes a frame hosting one control per page" -m "The seven tabs move one-to-one into ISettingsPage controls sharing a SettingsSession. The sidebar is one list built from SettingsPageRegistry, entry points pass a SettingsTarget, and Save validates every page before writing anything." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

## Task 2.11: Keyboard (Ctrl/Cmd+S, sidebar arrows)

**Files:**
- Modify: `src/Ntilde.App/SettingsWindow.axaml.cs`
- Create: `tests/Ntilde.App.Tests/Core/SettingsWindowKeyboardTests.cs`

**Interfaces:** `internal static KeyModifiers SettingsWindow.PrimaryModifier`, which is Meta on macOS and Control elsewhere.

- [ ] **Step 1: Write the failing tests.**

```csharp
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ntilde.Settings;
using Ntilde.Settings.Pages;

namespace Ntilde.Tests.Core;

public sealed class SettingsWindowKeyboardTests
{
    private static RawInputModifiers Primary => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    [AvaloniaFact]
    public void PrimaryS_SavesAndCloses()
    {
        using var root = SettingsTestRoot.WithSettings("""{"FontSize":14}""");
        var window = new Ntilde.SettingsWindow();
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        SettingsWindowFrameTests.FontSizeInput(window).Value = 19;

        window.KeyPress(Key.S, Primary, PhysicalKey.S, "s");
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed);
        using var saved = root.ReadSettings();
        Assert.Equal(19, saved.RootElement.GetProperty("FontSize").GetInt32());
    }

    /// <summary>Review focus 2: a number typed but not yet committed is what gets saved.</summary>
    [AvaloniaFact]
    public void PrimaryS_SavesAnEditStillPendingInANumberBox()
    {
        using var root = SettingsTestRoot.WithSettings("""{"FontSize":14}""");
        var window = new Ntilde.SettingsWindow();
        try
        {
            window.Show();
            TextBox text = SettingsWindowFrameTests.FontSizeInput(window).GetVisualDescendants().OfType<TextBox>().First();
            text.Focus();
            text.SelectAll();
            window.KeyTextInput("23");
            Dispatcher.UIThread.RunJobs();

            window.KeyPress(Key.S, Primary, PhysicalKey.S, "s");
            Dispatcher.UIThread.RunJobs();

            using var saved = root.ReadSettings();
            Assert.Equal(23, saved.RootElement.GetProperty("FontSize").GetInt32());
        }
        finally
        {
            if (window.IsVisible)
            {
                window.Close();
            }
        }
    }

    /// <summary>
    /// The chord goes to the focused binding editor, which records it, or reports a clash. Either
    /// way the window must not save: had the editor been skipped, Save would have closed the window.
    /// </summary>
    [AvaloniaFact]
    public void PrimaryS_InAShortcutEditor_IsLeftToTheEditor()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Shortcuts));
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            TextBox editor = SettingsPageTests.BindingEditorFor(window.GetPage<ShortcutsPage>(), "Settings");
            editor.Focus();

            window.KeyPress(Key.S, Primary, PhysicalKey.S, "s");
            Dispatcher.UIThread.RunJobs();

            Assert.False(closed);
            Assert.Equal(SettingsPage.Shortcuts, window.CurrentPage);
        }
        finally
        {
            if (window.IsVisible)
            {
                window.Close();
            }
        }
    }

    /// <summary>Review focus 3: arrows pass over group headers in both directions.</summary>
    [AvaloniaFact]
    public void ArrowKeysInTheSidebar_PassOverGroupHeaders()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Shortcuts));
        try
        {
            window.Show();
            window.NavItem(SettingsPage.Shortcuts).Focus();

            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(SettingsPage.CommandAssist, window.CurrentPage);

            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(SettingsPage.Shortcuts, window.CurrentPage);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CancelButton_SaysWhatItDoesNotUndo()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();

        Assert.Equal(
            "Reverts preview changes. Themes, snippets, history and backups are saved as you change them.",
            ToolTip.GetTip(window.FindControl<Button>("BtnCancel")!));
    }
}
```

- [ ] **Step 2: Run them and watch them fail.** Use the targeted run with `SettingsWindowKeyboardTests`.

Expected:
- `PrimaryS_SavesAndCloses` and `PrimaryS_SavesAnEditStillPendingInANumberBox` fail, because nothing saves yet.
- The editor test passes already, since nothing saves at all. It becomes the guard for the editor exemption once the handler exists.
- The arrow test may already pass if Avalonia skips disabled items. That is fine: the explicit handler below guarantees it.
- The tooltip test passes, because Task 2.10 added the tooltip.

- [ ] **Step 3: Add the handlers.** In `SettingsWindow.axaml.cs`, add `using Avalonia.Input;` and `using Avalonia.Interactivity;`. In the constructor, after `BuildSidebar();`, add:

```csharp
            // Tunnel, so the chord works from any focused field; ShortcutsPage's editors are the
            // one place it must not fire (the user may be recording Ctrl+S as a binding).
            AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
            _sideNav.AddHandler(KeyDownEvent, OnSideNavKeyDown, RoutingStrategies.Tunnel);
```

Then add the members:

```csharp
        /// <summary>The platform's command modifier: Cmd on macOS, Ctrl elsewhere.</summary>
        internal static KeyModifiers PrimaryModifier => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

        private void OnWindowKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyModifiers != PrimaryModifier || IsShortcutCapture(e.Source))
            {
                return;
            }

            if (e.Key == Key.S)
            {
                e.Handled = true;

                // Moving focus first commits an edit still pending in a NumericUpDown or TextBox,
                // exactly as clicking the button would.
                _btnSave.Focus();
                SaveAndClose();
            }
        }

        private static bool IsShortcutCapture(object? source)
            => source is TextBox box && box.Classes.Contains(ShortcutsPage.CaptureClass);

        /// <summary>↑ / ↓ move between pages directly, so a group header is never a stop.</summary>
        private void OnSideNavKeyDown(object? sender, KeyEventArgs e)
        {
            int step = e.Key switch { Key.Down => 1, Key.Up => -1, _ => 0 };
            if (step == 0 || e.KeyModifiers != KeyModifiers.None)
            {
                return;
            }

            e.Handled = true;
            List<SettingsPage> order = SettingsPageRegistry.Pages.Select(descriptor => descriptor.Page).ToList();
            int next = order.IndexOf(_currentPage) + step;
            if (next < 0 || next >= order.Count)
            {
                return;
            }

            ShowPage(order[next]);
            _navItems[order[next]].Focus(NavigationMethod.Directional);
        }
```

- [ ] **Step 4: Run the keyboard and frame tests.** Use the targeted run with `SettingsWindowKeyboardTests`, then `SettingsWindowFrameTests`.

Expected: PASS.

**If `PrimaryS_SavesAnEditStillPendingInANumberBox` fails with 14:** the focus move did not commit the NumericUpDown. Before calling `SaveAndClose` in the handler, commit any NumericUpDown in the current page whose text differs from its value:

```csharp
foreach (NumericUpDown number in GetPage(_currentPage).View.GetLogicalDescendants().OfType<NumericUpDown>())
{
    if (decimal.TryParse(number.Text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.CurrentCulture, out decimal typed))
    {
        number.Value = typed;
    }
}
```

Keep the focus move as well.

- [ ] **Step 5: Commit.**

```bash
git add src/Ntilde.App/SettingsWindow.axaml.cs tests/Ntilde.App.Tests/Core/SettingsWindowKeyboardTests.cs
git commit -m "feat(settings): Ctrl/Cmd+S saves, and sidebar arrows skip group headers" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

## Task 2.12: Guard: Settings XAML references the palette dynamically

**Files:**
- Create: `tests/Ntilde.Architecture.Tests/SettingsXamlResourceTests.cs`

**Interfaces:** `internal static IEnumerable<string> SettingsXamlResourceTests.MarkupExtensionNames(string attributeValue)`, a small parser with its own tests.

- [ ] **Step 1: Write the guard and the parser's tests.** The parser comes first in this file; the guard test then uses it.

```csharp
using System.Xml.Linq;

namespace Ntilde.Architecture.Tests;

/// <summary>
/// Every .axaml under src/Ntilde.App/Settings must reference resources with DynamicResource.
/// </summary>
/// <remarks>
/// A Settings page loads its XAML in its own constructor, before it joins the window, so a
/// StaticResource there cannot see the window's palette. Depending on where it sits, it either
/// throws at load or quietly resolves to nothing. NtStyles has the same problem one level up.
/// This parses each attribute's markup extensions (nested ones included) rather than searching for
/// a spelling, so a prefixed or nested StaticResource is caught too.
/// </remarks>
public class SettingsXamlResourceTests
{
    [Fact]
    public void SettingsXaml_ReferencesResourcesDynamically()
    {
        string settingsDirectory = Path.Combine(RepoRoot(), "src", "Ntilde.App", "Settings");
        var violations = new List<string>();

        foreach (string file in Directory.EnumerateFiles(settingsDirectory, "*.axaml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(RepoRoot(), file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            XDocument document = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (XElement element in document.Descendants())
            {
                if (element.Name.LocalName is "StaticResource" or "StaticResourceExtension")
                {
                    violations.Add($"{relative}:{((System.Xml.IXmlLineInfo)element).LineNumber}: <{element.Name.LocalName}> element");
                }

                foreach (XAttribute attribute in element.Attributes())
                {
                    if (MarkupExtensionNames(attribute.Value).Any(name => name is "StaticResource" or "StaticResourceExtension"))
                    {
                        violations.Add($"{relative}:{((System.Xml.IXmlLineInfo)attribute).LineNumber}: {attribute.Name.LocalName}=\"{attribute.Value}\"");
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Settings XAML must use {DynamicResource …}; a page loads before it joins the window:\n" + string.Join("\n", violations));
    }

    [Theory]
    [InlineData("{StaticResource NtFg}", "StaticResource")]
    [InlineData("{x:StaticResource NtFg}", "StaticResource")]
    [InlineData("{DynamicResource NtFg}", "DynamicResource")]
    [InlineData("{Binding Path, Converter={StaticResource Conv}}", "Binding,StaticResource")]
    [InlineData("{}{StaticResource is literal}", "")]
    [InlineData("plain text", "")]
    public void MarkupExtensionNames_FindsEveryExtension_IncludingNestedOnes(string value, string expected)
    {
        Assert.Equal(expected, string.Join(",", MarkupExtensionNames(value)));
    }

    /// <summary>
    /// The markup extension names an attribute value invokes, outermost first. A value starting with
    /// "{}" is an escaped literal and invokes none.
    /// </summary>
    internal static IEnumerable<string> MarkupExtensionNames(string value)
    {
        string trimmed = value.TrimStart();
        if (!trimmed.StartsWith('{') || trimmed.StartsWith("{}", StringComparison.Ordinal))
        {
            yield break;
        }

        for (int i = 0; i < trimmed.Length; i++)
        {
            if (trimmed[i] != '{')
            {
                continue;
            }

            int end = trimmed.IndexOfAny([' ', '}', ',', '\t', '\r', '\n'], i + 1);
            string name = end < 0 ? trimmed[(i + 1)..] : trimmed[(i + 1)..end];
            int colon = name.IndexOf(':');
            yield return colon >= 0 ? name[(colon + 1)..] : name;
        }
    }

    // Same walk-up as CompiledBindingTests.RepoRoot().
    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ntilde.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }
}
```

- [ ] **Step 2: Check that the guard can fail.** Temporarily change one `{DynamicResource NtFg3}` in `SshPage.axaml` to `{StaticResource NtFg3}`, then run:

```powershell
scripts/build.ps1 test tests/Ntilde.Architecture.Tests --filter "FullyQualifiedName~SettingsXamlResourceTests" *> "$env:TEMP\settings-arch.log"; Select-String -Path "$env:TEMP\settings-arch.log" -Pattern "Passed!|Failed!|SshPage"
```

Expected: FAIL, naming `SshPage.axaml` and the line. **Revert the edit**, run again, and expect PASS.

- [ ] **Step 3: Commit.**

```bash
git add tests/Ntilde.Architecture.Tests/SettingsXamlResourceTests.cs
git commit -m "test(arch): Settings XAML must reference resources dynamically" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

## Task 2.13: Screenshot harness on the registry, the gate, and PR 2

**Files:**
- Modify: `tests/Ntilde.App.Tests/Core/SettingsScreenshotHarness.cs` (`CaptureSettings`)

- [ ] **Step 1: Walk the registry instead of the TabControl.** Replace the body of `CaptureSettings`'s `try` block, and add `using Ntilde.Settings;`:

```csharp
            window.Show();
            foreach (SettingsPageDescriptor descriptor in SettingsPageRegistry.Pages)
            {
                window.NavigateTo(new SettingsTarget(descriptor.Page));
                written.AddRange(CaptureScrolled(window, output, $"settings-{slug}-{descriptor.Page}"));
            }
```

- [ ] **Step 2: Take the screenshots for the PR.**

```powershell
$env:NTILDE_SETTINGS_SHOTS = "$env:TEMP\settings-shots\pr2"; scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~SettingsScreenshotHarness&Lane!=PlatformBoot" *> "$env:TEMP\settings-shots.log"; Remove-Item Env:NTILDE_SETTINGS_SHOTS
```

Open them next to `pr1-after`. Expected:
- The page content is identical.
- The sidebar differs, and only in these ways: one list, the footer text gone, and only the selected glyph in the accent colour.

- [ ] **Step 3: Run the full gate** (Conventions). Expected: all green.

- [ ] **Step 4: Commit the harness change.**

```bash
git add tests/Ntilde.App.Tests/Core/SettingsScreenshotHarness.cs
git commit -m "test(settings): screenshot harness walks the page registry" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

- [ ] **Step 5: Give the user these smoke-test steps, and wait for their result.**
  1. Run the dev build.
  2. Press Ctrl+, and confirm Settings opens on Appearance.
  3. Click each sidebar item. Every page should show what the old tab showed.
  4. Use ↑/↓ in the sidebar, crossing from Shortcuts to Command Assist and back.
  5. Change the font size, press Ctrl+S, and confirm the window closes and the terminal font changed.
  6. Reopen with Ctrl+. It should land on the last page you visited.
  7. Right-click the title bar, choose Customize Title Bar…, and confirm Appearance opens scrolled to TITLE BAR with a brief blue highlight.
  8. From the command palette, run "Export configuration…" and confirm it opens Backup & Restore.
  9. Switch theme on Appearance and confirm the whole window recolours, including the title-bar cards and the shortcut rows.

- [ ] **Step 6: Ask the user before pushing.** Then push, and open the PR once they agree:

```bash
git push -u origin feat/settings-redesign-frame
gh pr create --base main --title "Settings redesign 2/5: frame and one-to-one page split" --body-file - <<'EOF'
Second of five PRs for the settings window redesign (spec: docs/superpowers/specs/2026-10-13-settings-window-redesign-design.md).

**What changes**
- `SettingsWindow` is now a frame: one sidebar list built from `SettingsPageRegistry`, every page hosted for the window's lifetime, Save / Cancel footer.
- The seven tabs moved one-to-one into `ISettingsPage` controls under `Settings/Pages/`, sharing a `SettingsSession` (working settings, drafts, stores, preview sink).
- `MainWindow.OpenSettings(SettingsTarget)` replaces the tab index, `SettingsSection` and `SelectBackupPage()`. Ctrl+, reopens the last page visited in this run.
- Deep links scroll to a row and highlight it (`SettingsNavigation.RowId`).

**Visible changes:** one sidebar list (only the selected glyph uses the accent colour), the misleading "Hit ⌘S" sidebar text is gone, Ctrl/Cmd+S now saves, arrow keys skip group headers, Cancel explains what it does not undo.

**Fixed on the way:** a Save refused for duplicate shortcuts no longer changes the in-memory settings. Save now validates every page before collecting any.

**Guards**
- `SettingsWindowRoundTripTests` was written against the old window first (first commit) and passes unchanged: Save without edits leaves settings.json identical.
- `SettingsXamlResourceTests` (Architecture) fails the build if Settings XAML uses StaticResource.
- The eight old window test files were migrated; reflection on private members is gone.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
```

- [ ] **Step 7: Merge only on the user's word,** once CI is green. Then update `settings-redesign-state.md`.

---

# Part 3 — PR 3: Re-homing, the row component, the theme editor sub-page, copy

**Outcome:**
- **Ten pages.**
  - Appearance holds the theme, window and background image.
  - General, Terminal, and Tabs & title bar are new.
- **Shared rows.** Rows are `SettingRow`s inside `SettingGroup`s, with "More" on the six long descriptions.
- **Theme editor.** It is a sub-page.
- **Readable combo labels.** Stored values are unchanged.
- **SSH panel.** The dead panel on Profiles is gone.
- **Docs.** They name the new pages.

## Task 3.0: Branch for PR 3

- [ ] **Step 1: Cut the branch.** PR 2 must have merged first.

```bash
cd /d/projects/nova2/.worktrees/settings-redesign
git fetch origin
git switch -c feat/settings-redesign-pages origin/main
```

## Task 3.1: `SettingRow`, `SettingGroup`, `ComboTags` and their styles

**Files:**
- Create: `src/Ntilde.App/Settings/SettingRow.cs`, `SettingGroup.cs`, `ComboTags.cs`, `SettingsRowChrome.cs`
- Modify: `src/Ntilde.App/Settings/NtStyles.axaml`
- Create: `tests/Ntilde.App.Tests/Core/SettingRowTests.cs`, `tests/Ntilde.App.Tests/Core/ComboTagsTests.cs`

**Interfaces:**
- `public class SettingRow : ContentControl`:
  - styled properties: `Title`, `Description`, `Details`, `Keywords` and `Status` (all `string?`); `Indent`, `Stacked` and `IsExpanded` (all `bool`);
  - CLR property `RowId`, which wraps `SettingsNavigation.RowIdProperty`;
  - template parts `PART_Layout` (Grid), `PART_MoreToggle` (Button), `PART_Details`, `PART_Status` and `PART_ContentPresenter`.
- `public class SettingGroup : ItemsControl`, with a `Header` property (`string?`). A null header shows no title.
- `internal static class ComboTags`, with `Select(ComboBox, string? value, StringComparison, int fallbackIndex)` and `SelectedValue(ComboBox)`.
- `internal static class SettingsRowChrome`, with `CreateRow(Control owner, Control content)`, which returns a hairline `Border`.
- New style classes:
  - `StackPanel.Page` and `StackPanel.PageHeader`;
  - `.Wide` (Width 320);
  - `TextBlock.SliderValue`;
  - `Button.Link`;
  - `Border.ThemePreview`.

- [ ] **Step 1: Write the failing tests.**

`tests/Ntilde.App.Tests/Core/SettingRowTests.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ntilde.Settings;

namespace Ntilde.Tests.Core;

public sealed class SettingRowTests
{
    /// <summary>Hosts <paramref name="content"/> the way a Settings page is hosted: the palette and NtStyles in reach.</summary>
    private static Window Host(Control content)
    {
        var host = new Window { Width = 640, Height = 400, Content = content };
        host.Resources.MergedDictionaries.Add(new NtPalette());
        host.Styles.Add(new NtStyles());
        host.Show();
        Dispatcher.UIThread.RunJobs();
        return host;
    }

    private static T Part<T>(SettingRow row, string name) where T : Control
        => row.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    [AvaloniaFact]
    public void ShowsTitleAndDescription_WithTheContentBesideThem()
    {
        var toggle = new CheckBox();
        var row = new SettingRow { Title = "Window opacity", Description = "Applies when focused.", Content = toggle };
        var host = Host(row);
        try
        {
            var texts = row.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains("Window opacity", texts);
            Assert.Contains("Applies when focused.", texts);
            Assert.Same(row, toggle.GetLogicalParent());
            Assert.Equal(1, Grid.GetColumn(Part<ContentPresenter>(row, "PART_ContentPresenter")));
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public void Details_StayHiddenUntilMore_AndMoreBecomesLess()
    {
        var row = new SettingRow { Title = "Native SSH backend", Description = "Short.", Details = "The long part." };
        var host = Host(row);
        try
        {
            var details = Part<TextBlock>(row, "PART_Details");
            var more = Part<Button>(row, "PART_MoreToggle");
            Assert.False(details.IsVisible);
            Assert.Equal("More", more.Content);

            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.True(row.IsExpanded);
            Assert.True(details.IsVisible);
            Assert.Equal("Less", more.Content);
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public void NoDetails_MeansNoMoreButton()
    {
        var row = new SettingRow { Title = "Font size" };
        var host = Host(row);
        try
        {
            Assert.False(Part<Button>(row, "PART_MoreToggle").IsVisible);
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public void Indent_ShiftsTheRow_AndStacked_PutsTheContentBelow()
    {
        var indented = new SettingRow { Title = "Agent replay export", Indent = true };
        var stacked = new SettingRow { Title = "Remote shell integration", Stacked = true, Content = new Button() };
        var host = Host(new StackPanel { Children = { indented, stacked } });
        try
        {
            Assert.Equal(24, Part<Grid>(indented, "PART_Layout").Margin.Left);
            var presenter = Part<ContentPresenter>(stacked, "PART_ContentPresenter");
            Assert.Equal(1, Grid.GetRow(presenter));
            Assert.Equal(0, Grid.GetColumn(presenter));
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public void RowId_IsTheNavigationAttachedProperty_AndStatusShowsWhenSet()
    {
        var row = new SettingRow { Title = "Remember commands", RowId = "remember-commands" };
        var host = Host(row);
        try
        {
            Assert.Equal("remember-commands", SettingsNavigation.GetRowId(row));
            Assert.False(Part<TextBlock>(row, "PART_Status").IsVisible);

            row.Status = "Command history cleared.";
            Dispatcher.UIThread.RunJobs();

            Assert.True(Part<TextBlock>(row, "PART_Status").IsVisible);
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public void DrawsItsHairlineWithThePaletteBrushInstance()
    {
        var row = new SettingRow { Title = "Blur effect" };
        var host = Host(row);
        try
        {
            Assert.Same(host.FindResource("NtHairline"), row.BorderBrush);
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public void Group_ShowsItsHeader_AndIsItsRowsLogicalParent()
    {
        var first = new SettingRow { Title = "Window opacity" };
        var group = new SettingGroup { Header = "WINDOW", Items = { first } };
        var host = Host(group);
        try
        {
            Assert.Contains(group.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "WINDOW" && t.Classes.Contains("SectionHeader"));
            Assert.Same(group, first.FindLogicalAncestorOfType<SettingGroup>());
        }
        finally
        {
            host.Close();
        }
    }
}
```

`tests/Ntilde.App.Tests/Core/ComboTagsTests.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Ntilde.Settings;

namespace Ntilde.Tests.Core;

public sealed class ComboTagsTests
{
    private static ComboBox Combo(params (string Label, string Value)[] items)
    {
        var combo = new ComboBox();
        foreach (var (label, value) in items)
        {
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        }

        return combo;
    }

    [AvaloniaFact]
    public void Select_MatchesTheStoredValue_NotTheLabel()
    {
        var combo = Combo(("Don't stretch", "None"), ("Fill (crop)", "UniformToFill"));

        ComboTags.Select(combo, "UniformToFill", StringComparison.Ordinal, fallbackIndex: 0);

        Assert.Equal("Fill (crop)", ((ComboBoxItem)combo.SelectedItem!).Content);
        Assert.Equal("UniformToFill", ComboTags.SelectedValue(combo));
    }

    [AvaloniaFact]
    public void Select_UnknownValue_FallsBackOnlyWhenNothingIsSelected()
    {
        var combo = Combo(("Horizontal", "Horizontal"), ("Vertical", "Vertical"));

        ComboTags.Select(combo, "Diagonal", StringComparison.Ordinal, fallbackIndex: 0);
        Assert.Equal("Horizontal", ComboTags.SelectedValue(combo));

        combo.SelectedIndex = 1;
        ComboTags.Select(combo, "Diagonal", StringComparison.Ordinal, fallbackIndex: 0);
        Assert.Equal("Vertical", ComboTags.SelectedValue(combo)); // the old loops kept a prior selection too
    }

    [AvaloniaFact]
    public void Select_HonoursTheComparison()
    {
        var combo = Combo(("Horizontal", "Horizontal"), ("Vertical", "Vertical"));

        ComboTags.Select(combo, "vertical", StringComparison.OrdinalIgnoreCase, fallbackIndex: 0);

        Assert.Equal("Vertical", ComboTags.SelectedValue(combo));
    }
}
```

- [ ] **Step 2: Run them and watch them fail.** Use the targeted run with `SettingRowTests`, then `ComboTagsTests`. Expected: compile errors.

- [ ] **Step 3: Write the four classes.**

`src/Ntilde.App/Settings/SettingRow.cs`:

```csharp
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace Ntilde.Settings;

/// <summary>
/// One setting. The title and a short description sit on the left, with the control on the right
/// (or below, when <see cref="Stacked"/>) and a hairline underneath. Longer explanation goes in
/// <see cref="Details"/>, behind a More / Less button.
/// </summary>
/// <remarks>
/// Search indexes Title, Description and Keywords; deep links and search jump to RowId. The
/// template lives in NtStyles.axaml.
/// </remarks>
public class SettingRow : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Title));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Description));

    /// <summary>Text shown only after "More". At most two lines belong in <see cref="Description"/>.</summary>
    public static readonly StyledProperty<string?> DetailsProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Details));

    /// <summary>Extra words search should match (synonyms, jargon). Never shown.</summary>
    public static readonly StyledProperty<string?> KeywordsProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Keywords));

    /// <summary>Feedback from the row's action ("Snippet saved."), shown under the description when set.</summary>
    public static readonly StyledProperty<string?> StatusProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Status));

    /// <summary>A sub-option of the row above it.</summary>
    public static readonly StyledProperty<bool> IndentProperty =
        AvaloniaProperty.Register<SettingRow, bool>(nameof(Indent));

    /// <summary>The control goes under the text rather than beside it, for wide controls.</summary>
    public static readonly StyledProperty<bool> StackedProperty =
        AvaloniaProperty.Register<SettingRow, bool>(nameof(Stacked));

    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<SettingRow, bool>(nameof(IsExpanded));

    private Button? _moreToggle;

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public string? Description { get => GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    public string? Details { get => GetValue(DetailsProperty); set => SetValue(DetailsProperty, value); }

    public string? Keywords { get => GetValue(KeywordsProperty); set => SetValue(KeywordsProperty, value); }

    public string? Status { get => GetValue(StatusProperty); set => SetValue(StatusProperty, value); }

    public bool Indent { get => GetValue(IndentProperty); set => SetValue(IndentProperty, value); }

    public bool Stacked { get => GetValue(StackedProperty); set => SetValue(StackedProperty, value); }

    public bool IsExpanded { get => GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }

    /// <summary>The id deep links and search use: the same attached property any control can carry.</summary>
    public string? RowId
    {
        get => SettingsNavigation.GetRowId(this);
        set => SettingsNavigation.SetRowId(this, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_moreToggle != null)
        {
            _moreToggle.Click -= OnMoreClick;
        }

        _moreToggle = e.NameScope.Find<Button>("PART_MoreToggle");
        if (_moreToggle != null)
        {
            _moreToggle.Click += OnMoreClick;
        }

        UpdateMoreToggle();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsExpandedProperty || change.Property == TitleProperty)
        {
            UpdateMoreToggle();
        }
    }

    private void OnMoreClick(object? sender, RoutedEventArgs e) => IsExpanded = !IsExpanded;

    private void UpdateMoreToggle()
    {
        if (_moreToggle is null)
        {
            return;
        }

        string verb = IsExpanded ? "Less" : "More";
        _moreToggle.Content = verb;
        AutomationProperties.SetName(_moreToggle, $"{verb} about {Title}");
    }
}
```

`src/Ntilde.App/Settings/SettingGroup.cs`:

```csharp
using Avalonia;
using Avalonia.Controls;

namespace Ntilde.Settings;

/// <summary>
/// A section of a Settings page: an uppercase header and its rows, with the same space before the
/// next section on every page. The rows are its items, so search reads a row's group from its
/// logical parent. A null header shows no title, for a page with a single section.
/// </summary>
public class SettingGroup : ItemsControl
{
    public static readonly StyledProperty<string?> HeaderProperty =
        AvaloniaProperty.Register<SettingGroup, string?>(nameof(Header));

    public string? Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
}
```

`src/Ntilde.App/Settings/ComboTags.cs`:

```csharp
using System;
using System.Linq;
using Avalonia.Controls;

namespace Ntilde.Settings;

/// <summary>
/// Combos that show a readable label but store a fixed value. Each item carries the stored value in
/// Tag and the label in Content. Load and Collect look only at Tag, so relabelling an item can
/// never change what is written to settings.json.
/// </summary>
internal static class ComboTags
{
    /// <summary>
    /// Selects the item whose Tag equals <paramref name="value"/>. With no match, selects
    /// <paramref name="fallbackIndex"/>, but only when nothing is selected yet. That is what the
    /// old Content-matching loops did.
    /// </summary>
    internal static void Select(ComboBox combo, string? value, StringComparison comparison, int fallbackIndex)
    {
        ComboBoxItem? match = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, value, comparison));
        if (match != null)
        {
            combo.SelectedItem = match;
        }
        else if (combo.SelectedItem == null && combo.ItemCount > 0)
        {
            combo.SelectedIndex = fallbackIndex;
        }
    }

    /// <summary>The selected item's stored value; null when nothing is selected.</summary>
    internal static string? SelectedValue(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;
}
```

`src/Ntilde.App/Settings/SettingsRowChrome.cs`:

```csharp
using Avalonia;
using Avalonia.Controls;

namespace Ntilde.Settings;

/// <summary>
/// The hairline-row look SettingRow draws, for rows built in code (shortcut bindings, snippets).
/// The brush is the palette INSTANCE, so a theme switch recolours these rows too.
/// </summary>
internal static class SettingsRowChrome
{
    /// <summary>Vertical padding of a code-built row; SettingRow's own template uses 14 for its taller rows.</summary>
    internal static readonly Thickness RowPadding = new(0, 10);

    internal static Border CreateRow(Control owner, Control content) => new()
    {
        BorderBrush = owner.FindPaletteBrush("NtHairline"),
        BorderThickness = new Thickness(0, 0, 0, 1),
        Padding = RowPadding,
        Child = content,
    };
}
```

- [ ] **Step 4: Add the styles to `NtStyles.axaml`.**
  - Add `xmlns:settings="using:Ntilde.Settings"` to the root `<Styles>` element.
  - Append the block below before `</Styles>`.

```xml
    <!-- ── Page layout (PR 3) ── -->
    <Style Selector="StackPanel.Page">
        <Setter Property="Margin" Value="32,28,32,28"/>
    </Style>
    <Style Selector="StackPanel.PageHeader">
        <Setter Property="Margin" Value="0,0,0,24"/>
    </Style>
    <Style Selector="StackPanel.PageHeader > TextBlock.Subtitle">
        <Setter Property="Margin" Value="0,4,0,0"/>
        <Setter Property="TextWrapping" Value="Wrap"/>
    </Style>

    <!-- Sliders, combos and paths in a SettingRow share one width. -->
    <Style Selector=":is(Control).Wide">
        <Setter Property="Width" Value="320"/>
    </Style>

    <Style Selector="TextBlock.SliderValue">
        <Setter Property="Width" Value="48"/>
        <Setter Property="TextAlignment" Value="Right"/>
        <Setter Property="VerticalAlignment" Value="Center"/>
        <Setter Property="FontFamily" Value="{DynamicResource NtMono}"/>
        <Setter Property="FontSize" Value="12"/>
        <Setter Property="Foreground" Value="{DynamicResource NtFg2}"/>
        <Setter Property="Margin" Value="10,0,0,0"/>
    </Style>

    <!-- Text-only button: "More" / "Less", "‹ Appearance". -->
    <Style Selector="Button.Link">
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="BorderThickness" Value="0"/>
        <Setter Property="Padding" Value="0"/>
        <Setter Property="Foreground" Value="{DynamicResource NtBlue}"/>
        <Setter Property="FontSize" Value="12"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="HorizontalAlignment" Value="Left"/>
    </Style>
    <Style Selector="Button.Link:pointerover /template/ ContentPresenter">
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="Foreground" Value="{DynamicResource NtAccentHover}"/>
    </Style>
    <Style Selector="Button.Link:pressed /template/ ContentPresenter">
        <Setter Property="Background" Value="Transparent"/>
    </Style>

    <!-- Theme sample cards. Their background is the theme's own, set in code. -->
    <Style Selector="Border.ThemePreview">
        <Setter Property="Padding" Value="12"/>
        <Setter Property="BorderBrush" Value="{DynamicResource NtHairline}"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="CornerRadius" Value="6"/>
    </Style>

    <!-- ── SettingRow ── -->
    <Style Selector="settings|SettingRow">
        <Setter Property="Padding" Value="0,14"/>
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="BorderBrush" Value="{DynamicResource NtHairline}"/>
        <Setter Property="BorderThickness" Value="0,0,0,1"/>
        <Setter Property="CornerRadius" Value="4"/>
        <Setter Property="Template">
            <ControlTemplate>
                <Border Background="{TemplateBinding Background}"
                        BorderBrush="{TemplateBinding BorderBrush}"
                        BorderThickness="{TemplateBinding BorderThickness}"
                        CornerRadius="{TemplateBinding CornerRadius}"
                        Padding="{TemplateBinding Padding}">
                    <Grid Name="PART_Layout" ColumnDefinitions="*,Auto" RowDefinitions="Auto,Auto" ColumnSpacing="24">
                        <StackPanel Grid.Column="0" Spacing="2" VerticalAlignment="Center">
                            <TextBlock Classes="RowLabel" Text="{TemplateBinding Title}" TextWrapping="Wrap"/>
                            <TextBlock Classes="RowDesc" Text="{TemplateBinding Description}"
                                       IsVisible="{TemplateBinding Description, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"/>
                            <TextBlock Name="PART_Details" Classes="RowDesc" Text="{TemplateBinding Details}"
                                       IsVisible="{TemplateBinding IsExpanded}"/>
                            <Button Name="PART_MoreToggle" Classes="Link"
                                    IsVisible="{TemplateBinding Details, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"/>
                            <TextBlock Name="PART_Status" Classes="RowDesc" Text="{TemplateBinding Status}"
                                       IsVisible="{TemplateBinding Status, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"/>
                        </StackPanel>
                        <ContentPresenter Name="PART_ContentPresenter"
                                          Grid.Column="1"
                                          Content="{TemplateBinding Content}"
                                          ContentTemplate="{TemplateBinding ContentTemplate}"
                                          VerticalAlignment="Center"/>
                    </Grid>
                </Border>
            </ControlTemplate>
        </Setter>
    </Style>
    <Style Selector="settings|SettingRow[Indent=True] /template/ Grid#PART_Layout">
        <Setter Property="Margin" Value="24,0,0,0"/>
    </Style>
    <Style Selector="settings|SettingRow[Stacked=True] /template/ ContentPresenter#PART_ContentPresenter">
        <Setter Property="Grid.Row" Value="1"/>
        <Setter Property="Grid.Column" Value="0"/>
        <Setter Property="Grid.ColumnSpan" Value="2"/>
        <Setter Property="Margin" Value="0,10,0,0"/>
    </Style>

    <!-- ── SettingGroup ── -->
    <Style Selector="settings|SettingGroup">
        <Setter Property="Margin" Value="0,0,0,28"/>
        <Setter Property="Template">
            <ControlTemplate>
                <StackPanel>
                    <TextBlock Classes="SectionHeader" Text="{TemplateBinding Header}" Margin="0,0,0,2"
                               IsVisible="{TemplateBinding Header, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"/>
                    <ItemsPresenter Name="PART_ItemsPresenter"/>
                </StackPanel>
            </ControlTemplate>
        </Setter>
    </Style>
```

- [ ] **Step 5: Run the tests.** Use the targeted run with `SettingRowTests`, then `ComboTagsTests`, then `SettingsXamlResourceTests` (Architecture). Expected: PASS.

**If `:is(Control).Wide` fails to compile:** replace that style with `Width="320"` written directly on each wide control in Tasks 3.3-3.6. Keep the class too, so search and screenshots can find them.

- [ ] **Step 6: Commit.**

```bash
git add src/Ntilde.App/Settings/ tests/Ntilde.App.Tests/Core/SettingRowTests.cs tests/Ntilde.App.Tests/Core/ComboTagsTests.cs
git commit -m "feat(settings): SettingRow, SettingGroup and Tag-valued combos" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

## Task 3.2: The General page

**Files:**
- Create: `src/Ntilde.App/Settings/Pages/GeneralPage.axaml(.cs)`
- Modify: `SettingsPage.cs` (add `General` as the first member), `SettingsPageRegistry.cs`
- Modify: `AppearancePage.axaml` and `AppearancePage.axaml.cs`, removing the four rows
- Test: `tests/Ntilde.App.Tests/Core/SettingsPageTests.cs`, `SettingsWindowFrameTests.cs`, `MainWindowSettingsTargetTests.cs`

**Interfaces:** `GeneralPage` owns `AutomaticUpdateChecks`, `LongCommandNotificationsEnabled`, `Osc9NotificationsEnabled` and `Osc9ProgressReportingEnabled`. Its row ids are `update-checks`, `long-command-notifications`, `osc9-notifications` and `osc9-progress`. `SettingsPageRegistry.DefaultPage` becomes `General`.

- [ ] **Step 1: Write the failing test.** Append it to `SettingsPageTests`:

```csharp
    [AvaloniaFact]
    public void GeneralPage_LoadsAndCollectsUpdatesAndNotifications()
    {
        var page = new GeneralPage();
        page.Load(SessionFor(new TerminalSettings
        {
            AutomaticUpdateChecks = false,
            LongCommandNotificationsEnabled = true,
            Osc9NotificationsEnabled = true,
            Osc9ProgressReportingEnabled = false,
        }));
        var target = new TerminalSettings();

        page.Collect(target);

        Assert.False(target.AutomaticUpdateChecks);
        Assert.True(target.LongCommandNotificationsEnabled);
        Assert.True(target.Osc9NotificationsEnabled);
        Assert.False(target.Osc9ProgressReportingEnabled);
    }
```

In `SettingsWindowFrameTests.Sidebar_ListsGroupsAndPagesInRegistryOrder`, put `"GENERAL", "General"` at the start of `expected`.

- [ ] **Step 2: Run them and watch them fail.** Expected: compile errors.

- [ ] **Step 3: Write `GeneralPage.axaml`.** The two long descriptions are split at a sentence boundary, with their wording unchanged.

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:settings="clr-namespace:Ntilde.Settings"
             x:Class="Ntilde.Settings.Pages.GeneralPage">
    <ScrollViewer VerticalScrollBarVisibility="Auto">
        <StackPanel Classes="Page">
            <StackPanel Classes="PageHeader">
                <TextBlock Classes="H1" Text="General"/>
                <TextBlock Classes="Subtitle" Text="Updates and notifications."/>
            </StackPanel>

            <settings:SettingGroup Header="UPDATES">
                <settings:SettingRow RowId="update-checks"
                                     Title="Automatic update checks"
                                     Description="Check GitHub for a newer version shortly after launch and download it in the background."
                                     Details="Nothing restarts until you say so. Only applies to installed builds, not the portable zip."
                                     Keywords="update upgrade version release download">
                    <CheckBox Name="AutomaticUpdateChecksToggle" Classes="Toggle" AutomationProperties.Name="Automatic update checks"/>
                </settings:SettingRow>
            </settings:SettingGroup>

            <settings:SettingGroup Header="NOTIFICATIONS">
                <settings:SettingRow RowId="long-command-notifications"
                                     Title="Long command notifications"
                                     Description="Show a toast when a command that ran at least 30 seconds finishes in a pane you're not looking at."
                                     Keywords="toast alert finished done">
                    <CheckBox Name="LongCommandNotificationsToggle" Classes="Toggle" AutomationProperties.Name="Long command notifications"/>
                </settings:SettingRow>
                <settings:SettingRow RowId="osc9-notifications"
                                     Title="Program notifications (OSC 9)"
                                     Description="Show a toast when a program explicitly asks for one via OSC 9 (e.g. an AI CLI pinging that a long task finished)."
                                     Details="Suppressed while you're looking at the pane, like long command notifications."
                                     Keywords="toast alert osc">
                    <CheckBox Name="Osc9NotificationsToggle" Classes="Toggle" AutomationProperties.Name="Program notifications"/>
                </settings:SettingRow>
                <settings:SettingRow RowId="osc9-progress"
                                     Title="Progress reporting (OSC 9;4)"
                                     Description="Show per-tab progress and Windows taskbar progress when a program reports it via OSC 9;4 (e.g. an AI CLI's task progress)."
                                     Keywords="taskbar progress bar osc">
                    <CheckBox Name="Osc9ProgressReportingToggle" Classes="Toggle" AutomationProperties.Name="Progress reporting"/>
                </settings:SettingRow>
            </settings:SettingGroup>
        </StackPanel>
    </ScrollViewer>
</UserControl>
```

- [ ] **Step 4: Write `GeneralPage.axaml.cs`.**

```csharp
using System;
using Avalonia.Controls;
using Ntilde.Shell;

namespace Ntilde.Settings.Pages;

/// <summary>General: update checks and notifications.</summary>
public partial class GeneralPage : UserControl, ISettingsPage
{
    public GeneralPage()
    {
        InitializeComponent();
    }

    public SettingsPage Page => SettingsPage.General;

    public Control View => this;

    public void Load(SettingsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        TerminalSettings settings = session.Settings;
        Toggle("AutomaticUpdateChecksToggle").IsChecked = settings.AutomaticUpdateChecks;
        Toggle("LongCommandNotificationsToggle").IsChecked = settings.LongCommandNotificationsEnabled;
        Toggle("Osc9NotificationsToggle").IsChecked = settings.Osc9NotificationsEnabled;
        Toggle("Osc9ProgressReportingToggle").IsChecked = settings.Osc9ProgressReportingEnabled;
    }

    public SettingsValidationError? Validate() => null;

    public void Collect(TerminalSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.AutomaticUpdateChecks = Toggle("AutomaticUpdateChecksToggle").IsChecked == true;
        target.LongCommandNotificationsEnabled = Toggle("LongCommandNotificationsToggle").IsChecked == true;
        target.Osc9NotificationsEnabled = Toggle("Osc9NotificationsToggle").IsChecked == true;
        target.Osc9ProgressReportingEnabled = Toggle("Osc9ProgressReportingToggle").IsChecked == true;
    }

    private CheckBox Toggle(string name) => this.FindControl<CheckBox>(name)!;
}
```

- [ ] **Step 5: Register it, and move ownership off Appearance.**
  - In `SettingsPage`, add `General,` as the first member.
  - In the registry, set `DefaultPage = SettingsPage.General`, and add as the first entry:
    `new(SettingsPage.General, "GENERAL", "General", "◇", () => new GeneralPage()),`
  - In `AppearancePage.axaml`, delete the four `Grid` blocks holding `LongCommandNotificationsToggle`, `Osc9NotificationsToggle`, `Osc9ProgressReportingToggle` and `AutomaticUpdateChecksToggle`, and the hairline `Border` after each.
  - In `AppearancePage.axaml.cs`, delete those four toggles' lines from `LoadControls` and from `Collect`.
  - In `AppearancePage_CollectsEveryFieldItLoaded`, move the four notification and update assertions into the General test above.
  - In `MainWindowSettingsTargetTests`, nothing changes: it reads `SettingsPageRegistry.DefaultPage`.

- [ ] **Step 6: Run the Settings tests.** Use the targeted run with `Settings`. Expected: PASS, with `SettingsWindowRoundTripTests` unchanged.

- [ ] **Step 7: Commit.**

```bash
git add -A src/Ntilde.App/Settings tests/Ntilde.App.Tests/Core
git commit -m "feat(settings): General page for update checks and notifications" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

## Task 3.3: The Terminal page

**Files:**
- Create: `src/Ntilde.App/Settings/Pages/TerminalPage.axaml(.cs)`
- Modify: `SettingsPage.cs` (add `Terminal` after `Appearance`), `SettingsPageRegistry.cs`, `AppearancePage.axaml(.cs)`
- Test: `SettingsPageTests.cs`, `SettingsWindowFrameTests.cs`, `SettingsWindowKeyboardTests.cs`

**Interfaces:**
- `TerminalPage` owns `FontFamily`, `FontSize`, `EnableLigatures`, `EnableComplexShaping` and `MaxHistory`, and previews font and size through the session.
- Row ids: `font-family`, `font-size`, `ligatures`, `complex-shaping`, `scrollback`.
- Named controls `FontList`, `FontSizeInput`, `LigatureToggle`, `ComplexShapingToggle` and `ScrollbackInput` keep their names.

- [ ] **Step 1: Write the failing tests.** Append to `SettingsPageTests`:

```csharp
    [AvaloniaFact]
    public void TerminalPage_LoadsAndCollectsFontsAndScrollback()
    {
        var page = new TerminalPage();
        page.Load(new SettingsSession(
            new TerminalSettings { FontFamily = "Page Mono", FontSize = 17, EnableLigatures = true, EnableComplexShaping = false, MaxHistory = 25000 },
            () => ["Page Mono"]));
        var target = new TerminalSettings();

        page.Collect(target);

        Assert.Equal("Page Mono", target.FontFamily);
        Assert.Equal(17, target.FontSize);
        Assert.True(target.EnableLigatures);
        Assert.False(target.EnableComplexShaping);
        Assert.Equal(25000, target.MaxHistory);
    }

    [AvaloniaFact]
    public void TerminalPage_PreviewsFontChangesThroughTheSession()
    {
        var session = new SettingsSession(new TerminalSettings(), () => ["One Mono", "Two Mono"]);
        var page = new TerminalPage();
        page.Load(session);
        string? previewed = null;
        session.Preview.FontChanged += family => previewed = family;

        var fonts = page.FindControl<ComboBox>("FontList")!;
        fonts.SelectedItem = fonts.Items.OfType<ComboBoxItem>().Single(i => (string?)i.Content == "Two Mono");

        Assert.Equal("Two Mono", previewed);
    }
```

Then:
- Change `SettingsWindowFrameTests.FontSizeInput` to `window.GetPage<TerminalPage>().FindControl<NumericUpDown>("FontSizeInput")!`, and update its comment.
- Add `"Terminal"` after `"Appearance"` in the sidebar `expected` array.

- [ ] **Step 2: Run them and watch them fail.** Expected: compile errors.

- [ ] **Step 3: Write `TerminalPage.axaml`.**

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:settings="clr-namespace:Ntilde.Settings"
             x:Class="Ntilde.Settings.Pages.TerminalPage">
    <ScrollViewer VerticalScrollBarVisibility="Auto">
        <StackPanel Classes="Page">
            <StackPanel Classes="PageHeader">
                <TextBlock Classes="H1" Text="Terminal"/>
                <TextBlock Classes="Subtitle" Text="Font, text rendering and scrollback."/>
            </StackPanel>

            <settings:SettingGroup Header="FONT">
                <settings:SettingRow RowId="font-family" Title="Font family" Description="Monospaced font used in the terminal." Keywords="typeface monospace">
                    <ComboBox Name="FontList" Classes="Wide" AutomationProperties.Name="Font family"/>
                </settings:SettingRow>
                <settings:SettingRow RowId="font-size" Title="Font size" Keywords="text size points zoom">
                    <NumericUpDown Name="FontSizeInput" Classes="Wide" Minimum="6" Maximum="72" Value="14" AutomationProperties.Name="Font size"/>
                </settings:SettingRow>
                <settings:SettingRow RowId="ligatures" Title="Programming ligatures" Description="Render => != >= |> as glyphs." Keywords="font arrows">
                    <CheckBox Name="LigatureToggle" Classes="Toggle" AutomationProperties.Name="Programming ligatures"/>
                </settings:SettingRow>
                <settings:SettingRow RowId="complex-shaping" Title="Complex shaping (HarfBuzz)" Description="Run-level shaping for non-Latin scripts and emoji." Keywords="harfbuzz emoji script rendering">
                    <CheckBox Name="ComplexShapingToggle" Classes="Toggle" AutomationProperties.Name="Complex shaping"/>
                </settings:SettingRow>
            </settings:SettingGroup>

            <settings:SettingGroup Header="SCROLLBACK">
                <settings:SettingRow RowId="scrollback" Title="Scrollback limit" Description="Maximum number of lines retained per pane." Keywords="history lines buffer">
                    <NumericUpDown Name="ScrollbackInput" Classes="Wide" Minimum="100" Maximum="100000" Value="10000" Increment="1000" AutomationProperties.Name="Scrollback limit"/>
                </settings:SettingRow>
            </settings:SettingGroup>
        </StackPanel>
    </ScrollViewer>
</UserControl>
```

- [ ] **Step 4: Write `TerminalPage.axaml.cs`.** The preview handlers are `ORIG` 801-822 with the Move rules applied.

```csharp
using System;
using System.Linq;
using Avalonia.Controls;
using Ntilde.Platform;
using Ntilde.Shell;

namespace Ntilde.Settings.Pages;

/// <summary>Terminal: font, text rendering and scrollback.</summary>
public partial class TerminalPage : UserControl, ISettingsPage
{
    private SettingsSession? _session;

    public TerminalPage()
    {
        InitializeComponent();

        var fontList = this.FindControl<ComboBox>("FontList")!;
        fontList.SelectionChanged += (_, _) =>
        {
            if (_session != null && fontList.SelectedItem is ComboBoxItem { Content: { } content })
            {
                _session.Preview.Font(content.ToString() ?? BundledFontCatalog.DefaultTerminalFontFamily);
            }
        };

        var fontSize = this.FindControl<NumericUpDown>("FontSizeInput")!;
        fontSize.ValueChanged += (_, _) =>
        {
            if (_session != null && fontSize.Value.HasValue)
            {
                _session.Preview.FontSize((double)fontSize.Value.Value);
            }
        };
    }

    public SettingsPage Page => SettingsPage.Terminal;

    public Control View => this;

    public void Load(SettingsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        TerminalSettings settings = session.Settings;

        var fontList = this.FindControl<ComboBox>("FontList")!;
        fontList.Items.Clear();
        foreach (string family in session.FontChoices)
        {
            fontList.Items.Add(new ComboBoxItem { Content = family });
        }

        if (fontList.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Content?.ToString() == settings.FontFamily) is { } match)
        {
            fontList.SelectedItem = match;
        }

        this.FindControl<NumericUpDown>("FontSizeInput")!.Value = (decimal)settings.FontSize;
        Toggle("LigatureToggle").IsChecked = settings.EnableLigatures;
        Toggle("ComplexShapingToggle").IsChecked = settings.EnableComplexShaping;
        this.FindControl<NumericUpDown>("ScrollbackInput")!.Value = settings.MaxHistory;
    }

    public SettingsValidationError? Validate() => null;

    public void Collect(TerminalSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.FontSize = (double)(this.FindControl<NumericUpDown>("FontSizeInput")!.Value ?? 14);
        target.MaxHistory = (int)(this.FindControl<NumericUpDown>("ScrollbackInput")!.Value ?? 10000);
        target.EnableLigatures = Toggle("LigatureToggle").IsChecked == true;
        target.EnableComplexShaping = Toggle("ComplexShapingToggle").IsChecked == true;
        if (this.FindControl<ComboBox>("FontList")!.SelectedItem is ComboBoxItem fontItem)
        {
            target.FontFamily = fontItem.Content?.ToString() ?? BundledFontCatalog.DefaultTerminalFontFamily;
        }
    }

    private CheckBox Toggle(string name) => this.FindControl<CheckBox>(name)!;
}
```

- [ ] **Step 5: Register it, and move ownership off Appearance.**
  - Add `Terminal,` after `Appearance` in `SettingsPage`.
  - Add the registry entry after Appearance: `new(SettingsPage.Terminal, "INTERFACE", "Terminal", "Aa", () => new TerminalPage()),`
  - From `AppearancePage.axaml`, delete the `FONTS & TEXT` header and its remaining rows (font family, font size, ligatures, complex shaping), and the `SCROLLBACK` header and row, with their hairlines.
  - From `AppearancePage.axaml.cs`, delete:
    - `WireFontRows` and its constructor call;
    - `PopulateFonts` and its `Load` call;
    - the font family, font size, ligatures, complex shaping and scrollback lines in `LoadControls` and in `Collect`.
  - From `AppearancePage_CollectsEveryFieldItLoaded`, delete the matching assertions.

- [ ] **Step 6: Run the Settings tests.** Expected: PASS, with the round trip unchanged.

- [ ] **Step 7: Commit.**

```bash
git add -A src/Ntilde.App/Settings tests/Ntilde.App.Tests/Core
git commit -m "feat(settings): Terminal page for fonts and scrollback" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

## Task 3.4: Rows on Command Assist, Agent Access, SSH and Shortcuts; readable rollup labels; hairline code rows

**Files:**
- Replace: `CommandAssistPage.axaml`, `AgentAccessPage.axaml`, `SshPage.axaml`
- Modify: `CommandAssistPage.axaml.cs` (status and chord text now on rows), `AgentAccessPage.axaml.cs` (`ComboTags`), `ShortcutsPage.axaml` (the caveat), `ShortcutsPage.axaml.cs` (hairline rows)
- Test: `SettingsPageTests.cs`, `SettingsWindowTitleBarShortcutRefreshTests.cs`, `ThemeApplicationRegressionTests.cs`

**Interfaces:**
- Command Assist rows: `command-assist`, `suggestion-bubble`, `remember-commands`, `shell-integration`, `snippets`, `remote-shell-integration`.
  - Named rows: `CommandAssistBubbleRow`, `CommandAssistHistoryRow`, `CommandAssistSnippetsRow`, `RemoteShellIntegrationRow`.
  - The old status `TextBlock`s become `SettingRow.Status`.
- Agent Access rows: `agent-observe`, `agent-replay-export`, `agent-act`, `agent-tab-indicator`.
- SSH row: `native-ssh`.
- Shortcuts row: `keybindings`.

- [ ] **Step 1: Write the failing tests.**
  - In `SettingsPageTests.CommandAssistPage_NamesTheEffectiveChords_AndFollowsLaterRebinds`, read the row instead of the `TextBlock`:

```csharp
        var history = page.FindControl<SettingRow>("CommandAssistHistoryRow")!;

        Assert.Contains("Alt+F8", history.Description);
        Assert.DoesNotContain("Ctrl+R", history.Description);

        session.ShortcutDraft["command_assist_history"] = "Alt+F7";
        session.RaiseShortcutsChanged();

        Assert.Contains("Alt+F7", history.Description);
```

  - In `SettingsWindowTitleBarShortcutRefreshTests.RebindingOnTheShortcutsPage…`, the same change: `string history = window.GetPage<CommandAssistPage>().FindControl<SettingRow>("CommandAssistHistoryRow")!.Description!;`.
  - Add the label tests (review focus 1) and the More check to `SettingsPageTests`:

```csharp
    [AvaloniaFact]
    public void AgentAccessPage_ShowsReadableLabels_AndStillStoresTheOldValues()
    {
        var page = new AgentAccessPage();
        page.Load(SessionFor(new TerminalSettings { AgentIndicatorTabRollup = "All" }));
        var combo = page.FindControl<ComboBox>("AgentIndicatorTabRollupList")!;
        var target = new TerminalSettings();

        page.Collect(target);

        string[] labels = ["Writes only", "All activity"];
        Assert.Equal(labels, combo.Items.OfType<ComboBoxItem>().Select(i => (string)i.Content!).ToArray());
        Assert.Equal("All activity", ((ComboBoxItem)combo.SelectedItem!).Content);
        Assert.Equal("All", target.AgentIndicatorTabRollup);
    }

    [AvaloniaTheory]
    [InlineData("writesonly")]
    [InlineData("Sometimes")]
    public void AgentAccessPage_AnUnknownStoredRollup_OpensOnWritesOnly(string stored)
    {
        var page = new AgentAccessPage();
        page.Load(SessionFor(new TerminalSettings { AgentIndicatorTabRollup = stored }));
        var target = new TerminalSettings();

        page.Collect(target);

        Assert.Equal("WritesOnly", target.AgentIndicatorTabRollup);
    }

    [AvaloniaFact]
    public void LongDescriptions_SitBehindMore()
    {
        string[] withMore = ["remember-commands", "remote-shell-integration", "native-ssh", "keybindings", "osc9-notifications", "update-checks"];
        Control[] pages = [new CommandAssistPage(), new SshPage(), new ShortcutsPage(), new GeneralPage()];

        var rows = pages.SelectMany(page => page.GetLogicalDescendants().OfType<SettingRow>()).ToDictionary(row => row.RowId!);

        Assert.All(withMore, id => Assert.False(string.IsNullOrWhiteSpace(rows[id].Details), $"{id} has no Details"));
    }
```

  - In `ThemeApplicationRegressionTests.SettingsWindow_CardRows_UseTheWindowPaletteBrushes`, shortcut rows are now hairline rows. Replace the shortcut-row loop body with:

```csharp
        foreach (var row in shortcutRows)
        {
            Assert.Null(row.Background);
            Assert.Same(hairlineBrush, row.BorderBrush);
        }
```

  Also rename the test to `SettingsWindow_CodeBuiltRows_UseTheWindowPaletteBrushes`. The title-bar rows keep their card assertions until PR 4.

- [ ] **Step 2: Run them and watch them fail.** Expected: compile errors, plus failures on Details and labels.

- [ ] **Step 3: Replace `CommandAssistPage.axaml`.**
  - Every name the code-behind wires keeps its name.
  - The four description `TextBlock`s and three status `TextBlock`s become row properties.
  - The "Suspect something sensitive" line joins `Details`, as a second paragraph.

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:settings="clr-namespace:Ntilde.Settings"
             x:Class="Ntilde.Settings.Pages.CommandAssistPage">
    <ScrollViewer VerticalScrollBarVisibility="Auto">
        <StackPanel Classes="Page">
            <StackPanel Classes="PageHeader">
                <TextBlock Classes="H1" Text="Command Assist"/>
                <TextBlock Classes="Subtitle" Text="Inline suggestions, history, snippets, and remote shell integration."/>
            </StackPanel>

            <settings:SettingGroup>
                <settings:SettingRow RowId="command-assist" Title="Command assistant" Description="Inline suggestions from shell integration." Keywords="autocomplete suggestions completion">
                    <CheckBox Name="CommandAssistToggle" Classes="Toggle" AutomationProperties.Name="Command assistant"/>
                </settings:SettingRow>
                <settings:SettingRow Name="CommandAssistBubbleRow" RowId="suggestion-bubble" Indent="True"
                                     Title="Suggestion bubble while typing"
                                     Description="After two characters, show the best matching command from your history just above the prompt. Esc hides it for the rest of the command. Turn this off to keep the assistant silent until you ask for it with Ctrl+Space or Ctrl+R."
                                     Keywords="popup inline hint">
                    <CheckBox Name="CommandAssistPassiveBubbleToggle" Classes="Toggle" AutomationProperties.Name="Suggestion bubble while typing"/>
                </settings:SettingRow>
                <settings:SettingRow Name="CommandAssistHistoryRow" RowId="remember-commands" Indent="True"
                                     Title="Remember commands"
                                     Description="Record the commands you run so they can be suggested and searched with Ctrl+R."
                                     Details="Only what the shell echoed on screen is recorded, so a password typed at a hidden prompt is never captured, and common secret-bearing arguments (--password, tokens, bearer headers) are redacted before anything is written. Other secrets typed as part of a visible command are not. With this off the assistant still suggests file paths and explains failures - it just stops writing history.&#10;&#10;Suspect something sensitive is in there? Clear history erases every recorded command, including anything carried over from an older version of Ntilde."
                                     Keywords="history record privacy clear secrets">
                    <StackPanel Orientation="Horizontal" Spacing="10">
                        <Button Name="BtnClearCommandAssistHistory" Classes="Pill" Content="Clear history"/>
                        <CheckBox Name="CommandAssistHistoryToggle" Classes="Toggle" VerticalAlignment="Center" AutomationProperties.Name="Remember commands"/>
                    </StackPanel>
                </settings:SettingRow>
                <settings:SettingRow RowId="shell-integration" Indent="True"
                                     Title="Shell integration"
                                     Description="Let Ntilde instrument local shells it starts, so it knows where the prompt is, what you typed, and how each command exited. Without it the assistant falls back to guessing from the screen."
                                     Keywords="prompt marks exit code">
                    <CheckBox Name="CommandAssistShellIntegrationToggle" Classes="Toggle" AutomationProperties.Name="Shell integration"/>
                </settings:SettingRow>
                <settings:SettingRow Name="CommandAssistSnippetsRow" RowId="snippets" Indent="True"
                                     Title="Saved snippets"
                                     Description="Commands you pinned with Ctrl+Shift+S, plus anything you add here. They show up in the assistant's popup alongside your history. Edit a name or a command in place - changes save when you leave the box."
                                     Keywords="snippet pin saved commands">
                    <Button Name="BtnAddCommandAssistSnippet" Classes="Pill" Content="Add snippet"/>
                </settings:SettingRow>
                <StackPanel Name="CommandAssistSnippetsPanel" Margin="24,8,0,8" Spacing="0"/>
            </settings:SettingGroup>

            <settings:SettingGroup Header="REMOTE SHELLS">
                <settings:SettingRow Name="RemoteShellIntegrationRow" RowId="remote-shell-integration" Stacked="True"
                                     Title="Remote shell integration"
                                     Description="Ntilde cannot install shell integration over SSH."
                                     Details="Copy the one-line installer for the remote shell, paste it at the prompt on that host, and the command assistant works there too: history, suggestions read from the prompt line, exit codes and prompt-anchored placement. Filesystem path suggestions stay off for remote sessions - they would list the local disk."
                                     Keywords="ssh remote installer bash zsh fish">
                    <StackPanel Orientation="Horizontal" Spacing="6" HorizontalAlignment="Left">
                        <ComboBox Name="RemoteShellIntegrationShellList" MinWidth="150" AutomationProperties.Name="Remote shell"/>
                        <Button Name="BtnCopyRemoteShellIntegration" Classes="Pill" Content="Copy installer"/>
                        <Button Name="BtnCopyRemoteShellIntegrationSnippet" Classes="Pill" Content="Copy plain snippet"/>
                    </StackPanel>
                </settings:SettingRow>
            </settings:SettingGroup>
        </StackPanel>
    </ScrollViewer>
</UserControl>
```

- [ ] **Step 4: Point the Command Assist code at the rows.** In `CommandAssistPage.axaml.cs`:

  **(a) Status.** Rename `ShowRemoteShellIntegrationStatus(TextBlock? status, string message)` to `ShowStatus(SettingRow? row, string message)`, with this body:

  ```csharp
  if (row != null)
  {
      row.Status = message;
  }
  ```

  Then update the lookups:

  | Old | New |
  |---|---|
  | `this.FindControl<TextBlock>("RemoteShellIntegrationStatus")` | `this.FindControl<SettingRow>("RemoteShellIntegrationRow")` |
  | `this.FindControl<TextBlock>("CommandAssistHistoryStatus")` | `this.FindControl<SettingRow>("CommandAssistHistoryRow")` |
  | `this.FindControl<TextBlock>("CommandAssistSnippetStatus")` | `this.FindControl<SettingRow>("CommandAssistSnippetsRow")` |

  Change the matching parameters of type `TextBlock? status` to `SettingRow? status` in `CopyRemoteShellIntegrationInstallerAsync`, `CopyRemoteShellIntegrationSnippetAsync`, `TryGetSnippetEditor` and `CommitCommandAssistSnippetAsync`.

  **(b) Chord text.** Replace `UpdateCommandAssistShortcutText` with:

```csharp
    /// <summary>
    /// The descriptions name their chords in prose. The XAML text is written with the
    /// Windows/Linux defaults; this swaps in the binding actually in force, which differs on macOS
    /// (Cmd+R, Cmd+Shift+S) and after a rebind.
    /// </summary>
    private void UpdateCommandAssistShortcutText()
    {
        string toggle = TitleBarShortcuts.Resolve("command_assist_toggle", Session.ShortcutDraft);
        string history = TitleBarShortcuts.Resolve("command_assist_history", Session.ShortcutDraft);
        string pin = TitleBarShortcuts.Resolve("command_assist_pin", Session.ShortcutDraft);

        Replace("CommandAssistBubbleRow", ("Ctrl+Space", toggle), ("Ctrl+R", history));
        Replace("CommandAssistHistoryRow", ("Ctrl+R", history));
        Replace("CommandAssistSnippetsRow", ("Ctrl+Shift+S", pin));

        if (_snippetsEmptyHint != null)
        {
            _snippetsEmptyHint.Text = FormatSnippetsEmptyHint();
        }

        void Replace(string rowName, params (string Written, string Effective)[] chords)
        {
            if (this.FindControl<SettingRow>(rowName) is not { } row)
            {
                return;
            }

            // Tag keeps the XAML original so a second rebind replaces from the source text, not
            // from the previous substitution.
            row.Tag ??= new RowText(row.Description, row.Details);
            var original = (RowText)row.Tag;
            row.Description = original.Description is null ? null : SubstituteChords(original.Description, chords);
            row.Details = original.Details is null ? null : SubstituteChords(original.Details, chords);
        }
    }

    private sealed record RowText(string? Description, string? Details);
```

  **(c) Snippet rows** use the shared hairline chrome. In `CreateCommandAssistSnippetRow`, replace the final `return new Border { … }` with:

```csharp
            return SettingsRowChrome.CreateRow(this, new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("220,*,Auto"),
                ColumnSpacing = 10,
                Children = { nameEditor, commandEditor, deleteButton },
            });
```

- [ ] **Step 5: Replace `AgentAccessPage.axaml`.** The tab-indicator description says "All activity" to match its new label.

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:settings="clr-namespace:Ntilde.Settings"
             x:Class="Ntilde.Settings.Pages.AgentAccessPage">
    <ScrollViewer VerticalScrollBarVisibility="Auto">
        <StackPanel Classes="Page">
            <StackPanel Classes="PageHeader">
                <TextBlock Classes="H1" Text="Agent Access"/>
                <TextBlock Classes="Subtitle" Text="Permissions for AI agents on this machine to read, capture, or act on your terminal sessions."/>
            </StackPanel>

            <settings:SettingGroup>
                <settings:SettingRow RowId="agent-observe" Title="Agent access (observe)"
                                     Description="Allow AI agents on this machine to read terminal sessions (screen and scrollback). Read-only: agents cannot type or open sessions. Screen content may include sensitive output."
                                     Keywords="ai mcp agent read screen scrollback">
                    <CheckBox Name="AgentAccessObserveToggle" Classes="Toggle" AutomationProperties.Name="Agent access (observe)"/>
                </settings:SettingRow>
                <settings:SettingRow RowId="agent-replay-export" Indent="True" Title="Agent replay export"
                                     Description="Additionally allow agents to export a session's recent output as a replay file. Exports contain output and window resizes only — never anything you type. Requires Agent access (observe)."
                                     Keywords="ai mcp replay export recording">
                    <CheckBox Name="AgentReplayExportToggle" Classes="Toggle" AutomationProperties.Name="Agent replay export"/>
                </settings:SettingRow>
                <settings:SettingRow RowId="agent-act" Indent="True" Title="Agent access (act)"
                                     Description="Additionally allow agents to type into, open, and close terminal sessions. SSH connections must also be allowlisted individually. Every action is shown in the agent activity journal. Requires Agent access (observe)."
                                     Keywords="ai mcp agent type input control">
                    <CheckBox Name="AgentAccessActToggle" Classes="Toggle" AutomationProperties.Name="Agent access (act)"/>
                </settings:SettingRow>
                <settings:SettingRow RowId="agent-tab-indicator" Title="Tab indicator"
                                     Description="Which agent activity is shown on tab headers. Writes are always shown; choose All activity to also mark tabs whose panes are being read."
                                     Keywords="badge tab agent reads writes">
                    <ComboBox Name="AgentIndicatorTabRollupList" Classes="Wide" AutomationProperties.Name="Tab indicator">
                        <ComboBoxItem Content="Writes only" Tag="WritesOnly"/>
                        <ComboBoxItem Content="All activity" Tag="All"/>
                    </ComboBox>
                </settings:SettingRow>
            </settings:SettingGroup>
        </StackPanel>
    </ScrollViewer>
</UserControl>
```

In `AgentAccessPage.axaml.cs`:
- Replace the rollup block in `Load` with the code below. Clearing the selection first keeps the old behaviour: an unknown value always fell back to the first item.

```csharp
        var rollup = this.FindControl<ComboBox>("AgentIndicatorTabRollupList")!;
        rollup.SelectedItem = null;
        ComboTags.Select(rollup, settings.AgentIndicatorTabRollup, StringComparison.Ordinal, fallbackIndex: 0);
```

- In `Collect`, replace the rollup lines with `if (ComboTags.SelectedValue(this.FindControl<ComboBox>("AgentIndicatorTabRollupList")!) is { } rollup) target.AgentIndicatorTabRollup = rollup;`.

- [ ] **Step 6: Replace `SshPage.axaml`.**

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:settings="clr-namespace:Ntilde.Settings"
             x:Class="Ntilde.Settings.Pages.SshPage">
    <ScrollViewer VerticalScrollBarVisibility="Auto">
        <StackPanel Classes="Page">
            <StackPanel Classes="PageHeader">
                <TextBlock Classes="H1" Text="SSH"/>
                <TextBlock Classes="Subtitle" Text="Global SSH behavior. Per-connection options (host, auth, jump hosts, forwards) live on each profile."/>
            </StackPanel>

            <settings:SettingGroup>
                <settings:SettingRow RowId="native-ssh" Title="Native SSH backend"
                                     Description="Allow profiles set to the Native backend to connect with Ntilde's built-in SSH client instead of the system ssh."
                                     Details="On by default; while it is on, new SSH profiles default to the Native backend (existing profiles keep their backend). Applies to new connections only; sessions already open are unaffected. While this is off, a profile explicitly set to Native refuses to connect rather than silently falling back — OpenSSH profiles are unaffected either way."
                                     Keywords="openssh client backend">
                    <CheckBox Name="NativeSshToggle" Classes="Toggle" AutomationProperties.Name="Native SSH backend"/>
                </settings:SettingRow>
            </settings:SettingGroup>
        </StackPanel>
    </ScrollViewer>
</UserControl>
```

- [ ] **Step 7: Shortcuts: the caveat goes behind More, and the rows become hairline rows.**
  - In `ShortcutsPage.axaml`, replace the two `RowDesc` `TextBlock`s that follow `ShortcutValidationMessage` with the row below. Add `xmlns:settings="clr-namespace:Ntilde.Settings"` to the root if the generated file lacks it.

```xml
                <settings:SettingRow RowId="keybindings" BorderThickness="0" Padding="0,4"
                                     Title="Recording a shortcut"
                                     Description="Select a shortcut field and press the new key chord. Reset restores the default binding."
                                     Details="Command Assist chords are matched while a suggestion surface is open, after the terminal has had its turn at the key. A chord the terminal sends to the shell first - most Ctrl+letter combinations, and anything the kitty keyboard protocol encodes for a full-screen program - will look bound here but never fire. Prefer Esc, the arrow keys, Enter, function keys, or a chord with Alt."
                                     Keywords="keybinding hotkey chord rebind"/>
```

  - In `ShortcutsPage.CreateShortcutBindingRow`, replace the card `var row = new Border { … };` and the later `row.Child = new StackPanel { … };` with `var row = SettingsRowChrome.CreateRow(this, new StackPanel { … });`, keeping the same StackPanel contents.

- [ ] **Step 8: Run the Settings tests and the Architecture tests.** Expected: PASS, with the round trip unchanged.

- [ ] **Step 9: Commit.**

```bash
git add -A src/Ntilde.App/Settings tests/Ntilde.App.Tests/Core
git commit -m "feat(settings): setting rows with More on Command Assist, Agent Access, SSH and Shortcuts" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

## Task 3.5: The Tabs & title bar page

**Files:**
- Create: `src/Ntilde.App/Settings/Pages/TabsAndTitleBarPage.axaml(.cs)`
- Modify: `SettingsPage.cs` (add `TabsAndTitleBar` after `Terminal`), `SettingsPageRegistry.cs` (and delete `SettingsRowIds`), `AppearancePage.axaml(.cs)`, `MainWindow.axaml.cs` (`CustomizeTitleBarSettingsTarget`)
- Test: `SettingsPageTests.cs`, `SettingsWindowFrameTests.cs`, `MainWindowSettingsTargetTests.cs`, `SettingsWindowTitleBarShortcutRefreshTests.cs`, `ThemeApplicationRegressionTests.cs`

**Interfaces:**
- `TabsAndTitleBarPage` owns `TabStripOrientation` and the title-bar draft rows. It listens to `ShortcutsChanged`.
- Named controls: `TabOrientationList`, `TitleBarValidationMessage`, `TitleBarItemsPanel`. Row id: `tab-orientation`.
- `MainWindow.CustomizeTitleBarSettingsTarget()` returns `new SettingsTarget(SettingsPage.TabsAndTitleBar)`.

- [ ] **Step 1: Write the failing tests.**

  - In `SettingsPageTests`, add:

```csharp
    [AvaloniaTheory]
    [InlineData("Vertical", "Vertical")]
    [InlineData("vertical", "Vertical")]     // the old loop compared case-insensitively too
    [InlineData("Diagonal", "Horizontal")]   // unknown: first item, as before
    public void TabsAndTitleBarPage_TabOrientation_RoundTrips_WithTheOldFallbacks(string stored, string expected)
    {
        var page = new TabsAndTitleBarPage();
        page.Load(SessionFor(new TerminalSettings { TabStripOrientation = stored }));
        var target = new TerminalSettings();

        page.Collect(target);

        Assert.Equal(expected, target.TabStripOrientation);
    }
```

  - Move `AppearancePage_RebuildsTheTitleBarRowsWhenShortcutsChange` to `TabsAndTitleBarPage_RebuildsTheTitleBarRowsWhenShortcutsChange`, constructing `TabsAndTitleBarPage`.
  - In `SettingsWindowTitleBarShortcutRefreshTests.TitleBarRowsPanel` and the card-row test in `ThemeApplicationRegressionTests`, use `window.GetPage<TabsAndTitleBarPage>()`.
  - In `MainWindowSettingsTargetTests.EntryPoints_OpenTheirPages`, expect `new SettingsTarget(SettingsPage.TabsAndTitleBar)` for Customize.
  - In the sidebar `expected` array, add `"Tabs & title bar"` after `"Terminal"`.
  - **Retarget the two deep-link tests** to a long page with real rows. Command Assist's last row starts below the fold. Add `using Avalonia.LogicalTree;` to `SettingsWindowFrameTests.cs`, then replace `RevealRow_ScrollsTheRowIntoView_AndHighlightsIt` and `Constructing_WithARowTarget_HighlightsTheRowOnceOpen` with:

```csharp
    private const string BelowTheFold = "remote-shell-integration";

    private static SettingRow RowOn(Ntilde.SettingsWindow window, string rowId)
        => window.GetPage<CommandAssistPage>().GetLogicalDescendants().OfType<SettingRow>().Single(row => row.RowId == rowId);

    [AvaloniaFact]
    public void RevealRow_ScrollsTheRowIntoView_AndHighlightsIt()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.CommandAssist));
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            SettingRow row = RowOn(window, BelowTheFold);
            ScrollViewer scroller = row.GetVisualAncestors().OfType<ScrollViewer>().First();
            double rowBottom = row.TranslatePoint(new Point(0, row.Bounds.Height), (Visual)scroller.Content!)!.Value.Y;
            Assert.True(rowBottom > scroller.Viewport.Height, "Setup: the row must start below the fold, or this proves nothing.");

            Assert.True(window.RevealRow(SettingsPage.CommandAssist, BelowTheFold));
            Dispatcher.UIThread.RunJobs();

            Assert.True(scroller.Offset.Y + scroller.Viewport.Height >= rowBottom - 1, $"Row bottom {rowBottom} is still below the viewport at offset {scroller.Offset.Y}.");
            Assert.Same(window.FindResource("NtBlueDim"), row.Background);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Constructing_WithARowTarget_HighlightsTheRowOnceOpen()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.CommandAssist, BelowTheFold));
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Same(window.FindResource("NtBlueDim"), RowOn(window, BelowTheFold).Background);
        }
        finally
        {
            window.Close();
        }
    }
```

  **If the setup assert fires** because Command Assist fits in one screen on this machine's fonts: set `window.Height = window.MinHeight;` before `Show()`. Do not weaken the assert.

- [ ] **Step 2: Run them and watch them fail.** Expected: compile errors.

- [ ] **Step 3: Write `TabsAndTitleBarPage.axaml`.**

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:settings="clr-namespace:Ntilde.Settings"
             x:Class="Ntilde.Settings.Pages.TabsAndTitleBarPage">
    <ScrollViewer VerticalScrollBarVisibility="Auto">
        <StackPanel Classes="Page">
            <StackPanel Classes="PageHeader">
                <TextBlock Classes="H1" Text="Tabs &amp; title bar"/>
                <TextBlock Classes="Subtitle" Text="Where tabs live, and which actions sit in the title bar."/>
            </StackPanel>

            <settings:SettingGroup Header="TABS">
                <settings:SettingRow RowId="tab-orientation" Title="Tab strip orientation"
                                     Description="Vertical shows tabs in a left sidebar with agent status and a last-output preview."
                                     Keywords="vertical horizontal sidebar layout">
                    <ComboBox Name="TabOrientationList" Classes="Wide" AutomationProperties.Name="Tab strip orientation">
                        <ComboBoxItem Content="Horizontal" Tag="Horizontal"/>
                        <ComboBoxItem Content="Vertical" Tag="Vertical"/>
                    </ComboBox>
                </settings:SettingRow>
            </settings:SettingGroup>

            <settings:SettingGroup Header="TITLE BAR ACTIONS">
                <StackPanel Spacing="10" Margin="0,8,0,0">
                    <TextBlock Classes="RowDesc"
                               Text="Pinned actions get their own icon in the title bar. Overflow actions live in the … menu. Hidden actions stay reachable by shortcut and from the command palette."/>
                    <TextBlock Name="TitleBarValidationMessage" Foreground="{DynamicResource NtRed}" FontSize="12" IsVisible="False" TextWrapping="Wrap"/>
                    <StackPanel Name="TitleBarItemsPanel" Spacing="8"/>
                </StackPanel>
            </settings:SettingGroup>
        </StackPanel>
    </ScrollViewer>
</UserControl>
```

- [ ] **Step 4: Write `TabsAndTitleBarPage.axaml.cs`.** Move these members from `AppearancePage.axaml.cs` unchanged: `RebuildTitleBarRows`, `CreateTitleBarRow`, `MoveDraftPinned`, `ShowTitleBarValidationMessage`, `ClearTitleBarValidationMessage`, and the `RowDescStyleClass` const.

```csharp
public partial class TabsAndTitleBarPage : UserControl, ISettingsPage
{
    private const string RowDescStyleClass = "RowDesc";

    private SettingsSession? _session;

    public TabsAndTitleBarPage()
    {
        InitializeComponent();
    }

    public SettingsPage Page => SettingsPage.TabsAndTitleBar;

    public Control View => this;

    private SettingsSession Session => _session ?? throw new InvalidOperationException("TabsAndTitleBarPage used before Load.");

    public void Load(SettingsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!ReferenceEquals(_session, session))
        {
            if (_session != null)
            {
                _session.ShortcutsChanged -= RebuildTitleBarRows;
            }

            session.ShortcutsChanged += RebuildTitleBarRows;
        }

        _session = session;
        ComboTags.Select(this.FindControl<ComboBox>("TabOrientationList")!, session.Settings.TabStripOrientation, StringComparison.OrdinalIgnoreCase, fallbackIndex: 0);
        RebuildTitleBarRows();
    }

    public SettingsValidationError? Validate() => null;

    public void Collect(TerminalSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (ComboTags.SelectedValue(this.FindControl<ComboBox>("TabOrientationList")!) is { } orientation)
        {
            target.TabStripOrientation = orientation;
        }

        // The title-bar placement is committed from the session's draft (SettingsSession.CommitDrafts).
    }

    // Moved members as listed above.
}
```

- [ ] **Step 5: Register it, and remove the title bar from Appearance.**
  - Add `TabsAndTitleBar,` after `Terminal` in `SettingsPage`.
  - Add the registry entry after Terminal: `new(SettingsPage.TabsAndTitleBar, "INTERFACE", "Tabs & title bar", "▭", () => new TabsAndTitleBarPage()),`
  - **Delete the `SettingsRowIds` class.**
  - From `AppearancePage.axaml`, delete the TITLE BAR section (the header, the description StackPanel and `TitleBarItemsPanel`) and the tab-orientation row.
  - From `AppearancePage.axaml.cs`, delete:
    - the `ShortcutsChanged` subscription block in `Load`, and its `RebuildTitleBarRows()` call;
    - the moved members;
    - the tab-orientation lines in `LoadControls` and `Collect`.
  - In `SettingsPageTests.AppearancePage_CollectsEveryFieldItLoaded`, delete the `TabStripOrientation` seed value and its assertion. The new Tabs & title bar test covers it.
  - In `MainWindow.axaml.cs`, change `CustomizeTitleBarSettingsTarget()` to `=> new(SettingsPage.TabsAndTitleBar);`, and shorten its doc to: "Customize Title Bar… opens the page the title-bar actions live on."
  - `git grep -n SettingsRowIds` must print nothing.

- [ ] **Step 6: Run the Settings and MainWindow tests.** Expected: PASS, with the round trip unchanged.

- [ ] **Step 7: Commit.**

```bash
git add -A src/Ntilde.App tests/Ntilde.App.Tests/Core
git commit -m "feat(settings): Tabs & title bar page; Customize Title Bar opens it" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

## Task 3.6: Appearance rebuilt, with the theme editor as a sub-page

**Files:**
- Replace: `src/Ntilde.App/Settings/Pages/AppearancePage.axaml`, `src/Ntilde.App/Settings/Pages/AppearancePage.axaml.cs`
- Create: `src/Ntilde.App/Settings/IHasSubPage.cs`
- Modify: `src/Ntilde.App/SettingsWindow.axaml.cs` (`RevealRow` closes a sub-page first)
- Create: `tests/Ntilde.App.Tests/Core/SettingsThemeEditorTests.cs`
- Test: `SettingsPageTests.cs` (the Appearance tests)

**Interfaces:**
- `public interface IHasSubPage { void CloseSubPage(); }`
- `AppearancePage : ISettingsPage, IHasSubPage`, with `internal bool IsThemeEditorOpen`.
- Row ids: `theme`, `window-opacity`, `interface-scale`, `blur-effect`, `background-image`, `background-image-opacity`, `background-image-stretch`.
- Named views: `AppearanceMainView` and `ThemeEditorView` (both ScrollViewers).
- Editor controls: `BtnThemeEditorBack`, `ThemeEditorTitle`, `EditThemeNameInput`, `EditThemeFgInput`, `EditThemeBgInput`, `EditThemeCursorInput`, `EditorSwatchGrid`, `EditSwatch0`-`15`, `BtnSaveTheme`, `BtnDeleteTheme`, `ThemeEditorStatus`.
- Editor preview: `EditorPreviewArea`, `EditorPreviewLabel`, `EditorSampleTextBorder`, `EditorSampleTextBlock`, `EditorPreviewSwatch0`-`15`.
- Page preview: `ThemePreviewArea`, `SampleTextBorder`, `SampleTextBlock`, `Swatch0`-`15`.

- [ ] **Step 1: Write the failing tests.**

  Update `SettingsPageTests.AppearancePage_CollectsEveryFieldItLoaded` to the fields Appearance still owns, plus the readable stretch label:

```csharp
    [AvaloniaFact]
    public void AppearancePage_CollectsEveryFieldItLoaded()
    {
        using var root = SettingsTestRoot.Empty();
        root.CopyShippedTheme("GitHubLight.json");
        var page = new AppearancePage();
        page.Load(SessionFor(new TerminalSettings
        {
            ThemeName = "GitHub Light", WindowOpacity = 0.75, UiScale = 1.25, BlurEffect = "Mica",
            BackgroundImagePath = "/img.png", BackgroundImageOpacity = 0.35, BackgroundImageStretch = "Uniform",
        }));
        var target = new TerminalSettings();

        page.Collect(target);

        Assert.Equal("GitHub Light", target.ThemeName);
        Assert.Equal(0.75, target.WindowOpacity, precision: 9);
        Assert.Equal(1.25, target.UiScale, precision: 9);
        Assert.Equal("Mica", target.BlurEffect);
        Assert.Equal("/img.png", target.BackgroundImagePath);
        Assert.Equal(0.35, target.BackgroundImageOpacity, precision: 9);
        Assert.Equal("Uniform", target.BackgroundImageStretch);
        Assert.Equal("Fit", ((ComboBoxItem)page.FindControl<ComboBox>("BgImageStretchList")!.SelectedItem!).Content);
    }

    /// <summary>Review focus 1: hand-edited or older values open on today's fallback and save it.</summary>
    [AvaloniaTheory]
    [InlineData("uniform", "UniformToFill")]       // case differs: no match, fall back to Fill (crop)
    [InlineData("Tile", "UniformToFill")]
    [InlineData("None", "None")]
    public void AppearancePage_BackgroundStretch_UnknownValuesFallBackAsBefore(string stored, string saved)
    {
        using var root = SettingsTestRoot.Empty();
        var page = new AppearancePage();
        page.Load(SessionFor(new TerminalSettings { BackgroundImageStretch = stored }));
        var target = new TerminalSettings();

        page.Collect(target);

        Assert.Equal(saved, target.BackgroundImageStretch);
    }

    [AvaloniaTheory]
    [InlineData("Transparent", "Acrylic")]
    [InlineData("mica", "Acrylic")]                 // the old loop compared ordinally
    [InlineData("None", "None")]
    public void AppearancePage_BlurEffect_UnknownValuesFallBackAsBefore(string stored, string saved)
    {
        using var root = SettingsTestRoot.Empty();
        var page = new AppearancePage();
        page.Load(SessionFor(new TerminalSettings { BlurEffect = stored }));
        var target = new TerminalSettings();

        page.Collect(target);

        Assert.Equal(saved, target.BlurEffect);
    }
```

`tests/Ntilde.App.Tests/Core/SettingsThemeEditorTests.cs`:

```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Ntilde.Settings;
using Ntilde.Settings.Pages;

namespace Ntilde.Tests.Core;

/// <summary>The theme editor as a sub-page of Appearance (spec §7).</summary>
public sealed class SettingsThemeEditorTests
{
    private static void Click(Control page, string button)
        => page.FindControl<Button>(button)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static (SettingsTestRoot Root, Ntilde.SettingsWindow Window, AppearancePage Page) Open(string themeName = "GitHub Light")
    {
        var root = SettingsTestRoot.WithSettings($$"""{"ThemeName":"{{themeName}}"}""");
        root.CopyShippedTheme("GitHubLight.json");
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Appearance));
        return (root, window, window.GetPage<AppearancePage>());
    }

    [AvaloniaFact]
    public void Edit_OpensTheSubPageForTheSelectedTheme_WithAppearanceStillSelected()
    {
        var (root, window, page) = Open();
        using (root)
        {
            Click(page, "BtnEditTheme");

            Assert.True(page.IsThemeEditorOpen);
            Assert.False(page.FindControl<ScrollViewer>("AppearanceMainView")!.IsVisible);
            Assert.Equal("GitHub Light", page.FindControl<TextBox>("EditThemeNameInput")!.Text);
            Assert.Equal("GitHub Light", page.FindControl<TextBlock>("ThemeEditorTitle")!.Text);
            Assert.Equal(SettingsPage.Appearance, window.CurrentPage);
            Assert.Same(window.NavItem(SettingsPage.Appearance), window.FindControl<ListBox>("SideNav")!.SelectedItem);
        }
    }

    [AvaloniaFact]
    public void Back_DropsUnsavedEdits()
    {
        var (root, _, page) = Open();
        using (root)
        {
            Click(page, "BtnEditTheme");
            string originalForeground = page.FindControl<TextBox>("EditThemeFgInput")!.Text!;
            page.FindControl<TextBox>("EditThemeNameInput")!.Text = "Renamed";
            page.FindControl<TextBox>("EditThemeFgInput")!.Text = "#ff0000";

            Click(page, "BtnThemeEditorBack");
            Assert.False(page.IsThemeEditorOpen);
            Click(page, "BtnEditTheme");

            Assert.Equal("GitHub Light", page.FindControl<TextBox>("EditThemeNameInput")!.Text);
            Assert.Equal(originalForeground, page.FindControl<TextBox>("EditThemeFgInput")!.Text);
        }
    }

    [AvaloniaFact]
    public void New_OpensTheEditorOnANewThemeDraft()
    {
        var (root, _, page) = Open();
        using (root)
        {
            Click(page, "BtnNewTheme");

            Assert.True(page.IsThemeEditorOpen);
            Assert.Equal("New Theme", page.FindControl<TextBox>("EditThemeNameInput")!.Text);
        }
    }

    [AvaloniaFact]
    public void Delete_RemovesTheTheme_AndReturnsToAppearance()
    {
        var (root, _, page) = Open();
        using (root)
        {
            Click(page, "BtnEditTheme");
            Click(page, "BtnDeleteTheme");

            Assert.False(page.IsThemeEditorOpen);
            Assert.DoesNotContain(
                page.FindControl<ComboBox>("ThemeList")!.Items.OfType<ComboBoxItem>(),
                item => (string?)item.Content == "GitHub Light");
        }
    }

    /// <summary>
    /// Correction 7: saving repaints Settings with the theme just saved, not the theme
    /// settings.json named when the window opened.
    /// </summary>
    [AvaloniaFact]
    public void Save_RepaintsSettingsWithTheSavedTheme()
    {
        var (root, window, page) = Open(themeName: "Default"); // stored theme is dark
        using (root)
        {
            var themes = page.FindControl<ComboBox>("ThemeList")!;
            themes.SelectedItem = themes.Items.OfType<ComboBoxItem>().Single(i => (string?)i.Content == "GitHub Light");
            Click(page, "BtnEditTheme");

            Click(page, "BtnSaveTheme");

            Assert.Equal(Color.Parse("#ffffff"), ((SolidColorBrush)window.FindResource("NtWindowBg")!).Color);
        }
    }

    /// <summary>Review focus 5: at the minimum width both editor columns are fully on screen.</summary>
    [AvaloniaFact]
    public void AtTheMinimumWidth_BothEditorColumnsFit()
    {
        var (root, window, page) = Open();
        using (root)
        {
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            try
            {
                window.Show();
                Click(page, "BtnEditTheme");
                Dispatcher.UIThread.RunJobs();

                var preview = page.FindControl<Border>("EditorPreviewArea")!;
                var swatches = page.FindControl<UniformGrid>("EditorSwatchGrid")!;
                double previewLeft = preview.TranslatePoint(default, window)!.Value.X;
                double previewRight = preview.TranslatePoint(new Point(preview.Bounds.Width, 0), window)!.Value.X;
                double swatchesRight = swatches.TranslatePoint(new Point(swatches.Bounds.Width, 0), window)!.Value.X;

                Assert.True(previewRight <= window.ClientSize.Width + 0.5, $"Preview ends at {previewRight}, past {window.ClientSize.Width}.");
                Assert.True(swatchesRight <= previewLeft, "The swatch grid runs under the preview column.");
            }
            finally
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public void JumpingToAnAppearanceRow_ClosesTheEditorFirst()
    {
        var (root, window, page) = Open();
        using (root)
        {
            try
            {
                window.Show();
                Click(page, "BtnEditTheme");

                Assert.True(window.RevealRow(SettingsPage.Appearance, "window-opacity"));
                Dispatcher.UIThread.RunJobs();

                Assert.False(page.IsThemeEditorOpen);
                var row = page.GetLogicalDescendants().OfType<SettingRow>().Single(r => r.RowId == "window-opacity");
                Assert.Same(window.FindResource("NtBlueDim"), row.Background);
            }
            finally
            {
                window.Close();
            }
        }
    }
}
```

- [ ] **Step 2: Run them and watch them fail.** Expected: compile errors (`IsThemeEditorOpen`, the new control names), plus label failures.

- [ ] **Step 3: Write `IHasSubPage.cs`, and teach `RevealRow` about it.**

```csharp
namespace Ntilde.Settings;

/// <summary>A page that can show a sub-page in place of its own content (Appearance's theme editor).</summary>
public interface IHasSubPage
{
    bool IsSubPageOpen { get; }

    /// <summary>Returns to the page's own content, so a deep link or search result into it can be shown.</summary>
    void CloseSubPage();
}
```

In `SettingsWindow.RevealRow`, add this right after the `page != _currentPage` check:

```csharp
            // A row on a page whose sub-page is showing has no layout yet. Close the sub-page and
            // reveal after the layout pass that brings the page's own content back.
            if (GetPage(page) is IHasSubPage { IsSubPageOpen: true } host)
            {
                host.CloseSubPage();
                Dispatcher.UIThread.Post(() => RevealRow(page, rowId), DispatcherPriority.Loaded);
                return true;
            }
```

- [ ] **Step 4: Replace `AppearancePage.axaml`.**

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:settings="clr-namespace:Ntilde.Settings"
             x:Class="Ntilde.Settings.Pages.AppearancePage">
    <Panel>
        <!-- ── The page ── -->
        <ScrollViewer Name="AppearanceMainView" VerticalScrollBarVisibility="Auto">
            <StackPanel Classes="Page">
                <StackPanel Classes="PageHeader">
                    <TextBlock Classes="H1" Text="Appearance"/>
                    <TextBlock Classes="Subtitle" Text="Theme, window and background image."/>
                </StackPanel>

                <settings:SettingGroup Header="THEME">
                    <settings:SettingRow RowId="theme" Title="Theme" Stacked="True"
                                         Description="Colours for the terminal and for this window."
                                         Keywords="color colour palette dark light scheme">
                        <StackPanel Spacing="12">
                            <Grid ColumnDefinitions="*,Auto,Auto,Auto" ColumnSpacing="6">
                                <ComboBox Name="ThemeList" Grid.Column="0" HorizontalAlignment="Stretch" AutomationProperties.Name="Theme"/>
                                <Button Name="BtnEditTheme" Grid.Column="1" Classes="Pill" Content="Edit"/>
                                <Button Name="BtnImportTheme" Grid.Column="2" Classes="Pill" Content="Import…"/>
                                <Button Name="BtnNewTheme" Grid.Column="3" Classes="Pill" Content="+ New" ToolTip.Tip="New theme"/>
                            </Grid>
                            <Border Name="ThemePreviewArea" Classes="ThemePreview" Background="#111">
                                <StackPanel Spacing="8">
                                    <Border Name="SampleTextBorder" Padding="10" CornerRadius="4">
                                        <TextBlock Name="SampleTextBlock" Text="The quick brown fox jumps over the lazy dog." FontFamily="{DynamicResource NtMono}" FontSize="12"/>
                                    </Border>
                                    <UniformGrid Rows="2" Columns="8" HorizontalAlignment="Left">
                                        <Border Name="Swatch0"  Width="20" Height="20" Margin="2" CornerRadius="3" ToolTip.Tip="Black"/>
                                        <Border Name="Swatch1"  Width="20" Height="20" Margin="2" CornerRadius="3" ToolTip.Tip="Red"/>
                                        <Border Name="Swatch2"  Width="20" Height="20" Margin="2" CornerRadius="3" ToolTip.Tip="Green"/>
                                        <Border Name="Swatch3"  Width="20" Height="20" Margin="2" CornerRadius="3" ToolTip.Tip="Yellow"/>
                                        <Border Name="Swatch4"  Width="20" Height="20" Margin="2" CornerRadius="3" ToolTip.Tip="Blue"/>
                                        <Border Name="Swatch5"  Width="20" Height="20" Margin="2" CornerRadius="3" ToolTip.Tip="Magenta"/>
                                        <Border Name="Swatch6"  Width="20" Height="20" Margin="2" CornerRadius="3" ToolTip.Tip="Cyan"/>
                                        <Border Name="Swatch7"  Width="20" Height="20" Margin="2" CornerRadius="3" ToolTip.Tip="White"/>
                                        <Border Name="Swatch8"  Width="20" Height="20" Margin="2" CornerRadius="3" ToolTip.Tip="Bright Black"/>
                                        <Border Name="Swatch9"  Width="20" Height="20" Margin="2" CornerRadius="3" ToolTip.Tip="Bright Red"/>
                                        <Border Name="Swatch10" Width="20" Height="20" Margin="2" CornerRadius="3" ToolTip.Tip="Bright Green"/>
                                        <Border Name="Swatch11" Width="20" Height="20" Margin="2" CornerRadius="3" ToolTip.Tip="Bright Yellow"/>
                                        <Border Name="Swatch12" Width="20" Height="20" Margin="2" CornerRadius="3" ToolTip.Tip="Bright Blue"/>
                                        <Border Name="Swatch13" Width="20" Height="20" Margin="2" CornerRadius="3" ToolTip.Tip="Bright Magenta"/>
                                        <Border Name="Swatch14" Width="20" Height="20" Margin="2" CornerRadius="3" ToolTip.Tip="Bright Cyan"/>
                                        <Border Name="Swatch15" Width="20" Height="20" Margin="2" CornerRadius="3" ToolTip.Tip="Bright White"/>
                                    </UniformGrid>
                                </StackPanel>
                            </Border>
                        </StackPanel>
                    </settings:SettingRow>
                </settings:SettingGroup>

                <settings:SettingGroup Header="WINDOW">
                    <settings:SettingRow RowId="window-opacity" Title="Window opacity" Description="Applies when the window is focused." Keywords="transparency transparent see-through">
                        <Grid Classes="Wide" ColumnDefinitions="*,Auto">
                            <Slider Name="WindowOpacitySlider" Minimum="0.2" Maximum="1.0" Value="1.0" TickFrequency="0.01" IsSnapToTickEnabled="True" AutomationProperties.Name="Window opacity"/>
                            <TextBlock Name="OpacityValueDisplay" Grid.Column="1" Classes="SliderValue" Text="100%"/>
                        </Grid>
                    </settings:SettingRow>
                    <settings:SettingRow RowId="interface-scale" Title="Interface scale"
                                         Description="Zooms tabs, sidebars, dialogs and settings. The terminal grows with it, on top of its font size."
                                         Keywords="zoom size dpi scaling bigger smaller">
                        <Grid Classes="Wide" ColumnDefinitions="*,Auto">
                            <Slider Name="UiScaleSlider" Minimum="0.8" Maximum="2.0" Value="1.0" TickFrequency="0.05" IsSnapToTickEnabled="True" AutomationProperties.Name="Interface scale"/>
                            <TextBlock Name="UiScaleValueDisplay" Grid.Column="1" Classes="SliderValue" Text="100%"/>
                        </Grid>
                    </settings:SettingRow>
                    <settings:SettingRow RowId="blur-effect" Title="Blur effect" Description="Acrylic / Mica where supported by the OS." Keywords="acrylic mica transparency glass">
                        <ComboBox Name="BlurList" Classes="Wide" AutomationProperties.Name="Blur effect">
                            <ComboBoxItem Content="Acrylic" Tag="Acrylic"/>
                            <ComboBoxItem Content="Mica" Tag="Mica"/>
                            <ComboBoxItem Content="None" Tag="None"/>
                        </ComboBox>
                    </settings:SettingRow>
                </settings:SettingGroup>

                <settings:SettingGroup Header="BACKGROUND IMAGE">
                    <settings:SettingRow RowId="background-image" Title="Background image" Description="PNG / JPG / BMP, per-theme." Keywords="wallpaper picture photo">
                        <Grid Classes="Wide" ColumnDefinitions="*,Auto,Auto">
                            <TextBox Name="BgImagePathInput" PlaceholderText="Path to image file…" AutomationProperties.Name="Background image path"/>
                            <Button Name="BtnBrowseImage" Grid.Column="1" Classes="Pill" Content="…" Width="40" Margin="6,0,0,0" ToolTip.Tip="Browse"/>
                            <Button Name="BtnClearImage" Grid.Column="2" Classes="IconBtn" Content="✕" Margin="4,0,0,0" ToolTip.Tip="Clear"/>
                        </Grid>
                    </settings:SettingRow>
                    <settings:SettingRow RowId="background-image-opacity" Title="Image opacity" Keywords="transparency wallpaper">
                        <Grid Classes="Wide" ColumnDefinitions="*,Auto">
                            <Slider Name="BgImageOpacitySlider" Minimum="0" Maximum="1" AutomationProperties.Name="Image opacity"/>
                            <TextBlock Name="BgImageOpacityDisplay" Grid.Column="1" Classes="SliderValue" Text="50%"/>
                        </Grid>
                    </settings:SettingRow>
                    <settings:SettingRow RowId="background-image-stretch" Title="Stretch mode" Keywords="fit fill crop wallpaper">
                        <ComboBox Name="BgImageStretchList" Classes="Wide" AutomationProperties.Name="Stretch mode">
                            <ComboBoxItem Content="Don't stretch" Tag="None"/>
                            <ComboBoxItem Content="Stretch" Tag="Fill"/>
                            <ComboBoxItem Content="Fit" Tag="Uniform"/>
                            <ComboBoxItem Content="Fill (crop)" Tag="UniformToFill"/>
                        </ComboBox>
                    </settings:SettingRow>
                </settings:SettingGroup>
            </StackPanel>
        </ScrollViewer>

        <!-- ── Edit theme: a sub-page of Appearance, not in the sidebar ── -->
        <ScrollViewer Name="ThemeEditorView" VerticalScrollBarVisibility="Auto" IsVisible="False">
            <StackPanel Classes="Page">
                <Button Name="BtnThemeEditorBack" Classes="Link" Content="‹ Appearance" Margin="0,0,0,12" AutomationProperties.Name="Back to Appearance"/>
                <StackPanel Classes="PageHeader">
                    <TextBlock Name="ThemeEditorTitle" Classes="H1" Text="Edit theme"/>
                    <TextBlock Classes="Subtitle" Text="Saving a theme writes it to disk straight away. Cancel does not undo it."/>
                </StackPanel>

                <!-- 508 px of content at the 780 px minimum: 264 for the fields (the swatch grid is 256), 24 gap, 220 preview. -->
                <Grid ColumnDefinitions="*,220" ColumnSpacing="24">
                    <StackPanel Grid.Column="0" Spacing="14">
                        <StackPanel Spacing="4">
                            <TextBlock Classes="RowDesc" Text="Theme name"/>
                            <TextBox Name="EditThemeNameInput" AutomationProperties.Name="Theme name"/>
                        </StackPanel>
                        <UniformGrid Columns="3">
                            <StackPanel Margin="0,0,6,0" Spacing="4">
                                <TextBlock Classes="RowDesc" Text="Foreground"/>
                                <TextBox Name="EditThemeFgInput" PlaceholderText="#RRGGBB" AutomationProperties.Name="Foreground"/>
                            </StackPanel>
                            <StackPanel Margin="3,0,3,0" Spacing="4">
                                <TextBlock Classes="RowDesc" Text="Background"/>
                                <TextBox Name="EditThemeBgInput" PlaceholderText="#RRGGBB" AutomationProperties.Name="Background"/>
                            </StackPanel>
                            <StackPanel Margin="6,0,0,0" Spacing="4">
                                <TextBlock Classes="RowDesc" Text="Cursor"/>
                                <TextBox Name="EditThemeCursorInput" PlaceholderText="#RRGGBB" AutomationProperties.Name="Cursor"/>
                            </StackPanel>
                        </UniformGrid>
                        <StackPanel Spacing="6">
                            <TextBlock Classes="RowDesc" Text="ANSI palette"/>
                            <UniformGrid Name="EditorSwatchGrid" Rows="2" Columns="8" HorizontalAlignment="Left">
                                <Button Name="EditSwatch0"  Width="28" Height="28" Margin="2" Background="Black" CornerRadius="3"/>
                                <Button Name="EditSwatch1"  Width="28" Height="28" Margin="2" Background="Black" CornerRadius="3"/>
                                <Button Name="EditSwatch2"  Width="28" Height="28" Margin="2" Background="Black" CornerRadius="3"/>
                                <Button Name="EditSwatch3"  Width="28" Height="28" Margin="2" Background="Black" CornerRadius="3"/>
                                <Button Name="EditSwatch4"  Width="28" Height="28" Margin="2" Background="Black" CornerRadius="3"/>
                                <Button Name="EditSwatch5"  Width="28" Height="28" Margin="2" Background="Black" CornerRadius="3"/>
                                <Button Name="EditSwatch6"  Width="28" Height="28" Margin="2" Background="Black" CornerRadius="3"/>
                                <Button Name="EditSwatch7"  Width="28" Height="28" Margin="2" Background="Black" CornerRadius="3"/>
                                <Button Name="EditSwatch8"  Width="28" Height="28" Margin="2" Background="Black" CornerRadius="3"/>
                                <Button Name="EditSwatch9"  Width="28" Height="28" Margin="2" Background="Black" CornerRadius="3"/>
                                <Button Name="EditSwatch10" Width="28" Height="28" Margin="2" Background="Black" CornerRadius="3"/>
                                <Button Name="EditSwatch11" Width="28" Height="28" Margin="2" Background="Black" CornerRadius="3"/>
                                <Button Name="EditSwatch12" Width="28" Height="28" Margin="2" Background="Black" CornerRadius="3"/>
                                <Button Name="EditSwatch13" Width="28" Height="28" Margin="2" Background="Black" CornerRadius="3"/>
                                <Button Name="EditSwatch14" Width="28" Height="28" Margin="2" Background="Black" CornerRadius="3"/>
                                <Button Name="EditSwatch15" Width="28" Height="28" Margin="2" Background="Black" CornerRadius="3"/>
                            </UniformGrid>
                        </StackPanel>
                        <StackPanel Orientation="Horizontal" Spacing="8">
                            <Button Name="BtnSaveTheme" Classes="Primary" Content="Save theme"/>
                            <Button Name="BtnDeleteTheme" Classes="Danger" Content="Delete"/>
                            <TextBlock Name="ThemeEditorStatus" VerticalAlignment="Center" FontSize="12" Foreground="{DynamicResource NtFg3}"/>
                        </StackPanel>
                    </StackPanel>

                    <Border Name="EditorPreviewArea" Grid.Column="1" Classes="ThemePreview" Background="#111" VerticalAlignment="Top">
                        <StackPanel Spacing="10">
                            <TextBlock Name="EditorPreviewLabel" Classes="RowDesc" Text="Preview"/>
                            <Border Name="EditorSampleTextBorder" Padding="10" CornerRadius="4">
                                <TextBlock Name="EditorSampleTextBlock" Text="The quick brown fox jumps over the lazy dog." TextWrapping="Wrap" FontFamily="{DynamicResource NtMono}" FontSize="12"/>
                            </Border>
                            <UniformGrid Rows="2" Columns="8" HorizontalAlignment="Left">
                                <Border Name="EditorPreviewSwatch0"  Width="20" Height="20" Margin="2" CornerRadius="3"/>
                                <Border Name="EditorPreviewSwatch1"  Width="20" Height="20" Margin="2" CornerRadius="3"/>
                                <Border Name="EditorPreviewSwatch2"  Width="20" Height="20" Margin="2" CornerRadius="3"/>
                                <Border Name="EditorPreviewSwatch3"  Width="20" Height="20" Margin="2" CornerRadius="3"/>
                                <Border Name="EditorPreviewSwatch4"  Width="20" Height="20" Margin="2" CornerRadius="3"/>
                                <Border Name="EditorPreviewSwatch5"  Width="20" Height="20" Margin="2" CornerRadius="3"/>
                                <Border Name="EditorPreviewSwatch6"  Width="20" Height="20" Margin="2" CornerRadius="3"/>
                                <Border Name="EditorPreviewSwatch7"  Width="20" Height="20" Margin="2" CornerRadius="3"/>
                                <Border Name="EditorPreviewSwatch8"  Width="20" Height="20" Margin="2" CornerRadius="3"/>
                                <Border Name="EditorPreviewSwatch9"  Width="20" Height="20" Margin="2" CornerRadius="3"/>
                                <Border Name="EditorPreviewSwatch10" Width="20" Height="20" Margin="2" CornerRadius="3"/>
                                <Border Name="EditorPreviewSwatch11" Width="20" Height="20" Margin="2" CornerRadius="3"/>
                                <Border Name="EditorPreviewSwatch12" Width="20" Height="20" Margin="2" CornerRadius="3"/>
                                <Border Name="EditorPreviewSwatch13" Width="20" Height="20" Margin="2" CornerRadius="3"/>
                                <Border Name="EditorPreviewSwatch14" Width="20" Height="20" Margin="2" CornerRadius="3"/>
                                <Border Name="EditorPreviewSwatch15" Width="20" Height="20" Margin="2" CornerRadius="3"/>
                            </UniformGrid>
                        </StackPanel>
                    </Border>
                </Grid>
            </StackPanel>
        </ScrollViewer>
    </Panel>
</UserControl>
```

- [ ] **Step 5: Replace `AppearancePage.axaml.cs`.** The handler bodies are `ORIG`'s with the Move rules applied. Three behaviours change, each commented in the code: two previews, the editor as a sub-page, and Save repainting with the saved theme.

```csharp
using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Ntilde.Shell;
using Ntilde.VT;

namespace Ntilde.Settings.Pages;

/// <summary>Appearance: the theme (with its editor as a sub-page), the window, and the background image.</summary>
public partial class AppearancePage : UserControl, ISettingsPage, IHasSubPage
{
    private SettingsSession? _session;
    private TerminalTheme? _editingTheme;

    public AppearancePage()
    {
        InitializeComponent();
        WireThemeRow();
        WireThemeEditor();
        WireWindowRows();
        WireBackgroundImageRows();
    }

    private enum PreviewSurface
    {
        Page,
        Editor,
    }

    public SettingsPage Page => SettingsPage.Appearance;

    public Control View => this;

    /// <summary>True while the theme editor sub-page is showing.</summary>
    internal bool IsThemeEditorOpen => this.FindControl<ScrollViewer>("ThemeEditorView")!.IsVisible;

    private SettingsSession Session => _session ?? throw new InvalidOperationException("AppearancePage used before Load.");

    public void Load(SettingsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        PopulateThemes();
        LoadControls();

        // The theme selection usually paints the preview via SelectionChanged, but only when an
        // item matches ThemeName - a name the manager normalizes (legacy "Default (Dark)") or a
        // since-deleted theme leaves the combo empty. Paint it from the active theme directly.
        UpdateThemePreview(session.Settings.ActiveTheme, PreviewSurface.Page);
    }

    public SettingsValidationError? Validate() => null;

    public void Collect(TerminalSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.WindowOpacity = Slider("WindowOpacitySlider").Value;
        target.UiScale = UiScale.Clamp(Slider("UiScaleSlider").Value);
        if (Combo("ThemeList").SelectedItem is ComboBoxItem themeItem)
        {
            target.ThemeName = themeItem.Content?.ToString() ?? "Default";
        }

        if (ComboTags.SelectedValue(Combo("BlurList")) is { } blur)
        {
            target.BlurEffect = blur;
        }

        target.BackgroundImagePath = this.FindControl<TextBox>("BgImagePathInput")!.Text ?? "";
        target.BackgroundImageOpacity = Slider("BgImageOpacitySlider").Value;
        if (ComboTags.SelectedValue(Combo("BgImageStretchList")) is { } stretch)
        {
            target.BackgroundImageStretch = stretch;
        }
    }

    public bool IsSubPageOpen => IsThemeEditorOpen;

    public void CloseSubPage() => ShowMainView();

    private void LoadControls()
    {
        TerminalSettings settings = Session.Settings;
        Slider("WindowOpacitySlider").Value = settings.WindowOpacity;
        Text("OpacityValueDisplay").Text = FormatPercent(settings.WindowOpacity);
        Slider("UiScaleSlider").Value = UiScale.Clamp(settings.UiScale);
        Text("UiScaleValueDisplay").Text = FormatUiScale(Slider("UiScaleSlider").Value);
        this.FindControl<TextBox>("BgImagePathInput")!.Text = settings.BackgroundImagePath;
        Slider("BgImageOpacitySlider").Value = settings.BackgroundImageOpacity;
        Text("BgImageOpacityDisplay").Text = FormatPercent(settings.BackgroundImageOpacity);

        // Legacy settings store "Default (Dark)" while the combo lists the manager's canonical
        // "Default". Normalize that one alias rather than resolving through GetTheme: for a name
        // GetTheme cannot find it falls back to the Default theme OBJECT, whose .Name would match
        // the "Default" item and silently rewrite the stored name on Save. With no match the
        // selection stays empty and Collect leaves ThemeName alone.
        string themeName = settings.ThemeName == "Default (Dark)" ? "Default" : settings.ThemeName;
        SelectTheme(themeName);

        ComboTags.Select(Combo("BlurList"), settings.BlurEffect, StringComparison.Ordinal, fallbackIndex: 0);
        ComboTags.Select(Combo("BgImageStretchList"), settings.BackgroundImageStretch, StringComparison.Ordinal, fallbackIndex: 3); // Fill (crop)
    }

    // ── Theme ─────────────────────────────────────────────────────────────────

    private void WireThemeRow()
    {
        var themeList = Combo("ThemeList");
        themeList.SelectionChanged += (_, _) =>
        {
            if (_session is null || themeList.SelectedItem is not ComboBoxItem { Content: { } content })
            {
                return;
            }

            string themeName = content.ToString() ?? "Default";
            TerminalTheme theme = Session.Settings.ThemeManager.GetTheme(themeName);
            UpdateThemePreview(theme, PreviewSurface.Page);
            Session.Preview.Theme(themeName);
            Session.Preview.RepaintWindow(theme);
        };

        this.FindControl<Button>("BtnEditTheme")!.Click += (_, _) =>
        {
            if (Combo("ThemeList").SelectedItem is ComboBoxItem item)
            {
                OpenThemeEditor(Session.Settings.ThemeManager.GetTheme(item.Content?.ToString() ?? "Default").Clone());
            }
        };

        this.FindControl<Button>("BtnNewTheme")!.Click += (_, _) => OpenThemeEditor(new TerminalTheme { Name = "New Theme" });

        this.FindControl<Button>("BtnImportTheme")!.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not { } topLevel)
            {
                return;
            }

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import Theme",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Theme Files") { Patterns = new[] { "*.json", "*.itermcolors" } },
                    new FilePickerFileType("Alacritty Theme") { Patterns = new[] { "*.toml" } },
                },
            });
            if (files.Count == 0)
            {
                return;
            }

            string importedThemeName = Session.Settings.ThemeManager.ImportTheme(files[0].Path.LocalPath);
            if (string.IsNullOrEmpty(importedThemeName))
            {
                return;
            }

            PopulateThemes();
            Session.RaiseThemesChanged();
            SelectTheme(importedThemeName);
            Session.Preview.Theme(importedThemeName);
        };
    }

    private void PopulateThemes()
    {
        Session.Settings.ThemeManager.LoadThemes();
        var themeList = Combo("ThemeList");
        themeList.Items.Clear();
        foreach (string theme in Session.Settings.ThemeManager.GetAvailableThemes().OrderBy(t => t))
        {
            themeList.Items.Add(new ComboBoxItem { Content = theme });
        }
    }

    private void SelectTheme(string name)
    {
        var themeList = Combo("ThemeList");
        if (themeList.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Content?.ToString() == name) is { } match)
        {
            themeList.SelectedItem = match;
        }
    }

    // ── Theme editor sub-page ─────────────────────────────────────────────────

    private void WireThemeEditor()
    {
        this.FindControl<Button>("BtnThemeEditorBack")!.Click += (_, _) => ShowMainView();

        var nameInput = this.FindControl<TextBox>("EditThemeNameInput")!;
        var status = Text("ThemeEditorStatus");

        this.FindControl<Button>("BtnSaveTheme")!.Click += (_, _) =>
        {
            if (_editingTheme is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(nameInput.Text))
            {
                status.Text = "Name required";
                return;
            }

            _editingTheme.Name = nameInput.Text;
            Session.Settings.ThemeManager.SaveTheme(_editingTheme);
            PopulateThemes();
            Session.RaiseThemesChanged();
            SelectTheme(_editingTheme.Name);
            Session.Settings.RefreshActiveTheme();
            Session.Preview.Theme(_editingTheme.Name);

            // Repaint with the theme just saved. The old editor re-applied the theme named in
            // settings.json, so saving an edit of any other theme left Settings in the wrong
            // colours until it was reopened.
            Session.Preview.RepaintWindow(Session.Settings.ThemeManager.GetTheme(_editingTheme.Name));

            Text("ThemeEditorTitle").Text = _editingTheme.Name;
            status.Text = "Saved!";
            DispatcherTimer.RunOnce(() => status.Text = "", TimeSpan.FromSeconds(2));
        };

        this.FindControl<Button>("BtnDeleteTheme")!.Click += (_, _) =>
        {
            if (_editingTheme is null || _editingTheme.Name == "Default")
            {
                return;
            }

            Session.Settings.ThemeManager.DeleteTheme(_editingTheme.Name);
            PopulateThemes();
            Session.RaiseThemesChanged();
            Combo("ThemeList").SelectedIndex = 0;
            ShowMainView();
        };

        for (int i = 0; i < 16; i++)
        {
            int index = i;
            var swatch = this.FindControl<Button>($"EditSwatch{index}")!;
            swatch.Click += (_, _) => OpenSwatchFlyout(swatch, index);
        }

        WireColourInput("EditThemeFgInput", (theme, colour) => theme.Foreground = colour);
        WireColourInput("EditThemeBgInput", (theme, colour) => theme.Background = colour);
        WireColourInput("EditThemeCursorInput", (theme, colour) => theme.CursorColor = colour);
    }

    /// <summary>Real-time updates for the foreground / background / cursor inputs (ORIG 418-442).</summary>
    private void WireColourInput(string name, Action<TerminalTheme, TermColor> apply)
    {
        var input = this.FindControl<TextBox>(name)!;
        input.TextChanged += (_, _) =>
        {
            if (Color.TryParse(input.Text, out var colour) && _editingTheme != null)
            {
                apply(_editingTheme, TermColorHelper.FromAvaloniaColor(colour));
                UpdateThemePreview(_editingTheme, PreviewSurface.Editor);
            }
        };
    }

    private void OpenThemeEditor(TerminalTheme theme)
    {
        _editingTheme = theme;
        this.FindControl<TextBox>("EditThemeNameInput")!.Text = theme.Name;
        this.FindControl<TextBox>("EditThemeFgInput")!.Text = theme.Foreground.ToString();
        this.FindControl<TextBox>("EditThemeBgInput")!.Text = theme.Background.ToString();
        this.FindControl<TextBox>("EditThemeCursorInput")!.Text = theme.CursorColor.ToString();
        this.FindControl<Button>("BtnDeleteTheme")!.IsEnabled = theme.Name != "Default";
        Text("ThemeEditorTitle").Text = theme.Name;
        Text("ThemeEditorStatus").Text = "";

        UpdateEditorSwatches();
        UpdateThemePreview(theme, PreviewSurface.Editor);

        this.FindControl<ScrollViewer>("AppearanceMainView")!.IsVisible = false;
        this.FindControl<ScrollViewer>("ThemeEditorView")!.IsVisible = true;
    }

    /// <summary>Leaves the editor. Unsaved edits are dropped, as the old editor's ✕ did.</summary>
    private void ShowMainView()
    {
        _editingTheme = null;
        this.FindControl<ScrollViewer>("ThemeEditorView")!.IsVisible = false;
        this.FindControl<ScrollViewer>("AppearanceMainView")!.IsVisible = true;
    }

    // ORIG 3274-3285 (UpdateEditorSwatches) and 3287-3315 (OpenSwatchFlyout) go here, with
    // UpdateThemePreview(_editingTheme, "Editor") becoming UpdateThemePreview(_editingTheme, PreviewSurface.Editor).

    /// <summary>
    /// Paints a preview card: the compact one on the page, or the editor's. They are separate
    /// cards now that the editor is its own sub-page.
    /// </summary>
    private void UpdateThemePreview(TerminalTheme theme, PreviewSurface surface)
    {
        bool editor = surface == PreviewSurface.Editor;
        var background = new SolidColorBrush(theme.Background.ToAvaloniaColor());
        this.FindControl<Border>(editor ? "EditorPreviewArea" : "ThemePreviewArea")!.Background = background;
        this.FindControl<Border>(editor ? "EditorSampleTextBorder" : "SampleTextBorder")!.Background = background;
        this.FindControl<TextBlock>(editor ? "EditorSampleTextBlock" : "SampleTextBlock")!.Foreground =
            new SolidColorBrush(theme.Foreground.ToAvaloniaColor());

        if (editor)
        {
            // The label sits on the theme's own background; keep it readable.
            double luminance = (0.299 * theme.Background.R + 0.587 * theme.Background.G + 0.114 * theme.Background.B) / 255.0;
            Text("EditorPreviewLabel").Foreground = luminance > 0.5 ? Brushes.Black : Brushes.White;
        }

        string swatchPrefix = editor ? "EditorPreviewSwatch" : "Swatch";
        for (int i = 0; i < 16; i++)
        {
            this.FindControl<Border>($"{swatchPrefix}{i}")!.Background =
                new SolidColorBrush(theme.GetAnsiColor(i % 8, i >= 8).ToAvaloniaColor());
        }
    }

    // ── Window ────────────────────────────────────────────────────────────────

    private void WireWindowRows()
    {
        var opacity = Slider("WindowOpacitySlider");
        var opacityDisplay = Text("OpacityValueDisplay");
        opacity.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || _session is null)
            {
                return;
            }

            opacityDisplay.Text = FormatPercent(opacity.Value);
            Session.Preview.Opacity(opacity.Value);
        };

        var scale = Slider("UiScaleSlider");
        var scaleDisplay = Text("UiScaleValueDisplay");
        scale.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || _session is null)
            {
                return;
            }

            double clamped = UiScale.Clamp(scale.Value);
            scaleDisplay.Text = FormatUiScale(clamped);
            Session.Preview.UiScale(clamped);
        };

        var blur = Combo("BlurList");
        blur.SelectionChanged += (_, _) =>
        {
            if (_session != null && ComboTags.SelectedValue(blur) is { } effect)
            {
                Session.Preview.Blur(effect);
            }
        };
    }

    // ── Background image ──────────────────────────────────────────────────────

    private void WireBackgroundImageRows()
    {
        var path = this.FindControl<TextBox>("BgImagePathInput")!;
        var opacity = Slider("BgImageOpacitySlider");
        var opacityDisplay = Text("BgImageOpacityDisplay");

        path.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                TriggerBgUpdate();
            }
        };

        opacity.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
            {
                opacityDisplay.Text = FormatPercent(opacity.Value);
                TriggerBgUpdate();
            }
        };

        Combo("BgImageStretchList").SelectionChanged += (_, _) => TriggerBgUpdate();

        this.FindControl<Button>("BtnBrowseImage")!.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not { } topLevel)
            {
                return;
            }

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Background Image",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Images") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp" } },
                },
            });
            if (files.Count > 0)
            {
                path.Text = files[0].Path.LocalPath;
                TriggerBgUpdate();
            }
        };

        this.FindControl<Button>("BtnClearImage")!.Click += (_, _) =>
        {
            path.Text = "";
            TriggerBgUpdate();
        };
    }

    private void TriggerBgUpdate()
    {
        if (_session is null)
        {
            return;
        }

        string path = this.FindControl<TextBox>("BgImagePathInput")!.Text ?? "";
        double opacity = Slider("BgImageOpacitySlider").Value;
        string stretch = ComboTags.SelectedValue(Combo("BgImageStretchList")) ?? "UniformToFill";
        Session.Preview.BackgroundImage(path, opacity, stretch);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string FormatPercent(double value) => $"{(int)(value * 100)}%";

    private static string FormatUiScale(double scale) => $"{(int)Math.Round(scale * 100)}%";

    private Slider Slider(string name) => this.FindControl<Slider>(name)!;

    private ComboBox Combo(string name) => this.FindControl<ComboBox>(name)!;

    private TextBlock Text(string name) => this.FindControl<TextBlock>(name)!;
}
```

- [ ] **Step 6: Run the tests.** Use the targeted run with `SettingsThemeEditorTests`, then `SettingsPageTests`, then `Settings`. Expected: PASS, with the round trip unchanged.

**If `AtTheMinimumWidth_BothEditorColumnsFit` fails:** read the numbers in the message.
- If the preview overruns, reduce the preview column to `200` and the editor preview swatches to `18`×`18`.
- If the swatches run into the preview, reduce `ColumnSpacing` to `16`.

Do not add a horizontal scroll bar.

- [ ] **Step 7: Commit.**

```bash
git add -A src/Ntilde.App tests/Ntilde.App.Tests/Core
git commit -m "feat(settings): Appearance rebuilt with the theme editor as a sub-page" -m "Saving a theme now repaints Settings with the theme just saved, not the one stored when the window opened." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

## Task 3.7: Remove the hidden SSH panel from Profiles

**Files:**
- Modify: `src/Ntilde.App/Settings/Pages/ProfilesPage.axaml(.cs)`
- Test: `SettingsPageTests.cs`

**Interfaces:** `ProfilesPage` stops being `IDisposable`, because there is no forwards timer left.

- [ ] **Step 1: Write the failing test.** Append it to `SettingsPageTests`, and remove the three `page.Dispose();` calls from the Profiles tests:

```csharp
    [AvaloniaFact]
    public void ProfilesPage_HasNoSshEditor()
    {
        var page = new ProfilesPage();

        Assert.Null(page.FindControl<Control>("SshSettingsPanel"));
        Assert.Null(page.FindControl<Control>("ProfileTypeList"));
        Assert.Null(page.FindControl<Control>("ForwardsList"));
    }
```

- [ ] **Step 2: Run it and watch it fail.** Expected: FAIL, because the panel is still there.

- [ ] **Step 3: Remove the dead UI and its code.**

  In `ProfilesPage.axaml`, delete:
  - the `StackPanel` holding the "Connection type" label and `ProfileTypeList`;
  - the whole `SshSettingsPanel` `StackPanel` (the SSH header, host and port, username, password, authentication, jump host, port forwarding).

  In `ProfilesPage.axaml.cs`, delete:
  - the type list and SSH-input wiring (`ORIG` 594-666) and the `btnAddRule` wiring (956);
  - the `_statusTimer` field, its construction, `Dispose()` and `IDisposable`;
  - `PopulateJumpHostList`, `RefreshForwardsList`, `CheckIfPortIsListening`, `BtnAddForward_Click` and `BtnRemoveForward_Click`;
  - in `SwitchSelectedProfile`, the `ProfileTypeList`, `SshSettingsPanel` and five SSH-field lines (`ORIG` 1843-1853), and its "SSH profile editing moved" comment;
  - in `ClearProfileEditorFields`, the five SSH-field lines (`ORIG` 1402-1411);
  - the usings that become unused (`System.Net.NetworkInformation`, `Avalonia.Controls.Shapes`).

  Add one line to the class summary: "SSH connections are edited in the connection manager; this page shows local profiles only."

- [ ] **Step 4: Run the tests.** Use the targeted run with `SettingsPageTests`, then `SettingsWindowRoundTripTests`, then `SettingsWindowBackupSectionTests`.

Expected: PASS. **Both round-trip tests pass unchanged**, which is the proof that removing the panel changed nothing.

- [ ] **Step 5: Commit.**

```bash
git add -A src/Ntilde.App/Settings tests/Ntilde.App.Tests/Core
git commit -m "refactor(settings): remove the never-visible SSH panel from Profiles" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

## Task 3.8: Docs and in-app strings name the new pages

**Files:** the docs below. Make each change exactly as given, then run the final check.

- [ ] **Step 1: Make the changes.**

| File | Find | Replace with |
|---|---|---|
| `docs/USER_MANUAL.md` (§1.2) | `Pages: Appearance, Profiles, Shortcuts, Command`<br>`Assist, Agent Access, SSH, Backup.` | `Pages: General, Appearance, Terminal, Tabs & title bar,`<br>`Shells & Profiles, Shortcuts, Command Assist, Agent Access, SSH and Backup & Restore.` |
| `docs/USER_MANUAL.md` | `size. You can also customize your preferred font family in Settings.` | `size. You can also choose the font family in Settings → Terminal.` |
| `docs/USER_MANUAL.md` (vertical tabs, around line 52) | `Settings → Appearance → **Tab strip orientation**` | `Settings → Tabs & title bar → **Tab strip orientation**` |
| `docs/USER_MANUAL.md` (backup, around line 258) | `Open **Settings → Backup**` | `Open **Settings → Backup & Restore**` |
| `docs/USER_MANUAL.md` (around line 319) | `or put the Agent Activity button on the title bar from Settings → Appearance.` | `or put the Agent Activity button on the title bar from Settings → Tabs & title bar.` |
| `docs/TABS_USER_MANUAL.md` (around line 74) | `Settings → Appearance → **Tab strip orientation**` | `Settings → Tabs & title bar → **Tab strip orientation**` |
| `docs/command-assist/CommandAssist.md` (around line 149) | `Settings → Terminal, under "Command assistant":` | `Settings → Command Assist:` |
| `docs/mcp/security.md` (around line 47) | `**Settings → Agent Access → Tab indicator** is set to `All`)` | `**Settings → Agent Access → Tab indicator** is set to **All activity**)` |
| `docs/mcp/security.md` (around line 53) | ``under the default `WritesOnly` tab setting`` | `under the default **Writes only** tab indicator` |
| `docs/ThemeSystem.md` (`## Theme Editor`) | the first two bullets: `Click the **Edit** button…` and `Adjust individual ANSI colors…` | `-   On Settings → Appearance, pick a theme and click **Edit** (or **+ New**). The editor opens in place of the page; **‹ Appearance** goes back without saving.`<br>`-   Adjust the foreground, background, cursor and the 16 ANSI colors; the preview beside the fields follows each change.` |
| `docs/ThemeSystem.md` (`## Theme Editor`) | `-   Save changes to persist them to the JSON file.` | `-   **Save theme** writes the JSON file straight away; the Settings window's Cancel does not undo it.` |

- [ ] **Step 2: Check the places the spec says stay valid**, and record the result in the PR body:
  - `README.md`: "Settings → Appearance → Window" (interface scale), "Settings → Backup & Restore" and "Settings → Agent access (observe)" all still match a page and row.
  - `docs/RemoteShellIntegration.md:227`, `docs/SSH_ROADMAP.md:51,73`, `docs/native-ssh/Native_SSH_Test_Matrix.md:85`, `docs/agent-host/DIRECTION.md:102` and `docs/mcp-dev-companion.md:105` all name pages or rows that still exist.
  - The in-app strings in `MainWindow.axaml.cs`, `NewSshConnectionViewModel.cs`, `SshSessionFactory.cs`, `AgentHostService.cs`, `AgentHostClient.cs` and `BackupTools.cs` say "Settings > SSH", "Settings → Agent access (observe) → …" or "Settings > Backup & Restore". All are still correct.
  - `UI/UiScaleWindowTheme.axaml:1` says "(Settings > Appearance > Window)", which is still correct.

- [ ] **Step 3: Final check.** Each command must print nothing outside `docs/superpowers/` and `docs/plans/`, which are historical:

```bash
git grep -n -E 'Pages: Appearance, Profiles|Settings → Appearance → \*\*Tab strip|Settings → Terminal, under' -- docs README.md
git grep -n -E 'Settings → Backup\*\*' -- docs
```

- [ ] **Step 4: Commit.**

```bash
git add docs README.md
git commit -m "docs: name the new Settings pages" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

## Task 3.9: Screenshots, AOT, gate, smoke test and PR 3

**Files:**
- Modify: `tests/Ntilde.App.Tests/Core/SettingsScreenshotHarness.cs` (capture the theme editor)

- [ ] **Step 1: Capture the theme editor too.** In `CaptureSettings`, after the page loop and before `finally`, add:

```csharp
            window.NavigateTo(new SettingsTarget(SettingsPage.Appearance));
            window.GetPage<Ntilde.Settings.Pages.AppearancePage>().FindControl<Button>("BtnEditTheme")!
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            written.AddRange(CaptureScrolled(window, output, $"settings-{slug}-ThemeEditor"));
```

- [ ] **Step 2: Take the screenshots and review every image.** Directory: `$env:TEMP\settings-shots\pr3`.

Check each of these, on dark and light:
- Every page has its header.
- Groups are evenly spaced, including the gap above WINDOW.
- Toggles line up on the right.
- "More" appears on the six rows and nowhere else.
- No text is clipped.
- The theme editor shows two columns.
- Only the selected glyph uses the accent colour.

Attach the dark and light sets to the PR.

- [ ] **Step 3: Run the full gate** (Conventions).

- [ ] **Step 4: AOT.** CI's `AOT Gate (windows-latest)` job runs because `.axaml` changed, and it must be green.

To check locally first (about 10 minutes), use the PowerShell tool:

```powershell
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"
scripts/build.ps1 clean src/Ntilde.App/Ntilde.App.csproj -c Release *> $null
scripts/build.ps1 publish src/Ntilde.App/Ntilde.App.csproj -c Release -r win-x64 "-p:PublishAot=true;SkipCliShim=true;TrimmerSingleWarn=false" -o "$env:TEMP\ntilde-aot" *> "$env:TEMP\settings-aot.log"
Select-String -Path "$env:TEMP\settings-aot.log" -Pattern "IL2026|IL3050"
```

Expected: no matches. The known, harmless `IL3000` in `Program.cs DescribeBuild` may appear; it is not one of these two codes.

- [ ] **Step 5: Give the user these smoke-test steps.** Run the AOT build if it was made, else the dev build.
  1. Ctrl+, opens **General**.
  2. Check that the sidebar shows GENERAL / INTERFACE / ASSISTANT / CONNECTION / DATA.
  3. On Appearance, change the opacity and the interface scale; the main window should preview live. Pick Stretch mode "Fit" and Save. Reopen: it still says Fit, and `settings.json` says `"BackgroundImageStretch": "Uniform"`.
  4. Click Edit on a theme. The editor fills the page, with "‹ Appearance" at the top. Change the background colour; the preview beside the fields follows it. Click "‹ Appearance": the edit is gone.
  5. Open the editor again, change a colour and click Save theme. Settings recolours with that theme.
  6. On Terminal, change the font. The terminal previews it.
  7. Right-click the title bar and choose Customize Title Bar…. It opens Tabs & title bar.
  8. On Command Assist, click "More" under Remember commands. The full privacy text appears. Click "Clear history" twice: the status shows under the row.
  9. On Agent Access, check that the tab indicator reads "Writes only" / "All activity".
  10. On Shells & Profiles, check that there is no SSH section.

- [ ] **Step 6: Commit the harness change, ask the user, then push and open the PR.**

```bash
git add tests/Ntilde.App.Tests/Core/SettingsScreenshotHarness.cs
git commit -m "test(settings): screenshot the theme editor sub-page" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push -u origin feat/settings-redesign-pages
gh pr create --base main --title "Settings redesign 3/5: topic pages, theme editor sub-page, readable labels" --body-file - <<'EOF'
Third of five PRs for the settings window redesign (spec: docs/superpowers/specs/2026-10-13-settings-window-redesign-design.md). This is the visible one.

**Pages**
- New: **General** (updates, notifications), **Terminal** (font, ligatures, shaping, scrollback), **Tabs & title bar** (tab orientation, title-bar actions). Appearance keeps theme, window and background image. Settings now opens on General.
- Every row is a `SettingRow` in a `SettingGroup`: one hairline per row, fixed group spacing (the missing gap above WINDOW is fixed), row ids for deep links.
- Six long descriptions moved behind "More", wording unchanged.

**Theme editor** is a sub-page ("‹ Appearance" back link, fields left, live preview right). Saving now repaints Settings with the theme just saved (it used to repaint with the theme stored when Settings opened).

**Readable labels, unchanged stored values**: "Writes only / All activity", "Don't stretch / Stretch / Fit / Fill (crop)". Values live in `Tag`; the round-trip test from PR 2 passes unchanged, and hand-edited unknown values fall back exactly as before.

**Removed**: the Profiles editor's SSH panel, which was never visible (behaviour-neutral, pinned by the round-trip tests).

**Docs** updated (USER_MANUAL, TABS_USER_MANUAL, CommandAssist, ThemeSystem, mcp/security); other page references checked and still valid.

Screenshots (dark and light) attached below. AOT Gate green.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
```

- [ ] **Step 7: Merge only on the user's word.** Then update `settings-redesign-state.md`.

---

# Part 4 — PR 4: The grouped title-bar editor

**Outcome:** the title-bar actions on Tabs & title bar change in three ways:
- **Mini title bar.** A live mini title bar sits on top.
- **Grouped rows.** Rows are one line each, grouped In the bar (n of 8) / In the … menu / Hidden.
- **Placement switch.** Each row has a Bar / Menu / Hide switch:
  - ▲▼ appear only in the bar group.
  - "Bar" is disabled, with a reason, once the bar is full.
  - New Tab stays locked.

## Task 4.0: Branch for PR 4

- [ ] **Step 1: Cut the branch.** PR 3 must have merged first.

```bash
cd /d/projects/nova2/.worktrees/settings-redesign
git fetch origin
git switch -c feat/settings-redesign-titlebar origin/main
```

## Task 4.1: Grouped rows and the Bar / Menu / Hide switch

**Files:**
- Modify: `src/Ntilde.App/Settings/Pages/TabsAndTitleBarPage.axaml(.cs)`, `src/Ntilde.App/Settings/NtStyles.axaml`
- Create: `tests/Ntilde.App.Tests/Core/SettingsTitleBarEditorTests.cs`
- Modify: `SettingsWindowTitleBarShortcutRefreshTests.cs`, `ThemeApplicationRegressionTests.cs`

**Interfaces:**
- In `TitleBarItemsPanel`, a group header is a `Grid` whose `Tag` is its title (`"IN THE BAR"`, `"IN THE … MENU"`, `"HIDDEN"`).
- A row is a hairline `Border` whose `Tag` is the catalog id.
- Inside a row:
  - its `ToggleButton.Segment`s carry a `TitleBarItemState` `Tag`;
  - its arrows are `Button`s tagged `"up"` / `"down"`;
  - its shortcut `TextBlock` is tagged `"shortcut"`.
- `internal const string TabsAndTitleBarPage.BarFullTooltip = "The title bar holds up to 8 actions"`.

- [ ] **Step 1: Write the failing tests.**

```csharp
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Ntilde.Settings;
using Ntilde.Settings.Pages;
using Ntilde.Shell.TitleBar;

namespace Ntilde.Tests.Core;

/// <summary>The grouped title-bar editor and its mini bar (spec §6).</summary>
public sealed class SettingsTitleBarEditorTests
{
    private static (SettingsTestRoot Root, Ntilde.SettingsWindow Window, TabsAndTitleBarPage Page) Open(string settingsJson = "{}")
    {
        var root = SettingsTestRoot.WithSettings(settingsJson);
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.TabsAndTitleBar));
        return (root, window, window.GetPage<TabsAndTitleBarPage>());
    }

    private static StackPanel Rows(TabsAndTitleBarPage page) => page.FindControl<StackPanel>("TitleBarItemsPanel")!;

    internal static Border Row(TabsAndTitleBarPage page, string id)
        => Rows(page).Children.OfType<Border>().Single(row => (string?)row.Tag == id);

    private static ToggleButton Segment(Border row, TitleBarItemState state)
        => row.GetLogicalDescendants().OfType<ToggleButton>().Single(toggle => Equals(toggle.Tag, state));

    private static void Click(Control control) => control.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static string GroupOf(TabsAndTitleBarPage page, string id)
    {
        string? group = null;
        foreach (Control child in Rows(page).Children)
        {
            if (child is Grid header)
            {
                group = (string?)header.Tag;
            }
            else if (child is Border row && (string?)row.Tag == id)
            {
                return group!;
            }
        }

        throw new Xunit.Sdk.XunitException($"No row for {id}.");
    }

    /// <summary>Eight pinned: the four defaults plus four that default to the menu.</summary>
    private const string FullBar = """{"TitleBarItems":{"toggle_recording":"Pinned","command_palette":"Pinned","find":"Pinned","split_vertical":"Pinned"}}""";

    [AvaloniaFact]
    public void ChoosingAPlacement_MovesTheRowToThatGroup()
    {
        var (root, window, page) = Open();
        using (root)
        {
            Click(Segment(Row(page, "settings"), TitleBarItemState.Overflow));

            Assert.Equal("IN THE … MENU", GroupOf(page, "settings"));
            Assert.Equal(TitleBarItemState.Overflow, window.Session.TitleBarDraft.GetState("settings"));
            Assert.True(Segment(Row(page, "settings"), TitleBarItemState.Overflow).IsChecked);
        }
    }

    [AvaloniaFact]
    public void AFullBar_DisablesBarOnUnpinnedRows_AndSaysWhy()
    {
        var (root, _, page) = Open(FullBar);
        using (root)
        {
            ToggleButton bar = Segment(Row(page, "split_horizontal"), TitleBarItemState.Pinned);

            Assert.False(bar.IsEnabled);
            Assert.Equal(TabsAndTitleBarPage.BarFullTooltip, ToolTip.GetTip(bar));
            Assert.True(ToolTip.GetShowOnDisabled(bar));
            Assert.True(Segment(Row(page, "find"), TitleBarItemState.Pinned).IsEnabled); // already pinned
            Assert.Contains(
                Rows(page).Children.OfType<Grid>().Single(g => (string?)g.Tag == "IN THE BAR").Children.OfType<TextBlock>(),
                text => text.Text == "8 of 8");
        }
    }

    [AvaloniaFact]
    public void ArrowsOnlyInTheBarGroup_AndNewTabStaysLocked()
    {
        var (root, _, page) = Open();
        using (root)
        {
            Assert.Contains(Row(page, "settings").GetLogicalDescendants().OfType<Button>(), b => (string?)b.Tag == "up");
            Assert.DoesNotContain(Row(page, "find").GetLogicalDescendants().OfType<Button>(), b => (string?)b.Tag is "up" or "down");

            Border newTab = Row(page, TitleBarCatalog.NewTabId);
            Assert.Empty(newTab.GetLogicalDescendants().OfType<ToggleButton>());
            Assert.Contains(newTab.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "Always in the bar");
        }
    }

    [AvaloniaFact]
    public void MovingUp_ReordersThePinnedActions()
    {
        var (root, window, page) = Open();
        using (root)
        {
            List<string> before = window.Session.TitleBarDraft.BuildSaveOrder();
            int settingsAt = before.IndexOf("settings");

            Click(Row(page, "settings").GetLogicalDescendants().OfType<Button>().Single(b => (string?)b.Tag == "up"));

            List<string> after = window.Session.TitleBarDraft.BuildSaveOrder();
            Assert.Equal(settingsAt - 1, after.IndexOf("settings"));
        }
    }
}
```

Then update the two older tests to the new row shape:
- In `SettingsWindowTitleBarShortcutRefreshTests`, delete `TitleBarRowsPanel` and `FindRowByTitle`, and read the shortcut with:

```csharp
    private static string ShortcutShownFor(Ntilde.SettingsWindow window, string id)
        => SettingsTitleBarEditorTests.Row(window.GetPage<TabsAndTitleBarPage>(), id)
            .GetLogicalDescendants().OfType<TextBlock>().Single(t => (string?)t.Tag == "shortcut").Text!;
```

  The asserts become:
  - `Assert.Equal("Ctrl+Alt+S", ShortcutShownFor(window, "settings"));`
  - `Assert.Equal(TitleBarItemState.Pinned, session.TitleBarDraft.GetState("find"));`, together with `Assert.Equal("IN THE BAR", …)`. Or simply check that `Row(page, "find")` exists.
  - `Assert.Equal(ShortcutMatcher.Format(Key.F9, KeyModifiers.Alt), ShortcutShownFor(window, "settings"));`
- In `ThemeApplicationRegressionTests.SettingsWindow_CodeBuiltRows_UseTheWindowPaletteBrushes`, title-bar rows are hairline rows now. Select them with `.Children.OfType<Border>()` and assert `Assert.Null(row.Background)` and `Assert.Same(hairlineBrush, row.BorderBrush)`, the same as for the shortcut rows.

- [ ] **Step 2: Run them and watch them fail.** Use the targeted run with `SettingsTitleBarEditorTests`. Expected: failures, because the rows are still cards with a combo.

- [ ] **Step 3: Replace the TITLE BAR ACTIONS group in `TabsAndTitleBarPage.axaml`.**

```xml
            <settings:SettingGroup Header="TITLE BAR ACTIONS">
                <StackPanel Spacing="12" Margin="0,8,0,0">
                    <!-- A lightweight replica of the bar, rebuilt on every draft change (spec §6). -->
                    <Border Name="TitleBarPreview" Classes="MiniTitleBar" AutomationProperties.Name="Title bar preview"/>
                    <TextBlock Name="TitleBarValidationMessage" Foreground="{DynamicResource NtRed}" FontSize="12" IsVisible="False" TextWrapping="Wrap"/>
                    <StackPanel Name="TitleBarItemsPanel"/>
                </StackPanel>
            </settings:SettingGroup>
```

- [ ] **Step 4: Replace the title-bar members of `TabsAndTitleBarPage.axaml.cs`.**
  - The members are `RebuildTitleBarRows`, `CreateTitleBarRow`, `MoveDraftPinned`, `ShowTitleBarValidationMessage` and `ClearTitleBarValidationMessage`.
  - Change the `ShortcutsChanged` subscription and the `Load` call to `RebuildTitleBarEditor`.
  - Add the usings `System.Collections.Generic`, `System.Globalization`, `Avalonia`, `Avalonia.Automation`, `Avalonia.Controls.Primitives`, `Avalonia.Layout`, `Avalonia.Media` and `Ntilde.Shell.TitleBar`.

```csharp
    // ── Title bar actions (spec §6) ───────────────────────────────────────────

    /// <summary>Tooltip on a disabled "Bar" button once the bar is full.</summary>
    internal const string BarFullTooltip = "The title bar holds up to 8 actions";

    private static readonly (TitleBarItemState State, string Label)[] Placements =
    [
        (TitleBarItemState.Pinned, "Bar"),
        (TitleBarItemState.Overflow, "Menu"),
        (TitleBarItemState.Hidden, "Hide"),
    ];

    private void RebuildTitleBarEditor()
    {
        if (_session is null)
        {
            return;
        }

        RebuildMiniTitleBar();
        RebuildTitleBarRows();
    }

    private void RebuildTitleBarRows()
    {
        var panel = this.FindControl<StackPanel>("TitleBarItemsPanel")!;
        panel.Children.Clear();

        TitleBarDraftState draft = Session.TitleBarDraft;
        Dictionary<string, TitleBarCatalogEntry> byId = TitleBarCatalog.GetEntries().ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> order = draft.GetDisplayOrder();
        List<TitleBarCatalogEntry> InGroup(TitleBarItemState state)
            => order.Where(id => draft.GetState(id) == state).Select(id => byId[id]).ToList();

        List<TitleBarCatalogEntry> bar = InGroup(TitleBarItemState.Pinned);
        List<TitleBarCatalogEntry> menu = InGroup(TitleBarItemState.Overflow);
        List<TitleBarCatalogEntry> hidden = InGroup(TitleBarItemState.Hidden);
        bool barFull = bar.Count >= TitleBarCatalog.MaxPinned;

        panel.Children.Add(GroupHeader("IN THE BAR", $"{bar.Count} of {TitleBarCatalog.MaxPinned}"));
        for (int i = 0; i < bar.Count; i++)
        {
            panel.Children.Add(CreateTitleBarRow(bar[i], TitleBarItemState.Pinned, i, bar.Count, barFull));
        }

        panel.Children.Add(GroupHeader("IN THE … MENU", menu.Count.ToString(CultureInfo.InvariantCulture)));
        foreach (TitleBarCatalogEntry entry in menu)
        {
            panel.Children.Add(CreateTitleBarRow(entry, TitleBarItemState.Overflow, -1, 0, barFull));
        }

        panel.Children.Add(GroupHeader("HIDDEN", $"{hidden.Count} · still reachable by shortcut and the command palette"));
        foreach (TitleBarCatalogEntry entry in hidden)
        {
            panel.Children.Add(CreateTitleBarRow(entry, TitleBarItemState.Hidden, -1, 0, barFull));
        }
    }

    private static Grid GroupHeader(string title, string detail)
    {
        var name = new TextBlock { Text = title, Classes = { "SectionHeader" }, VerticalAlignment = VerticalAlignment.Center };
        var count = new TextBlock { Text = detail, Classes = { RowDescStyleClass }, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(count, 1);
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 14, 0, 4),
            Tag = title,
            Children = { name, count },
        };
    }

    private Control CreateTitleBarRow(TitleBarCatalogEntry entry, TitleBarItemState state, int barIndex, int barCount, bool barFull)
    {
        var icon = new PathIcon
        {
            Data = Geometry.Parse(entry.IconGeometry),
            Width = entry.IconSize,
            Height = entry.IconSize,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var title = new TextBlock { Text = entry.Title, Classes = { "RowLabel" }, VerticalAlignment = VerticalAlignment.Center };
        var shortcut = new TextBlock
        {
            Text = TitleBarShortcuts.Resolve(entry.ShortcutKey, Session.ShortcutDraft),
            Classes = { RowDescStyleClass },
            VerticalAlignment = VerticalAlignment.Center,
            Tag = "shortcut",
        };

        // Locked: New Tab is the primary action and hosts the flyout with "New SSH Connection…" /
        // "Manage Profiles…" / "Agent Activity…". Letting it be hidden would lose that flyout.
        Control placement = entry.IsLocked
            ? new TextBlock { Text = "Always in the bar", Classes = { RowDescStyleClass }, VerticalAlignment = VerticalAlignment.Center }
            : CreatePlacementSwitch(entry, state, barFull);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("28,*,110,Auto,64"),
            ColumnSpacing = 12,
            Children = { icon, title, shortcut, placement },
        };
        Grid.SetColumn(title, 1);
        Grid.SetColumn(shortcut, 2);
        Grid.SetColumn(placement, 3);

        if (state == TitleBarItemState.Pinned && !entry.IsLocked)
        {
            // Index 0 is the locked New Tab, which never moves and can never be displaced.
            var up = new Button { Content = "▲", Classes = { "IconBtn" }, IsEnabled = barIndex > 1, Tag = "up" };
            var down = new Button { Content = "▼", Classes = { "IconBtn" }, IsEnabled = barIndex < barCount - 1, Tag = "down" };
            AutomationProperties.SetName(up, $"Move {entry.Title} left");
            AutomationProperties.SetName(down, $"Move {entry.Title} right");
            up.Click += (_, _) => MoveDraftPinned(entry.Id, -1);
            down.Click += (_, _) => MoveDraftPinned(entry.Id, +1);
            var arrows = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, Children = { up, down } };
            Grid.SetColumn(arrows, 4);
            grid.Children.Add(arrows);
        }

        Border row = SettingsRowChrome.CreateRow(this, grid);
        row.Tag = entry.Id;
        return row;
    }

    private StackPanel CreatePlacementSwitch(TitleBarCatalogEntry entry, TitleBarItemState current, bool barFull)
    {
        var host = new StackPanel { Orientation = Orientation.Horizontal, Classes = { "Segmented" }, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (state, label) in Placements)
        {
            var button = new ToggleButton { Content = label, Tag = state, IsChecked = state == current, Classes = { "Segment" } };
            AutomationProperties.SetName(button, $"{entry.Title}: {label}");
            if (state == TitleBarItemState.Pinned && current != TitleBarItemState.Pinned && barFull)
            {
                button.IsEnabled = false;
                ToolTip.SetTip(button, BarFullTooltip);
                ToolTip.SetShowOnDisabled(button, true);
            }

            button.Click += (_, _) => ChooseTitleBarPlacement(entry.Id, state);
            host.Children.Add(button);
        }

        return host;
    }

    private void ChooseTitleBarPlacement(string id, TitleBarItemState next)
    {
        // Clicking the current choice un-checks its ToggleButton; the rebuild below puts it back.
        if (Session.TitleBarDraft.GetState(id) != next && !Session.TitleBarDraft.TrySetState(id, next))
        {
            // "Bar" is disabled once the bar is full, so this is the draft's own cap firing.
            ShowTitleBarValidationMessage($"At most {TitleBarCatalog.MaxPinned} actions fit in the title bar. Move one to the menu or hide it first.");
        }
        else
        {
            ClearTitleBarValidationMessage();
        }

        RebuildTitleBarEditor();
    }

    private void MoveDraftPinned(string id, int delta)
    {
        Session.TitleBarDraft.MovePinned(id, delta);
        RebuildTitleBarEditor();
    }

    private void ShowTitleBarValidationMessage(string message)
    {
        var label = this.FindControl<TextBlock>("TitleBarValidationMessage")!;
        label.Text = message;
        label.IsVisible = true;
    }

    private void ClearTitleBarValidationMessage() => this.FindControl<TextBlock>("TitleBarValidationMessage")!.IsVisible = false;

    // RebuildMiniTitleBar is Task 4.2's; until then this placeholder keeps the build green:
    private void RebuildMiniTitleBar()
    {
    }
```

**If `ToolTip.SetShowOnDisabled` does not exist in this Avalonia version:**
1. Wrap the disabled button in a `Border` that carries the tooltip, with `Background="Transparent"`.
2. Assert the tooltip on that `Border` in the test.
3. Note the change in the PR.

- [ ] **Step 5: Add the switch styles to `NtStyles.axaml`.** Append before `</Styles>`:

```xml
    <!-- ── Bar / Menu / Hide segmented switch (PR 4) ── -->
    <Style Selector="StackPanel.Segmented > ToggleButton.Segment">
        <Setter Property="Background" Value="{DynamicResource NtPanelAlt}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource NtHairline}"/>
        <Setter Property="BorderThickness" Value="1,1,0,1"/>
        <Setter Property="CornerRadius" Value="0"/>
        <Setter Property="Padding" Value="10,3"/>
        <Setter Property="MinWidth" Value="52"/>
        <Setter Property="FontSize" Value="12"/>
        <Setter Property="Foreground" Value="{DynamicResource NtFg2}"/>
        <Setter Property="HorizontalContentAlignment" Value="Center"/>
    </Style>
    <Style Selector="StackPanel.Segmented > ToggleButton.Segment:nth-child(1)">
        <Setter Property="CornerRadius" Value="5,0,0,5"/>
    </Style>
    <Style Selector="StackPanel.Segmented > ToggleButton.Segment:nth-last-child(1)">
        <Setter Property="CornerRadius" Value="0,5,5,0"/>
        <Setter Property="BorderThickness" Value="1"/>
    </Style>
    <Style Selector="ToggleButton.Segment:pointerover /template/ ContentPresenter">
        <Setter Property="Background" Value="{DynamicResource NtHairline}"/>
    </Style>
    <Style Selector="ToggleButton.Segment:checked /template/ ContentPresenter">
        <Setter Property="Background" Value="{DynamicResource NtBlueDim}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource NtBlue}"/>
        <Setter Property="Foreground" Value="{DynamicResource NtFg}"/>
    </Style>
    <Style Selector="ToggleButton.Segment:disabled /template/ ContentPresenter">
        <Setter Property="Background" Value="{DynamicResource NtPanelAlt}"/>
        <Setter Property="Foreground" Value="{DynamicResource NtFg4}"/>
    </Style>
```

- [ ] **Step 6: Run the tests.** Use the targeted run with `SettingsTitleBarEditorTests`, then `SettingsWindowTitleBarShortcutRefreshTests`, then `ThemeApplicationRegressionTests`, then `SettingsWindowRoundTripTests`. Expected: PASS.

- [ ] **Step 7: Commit.**

```bash
git add -A src/Ntilde.App/Settings tests/Ntilde.App.Tests/Core
git commit -m "feat(settings): title-bar actions grouped by placement with a Bar/Menu/Hide switch" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

## Task 4.2: The live mini title bar

**Files:**
- Modify: `TabsAndTitleBarPage.axaml.cs` (`RebuildMiniTitleBar`), `NtStyles.axaml`
- Test: `SettingsTitleBarEditorTests.cs`

**Interfaces:**
- `TitleBarPreview.Child` is a `Grid`.
- Its pinned-actions `StackPanel` is tagged `"actions"`. Each child is a `PathIcon` tagged with the catalog id, then a `"…"` `TextBlock` tagged `"overflow"` when the menu is not empty.

- [ ] **Step 1: Write the failing tests.** Append them to `SettingsTitleBarEditorTests`:

```csharp
    private static List<string> MiniBarActions(TabsAndTitleBarPage page)
        => ((Grid)page.FindControl<Border>("TitleBarPreview")!.Child!).Children
            .OfType<StackPanel>().Single(panel => (string?)panel.Tag == "actions")
            .Children.Select(child => (string)child.Tag!).ToList();

    [AvaloniaFact]
    public void TheMiniBar_ShowsThePinnedActionsInOrder_ThenTheMenuButton()
    {
        var (root, window, page) = Open();
        using (root)
        {
            List<string> expected = window.Session.TitleBarDraft.BuildSaveOrder()
                .Where(id => id != TitleBarCatalog.NewTabId)
                .Append("overflow")
                .ToList();

            Assert.Equal(expected, MiniBarActions(page));
        }
    }

    [AvaloniaFact]
    public void TheMiniBar_FollowsEveryDraftChange()
    {
        var (root, _, page) = Open();
        using (root)
        {
            Click(Segment(Row(page, "find"), TitleBarItemState.Pinned));
            Assert.Equal("find", MiniBarActions(page)[^2]); // last pinned, before "…"

            foreach (TitleBarCatalogEntry entry in TitleBarCatalog.GetEntries().Where(e => !e.IsLocked).ToList())
            {
                if (GroupOf(page, entry.Id) == "IN THE … MENU")
                {
                    Click(Segment(Row(page, entry.Id), TitleBarItemState.Hidden));
                }
            }

            Assert.DoesNotContain("overflow", MiniBarActions(page));
        }
    }
```

- [ ] **Step 2: Run them and watch them fail.** Expected: `NullReferenceException` on `TitleBarPreview.Child`.

- [ ] **Step 3: Write `RebuildMiniTitleBar`.** It replaces the placeholder.

```csharp
    /// <summary>
    /// A lightweight replica of the title bar, not a real one: two sample tabs, New Tab's "+", the
    /// pinned actions in order, "…" when the menu has anything in it, and dim caption buttons.
    /// </summary>
    private void RebuildMiniTitleBar()
    {
        var host = this.FindControl<Border>("TitleBarPreview")!;
        TitleBarDraftState draft = Session.TitleBarDraft;
        Dictionary<string, TitleBarCatalogEntry> byId = TitleBarCatalog.GetEntries().ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

        var tabs = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { SampleTab("~/nova2", active: true), SampleTab("pwsh", active: false) },
        };
        var newTab = new TextBlock { Text = "+", Classes = { "MiniBarGlyph" }, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Tag = "actions" };
        foreach (string id in draft.BuildSaveOrder().Where(id => !byId[id].IsLocked))
        {
            actions.Children.Add(new PathIcon { Data = Geometry.Parse(byId[id].IconGeometry), Width = 12, Height = 12, Tag = id, Classes = { "MiniBarIcon" } });
        }

        if (TitleBarCatalog.GetEntries().Any(entry => draft.GetState(entry.Id) == TitleBarItemState.Overflow))
        {
            actions.Children.Add(new TextBlock { Text = "…", Tag = "overflow", Classes = { "MiniBarGlyph" }, VerticalAlignment = VerticalAlignment.Center });
        }

        var captions = new TextBlock { Text = "—   □   ✕", Classes = { "MiniBarCaptions" }, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto"), Children = { tabs, newTab, actions, captions } };
        Grid.SetColumn(newTab, 1);
        Grid.SetColumn(actions, 3);
        Grid.SetColumn(captions, 4);
        host.Child = grid;
    }

    private Border SampleTab(string title, bool active) => new()
    {
        Background = this.FindPaletteBrush(active ? "NtPanel" : "NtPanelAlt"),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(8, 2),
        Child = new TextBlock { Text = title, FontSize = 11, Foreground = this.FindPaletteBrush(active ? "NtFg" : "NtFg3") },
    };
```

Add these mini bar styles to `NtStyles.axaml`:

```xml
    <!-- ── Mini title bar (PR 4) ── -->
    <Style Selector="Border.MiniTitleBar">
        <Setter Property="Background" Value="{DynamicResource NtChromeBg}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource NtHairline}"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="CornerRadius" Value="6"/>
        <Setter Property="Padding" Value="8,6"/>
        <Setter Property="MinHeight" Value="34"/>
    </Style>
    <Style Selector="PathIcon.MiniBarIcon">
        <Setter Property="Foreground" Value="{DynamicResource NtFg2}"/>
    </Style>
    <Style Selector="TextBlock.MiniBarGlyph">
        <Setter Property="Foreground" Value="{DynamicResource NtFg2}"/>
        <Setter Property="FontSize" Value="13"/>
    </Style>
    <Style Selector="TextBlock.MiniBarCaptions">
        <Setter Property="Foreground" Value="{DynamicResource NtFg4}"/>
        <Setter Property="FontSize" Value="11"/>
    </Style>
```

- [ ] **Step 4: Run the tests.** Use the targeted run with `SettingsTitleBarEditorTests`. Expected: PASS.

- [ ] **Step 5: Commit.**

```bash
git add -A src/Ntilde.App/Settings tests/Ntilde.App.Tests/Core
git commit -m "feat(settings): live mini title bar above the title-bar actions" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

## Task 4.3: Gate, screenshots, smoke test and PR 4

- [ ] **Step 1: Run the full gate** (Conventions).
- [ ] **Step 2: Take screenshots** into `$env:TEMP\settings-shots\pr4`. The Tabs & title bar page is captured by the page loop. Check:
  - On dark and light, the mini bar reads as a bar.
  - The switch's checked segment is clear.
  - The disabled "Bar" is visibly dim.
- [ ] **Step 3: Give the user these smoke-test steps.**
  1. Right-click the title bar and choose Customize Title Bar….
  2. Move "Find in Terminal" to Bar. It joins IN THE BAR and appears in the mini bar.
  3. Use ▲ on it; the mini bar order follows.
  4. Pin until the count reads 8 of 8. "Bar" goes dim on the other rows, and hovering it says "The title bar holds up to 8 actions".
  5. Save. The real title bar matches the mini bar.
- [ ] **Step 4: Ask the user, then push and open the PR.**

```bash
git push -u origin feat/settings-redesign-titlebar
gh pr create --base main --title "Settings redesign 4/5: grouped title-bar editor with a live mini bar" --body-file - <<'EOF'
Fourth of five PRs for the settings window redesign (spec §6).

- The title-bar actions on Tabs & title bar are grouped In the bar (n of 8) / In the … menu / Hidden, one line per action.
- Each row has a Bar / Menu / Hide switch; ▲▼ appear only in the bar group; New Tab stays locked ("Always in the bar").
- When the bar is full, "Bar" is disabled on the other rows with the tooltip "The title bar holds up to 8 actions". The 8-pin limit itself is unchanged and still lives in TitleBarDraftState.
- A live mini title bar on top re-renders on every change.

Stored values (TitleBarItems deltas, TitleBarOrder) are unchanged; the round-trip test passes as is. Screenshots attached.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
```

- [ ] **Step 5: Merge only on the user's word.** Then update `settings-redesign-state.md`.

---

# Part 5 — PR 5: Settings search

**Outcome:**
- A search box sits at the top of the sidebar.
- While it has text, the content area lists matching settings, with "Page › Group", the highlighted title and the description.
- ↑/↓ move through the results, Enter or a click jumps to the row and highlights it, and Esc clears.
- Ctrl/Cmd+F focuses the box.

## Task 5.0: Branch for PR 5

- [ ] **Step 1: Cut the branch.** PR 4 must have merged first.

```bash
cd /d/projects/nova2/.worktrees/settings-redesign
git fetch origin
git switch -c feat/settings-redesign-search origin/main
```

## Task 5.1: The search index

**Files:**
- Create: `src/Ntilde.App/Settings/SettingsSearchIndex.cs`
- Create: `tests/Ntilde.App.Tests/Core/SettingsSearchIndexTests.cs`

**Interfaces:**
- `public sealed record SettingsSearchEntry(SettingsPage Page, string PageTitle, string? Group, string? RowId, string Title, string? Description, string? Keywords)`
- `public sealed record SettingsSearchResult(SettingsSearchEntry Entry, int TitleMatchStart, int TitleMatchLength)`. `TitleMatchStart` is -1 when the match is not in the title.
- `public sealed class SettingsSearchIndex`, with `static Build(IEnumerable<(SettingsPageDescriptor Descriptor, Control View)>)`, `Entries` and `Search(string?)`.

- [ ] **Step 1: Write the failing tests.**

```csharp
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Ntilde.Settings;

namespace Ntilde.Tests.Core;

public sealed class SettingsSearchIndexTests
{
    private static SettingsSearchEntry Entry(string title, string? description = null, string? keywords = null, string? group = null, SettingsPage page = SettingsPage.Appearance)
        => new(page, SettingsPageRegistry.Get(page).Title, group, title.ToLowerInvariant().Replace(' ', '-'), title, description, keywords);

    [Fact]
    public void Search_IsCaseInsensitive_AndTrimsTheQuery()
    {
        var index = new SettingsSearchIndex([Entry("Window opacity")]);

        Assert.Single(index.Search("  OPACITY "));
    }

    [Fact]
    public void Search_PutsTitleMatchesFirst_ThenKeepsIndexOrder()
    {
        var index = new SettingsSearchIndex(
        [
            Entry("Blur effect", keywords: "transparency opacity"),
            Entry("Window opacity"),
            Entry("Image opacity"),
        ]);

        string[] titles = index.Search("opacity").Select(r => r.Entry.Title).ToArray();

        Assert.Equal(new[] { "Window opacity", "Image opacity", "Blur effect" }, titles);
    }

    [Fact]
    public void Search_ReportsWhereTheTitleMatched()
    {
        var index = new SettingsSearchIndex([Entry("Window opacity", description: "opacity")]);

        SettingsSearchResult result = Assert.Single(index.Search("opac"));

        Assert.Equal((7, 4), (result.TitleMatchStart, result.TitleMatchLength));
    }

    [Fact]
    public void Search_MatchesDescriptionKeywordsGroupAndPageTitle()
    {
        var index = new SettingsSearchIndex(
        [
            Entry("A", description: "uses the zebra"),
            Entry("B", keywords: "giraffe"),
            Entry("C", group: "OKAPI"),
            Entry("D", page: SettingsPage.Ssh),
        ]);

        Assert.Equal("A", Assert.Single(index.Search("zebra")).Entry.Title);
        Assert.Equal("B", Assert.Single(index.Search("giraffe")).Entry.Title);
        Assert.Equal("C", Assert.Single(index.Search("okapi")).Entry.Title);
        Assert.Contains(index.Search("ssh"), r => r.Entry.Title == "D");
    }

    [Fact]
    public void Search_TreatsPunctuationLiterally_AndIgnoresAnEmptyQuery()
    {
        var index = new SettingsSearchIndex([Entry("Progress reporting (OSC 9;4)")]);

        Assert.Single(index.Search("9;4"));
        Assert.Single(index.Search("(OSC"));
        Assert.Empty(index.Search(""));
        Assert.Empty(index.Search("   "));
        Assert.Empty(index.Search(null));
    }

    /// <summary>Spec: a row added to a page is searchable with no extra registration.</summary>
    [AvaloniaFact]
    public void Build_IndexesEveryTitledRow_WithItsGroup_AndOneEntryPerPage()
    {
        var newRow = new SettingRow { Title = "Brand new row", RowId = "brand-new" };
        var view = new UserControl
        {
            Content = new StackPanel
            {
                Children =
                {
                    new SettingGroup { Header = "WINDOW", Items = { new SettingRow { Title = "Window opacity", RowId = "window-opacity" } } },
                    newRow,
                    new SettingRow { Title = "" },
                },
            },
        };

        var index = SettingsSearchIndex.Build([(SettingsPageRegistry.Get(SettingsPage.Appearance), (Control)view)]);

        string[] titles = index.Entries.Select(e => e.Title).ToArray();
        Assert.Equal(new[] { "Appearance", "Window opacity", "Brand new row" }, titles);
        Assert.Equal("WINDOW", index.Entries[1].Group);
        Assert.Null(index.Entries[0].RowId);
        Assert.Equal("brand-new", index.Entries[2].RowId);
    }
}
```

- [ ] **Step 2: Run them and watch them fail.** Expected: compile errors.

- [ ] **Step 3: Write `SettingsSearchIndex.cs`.**

```csharp
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
```

- [ ] **Step 4: Run the tests.** Expected: PASS.

- [ ] **Step 5: Commit.**

```bash
git add src/Ntilde.App/Settings/SettingsSearchIndex.cs tests/Ntilde.App.Tests/Core/SettingsSearchIndexTests.cs
git commit -m "feat(settings): search index read from the pages' setting rows" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

## Task 5.2: The search box, the results list and the keys

**Files:**
- Modify: `src/Ntilde.App/SettingsWindow.axaml`, `src/Ntilde.App/SettingsWindow.axaml.cs`, `src/Ntilde.App/Settings/NtStyles.axaml`
- Create: `tests/Ntilde.App.Tests/Core/SettingsWindowSearchTests.cs`
- Modify: `tests/Ntilde.App.Tests/Core/SettingsScreenshotHarness.cs`, `docs/USER_MANUAL.md`

**Interfaces:**
- Named controls: `SettingsSearchBox`, `SearchResultsView`, `SearchResultsHeading`, `SearchResultsList` (its items are `SettingsSearchResult`) and `SearchNoResults`.
- `internal SettingsSearchIndex SettingsWindow.SearchIndex`.
- Ctrl/Cmd+F focuses the box. Inside the box: ↑/↓, Enter and Esc.

- [ ] **Step 1: Write the failing tests.**

```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Ntilde.Settings;
using Ntilde.Settings.Pages;

namespace Ntilde.Tests.Core;

/// <summary>Search in the Settings window (spec §5).</summary>
public sealed class SettingsWindowSearchTests
{
    private static RawInputModifiers Primary => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private static TextBox Box(Ntilde.SettingsWindow window) => window.FindControl<TextBox>("SettingsSearchBox")!;

    private static ListBox Results(Ntilde.SettingsWindow window) => window.FindControl<ListBox>("SearchResultsList")!;

    private static List<SettingsSearchResult> Shown(Ntilde.SettingsWindow window)
        => Results(window).ItemsSource!.Cast<SettingsSearchResult>().ToList();

    [AvaloniaFact]
    public void Typing_ShowsResultsInPlaceOfThePage()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();

        Box(window).Text = "opacity";

        Assert.False(window.FindControl<Panel>("PageHost")!.IsVisible);
        Assert.True(window.FindControl<Control>("SearchResultsView")!.IsVisible);
        Assert.Equal($"{Shown(window).Count} results for “opacity”", window.FindControl<TextBlock>("SearchResultsHeading")!.Text);
    }

    [AvaloniaFact]
    public void Opacity_FindsWindowAndImageOpacity_OnAppearance_First()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();

        Box(window).Text = "opacity";

        string[] firstTwo = Shown(window).Take(2).Select(r => r.Entry.Title).ToArray();
        Assert.Equal(new[] { "Window opacity", "Image opacity" }, firstTwo);
        Assert.All(Shown(window).Take(2), r => Assert.Equal(SettingsPage.Appearance, r.Entry.Page));
    }

    [AvaloniaFact]
    public void Enter_JumpsToTheRow_ClearsTheSearch_AndHighlightsTheRow()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Shortcuts));
        try
        {
            window.Show();
            Box(window).Focus();
            window.KeyTextInput("scrollback");
            Dispatcher.UIThread.RunJobs();

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(SettingsPage.Terminal, window.CurrentPage);
            Assert.Equal(string.Empty, Box(window).Text);
            Assert.True(window.FindControl<Panel>("PageHost")!.IsVisible);
            SettingRow row = window.GetPage<TerminalPage>().GetLogicalDescendants().OfType<SettingRow>().Single(r => r.RowId == "scrollback");
            Assert.Same(window.FindResource("NtBlueDim"), row.Background);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Escape_ClearsTheSearch_AndTheSamePageIsBack()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Shortcuts));
        try
        {
            window.Show();
            Box(window).Focus();
            window.KeyTextInput("blur");

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(string.Empty, Box(window).Text);
            Assert.Equal(SettingsPage.Shortcuts, window.CurrentPage);
            Assert.True(window.GetPage(SettingsPage.Shortcuts).View.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void NoMatches_SaysSo()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();

        Box(window).Text = "zzqx";

        var none = window.FindControl<TextBlock>("SearchNoResults")!;
        Assert.True(none.IsVisible);
        Assert.Equal("No settings match “zzqx”.", none.Text);
        Assert.False(Results(window).IsVisible);
    }

    [AvaloniaFact]
    public void ArrowKeys_MoveThroughTheResults_AndStopAtTheEnds()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        try
        {
            window.Show();
            Box(window).Focus();
            window.KeyTextInput("opacity");
            Assert.Equal(0, Results(window).SelectedIndex);

            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Assert.Equal(1, Results(window).SelectedIndex);
            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            Assert.Equal(0, Results(window).SelectedIndex);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ClickingAResult_JumpsToIt()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        try
        {
            window.Show();
            Box(window).Text = "font size";
            Dispatcher.UIThread.RunJobs();
            Control first = Results(window).ContainerFromIndex(0)!;
            Point at = first.TranslatePoint(new Point(10, 10), window)!.Value;

            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(SettingsPage.Terminal, window.CurrentPage);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PrimaryF_FocusesTheSearchBox()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();
        try
        {
            window.Show();

            window.KeyPress(Key.F, Primary, PhysicalKey.F, "f");
            Dispatcher.UIThread.RunJobs();

            Assert.True(Box(window).IsFocused);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void BindingsProfilesAndTheThemeEditor_AreNotIndexed()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();

        List<SettingsSearchEntry> entries = window.SearchIndex.Entries.ToList();

        Assert.Equal(new[] { "Shells & Profiles" }, entries.Where(e => e.Page == SettingsPage.Profiles).Select(e => e.Title));
        Assert.Equal(new[] { "Shortcuts", "Recording a shortcut" }, entries.Where(e => e.Page == SettingsPage.Shortcuts).Select(e => e.Title));
        Assert.DoesNotContain(entries, e => e.Title == "Theme name");
    }
}
```

- [ ] **Step 2: Run them and watch them fail.** Expected: compile errors (`SearchIndex`, `SettingsSearchBox`).

- [ ] **Step 3: Add the search UI to `SettingsWindow.axaml`.**

  Replace the sidebar `Border`'s child (the `ListBox`) with:

```xml
                <DockPanel>
                    <TextBox Name="SettingsSearchBox"
                             DockPanel.Dock="Top"
                             Margin="12,14,12,6"
                             PlaceholderText="Search settings"
                             AutomationProperties.Name="Search settings"/>
                    <!-- Items are built from SettingsPageRegistry in code. -->
                    <ListBox Name="SideNav"
                             Classes="SideNav"
                             SelectionMode="Single"
                             Padding="0,6,0,16"
                             AutomationProperties.Name="Settings pages"/>
                </DockPanel>
```

  Replace `<Panel Name="PageHost" Grid.Column="1"/>` with:

```xml
            <Panel Grid.Column="1">
                <!-- Every page lives here for the window's lifetime; only the current one is visible.
                     Keeping them all in the tree is what lets FindResource and DynamicResource work for
                     pages that are not on screen - Save collects from all of them. -->
                <Panel Name="PageHost"/>

                <!-- Shown in place of the page while the search box has text. -->
                <Grid Name="SearchResultsView" RowDefinitions="Auto,*" Margin="32,28,32,0" IsVisible="False">
                    <TextBlock Name="SearchResultsHeading" Classes="H1" FontSize="16" Margin="0,0,0,12"/>
                    <ListBox Name="SearchResultsList" Grid.Row="1" Classes="SearchResults" AutomationProperties.Name="Search results"/>
                    <TextBlock Name="SearchNoResults" Grid.Row="1" Classes="Subtitle" VerticalAlignment="Top" IsVisible="False"/>
                </Grid>
            </Panel>
```

  Append these styles to `NtStyles.axaml`:

```xml
    <!-- ── Search results (PR 5) ── -->
    <Style Selector="ListBox.SearchResults">
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="BorderThickness" Value="0"/>
    </Style>
    <Style Selector="ListBox.SearchResults ListBoxItem">
        <Setter Property="Padding" Value="10,8"/>
        <Setter Property="Margin" Value="0,1"/>
        <Setter Property="CornerRadius" Value="5"/>
    </Style>
    <Style Selector="ListBox.SearchResults ListBoxItem:pointerover /template/ ContentPresenter">
        <Setter Property="Background" Value="{DynamicResource NtPanelAlt}"/>
    </Style>
    <Style Selector="ListBox.SearchResults ListBoxItem:selected /template/ ContentPresenter">
        <Setter Property="Background" Value="{DynamicResource NtBlueDim}"/>
    </Style>
```

- [ ] **Step 4: Wire it in `SettingsWindow.axaml.cs`.**
  - Add the usings `System.Globalization`, `Avalonia.Controls.Templates`, `Avalonia.Media` (already present) and `Ntilde.Controls`.
  - Add the fields:

```csharp
        private TextBox _searchBox = null!;
        private ListBox _searchResults = null!;
        private Control _searchView = null!;
        private Panel _pageHost = null!;
        private SettingsSearchIndex _searchIndex = new([]);
```

  - Add the property `internal SettingsSearchIndex SearchIndex => _searchIndex;`.
  - In the constructor, call `WireSearch();` right after `BuildSidebar();`.
  - In `ReloadSettingsAfterExternalChangeAsync`, call `RebuildSearchIndex();` after the page `Load` loop.
  - In `OnWindowKeyDown`, add the Ctrl/Cmd+F branch after the `Key.S` branch:

```csharp
            else if (e.Key == Key.F)
            {
                e.Handled = true;
                _searchBox.Focus();
                _searchBox.SelectAll();
            }
```

  Then add the members:

```csharp
        private void WireSearch()
        {
            _searchBox = this.FindControl<TextBox>("SettingsSearchBox")!;
            _searchResults = this.FindControl<ListBox>("SearchResultsList")!;
            _searchView = this.FindControl<Control>("SearchResultsView")!;
            _pageHost = this.FindControl<Panel>("PageHost")!;

            _searchResults.ItemTemplate = new FuncDataTemplate<SettingsSearchResult>(
                (result, _) => result is null ? new TextBlock() : CreateResultView(result));
            _searchBox.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty)
                {
                    UpdateSearch();
                }
            };
            _searchBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
            _searchResults.Tapped += (_, _) => JumpToSelectedResult();

            RebuildSearchIndex();

            // Command Assist rewrites its descriptions when a chord is rebound (its handler runs
            // first, having subscribed in Load), so the index re-reads the rows after it.
            _session.ShortcutsChanged += RebuildSearchIndex;
        }

        private void RebuildSearchIndex()
            => _searchIndex = SettingsSearchIndex.Build(_pages.Select(page => (SettingsPageRegistry.Get(page.Page), page.View)));

        private void UpdateSearch()
        {
            string query = (_searchBox.Text ?? string.Empty).Trim();
            bool searching = query.Length > 0;
            _pageHost.IsVisible = !searching;
            _searchView.IsVisible = searching;
            if (!searching)
            {
                _searchResults.ItemsSource = null;
                return;
            }

            IReadOnlyList<SettingsSearchResult> results = _searchIndex.Search(query);
            _searchResults.ItemsSource = results;
            _searchResults.SelectedIndex = results.Count > 0 ? 0 : -1;
            _searchResults.IsVisible = results.Count > 0;

            var heading = this.FindControl<TextBlock>("SearchResultsHeading")!;
            heading.IsVisible = results.Count > 0;
            heading.Text = results.Count == 1 ? $"1 result for “{query}”" : $"{results.Count} results for “{query}”";

            var none = this.FindControl<TextBlock>("SearchNoResults")!;
            none.IsVisible = results.Count == 0;
            none.Text = $"No settings match “{query}”.";
        }

        private void OnSearchKeyDown(object? sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Down:
                    MoveResultSelection(1);
                    e.Handled = true;
                    break;
                case Key.Up:
                    MoveResultSelection(-1);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    JumpToSelectedResult();
                    e.Handled = true;
                    break;
                case Key.Escape when !string.IsNullOrEmpty(_searchBox.Text):
                    _searchBox.Text = string.Empty; // the page that was showing comes back
                    e.Handled = true;
                    break;
            }
        }

        private void MoveResultSelection(int step)
        {
            if (!_searchResults.IsVisible || _searchResults.ItemCount == 0)
            {
                return;
            }

            _searchResults.SelectedIndex = Math.Clamp(_searchResults.SelectedIndex + step, 0, _searchResults.ItemCount - 1);
            _searchResults.ScrollIntoView(_searchResults.SelectedIndex);
        }

        private void JumpToSelectedResult()
        {
            if (_searchResults.SelectedItem is not SettingsSearchResult result)
            {
                return;
            }

            _searchBox.Text = string.Empty; // hides the results, so the page is on screen for the jump
            NavigateTo(new SettingsTarget(result.Entry.Page, result.Entry.RowId));
        }

        private Control CreateResultView(SettingsSearchResult result)
        {
            SettingsSearchEntry entry = result.Entry;
            string where = entry.Group is null
                ? entry.PageTitle
                : $"{entry.PageTitle} › {CultureInfo.InvariantCulture.TextInfo.ToTitleCase(entry.Group.ToLowerInvariant())}";

            var panel = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = where, Classes = { "RowDesc" } },
                    new MatchHighlightTextBlock
                    {
                        SourceText = entry.Title,
                        HighlightStart = result.TitleMatchStart,
                        HighlightLength = result.TitleMatchLength,
                        HighlightBrush = this.FindPaletteBrush("NtBlue"),
                        Foreground = this.FindPaletteBrush("NtFg"),
                        FontSize = 13,
                    },
                },
            };

            if (!string.IsNullOrWhiteSpace(entry.Description))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = entry.Description,
                    Classes = { "RowDesc" },
                    MaxLines = 2,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }

            return panel;
        }
```

- [ ] **Step 5: Capture the results too, and document the box.**
  - In `SettingsScreenshotHarness.CaptureSettings`, after the theme-editor capture, add:

```csharp
            window.FindControl<TextBox>("SettingsSearchBox")!.Text = "opacity";
            written.Add(Save(window, output, $"settings-{slug}-Search.png"));
            window.FindControl<TextBox>("SettingsSearchBox")!.Text = "";
```

  - In `docs/USER_MANUAL.md` §1.2, after the Pages sentence, add:

```
  Type in the search box at the top of the sidebar (or press `Ctrl+F`) to find
  any setting by name; Enter jumps to it.
```

- [ ] **Step 6: Run the tests.** Use the targeted run with `SettingsWindowSearchTests`, then `SettingsSearchIndexTests`, then `Settings`.

Expected: PASS, with the round trip and keyboard tests unchanged.

**If `ClickingAResult_JumpsToIt` does not jump:** the tap gesture did not fire from a headless press and release. Replace the `Tapped` subscription with a `PointerReleased` handler on `_searchResults` that jumps when `e.InitialPressMouseButton == MouseButton.Left`. Keep the test as it is.

- [ ] **Step 7: Commit.**

```bash
git add -A src/Ntilde.App tests/Ntilde.App.Tests/Core docs/USER_MANUAL.md
git commit -m "feat(settings): search across all pages that jumps to the row" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

## Task 5.3: Gate, screenshots, smoke test and PR 5

- [ ] **Step 1: Run the full gate** (Conventions). CI's AOT Gate must also be green.
- [ ] **Step 2: Take screenshots** into `$env:TEMP\settings-shots\pr5`, and check the search capture on dark and light.
- [ ] **Step 3: Give the user these smoke-test steps.**
  1. In Settings, press Ctrl+F (Cmd+F on macOS) and type `opacity`. Window opacity and Image opacity lead the list, under "Appearance › Window" and "Appearance › Background Image".
  2. Press ↓, then Enter. Appearance opens, scrolled to Image opacity, with a brief highlight.
  3. Type `zzqx`. It says "No settings match “zzqx”."
  4. Press Esc. The page you were on comes back.
  5. Type `9;4` and click the result. General opens, at Progress reporting.
- [ ] **Step 4: Ask the user, then push and open the PR.**

```bash
git push -u origin feat/settings-redesign-search
gh pr create --base main --title "Settings redesign 5/5: settings search" --body-file - <<'EOF'
Last of five PRs for the settings window redesign (spec §5).

- A search box at the top of the sidebar (Ctrl/Cmd+F). While it has text the content area lists matching settings: "Page › Group", the title with the match highlighted, and the description.
- ↑/↓ move, Enter or a click jumps to the row (scrolls and highlights it), Esc clears and returns to the page you were on.
- The index is read from the pages' SettingRows (title, description, keywords, group, page title), so a new row is searchable without registration. Shortcut bindings, profiles, snippets, title-bar actions and the theme editor are not indexed in v1, as specified.

Screenshots attached. AOT Gate green.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
```

- [ ] **Step 5: Merge only on the user's word.**

---

# After the last PR

- [ ] **Update memory.**
  - `settings-redesign-state.md`: all five PRs merged, with their SHAs.
  - `theme-system-v2-state.md`: its Settings migration now targets `Settings/NtPalette.axaml` and `NtStyles.axaml`, and its Advanced colors group goes in the theme editor sub-page's left column.
- [ ] **Tell the user what the other branches now need.** Say each in one line; do not act on them.
  - **dev lane:** at the dev→main merge, the screen-inference and TypeSafe API key rows on Agent Access become `SettingRow`s on `AgentAccessPage`, and their code-behind moves there.
  - **Shots harness branch:** its scenarios open Settings with `new SettingsWindow(new SettingsTarget(SettingsPage.AgentAccess))` / `SettingsPage.Terminal`, not tab indices.
  - **Follow-up bug** (Correction 1): Settings Save drops the local-profile fields the editor does not own. The fix is to merge into the original profile objects.
