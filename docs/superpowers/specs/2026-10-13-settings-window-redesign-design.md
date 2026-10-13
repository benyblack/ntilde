# Settings window redesign

Date: 2026-10-13
Status: designed (not implemented)
Touches: `src/Ntilde.App/SettingsWindow.axaml(.cs)`, `SettingsSection.cs`, `MainWindow.axaml.cs`
(`OpenSettings` and its callers), `Controls/ConnectionManager.axaml`,
`Shell/ThemePaletteResources.cs`, the Settings tests in `tests/Ntilde.App.Tests/Core/`, and
the docs that name settings pages. Interacts with theme system v2
(`2026-09-23-theme-system-design.md`, branch `feat/theme-v2-foundation`, not merged).

## Summary

The Appearance tab has become the default home for anything without one. Today it holds the
theme picker, editor and preview, about 12 full-width title-bar cards, 7 window rows, the font
rows, three notification toggles, the update-check toggle and the scrollback limit, which is
several screens of scrolling in a 620 px window. Behind it, `SettingsWindow.axaml.cs` is 3,450
lines with no internal boundaries: one constructor wires every tab, and state is shared across
tabs through fields.

This redesign:

- splits the window into ten topic pages;
- turns the theme editor into a sub-page;
- replaces the title-bar cards with a grouped editor that has a live preview;
- adds a settings search that jumps to the matching row;
- rebuilds the window as a thin frame hosting one control per page, behind a small contract.

The Save/Cancel model does not change.

## Decisions (made with the owner, 2026-10-13)

| Question | Decision |
|---|---|
| Scope | Full window review: layout, navigation, row component, search, code split. |
| Sequencing vs theme v2 | Settings first. Keep today's colours, moved into shared files that theme v2 later swaps for app tokens in one place. |
| Page structure | Topic pages: General / Appearance / Terminal / Tabs & title bar / Shells & Profiles / Shortcuts / Command Assist / Agent Access / SSH / Backup & Restore. |
| Theme editor | Sub-page inside Settings ("‹ Appearance" back link), fields left, live preview right. |
| Title-bar actions | Live mini title bar plus rows grouped In the bar / In the … menu / Hidden, each with a Bar / Menu / Hide switch; ▲▼ only in the bar group. |
| Search | A results list across all pages; choosing a result opens the page, scrolls to the row and flashes it. |
| Code structure | One control per page behind `ISettingsPage`; `SettingsWindow` becomes the frame. |
| Save/Cancel | Unchanged. Actions that write immediately are labelled as such. |
| Hidden SSH panel on Profiles | Removed. |
| Long descriptions | Short line, plus the rest behind a "More" toggle. |
| Connection manager styles | Moves onto the shared palette and style files in this project. |
| Default page | Last page visited in this app run, otherwise General. |
| Target branch | `main`. |

## Current state (measured on main at ee60cc2)

- **XAML** (`SettingsWindow.axaml`, 1,129 lines): 7 `TabItem`s in a `TabControl` whose header
  strip is hidden. Four sidebar `ListBox`es (`InterfaceNav` 3 items / `AssistantNav` 2 /
  `ConnectionNav` 1 / `DataNav` 1) map onto tab indices 0-2 / 3-4 / 5 / 6 through hand-written
  offsets, which are kept in step by `SyncSidebarFromTabs`.
- **Code-behind** (`SettingsWindow.axaml.cs`, 3,450 lines):
  - The constructor (124-975) wires every tab.
  - `LoadCurrentSettings` (3058-3214) and `SaveAndClose` (3346-3443) read and write every
    control by `FindControl` name.
  - `ReloadSettingsAfterExternalChangeAsync` (1243-1332) repeats the constructor's
    initialisation order by hand.
- **State shared across tabs:**
  - `_shortcutDraftBindings` is read by the title-bar rows, the Command Assist descriptions and
    Save. Shortcut edits call `RebuildTitleBarRows()`, which also calls
    `UpdateCommandAssistShortcutText()`.
  - The font choices on Appearance read `_selectedProfile?.FontFamily` from Profiles.
