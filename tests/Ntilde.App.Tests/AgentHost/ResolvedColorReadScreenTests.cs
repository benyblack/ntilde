using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Ntilde.AgentHost;
using Ntilde.AgentHost.Contracts;
using Ntilde.VT;

namespace Ntilde.AppTests.AgentHost;

/// <summary>
/// Tests for readScreen's opt-in resolved-color projection: theme-resolved per-cell
/// foreground/background RGB plus the palette/theme binding they were resolved against.
/// The values must follow the draw path's resolution precedence and stay absent from
/// responses that did not request them.
/// </summary>
public class ResolvedColorReadScreenTests : IDisposable
{
    private readonly string _tempDir;

    public ResolvedColorReadScreenTests()
    {
        _tempDir = AgentHostTestEndpoint.CreateTempDir("rslv");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private AgentHostService NewService(AgentSessionRegistry registry)
        => new(registry, AgentHostTestEndpoint.CreateEndpoint(_tempDir), _tempDir);

    private static AgentSessionRegistration Register(AgentSessionRegistry registry, TerminalBuffer buffer)
    {
        var registration = new AgentSessionRegistration(Guid.NewGuid(), buffer, "title", "Profile", "local", isActive: true);
        Assert.True(registry.Register(registration));
        return registration;
    }

    private static string RequestLine(string method, object? paramsObj)
        => $"{{\"v\":{AgentHostProtocol.Version},\"id\":1,\"method\":\"{method}\",\"params\":"
            + JsonSerializer.Serialize((ReadScreenParams)paramsObj!, AgentHostJsonContext.Default.ReadScreenParams) + "}";

    private static AgentHostResponse Handle(AgentHostService service, string line)
        => service.HandleRequestLineAsync(line, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    private ScreenSnapshotDto ReadResolved(TerminalBuffer buffer)
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, buffer);
        using var service = NewService(registry);
        return ResultOf(Handle(service, RequestLine(AgentHostProtocol.Methods.ReadScreen,
            new ReadScreenParams { PaneId = registration.PaneId, IncludeResolvedColors = true })),
            AgentHostJsonContext.Default.ScreenSnapshotDto);
    }

    private static T ResultOf<T>(AgentHostResponse response, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        Assert.Null(response.Error);
        Assert.NotNull(response.Result);
        var value = response.Result!.Value.Deserialize(typeInfo);
        Assert.NotNull(value);
        return value!;
    }

    private static ResolvedChannelDto[] Expand(ScreenSnapshotDto dto)
        => dto.ResolvedColors!.SelectMany(run => Enumerable.Repeat(run, run.Count)).Select(run => run.Fg).ToArray();

    private static ResolvedChannelDto[] ExpandBackgrounds(ScreenSnapshotDto dto)
        => dto.ResolvedColors!.SelectMany(run => Enumerable.Repeat(run, run.Count)).Select(run => run.Bg).ToArray();

    private static uint Rgb(int r, int g, int b) => 0xFF000000u | (uint)r << 16 | (uint)g << 8 | (uint)b;

