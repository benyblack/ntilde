using System.Text.Json.Serialization;

namespace Ntilde.VT;

/// <summary>One bounded OSC 66 block; its occupied cells are reconstructed on restore.</summary>
public sealed class TerminalTextSizingState
{
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    [JsonPropertyName("options")] public TextSizingOptions Options { get; set; }
    [JsonPropertyName("width")] public int BaseWidth { get; set; }
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("alt")] public bool Alt { get; set; }
}
