using System.Text.Json.Serialization;

namespace Ntilde.AgentHost.Contracts;

/// <summary>
/// One live terminal session as reported by <c>listSessions</c>: a pane, or a windowless session (a multiplexer
/// session no pane of the window shows; <see cref="Windowless"/> is true and <see cref="PaneId"/> is its mux session id).
/// </summary>
public sealed record SessionInfo
{
    [JsonPropertyName("paneId")]
    public required Guid PaneId { get; init; }

    /// <summary>
    /// Owning tab, or null when the pane has not yet been associated with a
    /// tab (association happens lazily in the UI layer; freshly created split
    /// or replacement panes may briefly be unassociated). Clients must treat
    /// null as "unknown", never as an identity. Not <c>required</c>: the wire
    /// format omits the field entirely when null (WhenWritingNull).
    /// </summary>
    [JsonPropertyName("tabId")]
    public Guid? TabId { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("profileName")]
    public required string ProfileName { get; init; }

    /// <summary>"local" or "ssh".</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("rows")]
    public required int Rows { get; init; }

    [JsonPropertyName("cols")]
    public required int Cols { get; init; }

    /// <summary>True for the active pane of the active tab.</summary>
    [JsonPropertyName("isActive")]
    public required bool IsActive { get; init; }

    /// <summary>
    /// Current status (<see cref="AgentHostProtocol.StatusKinds"/>), when the
    /// endpoint computes it (A2+). Null from older endpoints. Not required:
    /// the wire format omits it when null (WhenWritingNull).
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>How the status was derived (<see cref="AgentHostProtocol.StatusConfidences"/>); null when status is null.</summary>
    [JsonPropertyName("confidence")]
    public string? Confidence { get; init; }

    /// <summary>
    /// True for a windowless session: one running in a multiplexer daemon (this computer's, or a connected SSH host's)
    /// that no pane of the window shows. Its <see cref="PaneId"/> is its mux session id, and every per-session method
    /// takes it. Null for a pane, so a pane's row is unchanged on the wire (WhenWritingNull).
    /// </summary>
    [JsonPropertyName("windowless")]
    public bool? Windowless { get; init; }

    /// <summary>
    /// A windowless session's daemon: <c>local</c>, or <c>ssh:</c> and the SSH profile id. Null for a pane.
    /// </summary>
    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; init; }
}

/// <summary>Result payload for <c>listSessions</c>.</summary>
public sealed record ListSessionsResult
{
    [JsonPropertyName("sessions")]
    public required SessionInfo[] Sessions { get; init; }
}

/// <summary>Params for <c>readScreen</c>.</summary>
public sealed record ReadScreenParams
{
    [JsonPropertyName("paneId")]
    public required Guid PaneId { get; init; }

    /// <summary>When true, per-row attribute lines are included (BufferSnapshot format).</summary>
    [JsonPropertyName("includeAttributes")]
    public bool IncludeAttributes { get; init; }

    /// <summary>
    /// Include bounded, row-major runs of stored cell colors and underline metadata.
    /// These are buffer values, not resolved theme colors or rendered pixels. A viewport exceeding
    /// the cell or run budget returns <c>screenDetailsUnavailable</c>, never a partial projection.
    /// </summary>
    [JsonPropertyName("includeCellDetails")]
    public bool IncludeCellDetails { get; init; }

    /// <summary>Include overline booleans in cell details; only effective with IncludeCellDetails.
    /// Explicit opt-in preserves canonical run boundaries for older clients.</summary>
    [JsonPropertyName("includeOverline")]
    public bool IncludeOverline { get; init; }

    /// <summary>Include stored cursor style/blink/default modes, without resolving host preferences or blink phase.</summary>
    [JsonPropertyName("includeCursorMode")]
    public bool IncludeCursorMode { get; init; }

