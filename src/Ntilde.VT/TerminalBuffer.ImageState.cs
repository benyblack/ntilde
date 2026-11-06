namespace Ntilde.VT;

/// <summary>
/// Kitty and iTerm2 images in complete state transfer (#838). Where the parser retains image
/// sources, the bytes each image was sent as travel in the snapshot with its placements, in the
/// same coordinates as SIXEL placements; the importer keeps them undecoded and
/// <see cref="DecodeTransferredImages"/> decodes them with the host's decoder. A parser without a
/// decoder (the daemon) keeps them undecoded, holding the same cells.
/// </summary>
public partial class TerminalBuffer
{
    private void ExportImageState(TerminalStateSnapshot snapshot)
    {
        snapshot.KittyNextImageId = _nextKittyImageId;
        Dictionary<ImageSource, int> evictionOrder = ImageSources.EvictionOrder();
        var exported = new Dictionary<KittyImageAsset, int>();
        foreach (KittyImageAsset asset in _kittyImageAssets)
        {
            if (asset.Source?.Data is not { } data || asset.Encoding is not { } encoding) continue;
            exported[asset] = (snapshot.KittyImages ??= []).Count;
            snapshot.KittyImages.Add(new TerminalKittyImageState
            {
                Id = asset.Id,
                Number = asset.Number,
                Alt = asset.IsAltScreen,
                Format = encoding.Format,
                Compressed = encoding.Compressed,
                RawWidth = encoding.RawWidth,
                RawHeight = encoding.RawHeight,
                Width = asset.PixelWidth,
                Height = asset.PixelHeight,
                Data = data,
                SourceOrder = evictionOrder.TryGetValue(asset.Source, out int kittyOrder) ? kittyOrder : null,
            });
        }

        for (int order = 0; order < _images.Count; order++)
        {
            TerminalImage image = _images[order];
            if (image.IsSixelImage || !TryExportedRows(image, snapshot, out int y, out int top, out int bottom)) continue;
            if (image.KittyAsset is { } asset)
            {
                if (!exported.TryGetValue(asset, out int index)) continue;
                (snapshot.KittyPlacements ??= []).Add(new TerminalKittyPlacementState
                {
                    Image = index,
                    Placement = image.KittyPlacementId ?? 0,
                    X = image.CellX,
                    Y = y,
                    Width = image.CellWidth,
                    Height = image.CellHeight,
                    ClipTop = top,
                    ClipBottom = bottom,
                    ClipRight = image.ClipRightColumns,
                    PreserveAspectRatio = image.PreserveAspectRatio,
                    Order = order,
                });
            }
            else if (image.EncodedSource?.Data is { } data && PixelSize(image) is (int pixelWidth, int pixelHeight))
            {
                (snapshot.ITermImages ??= []).Add(new TerminalITermImageState
                {
                    Data = data,
                    SourceOrder = evictionOrder.TryGetValue(image.EncodedSource, out int itermOrder) ? itermOrder : null,
                    PixelWidth = pixelWidth,
                    PixelHeight = pixelHeight,
                    X = image.CellX,
                    Y = y,
                    Width = image.CellWidth,
                    Height = image.CellHeight,
                    Alt = image.IsAltScreenImage,
                    ClipTop = top,
                    ClipBottom = bottom,
                    ClipRight = image.ClipRightColumns,
                    PreserveAspectRatio = image.PreserveAspectRatio,
                    Order = order,
                });
            }
        }
    }

    /// <summary>
    /// The placement's row in exported coordinates and the rows the export clips from it; false when
    /// nothing of it is left, as for SIXEL.
    /// </summary>
    private static bool TryExportedRows(TerminalImage image, TerminalStateSnapshot snapshot, out int y, out int top, out int bottom)
    {
        y = image.IsAltScreenImage ? image.CellY : image.CellY - snapshot.ScrollbackRowsDropped;
        int end = image.IsAltScreenImage ? snapshot.Rows : snapshot.ScrollbackRowCount + snapshot.Rows;
        top = Math.Max(image.ClipTopRows, Math.Max(0, -y));
        bottom = Math.Max(image.ClipBottomRows, Math.Max(0, y + image.CellHeight - end));
        return top + bottom < image.CellHeight && image.CellX < snapshot.Cols;
    }

