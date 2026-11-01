# Settings reference

Open Settings with `Ctrl+,` (`Cmd+,` on macOS) or **Settings** in the command palette. The pages
sit in a sidebar, grouped:

| Group | Pages |
|---|---|
| General | [General](#general) |
| Interface | [Appearance](#appearance), [Terminal](#terminal), [Tabs & title bar](#tabs--title-bar), [Shells & Profiles](#shells--profiles), [Shortcuts](#shortcuts) |
| Assistant | [Command Assist](#command-assist), [Agent Access](#agent-access) |
| Connection | [SSH](#ssh) |
| Data | [Backup & Restore](#backup--restore) |

Type in the search box at the top of the sidebar (or press `Ctrl+F`, `Cmd+F` on macOS) to find a
setting by name or description; `Up` / `Down` pick a result and `Enter` jumps to it. **Save**
(`Ctrl+S` / `Cmd+S`) applies everything at once; **Cancel** throws the changes away. Font, font size,
theme, opacity, blur, background image and interface scale preview in the main window while you
change them, and Cancel undoes the preview.

Most settings apply to the open window as soon as you save. The exceptions are noted in each table:
a few apply only to tabs or connections opened afterwards, and a few only at the next launch.

Each table gives the setting's key in `settings.json` ([The settings file](#the-settings-file)).

## General

| Setting | Key | Default | What it does |
|---|---|---|---|
| Automatic update checks | `AutomaticUpdateChecks` | on | Checks GitHub for a newer version about 10 seconds after launch and downloads it in the background. Nothing restarts until you say so. Installed builds only, not the portable zip ([Updates](install.md#updates)). Takes effect at the next launch. |
| Long command notifications | `LongCommandNotificationsEnabled` | off | Shows a toast when a command that ran for at least 30 seconds finishes in a pane you are not looking at. Needs shell integration. |
| Program notifications (OSC 9) | `Osc9NotificationsEnabled` | off | Shows a toast when a program asks for one with OSC 9 (an AI CLI saying a long task finished, say), unless you are looking at that pane. |
| Progress reporting (OSC 9;4) | `Osc9ProgressReportingEnabled` | on | Shows progress a program reports with OSC 9;4 on its tab, and on the Windows taskbar button. |
| Keep shells running when the window closes | `SessionPersistence` | *Keep running* (`"KeepOnClose"`) | *Keep running* or *Off* (`"Off"`). Runs shells in the background multiplexer so they survive closing the window, a crash or an update ([Persistent sessions](persistent-sessions.md)). Applies to new tabs. |

Under the last setting, **Quit and close all shells…** ends every shell the multiplexer keeps and
quits ([Closing the window](persistent-sessions.md#closing-the-window)).

## Appearance

| Setting | Key | Default | What it does |
|---|---|---|---|
| Theme | `ThemeName` | `Default` | Colors for the terminal and for the window. **Edit**, **Import…** and **+ New** sit beside it ([Themes and fonts](themes-and-fonts.md)). |
| Minimum contrast | `MinimumContrast` | Off (1) | Off, or 1.5:1 to 7:1 (the file accepts up to 21). Terminal text closer to its background than this is lightened or darkened, keeping its hue ([Themes and fonts](themes-and-fonts.md#minimum-contrast)). |
| Window opacity | `WindowOpacity` | 1.0 | 0.2 to 1.0. Applies while the window is focused. |
| Interface scale | `UiScale` | 100% | 80% to 200%. Zooms tabs, sidebars, dialogs and Settings; the terminal grows with it, on top of its font size. The Settings window itself uses the new scale the next time it opens. |
| Blur effect | `BlurEffect` | `Acrylic` | `Acrylic`, `Mica` or `None`, where the operating system supports them. |
| Background image | `BackgroundImagePath` | none | A PNG, JPG, BMP or WebP drawn behind the terminal. One image for every theme. |
| Image opacity | `BackgroundImageOpacity` | 0.5 | 0 to 1. |
| Stretch mode | `BackgroundImageStretch` | Fill (crop) | *Don't stretch* (`None`), *Stretch* (`Fill`), *Fit* (`Uniform`) or *Fill (crop)* (`UniformToFill`). |

## Terminal

| Setting | Key | Default | What it does |
|---|---|---|---|
| Font family | `FontFamily` | `JetBrains Mono NL` | The terminal's monospaced font. The list holds your installed fonts and the ones bundled with Ntilde ([Fonts](themes-and-fonts.md#fonts)). |
| Font size | `FontSize` | 14 | 6 to 72. `Ctrl+=` / `Ctrl+-` change it too, and save it. |
| Programming ligatures | `EnableLigatures` | off | Draws `=>`, `!=`, `>=`, `\|>` and the like as single glyphs, when the font has them. |
| Complex shaping (HarfBuzz) | `EnableComplexShaping` | on | Shapes runs of text, for non-Latin scripts and emoji. |
| Scrollback limit | `MaxHistory` | 10000 | Lines of scrollback kept per pane, 100 to 100000. |

## Tabs & title bar

| Setting | Key | Default | What it does |
|---|---|---|---|
| Tab strip orientation | `TabStripOrientation` | `Horizontal` | `Horizontal`, or `Vertical`: a left sidebar with status and a preview of each tab's last output ([Vertical tab sidebar](tabs-and-panes.md#vertical-tab-sidebar)). `Ctrl+Shift+L` toggles it. |
| Title bar actions | `TitleBarItems`, `TitleBarOrder` | see below | For each action: **Bar** (a button in the title bar), **Menu** (in the title bar's … menu) or **Hide**. ▲ / ▼ reorder the buttons in the bar. The bar holds up to 8 actions. A live preview shows the result. |

The default title bar has **New Tab** (always in the bar), **Tab List**, **Connections** and
**Settings**. The … menu has Record Session, Command Palette, Find in Terminal, Split Vertical,
Split Horizontal, Remote Files and Transfers. **Agent Activity** is hidden. A hidden action is still
reachable by its shortcut and the command palette. Right-click the title bar → **Customize Title
Bar...** opens this page.

## Shells & Profiles

A *profile* is a shell Ntilde can open in a tab: its program, arguments and starting folder. This
page edits local profiles; SSH connections live in the Connection Manager ([SSH](ssh.md)).

| Field | Key in the profile | What it does |
|---|---|---|
| Profile name | `Name` | The name in menus and the palette (*New Tab: name*). |
| Executable path | `Command` | The program to run: `pwsh.exe`, `/bin/zsh`, `wsl.exe`, … |
| Arguments | `Arguments` | Its command-line arguments. |
| Starting directory | `StartingDirectory` | The folder it starts in. Empty leaves it to the system's default. |
| **Set as default** | `DefaultProfileId` (top level) | The profile **New Tab** opens. |
| Group | `Group` | Groups profiles in lists. Default `General`. |
| Tags | `Tags` | Comma-separated labels. |
| Visual overrides (experimental): Font, Size, Theme, Ligatures | `FontFamily`, `FontSize`, `ThemeName`, `EnableLigatures` | Replace the global setting for this profile's tabs. Empty uses the global one. |

**+ Add** creates a profile running your default shell; **Delete** removes one (the last one cannot
be deleted). **Import from: Windows Terminal** adds the local profiles of your Windows Terminal
settings whose names you do not already have.

Profiles have no field for environment variables: set them in your shell's startup files.

**Your default shell.** On Windows, Ntilde picks the first of `pwsh.exe`, `powershell.exe` and
`cmd.exe` on your `PATH`. On macOS and Linux it uses `$SHELL`, then your login shell from
`/etc/passwd`, then the first of `/bin/zsh`, `/bin/bash` and `/bin/sh`. On macOS, zsh with no
arguments starts as a login shell (`-l`), so Homebrew's `PATH` is set up; give it arguments of your
own and they are used instead.

**First run.** Ntilde creates a profile for each shell it finds: *Command Prompt*, *PowerShell* and
*Windows PowerShell* on Windows; *Default Shell*, *Bash*, *Zsh* and *Shell* on macOS and Linux. It
looks only on the first run, so add a shell you install later with **+ Add**.

Each local profile also gets a palette command, **New Tab: *name***. A profile whose program
belongs to another operating system (a `cmd.exe` profile restored on a Mac, say) is left out.

## Shortcuts

Lists every rebindable shortcut, with a recorder and a **Reset** button per row. See
[Keyboard shortcuts](shortcuts.md) for the defaults and [Rebinding](shortcuts.md#rebinding) for how
recording works. Stored in `Keybindings`, overrides only.

## Command Assist

| Setting | Key | Default | What it does |
|---|---|---|---|
| Command assistant | `CommandAssistEnabled` | **off** | Turns Command Assist on: suggestions, history search, fixes and snippets ([Command Assist](command-assist.md)). |
| Suggestion bubble while typing | `CommandAssistPassiveBubbleEnabled` | on | After two characters, shows the best match from your history above the prompt. Off keeps Command Assist silent until you open it with `Ctrl+Space` or `Ctrl+R`. |
| Remember commands | `CommandAssistHistoryEnabled` | on | Records the commands you run so they can be suggested and searched. Values matching the [supported secret patterns](../command-assist/CommandAssist.md#supported-secret-patterns) are redacted; this heuristic cannot detect every secret. **Clear history** deletes what is recorded. |
| Shell integration | `CommandAssistShellIntegrationEnabled` | on | Lets Ntilde set up the local shells it starts so it knows where the prompt is, what you typed and how each command exited ([Shell integration](shell-integration.md)). Applies to shells started afterwards. |
| Saved snippets | — | — | The commands you pinned with `Ctrl+Shift+S`, and any you add here. Kept in `command-assist/snippets.json`. |
| Remote shell integration | — | — | Pick *bash / zsh*, *fish* or *PowerShell*, then **Copy installer** (or **Copy plain snippet**) and paste it into a shell on the remote host ([Shell integration](shell-integration.md#on-a-remote-host)). |

## Agent Access

All off by default. See [Agents](agents.md).

| Setting | Key | Default | What it does |
|---|---|---|---|
| Agent access (observe) | `AgentAccessObserveEnabled` | off | Lets AI agents on this computer read terminal sessions: screen, scrollback and status. Off means there is no endpoint for agents at all. |
| Agent replay export | `AgentReplayExportEnabled` | off | Also lets an agent save a session's recent output as a replay file. Output and window resizes only, never what you type. Needs observe. |
| Agent access (act) | `AgentAccessActEnabled` | off | Also lets an agent type into, open and close sessions. SSH connections must each be allowed as well. Needs observe. |
| Tab indicator | `AgentIndicatorTabRollup` | Writes only | *Writes only* (`WritesOnly`) marks tabs an agent typed into; *All activity* (`All`) also marks tabs being read. |

## SSH

| Setting | Key | Default | What it does |
|---|---|---|---|
| Native SSH backend | `ExperimentalNativeSshEnabled` | on | Lets profiles set to the *Native* backend connect with Ntilde's built-in SSH client. While on, new SSH profiles use it. While off, a native profile refuses to connect, and new profiles use OpenSSH. Applies to new connections. |

Everything else about a connection is set per profile in the Connection Manager ([SSH](ssh.md)).

## Backup & Restore

Export, import and snapshot restore. None of it is stored in `settings.json`. See
[Backup and restore](backup.md).

## Settings with no page

These keys are read from `settings.json`. Some have palette commands.

| Key | Default | What it does |
|---|---|---|
| `CursorStyle` | `Underline` | `Block`, `Beam` (or `Bar`) or `Underline`. Palette: *Cursor: Block / Beam / Underline*. |
| `CursorBlink` | `true` | Palette: *Cursor: Toggle Blink*. Programs such as `vim` may set their own blinking. |
| `BellAudioEnabled` | `true` | Sound on bell. Palette: *Bell: Toggle Audio*. |
| `BellVisualEnabled` | `true` | Flash on bell. Palette: *Bell: Toggle Visual Flash*. |
| `SmoothScrolling` | `true` | Animated wheel scrolling. Palette: *Scroll: Toggle Smooth*. |
| `WheelLinesPerNotch` | `3` | Lines per wheel notch, up to 100. |
| `EnableLinkDetection` | `true` | Underline and open URLs found in the text ([Links](shortcuts.md#mouse)). |
| `EnableKittyKeyboardProtocol` | `true` | Lets programs ask for the kitty keyboard protocol. Turn it off if a program's keys come out wrong. |
| `AllowNativeKittyGraphics` | `true` | Kitty graphics protocol images ([Inline images](terminal.md#inline-images)). |
| `AllowOsc52ClipboardWrite` | `true` | Lets programs (`tmux`, `vim` over SSH) set your clipboard with OSC 52. Programs can never *read* the clipboard. |
| `PaneClosePolicy` | `Confirm` | Closing a pane with a running program: `Confirm` asks, `Graceful` sends `Ctrl+C` and `exit` first, `Force` closes. |
| `ShellExitPolicy` | `Graceful` | When a shell exits: `Graceful` closes the pane if it exited with 0 and keeps it open with the exit status otherwise; `Always` closes it; `Never` keeps it. SSH panes always stay open with their banner. |
| `VerticalTabStripWidth` | `220` | Width of the vertical tab sidebar, 140 to 600. Set by dragging its edge. |
| `QuakeModeEnabled` | `true` | Windows only: a system-wide key (`GlobalHotkey`) shows and hides the window. Takes effect at the next launch. |
| `GlobalHotkey` | `Alt+OemTilde` | That key, ``Alt+` ``: one or more of `Ctrl`, `Alt`, `Shift`, `Win`, then a key such as `A`, `D1`, `F11`, `Space` or `OemTilde`, joined with `+`; `F12` is reserved by Windows. A value Ntilde cannot read, or a key another program already holds, falls back to ``Alt+` ``, and `logs/debug.log` says so. Takes effect at the next launch. |
| `CommandAssistMaxHistoryEntries` | `5000` | How many commands Command Assist remembers. |
| `CommandAssistPowerShellIntegrationEnabled` | `true` | Shell integration for PowerShell shells. |
| `TabTemplateRules` | `[]` | Per-profile template rules ([Templates](tabs-and-panes.md#workspace-templates)). |

## The settings file

Settings are kept in `settings.json` in Ntilde's data folder:

| System | File |
|---|---|
| Windows | `%LOCALAPPDATA%\ntilde\settings.json` |
| macOS | `~/Library/Application Support/ntilde/settings.json` |
| Linux | `~/.local/share/ntilde/settings.json` (`$XDG_DATA_HOME/ntilde` when that is set) |

The environment variable `NTILDE_APPDATA_ROOT` moves the whole data folder ([File
locations](troubleshooting.md#file-locations)).

You can edit it by hand. Keys are the names in the tables above, in PascalCase; keys Ntilde does not
know are ignored, and a value it cannot read falls back to the default. Ntilde does not watch the
file: an edit takes effect the next time Ntilde starts. **Quit Ntilde before you edit it**, because
saving from Settings, changing the font size or running a palette toggle writes the whole file from
Ntilde's copy and would undo your edit.

Every save writes the file whole and keeps the previous version as `settings.json.bak`. If Ntilde
cannot read `settings.json` at startup, it keeps the broken file as `settings.json.corrupt`, and
starts from `settings.json.bak`, or from the defaults when that cannot be read either.

Inside a profile, `Type` is a number (`0` local, `1` SSH). A profile's password is never written to
the file.

To check a hand-edited file, an agent connected to Ntilde ([Agents](agents.md)) can run
`ntilde_validate_settings_json`, which checks types and ranges and warns about unknown keys.
