using System.Globalization;
using System.Text;
using Avalonia.Input;

namespace Ntilde.Shell;

/// <summary>Immutable key identity captured with the press, independent of later layout/modifier changes.</summary>
internal readonly record struct KittyKeyIdentity(int? Primary, int? Shifted, int? BaseLayout)
{
    /// <summary>Alternate fields are optional; shifted values are emitted only while Shift is held.</summary>
    public string? Format(int primary, bool shift)
    {
        int? shifted = shift && Shifted != primary ? Shifted : null;
        int? baseline = BaseLayout != primary ? BaseLayout : null;
        if (shifted == null && baseline == null) return null;
        var culture = CultureInfo.InvariantCulture;
        return string.Concat(primary.ToString(culture), ":", shifted?.ToString(culture),
            baseline.HasValue ? ":" + baseline.Value.ToString(culture) : "");
    }
}

internal static partial class TerminalInputModeEncoder
{
    /// <summary>
    /// Uses the framework's scalar key symbol and layout-independent physical position. Missing,
    /// control, combining, format, multi-scalar and malformed UTF-16 symbols retain the known key.
    /// Alt-produced symbols are not primary keys. Shifted punctuation retains the legacy
    /// logical primary: Avalonia does not expose its
    /// unshifted layout glyph. No native layout lookup or composition-state mutation occurs here.
    /// </summary>
    internal static KittyKeyIdentity ResolveKittyKeyIdentity(Key key, KeyModifiers modifiers, PhysicalKey physical, string? symbol, bool allowUnknownKey = false)
    {
        int primary = GetUnshiftedCodepoint(key);
        if (primary < 0)
        {
            if (!allowUnknownKey || (modifiers & KeyModifiers.Alt) != 0 || !TryKeyScalar(symbol, out Rune supplied)) return default;
            primary = Rune.ToLowerInvariant(supplied).Value;
        }
        int? shifted = null;
        if ((modifiers & KeyModifiers.Alt) == 0 && TryKeyScalar(symbol, out Rune rune))
        {
            bool shift = (modifiers & KeyModifiers.Shift) != 0;
            if (!shift || Rune.IsLetter(rune)) primary = Rune.ToLowerInvariant(rune).Value;
            if (shift && rune.Value != primary) shifted = rune.Value;
        }
        int? baseline = TryKeyScalar(physical.ToQwertyKeySymbol(), out Rune baseRune) ? baseRune.Value : null;
        return new(primary, shifted, baseline);
    }

    /// <summary>Accepts one standalone scalar; composition marks and format controls cannot identify a key.</summary>
    private static bool TryKeyScalar(string? value, out Rune rune)
    {
        rune = default;
        return value is { Length: > 0 and <= 2 } && Rune.TryGetRuneAt(value, 0, out rune)
            && rune.Utf16SequenceLength == value.Length && !Rune.IsControl(rune)
            && Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format);
    }
}
