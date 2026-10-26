# Focus Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A per-window toggle (Ctrl+Shift+Enter) that hides all app chrome around the selected tab, leaves a slim strip with the tab name, the agent light and the window buttons, and puts everything back exactly when turned off.

**Architecture:** One flag, `MainWindow._isFocusMode`, owned by a new partial `MainWindow.FocusMode.cs`. Every existing owner of a piece of chrome checks the flag when it lays that chrome out: `UpdateTabHeaderViewport`, `RebuildTitleBar`, `WirePane`/`TerminalPane` visibility methods, `FlushPersistenceNotices`, `RefreshAgentObserveIndicator`. Enter/leave then just set the flag and re-run those owners. The flag is saved only in the startup session file.

**Tech Stack:** C# / .NET 10, Avalonia 12 (headless xUnit tests via `[AvaloniaFact]`), NativeAOT release builds, source-generated JSON (`SessionSerializationContext`).

**Spec:** `docs/superpowers/specs/2026-10-23-focus-mode-design.md` (read it first; this plan argues from it).

## Global Constraints

- Shortcut: id `toggle_focus_mode`, title `View: Toggle Focus Mode`, category `View`, `ShortcutScope.App`, default `Ctrl+Shift+Enter` (macOS form comes from `ShortcutPlatform.DefaultBinding`).
- Palette: `CommandRegistry.Register("Toggle Focus Mode", "View", …, GetEffectiveShortcutBinding("toggle_focus_mode", "Ctrl+Shift+Enter"), "toggle_focus_mode")`, so its `FullTitle` is `View: Toggle Focus Mode`.
- Strip height: `MainWindow.FocusStripHeight = 24` (px). Normal title band stays 36, and the title bar overlay stays `Height=32`, `Margin.Top=4`.
- Strip look: transparent background (the `WindowBackground` grid shows through), title text in `TitleBarContrastForeground()` at `Opacity = 0.5`, `FontSize = 11`, `TextTrimming = CharacterEllipsis`.
- Agent light colours: write `#E8A33D` (the pane segment's amber), read/active `#4FB0D4`, idle `#6B737F`.
- Focus mode is stored only as `NtildeSession.FocusMode`, written only by `SessionManager.SaveSession`. It must never be a `TerminalSettings` field, which would trip the Settings pane whitelist and the MCP SettingsTools drift guard.
- Build and test only through `scripts/build.ps1` (Windows) or `scripts/build.sh`, never raw `dotnet build`/`dotnet test`. `dotnet format whitespace --verify-no-changes --no-restore` is the CI format check.
- Every test that builds a `MainWindow` or touches `Application.Current` or the dispatcher is `[AvaloniaFact]`, takes `IClassFixture<TestAppDataRoot>`, and disposes via `TestMainWindowFactory.DisposeCreatedWindows()`. Never start `AgentHostService`.
- Source files are CRLF. Edit with the Edit tool. Never `sed -i`, which rewrites a file as LF and fails the format check.
- Agent indicators must never go silent (`docs/mcp/security.md`). The attention machine (`AgentAttentionMachine`) is not changed.

## Review Focus

These are the inputs most likely to bite that no task's main tests exercise. Each has a test in the task named.

1. **Saving Settings while in focus mode.** `ApplySettingsWindowResult` re-runs `ApplyThemeToUI`, `ApplySettingsToAllTabs`, `RebuildTitleBar` and `ApplyTabLayout`. The chrome must stay hidden and the strip title must take the new theme colour. Test: Task 1, `Reapplying_settings_while_in_focus_mode_keeps_the_chrome_hidden`.
2. **Restoring into focus mode with deferred tabs.** Only the selected tab is built at startup; placeholder tabs hydrate later and must come up with their pane chrome hidden. Test: Task 7, `Deferred_tabs_hydrated_after_a_focus_mode_restore_hide_their_pane_chrome`.
3. **The selected tab closing by itself.** If the selected tab's shell exits (not a user action), focus mode stays on and the strip shows the newly selected tab. Test: Task 4, `A_tab_closing_on_its_own_keeps_focus_mode_and_retitles_the_strip`.
4. **An update notice already on screen when focus mode starts.** It hides with the chrome and comes back on leaving unless dismissed. Test: Task 5, `An_update_toast_on_screen_is_hidden_on_enter_and_returns_on_leave`.
5. **Vertical tabs.** Entering and leaving with the vertical sidebar must not change the persisted orientation or `VerticalTabStripWidth`, and must leave the sidebar docked left at its clamped width. Test: Task 1, `Vertical_sidebar_round_trips_without_changing_orientation_or_width`.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Ntilde.App/MainWindow.FocusMode.cs` (new) | the flag; enter, leave and toggle; the strip; title bar, decorations and drag band in focus mode; leaving for navigation; held notices; focus-mode helpers for the agent light |
| `src/Ntilde.App/MainWindow.axaml` | the `FocusStrip` element and its menu; an "Exit Focus Mode" item in the title bar menu |
| `src/Ntilde.App/MainWindow.axaml.cs` | one-line hooks into existing owners (key handler, palette, `UpdateTabHeaderViewport`, `RebuildTitleBar`, `WirePane`, `ApplyThemeToUI`, notice call sites, agent executor, `RefreshAgentObserveIndicator`, startup restore, teardown save) |
| `src/Ntilde.App/App.axaml` | `NtildeFocusWindowDecorationsTheme` |
| `src/Ntilde.App/Controls/TerminalPane.FocusMode.cs` (new partial) | `FocusChromeHidden`; pure visibility rules for pane chrome; starting a background pane's shell |
| `src/Ntilde.App/Controls/TerminalPane.axaml.cs` | the visibility owners consult `FocusChromeHidden` |
| `src/Ntilde.App/Shell/Shortcuts/ShortcutCatalog.cs` | catalog entry |
| `src/Ntilde.Pty/SessionModels.cs`, `src/Ntilde.App/Shell/SessionManager.cs` | `FocusMode` field, `SaveSession(..., focusMode)` |
| `tests/Ntilde.App.Tests/Core/WindowTestSupport.cs` (new) | `NewShownWindow`, `AddSplitTab` (moved from `AgentObserveIndicatorTests`), `TemplatePart<T>`, `Invoke` (private method by reflection) |
| `tests/Ntilde.App.Tests/Core/FocusMode*Tests.cs` (new) | one test class per task |
| `docs/USER_MANUAL.md`, `docs/TABS_USER_MANUAL.md`, `docs/mcp/security.md` | user-facing docs |

---

### Task 1: The toggle and the window chrome

**Files:**
- Create: `src/Ntilde.App/MainWindow.FocusMode.cs`, `tests/Ntilde.App.Tests/Core/WindowTestSupport.cs`, `tests/Ntilde.App.Tests/Core/FocusModeWindowTests.cs`
- Modify: `src/Ntilde.App/Shell/Shortcuts/ShortcutCatalog.cs` (after the `toggle_pane_zoom` entry), `src/Ntilde.App/MainWindow.axaml` (new `FocusStrip` Grid after `TitleBarBadgeParkingSlot`, before `TitleBar`), `src/Ntilde.App/MainWindow.axaml.cs`:
  - the key handler: a `toggle_focus_mode` branch next to `toggle_pane_zoom`;
  - `SetupCommandPalette`: next to "Pane: Toggle Zoom";
  - `UpdateTabHeaderViewport`;
  - the end of `RebuildTitleBar`;
  - `TopLevel_PointerPressed`;
  - the end of `ApplyThemeToUI`;
  - the `tabs.SelectionChanged` handler and `UpdateTabVisuals`, which call `UpdateFocusStrip()`.
- Modify: `tests/Ntilde.App.Tests/Core/AgentObserveIndicatorTests.cs` (use `WindowTestSupport.AddSplitTab` / `RegistrationFor`, delete its private copies), `tests/Ntilde.App.Tests/Core/ShortcutCatalogTests.cs`, `docs/USER_MANUAL.md` §3.2, `docs/TABS_USER_MANUAL.md` (next to "Toggle pane zoom")

**Interfaces:**
- Produces (MainWindow, `internal` unless noted):
  - `const double FocusStripHeight = 24`
  - `bool IsFocusMode { get; }`, backed by `private bool _isFocusMode`
  - `void EnterFocusMode()`, `void LeaveFocusMode()`, `void ToggleFocusMode()`: idempotent and safe before the template is applied
  - `private void ApplyFocusModeChrome()`: re-runs `UpdateTabHeaderViewport()`, `ApplyFocusModeToTitleBar()` and `UpdateFocusStrip()`. Later tasks add their own hooks to enter and leave, not to this method.
  - `private void ApplyFocusModeToTitleBar()`
  - `private void UpdateFocusStrip()`
  - `static double WindowDragBandHeight(bool focusMode)`: 24 in focus mode, 36 otherwise
- Produces (tests):
  - `WindowTestSupport.NewShownWindow()` (`Create`, `Show`, `RunJobs`);
  - `WindowTestSupport.AddSplitTab(MainWindow, out TerminalPane first, out TerminalPane second)` and `WindowTestSupport.RegistrationFor(TerminalPane)`, both moved unchanged from `AgentObserveIndicatorTests`;
  - `WindowTestSupport.TemplatePart<T>(MainWindow, string name)`;
  - `WindowTestSupport.Invoke(MainWindow, string privateMethod, params object[] args) → object?`. Callers cast a returned `Task` and await it.

- [ ] **Step 1: Write the failing tests**

`ShortcutCatalogTests`:
```csharp
[Fact]
public void GetEntries_IncludesToggleFocusMode()
{
    var e = Assert.Single(ShortcutCatalog.GetEntries(isMacOS: false), x => x.CommandId == "toggle_focus_mode");
    Assert.Equal(("View: Toggle Focus Mode", "View", ShortcutScope.App, "Ctrl+Shift+Enter"),
                 (e.Title, e.Category, e.Scope, e.DefaultBinding));
}

[Fact]
public void Ctrl_Shift_Enter_matches_the_focus_mode_binding()
{
    var e = new KeyEventArgs { Key = Key.Enter, KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift };
    Assert.True(ShortcutMatcher.Matches(e, "Ctrl+Shift+Enter"));
}
```

`FocusModeWindowTests` (`IClassFixture<TestAppDataRoot>, IDisposable`, all `[AvaloniaFact]`):
- `The_shortcut_toggles_focus_mode`: `window.KeyPress(Key.Enter, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.Enter, null)`, then `Assert.True(window.IsFocusMode)`; press it again, then `Assert.False`.
- `The_palette_lists_toggle_focus_mode_with_its_binding`: `Invoke(window, "SetupCommandPalette")`, then a single command with `Id == "toggle_focus_mode"`, `FullTitle == "View: Toggle Focus Mode"` and `Shortcut == ShortcutPlatform.DefaultBinding("toggle_focus_mode", "Ctrl+Shift+Enter")`. Invoking its `Action` sets `IsFocusMode` to true.
- `Entering_hides_the_tab_area_and_title_bar_items_and_shows_the_strip` (horizontal):
  - `PART_TabSidebar.IsVisible` is false;
  - `PART_TitleBandSpacer.Height` is 24;
  - `FocusStrip.IsVisible` is true;
  - `FocusStripTitle.Text` equals the selected tab's header text (`Invoke(window, "GetTabHeaderText", tab)`);
  - every child of `TitleBarItemsHost` except `AgentObserveIndicator` has `IsVisible == false`;
  - `TitleBar.Height` is 24 and `TitleBar.Margin.Top` is 0.
- `Leaving_restores_the_horizontal_layout`. Record the following before entering, then enter, leave and `RunJobs`:
  - `TitleBarItemsHost.Children.Count`;
  - each child's `Name` and `IsVisible`.

  Assert the same list, plus: sidebar visible, spacer 0, strip hidden, `TitleBar.Height == 32`, `TitleBar.Margin.Top == 4`.
- `Vertical_sidebar_round_trips_without_changing_orientation_or_width` (Review Focus 5). Set `_settings.TabStripOrientation = "Vertical"`, then `ApplyTabLayout()` and `RunJobs`. Enter: spacer is 24, sidebar hidden. Leave: spacer is 36, sidebar visible, `DockPanel.GetDock(sidebar) == Dock.Left`, and the scroll viewer's `Width == TabStripLayout.ClampSidebarWidth(settings.VerticalTabStripWidth)`. The orientation is still "Vertical" and `VerticalTabStripWidth` is unchanged.
- `Focus_mode_leaves_zoom_selection_and_split_alone`. Use `AddSplitTab` and select it, then enter and leave without zoom: `tab.Content` is still the same `Grid` with both panes. Then zoom one pane (`EnterPaneZoom` via reflection, as `AgentObserveIndicatorTests.EnterZoom` does), enter and leave: `IsPaneZoomActiveForTab(tab)` is still true, `GetZoomedPaneIdForTab(tab)` is unchanged, and the selected tab is the same.
- `Toggling_zoom_inside_focus_mode_stays_in_focus_mode`: in focus mode, `KeyPress` Ctrl+Shift+Z on a split tab. Zoom is now active and `IsFocusMode` is still true.
- `The_strip_title_follows_the_selected_tab`: add a second tab with `AddSplitTab` and set `tabs.SelectedItem` to it from code. `IsFocusMode` stays true and `FocusStripTitle.Text` is the new tab's header text.
- `Reapplying_settings_while_in_focus_mode_keeps_the_chrome_hidden` (Review Focus 1): in focus mode, `Invoke` `ApplyThemeToUI`, `ApplySettingsToAllTabs`, `RebuildTitleBar` and `ApplyTabLayout`, then `RunJobs`. The same assertions as the "Entering_hides…" test still hold.
- `The_drag_band_follows_the_strip`: `MainWindow.WindowDragBandHeight(true) == 24` and `WindowDragBandHeight(false) == 36`.

- [ ] **Step 2: Run to verify they fail**

Run: `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~FocusModeWindowTests|FullyQualifiedName~ShortcutCatalogTests"`
Expected: build fails (`IsFocusMode`, `WindowDragBandHeight` and `FocusStripHeight` do not exist).

- [ ] **Step 3: Implement**

- **Catalog:** `new("toggle_focus_mode", "View: Toggle Focus Mode", "View", ShortcutScope.App, "Ctrl+Shift+Enter")`.
- **XAML:** `<Grid x:Name="FocusStrip" IsVisible="False" VerticalAlignment="Top" Height="24" ZIndex="90" Background="Transparent">` containing `<TextBlock Name="FocusStripTitle" VerticalAlignment="Center" FontSize="11" Opacity="0.5" TextTrimming="CharacterEllipsis"/>`. The context menu is added in Task 2.
- **`UpdateTabHeaderViewport`:** an early focus branch that sets `sidebar.IsVisible = false` and `spacer.Height = FocusStripHeight`, then returns before any width, margin or overflow work, so nothing persisted is touched. Both existing branches gain `sidebar.IsVisible = true`.
- **`UpdateTabOverflowIndicator`:** in focus mode it sets `badge.IsVisible = false` and returns. It runs from paths other than `UpdateTabHeaderViewport` (`PopulateTabListMenu`, layout passes), and the badge can be parented inside `TitleBarItemsHost`, so without this guard it would reappear in the strip. Assert it in `Entering_hides…`: `TabOverflowBadge.IsVisible` is false even after `Invoke(window, "UpdateTabOverflowIndicator")`.
- **`ApplyFocusModeToTitleBar()`:**
  - In focus mode: every `TitleBarItemsHost` child except `AgentObserveIndicator` gets `IsVisible = false`; `TitleBar.Height = FocusStripHeight`; `TitleBar.Margin` keeps its Left, Right and Bottom with `Top = 0`.
  - Otherwise: `BtnNewTab.IsVisible = true`, `Height = 32`, `Top = 4`.
  - Called at the very end of `RebuildTitleBar`.
- **`LeaveFocusMode`:** calls `RebuildTitleBar()`, which regenerates every generated item with its own visibility, then `ApplyFocusModeChrome()`.
- **`UpdateFocusStrip()`:**
  - `FocusStrip.IsVisible = _isFocusMode`;
  - title text from `GetTabHeaderText(selected)`, or "" when nothing is selected;
  - `Foreground = TitleBarContrastForeground()`;
  - `Margin` left: `MacOsTrafficLightReserve` on macOS, 10 otherwise;
  - `Margin` right: `TitleBar.Margin.Right + 40`, which clears the light.
- **`TopLevel_PointerPressed`:** use `WindowDragBandHeight(_isFocusMode)` instead of the literal 36.
- **Docs:**
  - USER_MANUAL §3.2 bullet: "**Focus Mode:** `Ctrl+Shift+Enter` hides the tabs, title bar buttons, status bars and side panels, leaving only the current tab and a slim strip with the window buttons. Every tab keeps running. Press it again, or right-click the strip and choose Exit Focus Mode, to bring everything back."
  - TABS_USER_MANUAL: "Toggle focus mode: `Ctrl+Shift+Enter`".

- [ ] **Step 4: Run to verify they pass**

Run: the Step 2 command. Expected: `Passed!`, no failures. Also run `--filter "FullyQualifiedName~AgentObserveIndicatorTests"` to confirm that moving `AddSplitTab` didn't break it. Expected: `Passed!`.

- [ ] **Step 5: Commit**

`git add` the files above, then `git commit -m "feat(focus-mode): toggle hides the tab area and title bar items behind a slim strip"`.

---

### Task 2: Window buttons in the strip, and ways out

**Files:**
- Modify: `src/Ntilde.App/App.axaml` (after `NtildeWindowDecorationsTheme`)
- Modify: `src/Ntilde.App/MainWindow.axaml` (`FocusStrip` context menu; title bar context menu)
- Modify: `src/Ntilde.App/MainWindow.FocusMode.cs` and the `TopLevel_PointerPressed` handler in `src/Ntilde.App/MainWindow.axaml.cs`
- Test: `tests/Ntilde.App.Tests/Core/FocusModeWindowTests.cs`

**Interfaces:**
- Consumes: Task 1's `EnterFocusMode`, `LeaveFocusMode`, `FocusStripHeight` and `WindowDragBandHeight`.
- Produces: `internal static WindowState ToggledMaximizeState(WindowState current)`, which returns `Normal` for `Maximized` and `Maximized` for anything else.

- [ ] **Step 1: Write the failing tests**
- `Entering_swaps_in_the_slim_decorations_theme_and_leaving_swaps_it_back`:
  - after enter: `Assert.Same(window.FindResource("NtildeFocusWindowDecorationsTheme"), window.WindowDecorationsTheme)`;
  - after leave: `Assert.Same(window.FindResource("NtildeWindowDecorationsTheme"), window.WindowDecorationsTheme)`.
- `The_strip_menu_exits_focus_mode`: in focus mode, `window.FindControl<MenuItem>("MenuExitFocusMode")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent))`. `IsFocusMode` is false.
- `The_title_bar_menu_swaps_customize_for_exit_in_focus_mode`:
  - in focus mode: `MenuCustomizeTitleBar.IsVisible` false, `MenuTitleBarExitFocusMode.IsVisible` true;
  - clicking `MenuTitleBarExitFocusMode` leaves focus mode;
  - afterwards: Customize visible, Exit hidden.
- `Double_click_maximize_state_toggles`: `ToggledMaximizeState(Maximized) == Normal`, `(Normal) == Maximized`, `(Minimized) == Maximized`.

- [ ] **Step 2: Run to verify they fail**

Run: `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~FocusModeWindowTests"`. Expected: build fails (`ToggledMaximizeState` missing).

- [ ] **Step 3: Implement**
- **App.axaml:**
  ```xml
  <ControlTheme x:Key="NtildeFocusWindowDecorationsTheme" TargetType="WindowDrawnDecorations"
                BasedOn="{StaticResource NtildeWindowDecorationsTheme}">
      <Setter Property="DefaultTitleBarHeight" Value="24"/>
      <Style Selector="^ /template/ StackPanel#PART_OverlayPanel > Button">
          <Setter Property="Height" Value="24"/>
      </Style>
  </ControlTheme>
  ```
  This Style setter outranks the `NtildeDrawnCaptionButton` theme's Height of 30.
- **Theme swap:** enter sets `WindowDecorationsTheme = (ControlTheme)this.FindResource("NtildeFocusWindowDecorationsTheme")!`; leave sets it back to `"NtildeWindowDecorationsTheme"`.
- **XAML menus:**
  - `FocusStrip` gets `<Grid.ContextMenu><ContextMenu><MenuItem Header="Exit Focus Mode" Name="MenuExitFocusMode"/></ContextMenu></Grid.ContextMenu>`.
  - The `TitleBar` context menu gains `<MenuItem Header="Exit Focus Mode" Name="MenuTitleBarExitFocusMode" IsVisible="False"/>`.
  - Wire both Click handlers to `LeaveFocusMode` once, in the constructor next to `MenuCustomizeTitleBar`.
  - `ApplyFocusModeToTitleBar` sets both items' visibility.
- **`TopLevel_PointerPressed`:** in focus mode, when the press is inside the drag band and `e.ClickCount == 2`, set `WindowState = ToggledMaximizeState(WindowState)` and mark the event handled instead of calling `BeginMoveDrag`.

- [ ] **Step 4: Run to verify they pass.** Same command. Expected: `Passed!`.

- [ ] **Step 5: Manual check on Windows**

Headless tests don't build drawn decorations. Run the test build beside the sidecar (see memory "Test build beside the daily sidecar": `run-sidecar.ps1 -SidecarRoot ...-test` with its own `NTILDE_APPDATA_ROOT`), then:
- Enter focus mode. The minimise, maximise and close buttons are 24 px tall, sit inside the strip, and the amber/grey dot (with agent access on) sits just left of them.
- Drag the window by the strip.
- Double-click the strip: it maximises. Double-click again: it restores, with no double toggle.
- Right-click the strip and choose Exit Focus Mode.

If the buttons stay 30 px tall after the theme swap, record the height actually reached in the PR description. The spec allows "as slim as they allow". Don't add a different mechanism without coming back to the owner.

- [ ] **Step 6: Commit** `feat(focus-mode): slim window buttons, double-click maximise and exit menus in the strip`

---

### Task 3: Pane chrome

**Files:**
- Create: `src/Ntilde.App/Controls/TerminalPane.FocusMode.cs` (`public partial class TerminalPane`), `tests/Ntilde.App.Tests/Core/FocusModePaneChromeTests.cs`
- Modify: `src/Ntilde.App/Controls/TerminalPane.axaml.cs`:
  - `UpdateStatusBarVisibility`;
  - the `markdownPresenceChanged:` lambda (around line 800), which calls `OnMarkdownPresenceChanged`;
  - `UpdateAgentOutputPanelVisibility`;
  - `UpdateRemoteFilesSidebarVisibility`;
  - `ToggleRemoteFilesSidebar`.
- Modify: `src/Ntilde.App/MainWindow.axaml.cs` (`WirePane`), `src/Ntilde.App/MainWindow.FocusMode.cs` (enter and leave set it on `AllPanes()`)

**Interfaces:**
- Consumes: Task 1's `EnterFocusMode` and `LeaveFocusMode`.
- Produces (TerminalPane):
  - `internal bool FocusChromeHidden { get; set; }`. The setter re-applies on change: status bar, `TermScrollBar.IsVisible = !FocusChromeHidden`, MD toggle, Agent Output panel, Remote Files. Turning it on sets `_remoteFilesHiddenByFocus = <Remote Files currently open>`; turning it off clears that flag.
  - `internal void OnMarkdownPresenceChanged(bool present)`, which stores `_markdownPresent`.
  - `internal static bool ShouldShowRemoteFilesSidebar(bool isOpen, bool altScreen, bool supported, bool hiddenByFocus)`
  - `internal static (bool IsOpen, bool HiddenByFocus) ResolveRemoteFilesToggle(bool isOpen, bool hiddenByFocus)`: a toggle while the panel is open but hidden by focus mode reveals it (`(true, false)`). Otherwise it flips `isOpen` and leaves `hiddenByFocus` alone.

- [ ] **Step 1: Write the failing tests** (`FocusModePaneChromeTests`; pure ones `[Fact]`, the rest `[AvaloniaFact]`)
- `Remote_files_visibility_rule`, a `[Theory]` over (isOpen, altScreen, supported, hiddenByFocus) giving the expected result. It's true only for (true, false, true, false). Include (true, false, true, true) as false.
- `A_remote_files_toggle_while_hidden_by_focus_reveals_instead_of_closing`: `ResolveRemoteFilesToggle(true, true) == (true, false)`, `(true, false) == (false, false)`, `(false, false) == (true, false)`.
- `Focus_mode_hides_and_restores_the_status_bar_and_scrollbar`. Make the pane actable with `RegistrationFor(pane).IsAgentActable = true; RunJobs()` (helper as in `AgentObserveIndicatorTests`):
  - before: `StatusBar` and `TermScrollBar` visible;
  - after enter: both hidden;
  - after leave: both visible.
- `Focus_mode_hides_the_markdown_toggle_and_brings_it_back`: `pane.OnMarkdownPresenceChanged(true)`, then the toggle is visible. Enter: hidden. A presence change to true while in focus mode keeps it hidden. Leave: visible.
- `Every_pane_of_a_split_and_a_zoom_stashed_sibling_gets_the_flag`: split tab, zoom pane A, enter. Every pane in `window.AllPanesForTest()` has `FocusChromeHidden == true`, including B, which is off-tree. Leave: all false.
- `A_pane_created_in_focus_mode_starts_hidden`: in focus mode, `KeyPress` Ctrl+Shift+D (split). The new pane has `FocusChromeHidden == true`.

- [ ] **Step 2: Run to verify they fail.** `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~FocusModePaneChromeTests"`. Expected: build fails.

- [ ] **Step 3: Implement**

Each visibility owner ANDs in `!FocusChromeHidden`:
- `StatusBar.IsVisible = (sshForwards || _agentActable) && !FocusChromeHidden`. The agent segment's own visibility stays as it is.
- `AgentOutputToggle.IsVisible = _markdownPresent && !FocusChromeHidden`.
- `AgentOutputPanelPresenter.IsVisible = _agentOutput.IsShown && !FocusChromeHidden`.
- `UpdateRemoteFilesSidebarVisibility` uses `ShouldShowRemoteFilesSidebar(...)` with `_remoteFilesHiddenByFocus`.

The rest:
- `ToggleRemoteFilesSidebar` goes through `ResolveRemoteFilesToggle`.
- `WirePane` sets `pane.FocusChromeHidden = _isFocusMode`.
- Enter and leave loop over `AllPanes()`.

- [ ] **Step 4: Run to verify they pass.** Same command. Expected: `Passed!`.
- [ ] **Step 5: Commit** `feat(focus-mode): hide pane status bars, scrollbars and side panels`

---

### Task 4: User navigation leaves focus mode, and agents never move the user

**Files:**
- Modify: `src/Ntilde.App/MainWindow.axaml.cs`. Each listed method calls `LeaveFocusModeForNavigation()`, at its start unless noted:
  - `AddTab`: new parameters `bool select = true, bool leaveFocusMode = true`, and it now returns `TerminalPane?`, the pane it created;
  - `SwitchTabByMru`;
  - `MoveSelectedTab`;
  - `PopulateTabListMenu`, only when `showFlyout` is true;
  - `ToggleTabOrientation`;
  - `CloseSelectedTabAsync`;
  - `CloseActivePane`, only when `_currentPane` is its tab's only pane (`EnumeratePanes(GetLayoutRootForTab(tab)).Count() == 1`);
  - `AttachToMuxSessionAsync`, just before it selects a tab, whether through `AddTabWithPane(..., select: true)` or `FocusPaneShowingMuxSession`;
  - the per-tab palette lambdas in `SetupCommandPalette` (`tabs.SelectedItem = capturedTab`);
  - `ApplySessionSnapshot`.
- Modify: `src/Ntilde.App/MainWindow.axaml.cs`, `IAgentActionExecutor.SpawnAsync` and `IAgentActionExecutor.ClosePaneAsync`
- Modify: `src/Ntilde.App/Controls/TerminalPane.FocusMode.cs`
- Create: `tests/Ntilde.App.Tests/Core/FocusModeNavigationTests.cs`

**Interfaces:**
- Consumes: Task 1's `LeaveFocusMode` and `IsFocusMode`.
- Produces:
  - `private void LeaveFocusModeForNavigation()`, which is `if (_isFocusMode) LeaveFocusMode();`
  - `TerminalPane.StartSessionInBackground(int cols, int rows)`, which runs `InitializeSession(ShellCommand, Profile, cols, rows)`. That is a no-op once a session exists, because `InitializeSession` already returns early when `Session != null`.
  - `internal static bool ShouldAgentSpawnSelect(bool focusMode)`, which returns `!focusMode`.

- [ ] **Step 1: Write the failing tests** (`[AvaloniaFact]` unless pure)
- `Next_tab_leaves_focus_mode_and_switches`: two tabs, focus mode on, then Ctrl+Tab. `IsFocusMode` is false and the selection changed.
- `New_tab_leaves_focus_mode_and_opens_it`: Ctrl+Shift+T. Not in focus mode, and there is one more tab, which is selected.
- `Toggling_the_vertical_sidebar_leaves_focus_mode`: Ctrl+Shift+L. Not in focus mode, and the orientation flipped.
- `Opening_the_tab_list_leaves_focus_mode`: Ctrl+Shift+O. Not in focus mode.
- `A_palette_tab_switch_leaves_focus_mode`: `SetupCommandPalette`, then run the command whose `Category == "Tabs"` for the other tab. Not in focus mode, and that tab is selected.
- `Splitting_and_equalising_stay_in_focus_mode`: Ctrl+Shift+D, then Ctrl+Shift+G. Still in focus mode, still on the same tab.
- `Closing_one_pane_of_a_split_stays_and_closing_the_last_leaves`:
  - On a split tab, `Invoke(window, "CloseActivePaneAsync", true)` (skipConfirm) and await it. Still in focus mode.
  - On a single-pane tab, with a second tab present, the same call ends focus mode.
- `A_tab_closing_on_its_own_keeps_focus_mode_and_retitles_the_strip` (Review Focus 3):
  - set up two tabs and select A;
  - close A through `ClosePaneAsync(paneOfA)`, the shell-exit path, which is not a navigation verb;
  - still in focus mode, and `FocusStripTitle.Text` equals B's header text.
- `Agent_spawn_selects_only_outside_focus_mode`: `ShouldAgentSpawnSelect(false)` is true and `ShouldAgentSpawnSelect(true)` is false.
- `An_agent_spawn_in_focus_mode_opens_in_the_background_with_a_live_shell`. Call the executor: `await ((IAgentActionExecutor)window).SpawnAsync(null)`, which resolves the default profile. Then:
  - the selected tab and `_currentPane` are unchanged;
  - still in focus mode;
  - `result.PaneId` names a pane in a non-selected tab;
  - that pane's `Session` is not null.
- `An_agent_close_in_focus_mode_does_not_select_the_target_tab`: with tabs A (selected) and B, call `((IAgentActionExecutor)window).ClosePaneAsync(paneOfB.PaneId)`. It returns true, A is still selected, still in focus mode, and B is gone.
- `Agent_actions_outside_focus_mode_are_unchanged`: outside focus mode, spawn selects the new tab (today's behaviour).

- [ ] **Step 2: Run to verify they fail.** `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~FocusModeNavigationTests"`. Expected: build fails.

- [ ] **Step 3: Implement**
- Add the `LeaveFocusModeForNavigation()` calls at the sites listed under Files.
- **`SpawnAsync`:**
  - call `AddTab(profile, select: ShouldAgentSpawnSelect(_isFocusMode), leaveFocusMode: false)`;
  - use the returned pane instead of `_currentPane`;
  - when not selected, call `pane.StartSessionInBackground(cols, rows)`, using `_currentPane?.TermView` Cols/Rows, or 80×24 when there's no current pane.
- **`ClosePaneAsync`:** in focus mode, skip the select / `_activePaneByTab` / `_currentPane` block and call `ClosePaneAsync(target, skipConfirm: true)`, the pane-resolved overload (#311). Outside focus mode, keep today's code path unchanged.
- **`AddTab`:** forwards `select` to `AddTabWithPane`.
- **Sweep:** `grep` every caller of `AddTab(`. All callers except the startup calls (before any restore) and `SpawnAsync` are user actions and keep the defaults.

- [ ] **Step 4: Run to verify they pass.** Same command. Expected: `Passed!`.
- [ ] **Step 5: Commit** `feat(focus-mode): tab navigation leaves focus mode; agent spawns and closes stay in the background`

---

### Task 5: Held notices

**Files:**
- Modify: `src/Ntilde.App/MainWindow.axaml.cs`:
  - `FlushPersistenceNotices`;
  - the "Credential storage unavailable" call in the constructor;
  - `OnPaneLongCommandFinished` (the "Command finished" toast);
  - `OnPaneOscNotification`;
  - the `UpdateCoordinator` version callback (`ShowUpdateToast(version)` inside `version => Dispatcher.UIThread.Post(...)`);
  - `ShowUpdateToast`, which records `_shownUpdateVersion`.
- Modify: `src/Ntilde.App/MainWindow.FocusMode.cs`
- Create: `tests/Ntilde.App.Tests/Core/FocusModeNoticeTests.cs`

**Interfaces:**
- Consumes: `EnqueueNotice(title, message, action, key, untilDismissed)`, `DismissNotice(key)`, `ShowRecordingToast(...)`, `ShowUpdateToast(string)`, `HideUpdateToast()`.
- Produces:
  - `private void ShowUnpromptedToast(string title, string message, string key)`. In focus mode it calls `DismissNotice(key); EnqueueNotice(title, message, key: key);`, so the latest raise wins. Otherwise it calls `ShowRecordingToast(title, message, null, null, autoHide: true)`.
  - `private void ShowUpdateToastUnprompted(string version)`
  - `private void ReleaseHeldNotices()`, called by `LeaveFocusMode`.
  - Fields `_heldUpdateVersion` and `_shownUpdateVersion`.
  - Keys: `focus:command-finished:{pane.PaneId}`, `focus:osc9:{pane.PaneId}`, `focus:credential-storage`.

- [ ] **Step 1: Write the failing tests** (`[AvaloniaFact]`; read the toast as `MainWindowNoticeActionTests.Toast` does)
- `A_persistence_notice_raised_in_focus_mode_waits_for_leave`: enter, `EnqueueNotice("T", "M")`, `RunJobs`. The toast is hidden. Leave, `RunJobs`: the toast is visible with title "T".
- `Several_held_notices_merge_into_one_toast_on_leave`. Hold:
  - one `EnqueueNotice`;
  - two `ShowUnpromptedToast` calls with different keys.

  On leave, the message has three lines, and the title is the last one raised.
- `A_repeated_unprompted_notice_keeps_only_the_latest`: two `ShowUnpromptedToast("Command finished", "first"/"second", "focus:command-finished:x")`, then leave. The message is exactly "second".
- `A_dismissed_held_notice_never_shows`: enqueue with `key: "k"` in focus mode, `DismissNotice("k")`, leave. The toast is hidden.
- `Prompted_toasts_still_show_in_focus_mode`: in focus mode, `ShowRecordingToast("Recording started", …, autoHide: true)` shows immediately.
- `An_update_found_in_focus_mode_waits`: `ShowUpdateToastUnprompted("1.2.3")` in focus mode leaves `UpdateToast` hidden. On leave it's visible and `UpdateToastMessage` contains "1.2.3".
- `An_update_toast_on_screen_is_hidden_on_enter_and_returns_on_leave` (Review Focus 4): `ShowUpdateToast("1.2.3")`, enter (hidden), leave (visible, same version). Also: show, enter, leave, close, then enter and leave again. It stays hidden.

- [ ] **Step 2: Run to verify they fail.** `--filter "FullyQualifiedName~FocusModeNoticeTests"`. Expected: build fails (`ShowUnpromptedToast` missing).

- [ ] **Step 3: Implement**
- **`FlushPersistenceNotices`:** returns early, keeping the pending list, while `_isFocusMode`.
- **Unprompted call sites:** the three listed (credential storage, command finished, OSC 9) switch to `ShowUnpromptedToast`.
- **Update callback:** the coordinator's callback switches to `ShowUpdateToastUnprompted`. The manual-check path keeps `ShowUpdateToast`.
- **Enter:** an `UpdateToast` that's on screen is hidden and `_heldUpdateVersion = _shownUpdateVersion`.
- **`HideUpdateToast`:** clears `_shownUpdateVersion`, so a dismissed toast isn't revived.
- **`ReleaseHeldNotices`:** shows the held update, then calls `FlushPersistenceNotices()`.

- [ ] **Step 4: Run to verify they pass.** Expected: `Passed!`. Also rerun `--filter "FullyQualifiedName~MainWindowNoticeActionTests"`. Expected: `Passed!`, since the flush gate must not change behaviour outside focus mode.
- [ ] **Step 5: Commit** `feat(focus-mode): hold unprompted notices until focus mode ends`

---

### Task 6: The agent light in focus mode

**Files:**
- Modify: `src/Ntilde.App/MainWindow.axaml.cs` (`IsPaneReadInvisibleWithoutWindowLight`, both overloads; `RefreshAgentObserveIndicator`)
- Modify: `src/Ntilde.App/MainWindow.FocusMode.cs`, `docs/mcp/security.md` (the "Visibility while it happens" bullet)
- Test: `tests/Ntilde.App.Tests/Core/AgentObserveIndicatorTests.cs` (new section `// ---- focus mode ----`)

**Interfaces:**
- Consumes: Task 1's `_isFocusMode`. Registrations come from `AgentSessionRegistry.Instance.GetRegistrations()`. Panes and tabs come from `AllPanes()`, `ResolveOwningTabForPane(pane)`, `GetTabHeaderText(tab)` and `pane.GetBaseTabTitle()`.
- Produces:
  - `internal static bool IsPaneReadInvisibleWithoutWindowLight(bool isAgentActable, Guid? paneTabId, Guid? selectedTabId, bool paneHiddenByZoom, bool focusMode)`: returns true whenever `focusMode` is true. The existing four-argument overload delegates with `false`. The instance overload passes `_isFocusMode`.
  - `internal static string ObserveLightColor(bool active, bool unseenWrite)`: `"#E8A33D"` when `unseenWrite`, else `"#4FB0D4"` when `active`, else `"#6B737F"`.
  - `internal sealed record FocusWriteMark(string TabTitle, string? PaneTitle, DateTimeOffset? LastWriteUtc)`. `PaneTitle` is null when the tab has one pane.
  - `internal static string BuildFocusLightTooltip(IReadOnlyList<FocusWriteMark> marks)`
  - `internal IReadOnlyList<FocusWriteMark> CollectUnseenWriteMarks()`: registrations whose snapshot `Tier == Wrote`, in tab order. It's empty when not in focus mode.

Exact tooltip text (`{t}` is `HH:mm:ss` local time, invariant culture; a mark with no time drops the ` at {t}`; `{target}` is `TabTitle`, or `TabTitle › PaneTitle` when `PaneTitle` isn't null):
- no marks: `Agent access is enabled. Click to open the agent activity journal.` (today's XAML text)
- one mark: `An agent typed into {target} at {t}. Click to open the agent activity journal.`
- several: `Agents typed into:` then one line per mark, `- {target} at {t}`, then `Click to open the agent activity journal.`, joined with `\n`.

- [ ] **Step 1: Write the failing tests**
- `In_focus_mode_every_pane_is_unmarked`, a `[Theory]` over the existing cases (actable, selected tab, not hidden by zoom), each with `focusMode: true`. All return true. With `focusMode: false`, they match today's results.
- `Light_colour_priority`: `(false,false)` gives `#6B737F`, `(true,false)` gives `#4FB0D4`, `(true,true)` and `(false,true)` give `#E8A33D`.
- `Focus_light_tooltip_names_tab_and_pane`: covers the three exact strings above, using fixed `DateTimeOffset`s formatted the same way.
- `A_write_to_a_background_tab_is_collected_in_focus_mode` (`[AvaloniaFact]`):
  - set up two tabs, select A, and run `RegistrationFor(paneOfB).AttentionMachine.NoteWrote("sendInput")`;
  - not in focus mode: `CollectUnseenWriteMarks()` is empty;
  - in focus mode: one mark with B's header text and a null `PaneTitle`.
- `A_write_to_the_other_pane_of_a_split_names_the_pane` (`[AvaloniaFact]`): split tab, write to the non-current pane, enter. One mark, with `PaneTitle == thatPane.GetBaseTabTitle()`.
- `Focus_mode_refreshes_the_light`: entering and leaving calls `RefreshAgentObserveIndicator`. Check it the way `Entering_and_leaving_pane_zoom_refreshes_the_indicator` does today.

- [ ] **Step 2: Run to verify they fail.** `--filter "FullyQualifiedName~AgentObserveIndicatorTests"`. Expected: build fails.

- [ ] **Step 3: Implement**
- **`RefreshAgentObserveIndicator`:** computes `marks = CollectUnseenWriteMarks()` and passes `unseenWrite: marks.Count > 0` into `ObserveLightColor`. In focus mode it sets `ToolTip.SetTip(indicator, BuildFocusLightTooltip(marks))`; outside it, it restores the default text.
- **Enter and leave:** both call `RefreshAgentObserveIndicator()`.
- **security.md:** add one sentence: "In focus mode the pane status bars and tab badges are hidden, so the window light in the top strip reports every agent read and write in any tab; a write keeps it amber until you have looked at that pane, and its tooltip names the tab and pane."

- [ ] **Step 4: Run to verify they pass.** Expected: `Passed!`.
- [ ] **Step 5: Commit** `feat(focus-mode): the strip light reports agent reads and writes from every tab`

---

### Task 7: Remember focus mode across restarts

**Files:**
- Modify: `src/Ntilde.Pty/SessionModels.cs` (`NtildeSession`), `src/Ntilde.App/Shell/SessionManager.cs` (`SaveSession`)
- Modify: `src/Ntilde.App/MainWindow.axaml.cs`:
  - the teardown save call (`SessionManager.SaveSession(this, tabs, _localSessionsEndedOnClose)`);
  - the constructor right after `restoredSession = loadedSession;`, which calls `if (loadedSession!.FocusMode) EnterFocusMode();`.
- Create: `tests/Ntilde.App.Tests/Core/FocusModeSessionTests.cs`

**Interfaces:**
- Produces: `public bool FocusMode { get; set; }` on `NtildeSession` (default false), and `SessionManager.SaveSession(Window window, TabControl tabs, IReadOnlySet<Guid>? endedLocalMuxSessions = null, bool focusMode = false)`, which sets `session.FocusMode = focusMode` after `CaptureSession`. `CaptureSession` never sets it.

- [ ] **Step 1: Write the failing tests** (`IClassFixture<TestAppDataRoot>`; seed files with the `WriteSavedSession` pattern from `MainWindowStartupTests`: `sessions/last_session.json` serialized with `SessionSerializationContext.Default.NtildeSession`)
- `Save_with_focus_mode_round_trips`:
  - `SaveSession(window, tabs, null, focusMode: true)`;
  - `SessionManager.TryLoadSavedSession(out var s, out _)`;
  - `Assert.True(s!.FocusMode)`.
- `Workspace_captures_never_carry_focus_mode`: in focus mode, `SessionManager.CaptureSession(window, tabs).FocusMode` is false.
- `An_old_session_file_restores_in_normal_mode`: seed JSON without the property, create a window. `IsFocusMode` is false.
- `A_focus_mode_session_restores_into_focus_mode`:
  - seed a two-tab session with `FocusMode = true`;
  - `NewShownWindow()`: `IsFocusMode` is true, `FocusStrip.IsVisible` is true, and `PART_TabSidebar.IsVisible` is false.
- `Deferred_tabs_hydrated_after_a_focus_mode_restore_hide_their_pane_chrome` (Review Focus 2):
  - same seed, then let the deferred pass run (`RunJobs`, as `MainWindowStartupTests` does);
  - every pane in `AllPanesForTest()` has `FocusChromeHidden == true`.
- `Loading_a_workspace_in_focus_mode_leaves_it_first`: `Invoke(window, "ApplySessionSnapshot", snapshotWithOneTab)`, after which `IsFocusMode` is false. Covered by Task 4's hook; asserted here because it's the snapshot path.

- [ ] **Step 2: Run to verify they fail.** `--filter "FullyQualifiedName~FocusModeSessionTests"`. Expected: build fails (`FocusMode` missing).
- [ ] **Step 3: Implement** as in Interfaces. The teardown passes `focusMode: _isFocusMode`.
- [ ] **Step 4: Run to verify they pass.** Expected: `Passed!`. Also rerun `--filter "FullyQualifiedName~MainWindowStartupTests|FullyQualifiedName~SessionManagerMuxTests"`. Expected: `Passed!`.
- [ ] **Step 5: Commit** `feat(focus-mode): remember focus mode across restarts`

---

### Task 8: Whole-feature verification

**Files:** none new. This is the gate before the PR.

- [ ] **Step 1: Format.** `dotnet format whitespace --verify-no-changes --no-restore` at the repo root. Expected: exit 0.
- [ ] **Step 2: Full App.Tests, two lanes, one at a time** (memory "App.Tests local-run protocol"; never concurrently, even across worktrees):
  - `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "Category!=Replay&Category!=RenderMetrics&Category!=PtySmoke&Category!=Stress&Category!=GoldenSharedPng&Lane!=PlatformBoot" --blame-hang-timeout 5m`, logged to a file. Expected: `Failed: 0`. Read the `N executed` count.
  - then `--filter "Lane=PlatformBoot&Category!=GoldenSharedPng"`. Expected: `Failed: 0`.
- [ ] **Step 3: Manual smoke on Windows** (test build beside the sidecar, isolated `NTILDE_APPDATA_ROOT`):
  1. Open a split tab plus two other tabs, then Ctrl+Shift+Enter. Only the split's panes remain, with a dim strip on top.
  2. Ctrl+Shift+Z zooms and unzooms inside focus mode.
  3. Ctrl+Tab leaves focus mode and switches tabs.
  4. Re-enter. Toggle the vertical sidebar in Settings, Save: still in focus mode. Leave: the sidebar is there.
  5. With agent access on, use an MCP client to spawn a session. You stay on your tab and the dot turns amber; the tooltip names the new tab.
  6. Resize the window narrow with a long tab title. The title trims before the dot and the window buttons.
  7. Close Ntilde in focus mode and reopen: still in focus mode. Right-click the strip and choose Exit Focus Mode.
  8. Light theme: the strip title and buttons are readable.
- [ ] **Step 4: Commit any fixes** from Steps 1–3 with their own tests, then hand over for the branch review.
