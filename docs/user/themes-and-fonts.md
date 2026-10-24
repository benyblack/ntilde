# Themes and fonts

## Choosing a theme

Fourteen themes ship with Ntilde: Default, Catppuccin Mocha, Cobalt2, Dracula, GitHub Dark, GitHub
Light, Gruvbox Dark, Monokai, Nord, One Half Dark, One Half Light, Solarized Dark, Solarized Light
and Tokyo Night.

Pick one in Settings → Appearance → **Theme**, which previews it as you choose, or run
*Theme: name* from the command palette, which applies it at once. A theme colors the window too:
title bar, tabs, sidebar, Settings and dialogs take their colors from the theme's background,
foreground, and its blue, green, yellow, red and cyan, adjusted so text stays readable.

A local profile can use a theme of its own: Settings → Shells & Profiles → *Visual overrides* →
Theme ([Settings](settings.md#shells--profiles)).

## Importing a theme

Settings → Appearance → **Import…** reads:

| Format | File | Notes |
|---|---|---|
| Ntilde | `.json` | [The format](#the-theme-file) below |
| Windows Terminal | `.json` | A whole `settings.json` (every scheme in its `schemes` list), a list of schemes, or one scheme |
| iTerm2 | `.itermcolors` | Named after the file |
| Alacritty | `.toml` | Named after the file, with ` (Alacritty)` added; colors written `#rrggbb` or `rrggbb`, not `0x…` |

The imported theme is selected at once. A file that cannot be read is skipped without a message;
`logs/debug.log` says why. Importing a theme with the name of one you already have replaces it.

## Making or editing a theme

**+ New** starts a theme named *New Theme* from the default colors; **Edit** changes the selected
one. The editor has
its name, foreground, background, cursor and the 16 ANSI colors. **Save theme** writes it to disk
immediately: Settings' **Cancel** does not undo it. The built-in *Default* cannot be edited; save a
copy under another name.

Edit built-in themes under a new name too: Ntilde may replace its own theme files when it updates.

## The theme file

Themes are JSON files in the `themes` folder of Ntilde's data folder
([File locations](troubleshooting.md#file-locations)), one per theme. For example, the built-in
Dracula:

```json
{
  "Name": "Dracula",
  "Foreground": "#f8f8f2",
  "Background": "#282a36",
  "CursorColor": "#f8f8f2",
  "Black": "#21222c",
  "Red": "#ff5555",
  "Green": "#50fa7b",
  "Yellow": "#f1fa8c",
  "Blue": "#bd93f9",
  "Magenta": "#ff79c6",
  "Cyan": "#8be9fd",
  "White": "#f8f8f2",
  "BrightBlack": "#6272a4",
  "BrightRed": "#ff6e6e",
  "BrightGreen": "#69ff94",
  "BrightYellow": "#ffffa5",
  "BrightBlue": "#d6acff",
  "BrightMagenta": "#ff92df",
  "BrightCyan": "#a4ffff",
  "BrightWhite": "#ffffff"
}
```

- **Keys** are exactly as above, capitalized. A key spelled differently (`name`, `background`) is
  ignored.
- **`Name`** is what Settings and the palette show, and identifies the theme: the file's own name
  does not matter. Two files with the same `Name` are one theme.
- **Colors** are `#RRGGBB`. Write every one: a color you leave out gets a plain xterm default, and a
  value Ntilde cannot read becomes transparent, without an error.
- `Foreground`, `Background` and `CursorColor` are the default text, background and cursor; the
  other sixteen are the ANSI palette programs use.

Ntilde reads the folder when it starts and each time Settings opens, so a file you copy in shows up
in Settings → Appearance next time you open it. It does not watch the files: after editing one
by hand, open Settings and choose the theme again.

An agent connected to Ntilde can check a theme file with `ntilde_validate_theme_json`
([Agents](agents.md)).

## Fonts

Settings → Terminal → **Font family** lists your installed fonts and the ones Ntilde ships, so a new
install looks right with nothing installed:

- **JetBrains Mono NL**, the default;
- **Cascadia Mono PL**;
- **Symbols Nerd Font Mono**, which is not offered in the list but is used for any icon your font
  lacks, so prompt themes that use Nerd Font icons (Powerlevel10k, Starship, Oh My Posh) work
  whatever font you pick.

**Font size** is 14 by default; `Ctrl+=` and `Ctrl+-` (`Cmd` on macOS) change it and save it.

- **Programming ligatures** (off by default) draw `=>`, `!=`, `>=` and the like as single glyphs,
  in fonts that have them. *JetBrains Mono NL* is the no-ligatures cut: pick a font with ligatures,
  such as JetBrains Mono or Fira Code, to see them.
- **Complex shaping** (on) shapes text in non-Latin scripts and emoji properly.
- Box-drawing and block characters are drawn as lines and blocks rather than taken from the font,
  so borders join without gaps in any font. *Debug: Box Drawing Test Screen* shows them.

A local profile can use its own font, size and ligatures: Settings → Shells & Profiles → *Visual
overrides*.

## Window

Settings → Appearance also has:

- **Interface scale**, 80% to 200%: everything but the terminal's own font size;
- **Window opacity** and **Blur effect** (*Acrylic* or *Mica* where the system supports them);
- a **Background image** with its opacity and stretch mode, for every theme.

See [Settings](settings.md#appearance).
