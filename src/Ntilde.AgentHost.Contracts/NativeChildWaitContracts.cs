using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Ntilde.AgentHost.Contracts;

/// <summary>Private startup-QA observation. The first returned generation must be pinned by the caller.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReadNativeChildWaitParams
{
    [JsonPropertyName("paneId")] public Guid PaneId { get; init; }
    [JsonPropertyName("runId")] public string RunId { get; init; } = "";
    [JsonPropertyName("expectedChildPid")] public int ExpectedChildPid { get; init; }
    [JsonPropertyName("expectedNativeSessionId")] public Guid? ExpectedNativeSessionId { get; init; }
}

/// <summary>A genuine App-owned Unix wait fact, not process/PTY ownership or overall cleanup completion.</summary>
public sealed record NativeChildWaitResult
{
    public const string UnixSource = "unix-native-child-try-wait";
    [JsonPropertyName("version")] public int Version { get; init; } = 1;
    [JsonPropertyName("paneId")] public Guid PaneId { get; init; }
    [JsonPropertyName("nativeSessionId")] public Guid NativeSessionId { get; init; }
    [JsonPropertyName("originalChildPid")] public int OriginalChildPid { get; init; }
    [JsonPropertyName("source")] public string Source { get; init; } = UnixSource;
    /// <summary>False means unknown; it does not prove the process is running.</summary>
    [JsonPropertyName("nativeWaitObserved")] public bool NativeWaitObserved { get; init; }
    [JsonPropertyName("exitCode"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int? ExitCode { get; init; }
}

public static class NativeChildWaitValidation
{
    public static bool IsValid([NotNullWhen(true)] ReadNativeChildWaitParams? value) => value != null && value.PaneId != Guid.Empty
        && ClipboardFixtureValidation.IsRunId(value.RunId) && value.ExpectedChildPid > 0
        && value.ExpectedNativeSessionId != Guid.Empty;

    public static bool IsCoherent(NativeChildWaitResult value) => value.Version == 1
        && value.PaneId != Guid.Empty && value.NativeSessionId != Guid.Empty && value.OriginalChildPid > 0
        && value.Source == NativeChildWaitResult.UnixSource && value.NativeWaitObserved == value.ExitCode.HasValue;
}
