using Avalonia.Input;
using Ntilde.Shell.Shortcuts;

namespace Ntilde.Tests.Core;

public sealed class ShortcutMatcherTests
{
    [Fact]
    public void Matches_AcceptsSettingsShortcutCommaBinding()
    {
        var args = new KeyEventArgs
        {
            Key = Key.OemComma,
            KeyModifiers = KeyModifiers.Control,
        };

        Assert.True(ShortcutMatcher.Matches(args, "Ctrl+,"));
    }

    [Fact]
    public void Matches_RejectsExtraModifiers()
    {
        var args = new KeyEventArgs
        {
            Key = Key.OemComma,
            KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift,
        };

        Assert.False(ShortcutMatcher.Matches(args, "Ctrl+,"));
    }

    [Fact]
    public void Matches_CmdBindingMatchesMeta()
    {
        var args = new KeyEventArgs { Key = Key.V, KeyModifiers = KeyModifiers.Meta };

        Assert.True(ShortcutMatcher.Matches(args, "Cmd+V"));
        Assert.False(ShortcutMatcher.Matches(args, "Ctrl+V"));
    }

    [Fact]
    public void Matches_CtrlBindingRejectsAnAddedCmd()
    {
        // Meta used to be masked off, so Cmd+Ctrl+V still matched Ctrl+V.
        var args = new KeyEventArgs { Key = Key.V, KeyModifiers = KeyModifiers.Control | KeyModifiers.Meta };

        Assert.False(ShortcutMatcher.Matches(args, "Ctrl+V"));
    }

    [Fact]
    public void Format_WritesCmdFirst_AndRoundTrips()
    {
        string formatted = ShortcutMatcher.Format(Key.T, KeyModifiers.Meta | KeyModifiers.Shift);

        Assert.Equal("Cmd+Shift+T", formatted);
        Assert.True(ShortcutMatcher.TryParse(formatted, out Key key, out KeyModifiers modifiers));
        Assert.Equal(Key.T, key);
        Assert.Equal(KeyModifiers.Meta | KeyModifiers.Shift, modifiers);
    }

    [Theory]
    [InlineData("cmd+shift+t")]
    [InlineData("Command+Shift+T")]
    [InlineData("Shift+Meta+T")]
    [InlineData("Super+Shift+T")]
    [InlineData("Win+Shift+T")]
    public void Normalize_AcceptsCmdAliases(string binding)
    {
        Assert.Equal("Cmd+Shift+T", ShortcutNormalizer.Normalize(binding));
    }

    [Fact]
    public void Normalize_RejectsARepeatedCmd()
    {
        Assert.Throws<System.ArgumentException>(() => ShortcutNormalizer.Normalize("Cmd+Meta+T"));
    }
}
