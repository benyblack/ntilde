using System;
using System.Linq;
using System.Text.Json.Serialization;

namespace Ntilde.VT;

/// <summary>Stream-owned overrides. Null means follow the receiving host's current theme.</summary>
public sealed class TerminalColorOverrides
{
    [JsonPropertyName("fg")] public uint? Foreground { get; set; }
    [JsonPropertyName("bg")] public uint? Background { get; set; }
    [JsonPropertyName("cursor")] public uint? Cursor { get; set; }
    [JsonPropertyName("selection_bg")] public uint? SelectionBackground { get; set; }
    [JsonPropertyName("selection_fg")] public uint? SelectionForeground { get; set; }
    [JsonPropertyName("palette")] public uint?[] Palette { get; set; } = new uint?[256];

    /// <summary>
    /// rxvt's five special colors (OSC 5): bold, underline, blink, reverse and italic. They are a distinct
    /// resource set from the palette and the dynamic colors: an unset slot follows the default foreground,
    /// and the App renders attribute emphasis as flags, so a stored special color is never render-applied.
    /// </summary>
    public const int SpecialColorCount = 5;

    [JsonPropertyName("special")] public uint?[] SpecialColors { get; set; } = new uint?[SpecialColorCount];

    internal bool HasSameColors(TerminalColorOverrides other) =>
        Foreground == other.Foreground && Background == other.Background && Cursor == other.Cursor &&
        SelectionBackground == other.SelectionBackground && SelectionForeground == other.SelectionForeground &&
        Palette.AsSpan().SequenceEqual(other.Palette) && SpecialColors.AsSpan().SequenceEqual(other.SpecialColors);

    internal TerminalColorOverrides Clone() => new()
    {
        Foreground = Foreground,
        Background = Background,
        Cursor = Cursor,
        SelectionBackground = SelectionBackground,
        SelectionForeground = SelectionForeground,
        Palette = (uint?[])Palette.Clone(),
        SpecialColors = (uint?[])SpecialColors.Clone()
    };
}

/// <summary>Session-wide color overrides and a bounded oldest-to-newest save stack.</summary>
public sealed class TerminalRuntimeColors
{
    public const int MaxStackDepth = 16;

    /// <summary>xterm's palette stack has ten slots (<c>MAX_SAVED_SGR</c>).</summary>
    public const int MaxXtermSlots = 10;

    [JsonPropertyName("current")] public TerminalColorOverrides Current { get; set; } = new();
    [JsonPropertyName("stack")] public TerminalColorOverrides[] Stack { get; set; } = [];

    /// <summary>
    /// The xterm palette stack slots (XTPUSHCOLORS / XTPOPCOLORS), independent of <see cref="Stack"/>: null before the
    /// first push, otherwise exactly <see cref="MaxXtermSlots"/> entries, a null one never stored.
    /// </summary>
    [JsonPropertyName("xt_slots"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TerminalColorOverrides?[]? XtermSlots { get; set; }

    /// <summary>xterm's <c>used</c>: the current depth XTREPORTCOLORS reports first.</summary>
    [JsonPropertyName("xt_used"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int XtermUsed { get; set; }

    /// <summary>xterm's <c>last</c>: the deepest the stack has been pushed, reported second.</summary>
    [JsonPropertyName("xt_last"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int XtermLast { get; set; }

    internal bool HasXtermState => XtermSlots is not null || XtermUsed != 0 || XtermLast != 0;


    internal TerminalRuntimeColors Clone() => new()
    {
        Current = Current.Clone(),
        Stack = Stack.Select(frame => frame.Clone()).ToArray(),
        XtermSlots = XtermSlots?.Select(frame => frame?.Clone()).ToArray(),
        XtermUsed = XtermUsed,
        XtermLast = XtermLast
    };

    /// <summary>A published copy with another current frame and kitty stack, keeping the xterm palette stack.</summary>
    internal TerminalRuntimeColors WithColors(TerminalColorOverrides current, TerminalColorOverrides[] stack) => new()
    {
        Current = current,
        Stack = stack,
        XtermSlots = XtermSlots,
        XtermUsed = XtermUsed,
        XtermLast = XtermLast
    };

    internal static void Validate(TerminalRuntimeColors? state)
    {
        if (state is null) return; // Legacy snapshots carry no overrides.
        StateTransferValidation.RequirePresent(state.Current, "runtime colors");
        StateTransferValidation.RequirePresent(state.Stack, "color stack");
        if (state.Stack.Length > MaxStackDepth)
            throw StateTransferValidation.Reject("color stack", "exceeds the live depth limit");
        ValidateFrame(state.Current);
        foreach (TerminalColorOverrides frame in state.Stack)
            ValidateFrame(StateTransferValidation.RequirePresent(frame, "color stack frame"));
        ValidateXtermState(state);
    }

    private static void ValidateXtermState(TerminalRuntimeColors state)
    {
        if (state.XtermSlots is { } slots)
        {
            if (slots.Length != MaxXtermSlots)
                throw StateTransferValidation.Reject("xterm color stack", "must contain exactly 10 slots");
            foreach (TerminalColorOverrides stored in slots.OfType<TerminalColorOverrides>()) ValidateFrame(stored);
        }
        if (state.XtermUsed is < 0 or > MaxXtermSlots || state.XtermLast is < 0 or > MaxXtermSlots)
            throw StateTransferValidation.Reject("xterm color stack", "depth is outside the ten slots");
        if (state.XtermSlots is null && state.HasXtermState)
            throw StateTransferValidation.Reject("xterm color stack", "has a depth without its slots");
    }

    private static void ValidateFrame(TerminalColorOverrides frame)
    {
        StateTransferValidation.RequirePresent(frame.Palette, "runtime palette");
        if (frame.Palette.Length != 256)
            throw StateTransferValidation.Reject("runtime palette", "must contain exactly 256 entries");
        StateTransferValidation.RequirePresent(frame.SpecialColors, "runtime special colors");
        if (frame.SpecialColors.Length != TerminalColorOverrides.SpecialColorCount)
            throw StateTransferValidation.Reject("runtime special colors", $"must contain exactly {TerminalColorOverrides.SpecialColorCount} entries");
    }
}
