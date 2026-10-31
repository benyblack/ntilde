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

    internal TerminalColorOverrides Clone() => new()
    {
        Foreground = Foreground,
        Background = Background,
        Cursor = Cursor,
        SelectionBackground = SelectionBackground,
        SelectionForeground = SelectionForeground,
        Palette = (uint?[])Palette.Clone()
    };
}

/// <summary>Session-wide color overrides and a bounded oldest-to-newest save stack.</summary>
public sealed class TerminalRuntimeColors
{
    public const int MaxStackDepth = 16;
    [JsonPropertyName("current")] public TerminalColorOverrides Current { get; set; } = new();
    [JsonPropertyName("stack")] public TerminalColorOverrides[] Stack { get; set; } = [];

    internal TerminalRuntimeColors Clone() => new()
    {
        Current = Current.Clone(),
        Stack = Stack.Select(frame => frame.Clone()).ToArray()
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
    }

    private static void ValidateFrame(TerminalColorOverrides frame)
    {
        StateTransferValidation.RequirePresent(frame.Palette, "runtime palette");
        if (frame.Palette.Length != 256)
            throw StateTransferValidation.Reject("runtime palette", "must contain exactly 256 entries");
    }
}