    /// <summary>The header size an iTerm2 placement was made from, whether or not it was decoded.</summary>
    private static (int, int)? PixelSize(TerminalImage image)
    {
        if (image.ImageHandle is UndecodedImage undecoded) return (undecoded.PixelWidth, undecoded.PixelHeight);
        return image.EncodedSource?.Data is { } data && ImageHeaderProbe.TryGetSize(data, out _, out int w, out int h) ? (w, h) : null;
    }

    /// <summary>
    /// Refuses an image section a correct exporter could not have produced, before anything is
    /// imported: wrong encodings, bytes that do not declare the stated size, placements naming no
    /// image, or more than the per-screen quotas and the source budget allow.
    /// </summary>
    private static void ValidateImageState(TerminalStateSnapshot snapshot)
    {
        long bytes = 0;
        int sources = 0;
        var images = snapshot.KittyImages ?? [];
        foreach (bool alt in new[] { false, true })
        {
            var screen = images.Where(state => state?.Alt == alt).ToList();
            if (screen.Count > MaxKittyImageAssets)
                throw StateTransferValidation.Reject("Kitty images", $"more than {MaxKittyImageAssets} on one screen");
            if (screen.Sum(state => (long)state.Width * state.Height * 4) > MaxKittyImagePixelBytes)
                throw StateTransferValidation.Reject("Kitty images", "pixel charge over the per-screen quota");
        }
        foreach (TerminalKittyImageState? image in images)
        {
            var state = StateTransferValidation.RequirePresent(image, "Kitty image");
            byte[] data = StateTransferValidation.RequirePresent(state.Data, "Kitty image data");
            if (state.Format is not (0 or 24 or 32 or 100)) throw StateTransferValidation.Reject("Kitty image", "unknown format");
            var encoding = new KittyImageEncoding(state.Format, state.Compressed, state.RawWidth, state.RawHeight);
            if (DeclaredKittySize(data, encoding) != (state.Width, state.Height))
                throw StateTransferValidation.Reject("Kitty image", "data does not declare the stated size");
            bytes += data.Length;
            sources++;
        }
        var placements = snapshot.KittyPlacements ?? [];
        foreach (bool alt in new[] { false, true })
        {
            if (placements.Count(p => p is not null && p.Image >= 0 && p.Image < images.Count && images[p.Image].Alt == alt) > MaxKittyImagePlacements)
                throw StateTransferValidation.Reject("Kitty placements", $"more than {MaxKittyImagePlacements} on one screen");
        }
        foreach (TerminalKittyPlacementState? placement in placements)
        {
            var state = StateTransferValidation.RequirePresent(placement, "Kitty placement");
            if (state.Image < 0 || state.Image >= images.Count) throw StateTransferValidation.Reject("Kitty placement", "names no image");
            RequireCellRectangle(state.Width, state.Height, "Kitty placement");
        }
        foreach (TerminalITermImageState? image in snapshot.ITermImages ?? [])
        {
            var state = StateTransferValidation.RequirePresent(image, "iTerm2 image");
            byte[] data = StateTransferValidation.RequirePresent(state.Data, "iTerm2 image data");
            if (!ImageHeaderProbe.TryGetSize(data, out _, out int w, out int h) || (w, h) != (state.PixelWidth, state.PixelHeight))
                throw StateTransferValidation.Reject("iTerm2 image", "data does not declare the stated size");
            // The live parser refuses these before decoding; a snapshot must not hand them to a decoder.
            if (w > 2000 || h > 2000) throw StateTransferValidation.Reject("iTerm2 image", "pixel size over the 2000-pixel limit");
            RequireCellRectangle(state.Width, state.Height, "iTerm2 image");
            bytes += data.Length;
            sources++;
        }
        if (sources > ImageSourceBudget.DefaultMaxSources || bytes > ImageSourceBudget.DefaultMaxBytes)
            throw StateTransferValidation.Reject("image sources", "over the retained-source budget");
    }

    private static void RequireCellRectangle(int width, int height, string what)
    {
        if (width is < 1 or > 200 || height is < 1 or > 200) throw StateTransferValidation.Reject(what, "cell size out of range");
    }

    /// <summary>The pixel size Kitty bytes declare, as live transmission measures it; null if none.</summary>
    private static (int, int)? DeclaredKittySize(byte[] data, KittyImageEncoding encoding)
    {
        if (RetainedKittyPixels(data, encoding) is not { } pixels) return null;
        if (encoding.Format is 24 or 32)
        {
            if (encoding.RawWidth is < 1 or > 2000 || encoding.RawHeight is < 1 or > 2000) return null;
            return pixels.LongLength == (long)encoding.RawWidth * encoding.RawHeight * (encoding.Format == 24 ? 3 : 4)
                ? (encoding.RawWidth, encoding.RawHeight) : null;
        }
        return ImageHeaderProbe.TryGetSize(pixels, out _, out int w, out int h) && w <= 2000 && h <= 2000 ? (w, h) : null;
    }

