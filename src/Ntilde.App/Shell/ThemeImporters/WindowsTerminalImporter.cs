using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Media;
using Ntilde.VT;

namespace Ntilde.Shell.ThemeImporters
{
    public class WindowsTerminalImporter : IThemeImporter
    {
        public string Name => "Windows Terminal";
        public string Extension => ".json";

        public IEnumerable<TerminalTheme> Import(string filePath)
        {
            var themes = new List<TerminalTheme>();
            try
            {
                string json = File.ReadAllText(filePath);
                foreach (var scheme in FindSchemes(JsonNode.Parse(json)))
                {
                    var theme = MapToTerminalTheme(scheme);
                    if (theme != null) themes.Add(theme);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Not readable JSON; ThemeManager offers the file to the next importer.
            }
            return themes;
        }

        /// <summary>
        /// WT schemes turn up as a whole settings file with a "schemes" array, a bare array of
        /// schemes, or one scheme object copied out of that array. Any other shape, including a
        /// native theme (PascalCase keys), yields nothing instead of throwing, so it cannot mask
        /// a format another importer would have recognised.
        /// </summary>
        private static IEnumerable<JsonObject> FindSchemes(JsonNode? root) => root switch
        {
            JsonObject settings when settings["schemes"] is JsonArray schemes => schemes.OfType<JsonObject>().Where(IsScheme),
            JsonArray schemes => schemes.OfType<JsonObject>().Where(IsScheme),
            JsonObject scheme when IsScheme(scheme) => new[] { scheme },
            _ => Array.Empty<JsonObject>()
        };

        private static bool IsScheme(JsonObject node) =>
            node["name"]?.GetValueKind() == JsonValueKind.String
            && (node.ContainsKey("background") || node.ContainsKey("foreground"));

        private TerminalTheme? MapToTerminalTheme(JsonNode node)
        {
            try
            {
                var theme = new TerminalTheme
                {
                    Name = node["name"]?.ToString() ?? "Imported WT Theme",
                    Foreground = ParseColor(node["foreground"]?.ToString(), TermColor.LightGray),
                    Background = ParseColor(node["background"]?.ToString(), TermColor.Black),
                    CursorColor = ParseColor(node["cursorColor"]?.ToString(), TermColor.White),

                    Black = ParseColor(node["black"]?.ToString(), TermColor.Black),
                    Red = ParseColor(node["red"]?.ToString(), TermColor.Red),
                    Green = ParseColor(node["green"]?.ToString(), TermColor.Green),
                    Yellow = ParseColor(node["yellow"]?.ToString(), TermColor.Yellow),
                    Blue = ParseColor(node["blue"]?.ToString(), TermColor.Blue),
                    Magenta = ParseColor(node["purple"]?.ToString(), TermColor.Magenta),
                    Cyan = ParseColor(node["cyan"]?.ToString(), TermColor.Cyan),
                    White = ParseColor(node["white"]?.ToString(), TermColor.White),

                    BrightBlack = ParseColor(node["brightBlack"]?.ToString(), TermColor.DarkGray),
                    BrightRed = ParseColor(node["brightRed"]?.ToString(), TermColor.Red),
                    BrightGreen = ParseColor(node["brightGreen"]?.ToString(), TermColor.Green),
                    BrightYellow = ParseColor(node["brightYellow"]?.ToString(), TermColor.Yellow),
                    BrightBlue = ParseColor(node["brightBlue"]?.ToString(), TermColor.Blue),
                    BrightMagenta = ParseColor(node["brightPurple"]?.ToString(), TermColor.Magenta),
                    BrightCyan = ParseColor(node["brightCyan"]?.ToString(), TermColor.Cyan),
                    BrightWhite = ParseColor(node["brightWhite"]?.ToString(), TermColor.White)
                };
                return theme;
            }
            catch { return null; }
        }

        private TermColor ParseColor(string? hex, TermColor fallback)
        {
            if (string.IsNullOrEmpty(hex)) return fallback;
            try
            {
                var avaloniaColor = Color.Parse(hex);
                return TermColorHelper.FromAvaloniaColor(avaloniaColor);
            }
            catch { return fallback; }
        }
    }
}
