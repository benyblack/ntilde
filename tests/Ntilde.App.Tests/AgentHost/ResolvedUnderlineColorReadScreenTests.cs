using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Ntilde.AgentHost;
using Ntilde.AgentHost.Contracts;
using Ntilde.VT;

namespace Ntilde.AppTests.AgentHost;

/// <summary>
/// Tests for readScreen's opt-in resolved underline color (<c>includeResolvedUnderlineColors</c>): the
/// draw path's own underline-color precedence, captured with the foreground/background projection
/// from one render snapshot. Explicit indexed and literal colors, the default-inherit marker, inverse
/// and DECSCNM, palette mutation/restoration and the additive wire shape.
/// </summary>
public class ResolvedUnderlineColorReadScreenTests : IDisposable
{
    private readonly string _tempDir;

    public ResolvedUnderlineColorReadScreenTests()
    {
        _tempDir = AgentHostTestEndpoint.CreateTempDir("rsul");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static string RequestLine(ReadScreenParams p)
        => $"{{\"v\":{AgentHostProtocol.Version},\"id\":1,\"method\":\"{AgentHostProtocol.Methods.ReadScreen}\",\"params\":"
            + JsonSerializer.Serialize(p, AgentHostJsonContext.Default.ReadScreenParams) + "}";

    private AgentHostResponse Read(TerminalBuffer buffer, bool resolved, bool underline)
    {
        var registry = new AgentSessionRegistry();
        var registration = new AgentSessionRegistration(Guid.NewGuid(), buffer, "title", "Profile", "local", isActive: true);
        Assert.True(registry.Register(registration));
        using var service = new AgentHostService(registry, AgentHostTestEndpoint.CreateEndpoint(_tempDir), _tempDir);
        return service.HandleRequestLineAsync(RequestLine(new ReadScreenParams
        {
            PaneId = registration.PaneId,
            IncludeResolvedColors = resolved,
            IncludeResolvedUnderlineColors = underline,
        }), TestContext.Current.CancellationToken).GetAwaiter().GetResult();
    }

    private ScreenSnapshotDto ReadUnderline(string input, int cols = 20, int rows = 3, Action<TerminalBuffer, AnsiParser>? then = null)
    {
        var buffer = new TerminalBuffer(cols, rows);
        var parser = new AnsiParser(buffer);
        parser.Process(input);
        then?.Invoke(buffer, parser);
        var response = Read(buffer, resolved: true, underline: true);
        Assert.Null(response.Error);
        return response.Result!.Value.Deserialize(AgentHostJsonContext.Default.ScreenSnapshotDto)!;
    }

    private static ResolvedColorRunDto[] Cells(ScreenSnapshotDto dto)
        => dto.ResolvedColors!.SelectMany(run => Enumerable.Repeat(run, run.Count)).ToArray();

    private static uint Rgb(int r, int g, int b) => 0xFF000000u | (uint)r << 16 | (uint)g << 8 | (uint)b;

    [Fact]
    public void Underline_resolution_is_an_explicit_opt_in_and_leaves_the_channel_projection_unchanged()
    {
        var buffer = new TerminalBuffer(20, 3);
        new AnsiParser(buffer).Process("\x1b[4;58;5;5mX\x1b[0m");
        var plain = Read(buffer, resolved: true, underline: false).Result!.Value;
        Assert.False(plain.GetProperty("resolvedColors")[0].TryGetProperty("ul", out _));
        Assert.False(plain.GetProperty("colorBinding").TryGetProperty("underlineStage", out _));
        Assert.False(plain.GetProperty("colorBinding").TryGetProperty("underlineStageNote", out _));

        // The flag alone, without resolved colors, is ignored exactly as includeOverline is without details.
        var alone = Read(buffer, resolved: false, underline: true);
        Assert.Null(alone.Error);
        Assert.False(alone.Result!.Value.TryGetProperty("resolvedColors", out _));
        Assert.False(alone.Result!.Value.TryGetProperty("colorBinding", out _));

        var withUl = ReadUnderline("\x1b[4;58;5;5mX\x1b[0m");
        Assert.Equal("underline-resolution", withUl.ColorBinding!.UnderlineStage);
        Assert.Contains("rendered appearance may still differ", withUl.ColorBinding.UnderlineStageNote);
        // Foreground/background of the same cells are identical with and without the opt-in.
        var withCells = Cells(withUl);
        var plainDto = Read(buffer, resolved: true, underline: false).Result!.Value.Deserialize(AgentHostJsonContext.Default.ScreenSnapshotDto)!;
        Assert.Equal(Cells(plainDto).Select(c => (c.Fg, c.Bg)), withCells.Select(c => (c.Fg, c.Bg)));
    }

    [Fact]
    public void Indexed_literal_default_and_absent_underlines_resolve_through_the_draw_path_precedence()
    {
        // X indexed 5, Y literal, Z default marker after SGR 59, W has no underline.
        var dto = ReadUnderline("\x1b[4m\x1b[58;5;5mX\x1b[58;2;255;0;128mY\x1b[59mZ\x1b[24mW");
        var binding = dto.ColorBinding!;
        var cells = Cells(dto);

        Assert.Equal("palette", cells[0].Ul!.Source);
        Assert.Equal(5, cells[0].Ul!.Index);
        Assert.Equal(binding.AnsiPaletteArgb[5], cells[0].Ul!.Argb);
        Assert.Null(cells[0].Ul!.InheritsFrom);

        Assert.Equal("literal", cells[1].Ul!.Source);
        Assert.Equal(-1, cells[1].Ul!.Index);
        Assert.Equal(Rgb(255, 0, 128), cells[1].Ul!.Argb);

        Assert.Equal("inherit", cells[2].Ul!.Source);
        Assert.Equal(-1, cells[2].Ul!.Index);
        Assert.Equal("foreground", cells[2].Ul!.InheritsFrom);
        Assert.Equal(binding.ForegroundArgb, cells[2].Ul!.Argb);

        Assert.Equal("none", cells[3].Ul!.Source);
        Assert.Null(cells[3].Ul!.Argb);
        Assert.Equal("none", cells[10].Ul!.Source);
    }

    [Fact]
    public void Default_underline_inherits_the_cells_own_foreground_resolution()
    {
        // Explicit foreground index 5 / literal; underline default: it follows the channel, not the theme default.
        var dto = ReadUnderline("\x1b[4;38;5;5mA\x1b[38;2;10;20;30mB\x1b[39mC");
        var binding = dto.ColorBinding!;
        var cells = Cells(dto);
        Assert.Equal("inherit", cells[0].Ul!.Source);
        Assert.Equal(binding.AnsiPaletteArgb[5], cells[0].Ul!.Argb);
        Assert.Equal(cells[0].Fg.Argb, cells[0].Ul!.Argb);
        Assert.Equal(Rgb(10, 20, 30), cells[1].Ul!.Argb);
        Assert.Equal(binding.ForegroundArgb, cells[2].Ul!.Argb);
    }

    [Fact]
    public void Inverse_video_makes_a_default_underline_inherit_the_background_but_never_changes_an_explicit_color()
    {
        // A: default underline, inverse, foreground index 1, background index 4. B: explicit index 5, same pen.
        var dto = ReadUnderline("\x1b[4;7;31;44mA\x1b[58;5;5mB\x1b[27mC\x1b[59mD");
        var binding = dto.ColorBinding!;
        var cells = Cells(dto);
        Assert.Equal("inherit", cells[0].Ul!.Source);
        Assert.Equal("background", cells[0].Ul!.InheritsFrom);
        Assert.Equal(binding.AnsiPaletteArgb[4], cells[0].Ul!.Argb);
        Assert.Equal("palette", cells[1].Ul!.Source);
        Assert.Equal(binding.AnsiPaletteArgb[5], cells[1].Ul!.Argb);
        // Inverse off keeps the explicit color; SGR 59 then returns the underline to the foreground.
        Assert.Equal("palette", cells[2].Ul!.Source);
        Assert.Equal(binding.AnsiPaletteArgb[5], cells[2].Ul!.Argb);
        Assert.Equal("inherit", cells[3].Ul!.Source);
        Assert.Equal("foreground", cells[3].Ul!.InheritsFrom);
        Assert.Equal(binding.AnsiPaletteArgb[1], cells[3].Ul!.Argb);
    }

    [Fact]
    public void Inverse_with_default_channels_inherits_the_default_background()
    {
        var dto = ReadUnderline("\x1b[4;7mA");
        var cells = Cells(dto);
        Assert.Equal("background", cells[0].Ul!.InheritsFrom);
        Assert.Equal(dto.ColorBinding!.BackgroundArgb, cells[0].Ul!.Argb);
    }

    [Fact]
    public void Reverse_video_mode_swaps_the_presented_defaults_the_underline_inherits()
    {
        var dto = ReadUnderline("\x1b[?5h\x1b[4mA\x1b[7mB");
        var binding = dto.ColorBinding!;
        Assert.True(binding.ReverseVideo);
        var cells = Cells(dto);
        // The binding's defaults are already the presented (swapped) colors; the underline follows them.
        Assert.Equal(binding.ForegroundArgb, cells[0].Ul!.Argb);
        Assert.Equal(binding.BackgroundArgb, cells[1].Ul!.Argb);
    }

    [Fact]
    public void Faint_hidden_and_blink_do_not_alter_the_theme_stage_value()
    {
        var plain = Cells(ReadUnderline("\x1b[4;38;5;5mA"))[0].Ul!;
        var faint = Cells(ReadUnderline("\x1b[4;2;38;5;5mA"))[0].Ul!;
        var hidden = Cells(ReadUnderline("\x1b[4;8;38;5;5mA"))[0].Ul!;
        Assert.Equal(plain, faint);
        Assert.Equal(plain, hidden);
    }

    [Fact]
    public void Indexed_underline_follows_palette_mutation_and_restoration_while_literal_does_not()
    {
        const string fill = "\x1b[4m\x1b[58;5;5mX\x1b[58;2;255;0;128mY\x1b[0m";
        var buffer = new TerminalBuffer(20, 3);
        var parser = new AnsiParser(buffer);
        parser.Process(fill);
        ScreenSnapshotDto Capture()
        {
            var response = Read(buffer, resolved: true, underline: true);
            Assert.Null(response.Error);
            return response.Result!.Value.Deserialize(AgentHostJsonContext.Default.ScreenSnapshotDto)!;
        }

        var start = Capture();
        parser.Process("\x1b]4;5;rgb:11/22/33\x1b\\");
        var mutated = Capture();
        parser.Process("\x1b]104;5\x1b\\");
        var restored = Capture();

        Assert.Equal(new[] { 5 }, mutated.ColorBinding!.PaletteOverrideIndices);
        Assert.Equal(Rgb(0x11, 0x22, 0x33), Cells(mutated)[0].Ul!.Argb);
        Assert.Equal(mutated.ColorBinding.AnsiPaletteArgb[5], Cells(mutated)[0].Ul!.Argb);
        Assert.Equal(5, Cells(mutated)[0].Ul!.Index); // storage did not change
        Assert.Equal(Rgb(255, 0, 128), Cells(mutated)[1].Ul!.Argb); // literal did not follow
        Assert.Equal(Cells(start)[0].Ul, Cells(restored)[0].Ul);
        Assert.Equal(start.ColorBinding!.AnsiPaletteArgb, restored.ColorBinding!.AnsiPaletteArgb);
        Assert.Empty(restored.ColorBinding.PaletteOverrideIndices);
        Assert.NotEqual(Cells(start)[0].Ul!.Argb, Cells(mutated)[0].Ul!.Argb);
    }

    [Fact]
    public void Runs_split_when_only_the_underline_resolution_differs_and_stay_canonical()
    {
        // Same fg/bg everywhere; three underline states: palette 5 (x2), literal, none.
        var dto = ReadUnderline("\x1b[4;58;5;5mAB\x1b[58;2;1;2;3mC\x1b[24mD", cols: 6, rows: 1);
        var runs = dto.ResolvedColors!;
        Assert.Equal(new[] { 2, 1, 3 }, runs.Select(r => r.Count));
        for (var i = 1; i < runs.Length; i++) Assert.NotEqual(runs[i - 1].Ul, runs[i].Ul);
        Assert.Equal(6, runs.Sum(r => r.Count));
    }

    [Fact]
    public void Underline_resolution_is_captured_with_stored_details_from_the_same_cells()
    {
        var buffer = new TerminalBuffer(20, 3);
        new AnsiParser(buffer).Process("\x1b[4:3;58;5;5mX");
        var registry = new AgentSessionRegistry();
        var registration = new AgentSessionRegistration(Guid.NewGuid(), buffer, "title", "Profile", "local", isActive: true);
        Assert.True(registry.Register(registration));
        using var service = new AgentHostService(registry, AgentHostTestEndpoint.CreateEndpoint(_tempDir), _tempDir);
        var response = service.HandleRequestLineAsync(RequestLine(new ReadScreenParams
        {
            PaneId = registration.PaneId,
            IncludeCellDetails = true,
            IncludeResolvedColors = true,
            IncludeResolvedUnderlineColors = true,
        }), TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        var dto = response.Result!.Value.Deserialize(AgentHostJsonContext.Default.ScreenSnapshotDto)!;
        var stored = dto.CellDetails!.SelectMany(run => Enumerable.Repeat(run, run.Count)).ToArray();
        var resolved = Cells(dto);
        Assert.Equal(stored.Length, resolved.Length);
        Assert.Equal(5, stored[0].UnderlineColorIndex);
        Assert.Equal("palette", resolved[0].Ul!.Source);
        Assert.Equal(5, resolved[0].Ul!.Index);
    }

    [Fact]
    public void Underline_projection_keeps_the_viewport_budget_and_refuses_without_partial_data()
    {
        var buffer = new TerminalBuffer(200, 50);
        var response = Read(buffer, resolved: true, underline: true);
        Assert.Equal(AgentHostProtocol.ErrorCodes.ScreenDetailsUnavailable, response.Error?.Code);
        Assert.Null(response.Result);
    }

    [Fact]
    public void Underline_projection_refuses_a_resize_between_the_two_captures()
    {
        var buffer = new TerminalBuffer(20, 3);
        new AnsiParser(buffer).Process("\x1b[4mX");
        Assert.False(ResolvedColorSnapshot.TryCapture(buffer, out var runs, out var binding, minimumContrast: 1,
            expectedRows: 3, expectedCols: 21, includeUnderline: true));
        Assert.Null(runs);
        Assert.Null(binding);
    }
}
