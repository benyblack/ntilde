using System;
using System.Collections.Generic;

namespace Ntilde.VT;

/// <summary>A virtual placement (<c>U=1</c>): the grid a Unicode-placeholder image is fitted into. Invisible by itself.</summary>
/// <param name="PlacementId">The <c>p=</c> id, 0 when the client gave none.</param>
/// <param name="Columns">The grid width in cells.</param>
/// <param name="Rows">The grid height in cells.</param>
internal readonly record struct KittyVirtualPlacement(uint PlacementId, int Columns, int Rows);

/// <summary>
/// The kitty graphics Unicode placeholder: the private-use character U+10EEEE whose foreground colour
/// names an image, whose underline colour names a virtual placement and whose combining marks name the
/// cell's row and column in that placement's grid. Everything here is a pure function of a cell's text
/// and colours: the buffer draws no state of its own for a visible tile, so scrolling, erasing, reflow
/// and copy move or drop the image together with the text.
/// </summary>
internal static class KittyPlaceholders
{
    /// <summary>The placeholder character.</summary>
    internal const int Codepoint = 0x10EEEE;

    // The two UTF-16 units of U+10EEEE.
    private const char HighSurrogate = '\uDBFB';
    private const char LowSurrogate = '\uDEEE';

    /// <summary>
    /// Kitty's <c>rowcolumn-diacritics.txt</c>: the combining marks that stand for the numbers 0, 1, 2 ... in
    /// ascending code point order (297 of them, combining class 230 without a decomposition).
    /// </summary>
    private static readonly int[] Diacritics =
    [
        0x0305, 0x030D, 0x030E, 0x0310, 0x0312, 0x033D, 0x033E, 0x033F,
        0x0346, 0x034A, 0x034B, 0x034C, 0x0350, 0x0351, 0x0352, 0x0357,
        0x035B, 0x0363, 0x0364, 0x0365, 0x0366, 0x0367, 0x0368, 0x0369,
        0x036A, 0x036B, 0x036C, 0x036D, 0x036E, 0x036F, 0x0483, 0x0484,
        0x0485, 0x0486, 0x0487, 0x0592, 0x0593, 0x0594, 0x0595, 0x0597,
        0x0598, 0x0599, 0x059C, 0x059D, 0x059E, 0x059F, 0x05A0, 0x05A1,
        0x05A8, 0x05A9, 0x05AB, 0x05AC, 0x05AF, 0x05C4, 0x0610, 0x0611,
        0x0612, 0x0613, 0x0614, 0x0615, 0x0616, 0x0617, 0x0657, 0x0658,
        0x0659, 0x065A, 0x065B, 0x065D, 0x065E, 0x06D6, 0x06D7, 0x06D8,
        0x06D9, 0x06DA, 0x06DB, 0x06DC, 0x06DF, 0x06E0, 0x06E1, 0x06E2,
        0x06E4, 0x06E7, 0x06E8, 0x06EB, 0x06EC, 0x0730, 0x0732, 0x0733,
        0x0735, 0x0736, 0x073A, 0x073D, 0x073F, 0x0740, 0x0741, 0x0743,
        0x0745, 0x0747, 0x0749, 0x074A, 0x07EB, 0x07EC, 0x07ED, 0x07EE,
        0x07EF, 0x07F0, 0x07F1, 0x07F3, 0x0816, 0x0817, 0x0818, 0x0819,
        0x081B, 0x081C, 0x081D, 0x081E, 0x081F, 0x0820, 0x0821, 0x0822,
        0x0823, 0x0825, 0x0826, 0x0827, 0x0829, 0x082A, 0x082B, 0x082C,
        0x082D, 0x0951, 0x0953, 0x0954, 0x0F82, 0x0F83, 0x0F86, 0x0F87,
        0x135D, 0x135E, 0x135F, 0x17DD, 0x193A, 0x1A17, 0x1A75, 0x1A76,
        0x1A77, 0x1A78, 0x1A79, 0x1A7A, 0x1A7B, 0x1A7C, 0x1B6B, 0x1B6D,
        0x1B6E, 0x1B6F, 0x1B70, 0x1B71, 0x1B72, 0x1B73, 0x1CD0, 0x1CD1,
        0x1CD2, 0x1CDA, 0x1CDB, 0x1CE0, 0x1DC0, 0x1DC1, 0x1DC3, 0x1DC4,
        0x1DC5, 0x1DC6, 0x1DC7, 0x1DC8, 0x1DC9, 0x1DCB, 0x1DCC, 0x1DD1,
        0x1DD2, 0x1DD3, 0x1DD4, 0x1DD5, 0x1DD6, 0x1DD7, 0x1DD8, 0x1DD9,
        0x1DDA, 0x1DDB, 0x1DDC, 0x1DDD, 0x1DDE, 0x1DDF, 0x1DE0, 0x1DE1,
        0x1DE2, 0x1DE3, 0x1DE4, 0x1DE5, 0x1DE6, 0x1DFE, 0x20D0, 0x20D1,
        0x20D4, 0x20D5, 0x20D6, 0x20D7, 0x20DB, 0x20DC, 0x20E1, 0x20E7,
        0x20E9, 0x20F0, 0x2CEF, 0x2CF0, 0x2CF1, 0x2DE0, 0x2DE1, 0x2DE2,
        0x2DE3, 0x2DE4, 0x2DE5, 0x2DE6, 0x2DE7, 0x2DE8, 0x2DE9, 0x2DEA,
        0x2DEB, 0x2DEC, 0x2DED, 0x2DEE, 0x2DEF, 0x2DF0, 0x2DF1, 0x2DF2,
        0x2DF3, 0x2DF4, 0x2DF5, 0x2DF6, 0x2DF7, 0x2DF8, 0x2DF9, 0x2DFA,
        0x2DFB, 0x2DFC, 0x2DFD, 0x2DFE, 0x2DFF, 0xA66F, 0xA67C, 0xA67D,
        0xA6F0, 0xA6F1, 0xA8E0, 0xA8E1, 0xA8E2, 0xA8E3, 0xA8E4, 0xA8E5,
        0xA8E6, 0xA8E7, 0xA8E8, 0xA8E9, 0xA8EA, 0xA8EB, 0xA8EC, 0xA8ED,
        0xA8EE, 0xA8EF, 0xA8F0, 0xA8F1, 0xAAB0, 0xAAB2, 0xAAB3, 0xAAB7,
        0xAAB8, 0xAABE, 0xAABF, 0xAAC1, 0xFE20, 0xFE21, 0xFE22, 0xFE23,
        0xFE24, 0xFE25, 0xFE26, 0x10A0F, 0x10A38, 0x1D185, 0x1D186, 0x1D187,
        0x1D188, 0x1D189, 0x1D1AA, 0x1D1AB, 0x1D1AC, 0x1D1AD, 0x1D242, 0x1D243,
        0x1D244
    ];

