using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Ntilde.AgentHost;
using Ntilde.AgentHost.Contracts;
using Ntilde.Replay;
using Ntilde.VT;

namespace Ntilde.AppTests.AgentHost;

/// <summary>
/// Tests for the agent-host observe endpoint (PR3 of milestone A1,
/// docs/plans/2026-07-07-agent-host-a1-observe-design.md). Request handling
/// is covered in-process via HandleRequestLine; the transport (named pipe on
/// Windows, unix socket elsewhere) is covered by real connect/round-trip
/// tests using a private endpoint name so parallel test runs don't collide.
/// </summary>
public class AgentHostServiceTests : IDisposable
{
    private readonly string _tempDir;

    public AgentHostServiceTests()
    {
        _tempDir = AgentHostTestEndpoint.CreateTempDir("svc");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string NewEndpointName()
    {
        return AgentHostTestEndpoint.CreateEndpoint(_tempDir);
    }

    private AgentHostService NewService(AgentSessionRegistry registry)
        => new(registry, NewEndpointName(), _tempDir);

    private static AgentSessionRegistration Register(AgentSessionRegistry registry, TerminalBuffer buffer, Guid? paneId = null)
    {
        var registration = new AgentSessionRegistration(
            paneId ?? Guid.NewGuid(), buffer, "title", "Profile", "local", isActive: true);
        Assert.True(registry.Register(registration));
        return registration;
    }

    private static string RequestLine(string method, long id = 1, object? paramsObj = null, int version = AgentHostProtocol.Version)
    {
        var paramsJson = paramsObj switch
        {
            null => "null",
            ReadScreenParams p => JsonSerializer.Serialize(p, AgentHostJsonContext.Default.ReadScreenParams),
            ReadScrollbackParams p => JsonSerializer.Serialize(p, AgentHostJsonContext.Default.ReadScrollbackParams),
            _ => throw new InvalidOperationException("unexpected params type"),
        };
        return $"{{\"v\":{version},\"id\":{id},\"method\":\"{method}\",\"params\":{paramsJson}}}";
    }

    /// <summary>Sync convenience over the async handler for non-long-poll requests.</summary>
    private static AgentHostResponse Handle(AgentHostService service, string line)
        => service.HandleRequestLineAsync(line, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    private static T ResultOf<T>(AgentHostResponse response, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        Assert.Null(response.Error);
        Assert.NotNull(response.Result);
        var value = response.Result!.Value.Deserialize(typeInfo);
        Assert.NotNull(value);
        return value!;
    }

    // ── Request handling (in-process) ────────────────────────────────────────

    [Fact]
    public void Version_mismatch_is_rejected_with_stable_error_code()
    {
        using var service = NewService(new AgentSessionRegistry());
        var response = Handle(service, RequestLine(AgentHostProtocol.Methods.ListSessions, version: 99));
        Assert.Equal(AgentHostProtocol.ErrorCodes.VersionMismatch, response.Error?.Code);
    }

    [Fact]
    public void Malformed_frame_is_rejected_not_thrown()
    {
        using var service = NewService(new AgentSessionRegistry());
        var response = Handle(service, "this is not json");
        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, response.Error?.Code);
    }

    [Fact]
    public void Unknown_method_is_rejected_with_stable_error_code()
    {
        using var service = NewService(new AgentSessionRegistry());
        var response = Handle(service, RequestLine("thisMethodDoesNotExist"));
        Assert.Equal(AgentHostProtocol.ErrorCodes.UnknownMethod, response.Error?.Code);
    }

    [Fact]
    public void Missing_params_is_a_malformed_request_not_a_missing_session()
    {
        using var service = NewService(new AgentSessionRegistry());
        var readScreen = Handle(service, RequestLine(AgentHostProtocol.Methods.ReadScreen));
        var readScrollback = Handle(service, RequestLine(AgentHostProtocol.Methods.ReadScrollback));
        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, readScreen.Error?.Code);
        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, readScrollback.Error?.Code);
    }

    [Fact]
    public void ReadScrollback_preserves_extended_text_graphemes()
    {
        // Emoji / multi-codepoint graphemes are stored as extended text; they
        // must survive scrolling off screen (parity with readScreen).
        var registry = new AgentSessionRegistry();
        var buffer = new TerminalBuffer(20, 3);
        var parser = new AnsiParser(buffer);
        parser.Process("ok \U0001F44D done\r\n");
        for (var i = 0; i < 6; i++)
        {
            parser.Process($"filler-{i}\r\n");
        }
        var registration = Register(registry, buffer);
        using var service = NewService(registry);

        var response = Handle(service, RequestLine(
            AgentHostProtocol.Methods.ReadScrollback,
            paramsObj: new ReadScrollbackParams { PaneId = registration.PaneId, StartLine = 0, MaxLines = 10 }));
        var result = ResultOf(response, AgentHostJsonContext.Default.ReadScrollbackResult);

        Assert.Contains(result.Lines, line => line.Contains("ok \U0001F44D done", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadScreen_for_unknown_pane_reports_session_not_found()
    {
        using var service = NewService(new AgentSessionRegistry());
        var response = Handle(service, RequestLine(
            AgentHostProtocol.Methods.ReadScreen,
            paramsObj: new ReadScreenParams { PaneId = Guid.NewGuid() }));
        Assert.Equal(AgentHostProtocol.ErrorCodes.SessionNotFound, response.Error?.Code);
    }

    [Fact]
    public void ReadScreen_matches_BufferSnapshot_capture_exactly()
    {
        // The A1 parity invariant: what an agent reads over the wire is the
        // deterministic snapshot, byte for byte.
        var registry = new AgentSessionRegistry();
        var buffer = new TerminalBuffer(40, 10);
        var parser = new AnsiParser(buffer);
        parser.Process("hello \x1b[1mworld\x1b[0m\r\nsecond line\r\nwide: 你好");
        var registration = Register(registry, buffer);
        using var service = NewService(registry);

        var response = Handle(service, RequestLine(
            AgentHostProtocol.Methods.ReadScreen,
            paramsObj: new ReadScreenParams { PaneId = registration.PaneId, IncludeAttributes = true }));
        var dto = ResultOf(response, AgentHostJsonContext.Default.ScreenSnapshotDto);

        var expected = BufferSnapshot.Capture(buffer, includeAttributes: true);
        Assert.Equal(expected.Lines, dto.Lines);
        Assert.Equal(expected.AttributeLines, dto.AttributeLines);
        Assert.Equal(expected.CursorRow, dto.CursorRow);
        Assert.Equal(expected.CursorCol, dto.CursorCol);
        Assert.Equal(10, dto.Rows);
        Assert.Equal(40, dto.Cols);
        Assert.Equal("hello world", dto.Lines[0]);
        Assert.Null(dto.CellDetails);
        Assert.Null(dto.CursorMode);
        Assert.Null(dto.BufferState);
        Assert.DoesNotContain("includeBufferState", JsonSerializer.Serialize(
            new ReadScreenParams { PaneId = registration.PaneId }, AgentHostJsonContext.Default.ReadScreenParams));
        Assert.False(response.Result!.Value.TryGetProperty("bufferState", out _));
        Assert.False(response.Result!.Value.TryGetProperty("cursorMode", out _));
        Assert.False(response.Result!.Value.TryGetProperty("cellDetails", out _));
    }

    private ScreenSnapshotDto ReadDetails(TerminalBuffer buffer, bool includeOverline = false)
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, buffer);
        using var service = NewService(registry);
        return ResultOf(Handle(service, RequestLine(AgentHostProtocol.Methods.ReadScreen,
            paramsObj: new ReadScreenParams { PaneId = registration.PaneId, IncludeCellDetails = true, IncludeOverline = includeOverline })),
            AgentHostJsonContext.Default.ScreenSnapshotDto);
    }

    [Fact]
    public void ReadScreen_overline_is_explicitly_opted_in_and_preserves_legacy_run_boundaries()
    {
        var buffer = new TerminalBuffer(20, 3);
        new AnsiParser(buffer).Process("\x1b[53mA\x1b[55mB\x1b[53mC\x1b[0m");
        var legacy = ReadDetails(buffer);
        Assert.Single(legacy.CellDetails!);
        Assert.Null(legacy.CellDetails![0].Overline);
        Assert.DoesNotContain("overline", JsonSerializer.Serialize(legacy, AgentHostJsonContext.Default.ScreenSnapshotDto));
        var details = ReadDetails(buffer, includeOverline: true);
        Assert.Equal(4, details.CellDetails!.Length);
        var cells = ExpandDetails(details);
        Assert.True(cells[0].Overline);
        Assert.False(cells[1].Overline);
        Assert.True(cells[2].Overline);
        Assert.All(cells.Skip(3), cell => Assert.False(cell.Overline));
        Assert.All(cells, cell => Assert.Equal(0, cell.UnderlineStyle));
        Assert.All(cells, cell => Assert.Null(cell.UnderlineArgb));
        Assert.All(cells, cell => Assert.Equal(-1, cell.UnderlineColorIndex));
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void ReadScreen_overline_respects_the_run_budget_without_fragmenting_legacy_details(int cols, bool allowed)
    {
        var buffer = new TerminalBuffer(cols, 16);
        buffer.Lock.EnterWriteLock();
        try
        {
            var index = 0;
            foreach (var row in buffer.ViewportRows)
                for (int col = 0; col < cols; col++)
                    row.SetUnderline(col, index++ % 2 == 0 ? CellUnderline.OverlineOnly : null);
        }
        finally { buffer.Lock.ExitWriteLock(); }
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, buffer);
        using var service = NewService(registry);
        var response = Handle(service, RequestLine(AgentHostProtocol.Methods.ReadScreen,
            paramsObj: new ReadScreenParams { PaneId = registration.PaneId, IncludeCellDetails = true, IncludeOverline = true }));
        if (allowed)
            Assert.Equal(1024, ResultOf(response, AgentHostJsonContext.Default.ScreenSnapshotDto).CellDetails!.Length);
        else
        {
            Assert.Equal(AgentHostProtocol.ErrorCodes.ScreenDetailsUnavailable, response.Error?.Code);
            Assert.Null(response.Result);
            Assert.Equal(AgentAttentionTier.Idle, registration.AttentionMachine.Snapshot().Tier);
        }
        Assert.Single(ReadDetails(buffer).CellDetails!);
    }

    private static CellDetailRunDto[] ExpandDetails(ScreenSnapshotDto dto)
        => dto.CellDetails!.SelectMany(run => Enumerable.Repeat(run with { Count = 1 }, run.Count)).ToArray();

    [Fact]
    public void ReadScreen_cell_details_preserve_literal_colors_and_default_palette_distinctions()
    {
        var buffer = new TerminalBuffer(20, 3);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b[38;2;1;128;255;48;2;254;2;0mA\x1b[31;44mB\x1b[39;49mC");
        var dto = ReadDetails(buffer);
        var cells = ExpandDetails(dto);
        Assert.Null(dto.AttributeLines); // independent opt-ins
        Assert.Equal(60, cells.Length);
        Assert.Equal(0xff0180ffu, cells[0].ForegroundArgb);
        Assert.Equal(0xfffe0200u, cells[0].BackgroundArgb);
        Assert.Equal(-1, cells[0].Fg);
        Assert.Equal(-1, cells[0].Bg);
        Assert.Equal(0, cells[0].Flags & (ushort)(TerminalCellFlags.DefaultForeground | TerminalCellFlags.PaletteForeground));
        Assert.Equal(1, cells[1].Fg);
        Assert.Equal(4, cells[1].Bg);
        Assert.Null(cells[1].ForegroundArgb);
        Assert.Null(cells[1].BackgroundArgb);
        Assert.Equal((ushort)(TerminalCellFlags.PaletteForeground | TerminalCellFlags.PaletteBackground), cells[1].Flags);
        Assert.Equal((ushort)(TerminalCellFlags.DefaultForeground | TerminalCellFlags.DefaultBackground), cells[2].Flags);
        Assert.Null(cells[2].ForegroundArgb);
        Assert.Null(cells[2].BackgroundArgb);
        Assert.Equal(-1, cells[2].Fg);
        Assert.Equal(-1, cells[2].Bg);
        Assert.All(cells.Skip(2), cell => Assert.Equal(cells[2], cell));
        parser.Process("\x1b[H\x1b[2J");
        Assert.Equal(0xff0180ffu, cells[0].ForegroundArgb); // detached from the live buffer
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ReadScreen_cell_details_project_all_underline_styles_and_colors(int style)
    {
        var buffer = new TerminalBuffer(20, 3);
        new AnsiParser(buffer).Process($"\x1b[4:{style}mA\x1b[58;5;123mB\x1b[58:2::12:34:56mC\x1b[59mD\x1b[24mE\x1b[4;58;2;2;3;4mF\x1b[0mG");
        var cells = ExpandDetails(ReadDetails(buffer));
        foreach (var index in new[] { 0, 1, 2, 3 }) Assert.Equal(style, cells[index].UnderlineStyle);
        Assert.Equal(-1, cells[0].UnderlineColorIndex);
        Assert.Null(cells[0].UnderlineArgb);
        Assert.Equal(123, cells[1].UnderlineColorIndex);
        Assert.Null(cells[1].UnderlineArgb);
        Assert.Equal(-1, cells[2].UnderlineColorIndex);
        Assert.Equal(0xff0c2238u, cells[2].UnderlineArgb);
        Assert.Equal(-1, cells[3].UnderlineColorIndex);
        Assert.Null(cells[3].UnderlineArgb);
        Assert.Equal(1, cells[5].UnderlineStyle);
        Assert.Equal(0xff020304u, cells[5].UnderlineArgb);
        foreach (var index in new[] { 4, 6, 7 })
        {
            Assert.Equal(0, cells[index].UnderlineStyle);
            Assert.Equal(-1, cells[index].UnderlineColorIndex);
            Assert.Null(cells[index].UnderlineArgb);
        }
    }

    [Fact]
    public void ReadScreen_cell_details_cover_wide_continuations_and_remove_dirty_flags()
    {
        var buffer = new TerminalBuffer(20, 3);
        new AnsiParser(buffer).Process("\x1b[4:3;38;2;5;6;7m你");
        var dto = ReadDetails(buffer);
        var cells = ExpandDetails(dto);
        Assert.Equal(60, cells.Length);
        Assert.NotEqual(0, cells[0].Flags & (ushort)TerminalCellFlags.Wide);
        Assert.NotEqual(0, cells[1].Flags & (ushort)TerminalCellFlags.WideContinuation);
        Assert.All(cells, cell => Assert.Equal(0, cell.Flags & (ushort)TerminalCellFlags.Dirty));
        Assert.Equal(0xff050607u, cells[0].ForegroundArgb);
        Assert.Equal(3, cells[0].UnderlineStyle);
        Assert.Equal("你", dto.Lines[0]);
    }

    [Theory]
    [InlineData(64, 128, false, true)] // exactly the cell budget
    [InlineData(65, 128, false, false)]
    [InlineData(64, 16, true, true)] // exactly the run budget
    [InlineData(65, 16, true, false)]
    public void ReadScreen_cell_detail_budgets_refuse_complete_projection_without_affecting_default_reads(
        int cols, int rows, bool alternating, bool allowed)
    {
        var buffer = new TerminalBuffer(cols, rows);
        if (alternating)
        {
            buffer.Lock.EnterWriteLock();
            try
            {
                var index = 0;
                foreach (var row in buffer.ViewportRows)
                    for (var col = 0; col < cols; col++)
                        row.Cells[col].Foreground = new TermColor((byte)(index++ % 2), 2, 3);
            }
            finally { buffer.Lock.ExitWriteLock(); }
        }
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, buffer);
        using var service = NewService(registry);
        Assert.Equal(AgentAttentionTier.Idle, registration.AttentionMachine.Snapshot().Tier);
        var response = Handle(service, RequestLine(AgentHostProtocol.Methods.ReadScreen,
            paramsObj: new ReadScreenParams { PaneId = registration.PaneId, IncludeCellDetails = true }));
        Assert.Equal(allowed ? AgentAttentionTier.Watched : AgentAttentionTier.Idle,
            registration.AttentionMachine.Snapshot().Tier);
        if (allowed)
        {
            var runs = ResultOf(response, AgentHostJsonContext.Default.ScreenSnapshotDto).CellDetails!;
            Assert.Equal(rows * cols, runs.Sum(run => run.Count));
            foreach (var (first, second) in runs.Zip(runs.Skip(1)))
                Assert.NotEqual(first with { Count = 0 }, second with { Count = 0 });
            if (!alternating) Assert.Single(runs);
        }
        else
        {
            Assert.Equal(AgentHostProtocol.ErrorCodes.ScreenDetailsUnavailable, response.Error?.Code);
            Assert.Null(response.Result);
        }
        var plain = Handle(service, RequestLine(AgentHostProtocol.Methods.ReadScreen,
            paramsObj: new ReadScreenParams { PaneId = registration.PaneId }));
        Assert.Null(plain.Error);
        Assert.False(plain.Result!.Value.TryGetProperty("cellDetails", out _));
        Assert.Equal(AgentAttentionTier.Watched, registration.AttentionMachine.Snapshot().Tier);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, 0, true)]
    [InlineData(2, 0, false)]
    [InlineData(3, 2, true)]
    [InlineData(4, 2, false)]
    [InlineData(5, 1, true)]
    [InlineData(6, 1, false)]
    public void ReadScreen_optional_cursor_mode_matches_stored_state_and_is_detached(int parameter, int style, bool blink)
    {
        var buffer = new TerminalBuffer(40, 10);
        var parser = new AnsiParser(buffer);
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, buffer);
        using var service = NewService(registry);
        var request = RequestLine(AgentHostProtocol.Methods.ReadScreen, paramsObj: new ReadScreenParams
        {
            PaneId = registration.PaneId,
            IncludeCursorMode = true,
        });
        var initial = ResultOf(Handle(service, request), AgentHostJsonContext.Default.ScreenSnapshotDto);
        Assert.Equal(new CursorModeDto { Style = 2, Blink = true, IsDefault = true }, initial.CursorMode);
        parser.Process($"AB\x1b[{parameter} q\x1b[?25l");
        var screen = ResultOf(Handle(service, request), AgentHostJsonContext.Default.ScreenSnapshotDto);
        Assert.Equal(new CursorModeDto { Style = style, Blink = blink, IsDefault = false }, screen.CursorMode);
        Assert.Null(screen.CellDetails); // independent opt-ins
        Assert.False(screen.CursorVisible);
        Assert.Equal("AB", screen.Lines[0]);
        Assert.Equal(2, screen.CursorCol);
        parser.Process("\x1b[2 q\x1b[?25h");
        Assert.Equal(style, screen.CursorMode!.Style);
        Assert.Equal(blink, screen.CursorMode.Blink);
        Assert.False(screen.CursorVisible);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadScreen_optional_buffer_state_tracks_editing_modes_and_is_detached(bool alternate)
    {
        var buffer = new TerminalBuffer(40, 10);
        var parser = new AnsiParser(buffer);
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, buffer);
        using var service = NewService(registry);
        var request = RequestLine(AgentHostProtocol.Methods.ReadScreen, paramsObj: new ReadScreenParams
        {
            PaneId = registration.PaneId,
            IncludeBufferState = true,
        });
        var initial = ResultOf(Handle(service, request), AgentHostJsonContext.Default.ScreenSnapshotDto);
        Assert.Equal(new BufferStateDto
        {
            ScrollTop = 0,
            ScrollBottom = 9,
            OriginMode = false,
            AutoWrap = true,
            InsertMode = false,
            PendingWrap = false,
            AlternateScreen = false,
            ScrollbackLines = 0
        }, initial.BufferState);
        parser.Process((alternate ? "\x1b[?1049h" : "") + "\x1b[3;8r\x1b[?6h\x1b[4h\x1b[?7lX");
        var captured = ResultOf(Handle(service, request), AgentHostJsonContext.Default.ScreenSnapshotDto);
        Assert.Equal(new BufferStateDto
        {
            ScrollTop = 2,
            ScrollBottom = 7,
            OriginMode = true,
            AutoWrap = false,
            InsertMode = true,
            PendingWrap = false,
            AlternateScreen = alternate,
            ScrollbackLines = 0
        }, captured.BufferState);
        Assert.Equal(2, captured.CursorRow);
        Assert.Equal(1, captured.CursorCol);
        Assert.Equal("X", captured.Lines[2]);
        Assert.Null(captured.CellDetails);
        Assert.Null(captured.CursorMode);
        parser.Process("\x1b[?1049l\x1b[!p");
        Assert.True(captured.BufferState!.OriginMode);
        Assert.False(captured.BufferState.AutoWrap);
        Assert.Equal(alternate, captured.BufferState.AlternateScreen);
        Assert.False(initial.BufferState!.OriginMode);
    }

    [Fact]
    public void ReadScreen_buffer_state_does_not_require_the_optional_cell_detail_budget()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, new TerminalBuffer(200, 50));
        using var service = NewService(registry);
        var response = Handle(service, RequestLine(AgentHostProtocol.Methods.ReadScreen,
            paramsObj: new ReadScreenParams { PaneId = registration.PaneId, IncludeBufferState = true }));
        var screen = ResultOf(response, AgentHostJsonContext.Default.ScreenSnapshotDto);
        Assert.NotNull(screen.BufferState);
        Assert.Equal(49, screen.BufferState.ScrollBottom);
        Assert.Equal(0, screen.BufferState.ScrollbackLines);
        Assert.Null(screen.CellDetails);
        Assert.Equal(50, screen.Lines.Length);
    }

    [Fact]
    public void ReadScreen_buffer_state_observes_pending_wrap_before_the_next_character()
    {
        var buffer = new TerminalBuffer(4, 3);
        var parser = new AnsiParser(buffer);
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, buffer);
        using var service = NewService(registry);
        var request = RequestLine(AgentHostProtocol.Methods.ReadScreen, paramsObj: new ReadScreenParams
        {
            PaneId = registration.PaneId,
            IncludeBufferState = true,
        });
        parser.Process("ABCD");
        var pending = ResultOf(Handle(service, request), AgentHostJsonContext.Default.ScreenSnapshotDto);
        Assert.True(pending.BufferState!.PendingWrap);
        Assert.Equal(3, pending.CursorCol);
        Assert.Equal(0, pending.CursorRow);
        parser.Process("E");
        var wrapped = ResultOf(Handle(service, request), AgentHostJsonContext.Default.ScreenSnapshotDto);
        Assert.False(wrapped.BufferState!.PendingWrap);
        Assert.Equal(1, wrapped.CursorRow);
        Assert.Equal(1, wrapped.CursorCol);
        Assert.Equal("E", wrapped.Lines[1]);
        Assert.True(pending.BufferState.PendingWrap);
    }

    [Fact]
    public void ReadScreen_buffer_state_observes_ED3_history_removal_without_changing_viewport()
    {
        var buffer = new TerminalBuffer(20, 3);
        var parser = new AnsiParser(buffer);
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, buffer);
        using var service = NewService(registry);
        var request = RequestLine(AgentHostProtocol.Methods.ReadScreen, paramsObj: new ReadScreenParams
        {
            PaneId = registration.PaneId,
            IncludeBufferState = true,
            IncludeCellDetails = true,
        });
        parser.Process("OLD1\r\nOLD2\r\nKEEP1\r\nKEEP2\r\nKEEP3");
        var before = ResultOf(Handle(service, request), AgentHostJsonContext.Default.ScreenSnapshotDto);
        Assert.Equal(2, before.BufferState!.ScrollbackLines);
        Assert.Equal(new[] { "KEEP1", "KEEP2", "KEEP3" }, before.Lines);
        parser.Process("\x1b[3J");
        var after = ResultOf(Handle(service, request), AgentHostJsonContext.Default.ScreenSnapshotDto);
        Assert.Equal(0, after.BufferState!.ScrollbackLines);
        Assert.Equal(before.Lines, after.Lines);
        Assert.Equal(before.CellDetails, after.CellDetails);
        Assert.Equal(before.CursorRow, after.CursorRow);
        Assert.Equal(before.CursorCol, after.CursorCol);
        Assert.Equal(2, before.BufferState.ScrollbackLines);
    }

    [Fact]
    public void ReadScrollback_returns_ranged_lines_with_totals()
    {
        var registry = new AgentSessionRegistry();
        var buffer = new TerminalBuffer(20, 4);
        var parser = new AnsiParser(buffer);
        for (var i = 0; i < 12; i++)
        {
            parser.Process($"line-{i}\r\n");
        }
        var registration = Register(registry, buffer);
        using var service = NewService(registry);

        var response = Handle(service, RequestLine(
            AgentHostProtocol.Methods.ReadScrollback,
            paramsObj: new ReadScrollbackParams { PaneId = registration.PaneId, StartLine = 1, MaxLines = 3 }));
        var result = ResultOf(response, AgentHostJsonContext.Default.ReadScrollbackResult);

        Assert.Equal(1, result.StartLine);
        Assert.Equal(3, result.Lines.Length);
        Assert.Equal(buffer.Scrollback.Count, result.TotalLines);
        Assert.Equal("line-1", result.Lines[0]);
        Assert.Equal("line-3", result.Lines[2]);
    }

    // ── Endpoint lifecycle & transport ───────────────────────────────────────

    [Fact]
    public void Disabled_service_creates_no_endpoint_and_no_discovery_file()
    {
        var registry = new AgentSessionRegistry();
        using var service = NewService(registry);

        service.Apply(false);

        Assert.False(service.IsRunning);
        Assert.False(File.Exists(Path.Combine(_tempDir, AgentHostProtocol.DiscoveryFileName)));
    }

    [Fact]
    public void Stop_retires_the_discovery_descriptor()
    {
        using var service = NewService(new AgentSessionRegistry());
        service.Apply(true);
        var discoveryPath = Path.Combine(_tempDir, AgentHostProtocol.DiscoveryFileName);
        Assert.True(File.Exists(discoveryPath));
        Assert.True(new FileInfo(discoveryPath).Length > 0);

        service.Apply(false);

        // The descriptor is truncated under an exclusive handle rather than
        // deleted (delete-after-close would race a concurrent takeover). An
        // empty file is a stale descriptor: no live endpoint is advertised.
        Assert.False(service.IsRunning);
        Assert.True(!File.Exists(discoveryPath) || new FileInfo(discoveryPath).Length == 0);
    }

    [Fact]
    public void Stop_leaves_a_foreign_live_descriptor_untouched()
    {
        using var service = NewService(new AgentSessionRegistry());
        service.Apply(true);
        var discoveryPath = Path.Combine(_tempDir, AgentHostProtocol.DiscoveryFileName);

        // Simulate another instance taking over the endpoint between our start
        // and our stop: the descriptor on disk is no longer ours.
        var foreign = new EndpointDescriptor
        {
            Version = AgentHostProtocol.Version,
            Endpoint = "someone-elses-endpoint",
            Pid = Environment.ProcessId + 1,
        };
        File.WriteAllText(discoveryPath, JsonSerializer.Serialize(foreign, AgentHostJsonContext.Default.EndpointDescriptor));

        service.Apply(false);

        var survivor = JsonSerializer.Deserialize(
            File.ReadAllText(discoveryPath), AgentHostJsonContext.Default.EndpointDescriptor);
        Assert.NotNull(survivor);
        Assert.Equal("someone-elses-endpoint", survivor!.Endpoint);
    }

    [Fact]
    public async Task ListSessions_round_trips_over_the_real_transport()
    {
        var registry = new AgentSessionRegistry();
        var buffer = new TerminalBuffer(80, 24);
        var registration = Register(registry, buffer);
        using var service = NewService(registry);
        service.Start();
        Assert.True(service.IsRunning);

        // Discovery file advertises the endpoint, exactly as the MCP client will read it.
        var descriptor = JsonSerializer.Deserialize(
            File.ReadAllText(Path.Combine(_tempDir, AgentHostProtocol.DiscoveryFileName)),
            AgentHostJsonContext.Default.EndpointDescriptor);
        Assert.NotNull(descriptor);
        Assert.Equal(AgentHostProtocol.Version, descriptor!.Version);
        Assert.Equal(Environment.ProcessId, descriptor.Pid);

        await using var stream = await ConnectAsync(descriptor.Endpoint);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        await writer.WriteLineAsync(RequestLine(AgentHostProtocol.Methods.ListSessions, id: 42));
        var line = await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.NotNull(line);

        var response = JsonSerializer.Deserialize(line!, AgentHostJsonContext.Default.AgentHostResponse);
        Assert.NotNull(response);
        Assert.Equal(42, response!.Id);
        var sessions = ResultOf(response, AgentHostJsonContext.Default.ListSessionsResult).Sessions;
        var session = Assert.Single(sessions);
        Assert.Equal(registration.PaneId, session.PaneId);
        Assert.Equal(24, session.Rows);
        Assert.Equal(80, session.Cols);
    }

    [Fact]
    public async Task Disabling_the_service_closes_already_connected_clients()
    {
        // Security invariant: turning Agent Access off revokes access for
        // existing connections, not just future ones.
        var registry = new AgentSessionRegistry();
        Register(registry, new TerminalBuffer(80, 24));
        using var service = NewService(registry);
        service.Start();

        var descriptor = JsonSerializer.Deserialize(
            File.ReadAllText(Path.Combine(_tempDir, AgentHostProtocol.DiscoveryFileName)),
            AgentHostJsonContext.Default.EndpointDescriptor)!;
        await using var stream = await ConnectAsync(descriptor.Endpoint);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), leaveOpen: true);
        // Not await-using: disposal flushes, and flushing into the deliberately
        // broken pipe after Stop() would throw during teardown.
        var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        try
        {
            // Prove the connection works, so the assertion below is meaningful.
            await writer.WriteLineAsync(RequestLine(AgentHostProtocol.Methods.ListSessions));
            Assert.NotNull(await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

            service.Stop();

            // The server must have force-closed the stream: the next read observes
            // end-of-stream or a broken pipe/socket, never fresh data.
            try
            {
                var line = await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                Assert.Null(line);
            }
            catch (IOException)
            {
                // Also acceptable: the transport surfaces the closed connection as an error.
            }
        }
        finally
        {
            try { await writer.DisposeAsync(); } catch (IOException) { /* broken pipe is the expected end state */ }
        }
    }

    private static async Task<Stream> ConnectAsync(string endpoint)
    {
        if (OperatingSystem.IsWindows())
        {
            var client = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(10_000);
            return client;
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint));
        return new NetworkStream(socket, ownsSocket: true);
    }
}
