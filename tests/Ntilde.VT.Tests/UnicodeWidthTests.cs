using System.Text;
using Ntilde.VT;

namespace Ntilde.VT.Tests;

/// <summary>
/// Cell widths must agree with what applications compute, or every line containing a disagreeing
/// character puts the cursor a column away from where the application believes it is, and TUIs
/// misdraw from that point on. Shells, readline, ncurses and the rest measure with wcwidth, whose
/// double-width set is Unicode's East_Asian_Width W and F (UAX #11), so that is the reference here:
/// the rune-level expectations below are what glibc, Node and Rust report. The grapheme-level ones
/// (VS16 promotion, flags, ZWJ sequences) are this terminal's own rules, re-checked on the new table.
///
/// The rune table used to be nine hand-written ranges. They missed the BMP emoji that are EAW=W
/// (the ones exercised first below), and a blanket U+1F000..U+1FBFF range made every symbol in that
/// span wide, including the many that are narrow.
/// </summary>
public class UnicodeWidthTests
{
    private static string Str(int codePoint) => char.ConvertFromUtf32(codePoint);

    [Theory]
    [InlineData(0x2705)] // WHITE HEAVY CHECK MARK
    [InlineData(0x274C)] // CROSS MARK
    [InlineData(0x26A1)] // HIGH VOLTAGE SIGN
    [InlineData(0x2615)] // HOT BEVERAGE
    [InlineData(0x2B50)] // WHITE MEDIUM STAR
    [InlineData(0x231B)] // HOURGLASS
    public void BmpEmojiWithDefaultEmojiPresentation_AreTwoCells(int codePoint)
    {
        Assert.Equal(2, UnicodeWidth.GetRuneWidth(new Rune(codePoint)));
        Assert.Equal(2, UnicodeWidth.GetGraphemeWidth(Str(codePoint)));
    }

    [Theory]
    [InlineData(0x1FB00)] // BLOCK SEXTANT-1 (Symbols for Legacy Computing)
    [InlineData(0x1F321)] // THERMOMETER - a text-presentation pictograph
    [InlineData(0x1F700)] // ALCHEMICAL SYMBOL FOR QUINTESSENCE
    [InlineData(0x1F800)] // LEFTWARDS ARROW WITH SMALL TRIANGLE ARROWHEAD
    [InlineData(0x1FA00)] // NEUTRAL CHESS KING
    [InlineData(0x1F000)] // MAHJONG TILE EAST WIND (only the red dragon, U+1F004, is wide)
    public void NarrowSymbolsInsideTheOldBlanketEmojiRange_AreOneCell(int codePoint)
    {
        Assert.Equal(1, UnicodeWidth.GetRuneWidth(new Rune(codePoint)));
        Assert.Equal(1, UnicodeWidth.GetGraphemeWidth(Str(codePoint)));
    }

    [Theory]
    [InlineData(0xFF61)] // HALFWIDTH IDEOGRAPHIC FULL STOP
    [InlineData(0xFF71)] // HALFWIDTH KATAKANA LETTER A
    [InlineData(0xFFA1)] // HALFWIDTH HANGUL LETTER KIYEOK
    [InlineData(0xFFE8)] // HALFWIDTH FORMS LIGHT VERTICAL
    public void HalfwidthForms_AreOneCell(int codePoint)
    {
        Assert.Equal(1, UnicodeWidth.GetRuneWidth(new Rune(codePoint)));
    }

    [Theory]
    [InlineData(0x1100)]  // HANGUL CHOSEONG KIYEOK - the lowest wide code point
    [InlineData(0x3000)]  // IDEOGRAPHIC SPACE (F)
    [InlineData(0x3042)]  // HIRAGANA LETTER A
    [InlineData(0x4E2D)]  // CJK UNIFIED IDEOGRAPH-4E2D
    [InlineData(0xAC00)]  // HANGUL SYLLABLE GA
    [InlineData(0xD7A3)]  // HANGUL SYLLABLE HIH
    [InlineData(0xA960)]  // HANGUL CHOSEONG TIKEUT-MIEUM (Jamo Extended-A, missed by the old list)
    [InlineData(0xFF21)]  // FULLWIDTH LATIN CAPITAL LETTER A (F)
    [InlineData(0xFFE0)]  // FULLWIDTH CENT SIGN (F)
    [InlineData(0x1F004)] // MAHJONG TILE RED DRAGON
    [InlineData(0x1F600)] // GRINNING FACE
    [InlineData(0x20000)] // CJK UNIFIED IDEOGRAPH-20000
    [InlineData(0x3FFFD)] // unassigned, but Plane 3 defaults to W so future ideographs are wide
    public void EastAsianWideAndFullwidth_AreTwoCells(int codePoint)
    {
        Assert.Equal(2, UnicodeWidth.GetRuneWidth(new Rune(codePoint)));
    }