- **Entry points:** `MainWindow.OpenSettings(int tabIndex, Guid? profileId, SettingsSection
  section, bool selectBackupPage)` is the only construction site. Its callers are:

  | Caller | Call |
  |---|---|
  | Manage Profiles… | `OpenSettings(1)` |
  | Customize Title Bar… | `(0, SettingsSection.TitleBar)` |
  | Ctrl+, / palette "Settings" / title-bar settings button | `OpenSettings(0)` |
  | palette backup.export / import / restore | `OpenSettingsToBackupPage` |

  No caller passes a profile id.
- **Save/Cancel:**
  - Save collects every control, then the drafts, then calls `_settings.Save()` and
    `Close(true)`.
  - Cancel is `Close()`. `MainWindow.ApplySettingsWindowResult` reverts 9 previewed values:
    opacity, blur, background image ×3, font, size, theme, UI scale.
  - Theme save, delete and import; snippet edits; history clear; and backup import/restore
    write immediately and are not reverted.
- **Two latent problems:**
  - `SaveAndClose` writes every field into `_settings` before it validates shortcuts (3363
    vs 3417), so a rejected Save still changes the in-memory object.
  - The sidebar promises "Hit ⌘S… to persist", but no key handler exists.
- **Dead UI:**
  - The Profiles editor's SSH panel and its "Connection type" combo are always hidden or
    disabled (1843-1847). SSH profiles live in the connection manager.
  - `PopulateJumpHostList` is never called.
- **Stored values are display strings.** Several combos persist `ComboBoxItem.Content`:
  `AgentIndicatorTabRollup` ("WritesOnly"/"All"), `BackgroundImageStretch` ("UniformToFill"…),
  `BlurEffect`, `TabStripOrientation`.
- **Styles:**
  - 19 `Nt*` brushes are in `Window.Resources` and the styles in `Window.Styles`, both inside
    `SettingsWindow.axaml`.
  - `ConnectionManager.axaml` duplicates 21 brush keys (adding `NtBlueFaint`, `NtBlueBorder`,
    `NtMagenta`) and five styles.
  - `ThemePaletteResources.Apply` recolours by reading `resources[key]` on the passed dictionary
    itself (`ThemePaletteResources.cs:48`), which does not search merged dictionaries.
- **Tests:** about 48 tests in `tests/Ntilde.App.Tests/Core/`, 36 of which construct the window.
  They depend on the tab count, tab indices, the sidebar list names and counts, control names
  (`MainTabs`, `TitleBarItemsPanel`, `ThemeList`…), and private members by reflection
  (`_titleBarDraft`, `_shortcutDraftBindings`, `RebuildTitleBarRows`,
  `ReloadSettingsAfterExternalChangeAsync`, `ScrollToTitleBarSection`).

## Design

### 1. Pages

The sidebar groups and pages are listed in order below. The page subtitles are working copy.

**GENERAL**

- **General**:
  - Updates: automatic update checks.
  - Notifications: long-command notifications, program notifications (OSC 9), progress
    reporting (OSC 9;4).

**INTERFACE**

- **Appearance**:
  - Theme: picker with Edit / Import… / + New, and a compact preview (sample line + 16
    swatches).
  - Window: opacity, interface scale, blur.
  - Background image: path, opacity, stretch mode.
- **Edit theme** (a sub-page of Appearance; not in the sidebar). See §7.
- **Terminal**:
  - Font: family, size, programming ligatures, complex shaping.
  - Scrollback: limit.
- **Tabs & title bar**:
  - Tabs: tab strip orientation.
  - Title bar actions: see §6.
- **Shells & Profiles**: today's list + editor. The SSH panel (`SshSettingsPanel`, the jump
  host combo, the port-forward editor) and the disabled "Connection type" combo are removed,
  with their handlers (§3).
- **Shortcuts**: unchanged apart from the shared row styling.

**ASSISTANT**

- **Command Assist**: unchanged content.
- **Agent Access**: unchanged content. "WritesOnly / All" is shown as "Writes only / All
  activity" (§8).

**CONNECTION**

- **SSH**: unchanged content.

**DATA**

- **Backup & Restore**: unchanged content.

### 2. Frame and navigation

