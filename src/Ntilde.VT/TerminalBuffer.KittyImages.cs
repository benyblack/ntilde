using System;
using System.Collections.Generic;
using System.Linq;

namespace Ntilde.VT;

internal sealed class KittyImageAsset(uint id, uint number, bool isAltScreen, object handle, int pixelWidth, int pixelHeight)
{
    internal uint Id { get; } = id;
    internal uint Number { get; } = number;
    internal bool IsAltScreen { get; } = isAltScreen;
    /// <summary>The pixels; a snapshot restore swaps its undecoded placeholder for the decoded handle.</summary>
    internal object Handle { get; set; } = handle;
    internal int PixelWidth { get; } = pixelWidth;
    internal int PixelHeight { get; } = pixelHeight;
    internal long PixelCharge => (long)PixelWidth * PixelHeight * 4;
    /// <summary>The bytes as sent, kept for snapshots when the parser retains image sources (#838).</summary>
    internal ImageSource? Source { get; init; }
    /// <summary>How <see cref="Source"/> was sent, so it can be decoded again on restore.</summary>
    internal KittyImageEncoding? Encoding { get; init; }
}

public partial class TerminalBuffer
{
    // Per-screen budgets bound retained decoded data, identity lookup and placements.
    // Handles remain opaque; renderer disposal still passes through the snapshot gate.
    internal const int MaxKittyImageAssets = 128;
    internal const int MaxKittyImagePlacements = 512;
    internal const long MaxKittyImagePixelBytes = 64L * 1024 * 1024;
    private readonly List<KittyImageAsset> _kittyImageAssets = new();
    private uint _nextKittyImageId;

    internal KittyImageAsset? StoreKittyImage(object handle, int pixelWidth, int pixelHeight, uint id, uint number,
        ImageSource? source = null, KittyImageEncoding? encoding = null)
    {
        bool lockTaken = EnterWriteLockIfNeeded();
        int placementsBefore = _images.Count;
        try
        {
            if (!TryAdmitImageHandle(handle)) return null;
            KittyImageAsset? old = id == 0 ? null : FindKittyImage(id, 0);
            if (id == 0 && number != 0) id = AllocateKittyImageId();
            var asset = new KittyImageAsset(id, number, _isAltScreen, handle, pixelWidth, pixelHeight) { Source = source, Encoding = encoding };
            // Publish the new ownership before retiring the old one: even a host decoder
            // sharing an opaque handle cannot retire that handle while the new asset owns it.
            _kittyImageAssets.Add(asset);
            if (old is not null) FreeKittyImage(old);
            while (_kittyImageAssets.Count(item => item.IsAltScreen == _isAltScreen) > MaxKittyImageAssets
                || _kittyImageAssets.Where(item => item.IsAltScreen == _isAltScreen).Sum(item => item.PixelCharge) > MaxKittyImagePixelBytes)
            {
                KittyImageAsset oldest = _kittyImageAssets.FirstOrDefault(item => item.IsAltScreen == _isAltScreen
                    && !ReferenceEquals(item, asset) && !HasKittyPlacement(item))
                    ?? _kittyImageAssets.First(item => item.IsAltScreen == _isAltScreen && !ReferenceEquals(item, asset));
                FreeKittyImage(oldest);
            }
            return asset;
        }
        finally
        {
            bool removedPlacements = _images.Count < placementsBefore;
            ExitWriteLockIfNeeded(Lock, lockTaken);
            if (removedPlacements) Invalidate();
        }
    }

    private uint AllocateKittyImageId()
    {
        // At most 128 ids can be live on a screen, so a free nonzero uint is found
        // within 129 iterations even after the counter wraps.
        do { _nextKittyImageId = unchecked(_nextKittyImageId + 1); }
        while (_nextKittyImageId == 0 || FindKittyImage(_nextKittyImageId, 0) is not null);
        return _nextKittyImageId;
    }

    internal KittyImageAsset? FindKittyImage(uint id, uint number)
    {
        for (int i = _kittyImageAssets.Count - 1; i >= 0; i--)
        {
            KittyImageAsset asset = _kittyImageAssets[i];
            if (asset.IsAltScreen != _isAltScreen) continue;
            if (id != 0 ? asset.Id == id : number != 0 && asset.Number == number) return asset;
        }
        return null;
    }

