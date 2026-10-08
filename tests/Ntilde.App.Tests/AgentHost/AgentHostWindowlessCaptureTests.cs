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
/// <c>captureScreen</c> on a windowless session (Phase 5 spec §3): <c>render</c> draws the daemon's screen through the
/// same renderer a pane capture uses, with the font metrics borrowed from a pane of the window (a windowless session
/// was never measured); <c>live</c> has no control to photograph and is refused.
///
/// In the GoldenPng collection and the PlatformBoot lane for the reason <see cref="AgentHostCaptureProtocolTests"/>
/// gives: the renderer resolves fonts through Avalonia's font manager, which needs the headless platform booted once,
/// by one thread.
/// </summary>
[Collection("GoldenPng")]
[Trait("Lane", "PlatformBoot")]
public class AgentHostWindowlessCaptureTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _exportDir;

    private static readonly CellMetrics Metrics = new()
    {
        CellWidth = 8f,
        CellHeight = 16f,
        Baseline = 12f,
        Ascent = 12f,
        Descent = 4f,
        Leading = 0f,
    };

    public AgentHostWindowlessCaptureTests()
    {
        _tempDir = AgentHostTestEndpoint.CreateTempDir("wlcap");
        _exportDir = Path.Combine(_tempDir, "agent-exports");
        SnapshotService.EnsureAvaloniaInitialized();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private AgentHostService NewService(AgentSessionRegistry registry, AgentActivityJournal journal)
        => new(registry, AgentHostTestEndpoint.CreateEndpoint(_tempDir), _tempDir, _exportDir, journal);

    /// <summary>A measured pane, whose render parameters (and theme) a windowless capture borrows.</summary>
    private static AgentSessionRegistration RegisterMeasuredPane(AgentSessionRegistry registry, TerminalTheme? theme = null, CellMetrics? metrics = null)
    {
        var buffer = new TerminalBuffer(80, 24);
        if (theme != null) buffer.Theme = theme;
        var registration = new AgentSessionRegistration(Guid.NewGuid(), buffer, "title", "Profile", "local", isActive: true);
        registration.UpdateRenderParameters(new PaneRenderParameters(
            metrics ?? Metrics, TerminalSnapshotOptions.DefaultTypefaceFamily, FontSize: 14f, EnableLigatures: false, EnableComplexShaping: true));
        Assert.True(registry.Register(registration));
        return registration;
    }

    private static string CaptureLine(Guid id, string? mode = null, bool inline = false) => $"{{\"v\":{AgentHostProtocol.Version},\"id\":1,\"method\":\"{AgentHostProtocol.Methods.CaptureScreen}\",\"params\":{JsonSerializer.Serialize(new CaptureScreenParams { PaneId = id, Mode = mode, Inline = inline }, AgentHostJsonContext.Default.CaptureScreenParams)}}}";

    private static AgentHostResponse Handle(AgentHostService service, string line)
        => service.HandleRequestLineAsync(line, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    [Fact]
    public void Render_draws_the_sessions_own_grid_at_a_panes_cell_size_and_is_journaled()
    {
        var registry = new AgentSessionRegistry();
        var paneTheme = new TerminalTheme { Background = TermColor.FromRgb(0x12, 0x34, 0x56) };
        RegisterMeasuredPane(registry, paneTheme);
        var source = new StubWindowlessSource();
        var session = source.Add("drawn from the daemon's screen", cols: 100, rows: 30);
        var journal = new AgentActivityJournal();
        using var service = NewService(registry, journal);
        service.SetWindowlessSource(source);

        var response = Handle(service, CaptureLine(session.SessionId, inline: true));

        Assert.Null(response.Error);
        var result = response.Result!.Value.Deserialize(AgentHostJsonContext.Default.CaptureScreenResult)!;
        // The session's 100x30 grid, not the pane's 80x24: only the cell size is borrowed.
        Assert.Equal(100, result.Cols);
        Assert.Equal(30, result.Rows);
        Assert.Equal(100 * 8, result.Width);
        Assert.Equal(30 * 16, result.Height);
        Assert.Equal(AgentHostProtocol.CaptureModes.Render, result.Mode);
        Assert.StartsWith(Path.GetFullPath(_exportDir), result.FilePath, StringComparison.Ordinal);

        var bytes = File.ReadAllBytes(result.FilePath);
        Assert.Equal(bytes, Convert.FromBase64String(result.PngBase64!));
        using var bitmap = SKBitmap.Decode(bytes);
        Assert.Equal(100 * 8, bitmap.Width);
        Assert.Equal(30 * 16, bitmap.Height);
        // An empty cell shows the pane's theme background: the capture looks like the user's terminal.
        Assert.Equal(new SKColor(0x12, 0x34, 0x56), bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1));

        var entry = Assert.Single(journal.Snapshot());
        Assert.Equal(AgentHostProtocol.Methods.CaptureScreen, entry.Method);
        Assert.Equal(session.SessionId, entry.PaneId);
        Assert.Equal("ok", entry.Outcome);
        Assert.True(service.WindowlessWatched);
    }

    [Fact]
    public void Live_mode_on_a_windowless_session_is_captureUnavailable()
    {
        var registry = new AgentSessionRegistry();
        RegisterMeasuredPane(registry);
        var source = new StubWindowlessSource();
        var session = source.Add("no window");
        var journal = new AgentActivityJournal();
        using var service = NewService(registry, journal);
        service.SetWindowlessSource(source);
        service.SetActionExecutor(new StubExecutor { OnCaptureLive = (_, _, _) => throw new InvalidOperationException("never asked") });

        var response = Handle(service, CaptureLine(session.SessionId, mode: AgentHostProtocol.CaptureModes.Live));

        Assert.Equal(AgentHostProtocol.ErrorCodes.CaptureUnavailable, response.Error?.Code);
        Assert.Contains("a windowless session has no window to capture", response.Error!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(nameof(IWindowlessSessionSource.ReadScreenAsync), source.Calls); // nothing was read
        var entry = Assert.Single(journal.Snapshot());
        Assert.Equal((AgentHostProtocol.ErrorCodes.CaptureUnavailable, "windowless · this computer"), (entry.Outcome, entry.Target));
        Assert.False(Directory.Exists(_exportDir) && Directory.EnumerateFiles(_exportDir).Any());
    }

    [Fact]
    public void Render_with_no_measured_pane_to_borrow_metrics_from_is_captureUnavailable()
    {
        var source = new StubWindowlessSource();
        var session = source.Add("no metrics");
        using var service = NewService(new AgentSessionRegistry(), new AgentActivityJournal());
        service.SetWindowlessSource(source);

        var response = Handle(service, CaptureLine(session.SessionId));

        Assert.Equal(AgentHostProtocol.ErrorCodes.CaptureUnavailable, response.Error?.Code);
        Assert.Contains("no open pane to take font metrics from", response.Error!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(IWindowlessSessionSource.ReadScreenAsync), source.Calls);
    }

    [Fact]
    public void Render_over_the_pixel_budget_is_captureUnavailable_and_writes_nothing()
    {
        var registry = new AgentSessionRegistry();
        var huge = new CellMetrics { CellWidth = 5000f, CellHeight = 5000f, Baseline = 4000f, Ascent = 4000f, Descent = 1000f, Leading = 0f };
        RegisterMeasuredPane(registry, metrics: huge);
        var source = new StubWindowlessSource();
        var session = source.Add("too big to draw");
        var journal = new AgentActivityJournal();
        using var service = NewService(registry, journal);
        service.SetWindowlessSource(source);

        var response = Handle(service, CaptureLine(session.SessionId));

        Assert.Equal(AgentHostProtocol.ErrorCodes.CaptureUnavailable, response.Error?.Code);
        Assert.Contains("pixel per-capture budget", response.Error!.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_exportDir) && Directory.EnumerateFiles(_exportDir).Any());
        var entry = Assert.Single(journal.Snapshot());
        Assert.Equal((AgentHostProtocol.ErrorCodes.CaptureUnavailable, "windowless · this computer"), (entry.Outcome, entry.Target));
        Assert.False(service.WindowlessWatched); // nothing was disclosed
    }

    [Fact]
    public void Capture_of_an_id_no_daemon_has_is_sessionNotFound_in_either_mode()
    {
        var registry = new AgentSessionRegistry();
        RegisterMeasuredPane(registry);
        var source = new StubWindowlessSource();
        var journal = new AgentActivityJournal();
        using var service = NewService(registry, journal);
        service.SetWindowlessSource(source);

        Assert.Equal(AgentHostProtocol.ErrorCodes.SessionNotFound, Handle(service, CaptureLine(Guid.NewGuid())).Error?.Code);
        Assert.Equal(AgentHostProtocol.ErrorCodes.SessionNotFound, Handle(service, CaptureLine(Guid.NewGuid(), mode: AgentHostProtocol.CaptureModes.Live)).Error?.Code);
        Assert.Empty(journal.Snapshot());
    }
}