    [Theory]
    [InlineData(0x0020)] // SPACE
    [InlineData(0x0041)] // A
    [InlineData(0x007E)] // ~
    [InlineData(0x00E9)] // e WITH ACUTE
    [InlineData(0x0416)] // CYRILLIC CAPITAL LETTER ZHE
    [InlineData(0x10FF)] // just below the lowest wide code point
    [InlineData(0x303F)] // IDEOGRAPHIC HALF FILL SPACE - narrow inside the CJK Symbols block
    [InlineData(0x2764)] // HEAVY BLACK HEART - text presentation by default
    [InlineData(0x1F1FA)] // REGIONAL INDICATOR SYMBOL LETTER U, as a lone rune
    public void NarrowCodePoints_AreOneCell(int codePoint)
    {
        Assert.Equal(1, UnicodeWidth.GetRuneWidth(new Rune(codePoint)));
    }

    [Theory]
    [InlineData(0x0301)] // COMBINING ACUTE ACCENT
    [InlineData(0x20DD)] // COMBINING ENCLOSING CIRCLE
    [InlineData(0x302A)] // IDEOGRAPHIC LEVEL TONE MARK - a mark that is also EAW=W
    [InlineData(0x3099)] // COMBINING KATAKANA-HIRAGANA VOICED SOUND MARK - likewise
    [InlineData(0xFE0F)] // VARIATION SELECTOR-16
    [InlineData(0x1F3FD)] // EMOJI MODIFIER FITZPATRICK TYPE-4 - EAW=W, but only ever a modifier
    [InlineData(0x0007)] // BEL
    [InlineData(0x009B)] // CSI (C1)
    public void CombiningMarksSelectorsAndControls_AreZeroCells(int codePoint)
    {
        Assert.Equal(0, UnicodeWidth.GetRuneWidth(new Rune(codePoint)));
    }

    // ---- Grapheme-level rules, re-checked on top of the rune table ----

    [Fact]
    public void TextDefaultPictographWithEmojiSelector_IsTwoCells()
    {
        // Heart: Dingbats. Thermometer and white flag: text-default pictographs from the SMP symbol
        // blocks, which are narrow as bare runes now, so VS16 is what makes them wide.
        Assert.Equal(2, UnicodeWidth.GetGraphemeWidth("\u2764\uFE0F"));
        Assert.Equal(2, UnicodeWidth.GetGraphemeWidth("\U0001F321\uFE0F"));
        Assert.Equal(2, UnicodeWidth.GetGraphemeWidth("\U0001F3F3\uFE0F"));
    }

    [Fact]
    public void TextPresentationSelector_KeepsTheBaseRuneWidth()
    {
        // VS15 never widens, and it does not narrow a wide base either: wcwidth gives the selector
        // zero width, so an application measures HOT BEVERAGE + VS15 as 2 and so must we.
        Assert.Equal(1, UnicodeWidth.GetGraphemeWidth("\u2764\uFE0E"));
        Assert.Equal(1, UnicodeWidth.GetGraphemeWidth("\U0001F321\uFE0E"));
        Assert.Equal(2, UnicodeWidth.GetGraphemeWidth("\u2615\uFE0E"));
    }

    [Fact]
    public void WideEmojiWithEmojiSelector_StaysTwoCells()
    {
        Assert.Equal(2, UnicodeWidth.GetGraphemeWidth("\u2B50\uFE0F"));
        Assert.Equal(2, UnicodeWidth.GetGraphemeWidth("\u2705\uFE0F"));
    }

    [Fact]
    public void FlagsModifiersAndZwjSequences_AreTwoCells()
    {
        // Regional indicators are EAW=N as lone runes; the pair logic is what makes a flag 2 cells.
        Assert.Equal(2, UnicodeWidth.GetGraphemeWidth("\U0001F1FA\U0001F1F8"));
        Assert.Equal(2, UnicodeWidth.GetGraphemeWidth("\U0001F1FA"));
        Assert.Equal(2, UnicodeWidth.GetGraphemeWidth("\U0001F44D\U0001F3FD"));
        Assert.Equal(2, UnicodeWidth.GetGraphemeWidth("\U0001F468\u200D\U0001F469\u200D\U0001F467"));
        // Modifier on a text-default base (SLEUTH OR SPY, now narrow alone).
        Assert.Equal(2, UnicodeWidth.GetGraphemeWidth("\U0001F575\U0001F3FD"));
        // ZWJ sequences whose parts are all text-default pictographs (EYE + LEFT SPEECH BUBBLE) and
        // one built on a text-default base (WHITE FLAG + TRANSGENDER SYMBOL).
        Assert.Equal(2, UnicodeWidth.GetGraphemeWidth("\U0001F441\uFE0F\u200D\U0001F5E8\uFE0F"));
        Assert.Equal(2, UnicodeWidth.GetGraphemeWidth("\U0001F3F3\uFE0F\u200D\u26A7\uFE0F"));
    }

