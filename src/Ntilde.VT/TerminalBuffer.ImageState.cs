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
    private const string KittyImageWhat = "Kitty image";
    private const string ITermImageWhat = "iTerm2 image";
    private const string KittyImagesWhat = "Kitty images";

    /// <summary>Most bytes all compressed Kitty images in one snapshot may inflate to while validating.</summary>
    internal const long MaxSnapshotInflatedBytes = 128L * 1024 * 1024;

    /// <summary>
    /// Most decoded iTerm2 pixel bytes a restore materializes. Live iTerm2 images have no retained-pixel
    /// quota, so a snapshot is never refused for them; past this, older ones stay undecoded.
    /// </summary>
    internal const long MaxTransferredITermPixelBytes = 128L * 1024 * 1024;

    /// <summary>Furthest a transferred Kitty or iTerm2 placement may lie past the right or bottom edge.</summary>
    private const int MaxImportedOffscreenCells = 4096;

    /// <summary>How much of an inflated container is kept to read its header while validating.</summary>
    private const int InflatedHeaderPrefixBytes = 256 * 1024;

    // ---- Export ----------------------------------------------------------------------------

    private void ExportImageState(TerminalStateSnapshot snapshot)
    {
        snapshot.KittyNextImageId = _nextKittyImageId;
        Dictionary<ImageSource, int> evictionOrder = ImageSources.EvictionOrder();
        Dictionary<KittyImageAsset, int> exported = ExportKittyAssets(snapshot, evictionOrder);
        for (int order = 0; order < _images.Count; order++)
        {
            TerminalImage image = _images[order];
            if (image.IsSixelImage || !TryExportedRows(image, snapshot, out PlacementGeometry geometry)) continue;
            if (image.KittyAsset is { } asset)
            {
                if (exported.TryGetValue(asset, out int index)) ExportKittyPlacement(snapshot, image, index, geometry, order);
            }
            else
            {
                ExportITermImage(snapshot, image, geometry, order, evictionOrder);
            }
        }
    }

    private Dictionary<KittyImageAsset, int> ExportKittyAssets(TerminalStateSnapshot snapshot, Dictionary<ImageSource, int> evictionOrder)
    {
        var exported = new Dictionary<KittyImageAsset, int>();
        foreach (KittyImageAsset asset in _kittyImageAssets)
        {
            // An asset whose bytes were evicted is carried without them: it still holds its id and size, which a later
            // placement needs on every side. Without an encoding the parser retains no sources, and nothing is carried.
            if (asset.Source is not { } source || asset.Encoding is not { } encoding) continue;
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
                Data = source.Data?.ToArray() ?? [], // a copy: the snapshot must not alias live retained bytes
                SourceOrder = OrderOf(evictionOrder, source),
            });
        }
        return exported;
    }

    private static void ExportKittyPlacement(TerminalStateSnapshot snapshot, TerminalImage image, int index, PlacementGeometry geometry, int order)
    {
        (snapshot.KittyPlacements ??= []).Add(new TerminalKittyPlacementState
        {
            Image = index,
            Placement = image.KittyPlacementId ?? 0,
            X = geometry.X,
            Y = geometry.Y,
            Width = geometry.Width,
            Height = geometry.Height,
            ClipTop = geometry.ClipTop,
            ClipBottom = geometry.ClipBottom,
            ClipRight = geometry.ClipRight,
            PreserveAspectRatio = image.PreserveAspectRatio,
            Order = order,
        });
    }

    private static void ExportITermImage(TerminalStateSnapshot snapshot, TerminalImage image, PlacementGeometry geometry, int order,
        Dictionary<ImageSource, int> evictionOrder)
    {
        if (image.EncodedSource is not { } source || PixelSize(image) is not (int pixelWidth, int pixelHeight)) return;
        (snapshot.ITermImages ??= []).Add(new TerminalITermImageState
        {
            Data = source.Data?.ToArray() ?? [], // empty once evicted: the placement still holds its cells
            SourceOrder = OrderOf(evictionOrder, source),
            PixelWidth = pixelWidth,
            PixelHeight = pixelHeight,
            X = geometry.X,
            Y = geometry.Y,
            Width = geometry.Width,
            Height = geometry.Height,
            Alt = geometry.Alt,
            ClipTop = geometry.ClipTop,
            ClipBottom = geometry.ClipBottom,
            ClipRight = geometry.ClipRight,
            PreserveAspectRatio = image.PreserveAspectRatio,
            Order = order,
        });
    }

    private static int? OrderOf(Dictionary<ImageSource, int> evictionOrder, ImageSource source) =>
        evictionOrder.TryGetValue(source, out int order) ? order : null;

    /// <summary>
    /// The placement in exported coordinates; false when nothing of it is left. Rows above the first
    /// row (above the exported history, or scrolled above the alternate screen) are not clipped: the live
    /// buffer only moves Kitty and iTerm2 images there, as eviction does, and scrolling back down shows
    /// them whole again, so they are carried as they are. An image wholly above the first row never moves
    /// again, and eviction drops it; it is not carried. What lies past the screen's right or bottom edge
    /// is carried too: a Kitty image placed with <c>C=1</c> on the last row, or right of a column a
    /// narrowed alternate screen no longer has, shows again when the screen grows.
    /// </summary>
    private static bool TryExportedRows(TerminalImage image, TerminalStateSnapshot snapshot, out PlacementGeometry geometry)
    {
        int y = image.IsAltScreenImage ? image.CellY : image.CellY - snapshot.ScrollbackRowsDropped;
        geometry = new PlacementGeometry(image.CellX, y, image.CellWidth, image.CellHeight, image.IsAltScreenImage,
            image.ClipTopRows, image.ClipBottomRows, image.ClipRightColumns);
        return y + image.CellHeight > 0 && image.ClipTopRows + image.ClipBottomRows < image.CellHeight;
    }

    /// <summary>The header size an iTerm2 placement was made from, whether or not it was decoded.</summary>
    private static (int, int)? PixelSize(TerminalImage image)
    {
        if (image.ImageHandle is UndecodedImage undecoded) return (undecoded.PixelWidth, undecoded.PixelHeight);
        if (image.EncodedSource?.Data is { } data && ImageHeaderProbe.TryGetSize(data, out _, out int w, out int h)) return (w, h);
        return null;
    }

    // ---- Validation ------------------------------------------------------------------------

    /// <summary>
    /// Refuses an image section a correct exporter could not have produced, before anything is
    /// imported: wrong encodings, bytes that do not declare the stated size, duplicate ids, placements
    /// naming no image, more than the per-screen quotas allow, or more inflation work than any real
    /// state needs. How many bytes the importer can retain is not checked: what does not fit its
    /// budget is restored without bytes.
    /// </summary>
    private static void ValidateImageState(TerminalStateSnapshot snapshot)
    {
        var images = snapshot.KittyImages ?? [];
        ValidateKittyQuotas(images);
        long inflated = 0;
        foreach (TerminalKittyImageState? image in images) ValidateKittyImage(image, ref inflated);
        ValidateKittyPlacements(images, snapshot.KittyPlacements ?? []);
        foreach (TerminalITermImageState? image in snapshot.ITermImages ?? []) ValidateITermImage(image);
    }

    private static void ValidateKittyQuotas(List<TerminalKittyImageState> images)
    {
        foreach (bool alt in new[] { false, true })
        {
            var screen = images.Where(state => state?.Alt == alt).ToList();
            if (screen.Count > MaxKittyImageAssets)
                throw StateTransferValidation.Reject(KittyImagesWhat, $"more than {MaxKittyImageAssets} on one screen");
            if (screen.Sum(state => (long)state.Width * state.Height * 4) > MaxKittyImagePixelBytes)
                throw StateTransferValidation.Reject(KittyImagesWhat, "pixel charge over the per-screen quota");
            var ids = screen.Where(state => state.Id != 0).Select(state => state.Id).ToList();
            if (ids.Count != ids.Distinct().Count())
                throw StateTransferValidation.Reject(KittyImagesWhat, "one id names two images on one screen");
        }
    }

    private static void ValidateKittyImage(TerminalKittyImageState? image, ref long inflated)
    {
        var state = StateTransferValidation.RequirePresent(image, KittyImageWhat);
        byte[] data = StateTransferValidation.RequirePresent(state.Data, "Kitty image data");
        if (state.Format is not (0 or 24 or 32 or 100)) throw StateTransferValidation.Reject(KittyImageWhat, "unknown format");
        if (data.Length == 0)
        {
            // Evicted bytes: nothing to measure, so the stated size must be one live transmission could have produced.
            RequirePixelSize(state.Width, state.Height, KittyImageWhat);
            return;
        }
        var encoding = new KittyImageEncoding(state.Format, state.Compressed, state.RawWidth, state.RawHeight);
        if (DeclaredKittySize(data, encoding, ref inflated) != (state.Width, state.Height))
            throw StateTransferValidation.Reject(KittyImageWhat, "data does not declare the stated size");
        if (inflated > MaxSnapshotInflatedBytes)
            throw StateTransferValidation.Reject(KittyImagesWhat, "compressed images inflate past the snapshot limit");
    }

    private static void ValidateKittyPlacements(List<TerminalKittyImageState> images, List<TerminalKittyPlacementState> placements)
    {
        int[] perScreen = [0, 0];
        foreach (TerminalKittyPlacementState p in placements.Where(p => p is not null && p.Image >= 0 && p.Image < images.Count))
            perScreen[images[p.Image].Alt ? 1 : 0]++;
        if (perScreen.Max() > MaxKittyImagePlacements)
            throw StateTransferValidation.Reject("Kitty placements", $"more than {MaxKittyImagePlacements} on one screen");
        foreach (TerminalKittyPlacementState? placement in placements)
        {
            var state = StateTransferValidation.RequirePresent(placement, "Kitty placement");
            if (state.Image < 0 || state.Image >= images.Count) throw StateTransferValidation.Reject("Kitty placement", "names no image");
            RequireCellRectangle(state.Width, state.Height, "Kitty placement");
        }
    }

    private static void ValidateITermImage(TerminalITermImageState? image)
    {
        var state = StateTransferValidation.RequirePresent(image, ITermImageWhat);
        byte[] data = StateTransferValidation.RequirePresent(state.Data, "iTerm2 image data");
        RequireCellRectangle(state.Width, state.Height, ITermImageWhat);
        if (data.Length == 0)
        {
            RequirePixelSize(state.PixelWidth, state.PixelHeight, ITermImageWhat); // evicted bytes
            return;
        }
        if (!ImageHeaderProbe.TryGetSize(data, out _, out int w, out int h) || (w, h) != (state.PixelWidth, state.PixelHeight))
            throw StateTransferValidation.Reject(ITermImageWhat, "data does not declare the stated size");
        // The live parser refuses these before decoding; a snapshot must not hand them to a decoder.
        if (w > 2000 || h > 2000) throw StateTransferValidation.Reject(ITermImageWhat, "pixel size over the 2000-pixel limit");
    }

    private static void RequirePixelSize(int width, int height, string what)
    {
        if (width is < 1 or > 2000 || height is < 1 or > 2000) throw StateTransferValidation.Reject(what, "pixel size out of range");
    }

    private static void RequireCellRectangle(int width, int height, string what)
    {
        if (width is < 1 or > 200 || height is < 1 or > 200) throw StateTransferValidation.Reject(what, "cell size out of range");
    }

    private static bool IsRaw(KittyImageEncoding encoding) => encoding.Format is 24 or 32;

    private static int RawBytesPerPixel(KittyImageEncoding encoding) => encoding.Format == 24 ? 3 : 4;

    private static bool RawSizeInRange(KittyImageEncoding encoding) =>
        encoding.RawWidth is >= 1 and <= 2000 && encoding.RawHeight is >= 1 and <= 2000;

    /// <summary>
    /// The pixel size Kitty bytes declare, as live transmission measures it; null if none. A compressed
    /// image is inflated as a stream that only counts and keeps the header, so validating costs no large
    /// allocation, and the bytes it inflates are added to <paramref name="inflated"/>.
    /// </summary>
    private static (int, int)? DeclaredKittySize(byte[] data, KittyImageEncoding encoding, ref long inflated)
    {
        if (IsRaw(encoding) && !RawSizeInRange(encoding)) return null;
        if (!encoding.Compressed) return MeasureKittySize(data, encoding, ref inflated);
        var measured = CompressedSizeCache.GetValue(data, static _ => new());
        KittyImageEncoding key = CacheKey(encoding);
        int hash = ContentHash(data);
        if (measured.TryGetValue(key, out CompressedSize? known) && known.Hash == hash)
        {
            // Measured already (an earlier validation pass of this restore, or the same array named
            // twice): no inflation now, but its size still counts toward the snapshot limit.
            inflated += known.Inflated;
            return known.Size;
        }
        long before = inflated;
        (int, int)? size = MeasureKittySize(data, encoding, ref inflated);
        measured[key] = new CompressedSize(hash, size, inflated - before);
        return size;
    }

    /// <summary>
    /// Sizes measured from compressed Kitty bytes, keyed by the array (and checked against its content),
    /// so the two validation passes of a restore (<see cref="TerminalStateTransfer.Restore"/> and then
    /// <see cref="ImportState"/>) inflate each image only once.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[],
        System.Collections.Concurrent.ConcurrentDictionary<KittyImageEncoding, CompressedSize>> CompressedSizeCache = new();

    private sealed record CompressedSize(int Hash, (int, int)? Size, long Inflated);

    /// <summary>
    /// What sizing compressed bytes depends on: a raw image's format and size, or only that it is a
    /// container (<c>f=0</c> and <c>f=100</c> are measured the same way), so equivalent entries share
    /// one measurement.
    /// </summary>
    private static KittyImageEncoding CacheKey(KittyImageEncoding encoding) =>
        IsRaw(encoding) ? encoding : encoding with { Format = 100, RawWidth = 0, RawHeight = 0 };

    private static int ContentHash(byte[] data)
    {
        var hash = new HashCode();
        hash.AddBytes(data);
        return hash.ToHashCode();
    }

    /// <summary>
    /// What validating compressed Kitty bytes in a snapshot inflates, from what they inflated to live
    /// (<paramref name="inflated"/>): the whole stream, and once more when the start kept to read a
    /// container header does not hold it (see <see cref="MeasureKittySize"/> and
    /// <see cref="ContainerSize"/>, which this must match). 0 for uncompressed bytes.
    /// <see cref="ImageSourceBudget"/> keeps the total within the restore limit.
    /// </summary>
    internal static long SnapshotInflationCost(byte[] inflated, KittyImageEncoding encoding)
    {
        if (!encoding.Compressed) return 0;
        long cost = inflated.LongLength;
        if (!IsRaw(encoding) && inflated.Length > InflatedHeaderPrefixBytes
            && !ImageHeaderProbe.TryGetSize(inflated.AsSpan(0, InflatedHeaderPrefixBytes), out _, out _, out _))
            cost += inflated.LongLength;
        return cost;
    }

    private static (int, int)? MeasureKittySize(byte[] data, KittyImageEncoding encoding, ref long inflated)
    {
        long rawLength = (long)encoding.RawWidth * encoding.RawHeight * RawBytesPerPixel(encoding);
        long length = data.LongLength;
        byte[] header = data;
        if (encoding.Compressed)
        {
            long bound = IsRaw(encoding) ? rawLength : AnsiParser.KittyMaxInflatedContainerBytes;
            if (!InflatedLength(data, bound, out length, out header)) return null;
            inflated += length;
        }
        if (IsRaw(encoding)) return length == rawLength ? (encoding.RawWidth, encoding.RawHeight) : null;
        return ContainerSize(data, encoding, header, length, ref inflated);
    }

    /// <summary>
    /// A container's size from its header. When only the kept start of a compressed container was
    /// read and it did not hold the header (a JPEG frame after large metadata), the whole container is
    /// inflated, as live transmission reads all of it, within the same bounds; that inflation counts
    /// toward the snapshot limit too.
    /// </summary>
    private static (int, int)? ContainerSize(byte[] data, KittyImageEncoding encoding, byte[] header, long length, ref long inflated)
    {
        if (!ImageHeaderProbe.TryGetSize(header, out _, out int w, out int h))
        {
            if (!encoding.Compressed || length <= header.Length || RetainedKittyPixels(data, encoding) is not { } all) return null;
            inflated += all.LongLength;
            if (!ImageHeaderProbe.TryGetSize(all, out _, out w, out h)) return null;
        }
        return w <= 2000 && h <= 2000 ? (w, h) : null;
    }

    /// <summary>Inflates a zlib stream, counting up to <paramref name="bound"/> bytes and keeping only the start.</summary>
    private static bool InflatedLength(byte[] compressed, long bound, out long length, out byte[] prefix)
    {
        length = 0;
        prefix = [];
        try
        {
            using var source = new MemoryStream(compressed);
            using var zlib = new System.IO.Compression.ZLibStream(source, System.IO.Compression.CompressionMode.Decompress);
            var start = new byte[(int)Math.Min(bound, InflatedHeaderPrefixBytes)];
            var buffer = new byte[81920];
            int n;
            while ((n = zlib.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (length < start.Length) buffer.AsSpan(0, (int)Math.Min(n, start.Length - length)).CopyTo(start.AsSpan((int)length));
                length += n;
                InflationMeter.Bytes += n;
                if (length > bound) return false;
            }
            prefix = length < start.Length ? start[..(int)length] : start;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return false;
        }
    }

    /// <summary>The bytes after <c>o=z</c> inflation, bounded as live transmission bounds them.</summary>
    private static byte[]? RetainedKittyPixels(byte[] data, KittyImageEncoding encoding)
    {
        if (IsRaw(encoding) && !RawSizeInRange(encoding)) return null;
        if (!encoding.Compressed) return data;
        long bound = IsRaw(encoding)
            ? (long)encoding.RawWidth * encoding.RawHeight * RawBytesPerPixel(encoding)
            : AnsiParser.KittyMaxInflatedContainerBytes;
        return AnsiParser.InflateZlib(data, (int)bound);
    }

    // ---- Import ----------------------------------------------------------------------------

    /// <summary>
    /// Restores retained Kitty assets, their placements and iTerm2 images, undecoded, at their
    /// transferred positions. Like <see cref="ImportSixelState"/> it never replays placement or moves
    /// the cursor. Runs after the old images were cleared. An image whose bytes do not fit this buffer's
    /// budget, or that arrive without bytes (evicted at the source), is still restored, with its id and
    /// cells: it stays undecoded, and a later snapshot carries it without bytes too.
    /// </summary>
    private void ImportImageState(TerminalStateSnapshot snapshot, Dictionary<TerminalImage, int> order, int expiredRows)
    {
        _nextKittyImageId = snapshot.KittyNextImageId;
        var kittyStates = snapshot.KittyImages ?? [];
        var itermStates = snapshot.ITermImages ?? [];
        // iTerm2 images exist only as placements: one that does not survive (its rows were in history
        // this buffer could not keep) is left out before its bytes are charged, so it evicts nothing.
        TerminalImage?[] itermImages = itermStates.Select(state => ImportedPlacement(new UndecodedImage(state.PixelWidth, state.PixelHeight),
            new PlacementGeometry(state.X, state.Y, state.Width, state.Height, state.Alt, state.ClipTop, state.ClipBottom, state.ClipRight),
            expiredRows)).ToArray();
        (ImageSource[] kittySources, ImageSource?[] itermSources) = RetainImportedSources(kittyStates, itermStates, itermImages);
        List<KittyImageAsset> assets = ImportKittyAssets(kittyStates, kittySources);
        ImportKittyPlacements(snapshot.KittyPlacements ?? [], assets, order, expiredRows);
        ImportITermImages(itermStates, itermImages, itermSources, order);
    }

    /// <summary>
    /// Retains the bytes in the exported eviction order, not grouped by protocol, so the budget evicts
    /// the same image next as the source would; entries without an order keep theirs. The bytes are
    /// copies, so later edits to the snapshot object cannot change what this buffer retains. iTerm2
    /// images without a surviving placement in <paramref name="itermImages"/> are not retained.
    /// </summary>
    private (ImageSource[] Kitty, ImageSource?[] ITerm) RetainImportedSources(List<TerminalKittyImageState> kittyStates,
        List<TerminalITermImageState> itermStates, TerminalImage?[] itermImages)
    {
        var kitty = new ImageSource[kittyStates.Count];
        var iterm = new ImageSource?[itermStates.Count];
        var retention = kittyStates.Select((state, i) => (Order: state.SourceOrder, Sequence: i, Kitty: true, Index: i))
            .Concat(itermStates.Select((state, i) => (Order: state.SourceOrder, Sequence: kittyStates.Count + i, Kitty: false, Index: i))
                .Where(entry => itermImages[entry.Index] is not null))
            .OrderBy(entry => entry.Order ?? int.MaxValue).ThenBy(entry => entry.Sequence);
        foreach (var entry in retention)
        {
            byte[] data = entry.Kitty ? kittyStates[entry.Index].Data : itermStates[entry.Index].Data;
            ImageSource source = data.Length == 0 ? ImageSources.Evicted()
                : ImageSources.RetainCopy(data, entry.Kitty ? ImportedInflationCost(kittyStates[entry.Index]) : 0);
            if (entry.Kitty) kitty[entry.Index] = source;
            else iterm[entry.Index] = source;
        }
        return (kitty, iterm);
    }

    /// <summary>
    /// What validating an imported Kitty image inflated, charged to <see cref="ImageSources"/> as live
    /// transmission charges it. Validation measured it already, so this reads the cached measurement.
    /// </summary>
    private static long ImportedInflationCost(TerminalKittyImageState state)
    {
        long cost = 0;
        if (state.Compressed) DeclaredKittySize(state.Data, new KittyImageEncoding(state.Format, state.Compressed, state.RawWidth, state.RawHeight), ref cost);
        return cost;
    }

    private List<KittyImageAsset> ImportKittyAssets(List<TerminalKittyImageState> states, ImageSource[] sources)
    {
        var assets = new List<KittyImageAsset>(states.Count);
        for (int i = 0; i < states.Count; i++)
        {
            TerminalKittyImageState state = states[i];
            var asset = new KittyImageAsset(state.Id, state.Number, state.Alt, new UndecodedImage(state.Width, state.Height), state.Width, state.Height)
            {
                Source = sources[i],
                Encoding = new KittyImageEncoding(state.Format, state.Compressed, state.RawWidth, state.RawHeight),
            };
            _kittyImageAssets.Add(asset);
            assets.Add(asset);
        }
        return assets;
    }

    private void ImportKittyPlacements(List<TerminalKittyPlacementState> states, List<KittyImageAsset> assets, Dictionary<TerminalImage, int> order,
        int expiredRows)
    {
        foreach (TerminalKittyPlacementState state in states)
        {
            KittyImageAsset asset = assets[state.Image];
            var geometry = new PlacementGeometry(state.X, state.Y, state.Width, state.Height, asset.IsAltScreen,
                state.ClipTop, state.ClipBottom, state.ClipRight);
            if (ImportedPlacement(asset.Handle, geometry, expiredRows) is not { } image) continue;
            image.KittyImageId = asset.Id;
            image.KittyAsset = asset;
            image.KittyPlacementId = state.Placement == 0 ? null : state.Placement;
            image.PreserveAspectRatio = state.PreserveAspectRatio;
            _images.Add(image);
            if (state.Order is int z) order[image] = z;
        }
    }

    private void ImportITermImages(List<TerminalITermImageState> states, TerminalImage?[] images, ImageSource?[] sources,
        Dictionary<TerminalImage, int> order)
    {
        for (int i = 0; i < states.Count; i++)
        {
            if (images[i] is not { } image) continue;
            image.EncodedSource = sources[i];
            image.PreserveAspectRatio = states[i].PreserveAspectRatio;
            _images.Add(image);
            if (states[i].Order is int z) order[image] = z;
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

    /// <summary>A placement's cell rectangle, screen and clipping as a snapshot carries it.</summary>
    private readonly record struct PlacementGeometry(int X, int Y, int Width, int Height, bool Alt, int ClipTop, int ClipBottom, int ClipRight);

    /// <summary>
    /// A transferred placement; null when nothing of it is left. Unlike SIXEL, it keeps a position past
    /// the right or bottom edge (within <see cref="MaxImportedOffscreenCells"/>), where the live buffer
    /// keeps it too, so it shows again when the screen grows, and a start left of column 0, where
    /// deleting characters can shift it, partly or wholly off the screen. A main-screen placement moves
    /// up by <paramref name="expiredRows"/>, the history rows this buffer could not keep, as eviction
    /// moves it: unclipped, and dropped once wholly above the first row.
    /// </summary>
    private TerminalImage? ImportedPlacement(object handle, PlacementGeometry geometry, int expiredRows)
    {
        if (!geometry.Alt) geometry = geometry with { Y = geometry.Y - expiredRows };
        int top = Math.Clamp(geometry.ClipTop, 0, geometry.Height);
        int bottom = Math.Clamp(geometry.ClipBottom, 0, geometry.Height - top);
        int right = Math.Clamp(geometry.ClipRight, 0, geometry.Width);
        if (top + bottom >= geometry.Height || right >= geometry.Width) return null;
        // Wholly above the first row, a placement never moves back (scrolling down moves only what
        // overlaps the region), so nothing of it is left; export does not carry one either.
        if (geometry.Y + geometry.Height <= 0) return null;
        int lastRow = geometry.Alt ? Rows - 1 : _scrollback.Count + Rows - 1;
        // Deleting characters (CSI P) can shift a placement left of column 0, even all the way off the
        // screen, where the live buffer keeps it invisible; it stays there, within the same bound.
        return new TerminalImage(handle, Math.Clamp(geometry.X, -MaxImportedOffscreenCells, Cols - 1 + MaxImportedOffscreenCells),
            Math.Clamp(geometry.Y, 1 - geometry.Height, lastRow + MaxImportedOffscreenCells), geometry.Width, geometry.Height)
        {
            IsAltScreenImage = geometry.Alt,
            ClipTopRows = top,
            ClipBottomRows = bottom,
            ClipRightColumns = right,
        };
    }

    // ---- Decoding --------------------------------------------------------------------------

    /// <summary>
    /// Decodes transferred Kitty and iTerm2 images with the host's decoder. Pixels are kept only at
    /// the size the bytes declare, as in live placement; anything else stays undecoded. Kitty pixels are
    /// bounded by the per-screen quotas; iTerm2 pixels by <see cref="MaxTransferredITermPixelBytes"/>,
    /// newest first.
    /// </summary>
    internal void DecodeTransferredImages(IImageDecoder? decoder)
    {
        if (decoder is null) return;
        Lock.EnterWriteLock();
        try
        {
            long inflationBudget = MaxSnapshotInflatedBytes;
            foreach (KittyImageAsset asset in _kittyImageAssets.Where(asset => asset.Handle is UndecodedImage))
                DecodeTransferredKitty(asset, decoder, ref inflationBudget);
            DecodeTransferredITerm(decoder);
        }
        finally { Lock.ExitWriteLock(); }
        Invalidate();
    }

    /// <remarks>
    /// Compressed images draw on <paramref name="inflationBudget"/>: once it is spent they stay
    /// undecoded. With validation measured once (see <see cref="CompressedSizeCache"/>), a whole restore
    /// inflates at most twice <see cref="MaxSnapshotInflatedBytes"/> plus one image.
    /// </remarks>
    private void DecodeTransferredKitty(KittyImageAsset asset, IImageDecoder decoder, ref long inflationBudget)
    {
        if (asset.Source?.Data is not { } data || asset.Encoding is not { } encoding) return;
        if (encoding.Compressed && inflationBudget <= 0) return;
        if (RetainedKittyPixels(data, encoding) is not { } pixels) return;
        if (encoding.Compressed) inflationBudget -= pixels.LongLength;
        TransferredDecode decode = IsRaw(encoding)
            ? (out int w, out int h) => decoder.DecodeRawImage(pixels, RawBytesPerPixel(encoding), encoding.RawWidth, encoding.RawHeight, out w, out h)
            : (out int w, out int h) => decoder.DecodeImageBytes(pixels, out w, out h);
        if (DecodeTransferred(asset.PixelWidth, asset.PixelHeight, decode) is not { } handle) return;
        asset.Handle = handle;
        foreach (TerminalImage image in _images.Where(image => ReferenceEquals(image.KittyAsset, asset))) image.ImageHandle = handle;
    }

    private void DecodeTransferredITerm(IImageDecoder decoder)
    {
        long budget = MaxTransferredITermPixelBytes;
        for (int i = _images.Count - 1; i >= 0; i--)
        {
            TerminalImage image = _images[i];
            if (image.KittyAsset is not null || image.ImageHandle is not UndecodedImage undecoded || image.EncodedSource?.Data is not { } data) continue;
            long charge = (long)undecoded.PixelWidth * undecoded.PixelHeight * 4;
            if (charge > budget) continue;
            if (DecodeTransferred(undecoded.PixelWidth, undecoded.PixelHeight, (out int w, out int h) => decoder.DecodeImageBytes(data, out w, out h)) is not { } handle) continue;
            image.ImageHandle = handle;
            budget -= charge;
        }
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
