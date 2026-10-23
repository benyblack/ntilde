# Ntilde Theme System

Ntilde features a robust theme management system that separates visual appearance (color schemes) from functional settings. This allows users to switch between different color palettes without altering their core configuration.

## Overview

Themes are stored as JSON files and managed by the `ThemeManager`.
The core components are:

-   **`TerminalTheme`**: Represents a single theme (colors, name).
-   **`ThemeManager`**: Handles loading, saving, importing, and retrieving themes.
-   **`JsonColorConverter`**: A custom JSON converter that serializes/deserializes colors (e.g., `#RRGGBB` or `sc#RGBA`).

## Theme Structure

A theme is defined by a JSON object with the following properties:

-   **`Name`**: The display name of the theme (e.g., "Dracula").
-   **`Foreground`**: The default text color.
-   **`Background`**: The terminal background color.
-   **`CursorColor`**: The color of the cursor.
-   **`Black`**, `Red`, `Green`, `Yellow`, `Blue`, `Magenta`, `Cyan`, `White`: Standard ANSI colors (0-7).
-   **`BrightBlack`**, `BrightRed`... `BrightWhite`: Bright ANSI colors (8-15).

### Example (Dracula.json)

```json
{
  "Name": "Dracula",
  "Foreground": "#F8F8F2",
  "Background": "#282A36",
  "CursorColor": "#F8F8F2",
  "Black": "#21222C",
  "Red": "#FF5555",
  "Green": "#50FA7B",
  "Yellow": "#F1FA8C",
  "Blue": "#BD93F9",
  "Magenta": "#FF79C6",
  "Cyan": "#8BE9FD",
  "White": "#F8F8F2",
  "BrightBlack": "#6272A4",
  "BrightRed": "#FF6E6E",
  "BrightGreen": "#69FF94",
  "BrightYellow": "#FFFFA5",
  "BrightBlue": "#D6ACFF",
  "BrightMagenta": "#FF92DF",
  "BrightCyan": "#A4FFFF",
  "BrightWhite": "#FFFFFF"
}
```

## Theme Manager

The `ThemeManager` (`src/Ntilde.App/Shell/ThemeManager.cs`) is responsible for:

1.  **Loading Themes**: Scans the `themes/` directory for `.json` files on startup.
2.  **Built-in Themes**: Provides default themes (Dracula, Monokai, Solarized, etc.) if no custom themes are found.
3.  **Active Theme**: Tracks the currently applied theme.
4.  **Importing**: Can import themes from:
    *   **Windows Terminal** (`settings.json` schemes).
    *   **iTerm2** (`.itermcolors` files).
    *   **Lighthouse/Other** formats (via best-effort parsing).

## Usage

### In Code
```csharp
// Access the manager
var manager = new ThemeManager();

// Get a specific theme
var theme = manager.GetTheme("Dracula");

// Apply a theme
settings.ActiveTheme = theme;
```

### Adding a New Theme
1.  Create a JSON file in the `themes/` directory (e.g., `MyTheme.json`).
2.  Open Settings: it reads the theme files each time it opens, so the theme appears in the
    Settings → Appearance dropdown without a restart.

## Theme Editor
The application includes a built-in **Theme Editor** in the Settings window:
-   On Settings → Appearance, pick a theme and click **Edit** (or **+ New**). The editor opens in place of the page; **‹ Appearance** goes back without saving.
-   Adjust the foreground, background, cursor and the 16 ANSI colors; the preview beside the fields follows each change.
-   **Save theme** writes the JSON file straight away; the Settings window's Cancel does not undo it.
-   **Note**: The "Default" theme cannot be modified directly; clone it first or create a new theme.

## UI color tokens

The UI does not read theme colors directly. `ThemeTokens.Derive` (`src/Ntilde.App/Shell/Theming/`)
turns the active `TerminalTheme` into about 60 named UI colors: surfaces, text, interaction fills,
accent, status, tab-strip states. `ThemeService` installs them once into `Application.Resources` and
recolors them in place on every theme change, together with Fluent's stock-control palette and the
light/dark variant.

**Adding UI that follows the theme:**

- XAML: `{DynamicResource NtPanel}` (keys: `ThemeKeys.cs`). Never a hex or named color.
- C#: `this.FindResource(ThemeKeys.Panel)`, or bind with
  `control.Bind(Border.BackgroundProperty, control.GetResourceObservable(ThemeKeys.Panel))`.
  Never `Brushes.White`, `Color.Parse("#…")` or a `"#…"` string.
- No local copies: declaring `x:Key="NtPanel"` in a window shadows the app token.

**Guards (Architecture.Tests, gating):**

- `ThemeTokenKeyGuardTests`: every `Nt*` key used must exist in `ThemeKeys`.
- `ThemeLiteralGuardTests`: hardcoded colors outside `Shell/Theming` may only disappear
  (`ThemeLiteralBaseline.txt` is a shrinking ratchet). Colors fixed on purpose go in
  `ThemeLiteralPolicy.Permanent` with a reason.
- `ThemeContrastTests` (App.Tests): every shipped theme must yield readable chrome. When it fails,
  fix the derivation, never the threshold.

Theme v2 is landing in phases; see `docs/superpowers/specs/2026-09-23-theme-system-design.md`.
Phases 1 and 2 moved Settings, the Connection Manager, Agent Output and the main window's chrome
(tab strip and sidebar, title bar, splitters, toasts, command palette, transfers, dialogs) onto
the tokens. Pane overlays, Command Assist, Remote Files and Replay follow in phase 3, and the
terminal renderer's selection and search colours in phase 4.

## Key Files
-   `src/Ntilde.VT/TerminalTheme.cs`: Data model.
-   `src/Ntilde.App/Shell/ThemeManager.cs`: Loading, saving and importing themes.
-   `src/Ntilde.App/Shell/TermColorJsonConverter.cs`: JSON serialization.
-   `src/Ntilde.App/Shell/Theming/*`: UI color tokens (`ThemeKeys`, `ThemeTokens`, `ThemeService`, `ThemeColorMath`).
-   `src/Ntilde.App/Settings/Pages/AppearancePage.axaml`: UI for selecting and editing themes.
-   `src/Ntilde.App/themes/*.json`: Built-in theme definitions.
