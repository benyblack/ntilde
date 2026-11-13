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
    /// <summary>Keypad 0–9, decimal, separator, add, subtract, multiply, divide or enter; or one bounded ordinary main-block key.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }
    /// <summary>
    /// Exactly one keypad character for a routed TextInput event. On <c>text</c> it is the whole event. On <c>keyDown</c> it is a second
    /// routed TextInput raised right after that key-down within the same synchronous dispatch, the text some backends emit for a key
    /// the terminal already handled. For a keypad key it must be that key's own character with zero modifiers; for an ordinary key it
    /// must be exactly the US-layout character the key produces with the declared (zero or Shift-only) modifiers, so a single declared
    /// action can exercise the handled-key duplicate guard or the unhandled-key text path without fabricating a correlation.
    /// </summary>
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
    /// <summary>Whether the follow-up TextInput of a <c>keyDown</c> with text was marked handled; null when the action carried no such text.</summary>
    [JsonPropertyName("textHandled")]
    public bool? TextHandled { get; init; }
    [JsonPropertyName("boundary")]
    public string Boundary { get; init; } = "avalonia-routed";
    /// <summary>Post-dispatch geometry/modes were safely observed for the same target; false leaves them null.</summary>
    [JsonPropertyName("observationAvailable")]
    public bool ObservationAvailable { get; init; }
    [JsonPropertyName("rows")]
    public int? Rows { get; init; }
    [JsonPropertyName("cols")]
    public int? Cols { get; init; }
    [JsonPropertyName("viewRows")]
    public int? ViewRows { get; init; }
    [JsonPropertyName("viewCols")]
    public int? ViewCols { get; init; }
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
    /// <summary>Post-dispatch effective modifyOtherKeys level (0–2) of the active screen, or null when it was not observed.</summary>
    [JsonPropertyName("modifyOtherKeysLevel")]
    public int? ModifyOtherKeysLevel { get; init; }
    /// <summary>Post-dispatch parser mode samples, not atomic encoder or child-delivery attestations.</summary>
    [JsonPropertyName("applicationKeypad")]
    public bool? ApplicationKeypad { get; init; }
    [JsonPropertyName("mouseTracking")]
    public int? MouseTracking { get; init; }
    [JsonPropertyName("mouseSgr")]
    public bool? MouseSgr { get; init; }
    [JsonPropertyName("kittyKeyboardFlags")]
    public int? KittyKeyboardFlags { get; init; }
}

/// <summary>Shared BCL-only bounds, enforced before dispatch by both endpoint and App executor.</summary>
public static class GeneratePaneInputValidation
{
    public static bool IsValid(GeneratePaneInputParams p)
    {
        if (p.PaneId == Guid.Empty || p.Modifiers < 0 || p.Modifiers > 15) return false;
        return p.Kind switch
        {
            "keyDown" => HasNoPointerFields(p) && IsKey(p.Key) && (p.Text == null || IsValidPairedText(p)),
            "keyUp" => HasNoPointerFields(p) && p.Text == null && IsKey(p.Key),
            "text" => IsTextInput(p),
            _ => IsMouseInput(p),
        };
    }

    private static bool HasNoPointerFields(GeneratePaneInputParams p)
        => p.Column == null && p.Row == null && p.Button == null && p.WheelDelta == null;

    private static bool IsTextInput(GeneratePaneInputParams p)
        => HasNoPointerFields(p) && p.Key == null && p.Modifiers == 0 && IsKeypadText(p.Text);

    private static bool IsKeypadText(string? text)
        => text is { Length: 1 } && "0123456789.+-*/,=\r".Contains(text, StringComparison.Ordinal);

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

    private static bool IsKey(string? key) => IsKeypadKey(key) || IsOrdinaryKey(key);

    private static bool IsKeypadKey(string? key) => key is "0" or "1" or "2" or "3" or "4" or "5" or "6" or "7" or "8" or "9"
        or "decimal" or "separator" or "add" or "subtract" or "multiply" or "divide" or "enter";

    /// <summary>
    /// Ordinary main-block keys the fixture accepts, named by their US-layout position: letters <c>a</c>–<c>z</c>,
    /// digits <c>digit0</c>–<c>digit9</c> (main row, distinct from the keypad <c>0</c>–<c>9</c>), punctuation
    /// (<c>grave</c> <c>minus</c> <c>equal</c> <c>leftBracket</c> <c>rightBracket</c> <c>backslash</c>
    /// <c>semicolon</c> <c>apostrophe</c> <c>comma</c> <c>period</c> <c>slash</c>) and
    /// <c>tab</c> <c>backspace</c> <c>escape</c> <c>space</c> <c>return</c>. Modifiers are declared facts;
    /// <c>meta</c> and <c>control+alt</c> chords are refused with text so no declared event can fabricate an AltGr or Cmd correlation.
    /// </summary>
    private static bool IsOrdinaryKey(string? key) => key is { Length: 1 } && key[0] is >= 'a' and <= 'z'
        || key is "digit0" or "digit1" or "digit2" or "digit3" or "digit4" or "digit5" or "digit6" or "digit7" or "digit8" or "digit9"
        or "grave" or "minus" or "equal" or "leftBracket" or "rightBracket" or "backslash" or "semicolon" or "apostrophe"
        or "comma" or "period" or "slash" or "tab" or "backspace" or "escape" or "space" or "return";

    /// <summary>
    /// A key-down's follow-up text must be a valid correlation: the keypad key's own character with zero modifiers, or the ordinary
    /// key's exact US-layout character with zero or Shift-only modifiers. Ctrl, Alt, Meta and Ctrl+Alt chords are refused a text so a
    /// declared pair can never masquerade as a backend commit those keys do not produce.
    /// </summary>
    private static bool IsValidPairedText(GeneratePaneInputParams p)
    {
        if (IsKeypadKey(p.Key)) return p.Modifiers == 0 && IsKeypadText(p.Text);
        return IsOrdinaryKey(p.Key) && p.Modifiers is 0 or 1 && OrdinaryPairedText(p.Key, (p.Modifiers & 1) != 0) == p.Text;
    }

    /// <summary>The single US-layout character the ordinary key produces with that shift state, or null when it composes none.</summary>
    private static string? OrdinaryPairedText(string? key, bool shift) => key switch
    {
        "space" => " ",
        "grave" => shift ? "~" : "`",
        "minus" => shift ? "_" : "-",
        "equal" => shift ? "+" : "=",
        "leftBracket" => shift ? "{" : "[",
        "rightBracket" => shift ? "}" : "]",
        "backslash" => shift ? "|" : "\\",
        "semicolon" => shift ? ":" : ";",
        "apostrophe" => shift ? "\"" : "'",
        "comma" => shift ? "<" : ",",
        "period" => shift ? ">" : ".",
        "slash" => shift ? "?" : "/",
        _ when key is { Length: 1 } && key[0] is >= 'a' and <= 'z' => shift
            ? char.ToUpperInvariant(key[0]).ToString()
            : key,
        _ when key is ['d', 'i', 'g', 'i', 't', ..] && key.Length == 6 && key[5] is >= '0' and <= '9' => shift
            ? ")!@#$%^&*("[key[5] - '0'].ToString()
            : key[5].ToString(),
        _ => null,
    };
}
