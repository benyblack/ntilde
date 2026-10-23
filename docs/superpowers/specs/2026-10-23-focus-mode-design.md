# Focus mode

Date: 2026-10-23
Status: designed (not implemented)
Branch: `feat/focus-mode` (worktree `.worktrees/focus-mode`), cut from main at 7f37dd8
Touches: `src/Ntilde.App/MainWindow.axaml(.cs)` (tab strip, title bar overlay, notices, key
handler, agent observe light), `src/Ntilde.App/Controls/TerminalPane.axaml(.cs)` (status bar,
scrollbar, pane overlays), `src/Ntilde.App/App.axaml` (window decorations theme),
`Shell/Shortcuts/ShortcutCatalog.cs`, `Shell/SessionManager.cs` + `src/Ntilde.Pty/SessionModels.cs`
(remembering the mode), and tests in `tests/Ntilde.App.Tests/`.

## Summary

Sometimes the owner wants Ntilde to be only a terminal: no tabs, no buttons, no status bars,
nothing around the text pulling at their attention. Focus mode is a switch on the existing
window. Turning it on hides the app chrome and leaves the tab you are in on screen, inside a
normal, movable, resizable window with one slim, quiet strip along the top. Every tab and pane
keeps running in the background. Turning it off puts everything back exactly as it was.

## Decisions (made with the owner, 2026-10-23)

- **A toggle on the current window**, not a separate kind of window. It is switched on mid-work,
  usually with several tabs and splits already open.
- **Nothing is closed or paused.** Other tabs and panes keep running, just off screen.
- **Normal window, not fullscreen.** It can still be moved, resized, minimised and maximised.
- **A slim, quiet strip** stays along the top (option C of three shown: "nothing at all" and
  "appears on hover" were rejected). It carries the active tab's name and the window buttons,
  in dim colours.
- **Focus mode hides chrome and does not change the layout.** A split tab that is not zoomed
  keeps all its panes visible; a zoomed tab shows its one pane. Pane zoom stays its own switch
  (Ctrl+Shift+Z) and the two combine. (This replaced an earlier reading in which focus mode
  zoomed to the active pane.)
- **Going to another tab means leaving focus mode.** The owner's way to reach another tab is to
  toggle back to normal.
- **Notices wait.** Notices that arrive on their own are held until focus mode is turned off.
  Anything that answers something the user just did still appears immediately.
- **Remembered across restarts.** Closing Ntilde in focus mode reopens it in focus mode.
- **The agent-access light moves into the strip** (proposed, accepted) so the security promise
  in `docs/mcp/security.md` holds without keeping other chrome.
- **An agent write keeps the light lit until the owner has looked** at that pane, exactly as
  write marks clear today, even though in focus mode that means leaving it to visit another tab.

## Current state (measured on main at 7f37dd8)

- **Pane zoom exists**: `EnterPaneZoom` / `ExitPaneZoom` / `TogglePaneZoomForCurrentTab` in
  `MainWindow.axaml.cs`, shortcut `toggle_pane_zoom` (Ctrl+Shift+Z), palette "Pane: Toggle Zoom".
  It swaps the tab's content for the single pane, keeps the root in `_paneZoomStateByTab`, and is
  saved per tab as `TabSession.ZoomedPaneId`. On a single-pane tab it is a deliberate no-op
  (`ReferenceEquals(root, pane)`). The owner confirmed it works on split tabs.
- **No fullscreen command and no way to hide the tab strip or title bar.** `App.axaml`'s
  `NtildeWindowDecorationsTheme` already hides the drawn title bar under `:fullscreen`, which is
  prior art for hiding decorations by state.
- **Window chrome** (`MainWindow.axaml`): the `Tabs` TabControl's header area
  (`PART_TabSidebar`, horizontal in the 36 px title band or a vertical sidebar, with
  `PART_TitleBandSpacer` in vertical mode); the `TitleBar` overlay with `TitleBarItemsHost`
  (the + button, user-pinned actions from `TitleBarCatalog`, the overflow badge, and the
  `AgentObserveIndicator` locked last); the drawn caption buttons (Windows/Linux, measured by
  `UpdateCaptionButtonReserve`; macOS uses native traffic lights on the left; X11 tiling
  compositors may show only close).
- **Window notices**: `RecordingToast` (a general notice surface despite the name, via
  `ShowRecordingToast`) and `UpdateToast` (`ShowUpdateToast`).