    [Fact]
    public void ResolvedColors_are_an_explicit_opt_in()
    {
        var buffer = new TerminalBuffer(20, 3);
        new AnsiParser(buffer).Process("C\x1b[31mX");
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, buffer);
        using var service = NewService(registry);
        var response = Handle(service, RequestLine(AgentHostProtocol.Methods.ReadScreen,
            new ReadScreenParams { PaneId = registration.PaneId }));
        Assert.Null(ResultOf(response, AgentHostJsonContext.Default.ScreenSnapshotDto).ResolvedColors);
        Assert.False(response.Result!.Value.TryGetProperty("resolvedColors", out _));
        Assert.False(response.Result!.Value.TryGetProperty("colorBinding", out _));
    }

    [Fact]
    public void ResolvedColors_follow_the_draw_path_precedence_and_rederive_from_the_binding()
    {
        var buffer = new TerminalBuffer(20, 3);
        new AnsiParser(buffer).Process("C\x1b[31mX\x1b[34mY\x1b[38;2;10;20;30mZ\x1b[48;2;1;2;3mW\x1b[0m");
        var dto = ReadResolved(buffer);
        Assert.NotNull(dto.ResolvedColors);
        Assert.NotNull(dto.ColorBinding);

        var binding = dto.ColorBinding!;
        Assert.Equal("theme-resolution", binding.Stage);
        Assert.Equal(256, binding.AnsiPaletteArgb.Length);
        Assert.False(binding.ReverseVideo);
        Assert.Empty(binding.PaletteOverrideIndices);

        var foregrounds = Expand(dto);
        var backgrounds = ExpandBackgrounds(dto);
        Assert.Equal(20 * 3, foregrounds.Length);

        // Default cell resolves to the theme default recorded in the binding.
        Assert.Equal("default", foregrounds[0].Source);
        Assert.Equal(-1, foregrounds[0].Index);
        Assert.Equal(binding.ForegroundArgb, foregrounds[0].Argb);
        Assert.Equal("default", backgrounds[0].Source);
        Assert.Equal(binding.BackgroundArgb, backgrounds[0].Argb);

        // SGR 31/34 expose palette entries 1 and 4 of the binding's palette.
        Assert.Equal("palette", foregrounds[1].Source);
        Assert.Equal(1, foregrounds[1].Index);
        Assert.Equal(binding.AnsiPaletteArgb[1], foregrounds[1].Argb);
        Assert.Equal("palette", foregrounds[2].Source);
        Assert.Equal(4, foregrounds[2].Index);
        Assert.Equal(binding.AnsiPaletteArgb[4], foregrounds[2].Argb);

        // Direct RGB is literal storage, resolved to exactly the stored bytes.
        Assert.Equal("literal", foregrounds[3].Source);
        Assert.Equal(Rgb(10, 20, 30), foregrounds[3].Argb);
        Assert.Equal("literal", backgrounds[4].Source);
        Assert.Equal(Rgb(1, 2, 3), backgrounds[4].Argb);

        // SGR 0 returns both channels to the theme defaults.
        Assert.Equal("default", foregrounds[5].Source);
        Assert.Equal("default", backgrounds[5].Source);

        // Every channel re-derives from the binding and its recorded storage state —
        // the coherence rule the qualification evidence checks byte for byte. Literal
        // values are the exact stored bytes (asserted above); here they only stay opaque.
        for (var i = 0; i < foregrounds.Length; i++)
        {
            var expectedFg = foregrounds[i].Source switch
            {
                "default" => binding.ForegroundArgb,
                "palette" => binding.AnsiPaletteArgb[foregrounds[i].Index],
                _ => foregrounds[i].Argb,
            };
            var expectedBg = backgrounds[i].Source switch
            {
                "default" => binding.BackgroundArgb,
                "palette" => binding.AnsiPaletteArgb[backgrounds[i].Index],
                _ => backgrounds[i].Argb,
            };
            Assert.Equal(expectedFg, foregrounds[i].Argb);
            Assert.Equal(expectedBg, backgrounds[i].Argb);
            Assert.Equal(0xFF000000u, foregrounds[i].Argb & 0xFF000000u);
            Assert.Equal(0xFF000000u, backgrounds[i].Argb & 0xFF000000u);
        }
    }

    [Fact]
    public void ResolvedColors_runs_are_canonical_and_complete()
    {
        var buffer = new TerminalBuffer(12, 2);
        new AnsiParser(buffer).Process("AB\x1b[31mC\x1b[31mD\x1b[34mE");
        var dto = ReadResolved(buffer);
        var runs = dto.ResolvedColors!;
        Assert.Equal(12 * 2, runs.Sum(run => run.Count));
        for (var i = 1; i < runs.Length; i++)
        {
            // Canonical: adjacent runs differ in at least one channel.
            Assert.True(runs[i - 1].Fg != runs[i].Fg || runs[i - 1].Bg != runs[i].Bg);
        }
        // AB default, CD palette 1 (coalesced), E palette 4, remainder default.
        Assert.Equal(new[] { 2, 2, 1, 12 * 2 - 5 }, runs.Select(run => run.Count));
    }

    [Fact]
    public void ResolvedColors_reflect_palette_overrides_and_DECSCNM_in_the_binding()
    {
        var buffer = new TerminalBuffer(20, 3);
        var parser = new AnsiParser(buffer);
        // OSC 4 overrides palette entry 1 with a distinctive literal color.
        parser.Process("\x1b]4;1;rgb:aa/bb/cc\x1b\\C\x1b[31mX");
        var overridden = ReadResolved(buffer);
        var binding = overridden.ColorBinding!;
        Assert.Equal(new[] { 1 }, binding.PaletteOverrideIndices);
        Assert.Equal(Rgb(0xAA, 0xBB, 0xCC), binding.AnsiPaletteArgb[1]);
        Assert.Equal(binding.AnsiPaletteArgb[1], Expand(overridden)[1].Argb);

        // DECSCNM swaps the presented defaults; the binding says so and the defaults follow.
        parser.Process("\x1b[?5h");
        var reversed = ReadResolved(buffer);
        var reversedBinding = reversed.ColorBinding!;
        Assert.True(reversedBinding.ReverseVideo);
        Assert.Equal(overridden.ColorBinding!.ForegroundArgb, reversedBinding.BackgroundArgb);
        Assert.Equal(overridden.ColorBinding.BackgroundArgb, reversedBinding.ForegroundArgb);
    }

    [Fact]
    public void ResolvedColors_refuse_viewports_beyond_the_cell_budget_without_partial_data()
    {
        // 200x50 = 10 000 cells > MaxCellDetailCells; the typed refusal carries no result.
        var buffer = new TerminalBuffer(200, 50);
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, buffer);
        using var service = NewService(registry);
        var response = Handle(service, RequestLine(AgentHostProtocol.Methods.ReadScreen,
            new ReadScreenParams { PaneId = registration.PaneId, IncludeResolvedColors = true }));
        Assert.Equal(AgentHostProtocol.ErrorCodes.ScreenDetailsUnavailable, response.Error?.Code);
        Assert.Null(response.Result);
    }

    [Fact]
    public void ResolvedColors_refuse_a_resize_between_the_two_captures()
    {
        // A caller that read one geometry must never receive resolved runs of another:
        // the expected-dimensions check fails closed instead of mixing two screens.
        var buffer = new TerminalBuffer(20, 3);
        new AnsiParser(buffer).Process("C\x1b[31mX");
        Assert.False(ResolvedColorSnapshot.TryCapture(buffer, out var runs, out var binding, minimumContrast: 1,
            expectedRows: 20, expectedCols: 4));
        Assert.Null(runs);
        Assert.Null(binding);
        // The matching expectation still captures.
        Assert.True(ResolvedColorSnapshot.TryCapture(buffer, out runs, out binding, minimumContrast: 1,
            expectedRows: 20, expectedCols: 3));
        Assert.NotNull(runs);
        Assert.NotNull(binding);
    }

    [Fact]
    public void ResolvedColors_are_captured_with_stored_details_in_one_response()
    {
        // The projection composes with the stored opt-ins: flags/indices stay the stored
        // projection's business, resolved RGB this one's, and both describe the same cells.
        var buffer = new TerminalBuffer(20, 3);
        new AnsiParser(buffer).Process("\x1b[31mX");
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, buffer);
        using var service = NewService(registry);
        var dto = ResultOf(Handle(service, RequestLine(AgentHostProtocol.Methods.ReadScreen,
            new ReadScreenParams { PaneId = registration.PaneId, IncludeCellDetails = true, IncludeResolvedColors = true })),
            AgentHostJsonContext.Default.ScreenSnapshotDto);
        Assert.NotNull(dto.CellDetails);
        Assert.NotNull(dto.ResolvedColors);
        var stored = dto.CellDetails!.SelectMany(run => Enumerable.Repeat(run, run.Count)).ToArray();
        var resolved = dto.ResolvedColors!.SelectMany(run => Enumerable.Repeat(run, run.Count)).ToArray();
        Assert.Equal(stored.Length, resolved.Length);
        Assert.Equal(1, stored[0].Fg);
        Assert.Equal("palette", resolved[0].Fg.Source);
        Assert.Equal(1, resolved[0].Fg.Index);
        Assert.Equal(dto.ColorBinding!.AnsiPaletteArgb[1], resolved[0].Fg.Argb);
    }
}
