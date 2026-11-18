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
            List<KittyVirtualPlacement> placements = asset.VirtualPlacements;
            placements.RemoveAll(item => item.PlacementId == placementId);
            if (placements.Count >= MaxKittyVirtualPlacements) placements.RemoveAt(0);
            placements.Add(new KittyVirtualPlacement(placementId, columns, rows));
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        Invalidate();
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
            TerminalRow? row = GetRowAbsolute(absRow);
            if (row is null) continue;
            AppendPlaceholderRuns(images, assets, row, absRow);
        }
    }

    private void AppendPlaceholderRuns(List<RenderImageSnapshot> images, Dictionary<uint, KittyImageAsset?> assets, TerminalRow row, int absRow)
    {
        PlaceholderRun? run = null;
        var previous = default(KittyPlaceholders.Previous);
        int columns = Math.Min(Cols, row.Cells.Length);
        for (int col = 0; col < columns; col++)
        {
            TerminalCell cell = row.Cells[col];
            string? text = cell.HasExtendedText ? row.GetExtendedText(col) : null;
            if (!KittyPlaceholders.IsPlaceholder(text) || cell.IsDefaultForeground || cell.IsHidden)
            {
                FlushPlaceholderRun(images, ref run, absRow);
                previous = default;
                continue;
            }
            uint foreground = KittyPlaceholders.ColorNumber(cell.IsPaletteForeground, cell.Fg, cell.Fg);
            KittyPlaceholders.Cell resolved = KittyPlaceholders.Resolve(text!, foreground, UnderlineNumber(row.GetUnderline(col)), previous, out previous);
            if (!TryFindVirtualPlacement(assets, resolved, out KittyImageAsset? asset, out KittyVirtualPlacement placement)
                || resolved.Column >= placement.Columns || resolved.Row >= placement.Rows)
            {
                FlushPlaceholderRun(images, ref run, absRow);
                continue;
            }
            TermColor? background = cell.IsDefaultBackground || cell.IsPaletteBackground ? null : cell.Background;
            if (run is { } open && open.Continues(asset!, placement, resolved, col, background))
            {
                run = open with { Count = open.Count + 1 };
                continue;
            }
            FlushPlaceholderRun(images, ref run, absRow);
            run = new PlaceholderRun(asset!, placement, resolved.Row, resolved.Column, col, 1, background);
        }
        FlushPlaceholderRun(images, ref run, absRow);
    }

    private static uint UnderlineNumber(CellUnderline? underline) =>
        underline is not { HasColor: true } ? 0
        : underline.ColorIndex >= 0 ? (uint)underline.ColorIndex & 0xFF
        : underline.Color is { } color ? (uint)(color.R << 16 | color.G << 8 | color.B) : 0;

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
    private readonly record struct PlaceholderRun(KittyImageAsset Asset, KittyVirtualPlacement Placement, int Row, int GridColumn, int StartColumn, int Count, TermColor? Background)
    {
        internal bool Continues(KittyImageAsset asset, KittyVirtualPlacement placement, KittyPlaceholders.Cell cell, int column, TermColor? background) =>
            ReferenceEquals(Asset, asset) && Placement == placement && Row == cell.Row
            && GridColumn + Count == cell.Column && StartColumn + Count == column && Nullable.Equals(Background, background);
    }
}
