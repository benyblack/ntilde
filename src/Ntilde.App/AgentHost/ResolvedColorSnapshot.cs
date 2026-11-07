using System;
using System.Collections.Generic;
using Ntilde.AgentHost.Contracts;
using Ntilde.Shell;
using Ntilde.VT;
using SkiaSharp;

namespace Ntilde.AgentHost;

/// <summary>
/// Opt-in theme-resolved cell colors for <c>readScreen</c>. The resolution itself stays in the
/// App's rendering layer: each channel is resolved through the draw operation's own
/// <see cref="TerminalDrawOperation.ResolveCellForeground"/> /
/// <see cref="TerminalDrawOperation.ResolveCellBackground"/> precedence, against the theme of one
/// coherent <see cref="TerminalBuffer.CaptureRenderSnapshot"/> (cells and theme captured together
/// under the buffer's read lock). Theme/render state — the theme's defaults, the 256-entry ANSI
/// palette, override flags and DECSCNM — is returned beside the runs as a
/// <see cref="ColorBindingDto"/> so every resolved value can be re-derived from the binding and
/// the cell's storage state, and so the reader knows the exact pipeline stage the values belong to.
/// </summary>
internal static class ResolvedColorSnapshot
{
    internal const string Stage = "theme-resolution";
    internal const string StageNote = "Per-channel storage-to-theme resolution exactly as the draw operation resolves cells: "
        + "default -> theme default, palette index -> ANSI palette entry, literal -> stored ARGB; opaque alpha. "
        + "Captured with the cells and theme under one buffer read lock. Precedes the inverse-cell swap, "
        + "minimum-contrast adjustment, opacity compositing and glyph rasterisation, so rendered glyph "
        + "appearance may still differ from these values.";

    /// <summary>
    /// Capture a complete resolved-color viewport with its binding, or refuse before any partial data.
    /// The caller passes the rows/cols it already read for the rest of the response: a resize that
    /// lands between the two captures makes them differ, and the whole read is refused here rather
    /// than answered with projections of two different screens.
    /// </summary>
    internal static bool TryCapture(TerminalBuffer buffer, out ResolvedColorRunDto[]? runs, out ColorBindingDto? binding, double? minimumContrast = null,
        int expectedRows = -1, int expectedCols = -1)
    {
        runs = null;
        binding = null;
        int rows, cols;
        buffer.Lock.EnterReadLock();
        try
        {
            rows = buffer.Rows;
            cols = buffer.Cols;
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }
        if (rows <= 0 || cols <= 0 || (long)rows * cols > AgentHostProtocol.MaxCellDetailCells
            || rows != expectedRows || cols != expectedCols) return false;

        using var snapshot = buffer.CaptureRenderSnapshot(
            new RenderSnapshotRequest { ViewportRows = rows, ViewportCols = cols, Isolated = true }, out _);
        // A concurrent resize between the dimension read and the capture would mix geometries;
        // refuse rather than project a torn viewport.
        if (snapshot.ViewportRows != rows || snapshot.ViewportCols != cols
            || snapshot.BufferRows != rows || snapshot.BufferCols != cols
            || snapshot.RowsData.Length != rows)
        {
            return false;
        }

        var theme = snapshot.Theme;
        var themeFg = ToOpaqueSkColor(theme.Foreground);
        var themeBg = ToOpaqueSkColor(theme.Background);
        var runList = new List<ResolvedColorRunDto>();
        ResolvedColorRunDto? previous = null;
        var count = 0;
        for (var r = 0; r < rows; r++)
        {
            var cells = snapshot.RowsData.Array[r].Cells;
            if (cells.Length < cols) return false;
            for (var c = 0; c < cols; c++)
            {
                var cell = cells[c];
                var value = new ResolvedColorRunDto
                {
                    Count = 1,
                    Fg = ResolveChannel(cell, isForeground: true, themeFg, theme),
                    Bg = ResolveChannel(cell, isForeground: false, themeBg, theme),
                };
                // Canonical runs: adjacent runs differ; the budget mirrors CellDetailSnapshot's.
                if (previous != null && previous.Fg == value.Fg && previous.Bg == value.Bg)
                {
                    count++;
                    continue;
                }
                if (previous != null) runList.Add(previous with { Count = count });
                if (runList.Count >= AgentHostProtocol.MaxCellDetailRuns) return false;
                previous = value;
                count = 1;
            }
        }
        if (previous != null) runList.Add(previous with { Count = count });
        runs = runList.ToArray();
        binding = BindingOf(theme, minimumContrast);
        return true;
    }

    /// <summary>Classify one channel's storage state and resolve it through the draw path's precedence.</summary>
    private static ResolvedChannelDto ResolveChannel(RenderCellSnapshot cell, bool isForeground, SKColor themeDefault, RenderThemeSnapshot theme)
    {
        var resolved = isForeground
            ? TerminalDrawOperation.ResolveCellForeground(cell, themeDefault, 255, theme)
            : TerminalDrawOperation.ResolveCellBackground(cell, themeDefault, 255, theme);
        var isDefault = isForeground ? cell.IsDefaultForeground : cell.IsDefaultBackground;
        var index = isForeground ? cell.FgIndex : cell.BgIndex;
        // The same classification CellDetailSnapshot projects: default marker, palette index >= 0,
        // or a literal color. Under DECSCNM the theme default handed in is already the presented
        // (swapped) color, so a default cell resolves to the presented default.
        var source = isDefault ? "default" : index >= 0 ? "palette" : "literal";
        return new ResolvedChannelDto
        {
            Source = source,
            Index = isDefault || index < 0 ? (short)-1 : index,
            Argb = OpaqueArgb(resolved),
        };
    }

    /// <summary>Project the render theme into the reported binding facts.</summary>
    private static ColorBindingDto BindingOf(RenderThemeSnapshot theme, double? minimumContrast)
    {
        var palette = new uint[theme.AnsiPalette is { Length: 256 } array ? 256 : 0];
        if (palette.Length == 256)
        {
            for (var i = 0; i < palette.Length; i++) palette[i] = OpaqueArgb(theme.AnsiPalette[i]);
        }
        var overrides = new List<int>();
        for (var i = 0; i < 256; i++)
        {
            if (theme.PaletteOverrides.Contains(i)) overrides.Add(i);
        }
        return new ColorBindingDto
        {
            Stage = Stage,
            StageNote = StageNote,
            ThemeName = theme.Source?.Name,
            ForegroundArgb = OpaqueArgb(theme.Foreground),
            BackgroundArgb = OpaqueArgb(theme.Background),
            AnsiPaletteArgb = palette,
            ReverseVideo = theme.IsReverseVideo,
            ForegroundOverride = theme.HasForegroundOverride,
            BackgroundOverride = theme.HasBackgroundOverride,
            PaletteOverrideIndices = overrides.ToArray(),
            MinimumContrast = minimumContrast,
        };
    }

    /// <summary>An opaque Skia copy of a theme color; compositing is a later stage.</summary>
    private static SKColor ToOpaqueSkColor(TermColor color) => new(color.R, color.G, color.B, 255);

    /// <summary>Pack the resolved RGB with opaque alpha: compositing is a later pipeline stage.</summary>
    /// <summary>Pack a resolved SKColor with opaque alpha.</summary>
    private static uint OpaqueArgb(SKColor color) => 0xFF000000u | (uint)color.Red << 16 | (uint)color.Green << 8 | color.Blue;

    /// <summary>Pack a theme color with opaque alpha.</summary>
    private static uint OpaqueArgb(TermColor color) => 0xFF000000u | (uint)color.R << 16 | (uint)color.G << 8 | color.B;
}
