# Theme System v2 — Design

**Date:** 2026-09-23
**Status:** Design approved in brainstorming; spec awaiting review
**Lane:** each phase is a PR into `main` (changed 2026-10-23; the planned `dev-review` lane was never created)

## Problem

Themes were designed (Feb 2026) when the app had a terminal, a tab strip and a Settings
window. Since then Ntilde has grown agent output, Command Assist, remote files, transfers,
SSH dialogs, replay, a vertical tab sidebar with agent status, toasts and a command palette.
A full audit (2026-09-23) found that almost none of that UI follows the theme.

### Three coexisting color mechanisms

1. **`Nt*` token palette** — `Shell/ThemePaletteResources.Apply` derives ~21 brushes
   (`NtWindowBg`, `NtPanel`, `NtFg…`, `NtBlue…`) from the `TerminalTheme`. Good design, but it
   is *window-scoped*: only `SettingsWindow` and `ConnectionManager` declare the keys and call
   `Apply`, each with its own copy of the defaults (the two lists already differ).
2. **Imperative code in `MainWindow`** — `ApplyThemeToUI`, `ApplyThemeToDialogWindow` and
   `TerminalTheme.GetContrastForeground()` paint tabs, title bar, splitters and dialogs pure
   black/white. Dialogs are themed once at open and never follow a live switch.
3. **Hardcoded dark hex** — ~190 literals across 20 files: Command Assist, Agent Output
   chrome, Remote Files, Transfer Center/Dialog, pane search, paste toast, status bar, MD
   toggle, toasts, command palette, Replay.

Root cause: tokens are opt-in per window, so UI inside `MainWindow` had nothing to bind to.
`App.axaml` also forces `RequestedThemeVariant="Dark"`, and each window overrides it (or not)
on its own.

### Consequences on the shipped light themes (GitHubLight, OneHalfLight, SolarizedLight)

- Command palette: search text turns black (contrast foreground) on a fixed `#252526` panel —
  unreadable. Transfer overlay title: black on `#2D2D2D`.
- Dark islands with light Fluent buttons inside (toasts, Agent Output, Transfer), because
  `MainWindow` flips its variant but those panels are hardcoded dark.
- Connection Manager never sets a variant: dropdowns/tooltips stay dark; hover states are
  frozen dark hex.
- Restore/Import dialogs, TransferDialog and Replay are unthemed. Replay renders with the
  Default theme, not the active one.
- Vertical sidebar, measured contrast against the theme background:

  | Element | Literal | Dark themes | Light themes |
  |---|---|---|---|
  | Preview line | `#99FFFFFF` | 5.6–7.2:1 | **1.00–1.04:1** (invisible) |
  | Selected row fill | `#33FFFFFF` | ~1.9:1 | **1.00–1.02:1** (only the 3px bar shows selection) |
  | Attention dot | `#FFD25A` | 8.7–13:1 | **1.33–1.44:1** |
  | Agent-wrote chip | `#F0C07A` | 7.5–11:1 | **1.55–1.68:1** |
  | Attention dot on an unselected row (×0.6 opacity) | | ~4.5:1 | **~1.2:1** |

### Other defects found

- Unselected rows get `Opacity=0.6` (`App.axaml:187`) on the whole row, dot and chips included —
  Attention is sticky until the tab is selected, so its dot is dimmed on exactly the rows it flags.
- Working dot (raw ANSI blue) and AgentWatched dot (fixed `#4FB0D4`) are within 25° of hue on
  12 of 13 shipped themes (2° on OneHalfLight). ANSI blue vs cyan is within 20° on 7 themes, so
  a naive token mapping inherits the collision. Attention `#FFD25A` vs AgentWrote `#E8A33D` are
  ~7° apart; only the chips disambiguate.
- The sidebar has no surface: window background/opacity/image show through `PART_TabSidebar`;
  no divider, invisible resize grip; the overflow pill is an opaque raw-background block with an
  invisible `#28FFFFFF` hairline.
- Selected-tab underline/bar and Working dot use raw `theme.Blue` (`RGB(0,0,238)` on Default),
  not the softened accent (`MainWindow.axaml.cs:6354`).
- Agent Output markdown looks up `Nt*` keys that don't exist in its tree and always falls back to
  fixed dark colors (`AgentOutput/MarkdownTheme.cs:47`).
- Renderer selection `(51,153,255,100)`, search match `(255,255,0,100)`, active match
  `(255,128,0,150)` are hardcoded (`Shell/TerminalDrawOperation.cs:463-465`); bell flash is white
  (`TerminalView.cs:2353`), invisible on light.
- The brightness formula exists in 5 places with inconsistent thresholds; chrome background is
  derived two different ways; `UpdateTabVisuals` allocates new brushes on every pass.