- **Pane chrome** (`TerminalPane.axaml`): `StatusBar` (Auto row, shown only for SSH forwards or
  when the pane is agent-actable; it holds the agent status segment), `TermScrollBar`,
  `FocusBorder` (already always hidden), `InactiveOverlay` (dims inactive panes),
  `AgentOutputToggle` ("MD" button that appears when output looks like markdown),
  `MuxSharedIndicator` ("shared with N" overlay), `AgentOutputPanelPresenter` and
  `RemoteFilesSidebarPresenter` side panels, plus prompted overlays (`ToastPanel` paste prompt,
  `SearchPanel`, `CommandAssistOverlayHost`).
- **Agent observe light**: `RefreshAgentObserveIndicator` lights the window dot for reads
  (`Watched` tier) of panes whose own segment is not on screen, decided by
  `IsPaneReadInvisibleWithoutWindowLight` (not actable / other tab / unassociated / hidden by
  zoom). Writes are reported by the pane's status-bar segment and the tab-header keyboard badge
  (`RefreshTabAgentAttention`), not by the window light.
- **Key handling**: the window's `KeyDown` handler is registered with
  `RoutingStrategies.Tunnel`, so window shortcuts see Ctrl+Shift chords before the terminal does.
  Ctrl+Shift+Enter is unused in `ShortcutCatalog`.
- **Single window**: `App.axaml.cs` creates one `MainWindow`; there is no multi-window case.

## Design

### 1. The mode

One boolean per window: `IsFocusMode`. Entering and leaving are idempotent and safe to call in
any state (no tabs being restored yet, a zoomed tab, vertical or horizontal tab orientation).
Entering never touches pane zoom, the tab layout, pane sizes or the selected tab; leaving
restores every piece of chrome to the state it would have had if focus mode had never been on.
Settings changes made while in focus mode (for example switching tab orientation in the
Settings window) apply to the normal layout and show when focus mode is left.

Toggling resizes the terminal area, because chrome comes and goes. That sends a PTY resize like
any window resize does, including to a shared mux session ("latest resize wins"). This is
expected and needs no special handling.

### 2. Turning it on and off

- Shortcut `toggle_focus_mode`, "View: Toggle Focus Mode", category "View",
  `ShortcutScope.App`, default **Ctrl+Shift+Enter** (adapted to Cmd+Shift+Enter on macOS by the
  existing `ShortcutPlatform` mapping). Rebindable in Settings like every catalog entry.
  *(Claude's call: free in the catalog, and Ctrl+Shift chords reliably reach the tunnelling
  window handler, whereas Alt chords are consumed by `TerminalView` and the owner's Left Alt is
  remapped to Ctrl machine-wide. Trade-off: a program using the kitty keyboard protocol will no
  longer receive Ctrl+Shift+Enter, the same as every other window chord.)*
- Command palette entry "View: Toggle Focus Mode", registered next to "Pane: Toggle Zoom" in
  `SetupCommandPalette` with the effective binding shown.
- **Right-click on the strip** opens a small context menu with "Exit Focus Mode".
  *(Claude's call: the mode survives restarts and hides every button, so a user who forgets the
  shortcut must still have a visible way out besides the palette.)*

### 3. The strip

A slim bar across the top of the window, present only in focus mode, in both tab orientations.

- **Contents**: the active tab's title (the same text its tab header shows, user title first),
  then on the far side the agent light (section 7), then the window buttons. On macOS the native
  traffic lights stay on the left and the title follows them; the light sits at the right edge.
  On platforms where the window manager shows fewer caption buttons, the strip shows what the
  decorations provide.
- **Height**: noticeably slimmer than today's 36 px title band, aiming at roughly one terminal
  line (about 20 to 24 px). The drawn caption buttons on Windows/Linux come from
  `NtildeWindowDecorationsTheme` and size to its title-bar height, so the builder must make them
  fit the slimmer strip (for example a focus-mode pseudoclass or class on the window that the
  decorations theme styles). If the decorations cannot be made that slim, the strip is as slim as
  they allow, and the builder reports the height reached.
- **Look**: background the same as the terminal background, so the strip reads as part of the
  terminal rather than a bar; title and buttons in low-contrast text. Colours come from the
  active theme so it works on light and dark themes alike. Hover states on the window buttons
  stay as today. Theme v2 (`feat/theme-v2-foundation`, not merged) will own these tokens once it
  lands; until then use what main provides.
- **Behaviour**: dragging the strip moves the window; double-clicking it maximises/restores
  (standard title-bar behaviour, *Claude's call*); right-clicking opens the menu in section 2.
  The window edges keep resizing the window.

### 4. What is hidden

Hidden while focus mode is on, restored on leaving:

- the whole tab header area: horizontal tab strip or vertical sidebar, its resize grip,
  overflow pill and badge, insert indicator, and the vertical-mode title band spacer;
- the title bar overlay's items: the + button and every pinned `TitleBarCatalog` action (the
  agent light moves into the strip, section 7);
