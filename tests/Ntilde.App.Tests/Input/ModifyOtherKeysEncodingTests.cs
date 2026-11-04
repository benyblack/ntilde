using Avalonia.Input;
using Ntilde.Shell;
using Xunit;

namespace Ntilde.Tests.Input;

// Issue #829: exact bytes for xterm modifyOtherKeys levels 1 and 2 (CSI 27 ; m ; code ~),
// derived from xterm's input.c ModifyOtherKeys/allowedCharModifiers. null means "keep the
// legacy encoding" - the view then falls through to its unchanged legacy paths.
public sealed class ModifyOtherKeysEncodingTests
{
    private const KeyModifiers Shift = KeyModifiers.Shift;
    private const KeyModifiers Ctrl = KeyModifiers.Control;
    private const KeyModifiers Alt = KeyModifiers.Alt;

    public static TheoryData<Key, KeyModifiers, string?> LevelOne() => new()
    {
        // Letters: every chord keeps its legacy/text behavior at level 1.
        { Key.A, Ctrl, null },
        { Key.A, Ctrl | Shift, null },            // X11 still turns Ctrl+Shift+A into 0x01
        { Key.A, Alt, null },                     // Alt keeps sending ESC a
        { Key.A, Alt | Shift, null },
        { Key.A, Shift, null },
        // Digits: Ctrl+2..8 are X11 control aliases; 0, 1, 9 are not.
        { Key.D1, Ctrl, "\x1b[27;5;49~" },
        { Key.D9, Ctrl, "\x1b[27;5;57~" },
        { Key.D0, Ctrl, "\x1b[27;5;48~" },
        { Key.D2, Ctrl, null },
        { Key.D3, Ctrl, null },
        { Key.D8, Ctrl, null },
        { Key.D1, Ctrl | Shift, "\x1b[27;6;33~" }, // '!'
        { Key.D2, Ctrl | Shift, null },            // '@' -> NUL
        { Key.D6, Ctrl | Shift, null },            // '^' -> RS
        { Key.D1, Shift, null },
        { Key.D1, Alt, null },
        // Punctuation.
        { Key.OemSemicolon, Ctrl, "\x1b[27;5;59~" },
        { Key.OemSemicolon, Ctrl | Shift, "\x1b[27;6;58~" }, // ':'
        { Key.OemComma, Ctrl, "\x1b[27;5;44~" },
        { Key.OemPeriod, Ctrl, "\x1b[27;5;46~" },
        { Key.OemMinus, Ctrl, "\x1b[27;5;45~" },
        { Key.OemPlus, Ctrl, "\x1b[27;5;61~" },
        { Key.OemQuotes, Ctrl, "\x1b[27;5;39~" },
        { Key.OemQuestion, Ctrl | Shift, "\x1b[27;6;63~" }, // '?'
        { Key.OemQuestion, Ctrl, null },          // Ctrl+/ -> US
        { Key.OemOpenBrackets, Ctrl, null },      // Ctrl+[ -> ESC
        { Key.OemCloseBrackets, Ctrl, null },
        { Key.OemPipe, Ctrl, null },
        { Key.OemTilde, Ctrl, null },
        { Key.OemMinus, Ctrl | Shift, null },     // '_' -> US
        { Key.OemSemicolon, Alt, null },
        // Enter, Tab, Backspace, Escape, Space.
        { Key.Enter, Shift, "\x1b[27;2;13~" },
        { Key.Enter, Ctrl, "\x1b[27;5;13~" },
        { Key.Enter, Ctrl | Shift, "\x1b[27;6;13~" },
        { Key.Enter, Alt, null },
        { Key.Tab, Ctrl, "\x1b[27;5;9~" },
        { Key.Tab, Shift, null },
        { Key.Tab, Ctrl | Shift, null },
        { Key.Tab, Alt, null },
        { Key.Back, Shift, null },
        { Key.Back, Ctrl, null },
        { Key.Back, Alt, null },
        { Key.Escape, Shift, null },
        { Key.Escape, Ctrl, null },
        { Key.Escape, Alt, null },
        { Key.Space, Shift, null },
        { Key.Space, Ctrl, null },
        { Key.Space, Alt, null },
    };

