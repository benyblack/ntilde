# Theme System v2 — Phase 4 (Schema and renderer) Implementation Plan

**Spec:** `docs/superpowers/specs/2026-09-23-theme-system-design.md`, §1 (schema), §2 "Render
colors", §3 "Renderer", §6 "Schema propagation" and the §5 row marked [4]. Phases 1-3 (#584, #588,
#590) put every UI surface on the tokens; the renderer's selection, search and cursor colours are
what remains.

**Goal:** themes can carry 10 optional colours (selection, search, cursor text, accent, status).
Absent means derived, so every existing theme loads and re-saves unchanged. The renderer draws
selection, search and the block cursor from the pane's theme, and the colour-literal baseline
ends empty: only the permanent allowlist remains.

**Two PRs into `main`**, so each stays reviewable:

- **4a, schema and renderer:** tasks 1-5.
- **4b, importers, editor and shipped values:** tasks 6-9.

Build and test through `scripts/build.sh`. App.Tests runs in CI's two lanes.

## Findings that shape the plan

- `AppJsonContext` (`Shell/AppJsonContext.cs:24`) sets no `DefaultIgnoreCondition`. The new
  properties take `[JsonIgnore(Condition = WhenWritingNull)]` each, which leaves the other
  serialized types alone.
- **Byte-identity:** the shipped files use lowercase hex, and the writer emits uppercase. A
  load-then-save of a shipped file is therefore not byte-identical today. The test pins what phase
  4 owes: for every shipped theme, the save is byte-identical to the save made before the change
  (no new keys, same order). "Re-save byte-identically" in the spec means exactly that.
- `TermColorJsonConverter` turns a malformed value into `Transparent` silently. The optional
  fields need null plus a logged warning, so they get a `TermColor?` converter of their own. The
  20 required fields keep today's behaviour.
- Theme colours reach `TerminalDrawOperation` through the buffer's `RenderThemeSnapshot`, not
  through constructor arguments. The snapshot also carries the theme it came from (`Source`), and
  the draw operation asks `TerminalRenderColors.For(theme)` for selection, search and cursor text.
  `For` derives through `ThemeTokens` (one derivation for chrome and renderer), cached per theme
  instance behind a fingerprint of its colours, so a frame costs a hash, not a derivation. Every
  path that renders a buffer (live panes, Replay, snapshots, tests) is covered with no new
  plumbing, and the theme-epoch invalidation already redraws on a theme change.
- Opaque explicit selection or search colours would hide the text under them, because overlays
  draw after the text. The renderer redraws the covered text on top of each overlay (and the
  glyph under a block cursor), so an overlay is a fill under the glyphs.
- The renderer's literals:
  - selection `(51,153,255,100)`, match `(255,255,0,100)` and active match `(255,128,0,150)` at
    `TerminalDrawOperation.cs:476-478`;
  - the `TermColor.White` fallbacks in `ResolvePaletteIndex` (2931, 2954);
  - `TerminalView.SelectionBrush`, which nothing references.

## Tasks

### 4a

1. **Schema.**
   - Add 10 `TermColor?` properties to `TerminalTheme`: `SelectionBackground`,
     `SelectionForeground`, `CursorText`, `SearchMatch`, `SearchMatchActive`, `Accent`, `Success`,
     `Warning`, `Error` and `Info`.
   - `Clone()` copies them.
   - JSON:
     - `WhenWritingNull` on each new property;
     - an optional-colour converter (malformed → null, with a `TerminalLogger` warning);
     - a check that `AppJsonContext` source-gen still compiles and an AOT publish shows no new
       IL2026/IL3050 warnings.
2. **Derivation.**
   - `ThemeTokens.Derive` uses explicit `Accent`/`Success`/`Warning`/`Error`/`Info` verbatim. When
     one is absent, it derives the colour as today.
   - The spec's defaults:
     - selection: `Accent` at α 0x66;
     - search match: `Warning` at α 0x64;
     - active match: the midpoint hue of `Warning`/`Error` at α 0x96;
     - cursor text: `Background`.
   - These come from one place, used by both the tokens and `TerminalRenderColors`.
   - When the theme's text would not reach 4.5:1 on the selection (Solarized), a selected-text
     colour is derived; an explicit `SelectionForeground` always wins.