    private static readonly Dictionary<int, int> NumberOf = BuildNumbers();

    /// <summary>How many numbers the diacritic table can express.</summary>
    internal static int DiacriticCount => Diacritics.Length;

    private static Dictionary<int, int> BuildNumbers()
    {
        var numbers = new Dictionary<int, int>(Diacritics.Length);
        for (int i = 0; i < Diacritics.Length; i++) numbers[Diacritics[i]] = i;
        return numbers;
    }

    /// <summary>The combining mark for <paramref name="number"/>, or -1 when it is outside the table.</summary>
    internal static int DiacriticFor(int number) => (uint)number < (uint)Diacritics.Length ? Diacritics[number] : -1;

    /// <summary>Whether a cell's text is a placeholder: it starts with U+10EEEE.</summary>
    internal static bool IsPlaceholder(string? text) =>
        text is { Length: >= 2 } && text[0] == HighSurrogate && text[1] == LowSurrogate;

    /// <summary>Whether a written grapheme is a placeholder, for the write path.</summary>
    internal static bool IsPlaceholder(ReadOnlySpan<char> text) =>
        text.Length >= 2 && text[0] == HighSurrogate && text[1] == LowSurrogate;

    /// <summary>
    /// The row, column and image-id high byte a placeholder's marks give. Only the first three marks
    /// from the table count, in that order; any other mark is ignored. A missing number is null.
    /// </summary>
    internal static (int? Row, int? Column, int? HighByte) ReadMarks(string text)
    {
        Span<int> numbers = stackalloc int[3];
        int found = 0;
        for (int i = 2; i < text.Length && found < 3;)
        {
            System.Text.Rune.DecodeFromUtf16(text.AsSpan(i), out System.Text.Rune rune, out int consumed);
            i += Math.Max(1, consumed);
            if (NumberOf.TryGetValue(rune.Value, out int number)) numbers[found++] = number;
        }
        return (found > 0 ? numbers[0] : null, found > 1 ? numbers[1] : null, found > 2 ? numbers[2] : null);
    }

    /// <summary>
    /// A colour as the protocol reads it: a palette index (8-bit id) or a 24-bit RGB value. The kind is
    /// not part of the identity, so <c>38;5;42</c> and <c>38;2;0;0;42</c> name the same number.
    /// </summary>
    internal static uint ColorNumber(bool isPalette, uint paletteIndex, uint rgb) => isPalette ? paletteIndex & 0xFF : rgb & 0xFFFFFF;

    /// <summary>One resolved placeholder cell.</summary>
    internal readonly record struct Cell(uint ImageId, uint PlacementId, int Row, int Column);

    /// <summary>What the cell to the left contributes to the inheritance rules.</summary>
    internal readonly record struct Previous(bool Valid, uint ForegroundNumber, uint UnderlineNumber, int Row, int Column, int HighByte);

    /// <summary>
    /// Resolves a placeholder cell, applying the protocol's left-to-right inheritance of a missing row,
    /// column and image-id high byte from <paramref name="previous"/> (the placeholder cell immediately
    /// to the left, with the same foreground and underline colours). With nothing to inherit from the
    /// missing numbers are 0, one of the heuristics the protocol leaves to the terminal.
    /// </summary>
    internal static Cell Resolve(string text, uint foregroundNumber, uint underlineNumber, Previous previous, out Previous next)
    {
        (int? row, int? column, int? highByte) = ReadMarks(text);
        bool sameColors = previous.Valid && previous.ForegroundNumber == foregroundNumber && previous.UnderlineNumber == underlineNumber;
        int resolvedRow = row ?? (sameColors ? previous.Row : 0);
        int resolvedColumn, resolvedHigh;
        if (column is { } given)
        {
            resolvedColumn = given;
            bool continues = sameColors && previous.Row == resolvedRow && previous.Column + 1 == given;
            resolvedHigh = highByte ?? (continues ? previous.HighByte : 0);
        }
        else
        {
            bool continues = sameColors && previous.Row == resolvedRow;
            resolvedColumn = continues ? previous.Column + 1 : 0;
            resolvedHigh = highByte ?? (continues ? previous.HighByte : 0);
        }
        uint imageId = foregroundNumber | ((uint)resolvedHigh << 24);
        next = new Previous(true, foregroundNumber, underlineNumber, resolvedRow, resolvedColumn, resolvedHigh);
        return new Cell(imageId, underlineNumber, resolvedRow, resolvedColumn);
    }
}
