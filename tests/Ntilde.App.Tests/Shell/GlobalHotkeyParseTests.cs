using Ntilde.Shell;
using Xunit;

namespace Ntilde.Tests.Shell;

public sealed class GlobalHotkeyParseTests
{
    [Theory]
    [InlineData("Alt+OemTilde", GlobalHotkey.ModAlt, 0xC0u)]
    [InlineData("alt+`", GlobalHotkey.ModAlt, 0xC0u)]
    [InlineData("Ctrl+Shift+Space", GlobalHotkey.ModControl | GlobalHotkey.ModShift, 0x20u)]
    [InlineData("Control + Alt + F11", GlobalHotkey.ModControl | GlobalHotkey.ModAlt, 0x7Au)]
    [InlineData("Win+D1", GlobalHotkey.ModWin, 0x31u)]
    [InlineData("Ctrl+q", GlobalHotkey.ModControl, 0x51u)]
    [InlineData("Alt+7", GlobalHotkey.ModAlt, 0x37u)]
    public void TryParse_ReadsModifiersAndKey(string text, uint modifiers, uint key)
    {
        Assert.True(GlobalHotkey.TryParse(text, out uint parsedModifiers, out uint parsedKey));
        Assert.Equal(modifiers, parsedModifiers);
        Assert.Equal(key, parsedKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("OemTilde")]        // no modifier: would steal the key from every program
    [InlineData("Alt+")]
    [InlineData("Hyper+A")]
    [InlineData("Alt+F25")]
    [InlineData("Ctrl+Alt+F12")]   // reserved for the debugger
    [InlineData("Alt+NoSuchKey")]
    public void TryParse_RejectsWhatItCannotRegister(string? text)
    {
        Assert.False(GlobalHotkey.TryParse(text, out _, out _));
    }

    [Fact]
    public void TheSettingsDefault_ParsesToTheBuiltInDefault()
    {
        Assert.True(GlobalHotkey.TryParse(new TerminalSettings().GlobalHotkey, out uint modifiers, out uint key));
        Assert.Equal(GlobalHotkey.DefaultModifiers, modifiers);
        Assert.Equal(GlobalHotkey.DefaultKey, key);
    }
}
