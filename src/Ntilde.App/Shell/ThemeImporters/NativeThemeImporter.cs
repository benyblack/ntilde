using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ntilde.VT;

namespace Ntilde.Shell.ThemeImporters
{
    /// <summary>
    /// The app's own theme format: a serialized <see cref="TerminalTheme"/>, the shape the
    /// built-in themes ship in and <see cref="ThemeManager.SaveTheme"/> writes.
    /// </summary>
    public class NativeThemeImporter : IThemeImporter
    {
        public string Name => "Ntilde";
        public string Extension => ".json";

        public IEnumerable<TerminalTheme> Import(string filePath)
        {
            try
            {
                string json = File.ReadAllText(filePath);

                // Deserializing ignores unknown keys, so any JSON object would come back as a
                // TerminalTheme full of defaults. Require the PascalCase "Name" this format always
                // carries; the lookup is case-sensitive, so a WT scheme's "name" does not match.
                if (JsonNode.Parse(json) is JsonObject root && root.ContainsKey(nameof(TerminalTheme.Name)))
                {
                    var theme = JsonSerializer.Deserialize(json, AppJsonContext.Default.TerminalTheme);
                    if (theme != null) return new[] { theme };
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Not a readable native theme; ThemeManager offers the file to the next importer.
            }
            return Array.Empty<TerminalTheme>();
        }
    }
}