- in every pane: `StatusBar`, `TermScrollBar`, `AgentOutputToggle`, and the
  `AgentOutputPanelPresenter` / `RemoteFilesSidebarPresenter` side panels if they were open.

Kept as today:

- the other panes of the selected tab, the dividers between them (still draggable), and the
  `InactiveOverlay` dimming that shows which pane is active;
- prompted overlays: find (`SearchPanel`), the paste prompt (`ToastPanel`), Command Assist, the
  command palette, and dialogs such as SSH password and host-key prompts;
- `MuxSharedIndicator`. *(Claude's call: it tells the user that another client is attached to
  this pane's session and may be watching or typing. That is the same kind of fact the agent
  light exists for, and it is a small in-pane badge.)*

If the user explicitly opens Remote Files while in focus mode (pane context menu > Remote Files,
or the palette's "SFTP: Toggle Remote Files"), it opens: that is the user asking for it.
*(Claude's call, by the same rule as notices.)* The Agent Output panel is not available in focus
mode: its only way in is the hover-revealed "MD" `AgentOutputToggle`, which is hidden there, and
there is no command for it. No command is added for it. *(Claude's call, after the builder check
found that no such command exists. Adding one is new scope that wasn't asked for.)*

### 5. Moving around

Inside the selected tab everything works without leaving focus mode: clicking or navigating to
another pane, splitting, equalising, resizing panes, toggling zoom, broadcast input, find,
recording, the command palette, Settings.

Anything the **user** does that would select another tab or act on the tab strip leaves focus
mode first and then does it, so no key ever silently does nothing. At least: next/previous tab
(Ctrl+Tab / Ctrl+Shift+Tab), new tab (Ctrl+Shift+T), new SSH connection or opening a
connection from the Connection Manager, open tab list (Ctrl+Shift+O), move tab
previous/next, toggle vertical tab sidebar (Ctrl+Shift+L), close tab (Ctrl+W), closing the last
pane of the tab (which closes the tab), attaching a mux session into a new tab, and loading a
workspace, template or bundle. The builder should route these through one "leave focus mode for
navigation" step rather than patching each call site separately, and check that list against
every place that changes `Tabs.SelectedItem` for a user action.

If something **other than the user** changes the selected tab (for example a tab closing because
its shell exited, or an agent spawning a session if that selects the new tab), focus mode stays
on and the strip shows whichever tab is now selected. The user did not ask to leave.

### 6. Notices

The rule (owner): notices that arrive on their own wait until focus mode is turned off; anything
that answers something the user just did appears immediately.

Held while in focus mode (unprompted):

- "Update ready" (`ShowUpdateToast` from the automatic check);
- "Command finished" long-command notices and OSC 9 notifications from programs;
- persistence/mux notices (`ShowPersistenceNotices`);
- "Credential storage unavailable" at startup.

Shown immediately (prompted):

- recording started / saved / failed: recordings only start from the user's own toggle
  (`TerminalPane` calls `Session.StartRecording`), so these answer an action. *(Correction made
  while writing this up: "Recording started" was used as an example of an unprompted notice
  during the conversation, but under the rule the owner agreed to it is prompted.)*
- results of a manual update check, update apply failures after the user chose to restart,
  "Unable to open recordings folder", and the mux update confirmation failure;
- the paste prompt, find, SSH prompts and dialogs.

Mechanism: `ShowRecordingToast` / `ShowUpdateToast` callers already know which notice they are
raising, so each caller states whether it is prompted. Unprompted ones raised in focus mode are
queued. On leaving focus mode the queue is shown through the existing surfaces (the update toast
on its own surface, the rest through the general notice surface, combined the way
`ShowPersistenceNotices` combines several). Auto-hide timers start when a notice is actually
shown, not when it was raised. If the same kind of notice is raised twice while held, only the
latest is kept.

### 7. The agent-access light

`docs/mcp/security.md` promises that agent activity is never silent and that the indicators
cannot be switched off except by turning agent access off. Focus mode hides both places that
report writes today (the pane status-bar segment and the tab-header badges) and the pane segment
that reports reads of the visible pane. So in focus mode:

- the `AgentObserveIndicator` is shown in the strip whenever it would be shown today (agent
  access enabled);
- every pane counts as one whose segment is not on screen: `IsPaneReadInvisibleWithoutWindowLight`
  gains a focus-mode input that makes it return true;
- the light also reports **writes** (`AgentAttentionTier.Wrote`) while in focus mode, from any
  pane in any tab, with an appearance distinct from the read state so a write is not mistaken
  for a read. Its click (open the activity journal) stays as today. Its tooltip names the tab
  (and pane, for a split tab) holding the unacknowledged write, since nothing else on screen
  says which one it was. *(Claude's call.)*
- write marks clear exactly as they do today. `AgentAttentionMachine` retires a write only once
  that pane is the focused pane and at least 10 s have passed. A write to a background tab
  therefore keeps the light in its write state until the user leaves focus mode and looks at
  that tab. A write to a non-current pane of a split tab keeps it until that pane is clicked.
  The owner chose this (2026-10-23): the light stays lit until they have looked. No fading, and
  no clearing from the activity journal. The attention machine is not changed.

The invariant to test: for every agent read or write that shows on some indicator outside focus
mode, the strip light shows it inside focus mode. `docs/mcp/security.md` gets a sentence saying
where the indicators live in focus mode.

### 8. Remembering it

Focus mode is window state saved by the shutdown `SaveSession` and applied by `RestoreSession`
after the tabs are rebuilt. It is not part of workspace, template or bundle snapshots: those are
layouts to rebuild elsewhere, the same reason `CaptureSession` strips mux ids for them. Opening
a workspace while in focus mode does not turn focus mode off or on by itself. (Loading a
workspace is a user tab change, so section 5 takes the user out of focus mode first.)

*(Claude's call: store it in the session file, as a window-level field on `NtildeSession`,
rather than in `TerminalSettings`. It is view state like zoom, and a settings field would also
have to be added to the Settings pane whitelist and the MCP SettingsTools drift guard.)*
The serializer is source-generated (`SessionSerializationContext`) and release builds are
NativeAOT, so the new field must go through that context. An older session file without the
field restores in normal mode.

## Goals

- One keystroke hides all app chrome around the current tab and brings it all back.
- Nothing running is disturbed: no shell restarts, no lost scrollback, no layout changes.
- Works the same in horizontal and vertical tab orientations, on Windows, macOS and Linux.

## Non-goals

- Fullscreen. (A separate fullscreen command could be added later; it is not part of this.)
- Switching tabs or seeing other tabs while in focus mode.
- Per-tab or per-pane focus mode.
- Changing Command Assist, the command palette, dialogs, OS-level behaviour (taskbar, dock) or
  the Settings window.
- Exposing focus mode to agents through MCP.

## Anti-goals (would make this a failure even if it technically works)

- An agent read or write that is visible nowhere while focus mode is on.
- Being stuck in focus mode with no visible way out, especially after a restart.
- A key that silently does nothing because focus mode swallowed it.
- Leaving focus mode with chrome in a different state than before (wrong orientation, a
  side panel lost, zoom changed, title bar items reordered, caption reserve wrong).
- Unprompted notices popping up over the terminal while in focus mode.

## Testing

Headless window tests are possible here (`TestMainWindowFactory` + `Show()` + `RunJobs()`), and
App.Tests must be run as the two CI lanes (see the repo's App.Tests protocol). At least:

- entering hides every element in section 4 and leaving restores each one, in horizontal and
  vertical orientation, with and without zoom, on single-pane and split tabs;
- focus mode does not change zoom, active pane, selected tab or pane sizes;
- each navigation in section 5 leaves focus mode and then performs its action; in-tab actions
  do not leave it;
- unprompted notices are held and shown on leaving; prompted ones show immediately; auto-hide
  starts on show;
- the agent light: reads and writes of the visible pane and of background tabs light the strip
  in focus mode (use the pure `IsPaneReadInvisibleWithoutWindowLight` / indicator-state methods,
  since `AgentHostService` must not be started in tests);
- save/restore round trip keeps focus mode; workspace capture does not include it; an old
  session file restores in normal mode;
- the shortcut catalog entry exists, and the palette lists it with the effective binding.

Manual check on Windows (the owner prefers manual steps for GUI runtime checks): toggle on a
split tab, drag the window by the strip, resize, minimise/maximise/close from the strip,
right-click > Exit Focus Mode, restart in focus mode.

## Left to the builder

- Exact strip height, padding and colours within section 3's limits.
- How the decorations theme is made to fit the slimmer strip.
- How the "leave focus mode for navigation" step is wired, and the full list of call sites.
- The visual difference between the light's read and write states.
- Whether held notices are queued in `MainWindow` or in a small helper type.
