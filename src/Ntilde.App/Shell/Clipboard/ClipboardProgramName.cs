using System.Text;

namespace Ntilde.Shell.Clipboard;

internal static class ClipboardProgramName
{
    public static string Display(string name)
    {
        var text = new StringBuilder();
        foreach (var rune in name.EnumerateRunes())
            if (!Rune.IsControl(rune) && Rune.GetUnicodeCategory(rune) != System.Globalization.UnicodeCategory.Format)
                text.Append(rune.ToString());
        return text.ToString();
    }

    public static bool CanRemember(string displayName) => displayName.EnumerateRunes().Any(rune =>
        !IsDefaultIgnorable(rune.Value) && rune.Value != 0x2800 &&
        (Rune.IsLetterOrDigit(rune) || Rune.IsPunctuation(rune) || Rune.IsSymbol(rune)));

    // Unicode 17 Default_Ignorable_Code_Point, including non-format fillers and
    // supplementary variation selectors. Keep shaping marks in the displayed
    // name, but require a discernible base before offering remembered consent.
    // https://www.unicode.org/Public/17.0.0/ucd/DerivedCoreProperties.txt
    private static bool IsDefaultIgnorable(int value) => value is
        0x00AD or 0x034F or 0x061C or >= 0x115F and <= 0x1160 or
        >= 0x17B4 and <= 0x17B5 or >= 0x180B and <= 0x180F or
        >= 0x200B and <= 0x200F or >= 0x202A and <= 0x202E or
        >= 0x2060 and <= 0x206F or 0x3164 or >= 0xFE00 and <= 0xFE0F or
        0xFEFF or 0xFFA0 or >= 0xFFF0 and <= 0xFFF8 or
        >= 0x1BCA0 and <= 0x1BCA3 or >= 0x1D173 and <= 0x1D17A or
        >= 0xE0000 and <= 0xE0FFF;
}
