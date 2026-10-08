using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Ntilde.VT;

namespace Ntilde.Shell
{
    public class ThemeManager
    {
        private readonly string _themesDirectory;
        private readonly Dictionary<string, TerminalTheme> _loadedThemes = new();
        // Several formats share an extension (".json" is both native and Windows Terminal), so
        // ImportTheme tries every importer for the extension in this order.
        private readonly List<ThemeImporters.IThemeImporter> _importers = new()
        {
            new ThemeImporters.NativeThemeImporter(),
            new ThemeImporters.WindowsTerminalImporter(),
            new ThemeImporters.ITerm2Importer(),
            new ThemeImporters.AlacrittyImporter()
        };

        public ThemeManager(string? themesDirectory = null)
        {
            AppPaths.EnsureInitialized();
            _themesDirectory = themesDirectory ?? AppPaths.ThemesDirectory;
            if (!Directory.Exists(_themesDirectory))
            {
                Directory.CreateDirectory(_themesDirectory);
            }
        }

        private bool _isLoaded = false;

        public void ReloadThemes()
        {
            _isLoaded = false;
            LoadThemes();
        }

        public void LoadThemes()
        {
            if (_isLoaded) return;
            _loadedThemes.Clear();

            // Ensure Default theme is always available
            var defaultTheme = new TerminalTheme { Name = "Default" };
            _loadedThemes[defaultTheme.Name] = defaultTheme;

            // First load built-in defaults if directory is empty (extraction step)
            ExtractBuiltInThemes();

            var files = Directory.GetFiles(_themesDirectory, "*.json");
            foreach (var file in files)
            {
                try
                {
                    string json = File.ReadAllText(file);
                    var theme = JsonSerializer.Deserialize(json, AppJsonContext.Default.TerminalTheme);
                    if (theme != null)
                    {
                        _loadedThemes[theme.Name] = theme;
                    }
                }
                catch (Exception ex)
                {
                    TerminalLogger.Warning($"[ThemeManager] Error loading theme from {file}: {ex.Message}");
                }
            }
            _isLoaded = true;
        }

        public string ImportTheme(string filePath)
        {
            string ext = Path.GetExtension(filePath);

            // An importer that does not recognise the file yields nothing, and the next one for
            // the extension gets a turn; an unexpected throw from one is logged, not allowed to
            // end the search.
            foreach (var importer in _importers.Where(i => string.Equals(i.Extension, ext, StringComparison.OrdinalIgnoreCase)))
            {
                List<TerminalTheme> imported;
                try
                {
                    imported = importer.Import(filePath).ToList();
                }
                catch (Exception ex)
                {
                    TerminalLogger.Warning($"[ThemeManager] {importer.Name} importer failed on {filePath}: {ex.Message}");
                    continue;
                }
                if (imported.Count == 0) continue;

                // A theme that cannot be written is logged and skipped rather than thrown: the
                // settings window calls this from an async click handler that does not catch, and
                // one bad scheme should not cost the rest of the file.
                string lastWritten = "";
                foreach (var theme in imported)
                {
                    string targetPath = Path.Combine(_themesDirectory, ThemeFileName(theme.Name));
                    try
                    {
                        string json = JsonSerializer.Serialize(theme, AppJsonContext.Default.TerminalTheme);
                        File.WriteAllText(targetPath, json);
                    }
                    catch (Exception ex)
                    {
                        TerminalLogger.Warning($"[ThemeManager] Could not write imported theme {theme.Name} to {targetPath}: {ex.Message}");
                        continue;
                    }
                    _loadedThemes[theme.Name] = theme;
                    lastWritten = theme.Name;
                }
                return lastWritten;
            }

            TerminalLogger.Warning($"[ThemeManager] No theme importer recognised {filePath}");
            return "";
        }

        public IEnumerable<string> GetAvailableThemes()
        {
            LoadThemes();
            return _loadedThemes.Keys;
        }

        public TerminalTheme GetTheme(string name)
        {
            LoadThemes(); // Ensure loaded

            if (name == "Default (Dark)") name = "Default";

            if (_loadedThemes.TryGetValue(name, out var theme))
            {
                return theme;
            }
            return _loadedThemes["Default"];
        }

        public void SaveTheme(TerminalTheme theme)
        {
            string targetPath = Path.Combine(_themesDirectory, ThemeFileName(theme.Name));

            try
            {
                string json = JsonSerializer.Serialize(theme, AppJsonContext.Default.TerminalTheme);
                File.WriteAllText(targetPath, json);

                // Update cache
                _loadedThemes[theme.Name] = theme;
            }
            catch (Exception ex)
            {
                TerminalLogger.Error($"[ThemeManager] Error saving theme {theme.Name}: {ex.Message}");
            }
        }

        public void DeleteTheme(string name)
        {
            if (name == "Default") return; // Cannot delete default

            if (_loadedThemes.TryGetValue(name, out var theme))
            {
                string targetPath = Path.Combine(_themesDirectory, ThemeFileName(theme.Name));

                try
                {
                    if (File.Exists(targetPath))
                    {
                        File.Delete(targetPath);
                    }
                    _loadedThemes.Remove(name);
                }
                catch (Exception ex)
                {
                    TerminalLogger.Error($"[ThemeManager] Error deleting theme {name}: {ex.Message}");
                }
            }
        }

        // Backslash is not in the Unix invalid set, but SaveTheme always replaced it, so it stays
        // replaced on every platform.
        private static readonly char[] UnsafeFileNameChars = Path.GetInvalidFileNameChars().Append('\\').ToArray();

        // The one place a theme's file name comes from. The name is free text from the theme's
        // source, so it can hold a path separator or a character the file system rejects; import,
        // save, and delete must all derive the same file name from it, or a file one of them
        // writes is never found by another. Spaces are dropped as they always were, so existing
        // theme files keep their names (the built-in "Solarized Dark" ships as SolarizedDark.json).
        private static string ThemeFileName(string themeName) =>
            new string(themeName.Replace(" ", "").Select(c => UnsafeFileNameChars.Contains(c) ? '_' : c).ToArray()) + ".json";

        private void ExtractBuiltInThemes()
        {
            // Built-in themes are now provided as JSON files in the themes directory.
        }
    }
}
