# Ntilde Theme System

A theme is one JSON file of colors. It colors the terminal and, through derived UI tokens, every
other surface: Settings, the tab strip, dialogs, pane overlays. A theme switch applies live,
everywhere, with no restart. The design and its history are in
`docs/superpowers/specs/2026-09-23-theme-system-design.md`.

## Theme files

Themes live in the `themes/` folder of the app data directory (`%LOCALAPPDATA%\ntilde` on
Windows, `~/Library/Application Support/ntilde` on macOS, `~/.local/share/ntilde` on Linux).
`ThemeManager` loads every `*.json` there. The built-in themes ship next to the app
(`src/Ntilde.App/themes/`) and are copied into the folder whenever the shipped file is newer, so an
upgrade refreshes them and overwrites local edits to a built-in file. To customize one, save it
under a new name.

### Required colors

| Field | Notes |
|---|---|
| `Name` | Display name, e.g. "Dracula". |
| `Foreground`, `Background`, `CursorColor` | Default text, terminal background, cursor. |
| `Black` … `White` | ANSI 0-7: `Black`, `Red`, `Green`, `Yellow`, `Blue`, `Magenta`, `Cyan`, `White`. |
| `BrightBlack` … `BrightWhite` | ANSI 8-15, the same names with `Bright`. |

### Optional colors

Omit any of these (or set it to `null`) and Ntilde derives it. Derived colors are adjusted until
they read; an explicit value is used exactly as given.

| Field | What it colors | Derived from |
|---|---|---|
| `SelectionBackground` | The selection. Selected text is drawn on top of it. | The accent at alpha 0x66. |
| `SelectionForeground` | Selected text. | Each cell keeps its own color, unless the theme's text would not read on the selection: then the foreground, adjusted until it does. |
| `CursorText` | The glyph under a block cursor. | `Background`. |
| `SearchMatch` | Search matches. | The warning color at alpha 0x64. |
| `SearchMatchActive` | The current search match. | Between warning and error, at alpha 0x96. |
| `Accent` | Buttons, links, the selected tab, focus. | A softened `Blue`. |
| `Success`, `Warning`, `Error`, `Info` | Status text and indicators. | Softened `Green`, `Yellow`, `Red`, `Cyan`. |

A malformed optional value is treated as omitted (and logged); it never stops the theme loading.
Colors are `#RGB`, `#RRGGBB` or `#AARRGGBB` (alpha first).

### Example (Dracula.json)

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
  "BrightWhite": "#ffffff",
  "SelectionBackground": "#44475a"
}
```

## Importing

Settings → Appearance → **Import…** reads:

- **Ntilde** theme JSON (the format above).
- **Windows Terminal** schemes: a whole `settings.json`, an array of schemes, or one scheme.
  `selectionBackground` is kept.
- **iTerm2** `.itermcolors`. `Selection Color`, `Selected Text Color` and `Cursor Text Color` are
  kept.
- **Alacritty** `.toml`: `colors.selection`, `colors.cursor.text` and the search colors, in
  sections or inline tables. `CellForeground`/`CellBackground` mean derived.

## The theme editor

Settings → Appearance → **Edit** (or **+ New**) opens the editor in place of the page.

- Foreground, background, cursor and the 16 ANSI colors; click a swatch to change it.
- **Advanced colors** lists the optional colors. A derived one shows its computed color dimmed;
  click it to override. An override shows **Reset**, which goes back to derived. Derived values
  follow your other edits.
- The preview shows the draft: text, selected text, search matches, a block cursor over a glyph,
  an accent button, the status colors and the tab dots.
- **Save theme** writes the file at once; the Settings window's Cancel does not undo it. **‹
  Appearance** leaves without saving. The Default theme cannot be overwritten: save it under a new
  name.

## How colors reach the UI (for contributors)

`ThemeTokens.Derive` (`src/Ntilde.App/Shell/Theming/`) turns a `TerminalTheme` into about 60 named
UI colors (surfaces, text, fills, accent, status, tab states), contrast-fixed against each other.
`ThemeService` installs them once into `Application.Resources` and recolors them in place on every
theme change, together with Fluent's stock-control palette and the light/dark variant.
`TerminalRenderColors` derives the renderer's own colors (selection, search, cursor text) through
the same tokens; the draw operation reads them per frame from the pane's theme.

**Adding UI that follows the theme:**

- XAML: `{DynamicResource NtPanel}` (keys: `ThemeKeys.cs`). Never a hex or named color.
- C#: `this.FindResource(ThemeKeys.Panel)`, or bind with
  `control.Bind(Border.BackgroundProperty, control.GetResourceObservable(ThemeKeys.Panel))`.
  Never `Brushes.White`, `Color.Parse("#…")` or a `"#…"` string.
- No local copies: declaring `x:Key="NtPanel"` in a window shadows the app token.
- **Inside a terminal pane, bind** (`GetResourceObservable`) rather than hold a brush read once. A
  pane whose profile names its own theme carries its own token scope, so its overlays match its
  terminal, and only a binding follows that scope.

**Guards (gating):**

- `ThemeTokenKeyGuardTests`: every `Nt*` key used must exist in `ThemeKeys`.
- `ThemeLiteralGuardTests`: no hardcoded colors outside `Shell/Theming`. The baseline is empty;
  colors fixed on purpose (caption close-button red, connection swatches, debug drawing) are in
  `ThemeLiteralPolicy.Permanent`, each with a reason.
- `ThemeContrastTests`: every shipped theme and the stress themes yield readable chrome, selected
  text and distinguishable search fills. When it fails, fix the derivation, never the threshold.
- The MCP drift tests keep `ntilde.validate_theme_json` in step with `TerminalTheme`.

## Key files

- `src/Ntilde.VT/TerminalTheme.cs`: the theme model.
- `src/Ntilde.App/Shell/ThemeManager.cs`: loading, saving and importing.
- `src/Ntilde.App/Shell/ThemeImporters/`: Windows Terminal, iTerm2, Alacritty and native importers.
- `src/Ntilde.App/Shell/TermColorJsonConverter.cs`, `OptionalTermColorJsonConverter.cs`: JSON.
- `src/Ntilde.App/Shell/Theming/`: `ThemeKeys`, `ThemeTokens`, `ThemeService`, `ThemeColorMath`,
  `TerminalRenderColors`.
- `src/Ntilde.App/Settings/Pages/AppearancePage.axaml`: theme selection and the editor.
- `src/Ntilde.App/themes/*.json`: the built-in themes.