    [Fact]
    public void CombiningMarkOnWideBase_DoesNotAddWidth()
    {
        Assert.Equal(2, UnicodeWidth.GetGraphemeWidth("\u304B\u3099")); // KA + voiced mark
        Assert.Equal(1, UnicodeWidth.GetGraphemeWidth("e\u0301"));
    }

    // ---- The generated table ----

    [Fact]
    public void GeneratedTable_IsSortedDisjointAndMerged()
    {
        // The binary search is only exact if these hold; the generator produces them, and this
        // catches a hand edit or a generator change that breaks them.
        ReadOnlySpan<int> ranges = UnicodeWidth.EastAsianWideRanges;
        Assert.True(ranges.Length > 0 && ranges.Length % 2 == 0);
        Assert.Equal(UnicodeWidth.EastAsianWideMin, ranges[0]);
        Assert.Equal(UnicodeWidth.EastAsianWideMax, ranges[^1]);

        for (int i = 0; i < ranges.Length; i += 2)
        {
            Assert.True(ranges[i] <= ranges[i + 1], $"range {i / 2} is inverted");
            if (i + 2 < ranges.Length)
            {
                // Strictly greater than end + 1: adjacent ranges would have been merged.
                Assert.True(ranges[i + 2] > ranges[i + 1] + 1, $"range {i / 2} overlaps or touches the next");
            }
        }
    }

    [Fact]
    public void BinarySearch_AgreesWithTheTableAtEveryRangeBoundary()
    {
        // Every edge of every range, from both sides, checked against a linear scan. An off-by-one
        // in the search shows up exactly at these points.
        int[] ranges = UnicodeWidth.EastAsianWideRanges.ToArray();

        bool LinearContains(int cp)
        {
            for (int i = 0; i < ranges.Length; i += 2)
            {
                if (cp >= ranges[i] && cp <= ranges[i + 1]) return true;
            }
            return false;
        }

        for (int i = 0; i < ranges.Length; i += 2)
        {
            foreach (int cp in new[] { ranges[i] - 1, ranges[i], ranges[i + 1], ranges[i + 1] + 1 })
            {
                // The code point rides along so a failure names it.
                Assert.Equal((cp, LinearContains(cp)), (cp, UnicodeWidth.IsEastAsianWide(cp)));
            }
        }
    }

    [Fact]
    public void TableLookup_DoesNotAllocate()
    {
        // GetRuneWidth runs on the write path for every non-Latin rune. The table is a static array
        // for this reason: the tempting `ReadOnlySpan<int> X => new int[] { ... }` property is only
        // allocation-free when the JIT optimizes, and allocates on every access in Debug builds,
        // which is what the test suite runs.
        int[] probes = [0x4E2D, 0xAC00, 0x2705, 0x1F600, 0x1FB00, 0xFF61, 0x20000];
        foreach (int cp in probes) UnicodeWidth.GetRuneWidth(new Rune(cp));

        long before = GC.GetAllocatedBytesForCurrentThread();
        int sum = 0;
        for (int i = 0; i < 10_000; i++)
        {
            foreach (int cp in probes) sum += UnicodeWidth.GetRuneWidth(new Rune(cp));
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(10_000 * 12, sum); // five wide and two narrow probes per pass
        Assert.Equal(0, allocated);
    }

    // ---- The user-visible symptom: the cursor column after writing ----

    [Fact]
    public void WritingABmpEmoji_AdvancesTheCursorTwoColumns()
    {
        var buffer = new TerminalBuffer(20, 3);
        var parser = new AnsiParser(buffer);

        parser.Process("\u2705 ok");

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.True(buffer.GetCell(0, 0).IsWide);
            Assert.True(buffer.GetCell(1, 0).IsWideContinuation);
            Assert.Equal(" ", buffer.GetGrapheme(2, 0));
            Assert.Equal("o", buffer.GetGrapheme(3, 0));
            Assert.Equal(5, buffer.CursorCol);
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void WritingANarrowLegacyComputingSymbol_AdvancesTheCursorOneColumn()
    {
        var buffer = new TerminalBuffer(20, 3);
        var parser = new AnsiParser(buffer);

        parser.Process("\U0001FB00x");

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.False(buffer.GetCell(0, 0).IsWide);
            Assert.Equal("x", buffer.GetGrapheme(1, 0));
            Assert.Equal(2, buffer.CursorCol);
        }
        finally { buffer.Lock.ExitReadLock(); }
    }
}
