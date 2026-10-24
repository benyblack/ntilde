# Theme System v2 — Phase 3 (Panes and panels) Implementation Plan

**Spec:** `docs/superpowers/specs/2026-09-23-theme-system-design.md`, §3 "Profile-themed panes
(D7)" and the §5 rows marked [3]. Phases 1 (#584) and 2 (#588) put Settings, the Connection
Manager, Agent Output and the main window's chrome on the app tokens.

**Goal:** everything inside a terminal pane, and the remaining panels and windows, follow the
theme. A pane whose profile picks its own theme gets its own token scope, so its overlays match
its terminal rather than the app. After this phase the colour-literal baseline holds only what
phase 4 removes from the renderer.

**PRs target `main`.** Build and test through `scripts/build.sh`; App.Tests in CI's two lanes.

## Conventions

- XAML inside a pane uses `{DynamicResource Nt…}`, which resolves the pane's scope when there is
  one and the app's tokens otherwise.
- **Code-built UI inside a pane binds** with `GetResourceObservable(ThemeKeys.X)`, never a brush
  instance read once: the scope can appear or go while the control lives, and only an observable
  binding follows that. (The main window's chrome holds instances; it follows the app theme only.)
- Every removed literal lowers `ThemeLiteralBaseline.txt` in the same commit.

## Tasks

1. **Pane scope (D7).** `TerminalPane.ApplySettings` compares the pane's effective theme
   (`Profile?.ThemeName ?? settings.ThemeName`) with the app's. When they differ, the pane gets a
   token dictionary (`ThemeService.ApplyTo` with `ThemeTokens.Derive(effective, opacity)`) and a
   `ThemeVariantScope` around its content set to that theme's variant. When they match, or the
   profile theme is cleared, the scope is removed and the pane follows the app again. An app theme
   switch re-runs `ApplySettings` on every pane, which keeps an overriding pane's scope. Fluent's
   palette is not scoped (best effort per the spec): stock controls in an overriding pane follow
   its variant.
2. **Pane overlays.**
   - Paste toast and pane search: `NtOverlay`, `NtHairline`, `NtPanelAlt` for the search box,
     `NtFg*` text, `NtAccent` / `NtAccentFg` for the primary action.
   - Markdown toggle: `NtPanel` / `NtFg3`, checked `NtAccentDim` / `NtAccent`.
   - Status bar: `NtChromeBg`, `NtHairline`, `NtFg3` / `NtFg` text; port-forward states
     `NtSuccess` / `NtWarning` / `NtError` / `NtFg4`; agent segment `NtAgentWrote` /
     `NtAgentWatched` / `NtAgentIdle` with `NtFg3` text; shared indicator `NtChromeBg` /
     `NtAgentWatched`.
   - Inactive-pane dim: `NtScrim`. Bell flash: the pane theme's foreground at α 0x26.
3. **Command Assist bubble and popup:** `NtOverlay`, `NtHairline`, `NtAccent`, `NtSelectedFill` /
   `NtAccentDim`, `NtFg*`.
4. **Remote Files sidebar and `RemotePathTextBox`:** `NtPanel`, `NtPanelAlt`, `NtHairline`,
   `NtSelectedFill`, `NtAccentBorder`, `NtFg*`, `NtError`.
5. **Remaining panels and windows:** Transfer dialog, the SSH connection editor's warning and error
   text, Replay (tokens), the `SessionManager` and `TerminalView` UI literals, and the askpass
   helper.
6. Docs ("Phase 3 as landed"), baseline, full verification, before/after screenshots, PR.

## Tests

- Pane scope: a pane whose profile names another theme resolves its own `NtPanel`; an app switch
  leaves it; clearing the profile theme removes the scope and the pane follows the app.
- Overlays resolve the pane's scope, including code-built status-bar segments.
- Command Assist, Remote Files and the Transfer dialog on a light theme.