**Page registry.** A static list of `SettingsPageDescriptor(SettingsPage Page, string Group,
string Title, string Glyph, Func<ISettingsPage> Create)` defines the sidebar order, groups,
titles and glyphs. The sidebar, the navigation code and the search index are all driven from
it. Existing pages keep their glyphs. General, Terminal and Tabs & title bar get glyphs in
PR 3, from the same plain-Unicode set.

**`SettingsPage` enum:** `General, Appearance, Terminal, TabsAndTitleBar, Profiles, Shortcuts,
CommandAssist, AgentAccess, Ssh, Backup`.

**`SettingsTarget(SettingsPage Page, string? RowId = null)`** is the window's only "where to
open" input. It replaces `initialTab`, `SettingsSection` and `SelectBackupPage()`.
`SettingsSection.cs` is deleted.

**Sidebar:**

- One `ListBox` with non-selectable group-header items, so there is one selection and no
  cross-list syncing.
- The selected item's glyph uses the accent colour, and unselected glyphs use `NtFg3`. Today
  the Appearance glyph is accent-coloured whether or not it is selected.
- The search box sits at the top of the sidebar (§5).
- The "Changes apply live. Hit ⌘S…" block at the bottom of the sidebar is removed. The footer
  already says "Changes apply live."

**Content host.**

- A `ContentControl` shows the current page.
- All pages are created when the window opens, as today's XAML does, so Save can collect from
  every page and search can index every row. Only the current page is in the visual tree.

**Opening Settings.** `MainWindow.OpenSettings(SettingsTarget target)`:

| Caller | Target |
|---|---|
| Ctrl+, / palette "Settings" / title-bar settings button | `MainWindow`'s last page visited in this run, otherwise `General` |
| Manage Profiles… | `Profiles` |
| Customize Title Bar… | `TabsAndTitleBar` |
| palette backup.export / import / restore | `Backup` |

**Last page.** `MainWindow` keeps the last page visited in a field. It is not persisted, and
it is updated when the window closes, whether by Save or by Cancel.

**Jump to row.** When a target has a `RowId`, the frame:

1. selects the page;
2. waits for layout;
3. calls `BringIntoView` on the `SettingRow` with that id;
4. runs a highlight of about 1.5 s (an `NtBlueDim` background fading out).

Search and deep links share this mechanism. The old title-bar scroll special case
(`ScrollToTitleBarSection`) is gone, because the actions now have a short page of their own.

**Keyboard:**

- Ctrl+S (Cmd+S on macOS) runs Save.
- Ctrl+F (Cmd+F) focuses the search box.
- Esc in the search box clears the search and returns to the page that was showing.

**Footer.**

- "Changes apply live." with Cancel and Save, as today.
- Cancel gets a tooltip: "Reverts preview changes. Themes, snippets, history and backups are
  saved as you change them."

### 3. Page contract, shared state, Save/Cancel

```csharp
internal interface ISettingsPage
{
    SettingsPage Page { get; }
    Control View { get; }                       // the page's root control
    void Load(SettingsSession session);         // fill controls from the session
    SettingsValidationError? Validate();        // null = valid
    void Collect(TerminalSettings target);      // write controls back; Save only
}

internal sealed record SettingsValidationError(SettingsPage Page, string? RowId, string Message);
```

**`SettingsSession`** (one per open window) owns:

