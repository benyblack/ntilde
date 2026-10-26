using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media;
using Ntilde.VT;

namespace Ntilde.Shell.ThemeImporters
{
    public class AlacrittyImporter : IThemeImporter
    {
        public string Name => "Alacritty";
        public string Extension => ".toml";

        public IEnumerable<TerminalTheme> Import(string filePath)
        {
            var themes = new List<TerminalTheme>();
            try
            {
                var theme = new TerminalTheme
                {
                    Name = Path.GetFileNameWithoutExtension(filePath) + " (Alacritty)"
                };

                string section = string.Empty;
                foreach (var raw in File.ReadLines(filePath))
                {
                    string line = StripComment(raw).Trim();
                    if (line.Length == 0) continue;

                    if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                    {
                        section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                        continue;
                    }

                    if (!TrySplitPair(line, out string key, out string value)) continue;

                    // An inline table (`matches = { background = "#..." }`) is the same keys one level down.
                    if (value.StartsWith("{", StringComparison.Ordinal) && value.EndsWith("}", StringComparison.Ordinal))
                    {
                        foreach (var part in value.Substring(1, value.Length - 2).Split(','))
                        {
                            if (TrySplitPair(part, out string innerKey, out string innerValue))
                            {
                                Apply(theme, $"{section}.{key}.{innerKey}", innerValue);
                            }
                        }

                        continue;
                    }

                    Apply(theme, $"{section}.{key}", value);
                }

                themes.Add(theme);
            }
            catch
            {
                // Import errors are non-fatal and handled by caller.
            }

            return themes;
        }

        private static void Apply(TerminalTheme theme, string key, string value)
        {
            // Alacritty's CellForeground/CellBackground mean "the cell's own color": for the
            // optional colors that is exactly Ntilde's derived behavior, so they map to null.
            bool cellKeyword = value.Equals("CellForeground", StringComparison.OrdinalIgnoreCase) ||
                               value.Equals("CellBackground", StringComparison.OrdinalIgnoreCase);
            TermColor? optional = !cellKeyword && TryParseColor(value, out var parsed) ? parsed : null;
            if (optional is not { } color)
            {
                switch (key)
                {
                    case "colors.selection.background": theme.SelectionBackground = null; break;
                    case "colors.selection.text": theme.SelectionForeground = null; break;
                    case "colors.cursor.text": theme.CursorText = null; break;
                }

                return;
            }

            switch (key)
            {
                case "colors.primary.foreground": theme.Foreground = color; break;
                case "colors.primary.background": theme.Background = color; break;
                case "colors.cursor.cursor": theme.CursorColor = color; break;
                case "colors.cursor.text": theme.CursorText = color; break;
                case "colors.selection.background": theme.SelectionBackground = color; break;
                case "colors.selection.text": theme.SelectionForeground = color; break;
                case "colors.search.matches.background": theme.SearchMatch = color; break;
                case "colors.search.focused_match.background": theme.SearchMatchActive = color; break;

                case "colors.normal.black": theme.Black = color; break;
                case "colors.normal.red": theme.Red = color; break;
                case "colors.normal.green": theme.Green = color; break;
                case "colors.normal.yellow": theme.Yellow = color; break;
                case "colors.normal.blue": theme.Blue = color; break;
                case "colors.normal.magenta": theme.Magenta = color; break;
                case "colors.normal.cyan": theme.Cyan = color; break;
                case "colors.normal.white": theme.White = color; break;

                case "colors.bright.black": theme.BrightBlack = color; break;
                case "colors.bright.red": theme.BrightRed = color; break;
                case "colors.bright.green": theme.BrightGreen = color; break;
                case "colors.bright.yellow": theme.BrightYellow = color; break;
                case "colors.bright.blue": theme.BrightBlue = color; break;
                case "colors.bright.magenta": theme.BrightMagenta = color; break;
                case "colors.bright.cyan": theme.BrightCyan = color; break;
                case "colors.bright.white": theme.BrightWhite = color; break;
            }
        }

        /// <summary>
        /// Drops a <c>#</c> comment, but only outside quotes: colors are themselves spelled
        /// <c>"#282a36"</c>, and cutting there left every quoted color empty.
        /// </summary>
        private static string StripComment(string line)
        {
            char quote = '\0';
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                }
                else if (c is '"' or '\'')
                {
                    quote = c;
                }
                else if (c == '#')
                {
                    return line.Substring(0, i);
                }
            }

            return line;
        }

        private static bool TrySplitPair(string text, out string key, out string value)
        {
            int eq = text.IndexOf('=');
            if (eq <= 0)
            {
                key = value = string.Empty;
                return false;
            }

            key = text.Substring(0, eq).Trim().ToLowerInvariant();
            value = text.Substring(eq + 1).Trim().Trim('"', '\'');
            return true;
        }

        private static bool TryParseColor(string value, out TermColor color)
        {
            color = TermColor.Black;
            if (string.IsNullOrWhiteSpace(value)) return false;

            string normalized = value.Trim();
            if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "#" + normalized.Substring(2);
            }
            else if (!normalized.StartsWith("#", StringComparison.Ordinal) &&
                normalized.Length == 6)
            {
                normalized = "#" + normalized;
            }

            try
            {
                var parsed = Color.Parse(normalized);
                color = TermColorHelper.FromAvaloniaColor(parsed);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
