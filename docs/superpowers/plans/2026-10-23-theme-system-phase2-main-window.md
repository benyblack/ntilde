# Theme System v2 — Phase 2 (Main window) Implementation Plan

**Spec:** `docs/superpowers/specs/2026-09-23-theme-system-design.md`, §4 (tab strip and sidebar),
§3 "Consumers", and the §5 rows marked [2]. Phase 1 landed in #584 (app-wide tokens, Settings,
Connection Manager, Agent Output, the guards).

**Goal:** the main window's own chrome follows the theme through the app tokens, live, with no
per-pass brush allocation: tab strip (both orientations) and vertical sidebar, title bar and its
buttons, splitters, toasts, command palette, transfer overlay and Transfer Center, record button
and agent observe light, code-built dialogs and About. `ApplyThemeToDialogWindow` and every UI use
of `GetContrastForeground` go.

**PRs target `main`.** Build and test only through `scripts/build.sh`. App.Tests with CI's two
lanes (`Lane!=PlatformBoot` and `Lane=PlatformBoot`), never concurrently.

## Conventions

- XAML: `{DynamicResource Nt…}` only.
- Code-built UI holds the token brush **instance** (`this.FindResource(ThemeKeys.X)`), which
  ThemeService recolours in place. State changes swap which token a property points at; nothing
  allocates a brush per pass. The main window's chrome follows the app theme (spec §3, D7), so
  no observable binding is needed here; pane scopes are phase 3.
- Theme variant: windows inherit the application's (ThemeService sets it). The main window and
  dialogs stop setting their own.
- Every removed literal lowers `ThemeLiteralBaseline.txt` in the same commit.

## Tasks

1. **Dead code.** Delete `TabHeaderTemplate`, the main window's unused `SearchPanel` (each pane
   has its own), the `DragBorder`/`headerBg` code, and the `Button#SettingsBtn` selectors.
2. **Window, title bar, dialogs.**
   - `MainWindow` `Foreground` is `NtFg`; the window stops setting `RequestedThemeVariant`.
   - Title-bar buttons take Fluent's `ButtonForeground` (already `NtFg` since phase 1):
     `ApplyTitleBarForegrounds`/`TitleBarContrastForeground` go. `BtnNewTab` hover `NtHoverFill`.
   - Tab-list overflow badge "+N" and the tab-list button's hidden-tabs tint: `NtTabChipAttention`.
   - Record button: `NtRecording` glyph, `NtErrorDim` background while recording.
   - Agent observe light: `NtAgentWatched` / `NtAgentIdle`.
   - `CreateThemedDialogWindow`, the SSH prompts and the mux install dialog style their window
     through one helper that points `Background`/`Foreground` at `NtWindowBg`/`NtFg`, so open
     dialogs follow a live switch. `ApplyThemeToDialogWindow` goes. About links: `NtLink` /
     `NtLinkHover`.
3. **Tab strip and sidebar (§4).**
   - Row text `NtTabFg` / `NtTabFgSelected`; hover and selected fills `NtTabHoverFill` /
     `NtTabSelectedFill`; the whole-row `Opacity=0.6` goes.
   - Selection underline / 3px bar and the drag insert indicator: `NtTabIndicator`.
   - Sidebar surface `NtSidebarBg` with a 1px `NtSidebarDivider`, grip `NtGripHover` on hover.
   - Overflow pill: `NtSidebarBg`, `NtHairline` top line, `NtTabFg` text.
   - Preview line `NtTabPreviewFg`; chips `NtTabChip*`; progress bar Working / Attention /
     Activity tokens.
   - Status dot shapes (D5): `TabStatusPresentation.ResolveDotShape` — Working and Attention
     filled circle, AgentWrote filled diamond, AgentWatched hollow ring. The dot becomes a `Path`.
   - `UpdateTabVisuals` allocates no brushes.
4. **Splitters.** Styles keyed on the existing state classes: `NtHairline` at rest,
   `NtHairlineStrong` on hover, `NtAccentBorder` while dragging. `ApplySplitterVisualState` and the
   split grid's fixed `dividerBrush` go.
5. **Toasts, command palette, transfers.**
   - Recording and update toasts: `NtOverlay`, `NtHairline`, `NtFg` / `NtFg2`.
   - Command palette: `NtScrim`, `NtOverlay`, `NtAccentBorder`, `NtFg` / `NtFg3`.
   - Transfer overlay and Transfer Center: `NtOverlay`, `NtChromeBg`, `NtHairline`, `NtFg*`,
     `NtError`; `StateBrushConverter` resolves Info / Success / Error / Fg4 tokens.
6. **`GetContrastForeground`** is deleted from `TerminalTheme` if no caller remains; docs, the
   spec's "Phase 2 as landed", full verification, before/after screenshots, PR.

## Tests

- Tab strip: a header built under one theme shows the new theme's tokens after `ThemeService.Apply`
  without a rebuild; a light theme gives dark row text; the dot shape per state (plain `[Fact]`
  on `TabStatusPresentation`); `UpdateTabVisuals` reuses brush instances across passes.
- A code-built dialog and a toast follow a live switch.
- Command palette and Transfer Center resolve tokens on a light theme.
- Existing tests that pin `ApplyThemeToUI` internals (title-bar buttons sharing the window
  Foreground instance, contrast-black icons) are rewritten against the tokens.
