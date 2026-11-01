using System.Buffers;
using System.Globalization;
using System.Text;
using Avalonia.Input;

namespace Ntilde.Shell;

internal static partial class TerminalInputModeEncoder
{
    /// <summary>Flag 16 has defined text semantics only together with all-key reporting.</summary>
    internal static bool ReportsKittyAssociatedText(int flags)
        => (flags & (Ntilde.VT.KittyKeyboardState.FlagReportAllKeys | Ntilde.VT.KittyKeyboardState.FlagReportAssociatedText))
            == (Ntilde.VT.KittyKeyboardState.FlagReportAllKeys | Ntilde.VT.KittyKeyboardState.FlagReportAssociatedText);

    /// <summary>Only text-producing keys wait for the authoritative TextInput callback.</summary>
    internal static bool CanProduceKittyAssociatedText(Key key, KeyModifiers modifiers, string? symbol)
    {
        if (IsKittyControlKey(key) || (modifiers & KeyModifiers.Meta) != 0) return false;
        if ((modifiers & KeyModifiers.Control) != 0 && (modifiers & KeyModifiers.Alt) == 0) return false;
        return MayProduceTextInput(key, symbol);
    }

    /// <summary>
    /// Appends actual generated text as invariant Unicode scalar values. Rejects the entire
    /// payload on malformed UTF-16 or C0/C1 controls; combining marks and format scalars are text.
    /// The caller supplies a press/repeat CSI-u frame, never a release or a legacy final byte.
    /// </summary>
    internal static string? AppendKittyAssociatedText(string frame, string text)
    {
        if (text.Length == 0 || !frame.StartsWith("\x1b[", StringComparison.Ordinal) || !frame.EndsWith('u')) return null;
        var result = new StringBuilder(frame[..^1]);
        if (!frame.Contains(';')) result.Append(";1");
        result.Append(';');
        Span<char> digits = stackalloc char[10];
        bool first = true;
        for (int index = 0; index < text.Length;)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(index), out Rune rune, out int consumed) != OperationStatus.Done || Rune.IsControl(rune)) return null;
            if (!first) result.Append(':');
            rune.Value.TryFormat(digits, out int written, provider: CultureInfo.InvariantCulture);
            result.Append(digits[..written]);
            first = false;
            index += consumed;
        }
        return result.Append('u').ToString();
    }
}
