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
}

/// <summary>One complete active viewport's stored links and raw shell anchors, captured under the buffer read lock.</summary>
public sealed record PaneBufferMetadata
{
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