3. **Renderer.**
   - `RenderThemeSnapshot` carries its source theme, and the draw operation reads
     `TerminalRenderColors.For(theme)` in place of the three literals.
   - The text under a selection or search match is redrawn over the fill.
   - A block cursor redraws the glyph under it in `CursorText`.
   - When the theme sets `SelectionForeground`, selected text draws in it.
   - `ResolvePaletteIndex` falls back to the theme foreground, not white.
   - `SelectionBrush` is deleted.
   - The baseline file loses both entries.
4. **MCP and drift.**
   - `ThemeTools` gains `OptionalColorFields`, and the schema says "derived when omitted".
   - The validator stops warning on the new fields as unknown, rejects malformed values, and warns
     (never fails) when an explicit accent or status colour misses 4.5:1 against the derived
     `Panel`.
   - A drift test over `TerminalTheme`'s `TermColor`/`TermColor?` properties checks that they equal
     required ∪ optional.
5. **Contrast test:** selection (`Fg`, or `SelectionFg`, on `Selection` composited over
   `Background`) ≥ 4.5, and search match, active match and selection are pairwise
   distinguishable. The test covers the shipped and stress themes.

### 4b

6. **Importers.**
   - Windows Terminal: `selectionBackground`.
   - iTerm2:
     - `Selection Color`, `Selected Text Color` and `Cursor Text Color`;
     - parse floats with the invariant culture.
   - Alacritty:
     - `selection.background`/`text`, `cursor.text`, and `search.matches.background` and
       `search.focused_match.background`, including inline tables;
     - `CellForeground`/`CellBackground` → null;
     - `0x…` hex.
   - One fixture per importer under `tests/.../Fixtures/Themes/`. Today there are no iTerm2 or
     Alacritty tests at all.
7. **Theme editor:**
   - an **Advanced colors** expander with the 10 rows, each *Derived* (dimmed computed swatch;
     click to override) or *Override* (swatch plus reset);
   - `HasUnsavedThemeEdits` covers the new fields;
   - the preview renders through `ThemeService.ApplyTo` on a local scope and adds a selected-text
     sample, a search match, a cursor over a glyph, an accent button, a status row and the four dot
     shapes.
8. **Shipped themes:**
   - add `SelectionBackground` only where the upstream palette defines one, citing the source for
     each value in the PR;
   - everything else stays derived.
9. **Docs:**
   - rewrite `docs/ThemeSystem.md`: the stale paths, the "restart required" note, the token
     vocabulary, the optional fields, and how to add UI that follows the theme;
   - add a themes section to `docs/USER_MANUAL.md`;
   - add a "Phase 4 as landed" section to the spec;
   - add a release note: local edits to a built-in theme file are overwritten on upgrade, as they
     already were.

## Tests

- **JSON:**
  - every shipped theme saves the same bytes as before;
  - a theme with all 10 fields round-trips;
  - a malformed optional value is null and logs a warning;
  - a null optional field is not written.
- **Derivation:** explicit fields are used verbatim; each absent field derives as the spec says.
- **Renderer pixels**, extending `ThemeSwitchPixelTests` or the `RenderTests` harness:
  - the selection colour follows the theme;
  - an explicit `SelectionForeground` recolours selected text;
  - a block cursor shows its glyph in `CursorText`;
  - search and active-match colours follow the theme.
- **MCP:** the drift test, the schema listing the optional fields, and the contrast warnings.
- **Importers:** one fixture per mapping, including the Alacritty keywords and `0x` values.
- **Editor:** derived vs override rows, reset, unsaved-edit detection and the preview scope.
- **Smoke:**
  - GitHub Light and Dracula: select text, search (Ctrl+F), and check the block cursor on a
    character;
  - import an iTerm2 theme with a selection colour;
  - override and reset an advanced colour in the editor.
