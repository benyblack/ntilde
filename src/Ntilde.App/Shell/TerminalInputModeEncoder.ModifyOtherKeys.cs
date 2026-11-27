using System.Globalization;
using Avalonia.Input;

namespace Ntilde.Shell;

internal static partial class TerminalInputModeEncoder
{
    /// <summary>
    /// xterm modifyOtherKeys encoding for the "ordinary" keys (letters, digits, punctuation,
    /// Space) and Enter/Tab/Backspace/Escape, in xterm's default <c>formatOtherKeys: 0</c>
    /// form <c>CSI 27 ; m ; code ~</c>, where <c>m = 1 + shift(1) + alt(2) + ctrl(4)</c> and
    /// <c>code</c> is the character the key produces with Shift applied (Ctrl+Shift+A is
    /// <c>CSI 27;6;65~</c>). Returns <c>null</c> when the chord keeps its legacy encoding at
    /// <paramref name="level"/>, so the caller falls through to the legacy paths unchanged.
    ///
    /// Which chords are encoded follows xterm's input.c (<c>ModifyOtherKeys</c> and
    /// <c>allowedCharModifiers</c>), modeled with <c>metaSendsEscape</c> true, the DEC
    /// backarrow default (Backspace sends DEL, Ctrl+Backspace BS) and Alt as the meta key:
    ///
    /// Level 1 keeps every chord with well-known legacy behavior:
    /// - Ctrl or Ctrl+Shift on a key that X11 turns into a control character stays legacy:
    ///   letters, <c>@ [ \ ] ^ _ `</c> and their shifted forms, Space, <c>2</c>-<c>8</c> and
    ///   <c>/</c> (Ctrl+A is 0x01, Ctrl+Shift+A also 0x01, Ctrl+3 ESC).
    /// - Ctrl or Ctrl+Shift on any other ordinary key is encoded: Ctrl+1 <c>CSI 27;5;49~</c>,
    ///   Ctrl+; <c>CSI 27;5;59~</c>, Ctrl+Shift+1 <c>CSI 27;6;33~</c>.
    /// - Shift alone on a printable key is its text; Alt never causes or joins an encoding
    ///   (it keeps sending ESC-prefixed legacy bytes).
    /// - Enter with Shift and/or Ctrl is encoded (<c>CSI 27;2;13~</c>); Ctrl+Tab is
    ///   <c>CSI 27;5;9~</c>; Shift+Tab stays <c>CSI Z</c>; Backspace, Escape and Space always
    ///   stay legacy.
    ///
    /// Level 2 encodes every modified ordinary key, including the X11 control aliases:
    /// - any chord with Ctrl and/or Alt: Ctrl+A <c>CSI 27;5;97~</c>, Ctrl+Shift+A
    ///   <c>CSI 27;6;65~</c>, Alt+A <c>CSI 27;3;97~</c>, Ctrl+[ <c>CSI 27;5;91~</c>,
    ///   Ctrl+2 <c>CSI 27;5;50~</c>;
    /// - Shift+Space <c>CSI 27;2;32~</c>; Enter and Escape with any modifier; Tab with any
    ///   modifier except Shift alone (<c>CSI Z</c>);
    /// - Backspace with Shift and/or Alt as DEL (<c>CSI 27;2;127~</c>), Ctrl+Shift+Backspace
    ///   as BS (<c>CSI 27;6;8~</c>) and plain Ctrl+Backspace legacy, as xterm's backarrow toggle
    ///   gives.
    ///
    /// Level 3 also reports unmodified and Shift-only printable keys using the backend scalar when supplied. Named controls keep their level-2 routing.
    ///
    /// Deliberate deviations, each falling back to the existing legacy/text path:
    /// - Shift alone on a printable key stays text at levels 1 and 2. xterm level 2 sends Shift+A as
    ///   <c>CSI 27;2;65~</c> but Vim, tmux and Emacs strip a Shift that is already in the
    ///   character, so text is equivalent for them, and it keeps IME, dead-key and non-Latin
    ///   layout text on TextInput (the virtual key below is a US-layout key).
    /// - Ctrl+Alt is not encoded: on Windows it is how AltGr arrives, and AltGr composes text
    ///   (the same carve-out the legacy and kitty encoders make). Meta/Super/Cmd chords are not
    ///   encoded either, so macOS Cmd shortcuts stay with the UI.
    /// - Keypad, cursor, editing and function keys are not "other keys"
    ///   in xterm and keep their PC-style encodings at every level.
    /// - Only the <c>CSI 27 ; m ; code ~</c> form. xterm's <c>formatOtherKeys: 1</c> form
    ///   (<c>CSI code ; m u</c>) is not offered: it is a resource, not something the protocol
    ///   negotiates, the consumers named in #829 parse the <c>~</c> form, and a CSI-u key report
    ///   would be indistinguishable from the kitty keyboard protocol's frames.
    ///
    /// Layout: <paramref name="key"/> is a US-layout virtual key, the same assumption
    /// <see cref="EncodeLegacyControlKey"/> and <see cref="EncodeAltKey"/> make, so the code
    /// for a punctuation chord is that of the key in the US position.
    /// </summary>
    internal static string? EncodeModifyOtherKeys(Key key, KeyModifiers modifiers, int level, PhysicalKey physical = PhysicalKey.None, string? keySymbol = null)
    {
        const KeyModifiers altGr = KeyModifiers.Control | KeyModifiers.Alt;
        if (level is < 1 or > 3 || (modifiers & KeyModifiers.Meta) != 0 || (modifiers & altGr) == altGr) return null;
        // A physical keypad key can arrive under an ordinary logical key, e.g. keypad comma as OemComma.
        if (IsPhysicalKeypadKey(physical)) return null;

        int printable = GetUsLayoutCharacter(key, (modifiers & KeyModifiers.Shift) != 0);
        int code = printable >= 0
            ? ModifyOtherPrintableCode(key, modifiers, level, printable, keySymbol)
            : ModifyOtherControlCode(key, modifiers, level);
        return code < 0 ? null : FormatModifyOtherKey(code, GetXtermModifierParameter(modifiers));
    }

