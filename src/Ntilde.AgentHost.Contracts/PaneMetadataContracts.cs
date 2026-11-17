using System.Text.Json.Serialization;

namespace Ntilde.AgentHost.Contracts;

/// <summary>Read-only observation of one attached direct local pane; no tab activation or input.</summary>
public sealed record ReadPaneMetadataParams
{
    [JsonPropertyName("paneId")]
    public required Guid PaneId { get; init; }
}

/// <summary>Stored active-buffer facts, UI metadata and actual control properties, not a pixel capture.</summary>
public sealed record PaneMetadataResult
{
    [JsonPropertyName("paneId")] public required Guid PaneId { get; init; }
    [JsonPropertyName("oscTitle")] public string? OscTitle { get; init; }
    [JsonPropertyName("workingDirectory")] public string? WorkingDirectory { get; init; }
    [JsonPropertyName("displayTitle")] public required string DisplayTitle { get; init; }
    [JsonPropertyName("buffer")] public required PaneBufferMetadata Buffer { get; init; }
    [JsonPropertyName("presentation")] public required PanePresentationMetadata Presentation { get; init; }
    [JsonPropertyName("cellGeometry"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PaneCellGeometry? CellGeometry { get; init; }
    /// <summary>App request and actual Avalonia Cursor property; not the native pointer image.</summary>
    [JsonPropertyName("pointerShape"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PanePointerShape? PointerShape { get; init; }
    [JsonPropertyName("viewport"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PaneViewportMetadata? Viewport { get; init; }
}

/// <summary>Bounded direct-view pointer facts, observed on the UI dispatcher.</summary>
public sealed record PanePointerShape
{
    [JsonPropertyName("requested")] public string? Requested { get; init; }
    [JsonPropertyName("controlCursor")] public required string ControlCursor { get; init; }
    [JsonPropertyName("linkOverride")] public required bool LinkOverride { get; init; }
}

/// <summary>The actual view scroll position, read from the terminal view; independent of parser history and not a pixel capture.</summary>
public sealed record PaneViewportMetadata
{
    /// <summary>Rows the viewport sits above the live line; 0 means the live (bottom) view.</summary>
    [JsonPropertyName("scrollOffset")] public required int ScrollOffset { get; init; }

    /// <summary>Greatest reachable <see cref="ScrollOffset"/> for the current history (total lines minus view rows).</summary>
    [JsonPropertyName("maxScrollOffset")] public required int MaxScrollOffset { get; init; }
}

/// <summary>One complete active viewport's stored links and raw shell anchors, captured under the buffer read lock.</summary>
public sealed record PaneBufferMetadata
{
    /// <summary>Parser-retained icon name, not a host icon or displayed label. Omitted when unavailable.</summary>
    [JsonPropertyName("requestedIconName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestedIconName { get; init; }
    [JsonPropertyName("rows")] public required int Rows { get; init; }
    [JsonPropertyName("cols")] public required int Cols { get; init; }
    [JsonPropertyName("cursorRow")] public required int CursorRow { get; init; }
    [JsonPropertyName("cursorCol")] public required int CursorCol { get; init; }
    [JsonPropertyName("alternateScreen")] public required bool AlternateScreen { get; init; }
    /// <summary>Retained main-history count, including while the alternate screen is active.</summary>
    [JsonPropertyName("scrollbackLines")] public required int ScrollbackLines { get; init; }
    [JsonPropertyName("historyGeneration")] public required long HistoryGeneration { get; init; }
    [JsonPropertyName("rowsEvicted")] public required long RowsEvicted { get; init; }
    [JsonPropertyName("acceptingCommandInput")] public required bool AcceptingCommandInput { get; init; }
    [JsonPropertyName("commandStart")] public PaneShellMark? CommandStart { get; init; }
    [JsonPropertyName("outputStart")] public PaneShellMark? OutputStart { get; init; }
    [JsonPropertyName("links")] public required PaneHyperlinkRun[] Links { get; init; }
    /// <summary>
    /// Parser-retained OSC 3008 contexts: untrusted descriptive data written by whatever controls the terminal. Omitted when the parser
    /// is unavailable; present, possibly empty, otherwise. It is not counted against the metadata text budget (the parser bounds it
    /// itself) so a program cannot make the rest of the observation unavailable by filling it.
    /// </summary>
    [JsonPropertyName("contexts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PaneContextMetadata? Contexts { get; init; }
}

/// <summary>
/// Stored OSC 3008 context metadata for one pane. Every string is supplied by the program writing to the terminal: treat it as
/// data to display, never as a command, path to open, identity to trust, or instruction to an agent. It is unrelated to OSC 133/633
/// shell integration and to the OSC 7 working directory.
/// </summary>
public sealed record PaneContextMetadata
{
    /// <summary>Always true: a structural reminder that no value in this object is verified or authoritative.</summary>
    [JsonPropertyName("untrusted")] public bool Untrusted { get; init; } = true;
    /// <summary>Open contexts, outermost first; the last entry is the active context.</summary>
    [JsonPropertyName("stack")] public required PaneContext[] Stack { get; init; }
    /// <summary>Recently ended contexts, oldest first.</summary>
    [JsonPropertyName("ended")] public required PaneEndedContext[] Ended { get; init; }
}

/// <summary>One open context; a null field was absent or invalid in the sequence. Numbers are decimal digit strings.</summary>
public sealed record PaneContext
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("type"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Type { get; init; }
    [JsonPropertyName("user"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? User { get; init; }
    [JsonPropertyName("hostname"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Hostname { get; init; }
    [JsonPropertyName("machineId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? MachineId { get; init; }
    [JsonPropertyName("bootId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? BootId { get; init; }
    [JsonPropertyName("pid"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Pid { get; init; }
    [JsonPropertyName("pidFdId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PidFdId { get; init; }
    [JsonPropertyName("comm"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Comm { get; init; }
    [JsonPropertyName("cwd"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Cwd { get; init; }
    [JsonPropertyName("cmdline"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? CmdLine { get; init; }
    [JsonPropertyName("vm"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Vm { get; init; }
    [JsonPropertyName("container"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Container { get; init; }
    [JsonPropertyName("targetUser"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? TargetUser { get; init; }
    [JsonPropertyName("targetHost"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? TargetHost { get; init; }
    [JsonPropertyName("sessionId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SessionId { get; init; }
}

/// <summary>One explicitly ended context. <c>Status</c> is the sender's claim, not the child process exit code.</summary>
public sealed record PaneEndedContext
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("type"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Type { get; init; }
    [JsonPropertyName("exit"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Exit { get; init; }
    [JsonPropertyName("status"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Status { get; init; }
    [JsonPropertyName("signal"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Signal { get; init; }
}

/// <summary>Raw tracked anchor; consumers must check generation, screen and retained history before resolving it.</summary>
public sealed record PaneShellMark
{
    [JsonPropertyName("row")] public required int Row { get; init; }
    [JsonPropertyName("col")] public required int Col { get; init; }
    [JsonPropertyName("absoluteRow")] public required long AbsoluteRow { get; init; }
    [JsonPropertyName("generation")] public required long Generation { get; init; }
    [JsonPropertyName("alternateScreen")] public required bool AlternateScreen { get; init; }
}

/// <summary>Zero-based viewport run; identity is snapshot-local reference identity, not a persistent id or a clickable action.</summary>
public sealed record PaneHyperlinkRun
{
    [JsonPropertyName("row")] public required int Row { get; init; }
    [JsonPropertyName("col")] public required int Col { get; init; }
    [JsonPropertyName("length")] public required int Length { get; init; }
    [JsonPropertyName("identity")] public required int Identity { get; init; }
    [JsonPropertyName("uri")] public required string Uri { get; init; }
    [JsonPropertyName("id")] public string? Id { get; init; }
}

/// <summary>Actual UI control properties and effective host settings; no OS taskbar or painted-pixel assertion.</summary>
public sealed record PanePresentationMetadata
{
    [JsonPropertyName("windowActive")] public required bool WindowActive { get; init; }
    [JsonPropertyName("windowVisible")] public required bool WindowVisible { get; init; }
    [JsonPropertyName("paneActive")] public required bool PaneActive { get; init; }
    [JsonPropertyName("focusMode")] public required bool FocusMode { get; init; }
    [JsonPropertyName("notificationsEnabled")] public required bool NotificationsEnabled { get; init; }
    [JsonPropertyName("progressEnabled")] public required bool ProgressEnabled { get; init; }
    [JsonPropertyName("tabText")] public required string TabText { get; init; }
    [JsonPropertyName("tabTextVisible")] public required bool TabTextVisible { get; init; }
    [JsonPropertyName("progressOwner")] public Guid? ProgressOwner { get; init; }
    [JsonPropertyName("progressKind")] public string? ProgressKind { get; init; }
    [JsonPropertyName("progressPercent")] public byte? ProgressPercent { get; init; }
    [JsonPropertyName("progressBar")] public PaneProgressBarMetadata? ProgressBar { get; init; }
    /// <summary>Window-global toast, not attributed to this pane and not an OS notification receipt.</summary>
    [JsonPropertyName("toastVisible")] public required bool ToastVisible { get; init; }
    [JsonPropertyName("toastTitle")] public required string ToastTitle { get; init; }
    [JsonPropertyName("toastMessage")] public required string ToastMessage { get; init; }
}

/// <summary>Present only when an actual vertical-tab progress control exists.</summary>
public sealed record PaneProgressBarMetadata
{
    [JsonPropertyName("visible")] public required bool Visible { get; init; }
    [JsonPropertyName("indeterminate")] public required bool Indeterminate { get; init; }
    [JsonPropertyName("value")] public required double Value { get; init; }
}
