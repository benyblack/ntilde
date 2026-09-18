using System.Text.Json.Serialization;

namespace Ntilde.Inference;

/// <summary>Source-generated context: the app publishes NativeAOT, so no reflection serialization.</summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(SystemOneRequest))]
[JsonSerializable(typeof(SystemOneResponse))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, SystemOneQuestion>))]
[JsonSerializable(typeof(Dictionary<string, SystemOneAnswer>))]
[JsonSerializable(typeof(Dictionary<string, double>))]
public sealed partial class InferenceJsonContext : JsonSerializerContext
{
}
