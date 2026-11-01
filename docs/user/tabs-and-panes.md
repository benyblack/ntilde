# Tabs, panes and workspaces

Shortcuts on this page are the Windows and Linux defaults; on macOS most use `Cmd` instead of
`Ctrl` ([Keyboard shortcuts](shortcuts.md#macos)).

## Tabs

| Action | Shortcut | Palette |
|---|---|---|
| New tab (default profile) | `Ctrl+Shift+T` (`Cmd+T` on macOS) | New Tab, or *New Tab: profile* for another profile |
| Close tab | `Ctrl+W` | Close Tab |
| Next / previous tab | `Ctrl+Tab` / `Ctrl+Shift+Tab` | Tab: Next (MRU) / Tab: Previous (MRU) |
| Move tab left / right | `Ctrl+Shift+PageUp` / `Ctrl+Shift+PageDown` | Tab: Move Previous / Tab: Move Next |
| List all tabs | `Ctrl+Shift+O` | Tab: Open Tab List |
| Jump to a tab | | *Switch Tab: title* |
| Vertical sidebar | `Ctrl+Shift+L` | Tabs: Toggle Vertical Tab Sidebar |

**Switching** with `Ctrl+Tab` goes in most-recently-used order, not left to right, so it bounces
between the two or three tabs you are working in.

**Reordering.** Drag a tab along the strip: the dragged tab dims and a marker shows where it will
land, and dragging near either end scrolls the strip. `Escape` cancels a drag.
`Ctrl+Shift+PageUp` / `Ctrl+Shift+PageDown` move the current tab one place. Both work in either
orientation.

**Many tabs.** Tabs that do not fit are reached from the tab list: the **Tab List** button in the
title bar, the overflow pill at the bottom of the vertical sidebar, or `Ctrl+Shift+O`. Picking a
tab there switches to it.

**Tab actions.** Right-click a tab, or use the palette:

- **Rename** (*Tab: Rename Current*) sets a title of your own, which wins over the title the shell
  sets. Without one, a tab shows the title the shell or program reports, or its folder. Long titles
  are shortened, and tabs with the same title get a hint to tell them apart.
- **Copy Title** (*Tab: Copy Current Title*).
- **Pin** (*Tab: Toggle Pin*) and **Protect** (*Tab: Toggle Protect*). A protected tab is guarded
  against being closed by accident.
- **Close Others** (*Tab: Close Others*) closes every other tab except pinned and protected ones.

Middle-click a tab to close it.

### Indicators

The horizontal tab strip shows:

| Marker | Meaning |
|---|---|
| `•` | Output arrived while you were in another tab |
| `🔔` | A bell rang, or the tab wants attention |
| `✓` / `✖code` | How the last command or the shell exited |
| `📌` | Pinned |
| `🔒` | Protected |
| `⧉` | The tab's shell is also open in another window ([Several windows](persistent-sessions.md#several-windows-and-ntilde-mux-attach)) |

A progress bar on the tab shows progress a program reports (OSC 9;4), when *Progress reporting* is
on in Settings → General. The bell and activity markers clear when you open the tab.

### Vertical tab sidebar

`Ctrl+Shift+L`, *Tabs: Toggle Vertical Tab Sidebar*, or Settings → Tabs & title bar → **Tab strip
orientation** swaps the tab strip for a sidebar on the left. Each row shows the title, a status
dot, marker chips, and the tab's latest line of output as a live preview: what you want when tabs
run long jobs or agents.

The **status dot** shows one state, the first that applies:

| Dot | Meaning |
|---|---|
| Amber | A bell rang, or the tab wants attention |
| Amber (agent) | An agent typed into this tab and you have not looked yet |
| Blue | Output is streaming, or a command is still running |
| Blue (agent) | An agent is reading this tab (only with *Tab indicator* set to *All activity*, Settings → Agent Access) |
| *(none)* | Idle |

The status is worked out from what the pane does, about once a second, and goes back to idle on
its own.

**Marker chips** follow the title and can stack: bell, activity, agent-wrote and agent-watched are
tracked separately, except that a bell replaces plain activity. They clear when you open the tab.

Drag the sidebar's right edge to resize it; the width is remembered. When there are more rows than
fit, a pill at the bottom shows how many are hidden and opens a list of them.

## Panes

A tab can be split into panes, each with its own shell.

| Action | Shortcut | Palette |
|---|---|---|
| Split side by side | `Ctrl+Shift+D` | Split Vertical |
| Split one above the other | `Ctrl+Shift+E` | Split Horizontal |
| Close the current pane | `Ctrl+Shift+W` | Close Pane |
| Move between panes | `Alt+Left` / `Right` / `Up` / `Down` | Focus Pane Left / Right / Up / Down |
| Make all panes the same size | `Ctrl+Shift+G` | Equalize Panes |
| Fill the tab with the current pane, and back | `Ctrl+Shift+Z` | Pane: Toggle Zoom |
| Hide everything but the current tab, and back | `Ctrl+Shift+Enter` | View: Toggle Focus Mode |
| Type into every pane of the tab at once | `Ctrl+Shift+B` | Pane: Toggle Broadcast Input (Tab) |
| Search the pane's screen and scrollback | `Ctrl+F` or `Ctrl+Shift+F` | Find in Terminal |
| Reconnect a pane whose session ended | `Enter` in the pane | Pane: Reconnect |

Drag the splitter between two panes to resize them; double-click it to make that split even.

**Search** opens a box over the pane. Use its ▲ / ▼ buttons to go to the previous or next match and
✕ to close it. A match can span terminal soft wraps and still counts as one result; explicit
newlines remain search boundaries. Every visible part of the current match is highlighted.
Old restored snapshots without wrap-padding metadata retain their original spacing.

**Closing a pane with a program running** asks first by default. `PaneClosePolicy` in
`settings.json` changes that ([Settings with no page](settings.md#settings-with-no-page)). When a
shell exits by itself, its pane closes if it exited with status 0 and stays open with the exit
status otherwise (`ShellExitPolicy`).

**Persistent sessions.** Shells normally keep running when the window closes, and come back when
Ntilde starts again ([Persistent sessions](persistent-sessions.md)).

**Focus mode** (`Ctrl+Shift+Enter`) hides the tabs, title bar buttons, status bars and side
panels, leaving only the current tab and a slim strip with the window buttons. Every tab keeps
running. Press it again, or right-click the strip and choose Exit Focus Mode, to bring everything
back. Focus mode is remembered across restarts, switching or opening tabs leaves it, and notices
that arrive on their own wait until you leave it.

## Workspaces

A workspace saves the window's layout: its tabs and their panes, the current tab and pane, zoom,
broadcast input and each tab's title and markers.

- **Workspace: Save Current** saves it under a name.
- **Workspace: Load...**, or *Workspace: Load name*, opens it.

**Loading a workspace replaces every tab in the window**: the open tabs close first. A workspace
saves a layout, not running programs, so its panes start new shells. Each pane opens the profile it
was saved with; when that profile no longer exists, it runs the saved command, or your default
shell.

Workspaces are kept in the `workspaces` folder of Ntilde's data folder, one JSON file per
workspace.

### Workspace templates

A template is a reusable layout, for a multi-tab setup you open again and again.

- **Workspace Template: Save Current** saves the current layout as a template.
- **Workspace Template: Apply...**, or *Workspace Template: Apply name*, opens it, replacing the
  window's tabs as loading a workspace does.

Templates are kept in the `workspace_templates` folder.

**Per-profile rules** open a template whenever you open a new tab of a profile:

1. Switch to a tab of that profile.
2. Run **Tab Rule: Set Template for Current Profile...** and pick the template.
3. New tabs of that profile now open with the template's first tab, panes and all, instead of a
   single pane.

**Tab Rule: Clear Template for Current Profile** removes the rule. If a rule seems to do nothing,
check that its template still exists, and set the rule again.

### Workspace bundles

A bundle is a workspace in a single portable `.ntildews.json` file, to hand to someone else or move
to another computer.

- **Workspace: Export Bundle...** exports a saved workspace.
- **Workspace: Export Current Session Bundle...** exports the tabs you have open now.
- **Workspace: Import Bundle...** checks a bundle and saves it as a workspace.
- **Workspace: Open Bundle...** checks a bundle and opens it without saving it.

- Bundles carry a SHA-256 hash of their contents, and one that does not match is refused: export
  it again from the original.
- When a bundle would run commands of its own (panes whose profile you do not have), Ntilde lists
  them and asks before running them.
- A bundle names profiles but does not contain them. SSH profiles in particular are not included:
  on another computer, bring them over with a [backup](backup.md) (*Connections*).
- Bundles from NovaTerminal (`.novaws.json`) open too.

### Workspace policy

On managed computers an administrator can restrict bundles with `policy/workspace_policy.json` in
Ntilde's data folder ([File locations](troubleshooting.md#file-locations)):

| Field | Type | Effect |
|---|---|---|
| `AllowWorkspaceBundleExport` | bool | `false` removes the export commands |
| `AllowWorkspaceBundleImport` | bool | `false` removes the import and open commands |
| `MaxTabsPerWorkspace` | int | Refuses bundles with more tabs; `0` means no limit |
| `RequireSsoForWorkspaceBundles` | bool | Single sign-on is not implemented yet, so `true` refuses every bundle operation |

Ntilde reads the file every few seconds. A refused operation is noted only in `logs/debug.log`.

Workspace, template and bundle operations, whether they succeed or fail, are logged to
`logs/workspace_audit.log`.
