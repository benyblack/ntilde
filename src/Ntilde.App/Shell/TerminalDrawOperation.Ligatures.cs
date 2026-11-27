using HarfBuzzSharp;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Ntilde.Shell;

public sealed partial class TerminalDrawOperation
{
    private readonly record struct LigatureRow(int StartCol, float Top, float Height, float Baseline);

    /// <summary>Draws substituted clusters and clips the following ordinary draw to the other cells.</summary>
    private bool DrawAsciiLigatures(SKCanvas canvas, string text, LigatureRow row,
        SKFont font, SKPaint paint, SKTypeface primary)
    {
        if (!primary.IsFixedPitch || !HasAdjacentInk(text))
            return false;

        using var buffer = new HarfBuzzSharp.Buffer();
        buffer.AddUtf8(text);
        buffer.Direction = Direction.LeftToRight;
        buffer.Script = Script.Latin;
        var shaped = GetOrCreateShapingResources(primary).Shaper.Shape(buffer, font);
        if (shaped.Codepoints.Length == 0 || System.Array.IndexOf(shaped.Codepoints, 0u) >= 0)
            return false;

        using var substitutions = SubstitutedCells(shaped, text, font, row);
        if (substitutions.IsEmpty)
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
            float x = FromDevicePx(_pixelGrid.XForCol(row.StartCol + (int)cluster));
            glyphs[i] = (ushort)shaped.Codepoints[i];
            points[i] = new SKPoint(x + (advance + position.XOffset) * fontScale * font.ScaleX,
                -position.YOffset * fontScale);
            advance += position.XAdvance;
            lastCluster = cluster;
        }

        FlushBatches(canvas);
        using var blob = builder.Build();
        canvas.Save();
        canvas.ClipPath(substitutions);
        canvas.DrawText(blob, 0, row.Baseline, paint);
        canvas.Restore();

        // Preserve the existing raster path for neighboring cells, including its
        // antialiasing and hinting. The caller restores this clip after flushing.
        canvas.Save();
        canvas.ClipPath(substitutions, SKClipOperation.Difference);
        IncrementShapedTextRun();
        IncrementTextDrawCall();
        return true;
    }

    private SKPath SubstitutedCells(SKShaper.Result shaped, string text, SKFont font, LigatureRow row)
    {
        var substitutions = new SKPath();
        for (int i = 0; i < shaped.Clusters.Length;)
        {
            int start = (int)shaped.Clusters[i];
            int next = i + 1;
            while (next < shaped.Clusters.Length && shaped.Clusters[next] == shaped.Clusters[i])
                next++;

            int end = next < shaped.Clusters.Length ? (int)shaped.Clusters[next] : text.Length;
            if (end != start + 1 || next != i + 1 || shaped.Codepoints[i] != font.GetGlyph(text[start]))
            {
                float left = FromDevicePx(_pixelGrid.XForCol(row.StartCol + start));
                float right = FromDevicePx(_pixelGrid.XForCol(row.StartCol + end));
                substitutions.AddRect(new SKRect(left, row.Top, right, row.Top + row.Height));
            }

            i = next;
        }
        return substitutions;
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
