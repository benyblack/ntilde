using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Ntilde.AgentHost;
using Ntilde.AgentHost.Contracts;
using Ntilde.Shell;
using Ntilde.Tests.Infra;
using Ntilde.VT;
using SkiaSharp;

namespace Ntilde.AppTests.AgentHost;

/// <summary>
/// Tests for the A5 <c>captureScreen</c> protocol surface. Captures ride the
/// observe toggle alone, like every other read, so what is left to gate is
/// physical: the pane must have published render parameters, and the image must
/// fit the per-capture pixel budget at the requested scale. Renders go through the
/// production <see cref="TerminalSnapshotRenderer"/> — the same path the golden
/// PNG baselines pin down — with no window or visual tree involved, which is why
/// these run headless.
///
/// In the GoldenPng collection (#317) because that is where every other test that boots the
/// Avalonia headless platform lives, and the platform can only be booted once, by one thread.
/// Left as [Fact] deliberately: these tests do named-pipe I/O, and moving them onto the
/// framework's dispatcher thread with [AvaloniaFact] stalls any sweep that also runs the pane
/// tests - an intermittent failure traded for a worse hang. Serialising them against the other
/// Avalonia users is what removes the race; running them on that thread is not needed, since
/// they want the font manager rather than a visual tree.
/// </summary>
[Collection("GoldenPng")]
[Trait("Lane", "PlatformBoot")]
public class AgentHostCaptureProtocolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _exportDir;

    // A plausible 8x16 cell grid. Values only have to be self-consistent: the
    // renderer treats them as the geometry the live control measured.
    private static readonly CellMetrics Metrics = new()
    {
        CellWidth = 8f,
        CellHeight = 16f,
        Baseline = 12f,
        Ascent = 12f,
        Descent = 4f,
        Leading = 0f,
    };

    public AgentHostCaptureProtocolTests()
    {
        _tempDir = AgentHostTestEndpoint.CreateTempDir("capture");
        _exportDir = Path.Combine(_tempDir, "agent-exports");
        Directory.CreateDirectory(_tempDir);

        // The renderer resolves fonts through Avalonia's font manager.
        SnapshotService.EnsureAvaloniaInitialized();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private AgentHostService NewService(AgentSessionRegistry registry, AgentActivityJournal? journal = null)
    {
        var endpoint = AgentHostTestEndpoint.CreateEndpoint(_tempDir);
        return new AgentHostService(registry, endpoint, _tempDir, _exportDir, journal);
    }

    /// <summary>Registers a pane that has been measured, so it is capturable.</summary>
    private static AgentSessionRegistration Register(
        AgentSessionRegistry registry,
        string? content = null,
        CellMetrics? metrics = null,
        bool publishRenderParameters = true,
        string? fontFamily = null)
    {
        var buffer = new TerminalBuffer(80, 24);
        if (content != null)
        {
            new AnsiParser(buffer).Process(content);
        }

        var registration = new AgentSessionRegistration(
            Guid.NewGuid(), buffer, "title", "Profile", "local", isActive: true);
        if (publishRenderParameters)
        {
            registration.UpdateRenderParameters(new PaneRenderParameters(
                metrics ?? Metrics,
                fontFamily ?? TerminalSnapshotOptions.DefaultTypefaceFamily,
                FontSize: 14f,
                EnableLigatures: false,
                EnableComplexShaping: true));
        }

        Assert.True(registry.Register(registration));
        return registration;
    }

    private static string CaptureRequestLine(
        Guid paneId,
        long id = 1,
        bool inline = false,
        int maxWidth = 0,
        double scale = 0,
        string? mode = null,
        long? time = null,
        bool details = false)
    {
        var paramsJson = JsonSerializer.Serialize(
            new CaptureScreenParams
            {
                PaneId = paneId,
                Inline = inline,
                MaxWidth = maxWidth,
                Scale = scale,
                Mode = mode,
                PresentationTimeMs = time,
                IncludeRenderDetails = details
            },
            AgentHostJsonContext.Default.CaptureScreenParams);
        return $"{{\"v\":{AgentHostProtocol.Version},\"id\":{id},\"method\":\"{AgentHostProtocol.Methods.CaptureScreen}\",\"params\":{paramsJson}}}";
    }

    private static AgentHostResponse Handle(AgentHostService service, string line)
        => service.HandleRequestLineAsync(line, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    private static CaptureScreenResult Result(AgentHostResponse response)
    {
        Assert.Null(response.Error);
        Assert.NotNull(response.Result);
        var result = response.Result!.Value.Deserialize(AgentHostJsonContext.Default.CaptureScreenResult);
        Assert.NotNull(result);
        return result!;
    }

    [Theory]
    [InlineData(-1L, "render", false)]
    [InlineData(60001L, "render", false)]
    [InlineData(0L, "live", false)]
    [InlineData(null, "live", true)]
    public void Invalid_controlled_options_do_not_mark_attention_or_write_a_capture(long? time, string mode, bool details)
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, "unchanged");
        var journal = new AgentActivityJournal();
        using var service = NewService(registry, journal);
        Assert.Equal(AgentAttentionTier.Idle, registration.AttentionMachine.Snapshot().Tier);
        var response = Handle(service, CaptureRequestLine(registration.PaneId, mode: mode, time: time, details: details));
        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, response.Error?.Code);
        Assert.Equal(AgentAttentionTier.Idle, registration.AttentionMachine.Snapshot().Tier);
        Assert.Empty(journal.Snapshot());
        Assert.Empty(Directory.Exists(_exportDir) ? Directory.GetFiles(_exportDir) : Array.Empty<string>());
    }

    [Fact]
    public void Default_capture_omits_the_new_clock_and_provenance_fields()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, "default");
        using var service = NewService(registry);
        var line = CaptureRequestLine(registration.PaneId);
        Assert.DoesNotContain("presentationTimeMs", line, StringComparison.Ordinal);
        Assert.DoesNotContain("includeRenderDetails", line, StringComparison.Ordinal);
        Assert.DoesNotContain("syntheticSelection", line, StringComparison.Ordinal);
        var response = Handle(service, line);
        var result = Result(response);
        Assert.Null(result.PresentationTimeMs);
        Assert.Null(result.RenderDetails);
        Assert.False(response.Result!.Value.TryGetProperty("presentationTimeMs", out _));
        Assert.False(response.Result.Value.TryGetProperty("renderDetails", out _));
        Assert.False(response.Result.Value.TryGetProperty("syntheticSelection", out _));
    }

    [Fact]
    public void Controlled_capture_changes_pixels_without_changing_the_terminal_and_returns_the_actual_render_facts()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, "\x1b[?25l\x1b[5mMMMM", fontFamily: BundledFontCatalog.DefaultTerminalFontFamily);
        registration.Buffer.Theme = new TerminalTheme { Foreground = TermColor.White, Background = TermColor.Black };
        using var service = NewService(registry);
        var first = Result(Handle(service, CaptureRequestLine(registration.PaneId, scale: 2, time: 0, details: true)));
        var hidden = Result(Handle(service, CaptureRequestLine(registration.PaneId, scale: 2, time: 530, details: true)));
        var again = Result(Handle(service, CaptureRequestLine(registration.PaneId, scale: 2, time: 1060, details: true)));
        var defaultPhase = Result(Handle(service, CaptureRequestLine(registration.PaneId, scale: 2)));
        Assert.NotEqual(File.ReadAllBytes(first.FilePath), File.ReadAllBytes(hidden.FilePath));
        Assert.Equal(File.ReadAllBytes(first.FilePath), File.ReadAllBytes(again.FilePath));
        Assert.Equal(File.ReadAllBytes(first.FilePath), File.ReadAllBytes(defaultPhase.FilePath));
        Assert.Equal(530, hidden.PresentationTimeMs);
        var context = Assert.IsType<CaptureRenderDetails>(hidden.RenderDetails);
        Assert.Equal(1, context.SchemaVersion);
        Assert.Contains(context.Platform, new[] { "windows", "macos", "linux" });
        Assert.Matches("^[a-f0-9]{64}$", context.Font.PrimarySha256!);
        Assert.Equal(first.RenderDetails!.Font.PrimarySha256, context.Font.PrimarySha256);
        Assert.Equal(BundledFontCatalog.DefaultTerminalFontFamily, context.Font.ConfiguredFamily);
        Assert.False(string.IsNullOrWhiteSpace(context.Font.PrimaryFamily));
        Assert.Equal(14, context.Font.SizeDip);
        Assert.Equal(8, context.Grid.CellWidthDip);
        Assert.Equal(16, context.Grid.CellWidthPx);
        Assert.Equal(8, context.Grid.OriginXPx);
        Assert.Equal(hidden.Width, context.Grid.RenderWidthPx);
        Assert.Equal(hidden.Height, context.Grid.RenderHeightPx);
        Assert.Equal(0xff000000U, context.Theme.BackgroundArgb);
        Assert.Equal(0xffffffffU, context.Theme.ForegroundArgb);
        Assert.Equal(256, context.Theme.AnsiPaletteArgb.Length);
        Assert.Equal(530, context.Presentation.TimeMs);
        Assert.Equal(530, context.Presentation.HalfPeriodMs);
        Assert.False(context.Presentation.TextBlinkVisible);
        Assert.False(context.Presentation.CursorVisible);
        Assert.Equal(0, context.Presentation.CursorRow);
        Assert.Equal(4, context.Presentation.CursorCol);
        registration.Buffer.Lock.EnterReadLock();
        try { Assert.True(registration.Buffer.GetCell(0, 0).IsBlink); }
        finally { registration.Buffer.Lock.ExitReadLock(); }
        Assert.False(registration.Buffer.Modes.IsCursorVisible);
        Assert.Equal(4, registration.Buffer.CursorCol);
    }

    [Theory]
    [InlineData(100, 30, true, false)]
    [InlineData(40, 12, true, false)]
    [InlineData(100, 30, false, false)]
    [InlineData(40, 12, false, false)]
    [InlineData(100, 30, true, true)]
    [InlineData(40, 12, true, true)]
    [InlineData(100, 30, false, true)]
    [InlineData(40, 12, false, true)]
    public void Capture_refuses_a_grid_changed_during_render_preparation_and_succeeds_on_retry(int cols, int rows, bool details, bool beforeDraw)
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, "resize fixture");
        Action resize = () => registration.Buffer.Resize(cols, rows);
        var options = new AgentCaptureOptions(null, details)
        {
            BeforeRenderForTest = beforeDraw ? null : resize,
            BeforeDrawForTest = beforeDraw ? resize : null,
        };
        Assert.False(registration.TryCapturePng(0, 1, out var refused, out var error, options));
        Assert.Equal(AgentCaptureError.Unavailable, error);
        Assert.Null(refused.Png);
        Assert.True(registration.TryCapturePng(0, 1, out var retry, out _, new AgentCaptureOptions(null, details)));
        Assert.Equal(cols, retry.Cols);
        Assert.Equal(rows, retry.Rows);
        Assert.Equal(4 + cols * 8, retry.Width);
        Assert.Equal(rows * 16, retry.Height);
        if (details)
        {
            Assert.Equal(retry.Width, retry.RenderDetails!.Grid.RenderWidthPx);
            Assert.Equal(retry.Height, retry.RenderDetails.Grid.RenderHeightPx);
        }
        else Assert.Null(retry.RenderDetails);
    }

    [Fact]
    public void Render_context_coordinates_describe_the_draw_before_downscaling()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, "resampled");
        using var service = NewService(registry);
        var result = Result(Handle(service, CaptureRequestLine(registration.PaneId, maxWidth: 160, scale: 2, details: true)));
        Assert.True(result.Downscaled);
        Assert.Equal(160, result.Width);
        Assert.Equal(1288, result.RenderDetails!.Grid.RenderWidthPx);
        Assert.Equal(768, result.RenderDetails.Grid.RenderHeightPx);
        Assert.Equal(16, result.RenderDetails.Grid.CellWidthPx);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Fractional_metrics_capture_contains_the_complete_snapped_grid(double scale)
    {
        var registry = new AgentSessionRegistry();
        var metrics = new CellMetrics { CellWidth = 8.5f, CellHeight = 18.5f, Baseline = 14.5f, Ascent = 14.5f, Descent = 4f };
        var registration = Register(registry, metrics: metrics);
        registration.Buffer.Resize(140, 41);
        new AnsiParser(registration.Buffer).Process("\x1b[?25l\x1b[?7l\x1b[41;140H\x1b[48;2;23;91;173m ");
        using var service = NewService(registry);
        var result = Result(Handle(service, CaptureRequestLine(registration.PaneId, scale: scale, details: true)));
        var grid = result.RenderDetails!.Grid;
        Assert.True(grid.OriginXPx + result.Cols * grid.CellWidthPx <= result.Width);
        Assert.True(grid.OriginYPx + result.Rows * grid.CellHeightPx <= result.Height);
        using var image = SKBitmap.Decode(File.ReadAllBytes(result.FilePath));
        Assert.Equal(new SKColor(23, 91, 173), image.GetPixel(
            grid.OriginXPx + result.Cols * grid.CellWidthPx - 2,
            grid.OriginYPx + result.Rows * grid.CellHeightPx - 2));
    }

    [Fact]
    public void Pixel_budget_includes_snapped_cells_and_the_grid_origin()
    {
        var registry = new AgentSessionRegistry();
        var metrics = new CellMetrics { CellWidth = 99.5f, CellHeight = 79.5f, Baseline = 60f, Ascent = 60f, Descent = 19.5f };
        var registration = Register(registry, metrics: metrics);
        registration.Buffer.Resize(80, 25);
        using var service = NewService(registry);
        var response = Handle(service, CaptureRequestLine(registration.PaneId));
        Assert.Equal(AgentHostProtocol.ErrorCodes.CaptureUnavailable, response.Error?.Code);
        Assert.False(Directory.Exists(_exportDir) && Directory.EnumerateFiles(_exportDir).Any());
    }

    private static string WithSyntheticSelection(string request, string value) => request.Replace(
        "\"params\":{", "\"params\":{\"syntheticSelection\":" + value + ",", StringComparison.Ordinal);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Synthetic_selection_changes_only_the_offscreen_image_and_is_echoed(bool details)
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, "\x1b[?25lHMWqg");
        registration.Buffer.Theme = new TerminalTheme
        {
            Foreground = TermColor.White,
            Background = TermColor.Black,
            SelectionBackground = TermColor.FromRgb(23, 91, 173),
        };
        using var service = NewService(registry);
        string before = JsonSerializer.Serialize(registration.Buffer.ExportState(100));
        var normal = Result(Handle(service, CaptureRequestLine(registration.PaneId, details: details)));
        var response = Handle(service, WithSyntheticSelection(CaptureRequestLine(registration.PaneId, details: details),
            "{\"startRow\":0,\"startCol\":0,\"endRow\":0,\"endCol\":4}"));
        var selected = Result(response);
        Assert.NotEqual(File.ReadAllBytes(normal.FilePath), File.ReadAllBytes(selected.FilePath));
        Assert.Equal(4, response.Result!.Value.GetProperty("syntheticSelection").GetProperty("endCol").GetInt32());
        Assert.Equal(before, JsonSerializer.Serialize(registration.Buffer.ExportState(100)));
        var again = Result(Handle(service, CaptureRequestLine(registration.PaneId)));
        Assert.Equal(File.ReadAllBytes(normal.FilePath), File.ReadAllBytes(again.FilePath));
    }

    [Theory]
    [InlineData("render", "{\"startRow\":-1,\"startCol\":0,\"endRow\":0,\"endCol\":4}")]
    [InlineData("render", "{\"startRow\":0,\"startCol\":0,\"endRow\":24,\"endCol\":4}")]
    [InlineData("render", "{\"startRow\":0,\"startCol\":0,\"endRow\":0,\"endCol\":80}")]
    [InlineData("render", "{\"startRow\":1,\"startCol\":0,\"endRow\":0,\"endCol\":4}")]
    [InlineData("render", "{\"startRow\":0,\"startCol\":4,\"endRow\":0,\"endCol\":0}")]
    [InlineData("render", "{\"startRow\":0,\"startCol\":0,\"endRow\":0}")]
    [InlineData("live", "{\"startRow\":0,\"startCol\":0,\"endRow\":0,\"endCol\":4}")]
    public void Invalid_synthetic_selection_does_not_mark_attention_or_export(string mode, string selection)
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, "unchanged");
        var journal = new AgentActivityJournal();
        using var service = NewService(registry, journal);
        var response = Handle(service, WithSyntheticSelection(CaptureRequestLine(registration.PaneId, mode: mode), selection));
        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, response.Error?.Code);
        Assert.Equal(AgentAttentionTier.Idle, registration.AttentionMachine.Snapshot().Tier);
        Assert.Empty(journal.Snapshot());
        Assert.Empty(Directory.Exists(_exportDir) ? Directory.GetFiles(_exportDir) : Array.Empty<string>());
    }

    [Fact]
    public void Render_metadata_distinguishes_color_overrides_and_nullable_selection_defaults()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, "colors");
        registration.Buffer.Theme = new TerminalTheme { Foreground = TermColor.White, Background = TermColor.Black };
        using var service = NewService(registry);
        var baseline = Handle(service, CaptureRequestLine(registration.PaneId, details: true));
        var initial = baseline.Result!.Value.GetProperty("renderDetails").GetProperty("theme");
        Assert.True(initial.TryGetProperty("cursorOverride", out var cursorOverride));
        Assert.False(cursorOverride.GetBoolean());
        Assert.Equal(JsonValueKind.Null, initial.GetProperty("selectionBackgroundArgb").ValueKind);
        Assert.Equal(JsonValueKind.Null, initial.GetProperty("selectionForegroundArgb").ValueKind);
        Assert.Equal(0, initial.GetProperty("paletteOverrideIndices").GetArrayLength());
        new AnsiParser(registration.Buffer).Process("\x1b]12;#175bad\x1b\\\x1b]17;#205030\x1b\\\x1b]19;#f0e0d0\x1b\\\x1b]4;200;#112233\x1b\\");
        var changed = Handle(service, CaptureRequestLine(registration.PaneId, details: true));
        var theme = changed.Result!.Value.GetProperty("renderDetails").GetProperty("theme");
        Assert.True(theme.GetProperty("cursorOverride").GetBoolean());
        Assert.True(theme.GetProperty("selectionBackgroundOverride").GetBoolean());
        Assert.True(theme.GetProperty("selectionForegroundOverride").GetBoolean());
        Assert.Equal(0xff205030U, theme.GetProperty("selectionBackgroundArgb").GetUInt32());
        Assert.Equal(0xfff0e0d0U, theme.GetProperty("selectionForegroundArgb").GetUInt32());
        Assert.Equal(0xff205030U, theme.GetProperty("effectiveSelectionBackgroundArgb").GetUInt32());
        Assert.Equal(0xfff0e0d0U, theme.GetProperty("effectiveSelectionForegroundArgb").GetUInt32());
        Assert.Equal(200, theme.GetProperty("paletteOverrideIndices")[0].GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Synthetic_selection_uses_the_actual_viewport_after_history_and_intervening_scroll(bool appendDuringDraw)
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, "\x1b[?25l" + string.Join("\r\n", Enumerable.Range(0, 40).Select(i => "line" + i)));
        registration.Buffer.Theme = new TerminalTheme
        {
            Foreground = TermColor.White,
            Background = TermColor.Black,
            SelectionBackground = TermColor.FromRgb(23, 91, 173),
        };
        var range = new CaptureSelection { StartRow = 1, StartCol = 15, EndRow = 1, EndCol = 19 };
        var options = new AgentCaptureOptions(null, true, range)
        {
            BeforeDrawForTest = appendDuringDraw ? () => new AnsiParser(registration.Buffer).Process("\r\nnew\r\nnew") : null,
        };
        Assert.True(registration.TryCapturePng(0, 1, out var capture, out _, options));
        var details = Assert.IsType<CaptureRenderDetails>(capture.RenderDetails);
        Assert.Equal(range, details.SyntheticSelection);
        using var bitmap = SKBitmap.Decode(capture.Png);
        var grid = details.Grid;
        Assert.Equal(new SKColor(23, 91, 173), bitmap.GetPixel(grid.OriginXPx + 17 * grid.CellWidthPx + 4, grid.CellHeightPx + 8));
        Assert.Equal(SKColors.Black, bitmap.GetPixel(grid.OriginXPx + 20 * grid.CellWidthPx + 4, grid.CellHeightPx + 8));
    }

    [Fact]
    public void Color_metadata_resets_return_exactly_to_nullable_host_defaults()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, "colors");
        registration.Buffer.Theme = new TerminalTheme { Foreground = TermColor.White, Background = TermColor.Black };
        using var service = NewService(registry);
        var initial = Result(Handle(service, CaptureRequestLine(registration.PaneId, details: true))).RenderDetails!.Theme;
        Assert.Null(initial.SelectionBackgroundArgb);
        Assert.Null(initial.SelectionForegroundArgb);
        Assert.NotEqual(0U, initial.EffectiveSelectionBackgroundArgb);
        var parser = new AnsiParser(registration.Buffer);
        parser.Process("\x1b]10;#102030;#405060;#708090\x1b\\\x1b]17;#a0b0c0\x1b\\\x1b]19;#d0e0f0\x1b\\\x1b]4;1;#123456;200;#abcdef\x1b\\");
        var changed = Result(Handle(service, CaptureRequestLine(registration.PaneId, details: true))).RenderDetails!.Theme;
        Assert.True(changed.ForegroundOverride);
        Assert.True(changed.BackgroundOverride);
        Assert.Equal(new[] { 1, 200 }, changed.PaletteOverrideIndices);
        parser.Process("\x1b]104\x1b\\\x1b]110\x1b\\\x1b]111\x1b\\\x1b]112\x1b\\\x1b]117\x1b\\\x1b]119\x1b\\");
        var restored = Result(Handle(service, CaptureRequestLine(registration.PaneId, details: true))).RenderDetails!.Theme;
        Assert.Equal(JsonSerializer.Serialize(initial), JsonSerializer.Serialize(restored));
        Assert.True(changed.SelectionBackgroundOverride);
        Assert.Equal(0xffa0b0c0U, changed.SelectionBackgroundArgb);
    }

    // PNG signature: every capture must be a real PNG, not an empty or truncated file.
    private static readonly byte[] PngMagic = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void Capture_writes_a_png_sized_to_the_grid()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, "hello from the session\r\n");
        using var service = NewService(registry);

        var result = Result(Handle(service, CaptureRequestLine(registration.PaneId)));

        Assert.Equal(80, result.Cols);
        Assert.Equal(24, result.Rows);
        Assert.Equal(4 + 80 * 8, result.Width);
        Assert.Equal(24 * 16, result.Height);
        Assert.False(result.Downscaled);
        Assert.False(result.InlineOmitted);
        Assert.Null(result.PngBase64); // inline not requested

        Assert.True(File.Exists(result.FilePath));
        Assert.StartsWith(Path.GetFullPath(_exportDir), Path.GetFullPath(result.FilePath), StringComparison.Ordinal);
        Assert.StartsWith("ntilde_screen_", Path.GetFileName(result.FilePath), StringComparison.Ordinal);
        Assert.EndsWith(".png", result.FilePath, StringComparison.Ordinal);

        var bytes = File.ReadAllBytes(result.FilePath);
        Assert.Equal(result.ByteCount, bytes.Length);
        Assert.Equal(PngMagic, bytes.Take(PngMagic.Length));
    }

    [Fact]
    public void Two_captures_of_an_unchanged_buffer_are_byte_identical()
    {
        // The determinism bar for a screenshot: same buffer, same bytes. It holds
        // because the render is fixed at 1:1 (never the monitor's scaling), the
        // cursor follows the buffer's mode rather than a blink phase, and no
        // selection or HUD state leaks in.
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, "deterministic [31mred[0m output\r\n");
        using var service = NewService(registry);

        var first = Result(Handle(service, CaptureRequestLine(registration.PaneId, id: 1)));
        var second = Result(Handle(service, CaptureRequestLine(registration.PaneId, id: 2)));

        Assert.NotEqual(first.FilePath, second.FilePath); // no silent overwrite
        Assert.Equal(File.ReadAllBytes(first.FilePath), File.ReadAllBytes(second.FilePath));
    }

    [Fact]
    public void Inline_capture_returns_the_png_as_base64()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, "inline me");
        using var service = NewService(registry);

        var result = Result(Handle(service, CaptureRequestLine(registration.PaneId, inline: true)));

        Assert.NotNull(result.PngBase64);
        Assert.False(result.InlineOmitted);
        var decoded = Convert.FromBase64String(result.PngBase64!);
        Assert.Equal(File.ReadAllBytes(result.FilePath), decoded);
    }

    [Fact]
    public void MaxWidth_downscales_the_image_and_says_so()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, "shrink me");
        using var service = NewService(registry);

        var result = Result(Handle(service, CaptureRequestLine(registration.PaneId, maxWidth: 160)));

        Assert.True(result.Downscaled);
        Assert.Equal(160, result.Width);
        // 644x384 scaled to width 160 keeps the aspect ratio.
        Assert.Equal(96, result.Height);
        Assert.Equal(80, result.Cols); // the grid is unchanged; only the image shrank
    }

    [Fact]
    public void MaxWidth_larger_than_the_image_is_a_no_op()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry);
        using var service = NewService(registry);

        var result = Result(Handle(service, CaptureRequestLine(registration.PaneId, maxWidth: 10_000)));

        Assert.False(result.Downscaled);
        Assert.Equal(4 + 80 * 8, result.Width);
    }

    [Fact]
    public void Capture_for_unknown_pane_reports_session_not_found()
    {
        using var service = NewService(new AgentSessionRegistry());

        var response = Handle(service, CaptureRequestLine(Guid.NewGuid()));

        Assert.Equal(AgentHostProtocol.ErrorCodes.SessionNotFound, response.Error?.Code);
    }

    [Fact]
    public void Capture_without_params_is_a_malformed_request()
    {
        using var service = NewService(new AgentSessionRegistry());

        var line = $"{{\"v\":{AgentHostProtocol.Version},\"id\":7,\"method\":\"{AgentHostProtocol.Methods.CaptureScreen}\",\"params\":null}}";
        var response = Handle(service, line);

        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, response.Error?.Code);
    }

    [Fact]
    public void Capture_with_an_unparseable_paneId_is_malformed()
    {
        // `required Guid PaneId` throws JsonException rather than yielding null,
        // which must be caught here rather than escaping to the outer handler.
        using var service = NewService(new AgentSessionRegistry());

        var line = $"{{\"v\":{AgentHostProtocol.Version},\"id\":8,\"method\":\"{AgentHostProtocol.Methods.CaptureScreen}\",\"params\":{{\"paneId\":\"not-a-guid\"}}}}";
        var response = Handle(service, line);

        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, response.Error?.Code);
    }

    [Fact]
    public void Capture_is_not_journaled_because_it_is_an_observe_tier_read()
    {
        // The journal is the acting surface's record. A capture rides the observe
        // toggle like readScreen, and its visibility surface is the pane's own
        // agent-access indicator (#339), which TryNoteRead marks on every capture.
        var journal = new AgentActivityJournal();
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, "not journaled");
        using var service = NewService(registry, journal);

        var result = Result(Handle(service, CaptureRequestLine(registration.PaneId)));

        Assert.True(File.Exists(result.FilePath));
        Assert.Empty(journal.Snapshot());
    }

    [Fact]
    public void Capture_before_the_pane_is_measured_reports_captureUnavailable()
    {
        // Registration happens in the pane constructor, before layout has measured
        // the font: there is no geometry to render into yet.
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, publishRenderParameters: false);
        using var service = NewService(registry);

        var response = Handle(service, CaptureRequestLine(registration.PaneId));

        Assert.Equal(AgentHostProtocol.ErrorCodes.CaptureUnavailable, response.Error?.Code);
    }

    [Fact]
    public void Capture_of_a_grid_over_the_pixel_budget_reports_captureUnavailable()
    {
        var registry = new AgentSessionRegistry();
        var huge = new CellMetrics { CellWidth = 5000f, CellHeight = 5000f, Baseline = 4000f, Ascent = 4000f, Descent = 1000f, Leading = 0f };
        var registration = Register(registry, metrics: huge);
        using var service = NewService(registry);

        var response = Handle(service, CaptureRequestLine(registration.PaneId));

        Assert.Equal(AgentHostProtocol.ErrorCodes.CaptureUnavailable, response.Error?.Code);
        Assert.Contains("pixel per-capture budget", response.Error!.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_exportDir) && Directory.EnumerateFiles(_exportDir).Any());
    }

    [Fact]
    public void Scale_renders_more_device_pixels_for_the_same_grid()
    {
        // What #346 unlocked: RenderScaling used to be passed to the draw operation
        // with an unscaled canvas, so any value but 1.0 produced a wrong image. The
        // renderer now scales the canvas and sizes the bitmap in device pixels.
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, "scale me");
        using var service = NewService(registry);

        var oneX = Result(Handle(service, CaptureRequestLine(registration.PaneId, id: 1)));
        var twoX = Result(Handle(service, CaptureRequestLine(registration.PaneId, id: 2, scale: 2)));

        Assert.Equal(1.0, oneX.Scale);
        Assert.Equal(4 + 80 * 8, oneX.Width);
        Assert.Equal(2.0, twoX.Scale);
        Assert.Equal((4 + 80 * 8) * 2, twoX.Width);
        Assert.Equal(24 * 16 * 2, twoX.Height);

        // The grid is unchanged - only the resolution it was drawn at.
        Assert.Equal(80, twoX.Cols);
        Assert.Equal(24, twoX.Rows);
        Assert.Equal(AgentHostProtocol.CaptureModes.Render, twoX.Mode);
    }

    [Fact]
    public void Scale_is_clamped_to_the_protocol_ceiling()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry);
        using var service = NewService(registry);

        var result = Result(Handle(service, CaptureRequestLine(registration.PaneId, scale: 99)));

        Assert.Equal(AgentHostProtocol.MaxCaptureScale, result.Scale);
    }

    [Fact]
    public void A_capture_is_still_byte_identical_at_a_given_scale()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, "deterministic at 2x");
        using var service = NewService(registry);

        var first = Result(Handle(service, CaptureRequestLine(registration.PaneId, id: 1, scale: 2)));
        var second = Result(Handle(service, CaptureRequestLine(registration.PaneId, id: 2, scale: 2)));

        Assert.Equal(File.ReadAllBytes(first.FilePath), File.ReadAllBytes(second.FilePath));
    }

    [Fact]
    public void The_pixel_budget_is_measured_in_device_pixels_not_dips()
    {
        // A grid that fits at 1x can miss at 3x, because scale squares the pixel
        // count. Checking DIPs would under-count by scale squared and let the
        // capture allocate nine times the budget.
        var registry = new AgentSessionRegistry();
        var metrics = new CellMetrics { CellWidth = 40f, CellHeight = 40f, Baseline = 30f, Ascent = 30f, Descent = 10f, Leading = 0f };
        var registration = Register(registry, metrics: metrics); // 80x24 grid => 3200x960 DIPs = 3.07M
        using var service = NewService(registry);

        var fits = Result(Handle(service, CaptureRequestLine(registration.PaneId, id: 1, scale: 2)));
        Assert.Equal(6408, fits.Width); // 12.3M device pixels, inside the 16M budget

        var response = Handle(service, CaptureRequestLine(registration.PaneId, id: 2, scale: 3));
        Assert.Equal(AgentHostProtocol.ErrorCodes.CaptureUnavailable, response.Error?.Code); // 27.6M
        Assert.Contains("per-capture budget at scale 3", response.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Live_mode_without_a_window_reports_captureUnavailable_and_points_at_render()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry);
        using var service = NewService(registry); // no executor published

        var response = Handle(service, CaptureRequestLine(registration.PaneId, mode: "live"));

        Assert.Equal(AgentHostProtocol.ErrorCodes.CaptureUnavailable, response.Error?.Code);
        Assert.Contains("mode 'render'", response.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Live_mode_with_no_pane_on_screen_reports_captureUnavailable()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry);
        using var service = NewService(registry);
        service.SetActionExecutor(new StubExecutor()); // returns null: nothing on screen

        var response = Handle(service, CaptureRequestLine(registration.PaneId, mode: "live"));

        Assert.Equal(AgentHostProtocol.ErrorCodes.CaptureUnavailable, response.Error?.Code);
        Assert.Contains("no on-screen size", response.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Live_mode_returns_the_bridge_image_and_the_buffers_grid()
    {
        // The bridge owns the pixels (it is the only thing that may touch the UI
        // thread), so the endpoint's job is to pass the request through, write the
        // file, and fill in cols/rows from the buffer.
        var registry = new AgentSessionRegistry();
        var registration = Register(registry);
        using var service = NewService(registry);
        var png = TinyPng();
        var executor = new StubExecutor
        {
            OnCaptureLive = (_, _, _) => new AgentLiveCapture(png, 1234, 567, Downscaled: true),
        };
        service.SetActionExecutor(executor);

        var result = Result(Handle(service, CaptureRequestLine(registration.PaneId, maxWidth: 400, scale: 2, mode: "live")));

        Assert.Equal(AgentHostProtocol.CaptureModes.Live, result.Mode);
        Assert.Equal(1234, result.Width);
        Assert.Equal(567, result.Height);
        Assert.True(result.Downscaled);
        Assert.Equal(80, result.Cols);
        Assert.Equal(24, result.Rows);
        Assert.Equal(png, File.ReadAllBytes(result.FilePath));

        // The knobs reach the bridge rather than being silently dropped.
        Assert.Equal(registration.PaneId, executor.LastLiveCapturePane);
        Assert.Equal(400, executor.LastLiveCaptureMaxWidth);
        Assert.Equal(2.0, executor.LastLiveCaptureScale);
    }

    [Fact]
    public void An_unknown_mode_is_malformed_rather_than_coerced_to_render()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry);
        using var service = NewService(registry);

        var response = Handle(service, CaptureRequestLine(registration.PaneId, mode: "wysiwyg"));

        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, response.Error?.Code);
        Assert.False(Directory.Exists(_exportDir) && Directory.EnumerateFiles(_exportDir).Any());
    }

    /// <summary>Smallest valid PNG, for tests that only care that bytes round-trip.</summary>
    private static byte[] TinyPng() =>
    [
        0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00,
        0x1F, 0x15, 0xC4, 0x89,
    ];

    // U+F09B (a GitHub mark) is in the bundled Symbols Nerd Font Mono and in no plain
    // monospace face, so a capture can only draw it by resolving font fallback.
    private const int FallbackOnlyCodePoint = 0xF09B;

    [Fact]
    public void A_glyph_the_primary_face_lacks_is_drawn_through_font_fallback()
    {
        // Regression for the notdef-box bug found by looking at a real capture: the draw
        // operation resolves per-codepoint fallback only inside its `_glyphCache != null`
        // branch, and the capture passed null - so every glyph outside the primary face
        // came out as a notdef box while the live view rendered it from the fallback chain.
        //
        // The assertion is pixel equality against the same glyph rendered with the symbols
        // font as the *primary* face, which needs no fallback at all. That is what makes
        // this discriminating: a notdef box also has ink (measured: 42 pixels against the
        // glyph's 55), so "the cell is not blank" would pass while the bug was present.
        //
        // The primary face is pinned to the bundled Cascadia Mono PL rather than the
        // default family. The default resolves through the installed-font chain, which on
        // a machine with any Nerd Font installed lands on one that *has* this glyph - and
        // then nothing exercises fallback and the test proves nothing. Both fonts here
        // ship in the binary, so this behaves the same on every machine.
        var registry = new AgentSessionRegistry();
        var registration = Register(
            registry,
            char.ConvertFromUtf32(FallbackOnlyCodePoint),
            fontFamily: BundledFontCatalog.CascadiaFontFamily);
        using var service = NewService(registry);

        var result = Result(Handle(service, CaptureRequestLine(registration.PaneId)));
        using var captured = SKBitmap.Decode(File.ReadAllBytes(result.FilePath));
        Assert.NotNull(captured);

        using var reference = RenderIconCell(BundledFontCatalog.SymbolsFontFamily, withGlyphCache: true);
        Assert.True(
            FirstCellMatches(captured!, reference),
            "the capture should draw the glyph the symbols font draws, not a notdef box");
    }

    [Fact]
    public void Rendering_without_a_glyph_cache_draws_a_notdef_box_instead()
    {
        // The other half of the pin: shows the assertion above can fail, and that the
        // glyph cache is the thing that decides it. Without one, the same content renders
        // differently from the reference - that difference is the bug this PR fixes.
        using var reference = RenderIconCell(BundledFontCatalog.SymbolsFontFamily, withGlyphCache: true);
        using var withCache = RenderIconCell(BundledFontCatalog.CascadiaFontFamily, withGlyphCache: true);
        using var withoutCache = RenderIconCell(BundledFontCatalog.CascadiaFontFamily, withGlyphCache: false);

        Assert.True(FirstCellMatches(withCache, reference), "with a glyph cache, fallback resolves the real glyph");
        Assert.False(FirstCellMatches(withoutCache, reference), "without one, the glyph is not resolved");
    }

    /// <summary>
    /// Renders <see cref="FallbackOnlyCodePoint"/> in the first cell with the given
    /// primary family, mirroring the options the capture path uses.
    /// </summary>
    private static SKBitmap RenderIconCell(string fontFamily, bool withGlyphCache)
    {
        var buffer = new TerminalBuffer(8, 2);
        new AnsiParser(buffer).Process(char.ConvertFromUtf32(FallbackOnlyCodePoint));

        var options = new TerminalSnapshotOptions
        {
            FontResolution = SnapshotFontResolution.LiveParity,
            FillBackground = true,
            TypefaceFamily = fontFamily,
            FontSize = 14f,
            HideCursor = true,
        };
        int width = (int)Math.Ceiling(8 * (double)Metrics.CellWidth);
        int height = (int)Math.Ceiling(2 * (double)Metrics.CellHeight);

        if (!withGlyphCache)
        {
            return TerminalSnapshotRenderer.Capture(buffer, Metrics, width, height, options);
        }
        using var glyphCache = new Ntilde.Rendering.GlyphCache();
        return TerminalSnapshotRenderer.Capture(buffer, Metrics, width, height, options with { GlyphCache = glyphCache });
    }

    /// <summary>Pixel-compares the first cell of two renders drawn at the same metrics.</summary>
    private static bool FirstCellMatches(SKBitmap left, SKBitmap right)
    {
        int w = (int)Metrics.CellWidth;
        int h = (int)Metrics.CellHeight;
        if (left.Width < w || left.Height < h || right.Width < w || right.Height < h) return false;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (left.GetPixel(x, y) != right.GetPixel(x, y)) return false;
            }
        }
        return true;
    }

    [Fact]
    public void Capture_file_names_are_unique_within_the_same_second()
    {
        var timestamp = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Local);
        var first = AgentHostService.BuildCaptureFileName(timestamp, Guid.NewGuid().ToString("N"));
        var second = AgentHostService.BuildCaptureFileName(timestamp, Guid.NewGuid().ToString("N"));

        Assert.NotEqual(first, second);
        Assert.StartsWith("ntilde_screen_20260731_120000_", first, StringComparison.Ordinal);
        Assert.EndsWith(".png", first, StringComparison.Ordinal);
    }
}
