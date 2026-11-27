using HarfBuzzSharp;
using SkiaSharp;

namespace Ntilde.Shell;

public sealed partial class TerminalDrawOperation
{
    /// <summary>Shapes an ASCII run on the primary face and positions clusters on the terminal grid.</summary>
    private bool DrawAsciiLigatures(SKCanvas canvas, string text, int startCol, float baseline,
        SKFont font, SKPaint paint, SKTypeface primary)
    {
        if (!primary.IsFixedPitch || !HasAdjacentInk(text))
            return false;

        using var buffer = new HarfBuzzSharp.Buffer();
        buffer.AddUtf8(text);
        buffer.Direction = Direction.LeftToRight;
        buffer.Script = Script.Latin;
        var shaped = GetOrCreateShapingResources(primary).Shaper.Shape(buffer, font);
        if (shaped.Codepoints.Length == 0 || System.Array.IndexOf(shaped.Codepoints, 0u) >= 0
            || !HasSubstitutedGlyph(text, font, shaped.Codepoints, shaped.Clusters))
            return false;

        // Glyph positions within a cluster come from the font. Each cluster starts at its
        // logical column, so natural advances and rounding cannot displace later cells.
        var positions = buffer.GlyphPositions;
        using var builder = new SKTextBlobBuilder();
        var run = builder.AllocatePositionedRun(font, positions.Length);
        var glyphs = run.Glyphs;
        var points = run.Positions;
        float fontScale = font.Size / 512f; // SKShaper's HarfBuzz coordinate scale.
        uint lastCluster = uint.MaxValue;
        float advance = 0;
        for (int i = 0; i < positions.Length; i++)
        {
            uint cluster = shaped.Clusters[i];
            if (cluster != lastCluster)
                advance = 0;
            var position = positions[i];
            float x = FromDevicePx(_pixelGrid.XForCol(startCol + (int)cluster));
            glyphs[i] = (ushort)shaped.Codepoints[i];
            points[i] = new SKPoint(x + (advance + position.XOffset) * fontScale * font.ScaleX,
                -position.YOffset * fontScale);
            advance += position.XAdvance;
            lastCluster = cluster;
        }

        FlushBatches(canvas);
        using var blob = builder.Build();
        canvas.DrawText(blob, 0, baseline, paint);
        IncrementShapedTextRun();
        IncrementTextDrawCall();
        return true;
    }

    private static bool HasSubstitutedGlyph(string text, SKFont font, uint[] glyphs, uint[] clusters)
    {
        if (glyphs.Length != text.Length)
            return true;
        for (int i = 0; i < glyphs.Length; i++)
        {
            if (clusters[i] != (uint)i || glyphs[i] != font.GetGlyph(text[i]))
                return true;
        }
        return false;
    }

    private static bool HasAdjacentInk(string text)
    {
        for (int i = 1; i < text.Length; i++)
        {
            if (text[i - 1] != ' ' && text[i] != ' ')
                return true;
        }
        return false;
    }
}