- the working `TerminalSettings`;
- the shortcut draft (today's `_shortcutDraftBindings`) and the `ShortcutsChanged` event;
- the title-bar draft (`TitleBarDraftState`);
- the injected `IHistoryStore` / `ISnippetStore`;
- the font-family choices (computed from settings plus every local profile's font override,
  so the Terminal page no longer reads the Profiles page's selection);
- a preview sink (below).

**Page-local state:**

- Profiles owns the profile list, the selection, and the logic that re-points or clears the
  selection after a reload (today 1335-1421).
- Appearance owns the theme being edited.
- Command Assist owns the snippet editor and the armed "Clear history" state.

**Cross-page refresh.**

- The Shortcuts page raises `session.ShortcutsChanged` after any edit or reset.
- Tabs & title bar re-renders its shortcut text when it fires.
- Command Assist updates its chord descriptions and snippet hint when it fires.
- This replaces the direct calls at 2112, 2405 and 2510.

**Live preview.** Pages call `session.Preview.Font(...)`, `.Theme(...)` and so on. The window
re-raises its existing seven public events (`OnOpacityChanged`, `OnBlurChanged`,
`OnBgImageChanged`, `OnFontChanged`, `OnFontSizeChanged`, `OnUiScaleChanged`,
`OnThemeChanged`). `MainWindow`'s subscriptions and `ApplySettingsWindowResult` do not change.
The internal store properties and events (`CommandAssistHistoryStore`,
`CommandAssistSnippetStore`, `OnCommandAssistHistoryCleared`,
`OnCommandAssistSnippetsChanged`) also keep their names on the window.

**Save:**

1. `Validate()` every page. On the first error, navigate to `(Page, RowId)` and show the
   message there. Nothing has been written yet.
2. `Collect()` every page into the working settings.
3. Commit the drafts: keybindings, `TitleBarItems` / `TitleBarOrder` deltas, profiles via
   `NormalizeSettingsProfilesForSave` / `ResolveDefaultLocalProfileId`.
4. Call `_settings.Save()`, then `Close(true)`.

Validation moves first, so a rejected Save no longer changes the in-memory object.

**Cancel:** `Close()`, unchanged.

**Reload after backup import/restore** (`ReloadSettingsAfterExternalChangeAsync`) becomes:

1. The session reloads settings and both drafts.
2. `Load()` runs on every page in registry order.

`ConfigurationReplacedExternally` keeps its meaning and its effect on `MainWindow`.

**Removed:**

- The SSH panel handlers (today 595-665).
- The port-forward helpers (1888-2002), including the never-called `PopulateJumpHostList`.

The removal is behaviour-neutral. The panel was never visible, so its handlers never ran, and
local profiles keep whatever SSH field values they already have.

### 4. Shared styles and the row component

**Files.** Both live in `src/Ntilde.App/Settings/`:

- **`NtPalette.axaml`** (`ResourceDictionary`): the `Nt*` brushes, as the union of today's two
  key sets with today's default colours.
  - Settings merges it into `Window.Resources`; the connection manager merges it into
    `UserControl.Resources`.
  - Each merge must yield its own brush instances, so each surface recolours independently,
    as today.
- **`NtStyles.axaml`** (`Styles`):
  - SectionHeader, H1, Subtitle, RowLabel, RowDesc;
  - TextBox / NumericUpDown / ComboBox / Slider / RadioButton / Separator;
  - Button.Primary / Pill / Danger / IconBtn;
  - CheckBox.Toggle;
  - the sidebar;
  - the `SettingRow` / `SettingGroup` control themes.

  Settings and the connection manager both include it, and the connection manager's duplicate
  styles are deleted.

**Rules that keep theme switching working:**

1. **`ThemePaletteResources.Apply` resolves through merged dictionaries.** It uses
   `TryGetResource` and mutates the brush it finds. It only adds a new brush when the key
   truly does not exist. Without this, moving the brushes into a merged dictionary would make
   `Apply` add new, unreferenced brushes, and Settings would silently stop recolouring.
2. **Shared styles and page XAML reference brushes with `{DynamicResource Nt…}`.** A page
   control loads its XAML in its own constructor, before it is attached to the window. At that
   point a `StaticResource` cannot see the window's brushes.
3. **Code-built rows keep resolving and holding the brush instance**, as `ThemeResourceBrush`
   does today.

**`SettingRow`** (templated `ContentControl`):

- Properties: `Title`, `Description`, `Details` (optional; shown behind a "More" / "Less"
  toggle), `Keywords` (optional, search only), `RowId`, `Indent` (sub-options such as "Agent
  replay export").
- Its `Content` is the control on the right.
- It draws its own bottom hairline. The roughly 40 hand-placed
  `<Border BorderThickness="0,0,0,1">` separators and their 14 / 18 px margin drift are deleted.

**`SettingGroup`** (templated `HeaderedItemsControl` or equivalent): a section header plus its
rows, with fixed spacing above and below. This fixes the missing gap above WINDOW.

**Rows built in code** (title-bar actions, shortcut bindings, snippets) keep being built in
code. They take their brushes from the shared palette and their spacing from the same
constants.

**Colour literals:**

- No new colour literals. The search results, row flash, Bar / Menu / Hide switch, "More"
  toggle and mini title bar all use `Nt*` brushes.
- The existing literals stay for theme v2, whose spec already assigns them tokens: the Danger
  button's `#3a1a1d` / `#5c2329` / `#4a2024`, the preview's `#111`, and the toggle knob's
  `White`.

### 5. Search

**Index.** Built once when the window opens, by walking every page's `SettingRow`s, plus one
entry per page title. Each entry holds the page, `RowId`, title, description, keywords and the
group header. The index is read from the rows themselves, so a new row is searchable with no
extra step.

**Not indexed in v1:**

- individual shortcut bindings (the Shortcuts page has its own filter);
- profiles, snippets and title-bar actions;
- the theme editor sub-page.

**Matching:**

- Case-insensitive substring match over title, description, keywords, group header and page
  title.
- Ranking: title matches first, then the rest, then registry order.

**UI:**

- While the search box has text, the content host shows a results list instead of the page.
  The heading reads "N results for "q"".
- Each result shows `Page › Group`, the title (with the match highlighted) and the
  description.
- ↑ / ↓ move the selection, Enter or a click jumps to the row (§2), and Esc clears the search.
- With no results, the list shows "No settings match "q"."

### 6. Title-bar actions editor

```
TITLE BAR ACTIONS
[ ~/nova2 ][ pwsh ] +                       ☰ ⇄ ⚙ …   — □ ✕     ← live mini bar

IN THE BAR                                          4 of 8
  +  New Tab           Ctrl+T     Always in the bar
  ☰  Tab List                     [Bar|Menu|Hide]   ▲ ▼
  ⇄  Connections                  [Bar|Menu|Hide]   ▲ ▼
  ⚙  Settings          Ctrl+,     [Bar|Menu|Hide]   ▲ ▼
IN THE … MENU                                           7
  ●  Record Session               [Bar|Menu|Hide]
  ...
HIDDEN                 1 · still reachable by shortcut and the command palette
  ◌  Agent Activity               [Bar|Menu|Hide]
```

**Mini bar:**

- Two sample tabs, then the pinned icons in order, using `TitleBarCatalog` geometries.
- An "…" icon when the menu group is non-empty.
- Dim caption buttons.
- It is a lightweight replica, not a real title bar, and it re-renders on every draft change.

**Row behaviour:**

- Rows are one line each.
- The switch is three segmented radio buttons. Choosing a value moves the row to that group.
- ▲▼ appear only in the bar group and act on `TitleBarDraftState` order, as today.
- New Tab keeps today's locked behaviour.

**Limits:**

- The 8-pin limit stays in `TitleBarDraftState`.
- The UI shows the count and disables "Bar" on unpinned rows when the bar is full, with the
  tooltip "The title bar holds up to 8 actions".
- `TitleBarValidationMessage` stays for any rejection the draft still raises.

### 7. Theme editor sub-page

**Opening it:**

- Appearance's Edit opens the sub-page for the selected theme.
- + New opens it with a new theme draft.
- Import… stays on Appearance.
- The sidebar keeps Appearance selected.

**Layout:**

- A "‹ Appearance" link, the theme name as the heading, and the subtitle "Saving a theme writes
  it to disk straight away. Cancel does not undo it."
- Left column: name; foreground / background / cursor; the 16 ANSI swatches. Theme v2 later
  adds its Advanced colors group here.
- Right column (about 220 px): the live preview, i.e. today's sample text and swatches, moved.
- Below the fields: Save theme and Delete, plus the status text.
- At the 780 px minimum width the two columns still fit: 508 px of content, with the 256 px
  swatch grid on the left.

**Leaving it:**

- The back link drops unsaved edits, as today's ✕ does.
- Delete returns to Appearance after deleting.
- The theme list is refreshed whenever the editor saves or deletes.

### 8. Copy and consistency fixes

- **Descriptions** are at most two lines. The longer text moves into `Details` behind
  "More", unchanged in wording. This applies to:
  - Remember commands
  - Remote shell integration
  - Native SSH backend
  - the Shortcuts chord caveat
  - Program notifications (OSC 9)
  - Automatic update checks

  The security wording about what history records stays complete.
- **Readable labels, unchanged stored values.** Combo items carry the stored value in `Tag`
  and show a label as `Content`. Load and Collect use `Tag`.
  - `AgentIndicatorTabRollup`: "Writes only" → `WritesOnly`, "All activity" → `All`.
  - `BackgroundImageStretch`: "Don't stretch" → `None`, "Stretch" → `Fill`, "Fit" →
    `Uniform`, "Fill (crop)" → `UniformToFill`.
  - `BlurEffect` and `TabStripOrientation` are already readable. They move to the same
    Tag/Content pattern so every combo works the same way.
- The sidebar's misleading "⌘S" text is removed, and Ctrl/Cmd+S now works (§2).

## Behaviour that must not change

- Every `TerminalSettings` field the window reads and writes today is read and written with the
  same stored value (see the round-trip test below).
- `MainWindow`'s live preview and the Cancel revert of the 9 snapshot fields.
- Immediate writes stay immediate: theme save / delete / import, snippet edits, history clear,
  backup import / restore. So do `ConfigurationReplacedExternally` and the reload path after
  import / restore.
- Shortcut duplicate detection blocks Save. The title-bar 8-pin limit and the locked New Tab
  still apply.
- Local and SSH profile separation (`BuildLocalProfilesForEditor`,
  `NormalizeSettingsProfilesForSave`, `ResolveDefaultLocalProfileId`).
- Theme switches recolour Settings and the connection manager live, including rows built in
  code.

## Testing

**Rewritten** (all `tests/Ntilde.App.Tests/Core/`, against the frame and pages):

- SettingsWindowBackupSectionTests
- SettingsWindowSelectBackupPageTests (becomes target-navigation tests)
- SettingsWindowTitleBarSectionTests (becomes TabsAndTitleBar target tests)
- SettingsWindowTitleBarShortcutRefreshTests (now through `ShortcutsChanged`)
- ThemeApplicationRegressionTests (the Settings part)
- SettingsWindowUiScaleTests
- MainWindowBackupPaletteTests
- MainWindowCustomizeTitleBarSettingsTargetTests

Tests that reached private fields by reflection go through the session or page APIs instead.
The static-helper tests (FontChoices, ProfileSeparation, ShortcutFiltering, ShortcutText) move
with their helpers unchanged.

**New:**

- **Round trip.** Settings with every window-owned field set to a non-default value. Open,
  Save untouched, reload, and assert every field is equal. This is the guard against a field
  being dropped or a label replacing a stored value. A second case adds local profiles with
  SSH field values set and asserts they survive.
- **Registry.**
  - Every `SettingsPage` has exactly one descriptor.
  - The sidebar order and groups match §1.
  - Header items cannot be selected.
- **Navigation.**
  - Each `MainWindow` entry point opens the page in the §2 table.
  - The last page is remembered within a run.
  - A `RowId` target scrolls to the row and starts the highlight.
- **Validation.** A duplicate shortcut blocks Save, navigates to Shortcuts, and leaves the
  in-memory settings untouched.
- **Keyboard.** Ctrl+S saves; Ctrl+F focuses search.
- **Search.**
  - "opacity" returns Window opacity and Image opacity on Appearance.
  - Enter navigates; Esc restores the previous page.
  - The no-results text shows.
  - A row added to a page is indexed with no extra registration.
- **Shortcut refresh.** Changing a shortcut updates the Tabs & title bar rows and the Command
  Assist text.
- **Title-bar editor.**
  - Switching a row's group moves it.
  - "Bar" is disabled at 8 pins.
  - The mini bar matches the draft.
- **Palette.**
  - A theme switch recolours a brush that lives in the merged `NtPalette`.
  - Settings and the connection manager hold different brush instances.
  - Rows built in code recolour.
- **Theme editor sub-page.** Edit opens it for the selected theme, back discards edits, and
  Delete returns to Appearance.

**Conventions:**

- `[AvaloniaFact]` for anything that touches `Application.Current`.
- App.Tests run locally as the two CI lanes (`Lane!=PlatformBoot`, then `Lane=PlatformBoot`).
- Before PRs 3-5 merge, an AOT publish is scanned for IL2026 / IL3050. A reflection binding in
  new XAML would only fail in installed releases.

**Visual check:**

- Headless screenshots of every page, the theme editor sub-page, the search results and the
  title-bar editor, under one light and one dark shipped theme, attached to PRs 3-5.
- Manual smoke-test steps for the owner in the real app.

## Docs

PR 3 updates every page reference: `docs/USER_MANUAL.md` (page list at 12-28 and later
mentions), `README.md`, `TABS_USER_MANUAL.md`, `ThemeSystem.md`, `command-assist/CommandAssist.md`,
`RemoteShellIntegration.md`, `SSH_ROADMAP.md`, `Native_SSH_Test_Matrix.md`, `mcp/security.md`,
`agent-host/DIRECTION.md`, `mcp-dev-companion.md`, and the in-app strings that name a page. Most
of the in-app strings ("Settings > SSH", "Settings → Agent access") stay valid. Each is checked
rather than assumed.

## Delivery

Five PRs to `main`, in order, worked in the `.worktrees/settings-redesign` worktree. Each PR
gets its own branch, cut from `main` after the previous PR has merged:

| # | Content | User-visible |
|---|---|---|
| 1 | `NtPalette.axaml` + `NtStyles.axaml`; `Apply` resolves through merged dictionaries; connection manager adopts both; palette tests | No |
| 2 | Frame: registry, single sidebar, `SettingsPage` / `SettingsTarget`, `OpenSettings(SettingsTarget)`, `SettingsSession`, `ISettingsPage`, `SettingRow` / `SettingGroup`; today's 7 tabs moved one-to-one into page controls; tests rewritten; round-trip test | Sidebar becomes one list; Ctrl+S |
| 3 | The re-homing: General / Terminal / Tabs & title bar pages, theme editor sub-page, "More" details, readable labels, SSH panel removal, copy fixes, docs | Yes, the main change |
| 4 | Grouped title-bar editor with live mini bar | Yes |
| 5 | Search | Yes |

PR 2 is a pure restructure. The round-trip test lands in that PR, so the riskiest move is
guarded before any layout changes.

## Interaction with other work

- **Theme v2** (`feat/theme-v2-foundation`, phase 1 done, unmerged):
  - When it resumes, its Settings migration targets `NtPalette.axaml` (one file instead of two
    windows) and the token literals listed in §4.
  - Its Advanced colors group goes into the theme editor sub-page (§7).
  - Its plan's references to `SettingsWindow.axaml` line numbers will need refreshing.
- **dev lane** (screen inference, PR #469 and others): adds two rows and about 140 code-behind
  lines to Agent Access. At the dev→main merge they are ported into the Agent Access page as
  `SettingRow`s. The dev lane's handover should note this.
- **Shots harness** (`feat/shots-harness`, unmerged): `SettingsWindowScenario`,
  `SettingsAgentAccessScenario` (tab 4) and `SettingsAppearanceScenario` (tab 0, "FONTS & TEXT")
  depend on the old tabs and need `SettingsTarget` when that branch resumes.

## Out of scope

- Changing the Save/Cancel model, or making Cancel undo the immediate actions.
- A view-model layer for Settings.
- Replacing colour literals with tokens (theme v2).
- Indexing shortcuts, profiles or snippets in search.
- Redesigning the Profiles, Shortcuts or Command Assist pages beyond the shared row styling.
- Persisting the last-visited page across runs.

## Risks

| Risk | Mitigation |
|---|---|
| A field silently dropped, or a label written as a value, in the move | Round-trip test in PR 2, extended in PR 3 |
| Theme switching stops recolouring after the palette move | `Apply` via `TryGetResource`; palette instance and recolour tests in PR 1 |
| `StaticResource` in page XAML fails to resolve | `DynamicResource` rule (§4); every page is constructed by a test |
| Headless test poisoning from new controls touching `Application.Current` | `[AvaloniaFact]`; run both lanes locally |
| AOT-only reflection failures in new XAML | Compiled bindings only; IL2026 / IL3050 scan before PRs 3-5 |
| Conflicts with the dev lane's Agent Access rows | Port at the dev→main merge, as noted in the dev lane's handover |
