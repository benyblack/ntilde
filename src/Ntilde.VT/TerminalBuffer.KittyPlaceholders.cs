using System;
using System.Collections.Generic;
using System.Linq;

namespace Ntilde.VT;

public partial class TerminalBuffer
{
    /// <summary>Virtual placements one image keeps; storing one more drops the oldest.</summary>
    internal const int MaxKittyVirtualPlacements = 64;

    /// <summary>
    /// Records (or replaces) a virtual placement of <paramref name="asset"/>. It is invisible and moves no
    /// cursor: it only tells the Unicode placeholder cells that name the image how big the grid is.
    /// </summary>
    internal void StoreKittyVirtualPlacement(KittyImageAsset asset, uint placementId, int columns, int rows)
    {
        bool lockTaken = EnterWriteLockIfNeeded();
        try
        {
            // A placement id names one placement of an image, whether it is drawn at the cursor or is virtual: the new one replaces the old.
            if (placementId != 0) RemoveKittyPlacements(asset, placementId);
            List<KittyVirtualPlacement> placements = asset.VirtualPlacements;
            placements.RemoveAll(item => item.PlacementId == placementId);
            if (placements.Count >= MaxKittyVirtualPlacements) placements.RemoveAt(0);
            placements.Add(new KittyVirtualPlacement(placementId, columns, rows));
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        Invalidate();
    }

    /// <summary>Retires the asset's placements drawn at the cursor that carry <paramref name="placementId"/>.</summary>
    private void RemoveKittyPlacements(KittyImageAsset asset, uint placementId)
    {
        for (int i = _images.Count - 1; i >= 0; i--)
        {
            TerminalImage image = _images[i];
            if (!ReferenceEquals(image.KittyAsset, asset) || image.KittyPlacementId != placementId) continue;
            RetireImage(image);
            _images.RemoveAt(i);
        }
    }

    /// <summary>
    /// Removes an image's virtual placements: all of them, or just <paramref name="placementId"/>'s.
    /// Returns whether any existed.
    /// </summary>
    private static bool DeleteKittyVirtualPlacements(KittyImageAsset asset, uint placementId) =>
        asset.VirtualPlacements.RemoveAll(item => placementId == 0 || item.PlacementId == placementId) > 0;

    private bool HasKittyVirtualPlacements() =>
        _kittyImageAssets.Any(asset => asset.IsAltScreen == _isAltScreen && asset.VirtualPlacements.Count > 0);

    /// <summary>
    /// Adds the images the visible Unicode placeholder cells ask for: one entry per horizontal run of cells
    /// that show consecutive columns of one grid row of one virtual placement. Reads cells only, so a
    /// scrolled, erased or reflowed placeholder moves or loses its image with the text.
    /// </summary>
    internal void AppendPlaceholderImages(List<RenderImageSnapshot> images, int absDisplayStart, int absEnd)
    {
        AssertLockHeld();
        if (!HasKittyVirtualPlacements()) return;
        var assets = new Dictionary<uint, KittyImageAsset?>();
        for (int absRow = Math.Max(0, absDisplayStart); absRow < absEnd; absRow++)
        {
            // Scrollback is paged, so its cells and side tables are read through the page store rather than as a row object.
            ReadOnlySpan<TerminalCell> cells;
            Storage.SmallMap<string>? texts;
            Storage.SmallMap<CellUnderline>? underlines;
            if (!_isAltScreen && absRow >= 0 && absRow < _scrollback.Count)
            {
                cells = _scrollback.GetRow(absRow);
                texts = _scrollback.GetExtendedTextMap(absRow);
                underlines = _scrollback.GetUnderlineMap(absRow);
            }
            else
            {
                TerminalRow? row = GetRowAbsolute(absRow);
                if (row is null) continue;
                cells = row.Cells;
                texts = row.GetExtendedTextMap();
                underlines = row.GetUnderlineMap();
            }
            AppendPlaceholderRuns(images, assets, cells, texts, underlines, absRow);
        }
    }

    private void AppendPlaceholderRuns(List<RenderImageSnapshot> images, Dictionary<uint, KittyImageAsset?> assets,
        ReadOnlySpan<TerminalCell> cells, Storage.SmallMap<string>? texts, Storage.SmallMap<CellUnderline>? underlines, int absRow)
    {
        PlaceholderRun? run = null;
        var previous = default(KittyPlaceholders.Previous);
        int columns = Math.Min(Cols, cells.Length);
        for (int col = 0; col < columns; col++)
        {
            TerminalCell cell = cells[col];
            string? text = null;
            if (cell.HasExtendedText) texts?.TryGet(col, out text);
            if (!IsDrawablePlaceholder(cell, text))
            {
                FlushPlaceholderRun(images, ref run, absRow);
                previous = default;
                continue;
            }
            uint foreground = KittyPlaceholders.ColorNumber(cell.IsPaletteForeground, cell.Fg, cell.Fg);
            CellUnderline? cellUnderline = null;
            underlines?.TryGet(col, out cellUnderline);
            KittyPlaceholders.Cell resolved = KittyPlaceholders.Resolve(text!, foreground, UnderlineNumber(cellUnderline), previous, out previous);
            if (!TryFindVirtualPlacement(assets, resolved, out KittyImageAsset? asset, out KittyVirtualPlacement placement)
                || resolved.Column >= placement.Columns || resolved.Row >= placement.Rows)
            {
                FlushPlaceholderRun(images, ref run, absRow);
                continue;
            }
            if (asset is null) continue;
            TermColor? background = cell.IsDefaultBackground || cell.IsPaletteBackground ? null : cell.Background;
            short backgroundIndex = cell.IsDefaultBackground ? (short)-1 : cell.BgIndex;
            if (run is { } open && open.Continues(asset, placement, resolved, col, background, backgroundIndex))
            {
                run = open with { Count = open.Count + 1 };
                continue;
            }
            FlushPlaceholderRun(images, ref run, absRow);
            run = new PlaceholderRun(asset, placement, resolved.Row, resolved.Column, col, 1, background, backgroundIndex);
        }
        FlushPlaceholderRun(images, ref run, absRow);
    }

    private static bool IsDrawablePlaceholder(TerminalCell cell, string? text) =>
        KittyPlaceholders.IsPlaceholder(text) && !cell.IsDefaultForeground && !cell.IsHidden;

    private static uint UnderlineNumber(CellUnderline? underline)
    {
        if (underline is not { HasColor: true }) return 0;
        if (underline.ColorIndex >= 0) return (uint)underline.ColorIndex & 0xFF;
        if (underline.Color is not { } color) return 0;
        return (uint)(color.R << 16 | color.G << 8 | color.B);
    }

    private bool TryFindVirtualPlacement(Dictionary<uint, KittyImageAsset?> assets, KittyPlaceholders.Cell cell,
        out KittyImageAsset? asset, out KittyVirtualPlacement placement)
    {
        placement = default;
        if (!assets.TryGetValue(cell.ImageId, out asset)) assets[cell.ImageId] = asset = FindKittyImage(cell.ImageId, 0);
        if (asset is null || asset.VirtualPlacements.Count == 0) return false;
        // An unnamed placement (underline colour 0) may be any virtual placement of the image: the newest.
        int index = cell.PlacementId == 0
            ? asset.VirtualPlacements.Count - 1
            : asset.VirtualPlacements.FindIndex(item => item.PlacementId == cell.PlacementId);
        if (index < 0) return false;
        placement = asset.VirtualPlacements[index];
        return true;
    }

    private static void FlushPlaceholderRun(List<RenderImageSnapshot> images, ref PlaceholderRun? run, int absRow)
    {
        if (run is not { } finished) return;
        run = null;
        images.Add(new RenderImageSnapshot
        {
            PlacementId = RunIdentity(finished, absRow),
            CellX = finished.StartColumn,
            CellY = absRow,
            CellWidth = finished.Count,
            CellHeight = 1,
            ImageHandle = finished.Asset.Handle,
            PreserveAspectRatio = true,
            GridColumn = finished.GridColumn,
            GridRow = finished.Row,
            GridColumns = finished.Placement.Columns,
            GridRows = finished.Placement.Rows,
            TileBackground = finished.Background,
            TileBackgroundIndex = finished.BackgroundIndex >= 0 ? finished.BackgroundIndex : null,
            IsSticky = true
        });
    }

    /// <summary>A stable, distinct identity for a run, so observers can tell the runs of one frame apart.</summary>
    private static Guid RunIdentity(PlaceholderRun run, int absRow)
    {
        Span<byte> tail = stackalloc byte[8];
        BitConverter.TryWriteBytes(tail, absRow);
        tail[4] = (byte)run.Count;
        tail[5] = (byte)run.Placement.Columns;
        tail[6] = (byte)run.Placement.Rows;
        tail[7] = 0xE0;
        return new Guid((int)run.Asset.Id, (short)run.StartColumn, (short)run.Row, tail.ToArray());
    }

    /// <summary>The cells of one grid row of a virtual placement shown side by side on one screen row.</summary>
    private readonly record struct PlaceholderRun(KittyImageAsset Asset, KittyVirtualPlacement Placement, int Row, int GridColumn, int StartColumn, int Count, TermColor? Background, short BackgroundIndex)
    {
        internal bool Continues(KittyImageAsset asset, KittyVirtualPlacement placement, KittyPlaceholders.Cell cell, int column, TermColor? background, short backgroundIndex) =>
            ReferenceEquals(Asset, asset) && Placement == placement && Row == cell.Row
            && GridColumn + Count == cell.Column && StartColumn + Count == column && Nullable.Equals(Background, background) && BackgroundIndex == backgroundIndex;
    }
}
