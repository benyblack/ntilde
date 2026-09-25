using System.Text.Json.Serialization;

namespace Ntilde.Mux.Contracts;

/// <summary>
/// Source-generated JSON context for every control-frame type. Both ends serialize exclusively
/// through it: reflection-free (Native AOT) and one wire shape.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(MuxRequest))]
[JsonSerializable(typeof(MuxResponse))]
[JsonSerializable(typeof(MuxNotification))]
[JsonSerializable(typeof(MuxError))]
[JsonSerializable(typeof(MuxEmpty))]
[JsonSerializable(typeof(HelloParams))]
[JsonSerializable(typeof(WelcomeResult))]
[JsonSerializable(typeof(MuxPresentation))]
[JsonSerializable(typeof(SpawnParams))]
[JsonSerializable(typeof(SpawnResult))]
[JsonSerializable(typeof(SessionSummary))]
[JsonSerializable(typeof(ListSessionsResult))]
[JsonSerializable(typeof(AttachParams))]
[JsonSerializable(typeof(SessionIdParams))]
[JsonSerializable(typeof(DetachParams))]
[JsonSerializable(typeof(ResizeParams))]
[JsonSerializable(typeof(SessionInfoResult))]
[JsonSerializable(typeof(StartRecordingParams))]
[JsonSerializable(typeof(EnableFlightRecordingParams))]
[JsonSerializable(typeof(ExportFlightResult))]
[JsonSerializable(typeof(ExitedNotification))]
[JsonSerializable(typeof(FaultedNotification))]
[JsonSerializable(typeof(MuxEndpointDescriptor))]
public sealed partial class MuxJsonContext : JsonSerializerContext
{
}