    /// <summary>
    /// The bytes after <c>o=z</c> inflation, bounded as live transmission bounds them. A raw size is
    /// checked against the 2000-pixel limit first, as live transmission checks it before inflating, so a
    /// crafted snapshot cannot license a huge inflation.
    /// </summary>
    private static byte[]? RetainedKittyPixels(byte[] data, KittyImageEncoding encoding)
    {
        if (encoding.Format is 24 or 32 && (encoding.RawWidth is < 1 or > 2000 || encoding.RawHeight is < 1 or > 2000)) return null;
        if (!encoding.Compressed) return data;
        long bound = encoding.Format is 24 or 32
            ? (long)encoding.RawWidth * encoding.RawHeight * (encoding.Format == 24 ? 3 : 4)
            : AnsiParser.KittyMaxInflatedContainerBytes;
        return bound is > 0 and <= int.MaxValue ? AnsiParser.InflateZlib(data, (int)bound) : null;
    }

    /// <summary>
    /// Restores retained Kitty assets, their placements and iTerm2 images, undecoded, at their
    /// transferred positions. Like <see cref="ImportSixelState"/> it never replays placement or moves
    /// the cursor. Runs after the old images were cleared.
    /// </summary>
    private void ImportImageState(TerminalStateSnapshot snapshot, Dictionary<TerminalImage, int> order)
    {
        _nextKittyImageId = snapshot.KittyNextImageId;
        var kittyStates = snapshot.KittyImages ?? [];
        var itermStates = snapshot.ITermImages ?? [];

        // Retain the bytes in the exported eviction order, not grouped by protocol, so the budget
        // evicts the same image next as the source would. Entries without an order keep theirs.
        var kittySources = new ImageSource?[kittyStates.Count];
        var itermSources = new ImageSource?[itermStates.Count];
        var retention = kittyStates.Select((state, i) => (Order: state.SourceOrder, Sequence: i, Kitty: true, Index: i))
            .Concat(itermStates.Select((state, i) => (Order: state.SourceOrder, Sequence: kittyStates.Count + i, Kitty: false, Index: i)))
            .OrderBy(entry => entry.Order ?? int.MaxValue).ThenBy(entry => entry.Sequence);
        foreach (var entry in retention)
        {
            if (entry.Kitty) kittySources[entry.Index] = ImageSources.Retain(kittyStates[entry.Index].Data);
            else itermSources[entry.Index] = ImageSources.Retain(itermStates[entry.Index].Data);
        }

        var assets = new List<KittyImageAsset?>();
        for (int i = 0; i < kittyStates.Count; i++)
        {
            TerminalKittyImageState state = kittyStates[i];
            ImageSource source = kittySources[i]!;
            if (source.Data is null) { assets.Add(null); continue; }
            var asset = new KittyImageAsset(state.Id, state.Number, state.Alt, new UndecodedImage(state.Width, state.Height), state.Width, state.Height)
            {
                Source = source,
                Encoding = new KittyImageEncoding(state.Format, state.Compressed, state.RawWidth, state.RawHeight),
            };
            _kittyImageAssets.Add(asset);
            assets.Add(asset);
        }
        foreach (TerminalKittyPlacementState state in snapshot.KittyPlacements ?? [])
        {
            if (assets[state.Image] is not { } asset) continue;
            if (ImportedPlacement(asset.Handle, state.X, state.Y, state.Width, state.Height, asset.IsAltScreen,
                    state.ClipTop, state.ClipBottom, state.ClipRight) is not { } image) continue;
            image.KittyImageId = asset.Id;
            image.KittyAsset = asset;
            image.KittyPlacementId = state.Placement == 0 ? null : state.Placement;
            image.PreserveAspectRatio = state.PreserveAspectRatio;
            _images.Add(image);
            if (state.Order is int z) order[image] = z;
        }
        for (int i = 0; i < itermStates.Count; i++)
        {
            TerminalITermImageState state = itermStates[i];
            ImageSource source = itermSources[i]!;
            if (source.Data is null || ImportedPlacement(new UndecodedImage(state.PixelWidth, state.PixelHeight), state.X, state.Y, state.Width, state.Height,
                    state.Alt, state.ClipTop, state.ClipBottom, state.ClipRight) is not { } image)
            {
                source.Release();
                continue;
            }
            image.EncodedSource = source;
            image.PreserveAspectRatio = state.PreserveAspectRatio;
            _images.Add(image);
            if (state.Order is int z) order[image] = z;
        }
    }

