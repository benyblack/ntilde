# The terminal

How the terminal itself behaves, and the tools for saving and inspecting what it shows.

## Cursor, bell and scrolling

These have palette commands; their settings are in `settings.json`
([Settings with no page](settings.md#settings-with-no-page)).

- **Cursor shape:** *Cursor: Block*, *Cursor: Beam* or *Cursor: Underline* (the default).
- **Cursor blink:** *Cursor: Toggle Blink*. Programs such as `vim` or `yazi` may set their own
  cursor shape and blinking.
- **Bell:** *Bell: Toggle Audio* and *Bell: Toggle Visual Flash*, both on by default. A bell in
  another tab also marks that tab ([Indicators](tabs-and-panes.md#indicators)).
- **Smooth scrolling:** *Scroll: Toggle Smooth*, on by default.
- **Scrollback:** 10000 lines per pane by default (Settings → Terminal → *Scrollback limit*).
  Scroll it with the mouse wheel; `Shift+PageUp` / `Shift+PageDown` go to the shell.

## Copy, paste and links

See [Mouse](shortcuts.md#mouse) for selecting, links, dropping files and pasting images.

Programs can **set your clipboard** with OSC 52, which is how `tmux` and `vim` copy over SSH. Turn
that off with `AllowOsc52ClipboardWrite`. Programs can never read your clipboard.

## Notifications and progress

- **Long commands.** With *Long command notifications* on (Settings → General), a command that ran
  for 30 seconds or more shows a toast when it finishes, if you are not looking at its pane. Needs
  [shell integration](shell-integration.md).
- **Program notifications.** With *Program notifications (OSC 9)* on, a program can show a toast,
  for example an AI CLI saying a long task finished. It is not shown while you are looking at the
  pane.
- **Progress.** A program that reports progress with OSC 9;4 shows it on its tab and, on Windows,
  on the taskbar button (*Progress reporting*, on by default).

## Inline images

Ntilde shows images that programs draw inline:

| Protocol | Programs | Windows | macOS, Linux |
|---|---|---|---|
| Sixel | `lsix`, `img2sixel` (libsixel), `gnuplot` | Supported; ConPTY may filter it, in which case programs that can use Ntilde's OSC 1339 tunnel still work | Supported |
| iTerm2 inline images (OSC 1337) | `imgcat` | Supported | Supported |
| Kitty graphics protocol | `kitty +kitten icat`, `chafa`, `timg`, `yazi` | Supported (`AllowNativeKittyGraphics`, on by default) | Supported |

Images are not shown in persistent panes ([What runs](persistent-sessions.md#what-runs)): turn
persistent sessions off, or open a profile that does not use them, to view them.

## Fonts, ligatures and text

See [Themes and fonts](themes-and-fonts.md).

## Snapshots

Save what a pane shows now, from the palette:

- *Pane: Export Snapshot (Plain Text)*: the text only.
- *Pane: Export Snapshot (ANSI)*: the text with its colors and styles, to `cat` in a terminal later.
- *Pane: Export Snapshot (PNG)*: a picture.

## Recording and replay

A recording captures everything a pane's program prints, with timing, so it can be played back
exactly.

- **Start and stop:** `Ctrl+Shift+R` (`Cmd+Shift+R` on macOS), the title bar's **Record Session**, or
  *Toggle Recording*. Recordings are saved as `ntilde_rec_<date>_<time>_<id>.rec` in the
  `recordings` folder of Ntilde's data folder.
- **Find them:** *Open Recordings Folder*.
- **Play one:** *Open Recording...* opens a `.rec` file, or an asciinema `.cast`, in a replay
  window.
- **Without a window:** `ntilde --replay <file>` prints the final screen ([Command line](cli.md#ntilde---replay)).

New recordings hold the program's output and window resizes, not what you type, including passwords
entered without echo. They can still contain anything shown on screen, including passwords a program
echoed. Older recordings may also contain raw typed input: check any recording before sharing it.

Recordings from NovaTerminal play unchanged.

## Diagnostic tools

- *Pane: Toggle Render HUD* shows frame time, redrawn rows and cells, draw calls and glyph cache hits
  over the pane. Use it when drawing feels slow, and include a screenshot in a bug report.
- *Debug: Box Drawing Test Screen* draws a box-drawing test pattern in the pane, to check line
  joins and gaps in your font.
- `ntilde --vt-report` lists the escape sequences Ntilde supports ([Command line](cli.md#ntilde---vt-report)).