    private static int ModifyOtherPrintableCode(Key key, KeyModifiers modifiers, int level, int printable, string? keySymbol)
    {
        if (modifiers is KeyModifiers.None or KeyModifiers.Shift)
        {
            if (level < 3) return level == 2 && key == Key.Space && modifiers == KeyModifiers.Shift ? printable : -1;
            // Lock state and layout are already reflected in the symbol. Leave composition to TextInput.
            if (string.IsNullOrEmpty(keySymbol)) return printable;
            return TryKeyScalar(keySymbol, out var rune) ? rune.Value : -1;
        }
        // Level 1 leaves Alt and X11 control aliases on the legacy path.
        if (level == 1 && ((modifiers & KeyModifiers.Alt) != 0 || IsX11ControlAlias(printable))) return -1;
        return printable;
    }

    private static int ModifyOtherControlCode(Key key, KeyModifiers modifiers, int level)
    {
        if (modifiers == KeyModifiers.None) return -1;
        return key switch
        {
            Key.Enter when level >= 2 || (modifiers & KeyModifiers.Alt) == 0 => 13,
            Key.Tab when modifiers != KeyModifiers.Shift && (level >= 2 || modifiers == KeyModifiers.Control) => 9,
            Key.Back when level >= 2 && (modifiers & KeyModifiers.Control) == 0 => 127,
            Key.Back when level >= 2 && (modifiers & KeyModifiers.Shift) != 0 => 8,
            Key.Escape when level >= 2 => 27,
            _ => -1
        };
    }

    // A symbolless generated key still has a known printable US character. A real backend
    // symbol identifies the correlated text even when its layout differs from the US table.
    internal static string? OrdinaryKeyText(Key key, KeyModifiers modifiers, string? symbol)
    {
        int code = GetUsLayoutCharacter(key, (modifiers & KeyModifiers.Shift) != 0);
        if (code < 0) return null;
        return TryKeyScalar(symbol, out _) ? symbol : ((char)code).ToString();
    }

    private static bool IsPhysicalKeypadKey(PhysicalKey physical)
        => physical is >= PhysicalKey.NumPad0 and <= PhysicalKey.NumPad9
            or PhysicalKey.NumPadAdd or PhysicalKey.NumPadClear or PhysicalKey.NumPadComma
            or PhysicalKey.NumPadDecimal or PhysicalKey.NumPadDivide or PhysicalKey.NumPadEnter
            or PhysicalKey.NumPadEqual or PhysicalKey.NumPadMultiply or PhysicalKey.NumPadParenLeft
            or PhysicalKey.NumPadParenRight or PhysicalKey.NumPadSubtract;

    /// <summary>
    /// Xlib's XLookupString Control translation: true when Ctrl+<paramref name="code"/>
    /// produces a control character, i.e. the "special cases built into the X11 library" that
    /// modifyOtherKeys level 1 leaves alone.
    /// </summary>
    private static bool IsX11ControlAlias(int code)
        => code is >= '@' and < 0x7F or ' ' or '/' or >= '2' and <= '8';

    /// <summary>The US-layout character for a printable key, with Shift applied, or -1.</summary>
    private static int GetUsLayoutCharacter(Key key, bool shift)
    {
        if (key is >= Key.A and <= Key.Z) return (shift ? 'A' : 'a') + (key - Key.A);
        if (key is >= Key.D0 and <= Key.D9) return shift ? ")!@#$%^&*("[key - Key.D0] : '0' + (key - Key.D0);
        return key switch
        {
            Key.Space => ' ',
            Key.OemTilde => shift ? '~' : '`',
            Key.OemMinus => shift ? '_' : '-',
            Key.OemPlus => shift ? '+' : '=',
            Key.OemOpenBrackets => shift ? '{' : '[',
            Key.OemCloseBrackets => shift ? '}' : ']',
            Key.OemPipe or Key.OemBackslash => shift ? '|' : '\\',
            Key.OemSemicolon => shift ? ':' : ';',
            Key.OemQuotes => shift ? '"' : '\'',
            Key.OemComma => shift ? '<' : ',',
            Key.OemPeriod => shift ? '>' : '.',
            Key.OemQuestion => shift ? '?' : '/',
            _ => -1
        };
    }

    private static string FormatModifyOtherKey(int code, int modifierParameter)
        => string.Concat("\x1b[27;", modifierParameter.ToString(CultureInfo.InvariantCulture), ";",
            code.ToString(CultureInfo.InvariantCulture), "~");
}
