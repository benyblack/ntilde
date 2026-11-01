using Avalonia.Input;

namespace Ntilde.Shell;

internal static partial class TerminalInputModeEncoder
{
    /// <summary>All-key mode uses layout identity independently of the optional alternate fields.</summary>
    internal static KittyKeyIdentity ResolveKittyAllKeyIdentity(Key key, KeyModifiers modifiers, PhysicalKey physical, string? symbol)
    {
        if (key is Key.ImeProcessed or Key.DeadCharProcessed) return default;
        var (number, final) = KittyAllKeypadKey(key, physical);
        if (final != '\0') return new(number, null, null);
        return ResolveKittyKeyIdentity(key, modifiers, physical, symbol, allowUnknownKey: true);
    }

    /// <summary>
    /// Flag 8 implies canonical key encoding, including recovery and modifier keys. Event types
    /// remain controlled by flag 2; flag 4 controls only optional shifted/base-layout fields.
    /// </summary>
    private static string? EncodeKittyAllKey(Key key, int bits, TerminalKeyEventKind kind, KittyKeyIdentity identity, bool alternates)
    {
        var (number, final) = KittyAllFunctionalKey(key);
        if (identity.Primary is >= 57399 and <= 57427)
        {
            int keypad = identity.Primary.Value;
            return FormatKittyEvent(keypad == 57427 ? 1 : keypad, bits, keypad == 57427 ? 'E' : 'u', kind);
        }
        if (final != '\0') return FormatKittyEvent(number, bits, final, kind);
        int primary = identity.Primary ?? GetUnshiftedCodepoint(key);
        if (primary < 0) return null; // No invented code for an unavailable host key identity.
        return FormatKittyEvent(primary, bits, 'u', kind, alternates ? identity.Format(primary, (bits & KittyShift) != 0) : null);
    }

    /// <summary>Uses physical position only to distinguish keypad keys that share a virtual key.</summary>
    private static (int Number, char Final) KittyAllKeypadKey(Key key, PhysicalKey physical)
    {
        if (physical == PhysicalKey.NumPadEnter && key == Key.Enter) return (57414, 'u');
        if (physical == PhysicalKey.NumPadEqual && key == Key.OemPlus) return (57415, 'u');
        if (physical == PhysicalKey.NumPadDecimal && key == Key.Delete) return (57426, 'u');
        if (physical == PhysicalKey.NumPadClear && key == Key.Clear) return (57427, 'E');
        if (physical == PhysicalKey.NumPadComma && key is Key.Separator or Key.Decimal or Key.OemComma) return (57416, 'u');
        if (physical is < PhysicalKey.NumPad0 or > PhysicalKey.NumPad9) return (0, '\0');
        return key switch
        {
            Key.Left => (57417, 'u'),
            Key.Right => (57418, 'u'),
            Key.Up => (57419, 'u'),
            Key.Down => (57420, 'u'),
            Key.PageUp => (57421, 'u'),
            Key.PageDown => (57422, 'u'),
            Key.Home => (57423, 'u'),
            Key.End => (57424, 'u'),
            Key.Insert => (57425, 'u'),
            Key.Delete => (57426, 'u'),
            Key.Clear => (57427, 'E'),
            _ => (0, '\0')
        };
    }

    /// <summary>Canonical codes for the modifier, keypad and media keys exposed by Avalonia.</summary>
    private static (int Number, char Final) KittyAllFunctionalKey(Key key) => key switch
    {
        Key.Enter => (13, 'u'),
        Key.Tab => (9, 'u'),
        Key.Back => (127, 'u'),
        Key.Escape => (27, 'u'),
        Key.LeftShift => (57441, 'u'),
        Key.LeftCtrl => (57442, 'u'),
        Key.LeftAlt => (57443, 'u'),
        Key.LWin => (57444, 'u'),
        Key.RightShift => (57447, 'u'),
        Key.RightCtrl => (57448, 'u'),
        Key.RightAlt => (57449, 'u'),
        Key.RWin => (57450, 'u'),
        >= Key.NumPad0 and <= Key.NumPad9 => (57399 + (key - Key.NumPad0), 'u'),
        Key.Decimal => (57409, 'u'),
        Key.Divide => (57410, 'u'),
        Key.Multiply => (57411, 'u'),
        Key.Subtract => (57412, 'u'),
        Key.Add => (57413, 'u'),
        Key.Separator => (57416, 'u'),
        Key.MediaPlayPause => (57430, 'u'),
        Key.MediaStop => (57432, 'u'),
        Key.MediaNextTrack => (57435, 'u'),
        Key.MediaPreviousTrack => (57436, 'u'),
        Key.VolumeDown => (57438, 'u'),
        Key.VolumeUp => (57439, 'u'),
        Key.VolumeMute => (57440, 'u'),
        _ => KittyFunctionalKey(key)
    };
}