    public static TheoryData<Key, KeyModifiers, string?> LevelTwo() => new()
    {
        // Letters with Ctrl, Ctrl+Shift and Alt.
        { Key.A, Ctrl, "\x1b[27;5;97~" },
        { Key.I, Ctrl, "\x1b[27;5;105~" },        // Ctrl+I is no longer Tab
        { Key.M, Ctrl, "\x1b[27;5;109~" },        // Ctrl+M is no longer Enter
        { Key.C, Ctrl, "\x1b[27;5;99~" },
        { Key.A, Ctrl | Shift, "\x1b[27;6;65~" },
        { Key.Z, Ctrl | Shift, "\x1b[27;6;90~" },
        { Key.A, Alt, "\x1b[27;3;97~" },
        { Key.A, Alt | Shift, "\x1b[27;4;65~" },
        { Key.A, Shift, null },                   // the shifted letter is its own text
        // Digits and punctuation, including the X11 control aliases.
        { Key.D1, Ctrl, "\x1b[27;5;49~" },
        { Key.D2, Ctrl, "\x1b[27;5;50~" },
        { Key.D3, Ctrl, "\x1b[27;5;51~" },
        { Key.D1, Ctrl | Shift, "\x1b[27;6;33~" },
        { Key.D1, Alt, "\x1b[27;3;49~" },
        { Key.D1, Alt | Shift, "\x1b[27;4;33~" },
        { Key.D1, Shift, null },
        { Key.OemOpenBrackets, Ctrl, "\x1b[27;5;91~" }, // Ctrl+[ is no longer Esc
        { Key.OemOpenBrackets, Ctrl | Shift, "\x1b[27;6;123~" },
        { Key.OemPipe, Ctrl, "\x1b[27;5;92~" },
        { Key.OemBackslash, Ctrl, "\x1b[27;5;92~" },
        { Key.OemQuestion, Ctrl, "\x1b[27;5;47~" },
        { Key.OemTilde, Ctrl, "\x1b[27;5;96~" },
        { Key.OemSemicolon, Ctrl, "\x1b[27;5;59~" },
        { Key.OemPeriod, Alt, "\x1b[27;3;46~" },
        { Key.OemMinus, Shift, null },
        // Enter, Tab, Backspace, Escape and Space with modifiers.
        { Key.Enter, Shift, "\x1b[27;2;13~" },
        { Key.Enter, Ctrl, "\x1b[27;5;13~" },
        { Key.Enter, Alt, "\x1b[27;3;13~" },
        { Key.Enter, Ctrl | Shift, "\x1b[27;6;13~" },
        { Key.Tab, Ctrl, "\x1b[27;5;9~" },
        { Key.Tab, Alt, "\x1b[27;3;9~" },
        { Key.Tab, Ctrl | Shift, "\x1b[27;6;9~" },
        { Key.Tab, Alt | Shift, "\x1b[27;4;9~" },
        { Key.Tab, Shift, null },                 // back-tab stays CSI Z
        { Key.Back, Shift, "\x1b[27;2;127~" },
        { Key.Back, Alt, "\x1b[27;3;127~" },
        { Key.Back, Alt | Shift, "\x1b[27;4;127~" },
        { Key.Back, Ctrl | Shift, "\x1b[27;6;8~" },
        { Key.Back, Ctrl, null },                 // xterm's backarrow toggle: legacy
        { Key.Escape, Shift, "\x1b[27;2;27~" },
        { Key.Escape, Ctrl, "\x1b[27;5;27~" },
        { Key.Escape, Alt, "\x1b[27;3;27~" },
        { Key.Space, Shift, "\x1b[27;2;32~" },
        { Key.Space, Ctrl, "\x1b[27;5;32~" },
        { Key.Space, Alt, "\x1b[27;3;32~" },
        { Key.Space, Ctrl | Shift, "\x1b[27;6;32~" },
    };

    [Theory]
    [MemberData(nameof(LevelOne))]
    public void LevelOne_EncodesOnlyChordsWithoutWellKnownLegacyBehavior(Key key, KeyModifiers modifiers, string? expected)
        => Assert.Equal(expected, TerminalInputModeEncoder.EncodeModifyOtherKeys(key, modifiers, 1));

    [Theory]
    [MemberData(nameof(LevelTwo))]
    public void LevelTwo_EncodesEveryModifiedOrdinaryKey(Key key, KeyModifiers modifiers, string? expected)
        => Assert.Equal(expected, TerminalInputModeEncoder.EncodeModifyOtherKeys(key, modifiers, 2));

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    public void UnsupportedLevels_EncodeNothing(int level)
    {
        Assert.Null(TerminalInputModeEncoder.EncodeModifyOtherKeys(Key.A, Ctrl, level));
        Assert.Null(TerminalInputModeEncoder.EncodeModifyOtherKeys(Key.D1, Ctrl, level));
        Assert.Null(TerminalInputModeEncoder.EncodeModifyOtherKeys(Key.Enter, Shift, level));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void UnmodifiedKeys_AltGrMetaKeypadAndSpecialKeys_StayLegacy(int level)
    {
        foreach (Key key in new[] { Key.A, Key.D1, Key.Enter, Key.Tab, Key.Back, Key.Escape, Key.Space })
            Assert.Null(TerminalInputModeEncoder.EncodeModifyOtherKeys(key, KeyModifiers.None, level));

        // Ctrl+Alt is AltGr on Windows; Meta is Cmd/Super and belongs to the UI.
        Assert.Null(TerminalInputModeEncoder.EncodeModifyOtherKeys(Key.Q, Ctrl | Alt, level));
        Assert.Null(TerminalInputModeEncoder.EncodeModifyOtherKeys(Key.D8, Ctrl | Alt, level));
        Assert.Null(TerminalInputModeEncoder.EncodeModifyOtherKeys(Key.C, KeyModifiers.Meta, level));
        Assert.Null(TerminalInputModeEncoder.EncodeModifyOtherKeys(Key.A, Ctrl | KeyModifiers.Meta, level));

        // Keypad, cursor, editing and function keys are not xterm "other keys".
        Assert.Null(TerminalInputModeEncoder.EncodeModifyOtherKeys(Key.Enter, Shift, level, PhysicalKey.NumPadEnter));
        foreach (Key key in new[] { Key.Up, Key.Home, Key.Delete, Key.PageUp, Key.F1, Key.F5, Key.NumPad1, Key.Add })
            Assert.Null(TerminalInputModeEncoder.EncodeModifyOtherKeys(key, Ctrl, level));
    }
}