    internal void PlaceKittyImage(KittyImageAsset asset, TerminalImage image, uint placementId)
    {
        bool lockTaken = EnterWriteLockIfNeeded();
        try
        {
            RequireImageHandleAdmission(image.ImageHandle);
            image.IsAltScreenImage = _isAltScreen;
            image.KittyImageId = asset.Id;
            image.KittyAsset = asset;
            image.KittyPlacementId = placementId == 0 ? null : placementId;
            if (placementId != 0)
            {
                for (int i = _images.Count - 1; i >= 0; i--)
                {
                    TerminalImage old = _images[i];
                    if (old.IsAltScreenImage == _isAltScreen && old.KittyImageId == asset.Id && old.KittyPlacementId == placementId)
                    {
                        RetireImage(old);
                        _images.RemoveAt(i);
                    }
                }
            }
            while (_images.Count(item => item.IsAltScreenImage == _isAltScreen && item.KittyImageId.HasValue) >= MaxKittyImagePlacements)
            {
                int index = _images.FindIndex(item => item.IsAltScreenImage == _isAltScreen && item.KittyImageId.HasValue);
                RetireImage(_images[index]);
                _images.RemoveAt(index);
            }
            _images.Add(image);
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        Invalidate();
    }

    internal void DeleteKittyImages(uint id, uint number, uint placementId, bool freeData, bool visibleOnly)
    {
        bool lockTaken = EnterWriteLockIfNeeded();
        try
        {
            KittyImageAsset? selected = visibleOnly ? null : FindKittyImage(id, number);
            HashSet<KittyImageAsset> affected = DeleteKittyPlacements(selected, placementId, visibleOnly);
            // A placement-scoped deletion only frees an asset when that placement existed.
            // An unscoped hard delete must also release transmit-only data with no placement.
            if (!visibleOnly && selected is not null && placementId == 0) affected.Add(selected);
            if (freeData)
            {
                // Visible-only deletion must keep data referenced by offscreen placements.
                foreach (KittyImageAsset asset in affected.Where(asset => !HasKittyPlacement(asset)))
                    FreeKittyImage(asset);
            }
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        Invalidate();
    }

    private HashSet<KittyImageAsset> DeleteKittyPlacements(KittyImageAsset? selected, uint placementId, bool visibleOnly)
    {
        var affected = new HashSet<KittyImageAsset>();
        for (int i = _images.Count - 1; i >= 0; i--)
        {
            TerminalImage image = _images[i];
            if (!MatchesKittyDeletion(image, selected, placementId, visibleOnly)) continue;
            KittyImageAsset? owner = image.KittyAsset;
            if (owner is not null) affected.Add(owner);
            RetireImage(image);
            _images.RemoveAt(i);
        }
        return affected;
    }

    private bool MatchesKittyDeletion(TerminalImage image, KittyImageAsset? selected, uint placementId, bool visibleOnly)
    {
        if (image.IsAltScreenImage != _isAltScreen || !image.KittyImageId.HasValue) return false;
        if (placementId != 0 && image.KittyPlacementId != placementId) return false;
        int top = _isAltScreen ? 0 : _scrollback.Count;
        return visibleOnly ? image.CellY + image.CellHeight > top && image.CellY < top + Rows
            : selected is not null && image.KittyImageId == selected.Id;
    }

    private bool HasKittyPlacement(KittyImageAsset asset) => _images.Any(image => ReferenceEquals(image.KittyAsset, asset));

    private void FreeKittyImage(KittyImageAsset asset)
    {
        for (int i = _images.Count - 1; i >= 0; i--)
        {
            TerminalImage image = _images[i];
            if (!ReferenceEquals(image.KittyAsset, asset)) continue;
            RetireImage(image);
            _images.RemoveAt(i);
        }
        _kittyImageAssets.Remove(asset);
        asset.Source?.Release();
        RetireUnstoredKittyHandle(asset.Handle);
    }

    private bool IsRetainedKittyHandle(object handle) => _kittyImageAssets.Any(asset => ReferenceEquals(asset.Handle, handle));

    /// <summary>
    /// Queues an unreferenced retained handle; a native source enters the retired tier only after its last alias is gone.
    /// </summary>
    internal void RetireUnstoredKittyHandle(object handle)
    {
        bool lockTaken = EnterWriteLockIfNeeded();
        try
        {
            if (IsRetainedKittyHandle(handle) || _images.Any(image => ReferenceEquals(image.ImageHandle, handle))) return;
            if (handle is INativeSixelImage native) native.Source.Retire();
            QueueImageHandleRetirement(handle, disposeImmediately: true);
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
    }

    private void ClearRetainedKittyImages()
    {
        foreach (KittyImageAsset asset in _kittyImageAssets.ToArray()) FreeKittyImage(asset);
    }
}
