using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Ntilde.VT;

/// <summary>OSC 66 metadata. Fractional sizing changes ink, never occupied grid geometry.</summary>
public readonly record struct TextSizingOptions(int Scale, int Width, int Numerator, int Denominator,
    int VerticalAlignment, int HorizontalAlignment)
{
    internal bool IsValid => Scale is >= 1 and <= 7 && Width is >= 0 and <= 7
        && Numerator is >= 0 and <= 15 && Denominator is >= 0 and <= 15
        && (Denominator == 0 || Denominator > Numerator)
        && VerticalAlignment is >= 0 and <= 2 && HorizontalAlignment is >= 0 and <= 2;

    [JsonIgnore]
    public float Fraction => Numerator > 0 && Denominator > Numerator ? (float)Numerator / Denominator : 1f;

    internal static bool TryParse(ReadOnlySpan<char> metadata, out TextSizingOptions options)
    {
        options = default;
        if (metadata.Length > 256) return false;
        Span<int> values = stackalloc int[] { 1, 0, 0, 0, 0, 0 };
        foreach (Range range in metadata.Split(':'))
            if (!ReadField(metadata[range], values)) return false;
        options = new(values[0], values[1], values[2], values[3], values[4], values[5]);
        return options.IsValid;
    }

    private static bool ReadField(ReadOnlySpan<char> field, Span<int> values)
    {
        if (field.IsEmpty) return true;
        int separator = field.IndexOf('=');
        if (separator <= 0) return false;
        if (separator != 1) return true;
        int index = field[0] switch { 's' => 0, 'w' => 1, 'n' => 2, 'd' => 3, 'v' => 4, 'h' => 5, _ => -1 };
        return index < 0 || int.TryParse(field[2..], NumberStyles.None, CultureInfo.InvariantCulture, out values[index]);
    }

    internal static bool IsSafeText(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return false;
        int bytes = 0;
        while (!text.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(text, out Rune rune, out int consumed) != OperationStatus.Done || Rune.IsControl(rune)) return false;
            bytes += rune.Utf8SequenceLength;
            if (bytes > 4096) return false;
            text = text[consumed..];
        }
        return true;
    }
}

/// <summary>Immutable text shared by all occupied cells and by immutable render snapshots.</summary>
public sealed class TextSizingBlock
{
    public string Text { get; }
    public TextSizingOptions Options { get; }
    public int Width { get; }
    public int Height => Options.Scale;
    public int BaseWidth => Width / Options.Scale;
    internal long StorageCost => 128L + Text.Length * 2L + Width * Height * 64L + Height * 128L;

    internal TextSizingBlock(string text, TextSizingOptions options, int baseWidth)
    {
        if (!options.IsValid) throw new ArgumentOutOfRangeException(nameof(options));
        if (baseWidth is < 1 or > 7) throw new ArgumentOutOfRangeException(nameof(baseWidth));
        if (!TextSizingOptions.IsSafeText(text)) throw new ArgumentException("Invalid sized-text payload.", nameof(text));
        Text = text;
        Options = options;
        Width = baseWidth * options.Scale;
    }
}

/// <summary>A piece of a block, positioned relative to its top-left grid cell.</summary>
public sealed record CellTextSizing
{
    public TextSizingBlock Block { get; }
    public int ColumnOffset { get; }
    public int RowOffset { get; }
    public bool IsAnchor => ColumnOffset == 0 && RowOffset == 0;

    internal CellTextSizing(TextSizingBlock block, int columnOffset, int rowOffset)
    {
        ArgumentNullException.ThrowIfNull(block);
        if ((uint)columnOffset >= (uint)block.Width) throw new ArgumentOutOfRangeException(nameof(columnOffset));
        if ((uint)rowOffset >= (uint)block.Height) throw new ArgumentOutOfRangeException(nameof(rowOffset));
        Block = block;
        ColumnOffset = columnOffset;
        RowOffset = rowOffset;
    }
}
