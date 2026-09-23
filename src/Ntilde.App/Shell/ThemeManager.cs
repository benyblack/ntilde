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

                foreach (var theme in imported)
                {
                    string fileName = theme.Name.Replace(" ", "") + ".json";
                    string targetPath = Path.Combine(_themesDirectory, fileName);
                    string json = JsonSerializer.Serialize(theme, AppJsonContext.Default.TerminalTheme);
                    File.WriteAllText(targetPath, json);
                    _loadedThemes[theme.Name] = theme;
                }
                return imported[^1].Name;
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
            string fileName = theme.Name.Replace(" ", "").Replace("/", "_").Replace("\\", "_") + ".json";
            string targetPath = Path.Combine(_themesDirectory, fileName);

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
                string fileName = theme.Name.Replace(" ", "").Replace("/", "_").Replace("\\", "_") + ".json";
                string targetPath = Path.Combine(_themesDirectory, fileName);

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

        private void ExtractBuiltInThemes()
        {
            // Built-in themes are now provided as JSON files in the themes directory.
        }
    }
}
