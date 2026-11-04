using Avalonia.Input;
using Ntilde.VT;

namespace Ntilde.Shell;

internal static partial class TerminalInputModeEncoder
{
    /// <summary>
    /// Unmodified VT100/VT220 auxiliary-keypad frames. Logical navigation keys
    /// keep their PC-style mapping; no unavailable native NumLock bit is guessed.
    /// Modified keys retain existing legacy/Kitty routing.
    /// </summary>
    internal static string? EncodeApplicationKeypad(Key key, KeyModifiers modifiers, PhysicalKey physical, ModeState? modes)
    {
        if (modes?.IsApplicationKeypad != true || modifiers != KeyModifiers.None) return null;
        char final = key switch
        {
            >= Key.NumPad0 and <= Key.NumPad9 => (char)('p' + (key - Key.NumPad0)),
            Key.Multiply => 'j',
            Key.Add => 'k',
            Key.Separator => 'l',
            Key.Subtract => 'm',
            Key.Decimal when physical == PhysicalKey.NumPadComma => 'l',
            Key.Decimal => 'n',
            Key.Divide => 'o',
            Key.Enter when physical == PhysicalKey.NumPadEnter => 'M',
            Key.OemComma when physical == PhysicalKey.NumPadComma => 'l',
            _ => '\0'
        };
        return final == '\0' ? null : "\x1bO" + final;
    }
}
