using System;
using System.Linq;
using System.Security.Cryptography;
using SkiaSharp;
using Ntilde.Rendering;
using Ntilde.Shell.Theming;
using Ntilde.VT;

namespace Ntilde.Shell;

internal readonly record struct SnapshotSelectionRange(int StartRow, int StartCol, int EndRow, int EndCol);

/// <summary>Values from the exact snapshot and pixel grid used by one draw, never a later buffer read.</summary>
internal readonly record struct RenderedFrameInfo(
    RenderThemeSnapshot Theme,
    TerminalRenderColors DerivedColors,
    PixelGrid PixelGrid,
    int Rows,
    int Cols,
    int CursorRow,
    int CursorCol,
    CursorStyle CursorStyle,
    bool CursorVisible,
    bool CursorBlinkEnabled,
    SnapshotSelectionRange? SyntheticSelection = null,
    RenderedImageFrame? Images = null);

/// <summary>Detached observations; contains neither image handles nor source bytes.</summary>
internal readonly record struct RenderedImageInfo(Guid PlacementId, int Col, int ViewportRow,
    int WidthCells, int HeightCells, int ClipTopRows, int ClipBottomRows, int ClipRightColumns,
    bool PreserveAspectRatio, int? BitmapWidthPx, int? BitmapHeightPx, SixelPlacementProvenance? SixelPlacement);

internal sealed record RenderedImageFrame(bool Complete, int TotalPlacements, RenderedImageInfo[] Placements)
{
    internal static RenderedImageFrame From(System.Collections.Generic.List<RenderImageSnapshot> images, int displayStart)
    {
        if (images.Count > Ntilde.AgentHost.Contracts.AgentHostProtocol.MaxCaptureImagePlacements)
            return new(false, images.Count, []);
        var placements = new RenderedImageInfo[images.Count];
        for (int i = 0; i < images.Count; i++)
        {
            var image = images[i];
            placements[i] = new(image.PlacementId, image.CellX, image.CellY - displayStart,
                image.CellWidth, image.CellHeight, image.ClipTopRows, image.ClipBottomRows, image.ClipRightColumns,
                image.PreserveAspectRatio, null, null, image.SixelPlacement);
        }
        return new(true, images.Count, placements);
    }
}

/// <summary>Cheap draw-grid facts are always returned; native font/provenance work remains opt-in.</summary>
internal readonly record struct SnapshotCaptureInfo(int Rows, int Cols, int BufferRows, int BufferCols, SnapshotRenderContext? Context);

/// <summary>Opt-in render provenance. It describes an offscreen render, not a desktop photograph.</summary>
internal sealed record SnapshotRenderContext
{
    internal required RenderedFrameInfo Frame { get; init; }
    internal required string PrimaryFontFamily { get; init; }
    internal int PrimaryFontWeight { get; init; }
    internal int PrimaryFontWidth { get; init; }
    internal required string PrimaryFontSlant { get; init; }
    internal string? PrimaryFontSha256 { get; init; }
    internal int PrimaryFontCollectionIndex { get; init; }
    internal required string[] FallbackFontFamilies { get; init; }
    internal required string ConfiguredFontFamily { get; init; }
    internal float FontSize { get; init; }
    internal int RenderWidthPx { get; init; }
    internal int RenderHeightPx { get; init; }
    internal double MinimumContrast { get; init; }
    internal bool EnableLigatures { get; init; }
    internal bool EnableComplexShaping { get; init; }
    internal bool TextBlinkVisible { get; init; }
    internal long? PresentationTimeMs { get; init; }
    internal required CellMetrics Metrics { get; init; }

    internal static SnapshotRenderContext Create(RenderedFrameInfo frame, SKTypeface primary,
        SKTypeface[] fallback, CellMetrics metrics, TerminalSnapshotOptions options, int renderWidth, int renderHeight)
    {
        var (fingerprint, collectionIndex) = Fingerprint(primary);
        using var style = primary.FontStyle;
        return new SnapshotRenderContext
        {
            Frame = frame with { Theme = CopyTheme(frame.Theme) },
            ConfiguredFontFamily = options.TypefaceFamily,
            PrimaryFontFamily = primary.FamilyName,
            PrimaryFontWeight = style.Weight,
            PrimaryFontWidth = style.Width,
            PrimaryFontSlant = style.Slant.ToString(),
            PrimaryFontSha256 = fingerprint,
            PrimaryFontCollectionIndex = collectionIndex,
            FallbackFontFamilies = fallback.Select(face => face.FamilyName).Distinct(StringComparer.Ordinal).ToArray(),
            FontSize = options.FontSize,
            Metrics = metrics,
            RenderWidthPx = renderWidth,
            RenderHeightPx = renderHeight,
            MinimumContrast = options.MinimumContrast,
            EnableLigatures = options.EnableLigatures,
            EnableComplexShaping = options.EnableComplexShaping,
            PresentationTimeMs = options.PresentationTimeMs,
            TextBlinkVisible = options.PresentationTimeMs.HasValue
                ? TextBlinkAnimation.IsVisibleAtMilliseconds(options.PresentationTimeMs.Value) : options.TextBlinkVisible,
        };
    }

    private static RenderThemeSnapshot CopyTheme(RenderThemeSnapshot theme) => new()
    {
        Foreground = theme.Foreground,
        Background = theme.Background,
        CursorColor = theme.CursorColor,
        AnsiPalette = (TermColor[])theme.AnsiPalette.Clone(),
        HasForegroundOverride = theme.HasForegroundOverride,
        HasBackgroundOverride = theme.HasBackgroundOverride,
        HasCursorOverride = theme.HasCursorOverride,
        HasSelectionBackgroundOverride = theme.HasSelectionBackgroundOverride,
        HasSelectionForegroundOverride = theme.HasSelectionForegroundOverride,
        SelectionBackground = theme.SelectionBackground,
        SelectionForeground = theme.SelectionForeground,
        PaletteOverrides = theme.PaletteOverrides,
        IsReverseVideo = theme.IsReverseVideo,
    };

    // Only an explicit provenance capture reads the resolved face's bytes. Never hash a font path
    // guessed from its family, and never retain a native typeface/stream or cache in the result.
    private static (string? Sha256, int CollectionIndex) Fingerprint(SKTypeface face)
    {
        using var stream = face.OpenStream(out int collectionIndex);
        if (stream == null || stream.Length <= 0 || stream.Length > 64 * 1024 * 1024) return (null, collectionIndex);
        int remaining = stream.Length;
        var scratch = new byte[8192];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (remaining > 0)
        {
            int read = stream.Read(scratch, Math.Min(scratch.Length, remaining));
            if (read <= 0) return (null, collectionIndex);
            hash.AppendData(scratch, 0, read);
            remaining -= read;
        }
        return (Convert.ToHexStringLower(hash.GetHashAndReset()), collectionIndex);
    }
}
