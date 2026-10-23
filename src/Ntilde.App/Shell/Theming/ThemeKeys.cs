using System.Collections.Generic;

namespace Ntilde.Shell.Theming;

/// <summary>
/// Resource keys of every UI color token. XAML references them as
/// <c>{DynamicResource NtX}</c>. C# uses these constants: the architecture guard rejects raw
/// <c>"Nt…"</c> strings outside <c>Shell/Theming</c>, and rejects any <c>Nt…</c> key in XAML that
/// is not listed here. The values come from <see cref="ThemeTokens.Derive"/> and are installed
/// app-wide by <see cref="ThemeService"/>.
/// </summary>
public static class ThemeKeys
{
    // Surfaces
    public const string WindowBg = "NtWindowBg";
    public const string ChromeBg = "NtChromeBg";
    public const string SidebarBg = "NtSidebarBg";
    public const string Panel = "NtPanel";
    public const string PanelAlt = "NtPanelAlt";
    public const string Overlay = "NtOverlay";
    public const string Scrim = "NtScrim";

    // Lines
    public const string Hairline = "NtHairline";
    public const string HairlineStrong = "NtHairlineStrong";
    public const string SidebarDivider = "NtSidebarDivider";
    public const string GripHover = "NtGripHover";

    // Text
    public const string Fg = "NtFg";
    public const string Fg2 = "NtFg2";
    public const string Fg3 = "NtFg3";
    public const string Fg4 = "NtFg4";

    // Interaction fills (foreground at low alpha, so they show on light and dark themes alike)
    public const string HoverFill = "NtHoverFill";
    public const string PressedFill = "NtPressedFill";
    public const string SelectedFill = "NtSelectedFill";

    // Accent
    public const string Accent = "NtAccent";
    public const string AccentHover = "NtAccentHover";
    public const string AccentPressed = "NtAccentPressed";
    public const string AccentFg = "NtAccentFg";
    public const string AccentDim = "NtAccentDim";
    public const string AccentFaint = "NtAccentFaint";
    public const string AccentBorder = "NtAccentBorder";

    // Status: each with a tinted background (Dim) and a border
    public const string Success = "NtSuccess";
    public const string SuccessDim = "NtSuccessDim";
    public const string SuccessBorder = "NtSuccessBorder";
    public const string Warning = "NtWarning";
    public const string WarningDim = "NtWarningDim";
    public const string WarningBorder = "NtWarningBorder";
    public const string Error = "NtError";
    public const string ErrorDim = "NtErrorDim";
    public const string ErrorBorder = "NtErrorBorder";
    public const string Info = "NtInfo";
    public const string InfoDim = "NtInfoDim";
    public const string InfoBorder = "NtInfoBorder";
    public const string Attention = "NtAttention";
    public const string AttentionDim = "NtAttentionDim";
    public const string AttentionBorder = "NtAttentionBorder";

    // Semantic aliases: separate keys so a surface's meaning survives a mapping change
    public const string AgentWrote = "NtAgentWrote";
    public const string AgentWatched = "NtAgentWatched";
    public const string AgentIdle = "NtAgentIdle";
    public const string DiffAdded = "NtDiffAdded";
    public const string DiffRemoved = "NtDiffRemoved";
    public const string DiffHunk = "NtDiffHunk";
    public const string Recording = "NtRecording";
    public const string Link = "NtLink";
    public const string LinkHover = "NtLinkHover";

    // Tab strip (both orientations) and the vertical sidebar
    public const string TabFg = "NtTabFg";
    public const string TabFgSelected = "NtTabFgSelected";
    public const string TabHoverFill = "NtTabHoverFill";
    public const string TabSelectedFill = "NtTabSelectedFill";
    public const string TabIndicator = "NtTabIndicator";
    public const string TabPreviewFg = "NtTabPreviewFg";
    public const string TabWorking = "NtTabWorking";
    public const string TabAttention = "NtTabAttention";
    public const string TabAgentWrote = "NtTabAgentWrote";
    public const string TabAgentWatched = "NtTabAgentWatched";
    public const string TabChipActivity = "NtTabChipActivity";
    public const string TabChipAttention = "NtTabChipAttention";
    public const string TabChipAgentWrote = "NtTabChipAgentWrote";
    public const string TabChipAgentWatched = "NtTabChipAgentWatched";

    public static IReadOnlyList<string> All { get; } =
    [
        WindowBg, ChromeBg, SidebarBg, Panel, PanelAlt, Overlay, Scrim,
        Hairline, HairlineStrong, SidebarDivider, GripHover,
        Fg, Fg2, Fg3, Fg4,
        HoverFill, PressedFill, SelectedFill,
        Accent, AccentHover, AccentPressed, AccentFg, AccentDim, AccentFaint, AccentBorder,
        Success, SuccessDim, SuccessBorder, Warning, WarningDim, WarningBorder,
        Error, ErrorDim, ErrorBorder, Info, InfoDim, InfoBorder,
        Attention, AttentionDim, AttentionBorder,
        AgentWrote, AgentWatched, AgentIdle, DiffAdded, DiffRemoved, DiffHunk, Recording, Link, LinkHover,
        TabFg, TabFgSelected, TabHoverFill, TabSelectedFill, TabIndicator, TabPreviewFg,
        TabWorking, TabAttention, TabAgentWrote, TabAgentWatched,
        TabChipActivity, TabChipAttention, TabChipAgentWrote, TabChipAgentWatched,
    ];
}
