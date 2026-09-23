using Ntilde.Shell;
using System;
using System.IO;
using System.Text.Json;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests
{
    /// <summary>
    /// ThemeManager.ImportTheme accepts several unrelated formats under the same ".json"
    /// extension: a native Ntilde theme (PascalCase keys, the shape the built-in themes ship in),
    /// a Windows Terminal settings file with a "schemes" array, a bare array of WT schemes, and a
    /// single WT scheme object. A failed import returns "" with no other signal, so each shape is
    /// asserted here rather than trusted to the settings UI.
    /// </summary>
    public class ThemeImportTests : IDisposable
    {
        private readonly string _root;
        private readonly string _themesDirectory;

        public ThemeImportTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ntilde-theme-import-" + Guid.NewGuid().ToString("N"));
            _themesDirectory = Path.Combine(_root, "themes");
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        [Theory]
        [MemberData(nameof(BuiltinThemeTests.AllBuiltinThemeFiles), MemberType = typeof(BuiltinThemeTests))]
        public void ImportTheme_NativeThemeFile_ImportsWithEveryColor(string fileName)
        {
            string source = Path.Combine(BuiltinThemeTests.FindThemesDirectory(), fileName + ".json");
            var expected = JsonSerializer.Deserialize(File.ReadAllText(source), AppJsonContext.Default.TerminalTheme)!;

            string imported = new ThemeManager(_themesDirectory).ImportTheme(source);

            Assert.Equal(expected.Name, imported);
            // A fresh manager proves the import was persisted, not just cached in memory.
            AssertSameColors(expected, new ThemeManager(_themesDirectory).GetTheme(imported));
        }

        [Fact]
        public void ImportTheme_WindowsTerminalSchemeObject_Imports()
        {
            // The shape a user gets by copying one entry out of WT's settings.json "schemes".
            string source = WriteSource("campbell.json", """
                {
                    "name": "Campbell",
                    "background": "#0C0C0C",
                    "foreground": "#CCCCCC",
                    "cursorColor": "#FFFFFF",
                    "black": "#0C0C0C",
                    "red": "#C50F1F",
                    "green": "#13A10E",
                    "yellow": "#C19C00",
                    "blue": "#0037DA",
                    "purple": "#881798",
                    "cyan": "#3A96DD",
                    "white": "#CCCCCC",
                    "brightBlack": "#767676",
                    "brightRed": "#E74856",
                    "brightGreen": "#16C60C",
                    "brightYellow": "#F9F1A5",
                    "brightBlue": "#3B78FF",
                    "brightPurple": "#B4009E",
                    "brightCyan": "#61D6D6",
                    "brightWhite": "#F2F2F2"
                }
                """);

            string imported = new ThemeManager(_themesDirectory).ImportTheme(source);

            Assert.Equal("Campbell", imported);
            var theme = new ThemeManager(_themesDirectory).GetTheme("Campbell");
            Assert.Equal("Campbell", theme.Name);
            Assert.Equal(new TermColor(0x0C, 0x0C, 0x0C), theme.Background);
            Assert.Equal(new TermColor(0xCC, 0xCC, 0xCC), theme.Foreground);
            // WT spells magenta "purple"; a mapping slip would leave the TerminalTheme default.
            Assert.Equal(new TermColor(0x88, 0x17, 0x98), theme.Magenta);
            Assert.Equal(new TermColor(0xB4, 0x00, 0x9E), theme.BrightMagenta);
        }

        [Fact]
        public void ImportTheme_WindowsTerminalSchemeArray_ImportsEveryScheme()
        {
            string source = WriteSource("schemes.json", """
                [
                    { "name": "Alpha", "background": "#101010", "foreground": "#E0E0E0" },
                    { "name": "Beta", "background": "#202020", "foreground": "#D0D0D0" }
                ]
                """);

            string imported = new ThemeManager(_themesDirectory).ImportTheme(source);

            Assert.Equal("Beta", imported); // the last scheme is the one the UI selects
            var reloaded = new ThemeManager(_themesDirectory);
            Assert.Equal(new TermColor(0x10, 0x10, 0x10), reloaded.GetTheme("Alpha").Background);
            Assert.Equal(new TermColor(0x20, 0x20, 0x20), reloaded.GetTheme("Beta").Background);
        }

        [Fact]
        public void ImportTheme_WindowsTerminalSettingsFile_ImportsEveryScheme()
        {
            string source = WriteSource("settings.json", """
                {
                    "defaultProfile": "{61c54bbd-c2c6-5271-96e7-009a87ff44bf}",
                    "profiles": { "list": [] },
                    "schemes": [
                        { "name": "Alpha", "background": "#101010", "foreground": "#E0E0E0" },
                        { "name": "Beta", "background": "#202020", "foreground": "#D0D0D0" }
                    ]
                }
                """);

            string imported = new ThemeManager(_themesDirectory).ImportTheme(source);

            Assert.Equal("Beta", imported);
            var reloaded = new ThemeManager(_themesDirectory);
            Assert.Equal(new TermColor(0x10, 0x10, 0x10), reloaded.GetTheme("Alpha").Background);
            Assert.Equal(new TermColor(0x20, 0x20, 0x20), reloaded.GetTheme("Beta").Background);
        }

        [Theory]
        [InlineData("""{ "editor.fontSize": 14 }""")] // valid JSON, no theme in it
        [InlineData("""{ "name": "No colors" }""")]  // a name alone is not a WT scheme
        [InlineData("""{ not json""")]
        public void ImportTheme_UnrecognizedJson_ReturnsEmptyAndWritesNothing(string content)
        {
            string source = WriteSource("unrelated.json", content);

            string imported = new ThemeManager(_themesDirectory).ImportTheme(source);

            Assert.Equal("", imported);
            Assert.Empty(Directory.GetFiles(_themesDirectory));
        }

        private string WriteSource(string fileName, string content)
        {
            string path = Path.Combine(_root, fileName);
            File.WriteAllText(path, content);
            return path;
        }

        private static void AssertSameColors(TerminalTheme expected, TerminalTheme actual)
        {
            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(expected.Foreground, actual.Foreground);
            Assert.Equal(expected.Background, actual.Background);
            Assert.Equal(expected.CursorColor, actual.CursorColor);
            for (int index = 0; index < 8; index++)
            {
                Assert.Equal(expected.GetAnsiColor(index, bright: false), actual.GetAnsiColor(index, bright: false));
                Assert.Equal(expected.GetAnsiColor(index, bright: true), actual.GetAnsiColor(index, bright: true));
            }
        }
    }
}