    /// <summary>
    /// Include per-cell foreground/background RGB resolved through the pane's live render theme
    /// (the storage-to-theme resolution the draw path performs), plus the theme binding those
    /// values were resolved against. Captured from the same coherent render snapshot as the
    /// resolved cells; a viewport exceeding the cell or run budget returns
    /// <c>screenDetailsUnavailable</c>, never a partial projection. These are theme-resolved
    /// channel colors, not stored attributes (<see cref="IncludeCellDetails"/>) and not rendered
    /// pixels (contrast adjustment, opacity compositing and rasterisation are not applied).
    /// </summary>
    [JsonPropertyName("includeResolvedColors")]
    public bool IncludeResolvedColors { get; init; }

    /// <summary>Include observed margins, editing modes, pending wrap, active screen and local history count.</summary>
    [JsonPropertyName("includeBufferState"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IncludeBufferState { get; init; }
}

/// <summary>
/// Result payload for <c>readScreen</c>: a 1:1 projection of the deterministic
/// <c>Ntilde.Replay.BufferSnapshot</c> capture plus cursor state. The A1
/// parity test asserts this equals a direct <c>BufferSnapshot.Capture</c> of
/// the same buffer. Optional cell details extend this projection when explicitly requested.
/// </summary>
public sealed record ScreenSnapshotDto
{
    /// <summary>Visible viewport, one string per row, top to bottom.</summary>
    [JsonPropertyName("lines")]
    public required string[] Lines { get; init; }

    /// <summary>Per-row attribute encoding; present only when requested.</summary>
    [JsonPropertyName("attributeLines")]
    public string[]? AttributeLines { get; init; }

    /// <summary>
    /// Canonical runs covering every physical viewport cell, including wide-character continuation cells.
    /// Present only when requested; captured under the same read lock as lines and cursor state.
    /// </summary>
    [JsonPropertyName("cellDetails")]
    public CellDetailRunDto[]? CellDetails { get; init; }

    /// <summary>Stored cursor modes, captured under the same read lock; present only when requested.</summary>
    [JsonPropertyName("cursorMode")]
    public CursorModeDto? CursorMode { get; init; }

    /// <summary>
    /// Canonical runs of theme-resolved foreground/background colors covering every physical
    /// viewport cell. Present only when requested; captured with its theme binding from one
    /// coherent render snapshot (the same cells-plus-theme capture the renderer consumes).
    /// </summary>
    [JsonPropertyName("resolvedColors")]
    public ResolvedColorRunDto[]? ResolvedColors { get; init; }

    /// <summary>The palette/theme/render state the resolved colors were resolved against; present only with resolvedColors.</summary>
    [JsonPropertyName("colorBinding")]
    public ColorBindingDto? ColorBinding { get; init; }

    /// <summary>Stored editing state captured under the same lock; omitted unless explicitly requested.</summary>
    [JsonPropertyName("bufferState")]
    public BufferStateDto? BufferState { get; init; }

    /// <summary>Cursor row in viewport coordinates (0-based).</summary>
    [JsonPropertyName("cursorRow")]
    public required int CursorRow { get; init; }

    /// <summary>Cursor column (0-based).</summary>
    [JsonPropertyName("cursorCol")]
    public required int CursorCol { get; init; }

    [JsonPropertyName("cursorVisible")]
    public required bool CursorVisible { get; init; }

    [JsonPropertyName("rows")]
    public required int Rows { get; init; }

    [JsonPropertyName("cols")]
    public required int Cols { get; init; }
}

/// <summary>Stored cursor mode, not resolved UI appearance or a measured blink phase.</summary>
public sealed record CursorModeDto
{
    /// <summary>0 block, 1 bar, 2 underline. When IsDefault is true, the host may present its configured style.</summary>
    [JsonPropertyName("style")]
    public required int Style { get; init; }

    [JsonPropertyName("blink")]
    public required bool Blink { get; init; }

    [JsonPropertyName("isDefault")]
    public required bool IsDefault { get; init; }
}

/// <summary>
/// One row-major run of identical stored cell attributes. Flags exclude the transient Dirty bit.
/// Default and palette flags distinguish inherited colors and indexed colors from literal ARGB.
/// Literal colors are unsigned packed ARGB (alpha in the high byte); defaults and palette entries
/// omit ARGB, without resolving themes, inverse, faint, selection or renderer effects.
/// </summary>
public sealed record CellDetailRunDto
{
    [JsonPropertyName("count")]
    public required int Count { get; init; }

    [JsonPropertyName("flags")]
    public required ushort Flags { get; init; }

    /// <summary>Palette index 0..255, or -1 for default/literal colors.</summary>
    [JsonPropertyName("fg")]
    public required short Fg { get; init; }

    [JsonPropertyName("bg")]
    public required short Bg { get; init; }

    [JsonPropertyName("foregroundArgb")]
    public uint? ForegroundArgb { get; init; }

    [JsonPropertyName("backgroundArgb")]
    public uint? BackgroundArgb { get; init; }

    /// <summary>0 when disabled; otherwise single=1, double=2, curly=3, dotted=4, dashed=5.</summary>
    [JsonPropertyName("underlineStyle")]
    public required byte UnderlineStyle { get; init; }

    /// <summary>Underline palette index, or -1. -1 with no ARGB inherits the text color.</summary>
    [JsonPropertyName("underlineColorIndex")]
    public required short UnderlineColorIndex { get; init; }

    [JsonPropertyName("underlineArgb")]
    public uint? UnderlineArgb { get; init; }

    /// <summary>Stored SGR 53 state, present only when includeOverline was requested.</summary>
    [JsonPropertyName("overline"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Overline { get; init; }
}

/// <summary>
/// How one cell channel's color was resolved and to which RGB value: the storage state
/// (<c>default</c>, <c>palette</c>, or <c>literal</c>) plus the resolved ARGB. Alpha is always
/// opaque at this stage — opacity compositing happens later in the render pipeline.
/// </summary>
public sealed record ResolvedChannelDto
{
    /// <summary>One of <c>default</c>, <c>palette</c>, <c>literal</c>.</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    /// <summary>Palette index 0..255 for <c>palette</c>; -1 otherwise.</summary>
    [JsonPropertyName("index")]
    public required short Index { get; init; }

    /// <summary>The resolved color, packed ARGB (alpha in the high byte).</summary>
    [JsonPropertyName("argb")]
    public required uint Argb { get; init; }
}

/// <summary>
/// One row-major run of identical theme-resolved foreground/background colors. Runs are
/// canonical (adjacent runs differ) and cover every physical viewport cell, wide-character
/// continuation cells included. Each channel names the storage state it was resolved from, so
/// a resolved value can always be re-derived from the binding and the stored state.
/// </summary>
public sealed record ResolvedColorRunDto
{
    [JsonPropertyName("count")]
    public required int Count { get; init; }

    [JsonPropertyName("fg")]
    public required ResolvedChannelDto Fg { get; init; }

    [JsonPropertyName("bg")]
    public required ResolvedChannelDto Bg { get; init; }
}

/// <summary>
/// The palette/theme/render state the resolved colors were resolved against, recorded with them
/// so a reader can re-derive every resolved value and knows exactly which stage of the render
/// pipeline the values belong to. Under DECSCNM the default foreground/background here are the
/// presented (swapped) colors, exactly as the renderer receives them.
/// </summary>
public sealed record ColorBindingDto
{
    /// <summary>Fixed marker for this projection's resolution stage: <c>theme-resolution</c>.</summary>
    [JsonPropertyName("stage")]
    public required string Stage { get; init; }

    /// <summary>What the stage resolves and what it deliberately does not (contrast, compositing, rasterisation).</summary>
    [JsonPropertyName("stageNote")]
    public required string StageNote { get; init; }

    /// <summary>The theme's name, when the render theme carries one.</summary>
    [JsonPropertyName("themeName")]
    public string? ThemeName { get; init; }

    /// <summary>Resolved default foreground, packed ARGB (the presented color under DECSCNM).</summary>
    [JsonPropertyName("foregroundArgb")]
    public required uint ForegroundArgb { get; init; }

    /// <summary>Resolved default background, packed ARGB (the presented color under DECSCNM).</summary>
    [JsonPropertyName("backgroundArgb")]
    public required uint BackgroundArgb { get; init; }

    /// <summary>The 256-entry ANSI palette the palette indices resolve through, packed ARGB per entry.</summary>
    [JsonPropertyName("ansiPaletteArgb")]
    public required uint[] AnsiPaletteArgb { get; init; }

    /// <summary>DECSCNM (?5) was set at capture; the defaults above are already swapped.</summary>
    [JsonPropertyName("reverseVideo")]
    public required bool ReverseVideo { get; init; }

    /// <summary>The theme's foreground default is an explicit override rather than the theme default.</summary>
    [JsonPropertyName("foregroundOverride")]
    public required bool ForegroundOverride { get; init; }

    /// <summary>The theme's background default is an explicit override rather than the theme default.</summary>
    [JsonPropertyName("backgroundOverride")]
    public required bool BackgroundOverride { get; init; }

    /// <summary>Indices whose palette entries carry runtime overrides (OSC 4/104 style), ascending.</summary>
    [JsonPropertyName("paletteOverrideIndices")]
    public required int[] PaletteOverrideIndices { get; init; }

    /// <summary>
    /// The pane's Minimum contrast setting when known (1 is off). Recorded because it is applied
    /// later in the pipeline: resolved values here are pre-contrast, so rendered glyphs may differ.
    /// Null when the reading endpoint has no pane render parameters (a windowless import).
    /// </summary>
    [JsonPropertyName("minimumContrast"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MinimumContrast { get; init; }
}

/// <summary>Params for <c>captureScreen</c> (A5).</summary>
public sealed record CaptureScreenParams
{
    [JsonPropertyName("paneId")]
    public required Guid PaneId { get; init; }

    /// <summary>
    /// When true the endpoint also returns the PNG bytes base64-encoded in
    /// <see cref="CaptureScreenResult.PngBase64"/>, so a caller that cannot read
    /// local files still sees the image. Dropped when the PNG exceeds
    /// <see cref="AgentHostProtocol.MaxInlineCaptureBytes"/>.
    /// </summary>
    [JsonPropertyName("inline")]
    public bool Inline { get; init; }

    /// <summary>
    /// Resample the capture down to at most this pixel width, preserving aspect
    /// ratio. 0 (the default) delivers the capture at its rendered size. Applied
    /// after the render, so it trades detail for payload size; to render *more*
    /// detail, raise <see cref="Scale"/> instead.
    /// </summary>
    [JsonPropertyName("maxWidth")]
    public int MaxWidth { get; init; }

    /// <summary>
    /// Device pixels per DIP to render at, from 1 to
    /// <see cref="AgentHostProtocol.MaxCaptureScale"/>. 0 or absent means 1.
    /// Raising it makes the image bigger and the text easier to read back; the
    /// value is the caller's, never the monitor's, so a capture stays
    /// reproducible.
    /// </summary>
    [JsonPropertyName("scale")]
    public double Scale { get; init; }

    /// <summary>
    /// <see cref="AgentHostProtocol.CaptureModes"/>: <c>render</c> (default) for
    /// the deterministic headless re-render, or <c>live</c> to photograph the
    /// on-screen control. Absent, empty, or unrecognized-with-correct-casing is
    /// not silently coerced — an unknown mode is a malformed request, so a typo
    /// cannot quietly get a different picture than the caller asked for.
    /// </summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    /// <summary>Synthetic milliseconds since a visible phase (0..60,000), for render mode only.
    /// Does not sample, advance or reset the live view's clock. Absent preserves visible phases.</summary>
    [JsonPropertyName("presentationTimeMs")]
    public long? PresentationTimeMs { get; init; }

    /// <summary>Synthetic viewport selection for render mode only; does not change the live selection.</summary>
    [JsonPropertyName("syntheticSelection"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CaptureSelection? SyntheticSelection { get; init; }

    /// <summary>Include the actual render font, pixel grid, palette and presentation facts. Render mode only.</summary>
    [JsonPropertyName("includeRenderDetails")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IncludeRenderDetails { get; init; }

    /// <summary>Exact draw image/creation facts. Render mode only, with includeRenderDetails.
    /// Default off; missing legacy provenance cannot qualify native SIXEL geometry.</summary>
    [JsonPropertyName("includeImageDetails"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IncludeImageDetails { get; init; }
}

/// <summary>Result payload for <c>captureScreen</c> (A5).</summary>
public sealed record CaptureScreenResult
{
    /// <summary>Absolute path of the written PNG.</summary>
    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    /// <summary>Pixel width of the delivered image (after any downscale).</summary>
    [JsonPropertyName("width")]
    public required int Width { get; init; }

    /// <summary>Pixel height of the delivered image (after any downscale).</summary>
    [JsonPropertyName("height")]
    public required int Height { get; init; }

    /// <summary>Grid columns that were rendered.</summary>
    [JsonPropertyName("cols")]
    public required int Cols { get; init; }

    /// <summary>Grid rows that were rendered.</summary>
    [JsonPropertyName("rows")]
    public required int Rows { get; init; }

    /// <summary>Size of the PNG on disk, in bytes.</summary>
    [JsonPropertyName("byteCount")]
    public required int ByteCount { get; init; }

    /// <summary>True when <c>maxWidth</c> forced a resample.</summary>
    [JsonPropertyName("downscaled")]
    public required bool Downscaled { get; init; }

    /// <summary>
    /// Which mode produced this image. Echoed rather than assumed: a caller that
    /// asked for <c>live</c> should be able to see that it got <c>live</c>.
    /// Defaulted rather than <c>required</c> so an older client reading a newer
    /// endpoint's result still deserializes.
    /// </summary>
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = AgentHostProtocol.CaptureModes.Render;

    /// <summary>The scale the capture was rendered at, after clamping.</summary>
    [JsonPropertyName("scale")]
    public double Scale { get; init; } = 1.0;

    /// <summary>
    /// Base64 PNG, present only when the caller asked for it and it fit under the
    /// inline cap. Omitted from the wire when null.
    /// </summary>
    [JsonPropertyName("pngBase64")]
    public string? PngBase64 { get; init; }

    /// <summary>
    /// True when the caller asked for an inline image and the endpoint dropped it
    /// for exceeding <see cref="AgentHostProtocol.MaxInlineCaptureBytes"/>. The
    /// file on disk is unaffected.
    /// </summary>
    [JsonPropertyName("inlineOmitted")]
    public bool InlineOmitted { get; init; }

    /// <summary>Echoed only when explicitly requested, so an older endpoint cannot silently supply another phase.</summary>
    [JsonPropertyName("presentationTimeMs")]
    public long? PresentationTimeMs { get; init; }

    /// <summary>Echoed only when a synthetic selection was requested and rendered.</summary>
    [JsonPropertyName("syntheticSelection"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CaptureSelection? SyntheticSelection { get; init; }

    [JsonPropertyName("renderDetails")]
    public CaptureRenderDetails? RenderDetails { get; init; }

    [JsonPropertyName("imageDetails"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CaptureImageDetails? ImageDetails { get; init; }
}

/// <summary>Params for <c>readScrollback</c> (ranged read, oldest line = 0).</summary>
public sealed record ReadScrollbackParams
{
    [JsonPropertyName("paneId")]
    public required Guid PaneId { get; init; }

    /// <summary>First scrollback line to return (0-based, oldest first).</summary>
    [JsonPropertyName("startLine")]
    public required int StartLine { get; init; }

    /// <summary>
    /// Maximum lines to return; the server additionally caps this at
    /// <see cref="AgentHostProtocol.MaxScrollbackLinesPerRequest"/>.
    /// </summary>
    [JsonPropertyName("maxLines")]
    public required int MaxLines { get; init; }
}

/// <summary>Result payload for <c>readScrollback</c>.</summary>
public sealed record ReadScrollbackResult
{
    [JsonPropertyName("lines")]
    public required string[] Lines { get; init; }

    /// <summary>Echo of the effective start line after clamping.</summary>
    [JsonPropertyName("startLine")]
    public required int StartLine { get; init; }

    /// <summary>
    /// Total scrollback lines available at capture time. For a windowless session (<see cref="SessionInfo.Windowless"/>)
    /// that is at most the newest 2000 rows its daemon holds, the most one read of it carries.
    /// </summary>
    [JsonPropertyName("totalLines")]
    public required int TotalLines { get; init; }
}