- The theme model has 20 fields. Importers drop what sources provide: WT `selectionBackground`;
  iTerm2 Selection / Selected Text / Cursor Text; Alacritty `selection.*`, `search.*`,
  `cursor.text` (and `0x…` values fail to parse).
- Unrelated but found: native `.json` theme import silently fails (WT importer claims `.json`
  and throws on a single object). **Tracked as a separate task, not part of this spec.**

## Goals

1. Every surface follows the active theme, live, including dialogs and pane overlays.
2. Light themes are readable everywhere, enforced by a contrast test.
3. New UI cannot bypass the token layer, enforced by a gating guard.
4. Themes can optionally specify selection/search/cursor-text, UI accent and status colors;
   importers keep what they currently drop.
5. Existing theme files load unchanged and re-save byte-identically.

## Non-goals

- A UI theme chosen separately from the terminal color scheme.
- Themes overriding UI surface tones (window/panel/border/text) — surfaces stay derived so a
  theme cannot make the chrome unreadable.
- Bold, link, badge or cursor-guide colors.
- Theme hot-reload from disk.
- Per-connection accent tinting of tabs.
- Vertical-tab layout changes beyond the surface, row states and dot shapes described here.
- The native `.json` import bug (separate task).

## Decisions (from brainstorming)

| # | Question | Decision |
|---|---|---|
| D1 | Scope | Consistency + richer themes |
| D2 | New optional theme fields | Selection & search, UI accent, status colors (not UI surfaces) |
| D3 | Enforcement | Literal guard (gating) + contrast test (gating) |
| D4 | Architecture | App-wide token set in `Application.Resources` (not per-window) |
| D5 | Sidebar dot distinguishability | Color **and** shape |
| D6 | Sidebar surface | Chrome panel with divider and grip hover |
| D7 | Profile-themed pane chrome | Follows the pane's own theme (local token scope) |
| D8 | Landing | 4 PRs into `main` (was `dev-review`; changed 2026-10-23) |

## 1. Theme schema

`Ntilde.VT.TerminalTheme` gains 10 optional properties, all `TermColor?`. Absent/null means
"derive":

