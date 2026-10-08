using Avalonia.Input;
using Ntilde.Shell;
using Xunit;

namespace Ntilde.Tests.Input;

public sealed class MacClipboardChordTests
{
    [Theory]
    [InlineData(Key.C, KeyModifiers.Meta, Key.C, true, true)]
    [InlineData(Key.V, KeyModifiers.Meta, Key.V, true, true)]
    // Not macOS: Cmd/Win+C must not be treated as copy.
    [InlineData(Key.C, KeyModifiers.Meta, Key.C, false, false)]
    // Extra modifiers are a different chord (Cmd+Shift+C, Cmd+Ctrl+V, Cmd+Alt+C).
    [InlineData(Key.C, KeyModifiers.Meta | KeyModifiers.Shift, Key.C, true, false)]
    [InlineData(Key.V, KeyModifiers.Meta | KeyModifiers.Control, Key.V, true, false)]
    [InlineData(Key.C, KeyModifiers.Meta | KeyModifiers.Alt, Key.C, true, false)]
    // Ctrl+C / plain C are not the Cmd chord.
    [InlineData(Key.C, KeyModifiers.Control, Key.C, true, false)]
    [InlineData(Key.C, KeyModifiers.None, Key.C, true, false)]
    // Wrong key.
    [InlineData(Key.X, KeyModifiers.Meta, Key.C, true, false)]
    public void MatchesOnlyBareCmdChordOnMac(Key key, KeyModifiers modifiers, Key expected, bool isMacOS, bool matches)
    {
        Assert.Equal(matches, TerminalView.IsMacClipboardChord(key, modifiers, expected, isMacOS));
    }
}
