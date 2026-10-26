using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml.Linq;
using Avalonia.Media;
using Ntilde.VT;

namespace Ntilde.Shell.ThemeImporters
{
    public class ITerm2Importer : IThemeImporter
    {
        public string Name => "iTerm2";
        public string Extension => ".itermcolors";

        public IEnumerable<TerminalTheme> Import(string filePath)
        {
            var themes = new List<TerminalTheme>();
            try
            {
                var doc = XDocument.Load(filePath);
                var dict = doc.Root?.Element("dict");
                if (dict == null) return themes;

                var theme = new TerminalTheme
                {
                    Name = Path.GetFileNameWithoutExtension(filePath)
                };

                var elements = dict.Elements();
                string? currentKey = null;

                foreach (var el in elements)
                {
                    if (el.Name == "key")
                    {
                        currentKey = el.Value;
                    }
                    else if (el.Name == "dict" && currentKey != null)
                    {
                        var color = ParsePlistColor(el);
                        MapItermKeyToTheme(currentKey, color, theme);
                    }
                }

                themes.Add(theme);
            }
            catch { }
            return themes;
        }

        private TermColor ParsePlistColor(XElement dict)
        {
            float r = 0, g = 0, b = 0;
            var elements = dict.Elements();
            string? currentKey = null;

            foreach (var el in elements)
            {
                if (el.Name == "key") currentKey = el.Value;
                else if (el.Name == "real" && currentKey != null)
                {
                    // Plist reals are invariant ("0.5"): a comma-decimal locale must not misread them.
                    if (!float.TryParse(el.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float val)) continue;
                    if (currentKey.Contains("Red Component")) r = val;
                    else if (currentKey.Contains("Green Component")) g = val;
                    else if (currentKey.Contains("Blue Component")) b = val;
                }
            }

            return TermColor.FromRgb(ToByte(r), ToByte(g), ToByte(b));
        }

        // Rounded, not truncated: components exported from 8-bit values come back as 254.9999.
        private static byte ToByte(float component) => (byte)Math.Clamp((int)Math.Round(component * 255f), 0, 255);

        private void MapItermKeyToTheme(string key, TermColor color, TerminalTheme theme)
        {
            switch (key)
            {
                case "Foreground Color": theme.Foreground = color; break;
                case "Background Color": theme.Background = color; break;
                case "Cursor Color": theme.CursorColor = color; break;
                case "Cursor Text Color": theme.CursorText = color; break;
                case "Selection Color": theme.SelectionBackground = color; break;
                case "Selected Text Color": theme.SelectionForeground = color; break;
                case "Ansi 0 Color": theme.Black = color; break;
                case "Ansi 1 Color": theme.Red = color; break;
                case "Ansi 2 Color": theme.Green = color; break;
                case "Ansi 3 Color": theme.Yellow = color; break;
                case "Ansi 4 Color": theme.Blue = color; break;
                case "Ansi 5 Color": theme.Magenta = color; break;
                case "Ansi 6 Color": theme.Cyan = color; break;
                case "Ansi 7 Color": theme.White = color; break;
                case "Ansi 8 Color": theme.BrightBlack = color; break;
                case "Ansi 9 Color": theme.BrightRed = color; break;
                case "Ansi 10 Color": theme.BrightGreen = color; break;
                case "Ansi 11 Color": theme.BrightYellow = color; break;
                case "Ansi 12 Color": theme.BrightBlue = color; break;
                case "Ansi 13 Color": theme.BrightMagenta = color; break;
                case "Ansi 14 Color": theme.BrightCyan = color; break;
                case "Ansi 15 Color": theme.BrightWhite = color; break;
            }
        }
    }
}