| Field | Kind | Derived default |
|---|---|---|
| `SelectionBackground` | terminal | `Accent` at α 0x66 |
| `SelectionForeground` | terminal | null → selected cells keep their own colors |
| `CursorText` | terminal | `Background` |
| `SearchMatch` | terminal | `Warning` at α 0x64 |
| `SearchMatchActive` | terminal | midpoint hue of `Warning`/`Error` at α 0x96 (today's orange) |
| `Accent` | UI | `SoftenAccent(Blue)` (existing algorithm) |
| `Success` | UI | softened `Green`, contrast-fixed |
| `Warning` | UI | softened `Yellow`, contrast-fixed |
| `Error` | UI | softened `Red`, contrast-fixed |
| `Info` | UI | softened `Cyan`, contrast-fixed |

- JSON: `WhenWritingNull`, so existing files re-save byte-identically.
- In the optional fields, a malformed value is read as null (derived) with a logged warning,
  not `Transparent` (today's converter behavior, kept for the 20 required fields).
- `Clone()` copies the new fields.
- There is no `Attention` field. The `Attention` token derives from `Warning`, and adding a
  field later needs no renames.

## 2. Tokens and derivation

`ThemeTokens.Derive(TerminalTheme theme, double windowOpacity)` is a pure function (no
`Application`, usable from plain `[Fact]` tests) in `Shell/Theming/`. It returns an immutable
`ThemeTokens` holding every color below. It replaces `ThemePaletteResources`' math and all five
luminance copies.

**Color math, defined once (`ThemeColorMath`):**

- `IsDark(bg)`: perceived luma `0.299R+0.587G+0.114B < 0.5` (today's rule, one threshold).
- `Contrast(a, b)`: WCAG relative-luminance ratio.
- `EnsureContrast(c, surface, target)`: moves HSL lightness only, away from the surface
  (lighter on dark themes, darker on light), until the target is met; hue and saturation are
  preserved.
- Applied only to **derived** colors. Explicit theme values are used verbatim.

### Resource tokens (keys in `ThemeKeys`, all prefixed `Nt`)

| Group | Keys | Derivation |
|---|---|---|
| Surfaces | `NtWindowBg`, `NtChromeBg`, `NtSidebarBg`, `NtPanel`, `NtPanelAlt`, `NtOverlay`, `NtScrim` | `Background` shifted ±10/±18/±6 (existing rule, one direction per `IsDark`); `Overlay` = elevated opaque panel for palette/toasts/popups; `SidebarBg` = `ChromeBg` at the window-opacity alpha; `Scrim` = black α 0x80 (dark) / 0x40 (light) |
| Lines | `NtHairline`, `NtHairlineStrong`, `NtSidebarDivider`, `NtGripHover` | shifted ±28/±40; divider = `Hairline`; grip hover = `AccentBorder` |
| Text | `NtFg`, `NtFg2`, `NtFg3`, `NtFg4` | `Foreground` at α FF/C8/9A/6E |
| Interaction | `NtHoverFill`, `NtPressedFill`, `NtSelectedFill` | `Foreground` at low alpha (replaces the `#1A/#30/#33FFFFFF` white tints) |
| Accent | `NtAccent`, `NtAccentHover`, `NtAccentPressed`, `NtAccentFg`, `NtAccentDim`, `NtAccentFaint`, `NtAccentBorder` | `Accent` field or `SoftenAccent(Blue)`; hover/pressed ±lightness; `AccentFg` = whichever of white/`#0a1622` has higher contrast |
| Status | `NtSuccess`, `NtWarning`, `NtError`, `NtInfo`, `NtAttention`, each with `…Dim` (tinted background) and `…Border` | fields or softened ANSI; `Attention` = `Warning` |
| Semantic aliases | `NtAgentWrote`, `NtAgentWatched`, `NtAgentIdle`, `NtDiffAdded`, `NtDiffRemoved`, `NtDiffHunk`, `NtRecording`, `NtLink`, `NtLinkHover` | `AgentWrote` = its own derived orange (midpoint hue of Warning and Error, contrast-fixed) — **not** merged into Warning; `AgentWatched` = Info; `AgentIdle` = Fg4; diff = Success/Error/Warning; `Recording` = Error; links = Accent/AccentHover |
| Tab strip | `NtTabFg`, `NtTabFgSelected`, `NtTabHoverFill`, `NtTabSelectedFill`, `NtTabIndicator`, `NtTabPreviewFg`, `NtTabWorking`, `NtTabAttention`, `NtTabAgentWrote`, `NtTabAgentWatched`, `NtTabChipActivity`, `NtTabChipAttention`, `NtTabChipAgentWrote`, `NtTabChipAgentWatched` | row text Fg2/Fg; fills from Foreground; indicator = Accent; preview = Fg3 contrast-fixed to 4.5; dots = Accent/Attention/AgentWrote/AgentWatched contrast-fixed to 3:1 against both `SidebarBg` and `ChromeBg`; chips = same hues contrast-fixed to 4.5:1 |

`NtBlue*` keys are renamed to `NtAccent*`. `NtMagenta` is dropped (unused). `NtMono` (a font)
is untouched.

### Render colors (not resources)

`TerminalRenderColors` is a plain struct of `SKColor` values derived from the pane's
*effective* theme: `Selection`, `SelectionFg?`, `CursorText`, `SearchMatch`,
`SearchMatchActive`, `BellFlash` (`Foreground` α 0x26), next to the existing
background/foreground/cursor.

### Fixed on purpose (the guard's permanent allowlist)

- Caption close-button red (`#E81123` / `#F1707A`) — platform convention.
- User-chosen colors: connection-group tag swatches and per-connection `AccentColor`.
- Debug-only drawing: perf HUD and debug grid.

## 3. Runtime

### `ThemeService` (`Shell/Theming/ThemeService.cs`, static, modeled on `UiScale`)

- `Current` (the active `ThemeTokens`) and `event Changed`.
- `Install(Application app, TerminalTheme theme, double windowOpacity)`: called from app
  initialization before any window is created. It merges one `ResourceDictionary` holding a
  mutable `SolidColorBrush` per key into `Application.Resources`. It replaces the XAML default
  blocks, so there are no hand-maintained defaults.
- `Apply(TerminalTheme theme, double windowOpacity)`:
  1. `Dispatcher.UIThread.VerifyAccess()` plus an owner-application guard (the same pattern as
     `UiScale`; see memory *headless-static-event-plain-fact-poison*);
  2. derive tokens;
  3. update each brush's `Color` **in place**, so `DynamicResource` bindings and held brush
     references stay live;
  4. set `Application.RequestedThemeVariant` from `IsDark`;
  5. write the Fluent `ColorPaletteResources` for that variant (Accent, RegionColor, Base\*,
     Alt\*, Chrome\*, List\*, ErrorText). Stock controls, menus, tooltips and scrollbars then
     use theme tones instead of stock grey.
  6. raise `Changed`.
- `ApplyTo(IResourceDictionary scope, ThemeTokens tokens)`: the same writer for a local scope,
  used by the theme editor preview and profile-themed panes.
- **#422 compatibility:** that fix made *static, process-wide* brushes immutable. These brushes
  are per-`Application` and mutated only on the UI thread. The static `Tab*Brush` fields in
  `MainWindow` are deleted.

### Consumers

- XAML uses `{DynamicResource Nt…}` only.
- Code-built UI (tab headers, dots, chips, `CreateThemedDialogWindow` dialogs) binds through
  `GetResourceObservable(ThemeKeys.X)`. It never reads a brush once and stores it, which is
  what makes pane scopes work.
- **Deleted:**
  - per-window `ThemePaletteResources.Apply` calls (`SettingsWindow`, `ConnectionManager`);
  - both duplicated `Nt*` declaration blocks;
  - `ApplyThemeToDialogWindow`;
  - every UI use of `GetContrastForeground`; the method itself is deleted from
    `TerminalTheme` unless a non-UI caller remains;
  - the inline luminance copies (`MainWindow.axaml.cs:6357`, `SettingsWindow.axaml.cs:3149`,
    `TerminalPane.axaml.cs:4076`, `TerminalDrawOperation.cs:2855`);
  - the dead `DragBorder`/`headerBg` code;
  - the unused `TabHeaderTemplate`;
  - the `Button#SettingsBtn` selectors (they match nothing);
  - the dead MainWindow search overlay.
- `ApplyThemeToUI` keeps only the window-specific work (background image, window opacity) and
  calls `ThemeService.Apply`. Its 7 call sites (startup, palette "Theme:", settings
  preview/opacity/background image/save/cancel) are unchanged in meaning.
- Settings stops re-theming itself from its own combo box. It follows `ThemeService` like every
  other window, so preview and cancel apply app-wide exactly as today.

### Profile-themed panes (D7)

- When a pane's effective theme (`Profile?.ThemeName ?? settings.ThemeName`) differs from the
  app theme, `TerminalPane` creates a local dictionary via `ApplyTo` and a variant scope
  (`ThemeVariantScope` or equivalent) around its content.
- Everything inside the pane resolves the pane's tokens: status bar, pane search, paste toast,
  MD toggle, Agent Output, Command Assist bubble/popup.
- When the profile theme is cleared, or equals the app theme, the local scope is removed.
- An app theme switch never clobbers a pane scope.
- Tab strip, title bar, sidebar and dialogs follow the app theme.
- Fluent palette inside a pane scope is **best effort**. The plan runs a spike test on whether
  a local `ColorPaletteResources` works in Avalonia 12.0.4. If it doesn't, stock controls in an
  overridden pane follow only the variant.

### Renderer

- Each pane builds `TerminalRenderColors` from its effective theme and passes them with the
  theme through the existing `ApplySettings` path. `TerminalDrawOperation` reads plain values
  and never touches resources, because it runs on the render thread.
- Behavior changes:
  - a block cursor draws the glyph under it in `CursorText` (today it is an opaque rectangle);
  - when a theme sets `SelectionForeground`, selected text is drawn in it.
- The existing theme-epoch invalidation (d8db378) covers render-color changes as well.
- Remove the unused `SelectionBrush` field (`TerminalView.cs:1237`).

### Other processes and windows

- The askpass helper calls `ThemeService.Install/Apply` with `TerminalSettings.Load().ActiveTheme`,
  next to its existing `UiScale` load. It follows the app theme instead of the OS light/dark
  setting.
- Replay renders with the active theme.

## 4. Tab strip (both orientations) and the vertical sidebar

- **Row states:**
  - Title text uses `NtTabFg` (unselected) and `NtTabFgSelected`.
  - Remove the whole-row `Opacity=0.6` (`App.axaml:187`) and its pointer-over/selected
    `Opacity=1.0` counterparts. Dots and chips always render at full strength.
  - Hover and selected fills use `NtTabHoverFill` / `NtTabSelectedFill`, visible on light
    themes.
- **Selection marker:** the horizontal underline and the vertical 3px left bar use
  `NtTabIndicator` (softened accent), not raw ANSI blue. The drag insert indicator uses it too.
- **Sidebar surface (D6):** `PART_TabSidebar` gets `NtSidebarBg`, a 1px `NtSidebarDivider`
  toward the terminal, and a resize grip that shows `NtGripHover` on pointer-over.
- **Overflow pill:** `NtSidebarBg` background, `NtHairline` top line, `NtTabFg` text. Replaces the
  raw opaque theme background and the invisible `#28FFFFFF`.
- **Preview line:** `NtTabPreviewFg`.
- **Status dot shapes (D5).** `TabStatusPresentation` stays pure and returns shape alongside
  state:

  | State | Shape | Token |
  |---|---|---|
  | Working | filled circle ● | `NtTabWorking` |
  | Attention | filled circle ● | `NtTabAttention` |
  | AgentWrote | filled diamond ◆ | `NtTabAgentWrote` |
  | AgentWatched | hollow ring ○ | `NtTabAgentWatched` |

  Every hue-adjacent pair differs in shape: Working vs Watched (filled vs hollow), Attention vs
  AgentWrote (circle vs diamond). The dot element becomes a small `Path`/`Shape` host that
  swaps geometry; its size (8px) is unchanged.
- **Chips:** `NtTabChip*` tokens. The bell emoji chip stays an emoji.
- **Horizontal-only items:**
  - title-bar overflow badge "+N" and the tab-list button's hidden-tabs tint use
    `NtTabChipAttention`;
  - `BtnNewTab` hover uses `NtHoverFill` (the `#30/#40FFFFFF` rules go).
- **Performance:** `UpdateTabVisuals` stops allocating brushes per pass. It sets resource
  references once at header creation and only swaps state (which key, which shape).

## 5. Surface migration map

Target tokens for every audited surface (phase in brackets):

| Surface | Today | Target |
|---|---|---|
| Settings window [1] | local `Nt*` + own `Apply` + variant | app tokens; danger button `NtErrorDim`/`NtErrorBorder`/`NtError`; toggle knob `NtAccentFg`; port-forward dot `NtSuccess`/`NtFg4`; shortcut error `NtError`; Restore/Import dialogs themed |
| Connection Manager [1] | local `Nt*`, frozen dark hovers, no variant | app tokens; hovers `NtHoverFill`/`NtPressedFill`; root `#16181d`/`#e6e8ec` removed |
| Agent Output markdown [1, side effect] | fallback dark colors | resolves real tokens (no code change needed beyond key renames) |
| Title bar, caption buttons, window frame [2] | contrast fg, Fluent | tokens + Fluent palette; close red stays |
| Tab strip + sidebar [2] | see §4 | see §4 |
| Splitters [2] | contrast-color alpha | `NtHairline` / `NtHairlineStrong` / `NtAccentBorder` (hover/drag) |
| Recording / update toasts [2] | `#20242C`, `#3A404A`, `#C9CFD9` | `NtOverlay`, `NtHairline`, `NtFg`/`NtFg2` |
| Command palette [2] | `#80000000`, `#252526`, `#007ACC`, `#E0E0E0` | `NtScrim`, `NtOverlay`, `NtAccentBorder`, `NtFg`/`NtFg3`; search box inherits |
| Transfer overlay + TransferCenter [2] | `#1E1E1E`, `#2D2D2D`, greys, `StateBrushConverter` SkyBlue/LimeGreen/Red/Gray | `NtOverlay`, `NtChromeBg`, `NtFg*`; the converter resolves token brushes: Info/Success/Error/Fg4 |
| Record button, agent observe indicator [2] | `#F1636B`, `#4FB0D4`/`#6B737F` | `NtRecording` (glyph) / `NtErrorDim` (recording background), `NtAgentWatched`/`NtAgentIdle` |
| Code-built dialogs, SSH prompts, About [2] | themed once at open | inherit app tokens, live; About links `NtLink`/`NtLinkHover` |
| Pane search, paste toast, MD toggle [3] | `#383838`, `#505050`, `#1a1a1a`, `#0078D7`, `#2A2D33`… | `NtOverlay`, `NtHairline`, `NtPanelAlt`, `NtAccent`, `NtAccentDim` |
| Pane status bar + agent segment [3] | `#CC1A1A1A`, `#AAA`, LimeGreen/Yellow/Red/Gray, `#E8A33D`… | `NtChromeBg`, `NtFg3`, `NtSuccess`/`NtWarning`/`NtError`/`NtFg4`, `NtAgentWrote`/`NtAgentWatched`/`NtAgentIdle` |
| Inactive-pane dim, bell flash [3] | black α 72/96, white flash | `NtScrim`-derived dim, `BellFlash` render color |
| Agent Output chrome [3] | `#1B1D21`, `#31353A`, `#F3F6FA`… | `NtPanel`, `NtHairline`, `NtFg*`; markdown diff via `NtDiff*` |
| Command Assist bubble/popup [3] | `#CC202020`, `#8EC5FF`, `#332F6FEB`… | `NtOverlay`, `NtAccent`, `NtSelectedFill`/`NtAccentDim`, `NtFg*` |
| Remote Files sidebar, RemotePathTextBox [3] | `#1B1D21`, `#25384B`, `#F16C6C`… | `NtPanel`, `NtSelectedFill`, `NtAccentBorder`, `NtError` |
| TransferDialog [3] | unthemed `#252526`, `#007ACC`, `#F14C4C` | tokens; `NtAccent`, `NtError` |
| New/Edit SSH connection [3] | warning `#D7BA7D`, error `#E06C75`, App TabItem hover | `NtWarning`, `NtError`, tab tokens |
| Replay window [3] | unthemed, Default theme buffer | tokens; active theme |
| Askpass helper [3] | OS variant | `ThemeService` |
| Renderer selection/search/cursor text [4] | hardcoded | `TerminalRenderColors` |

### After the Settings redesign (added 2026-10-19, carried over from #528)

The Settings redesign (#522-#527) landed on `main` after this spec. Line references are to `main` at 616fba5.

- **Where the Settings row now points.** The migration targets `Settings/NtPalette.axaml` and `Settings/NtStyles.axaml`, not `SettingsWindow.axaml`. Both are `x:Class` dictionaries that use `DynamicResource` only, guarded by `SettingsXamlResourceTests`. The §6 Advanced colors group goes in the left column of Appearance's theme editor sub-page.
- **Connection Manager** still has its own denser styles and is not on `NtStyles`/`NtPalette`. The redesign deferred it here (its plan's correction 2), so the row above stands.
- **Literals the redesign moved over verbatim**, for the phase-1 sweep:
  - the danger button `#3a1a1d` / `#5c2329` / `#4a2024` (`NtStyles.axaml:112-121`);
  - the theme preview `Background="#111"` (`AppearancePage.axaml:25` and the editor preview at `:162`);
  - the editor preview label's `Brushes.Black` / `Brushes.White` (`AppearancePage.axaml.cs:484`);
  - the shortcut conflict text `Brushes.IndianRed` (`ShortcutsPage.axaml.cs:184`);
  - the theme editor's 16 `EditSwatch` buttons' initial `Background="Black"` (`AppearancePage.axaml:137-152`);
  - the toggle knob's `Fill="White"` (`NtStyles.axaml:225`).
- **Contrast defects seen in review.** They are pre-existing on `main`, so the contrast test and the light/dark acceptance pass should cover them:
  - Light themes: the sidebar's group headers and unselected glyphs are very faint, and the Backup glyph is nearly invisible.
  - Light: the Delete (danger) button renders dark maroon, which is the literals above.
  - Dark: the Agent Access glyph is olive-tinted.
  - Dark Default: "More" links (`Button.Link`) use the same deep blue as toggles and Save, which is low contrast on near-black.
  - The title-bar preview's "…" glyph (`TextBlock.MiniBarGlyph`, `NtStyles.axaml:421`) is tiny and faint.

### Phase 1 as landed (2026-10-23)

Where phase 1 departs from the plan, so phases 2-4 start from what is there:

- **Toggle knob** is `NtFg` off and `NtAccentFg` on. `NtAccentFg` alone is the dark ink on light
  accents, which is dark on the dark off-track.
- **Agent Output chrome** came forward from phase 3: with only the markdown on tokens, a light theme
  put dark text on the fixed `#1B1D21` panel. Its five literals are now `NtPanel`, `NtHairline`,
  `NtFg`/`NtFg3`/`NtFg4`.
- **`NtPalette.axaml` is deleted**, and `NtMono` lives in `App.axaml`. The unknown-token guard counts
  every key `App.axaml` declares as app-wide.
- **Theme editor preview**: the `#111` placeholders and `Black` swatch placeholders are gone (code
  paints them before they show), and the preview label uses the edited theme's own foreground.
- **Permanent allowlist** also covers the connection manager's default group/tag `Swatch`
  (`Brushes.Gray`), the same identity-colour family as `GroupSwatches`.
- **Baseline**: 217 literals in 18 files, all on surfaces phases 2-4 migrate.

## 6. Schema propagation

- **MCP (`ThemeTools`):**
  - Keep the 19 required color fields and add `OptionalColorFields`.
  - The schema text documents "derived when omitted".
  - The validator checks that values parse, and warns (never fails) when an explicit
    Accent/status color misses its contrast target against the theme's derived `Panel`.
- **Drift test:** reflection over `TerminalTheme` asserts that the set of
  `TermColor`/`TermColor?` properties equals required ∪ optional, the theme counterpart of the
  settings drift guard.
- **Importers:**
  - Windows Terminal: `selectionBackground` → `SelectionBackground`.
  - iTerm2: `Selection Color` → `SelectionBackground`, `Selected Text Color` →
    `SelectionForeground`, `Cursor Text Color` → `CursorText`.
  - Alacritty: `selection.background`/`selection.text`, `cursor.text`,
    `search.matches.background`, `search.focused_match.background`. The
    `CellForeground`/`CellBackground` keywords map to null. `0x…` hex values parse.
- **Theme editor (Settings):**
  - A new **Advanced colors** expander holds the 10 fields. Each row is either *Derived*
    (computed swatch shown dimmed; click to override) or *Override* (swatch + reset).
  - The preview renders through `ThemeService.ApplyTo` on a local scope. It adds a
    selected-text sample, a search match, a cursor with a glyph, an accent button, a status row
    and the four dot shapes.
- **Shipped themes:**
  - Add `SelectionBackground` only where the palette's upstream definition specifies one
    (e.g. Dracula, Catppuccin, Solarized, Nord, Tokyo Night, Gruvbox, GitHub). The PR cites the
    upstream source for each value.
  - Everything else stays derived.
  - `AppPaths` already refreshes newer shipped files on upgrade. Note in the release notes that
    local edits to a *built-in* theme file are overwritten, which was already true.
- **Docs:**
  - Rewrite `docs/ThemeSystem.md`: stale paths, the "restart required" note, the missing UI
    palette, the token vocabulary, and how to add UI that follows the theme.
  - Add a user-manual section on the optional fields.

## 7. Enforcement

### Literal guard (`Ntilde.Architecture.Tests`, gating)

- **XAML:**
  - `XDocument`-parse every shipped `.axaml`, as `CompiledBindingTests` does.
  - Flag any attribute value, `Setter` `Value`, `SolidColorBrush`/`Color` element content or
    attribute that parses as an Avalonia color (hex or a known color name). `Transparent` is
    exempt.
  - Fail-closed: the only exemptions are attributes that cannot hold a color (`Text`,
    `Content`, `Header`, `Title`, `ToolTip.Tip`, `PlaceholderText`, `x:Name`, `Name`,
    `x:Key`, `Classes`, `AutomationProperties.*`). A missing exemption produces a false
    failure, never a missed literal (memory *guards-parse-dont-match-spellings*).
- **C#:**
  - Build a Roslyn **semantic** model over `src/Ntilde.App/**/*.cs`, referencing the loaded
    Avalonia/Skia/Ntilde assemblies (`Microsoft.CodeAnalysis.CSharp` becomes a test-only
    package).
  - Flag, by resolved symbol:
    - any member of `Avalonia.Media.Colors`, `Avalonia.Media.Brushes`, `Avalonia.Media.KnownColors` or `SkiaSharp.SKColors`;
    - any constructor or factory (`Parse`, `FromRgb`, `FromArgb`, `FromUInt32`) producing `Color`, `SKColor`, `TermColor`, `SolidColorBrush` or `ImmutableSolidColorBrush` with all-constant color arguments.
  - Symbol resolution catches target-typed `new(…)`, aliases and `using static`.
  - Allowed locations: `Shell/Theming/**`, `Shell/ThemeImporters/**`, the JSON color
    converters, and the permanent allowlist from §2, each allowlist entry with a written reason.
- **Ratchet:**
  - The guard ships in phase 1 with a baseline file of current per-file counts.
  - It fails if a count rises or a new file appears (so concurrent sessions on `main` cannot
    add literals mid-migration).
  - It also fails if a count falls without the baseline being lowered in the same change.
  - After phase 3 the baseline holds only the permanent allowlist plus the renderer's
    selection/search/bell literals. Phase 4 removes those, leaving the permanent allowlist.

### Unknown-token guard (gating)

- A `DynamicResource` to a missing key renders nothing, silently.
- Every `Nt*` key referenced in XAML must exist in `ThemeKeys`.
- C# references go through `ThemeKeys` constants. The guard rejects raw `"Nt…"` string
  literals outside `Shell/Theming/`.

### Contrast test (plain `[Fact]`, pure, gating)

- **Themes tested:** all 13 shipped themes plus synthetic stress themes: pure white background,
  pure black background, mid-grey `#808080`, the Default theme (`Blue = RGB(0,0,238)`), and a
  theme whose `Blue == Cyan`.
- **Assertions** (the plan may tune the numbers, not the structure):

  | Pair | Minimum |
  |---|---|
  | `Fg` on every surface (`WindowBg`, `ChromeBg`, `SidebarBg` opaque, `Panel`, `PanelAlt`, `Overlay`) | 4.5 |
  | `TabPreviewFg` on `SidebarBg` | 4.5 |
  | `Fg3` on `Panel` | 3.0 |
  | `AccentFg` on `Accent` | 4.5 |
  | `Success`/`Warning`/`Error`/`Info` text on `Panel` | 4.5 |
  | Tab dots on `SidebarBg` and `ChromeBg` | 3.0 |
  | Tab chips on `SidebarBg` | 4.5 |
  | `TabHoverFill`, `TabSelectedFill` vs their surface | visibly distinct (≥ 1.1), and distinct from each other |
  | `Fg` (or `SelectionFg`) on `Selection` composited on `Background` | 4.5 |
  | `SearchMatch` vs `SearchMatchActive` vs `Selection` | pairwise distinguishable |

- Explicit values in shipped themes must pass. User themes are only warned about (MCP
  validator).

## 8. Testing

- **`ThemeTokens.Derive` (plain `[Fact]`):** explicit fields respected verbatim; derivation when
  absent; one `IsDark` rule; `EnsureContrast` moves lightness only; existing `SoftenAccent`
  cases carried over; AgentWrote distinct from Warning/Attention.
- **`ThemeService` (`[AvaloniaFact]`):** brushes mutate in place (same instance before/after
  `Apply`); variant and Fluent palette set; off-UI-thread `Apply` throws; `ApplyTo` does not
  leak into the app dictionary; `Changed` raised once per apply.
- **Pane scope (`[AvaloniaFact]`):** a profile-themed pane resolves its own `NtPanel`; an app
  switch leaves it; clearing the profile removes the scope. Uses the `TestMainWindowFactory` +
  `Show()` + `RunJobs()` pattern (memory *headless-window-tests-are-possible*).
- **Representative live-switch tests (`[AvaloniaFact]`):** command palette, a toast, a
  code-built dialog and a vertical tab row change brushes after `Apply` without being rebuilt.
- **`TabStatusPresentation`:** shape per state (plain `[Fact]`).
- **Renderer:** extend the pixel-level render tests from d8db378 for selection color, selection
  foreground and cursor text.
- **JSON:** round-trip of every shipped theme is byte-identical; an optional-field value that
  is malformed becomes null plus a warning.
- **Importers:** fixture files for each new mapping, including Alacritty keywords and `0x`
  values.
- **MCP:** drift test; schema lists optional fields; contrast warnings.
- **Rewrite existing tests** that pin `ApplyThemeToUI` internals (e.g. title-bar buttons
  sharing the window-foreground brush instance).
- **AOT:** grep an AOT publish for IL2026/IL3050 after the nullable JSON change (memory
  *release-is-nativeaot-reflection-breaks*).
- **Manual smoke checklist** (per memory *gui-manual-smoke-test-preference*): GitHubLight and
  Dracula; horizontal and vertical tabs; a pane with a profile theme; live switch with Settings,
  an SSH prompt and the command palette open.
- Run targeted projects via `scripts/build.ps1`; App.Tests with `--blame-hang-timeout 5m`,
  never concurrently (memory *apptests-local-run-protocol*).

## 9. Phasing

Each phase is a PR into `main` (originally `dev-review`, never created), developed in a worktree
(the main checkout is shared with other sessions). Each phase lowers the guard baseline.

1. **Foundation:**
   - `ThemeColorMath`, `ThemeTokens`, `ThemeKeys`, `ThemeService` (install, apply, Fluent
     palette, variant);
   - the literal guard with its ratchet baseline, the unknown-token guard and the contrast
     test;
   - Settings and Connection Manager moved to app tokens and both default blocks deleted;
     Agent Output markdown starts resolving tokens.
   - Visible change: light-theme fixes in those two windows, and Fluent controls following the
     theme.
2. **Main window:**
   - tab strip in both orientations and the sidebar (§4);
   - title bar, splitters, toasts, command palette, transfer overlay, record and observe
     indicators;
   - dialogs live, and `ApplyThemeToDialogWindow` deleted.
3. **Panes and panels:** the pane scope (D7), then every remaining surface in §5 marked [3],
   including askpass and Replay. The baseline drops to the permanent allowlist plus the
   renderer literals phase 4 removes.
4. **Schema:** optional fields, JSON, `TerminalRenderColors` and the renderer changes, MCP and
   drift test, importers, the editor's Advanced section, shipped selection values, docs.

Phases 1–3 need no schema change. Phase 4 depends only on phase 1.

## 10. Risks

| Risk | Mitigation |
|---|---|
| Mutating `Application.Resources` poisons headless runs (it happened with `UiScale`) | `VerifyAccess` + owner-application guard; every test touching it is `[AvaloniaFact]`; `Derive` stays pure |
| `DynamicResource` to a mistyped key renders invisibly | unknown-token guard |
| Fluent `ColorPaletteResources` mapping mis-tunes stock controls | plan phase 1 defines the mapping table; a test asserts a stock ComboBox dropdown and ContextMenu background resolve to token colors; smoke checklist |
| Local `ColorPaletteResources` in a pane scope unsupported | spike test in phase 3; fallback is variant-only for stock controls in overridden panes |
| Nullable `TermColor?` breaks source-gen JSON or AOT | plan-time check of `AppJsonContext`; AOT publish grep |
| Conflicts with `dev` (touches `MainWindow.axaml`, `SettingsWindow.axaml`, `NewSshConnectionView.axaml`; adds no color literals, measured) | resolve at dev→main merge; the guard catches any literal that slips in |
| Concurrent sessions add literals to `main` mid-migration | ratchet fails on any count increase |
| Existing tests pin old `ApplyThemeToUI` internals | rewritten in the phase that removes them |

## Success criteria

- The contrast test passes for all shipped themes and stress cases.
- The literal-guard baseline equals the permanent allowlist (end of phase 4), and new literals
  fail CI from phase 1 on.
- `ThemePaletteResources.Apply` per-window calls, `ApplyThemeToDialogWindow`, UI uses of
  `GetContrastForeground` and the inline luminance copies are gone.
- A live theme switch updates every open surface, including open dialogs and profile-themed
  panes (tests + smoke checklist).
- Every existing theme file loads unchanged and re-saves byte-identically. Importers carry the
  new fields.