    /// <summary>
    /// Puts imported placements back in their exported order, which is the order they are drawn in,
    /// so an image covered by another before the transfer is still covered after it. Placements from
    /// older snapshots without an order keep the order they were imported in, after the ordered ones.
    /// </summary>
    private void RestorePlacementOrder(Dictionary<TerminalImage, int> order)
    {
        if (order.Count == 0) return;
        var sorted = _images.Select((image, index) => (image, index))
            .OrderBy(entry => order.TryGetValue(entry.image, out int z) ? z : int.MaxValue)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.image)
            .ToList();
        _images.Clear();
        _images.AddRange(sorted);
    }

    /// <summary>A placement clamped to this buffer as SIXEL imports are; null when nothing of it is left.</summary>
    private TerminalImage? ImportedPlacement(object handle, int x, int y, int width, int height, bool alt, int clipTop, int clipBottom, int clipRight)
    {
        int top = Math.Clamp(clipTop, 0, height);
        int bottom = Math.Clamp(clipBottom, 0, height - top);
        int right = Math.Clamp(clipRight, 0, width);
        if (top + bottom >= height || right >= width) return null;
        int lastRow = alt ? Rows - 1 : _scrollback.Count + Rows - 1;
        return new TerminalImage(handle, Math.Clamp(x, 0, Cols - 1), Math.Clamp(y, 1 - height, lastRow), width, height)
        {
            IsAltScreenImage = alt,
            ClipTopRows = top,
            ClipBottomRows = bottom,
            ClipRightColumns = right,
        };
    }

    /// <summary>
    /// Decodes transferred Kitty and iTerm2 images with the host's decoder. Pixels are kept only at
    /// the size the bytes declare, as in live placement; anything else stays undecoded.
    /// </summary>
    internal void DecodeTransferredImages(IImageDecoder? decoder)
    {
        if (decoder is null) return;
        Lock.EnterWriteLock();
        try
        {
            foreach (KittyImageAsset asset in _kittyImageAssets)
            {
                if (asset.Handle is not UndecodedImage || asset.Source?.Data is not { } data || asset.Encoding is not { } encoding) continue;
                if (RetainedKittyPixels(data, encoding) is not { } pixels) continue;
                object? handle = DecodeTransferred(asset.PixelWidth, asset.PixelHeight, (out int w, out int h) => encoding.Format is 24 or 32
                    ? decoder.DecodeRawImage(pixels, encoding.Format == 24 ? 3 : 4, encoding.RawWidth, encoding.RawHeight, out w, out h)
                    : decoder.DecodeImageBytes(pixels, out w, out h));
                if (handle is null) continue;
                asset.Handle = handle;
                foreach (TerminalImage image in _images)
                {
                    if (ReferenceEquals(image.KittyAsset, asset)) image.ImageHandle = handle;
                }
            }
            foreach (TerminalImage image in _images)
            {
                if (image.KittyAsset is not null || image.ImageHandle is not UndecodedImage undecoded || image.EncodedSource?.Data is not { } data) continue;
                if (DecodeTransferred(undecoded.PixelWidth, undecoded.PixelHeight, (out int w, out int h) => decoder.DecodeImageBytes(data, out w, out h)) is { } handle)
                    image.ImageHandle = handle;
            }
        }
        finally { Lock.ExitWriteLock(); }
        Invalidate();
    }

    private delegate object? TransferredDecode(out int pixelWidth, out int pixelHeight);

    /// <summary>The decoder's admitted handle at exactly the declared size, else null (pixels released).</summary>
    private object? DecodeTransferred(int pixelWidth, int pixelHeight, TransferredDecode decode)
    {
        try
        {
            if (decode(out int w, out int h) is not { } handle) return null;
            if ((w, h) == (pixelWidth, pixelHeight) && TryAdmitImageHandle(handle)) return handle;
            ReleaseRejectedImageHandle(handle);
        }
        catch (Exception ex) { TerminalLogger.Debug($"[IMAGES] Snapshot decode failed: {ex.Message}"); }
        return null;
    }
}
