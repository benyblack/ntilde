namespace Ntilde.VT.Tests;

public sealed class NativeSixelLayoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Growing_after_a_metric_change_preserves_the_full_native_crop(bool alternateScreen)
    {
        var buffer = new TerminalBuffer(10, 6, SixelRasterPolicy.PresenterNativeVersion);
        var parser = new AnsiParser(buffer) { SixelCellSizeOverride = new(10, 20) };
        if (alternateScreen) parser.Process("\x1b[?1049h");
        parser.Process("\x1b[?80h\x1bPq#2!16~-!16~-!16N\x1b\\");
        var original = Assert.Single(buffer.Images).NativeRaster;
        Assert.NotNull(original);
        parser.SixelCellSizeOverride = new(1, 1);
        buffer.Resize(11, 7);
        Assert.Equal(original, Assert.Single(buffer.Images).NativeRaster);
        buffer.ClearImages();
    }

    [Fact]
    public void Reflow_can_restore_an_image_beyond_the_old_viewport_height()
    {
        var buffer = new TerminalBuffer(10, 4, SixelRasterPolicy.PresenterNativeVersion);
        var parser = new AnsiParser(buffer) { SixelCellSizeOverride = new(10, 20) };
        parser.Process("one\r\ntwo\r\nthree\r\nfour\r\nfive\r\nsix\r\n\x1bPq~\x1b\\");
        var image = Assert.Single(buffer.Images);
        Assert.True(image.CellY >= buffer.Rows);

        buffer.Resize(11, 12);

        Assert.Same(image, Assert.Single(buffer.Images));
        Assert.Equal(new SixelSourceCrop(0, 0, 1, 6), image.NativeRaster!.Crop);
        Assert.Equal('f', buffer.ViewportRows[0].Cells[0].Character);
        buffer.ClearImages();
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, SixelRasterPolicy.PresenterNativeVersion)]
    [InlineData(true, SixelRasterPolicy.PresenterNativeVersion)]
    public void Scrolling_sixel_outside_vertical_margins_uses_the_page_bounds(bool alternateScreen, int policy)
    {
        var buffer = new TerminalBuffer(10, 6, policy);
        var parser = new AnsiParser(buffer) { SixelCellSizeOverride = new(10, 20), RetainSixelSources = true };
        if (alternateScreen) parser.Process("\x1b[?1049h");
        parser.Process("\x1b[3;5r\x1b[1;1H\x1bPq~\x1b\\");
        var image = Assert.Single(buffer.Images);
        Assert.Equal((0, 0, 0), (image.CellY, image.ClipTopRows, image.ClipBottomRows));
        if (policy == 0) Assert.Null(image.NativeRaster);
        else Assert.Equal(new SixelSourceCrop(0, 0, 1, 6), image.NativeRaster!.Crop);
        buffer.ClearImages();
    }

    [Fact]
    public void Crop_uses_the_hidden_main_banks_double_width_anchor()
    {
        var buffer = new TerminalBuffer(12, 6, SixelRasterPolicy.PresenterNativeVersion);
        var parser = new AnsiParser(buffer) { SixelCellSizeOverride = new(10, 20) };
        parser.Process("\x1b[1;4H\x1b#6\x1bPq#2!40~\x1b\\");
        Assert.Equal(40, Assert.Single(buffer.Images).NativeRaster!.Crop.Right);
        parser.Process("\x1b[?1049h");
        buffer.Resize(8, 6);
        parser.Process("\x1b[?1049l");
        buffer.Resize(12, 6);
        Assert.Equal(20, Assert.Single(buffer.Images).NativeRaster!.Crop.Right);
        buffer.ClearImages();
    }

    [Fact]
    public void Margin_scroll_retires_an_image_whose_native_overhang_crosses_the_band()
    {
        var buffer = new TerminalBuffer(20, 8, SixelRasterPolicy.PresenterNativeVersion);
        var parser = new AnsiParser(buffer) { SixelCellSizeOverride = new(10, 20) };
        parser.Process("\x1bPq#2!16~-!16~-!16N\x1b\\");
        Assert.Equal(2, Assert.Single(buffer.Images).CellWidth);
        parser.SixelCellSizeOverride = new(1, 1);
        parser.Process("\x1b[?69h\x1b[4;10s\x1b[S");
        Assert.Empty(buffer.Images);
    }

    [Fact]
    public void Repeated_headless_import_returns_retired_owner_credit()
    {
        var source = new TerminalBuffer(10, 6, SixelRasterPolicy.PresenterNativeVersion);
        var parser = new AnsiParser(source) { SixelCellSizeOverride = new(10, 20) };
        parser.Process("\x1bPq~\x1b\\");
        var snapshot = source.ExportState(0);

        var budget = new NativeSixelBudget(maxPlacements: 2);
        var target = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, budget, metrics: null);
        for (int i = 0; i < 3; ++i)
        {
            target.ImportState(snapshot);
            Assert.Equal(1, budget.Usage.Placements);
        }
        target.ClearImages();
        source.ClearImages();
    }

    [Fact]
    public void Headless_native_parse_retains_transferable_production_intent()
    {
        var budget = new NativeSixelBudget();
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, budget, new SixelCellSize(10, 20));
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false)
        {
            SixelCellSizeOverride = new(10, 20),
            RetainSixelSources = true,
        };
        parser.Process("\x1b[?80h\x1bPq#2!16~-!16~-!16N\x1b\\");
        Assert.Single(buffer.Images);
        var state = Assert.Single(buffer.ExportState(0).Sixels!);
        var intent = Assert.IsType<SixelRasterIntent>(state.NativeRaster);
        Assert.Equal((16, 18), (intent.CanvasWidth, intent.CanvasHeight));
        Assert.Equal(new SixelSourceCrop(0, 0, 16, 18), intent.Crop);
        Assert.Equal((2, 1), (state.Width, state.Height));
    }

    [Fact]
    public void Native_import_preserves_intent_and_policy_without_public_admission()
    {
        var source = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, new NativeSixelBudget(), new SixelCellSize(10, 20));
        var parser = new AnsiParser(source, false) { SixelCellSizeOverride = new(10, 20), RetainSixelSources = true };
        parser.Process("\x1b[?80h\x1bPq#2!16~-!16~-!16N\x1b\\");
        var state = source.ExportState(0);
        state.Parser = parser.ExportState();
        var destination = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, new NativeSixelBudget(), new SixelCellSize(10, 20));
        var restored = new AnsiParser(destination, false);
        TerminalStateTransfer.Restore(destination, restored, state);
        var actual = Assert.Single(destination.ExportState(0).Sixels!);
        Assert.Equal(Assert.Single(state.Sixels!).NativeRaster, actual.NativeRaster);
        Assert.Equal(state.NativeSixelCellMetrics, destination.NativeSixelCellMetrics);
        Assert.Equal(SixelRasterPolicy.PresenterNativeVersion, SixelRasterPolicy.EnabledVersion);
        Assert.Throws<InvalidOperationException>(() => new TerminalBuffer(10, 6).ImportState(state));
    }

    [Fact]
    public void Full_cap_import_refusal_is_before_clear_resize_or_source_effects()
    {
        var source = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, new NativeSixelBudget(), new SixelCellSize(10, 20));
        new AnsiParser(source, false).Process("\x1b[?80h\x1bPq~\x1b\\");
        var state = source.ExportState(0);
        state.Parser = null!; // Buffer-only payload; do not let an unrelated parser check mask admission.
        var budget = new NativeSixelBudget(maxPlacements: 1);
        var destination = TerminalBuffer.CreateNativeSixelTestBuffer(20, 8, budget, new SixelCellSize(10, 20));
        var parser = new AnsiParser(destination, false);
        parser.Process("held\x1b[?80h\x1bPq~\x1b\\");
        var image = Assert.Single(destination.Images);
        var usage = budget.Usage;
        int retained = destination.SixelSources.RetainedChars;
        Assert.Throws<InvalidOperationException>(() => TerminalStateTransfer.Restore(destination, parser, state));
        Assert.Equal((20, 8), (destination.Cols, destination.Rows));
        Assert.Same(image, Assert.Single(destination.Images));
        Assert.Equal(usage, budget.Usage);
        Assert.Equal(retained, destination.SixelSources.RetainedChars);
    }

    [Fact]
    public void Canonical_metric_disagreement_refuses_restore_before_mutation()
    {
        var source = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, new NativeSixelBudget(), new SixelCellSize(10, 20));
        var state = source.ExportState(0);
        state.Parser = new AnsiParser(source, false).ExportState();
        state.Parser.SixelCellHeightPx = 21;
        var destination = TerminalBuffer.CreateNativeSixelTestBuffer(20, 8, new NativeSixelBudget(), new SixelCellSize(10, 20));
        Assert.Throws<InvalidOperationException>(() => TerminalStateTransfer.Restore(destination, new AnsiParser(destination, false), state));
        Assert.Equal((20, 8), (destination.Cols, destination.Rows));
        Assert.Empty(destination.Images);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.5f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Invalid_native_metrics_are_rejected_without_changing_canonical_state(float height)
    {
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, new NativeSixelBudget(), new SixelCellSize(10, 20));
        var parser = new AnsiParser(buffer, false);
        var previous = buffer.NativeSixelCellMetrics;
        Assert.Throws<InvalidOperationException>(() => parser.SixelCellSizeOverride = new(10, height));
        Assert.Equal(previous, buffer.NativeSixelCellMetrics);
    }

    [Fact]
    public void Regional_loss_retains_fractional_crop_through_reverse_scroll_grow_and_import()
    {
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 20, new NativeSixelBudget(), new SixelCellSize(10, 20));
        var parser = new AnsiParser(buffer, false) { SixelCellSizeOverride = new(10, 3.25f) };
        parser.Process("\x1b[?1049h\x1b[?80h\x1bPq#2!16~-!16~-!16N\x1b\\\x1b[2;10r\x1b[S");
        var image = Assert.Single(buffer.Images);
        Assert.Equal(6.5, image.NativeRaster!.Crop.Top);
        parser.Process("\x1b[S");
        Assert.Equal(9.75, image.NativeRaster.Crop.Top);
        parser.Process("\x1b[T");
        buffer.Resize(10, 40);
        Assert.Equal(9.75, image.NativeRaster.Crop.Top);
        var state = buffer.ExportState(0);
        var copy = TerminalBuffer.CreateNativeSixelTestBuffer(10, 40, new NativeSixelBudget(), new SixelCellSize(10, 20));
        copy.ImportState(state);
        Assert.Equal(image.NativeRaster, Assert.Single(copy.Images).NativeRaster);
    }

    [Fact]
    public void Width_loss_is_irreversible_after_growing_the_screen_again()
    {
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 20, new NativeSixelBudget(), new SixelCellSize(10, 20));
        var parser = new AnsiParser(buffer, false) { SixelCellSizeOverride = new(3.25f, 20) };
        parser.Process("\x1b[?1049h\x1b[?80h\x1bPq#2!16~-!16~-!16N\x1b\\");
        buffer.Resize(2, 20);
        var image = Assert.Single(buffer.Images);
        Assert.Equal(6.5, image.NativeRaster!.Crop.Right);
        buffer.Resize(10, 20);
        Assert.Equal(6.5, image.NativeRaster.Crop.Right);
    }

    [Fact]
    public void Source_only_retirement_returns_credit_after_the_last_snapshot_without_a_renderer()
    {
        var budget = new NativeSixelBudget(maxPlacements: 1);
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, budget, new SixelCellSize(10, 20));
        var parser = new AnsiParser(buffer, false);
        parser.Process("\x1b[?80h\x1bPq~\x1b\\");
        int snapshot = buffer.BeginSnapshotSession();
        parser.Process("\u001bc");
        Assert.Empty(buffer.Images);
        Assert.Equal((1, 24L), budget.Usage);
        buffer.EndSnapshotSession(snapshot);
        Assert.Equal((0, 0L), budget.Usage);
        Assert.Equal(0, buffer.SixelSources.Count);
        parser.Process("\x1bPq~\x1b\\");
        Assert.Single(buffer.Images);
        buffer.ClearImages();
        Assert.Equal((0, 0L), budget.Usage);
    }

    [Fact]
    public void A_later_staging_refusal_rolls_back_prior_source_owners_without_mutating_the_target()
    {
        var source = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, new NativeSixelBudget(), new SixelCellSize(10, 20));
        new AnsiParser(source, false).Process("\x1b[?80l\x1bPq~\x1b\\\x1bPq~\x1b\\");
        var state = source.ExportState(0);
        Assert.Equal(2, state.Sixels!.Count);
        state.Parser = null!; // Reach staged reservation/rollback rather than parser-envelope refusal.
        var budget = new NativeSixelBudget(maxPlacements: 1);
        var target = TerminalBuffer.CreateNativeSixelTestBuffer(20, 8, budget, new SixelCellSize(10, 20));
        Assert.Throws<InvalidOperationException>(() => TerminalStateTransfer.Restore(target, new AnsiParser(target, false), state));
        Assert.Equal((20, 8), (target.Cols, target.Rows));
        Assert.Empty(target.Images);
        Assert.Equal((0, 0L), budget.Usage);
        Assert.Equal(0, target.SixelSources.Count);
    }

    [Fact]
    public void Native_pixel_culling_and_explicit_text_omission_use_different_request_contracts()
    {
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, new NativeSixelBudget(), new SixelCellSize(10, 20));
        var parser = new AnsiParser(buffer, false);
        parser.Process("\x1b[?80h\x1bPq#2!16~-!16~-!16N\x1b\\\x1b[?80l\x1b[6;1H\n\n");
        using var visible = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest
        {
            ViewportRows = 6,
            ViewportCols = 10,
            Isolated = true,
            NativeSixelProjection = new(10, 4),
        }, out _);
        Assert.Equal(1, visible.Images.Length);
        using var absent = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest
        {
            ViewportRows = 6,
            ViewportCols = 10,
            Isolated = true,
            NativeSixelProjection = new(10, 20),
        }, out _);
        Assert.Equal(0, absent.Images.Length);
        using var text = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest
        {
            ViewportRows = 6,
            ViewportCols = 10,
            Isolated = true,
            OmitImages = true,
        }, out _);
        Assert.Equal(0, text.Images.Length);
        Assert.Throws<InvalidOperationException>(() => buffer.CaptureRenderSnapshot(new RenderSnapshotRequest
        {
            ViewportRows = 6,
            ViewportCols = 10,
            Isolated = true,
        }, out _));
    }

    [Fact]
    public void Fully_clipped_reservation_does_not_erase_native_overhang_or_prevent_transfer()
    {
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 20, new NativeSixelBudget(), new SixelCellSize(10, 20));
        var parser = new AnsiParser(buffer, false);
        parser.Process("\x1b[?1049h\x1b[?80h\x1bPq#2!16~-!16~-!16N\x1b\\");
        parser.SixelCellSizeOverride = new(10, 1);
        parser.Process("\x1b[2;20r\x1b[S\x1b[S");
        var image = Assert.Single(buffer.Images);
        Assert.Equal(1, image.CellHeight);
        Assert.Equal(1, image.ClipTopRows);
        Assert.Equal(3, image.NativeRaster!.Crop.Top);
        var state = buffer.ExportState(0);
        var target = TerminalBuffer.CreateNativeSixelTestBuffer(10, 20, new NativeSixelBudget(), new SixelCellSize(10, 20));
        target.ImportState(state);
        Assert.Equal(image.NativeRaster, Assert.Single(target.Images).NativeRaster);
        Assert.Equal(1, Assert.Single(state.Sixels!).ClipTop);
    }

    [Theory]
    [InlineData("policy")]
    [InlineData("metrics")]
    [InlineData("intent")]
    [InlineData("canvas")]
    [InlineData("x")]
    [InlineData("y")]
    [InlineData("clip")]
    [InlineData("source")]
    [InlineData("sourceLimit")]
    [InlineData("count")]
    public void Malformed_native_state_is_refused_before_any_target_mutation(string defect)
    {
        var state = TwoNativeSources();
        switch (defect)
        {
            case "policy": state.RequiredSixelRasterPolicy = 2; break;
            case "metrics": state.NativeSixelCellMetrics = null; break;
            case "intent": state.Sixels![0].NativeRaster = null; break;
            case "canvas": state.Sixels![0].NativeRaster = new(1, 2, 6, new(0, 0, 2, 6)); break;
            case "x": state.Sixels![0].X = int.MaxValue; break;
            case "y": state.Sixels![0].Y = int.MinValue; break;
            case "clip": state.Sixels![0].ClipTop = int.MaxValue; break;
            case "source": state.Sixels![0].Source = "bad header"; break;
            case "sourceLimit": state.Sixels![0].Source = new string('1', SixelSourceBudget.DefaultMaxChars + 1); break;
            case "count": state.Sixels = Enumerable.Repeat(state.Sixels![0], SixelSourceBudget.DefaultMaxSources + 1).ToList(); break;
        }
        var budget = new NativeSixelBudget();
        var target = TerminalBuffer.CreateNativeSixelTestBuffer(20, 8, budget, new SixelCellSize(10, 20));
        target.Write("sentinel");
        string before = System.Text.Json.JsonSerializer.Serialize(target.ExportState(100));
        Assert.Throws<InvalidOperationException>(() => TerminalStateTransfer.Restore(target, new AnsiParser(target, false), state));
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(target.ExportState(100)));
        Assert.Equal((0, 0L), budget.Usage);
        Assert.Equal(0, target.SixelSources.Count);
    }

    [Fact]
    public void Refused_import_disposal_remains_charged_and_snapshot_gated_until_actual_retry()
    {
        var budget = new NativeSixelBudget();
        var target = TerminalBuffer.CreateNativeSixelTestBuffer(20, 8, budget, new SixelCellSize(10, 20));
        var decoder = new StagingDecoder(target);
        var parser = new AnsiParser(target, false) { ImageDecoder = decoder };
        string before = System.Text.Json.JsonSerializer.Serialize(target.ExportState(100));
        int snapshot = target.BeginSnapshotSession();
        target.OnInvalidate += () => throw new InvalidOperationException("Notification must not replace the original refusal.");
        var refused = Assert.Throws<InvalidOperationException>(() => TerminalStateTransfer.Restore(target, parser, TwoNativeSources()));
        Assert.Contains("decoder refused", refused.Message, StringComparison.Ordinal);
        Assert.Equal(2, decoder.Calls);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(target.ExportState(100)));
        Assert.Equal(1, budget.Usage.Placements);
        var retired = new List<object>();
        target.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Empty(retired);
        target.EndSnapshotSession(snapshot);
        target.DrainRetiredImageHandles(retired, long.MaxValue);
        var owner = Assert.IsType<FailOnceOwner>(Assert.Single(retired));
        Assert.Equal(1, budget.Usage.Placements);
        owner.Dispose();
        Assert.Equal(2, owner.Attempts);
        Assert.Equal((0, 0L), budget.Usage);
    }

    [Fact]
    public void Native_retention_fit_is_checked_before_any_staged_reservation()
    {
        var budget = new NativeSixelBudget();
        var target = TerminalBuffer.CreateNativeSixelTestBuffer(20, 8, budget, new SixelCellSize(10, 20), new SixelSourceBudget(2, 1));
        Assert.Throws<InvalidOperationException>(() => target.ImportState(TwoNativeSources()));
        Assert.Equal((0, 0L), budget.Usage);
        Assert.Empty(target.Images);
        Assert.Equal(0, target.SixelSources.Count);
    }

    [Fact]
    public void Orphan_native_metrics_are_rejected_at_the_public_compatibility_gate()
    {
        var state = new TerminalBuffer(10, 6).ExportState(0);
        state.NativeSixelCellMetrics = new(10, 20);
        Assert.Throws<InvalidOperationException>(() => SixelRasterPolicy.RequireSupportedState(state));
    }


    [Theory]
    [InlineData("modes")]
    [InlineData("rows")]
    [InlineData("parser")]
    [InlineData("x")]
    [InlineData("crop")]
    [InlineData("metrics")]
    [InlineData("source")]
    public void A_decoder_callback_cannot_replace_the_prevalidated_envelope_or_staged_native_facts(string change)
    {
        var state = TwoNativeSources();
        state.Sixels!.RemoveAt(1);
        state.Parser = new AnsiParser(TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, new NativeSixelBudget(), new SixelCellSize(10, 20)), false).ExportState();
        var budget = new NativeSixelBudget();
        var target = TerminalBuffer.CreateNativeSixelTestBuffer(20, 8, budget, new SixelCellSize(10, 20));
        target.Write("sentinel");
        string before = System.Text.Json.JsonSerializer.Serialize(target.ExportState(100));
        var decoder = new CallbackDecoder(() =>
        {
            switch (change)
            {
                case "modes": state.Modes = null!; break;
                case "rows": state.Rows = 0; break;
                case "parser": state.Parser.State = int.MaxValue; break;
                case "x": state.Sixels[0].X = 1; break;
                case "crop": state.Sixels[0].NativeRaster = new(1, 1, 6, new(0, 1, 1, 6)); break;
                case "metrics": state.NativeSixelCellMetrics = new(11, 20); state.Parser.SixelCellWidthPx = 11; break;
                case "source": state.Sixels[0].Source = "q@"; break;
            }
        });
        var error = Record.Exception(() => TerminalStateTransfer.Restore(target, new AnsiParser(target, false) { ImageDecoder = decoder }, state));
        Assert.NotNull(error);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(target.ExportState(100)));
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(1, decoder.Calls);
        Assert.Equal((0, 0L), budget.Usage);
        Assert.Equal(0, target.SixelSources.Count);
    }


    [Fact]
    public void Valid_callback_cell_blob_is_revalidated_and_the_refreshed_bytes_are_committed()
    {
        var state = TwoNativeSources();
        state.Sixels!.RemoveAt(1);
        var replacement = new TerminalBuffer(10, 6);
        replacement.Write("refreshed");
        string? cells = replacement.ExportState(0).Main.CellsBase64;
        var target = TerminalBuffer.CreateNativeSixelTestBuffer(20, 8, new NativeSixelBudget(), new SixelCellSize(10, 20));
        var decoder = new CallbackDecoder(() => state.Main.CellsBase64 = cells);
        TerminalStateTransfer.Restore(target, new AnsiParser(target, false) { ImageDecoder = decoder }, state);
        target.Lock.EnterReadLock();
        try { Assert.Equal('r', target.GetCell(0, 0).Character); }
        finally { target.Lock.ExitReadLock(); }
        Assert.Single(target.Images);
        Assert.Equal(1, decoder.Calls);
        target.ClearImages();
    }

    private sealed class CallbackDecoder(Action callback) : IImageDecoder, INativeSixelDecoder
    {
        internal int Calls;
        public INativeSixelImage? DecodeNativeSixel(SixelSource source, NativeSixelAllocation allocation)
        {
            Calls++;
            callback();
            return new NativeSixelSourceOwner(source, allocation);
        }
        public object? DecodeSixel(string data, out int width, out int height) => throw new InvalidOperationException("Legacy fallback is forbidden.");
        public object? DecodeImageBytes(byte[] data, out int width, out int height) => throw new InvalidOperationException("Image fallback is forbidden.");
        public object? DecodeRawImage(byte[] data, int bpp, int width, int height, out int w, out int h) => throw new InvalidOperationException("Raw fallback is forbidden.");
    }

    private static TerminalStateSnapshot TwoNativeSources()
    {
        var source = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, new NativeSixelBudget(), new SixelCellSize(10, 20));
        new AnsiParser(source, false).Process("\x1b[?80l\x1bPq~\x1b\\\x1bPq~\x1b\\");
        var state = source.ExportState(0);
        state.Parser = null!;
        return state;
    }

    private sealed class StagingDecoder(TerminalBuffer buffer) : IImageDecoder, INativeSixelDecoder
    {
        internal int Calls;
        public INativeSixelImage? DecodeNativeSixel(SixelSource source, NativeSixelAllocation allocation) =>
            ++Calls == 1 ? new FailOnceOwner(buffer, source, allocation) : null;
        public object? DecodeSixel(string data, out int width, out int height) => throw new InvalidOperationException("Legacy fallback is forbidden.");
        public object? DecodeImageBytes(byte[] data, out int width, out int height) => throw new InvalidOperationException("Image fallback is forbidden.");
        public object? DecodeRawImage(byte[] data, int bpp, int width, int height, out int w, out int h) => throw new InvalidOperationException("Raw fallback is forbidden.");
    }

    private sealed class FailOnceOwner(TerminalBuffer buffer, SixelSource source, NativeSixelAllocation allocation) : INativeSixelImage
    {
        public SixelSource Source => source;
        public NativeSixelAllocation Allocation => allocation;
        public bool NeedsSource => true;
        public bool IsMaterialized => false;
        public NativeSixelDecodeState DecodeState => NativeSixelDecodeState.Pending;
        internal int Attempts;
        public void Dispose()
        {
            Assert.False(buffer.Lock.IsReadLockHeld);
            Assert.False(buffer.Lock.IsWriteLockHeld);
            if (++Attempts == 1) throw new InvalidOperationException("Controlled disposal failure.");
            source.Dispose();
            allocation.Owner.CompleteDisposal(allocation);
        }
    }

    [Fact]
    public void Full_export_does_not_narrow_native_crop_after_a_metric_only_change()
    {
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, new NativeSixelBudget(), new SixelCellSize(10, 20));
        var parser = new AnsiParser(buffer, false);
        parser.Process("\x1b[?80h\x1bPq#2!16~-!16~-!16N\x1b\\");
        var original = Assert.Single(buffer.Images).NativeRaster;
        parser.SixelCellSizeOverride = new(1, 1);
        var state = buffer.ExportState(100);
        Assert.Equal(original, Assert.Single(state.Sixels!).NativeRaster);
        Assert.Equal(original, Assert.Single(buffer.Images).NativeRaster);
    }

    [Fact]
    public void Native_prepared_import_is_owned_by_one_target_and_cannot_be_reused_or_revived()
    {
        var state = TwoNativeSources();
        var budget = new NativeSixelBudget();
        var owner = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, budget, new SixelCellSize(10, 20));
        var foreign = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, new NativeSixelBudget(), new SixelCellSize(10, 20));
        using (var prepared = owner.PrepareStateImport(state, null))
        {
            Assert.Throws<InvalidOperationException>(() => foreign.ImportPreparedState(state, prepared));
            Assert.Empty(foreign.Images);
            owner.ImportPreparedState(state, prepared);
            var image = owner.Images[0];
            var usage = budget.Usage;
            Assert.Throws<InvalidOperationException>(() => owner.ImportPreparedState(state, prepared));
            Assert.Same(image, owner.Images[0]);
            Assert.Equal(usage, budget.Usage);
        }
        owner.ClearImages();
        Assert.Equal((0, 0L), budget.Usage);
        var disposed = owner.PrepareStateImport(state, null);
        disposed.Dispose();
        Assert.Throws<InvalidOperationException>(() => owner.ImportPreparedState(state, disposed));
        Assert.Empty(owner.Images);
        Assert.Equal((0, 0L), budget.Usage);
    }

    [Fact]
    public void Direct_native_import_refuses_wrong_target_geometry_but_whole_restore_resizes_coherently()
    {
        var state = TwoNativeSources();
        var budget = new NativeSixelBudget();
        var target = TerminalBuffer.CreateNativeSixelTestBuffer(20, 8, budget, new SixelCellSize(10, 20));
        Assert.Throws<InvalidOperationException>(() => target.ImportState(state));
        Assert.Equal((20, 8), (target.Cols, target.Rows));
        Assert.Empty(target.Images);
        Assert.Equal((0, 0L), budget.Usage);
        TerminalStateTransfer.Restore(target, new AnsiParser(target, false), state);
        Assert.Equal((10, 6), (target.Cols, target.Rows));
        Assert.Equal(2, target.Images.Count);
        target.ClearImages();
        Assert.Equal((0, 0L), budget.Usage);
    }

    [Fact]
    public void Empty_native_crop_retires_once_and_cannot_resurrect_after_grow_or_reverse_scroll()
    {
        var budget = new NativeSixelBudget(maxPlacements: 1);
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 40, budget, new SixelCellSize(10, 20));
        var parser = new AnsiParser(buffer, false) { SixelCellSizeOverride = new(10, 1) };
        parser.Process("\x1b[?1049h\x1b[?80h\x1bPq#2!16~-!16~-!16N\x1b\\\x1b[2;20r");
        var image = Assert.Single(buffer.Images);
        int snapshot = buffer.BeginSnapshotSession();
        for (int i = 0; i < 17; i++) parser.Process("\x1b[S");
        Assert.Empty(buffer.Images);
        Assert.Equal(image.NativeRaster!.Crop.Top, image.NativeRaster.Crop.Bottom);
        Assert.Equal(1, budget.Usage.Placements);
        parser.Process("\x1b[T");
        buffer.Resize(10, 80);
        Assert.Empty(buffer.Images);
        buffer.EndSnapshotSession(snapshot);
        Assert.Equal((0, 0L), budget.Usage);
        parser.Process("\x1bPq~\x1b\\");
        Assert.Single(buffer.Images);
        buffer.ClearImages();
        Assert.Equal((0, 0L), budget.Usage);
    }

    [Fact]
    public void History_loss_and_ED3_crop_native_overhang_without_losing_its_transferable_remainder()
    {
        var budget = new NativeSixelBudget();
        var buffer = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, budget, new SixelCellSize(10, 20));
        buffer.MaxHistory = 2;
        var parser = new AnsiParser(buffer, false);
        parser.Process("\x1b[?80h\x1bPq#2!16~-!16~-!16N\x1b\\");
        parser.SixelCellSizeOverride = new(10, 1);
        parser.Process("\x1b[?80l\x1b[6;1H\n\n\n");
        var image = Assert.Single(buffer.Images);
        int evicted = 3 - (buffer.TotalLines - buffer.Rows);
        Assert.InRange(evicted, 1, 3); // Page retention may round the tiny two-row budget down.
        Assert.Equal(-evicted, image.CellY);
        Assert.Equal((double)evicted, image.NativeRaster!.Crop.Top);
        buffer.MaxHistory = 10000;
        parser.Process("\n\n");
        int retained = buffer.TotalLines - buffer.Rows;
        Assert.True(retained >= 2);
        parser.Process("\x1b[3J");
        Assert.Equal(-evicted - retained, image.CellY);
        Assert.Equal((double)(evicted + retained), image.NativeRaster.Crop.Top);
        var state = buffer.ExportState(100);
        var target = TerminalBuffer.CreateNativeSixelTestBuffer(10, 6, new NativeSixelBudget(), new SixelCellSize(10, 20));
        target.ImportState(state);
        Assert.Equal(image.NativeRaster, Assert.Single(target.Images).NativeRaster);
        parser.SixelCellSizeOverride = new(10, 20);
        Assert.Equal(image.NativeRaster, Assert.Single(buffer.ExportState(100).Sixels!).NativeRaster);
        parser.SixelCellSizeOverride = new(10, 1);
        parser.Process("\x1b[2J");
        Assert.Empty(buffer.Images);
        Assert.Equal((0, 0L), budget.Usage);
        target.ClearImages();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.Epsilon)]
    public void Native_projection_rejects_unrepresentable_source_extents(double denominator)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeSixelViewProjection(denominator, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeSixelViewProjection(10, denominator));
    }
}
