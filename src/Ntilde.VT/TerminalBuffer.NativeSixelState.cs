namespace Ntilde.VT;

public partial class TerminalBuffer
{
    private const string NativeSixelImportContext = "native SIXEL import";
    private const string NativeSixelStateContext = "native SIXEL state";

    /// <summary>Owns pre-admitted native wrappers until the atomic import cut; rollback disposal is outside VT locks.</summary>
    internal sealed class PreparedStateImport : IDisposable
    {
        internal required ValidatedBufferState Validated { get; set; }
        internal List<TerminalImage>? NativeImages { get; init; }
        internal TerminalBuffer? Owner { get; init; }
        internal SixelCellSize? NativeMetrics { get; init; }
        internal bool Committed { get; set; }
        internal bool Disposed { get; private set; }

        /// <summary>
        /// Releases uncommitted owners outside VT locks, requeueing failed cleanup without early credit or exception replacement.
        /// </summary>
        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            if (Committed || NativeImages is null || Owner is not { } owner) return;
            bool queued = false;
            foreach (var image in NativeImages)
                queued |= owner.DisposeStagedSixel(image.ImageHandle);
            NativeImages.Clear();
            if (queued)
            {
                try { owner.Invalidate(); }
                catch { /* A host notification failure must not replace the original import refusal. */ }
            }
        }
    }

    private bool DisposeStagedSixel(object handle)
    {
        if (handle is SixelSource source)
        {
            source.Dispose();
            return false;
        }
        var native = (INativeSixelImage)handle;
        try { native.Dispose(); }
        catch
        {
            RetireUnplacedNativeSixel(native);
            return true;
        }
        return false;
    }

    /// <summary>Reserves all native work before resize/clear; prior owners remain charged until actual disposal.</summary>
    internal PreparedStateImport PrepareStateImport(TerminalStateSnapshot snapshot, IImageDecoder? decoder, AnsiParser? parser = null)
    {
        if (CurrentThreadHoldsBufferLock)
            throw new InvalidOperationException("State import must stage outside VT locks.");
        var validated = ValidateStateForImport(snapshot);
        if (NativeSixelBudget is not { } budget) return new() { Validated = validated };
        // Copy native placement facts before invoking any host decoder. Later stages consume
        // these private records rather than rereading mutable caller-owned placement DTOs.
        var states = (snapshot.Sixels ?? []).Select(state => new TerminalSixelImageState
        {
            Source = state.Source,
            X = state.X,
            Y = state.Y,
            Width = state.Width,
            Height = state.Height,
            Alt = state.Alt,
            ClipTop = state.ClipTop,
            ClipBottom = state.ClipBottom,
            ClipRight = state.ClipRight,
            NativeRaster = state.NativeRaster,
            SourceOnly = state.SourceOnly,
            Order = state.Order,
        }).ToList();
        if (!SixelSources.FitsRetentionSet(states.Count, states.Sum(state => (long)state.Source.Length)))
            throw StateTransferValidation.Reject(NativeSixelImportContext, "source retention cannot preserve the complete placement set");
        var images = new List<TerminalImage>(states.Count);
        var prepared = new PreparedStateImport
        {
            Validated = validated,
            NativeImages = images,
            Owner = this,
            NativeMetrics = snapshot.NativeSixelCellMetrics,
        };
        try
        {
            foreach (var state in states) images.Add(CreateNativeImportedPlacement(state, budget, decoder));
            // Host decoding is a synchronous yield. Refresh the complete envelope validation
            // afterwards, before Resize/clear/commit, and never mix changed native DTOs with
            // wrappers already admitted from the previous facts. Caller-concurrent mutation
            // remains outside this DTO's existing ownership contract.
            prepared.Validated = ValidateStateForImport(snapshot);
            if (parser is not null) ValidateParserForImport(parser, snapshot);
            RequireUnchangedStagedNativeFacts(snapshot, prepared.NativeMetrics, images, states);
            return prepared;
        }
        catch
        {
            prepared.Dispose();
            throw;
        }
    }

    /// <summary>Builds an unpublished placement from private facts and a bounded owner; it commits no retained source.</summary>
    private TerminalImage CreateNativeImportedPlacement(TerminalSixelImageState state, NativeSixelBudget budget, IImageDecoder? decoder)
    {
        var intent = StateTransferValidation.RequirePresent(state.NativeRaster, "native SIXEL raster");
        SixelSource source;
        object handle;
        if (state.SourceOnly)
        {
            source = SixelSources.CreateProvisional(state.Source);
            handle = source;
        }
        else
        {
            handle = CreateNativeImportOwner(state.Source, intent, budget, decoder, out source);
        }
        return new(handle, state.X, state.Y, state.Width, state.Height)
        {
            IsSixelImage = true,
            IsAltScreenImage = state.Alt,
            SixelSource = source,
            NativeRaster = intent,
            ClipTopRows = state.ClipTop,
            ClipBottomRows = state.ClipBottom,
            ClipRightColumns = state.ClipRight,
        };
    }

    /// <summary>Reserves before invoking the host and retains unknown failed allocations rather than crediting unproved disposal.</summary>
    private INativeSixelImage CreateNativeImportOwner(string data, SixelRasterIntent intent, NativeSixelBudget budget,
        IImageDecoder? decoder, out SixelSource source)
    {
        var allocation = budget.TryReserve(intent.CanvasWidth, intent.CanvasHeight, data.Length)
            ?? throw StateTransferValidation.Reject(NativeSixelImportContext, "outstanding owners leave insufficient admission credit");
        source = SixelSources.CreateProvisional(data);
        if (decoder is null) return new NativeSixelSourceOwner(source, allocation);
        if (decoder is not INativeSixelDecoder native)
        {
            source.Dispose();
            budget.CompleteDisposal(allocation);
            throw StateTransferValidation.Reject(NativeSixelImportContext, "decoder does not honor bounded ownership");
        }
        INativeSixelImage? owner;
        try { owner = native.DecodeNativeSixel(source, allocation); }
        catch
        {
            source.Dispose();
            throw; // Unknown decoder failure may own resources: retain its bounded charge.
        }
        if (owner is null)
        {
            source.Dispose();
            budget.CompleteDisposal(allocation);
            throw StateTransferValidation.Reject(NativeSixelImportContext, "decoder refused the admitted source");
        }
        if (!ReferenceEquals(owner.Allocation, allocation) || !ReferenceEquals(owner.Source, source))
        {
            source.Dispose();
            throw StateTransferValidation.Reject(NativeSixelImportContext, "decoder ownership contract failed");
        }
        return owner;
    }

    /// <summary>
    /// Checks the parser half only for whole Restore, including native canonical agreement before and after host decoding.
    /// </summary>
    internal void ValidateParserForImport(AnsiParser parser, TerminalStateSnapshot snapshot)
    {
        AnsiParser.ValidateOutputTail(snapshot.DecoderTail);
        if (snapshot.Parser is not { } state) return;
        parser.ValidateParserState(state);
        if (NativeSixelBudget is not null && snapshot.NativeSixelCellMetrics is { } metrics &&
            ParserCellMetrics(state) != metrics)
            throw StateTransferValidation.Reject(NativeSixelStateContext, "parser and buffer canonical metrics disagree");
    }

    /// <summary>
    /// Rejects changed source, placement, crop or metric facts after the synchronous host callback before any state effect.
    /// </summary>
    private static void RequireUnchangedStagedNativeFacts(TerminalStateSnapshot snapshot, SixelCellSize? metrics, List<TerminalImage> images, List<TerminalSixelImageState> stagedStates)
    {
        var states = snapshot.Sixels ?? [];
        if (snapshot.NativeSixelCellMetrics != metrics || states.Count != images.Count)
            throw StateTransferValidation.Reject(NativeSixelImportContext, "decoder callback changed staged metrics or placement count");
        for (int i = 0; i < states.Count; i++)
        {
            var state = states[i];
            var image = images[i];
            if (state.X != image.CellX || state.Y != image.CellY || state.Width != image.CellWidth || state.Height != image.CellHeight ||
                state.Alt != image.IsAltScreenImage || state.ClipTop != image.ClipTopRows || state.ClipBottom != image.ClipBottomRows ||
                state.Order != stagedStates[i].Order || state.ClipRight != image.ClipRightColumns || state.NativeRaster != image.NativeRaster ||
                state.SourceOnly != stagedStates[i].SourceOnly ||
                !string.Equals(state.Source, image.SixelSource?.Data, StringComparison.Ordinal))
                throw StateTransferValidation.Reject(NativeSixelImportContext, "decoder callback changed staged placement facts or source availability");
        }
    }

    private static SixelCellSize? ParserCellMetrics(AnsiParserState state) =>
        state.SixelCellWidthPx is float width && state.SixelCellHeightPx is float height ? new(width, height) : null;

    /// <summary>Rejects native geometry and semantics before any target state, source or decoder effect.</summary>
    private static void ValidateNativeSixelState(TerminalStateSnapshot snapshot)
    {
        if (snapshot.RequiredSixelRasterPolicy != SixelRasterPolicy.PresenterNativeVersion)
            throw StateTransferValidation.Reject(NativeSixelStateContext, "policy does not match the admitted native generation");
        RequireNativeSixelMetrics(snapshot.NativeSixelCellMetrics);
        if (snapshot.Sixels is not { } states) return;
        if (states.Count > SixelSourceBudget.DefaultMaxSources)
            throw StateTransferValidation.Reject(NativeSixelStateContext, "placement source count exceeds its bound");
        long chars = 0;
        foreach (var entry in states)
        {
            var state = StateTransferValidation.RequirePresent(entry, "native SIXEL placement");
            var source = StateTransferValidation.RequirePresent(state.Source, "native SIXEL source");
            var intent = StateTransferValidation.RequirePresent(state.NativeRaster, "native SIXEL intent");
            chars += source.Length;
            if (source.Length == 0 || chars > SixelSourceBudget.DefaultMaxChars || !AnsiParser.IsSixel(source) ||
                intent.Version != SixelRasterPolicy.PresenterNativeVersion ||
                SixelGeometry.Measure(source) != (intent.CanvasWidth, intent.CanvasHeight))
                throw StateTransferValidation.Reject(NativeSixelStateContext, "source canvas or policy is invalid");
            if (state.X < 0 || state.X >= snapshot.Cols || state.Y < -SixelGeometry.DefaultMaxPixelDimension ||
                state.Y >= (state.Alt ? snapshot.Rows : (long)snapshot.Rows + snapshot.ScrollbackRowCount) ||
                state.Width is < 1 or > SixelGeometry.DefaultMaxPixelDimension ||
                state.Height is < 1 or > SixelGeometry.DefaultMaxPixelDimension ||
                state.ClipTop < 0 || state.ClipTop > state.Height || state.ClipBottom < 0 || state.ClipBottom > state.Height ||
                state.ClipRight < 0 || state.ClipRight > state.Width)
                throw StateTransferValidation.Reject("native SIXEL placement", "geometry is outside its bounded domain");
        }
    }
}
