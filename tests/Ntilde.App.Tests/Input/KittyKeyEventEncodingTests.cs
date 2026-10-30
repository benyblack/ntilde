using Avalonia.Input;
using Ntilde.Shell;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.Input;

public sealed class KittyKeyEventEncodingTests
{
    private static ModeState Modes(int flags)
    {
        var modes = new ModeState();
        modes.KittyKeyboard.Push(flags);
        return modes;
    }

    [Theory]
    [InlineData((int)TerminalKeyEventKind.Press, "\x1b[99;5u")]
    [InlineData((int)TerminalKeyEventKind.Repeat, "\x1b[99;5:2u")]
    [InlineData((int)TerminalKeyEventKind.Release, "\x1b[99;5:3u")]
    public void DisambiguatedKey_EncodesNegotiatedEventTypes(int kind, string expected)
    {
        Assert.Equal(expected, TerminalInputModeEncoder.EncodeKittyKey(Key.C, KeyModifiers.Control, Modes(3), (TerminalKeyEventKind)kind));
    }

    [Theory]
    [InlineData(Key.Up, KeyModifiers.None, (int)TerminalKeyEventKind.Press, "\x1b[A")]
    [InlineData(Key.Up, KeyModifiers.None, (int)TerminalKeyEventKind.Repeat, "\x1b[1;1:2A")]
    [InlineData(Key.Up, KeyModifiers.Shift, (int)TerminalKeyEventKind.Release, "\x1b[1;2:3A")]
    [InlineData(Key.Delete, KeyModifiers.Control, (int)TerminalKeyEventKind.Repeat, "\x1b[3;5:2~")]
    [InlineData(Key.F1, KeyModifiers.None, (int)TerminalKeyEventKind.Press, "\x1b[P")]
    [InlineData(Key.F3, KeyModifiers.None, (int)TerminalKeyEventKind.Release, "\x1b[13;1:3~")]
    [InlineData(Key.Left, KeyModifiers.Control | KeyModifiers.Alt, (int)TerminalKeyEventKind.Repeat, "\x1b[1;7:2D")]
    [InlineData(Key.F24, KeyModifiers.None, (int)TerminalKeyEventKind.Release, "\x1b[57387;1:3u")]
    [InlineData(Key.F12, KeyModifiers.Alt, (int)TerminalKeyEventKind.Release, "\x1b[24;3:3~")]
    public void FunctionalKeys_KeepTheirProtocolFinalByte(Key key, KeyModifiers modifiers, int kind, string expected)
    {
        Assert.Equal(expected, TerminalInputModeEncoder.EncodeKittyKey(key, modifiers, Modes(2), (TerminalKeyEventKind)kind));
    }

    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Tab)]
    [InlineData(Key.Back)]
    public void RecoveryKeys_NeverReportReleasesWithoutAllKeysMode(Key key)
    {
        Assert.Null(TerminalInputModeEncoder.EncodeKittyKey(key, KeyModifiers.Control, Modes(3), TerminalKeyEventKind.Release));
    }

    [Fact]
    public void EventReportingDisabled_RepeatsRemainPressesAndReleasesAreSilent()
    {
        Assert.Equal("\x1b[99;5u", TerminalInputModeEncoder.EncodeKittyKey(Key.C, KeyModifiers.Control, Modes(1), TerminalKeyEventKind.Repeat));
        Assert.Null(TerminalInputModeEncoder.EncodeKittyKey(Key.C, KeyModifiers.Control, Modes(1), TerminalKeyEventKind.Release));
    }

    [Fact]
    public void ReleasingAReportedChord_UsesTheCurrentModifiers()
    {
        Assert.Equal("\x1b[99;1:3u", TerminalInputModeEncoder.EncodeKittyKey(Key.C, KeyModifiers.None, Modes(3), TerminalKeyEventKind.Release));
    }

    [Theory]
    [InlineData(Key.A, KeyModifiers.None)]
    [InlineData(Key.A, KeyModifiers.Shift)]
    [InlineData(Key.Q, KeyModifiers.Control | KeyModifiers.Alt)]
    public void TextAndAltGrRepeats_RemainOnTheTextInputPath(Key key, KeyModifiers modifiers)
    {
        Assert.Null(TerminalInputModeEncoder.EncodeKittyKey(key, modifiers, Modes(3), TerminalKeyEventKind.Repeat));
    }
}
