using System.Text.Json.Serialization;

namespace Ntilde.AgentHost.Contracts;

/// <summary>One declared synthetic framework event. Requires act permission; not a native-input API.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GeneratePaneInputParams
{
    [JsonPropertyName("paneId")]
    public required Guid PaneId { get; init; }
    /// <summary>keyDown, keyUp, text, mousePress, mouseRelease, mouseMove or mouseWheel.</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }
    /// <summary>Keypad 0–9, decimal, separator, add, subtract, multiply, divide or enter.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }
    /// <summary>Exactly one keypad character for a separate routed TextInput event.</summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }
    /// <summary>Bit mask: shift=1, control=2, alt=4, meta=8. Text events require zero.</summary>
    [JsonPropertyName("modifiers")]
    public int Modifiers { get; init; }
    /// <summary>One-based viewport cell; must also fit the actual current view and buffer.</summary>
    [JsonPropertyName("column")]
    public int? Column { get; init; }
    [JsonPropertyName("row")]
    public int? Row { get; init; }
    /// <summary>left, middle, right; move additionally accepts none. Wheel has no held button.</summary>
    [JsonPropertyName("button")]
    public string? Button { get; init; }
    /// <summary>Exactly +1 or -1 for one vertical wheel notch; absent for other events.</summary>
    [JsonPropertyName("wheelDelta")]
    public int? WheelDelta { get; init; }
}

/// <summary>App dispatch/observation facts only. Does not attest child delivery, native input or fixture ownership.</summary>
public sealed record GeneratePaneInputResult
{
    [JsonPropertyName("eventDispatched")]
    public required bool EventDispatched { get; init; }
    /// <summary>A normal session-input call returned, or byte input was accepted; not child delivery.</summary>
    [JsonPropertyName("inputSubmitted")]
    public bool InputSubmitted { get; init; }
    [JsonPropertyName("handled")]
    public required bool Handled { get; init; }
    [JsonPropertyName("boundary")]
    public string Boundary { get; init; } = "avalonia-routed";
    [JsonPropertyName("rows")]
    public required int Rows { get; init; }
    [JsonPropertyName("cols")]
    public required int Cols { get; init; }
    [JsonPropertyName("viewRows")]
    public required int ViewRows { get; init; }
    [JsonPropertyName("viewCols")]
    public required int ViewCols { get; init; }
    [JsonPropertyName("column")]
    public int? Column { get; init; }
    [JsonPropertyName("row")]
    public int? Row { get; init; }
    /// <summary>Actual routed view-local DIP position, at the requested cell center.</summary>
    [JsonPropertyName("x")]
    public double? X { get; init; }
    [JsonPropertyName("y")]
    public double? Y { get; init; }
    /// <summary>Observed framework event modifiers, using the request bit mask.</summary>
    [JsonPropertyName("modifiers")]
    public required int Modifiers { get; init; }
    /// <summary>Post-dispatch parser mode samples, not atomic encoder or child-delivery attestations.</summary>
    [JsonPropertyName("applicationKeypad")]
    public required bool ApplicationKeypad { get; init; }
    [JsonPropertyName("mouseTracking")]
    public required int MouseTracking { get; init; }
    [JsonPropertyName("mouseSgr")]
    public required bool MouseSgr { get; init; }
    [JsonPropertyName("kittyKeyboardFlags")]
    public required int KittyKeyboardFlags { get; init; }
}

/// <summary>Shared BCL-only bounds, enforced before dispatch by both endpoint and App executor.</summary>
public static class GeneratePaneInputValidation
{
    public static bool IsValid(GeneratePaneInputParams p)
    {
        if (p.PaneId == Guid.Empty || p.Modifiers < 0 || p.Modifiers > 15) return false;
        return p.Kind switch
        {
            "keyDown" or "keyUp" => HasNoPointerFields(p) && p.Text == null && IsKey(p.Key),
            "text" => IsTextInput(p),
            _ => IsMouseInput(p),
        };
    }

    private static bool HasNoPointerFields(GeneratePaneInputParams p)
        => p.Column == null && p.Row == null && p.Button == null && p.WheelDelta == null;

    private static bool IsTextInput(GeneratePaneInputParams p)
        => HasNoPointerFields(p) && p.Key == null && p.Modifiers == 0 && p.Text is { Length: 1 }
            && "0123456789.+-*/,=\r".Contains(p.Text, StringComparison.Ordinal);

    private static bool IsMouseInput(GeneratePaneInputParams p)
    {
        if (p.Key != null || p.Text != null || p.Column is not (>= 1 and <= 256) || p.Row is not (>= 1 and <= 128)) return false;
        return p.Kind switch
        {
            "mousePress" or "mouseRelease" => IsButton(p.Button) && p.WheelDelta == null,
            "mouseMove" => (p.Button == "none" || IsButton(p.Button)) && p.WheelDelta == null,
            "mouseWheel" => p.Button == null && p.WheelDelta is -1 or 1,
            _ => false,
        };
    }

    private static bool IsButton(string? button) => button is "left" or "middle" or "right";
    private static bool IsKey(string? key) => key is "0" or "1" or "2" or "3" or "4" or "5" or "6" or "7" or "8" or "9"
        or "decimal" or "separator" or "add" or "subtract" or "multiply" or "divide" or "enter";
}
