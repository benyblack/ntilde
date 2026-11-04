using System.Text.Json.Serialization;

namespace Ntilde.AgentHost.Contracts;

/// <summary>Private QA storage only. This API never reads or writes the system clipboard.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ClipboardFixtureParams
{
    [JsonPropertyName("paneId")]
    public Guid PaneId { get; init; }
    [JsonPropertyName("runId")]
    public string RunId { get; init; } = "";
    [JsonPropertyName("operation")]
    public string Operation { get; init; } = "";
    [JsonPropertyName("fixtureId")]
    public Guid? FixtureId { get; init; }
    [JsonPropertyName("value")]
    public string? Value { get; init; }
}

/// <summary>App storage observations, not proof of process/PTY ownership or native interchange.</summary>
public sealed record ClipboardFixtureResult
{
    [JsonPropertyName("fixtureId")]
    public Guid FixtureId { get; init; }
    [JsonPropertyName("state")]
    public string State { get; init; } = "";
    [JsonPropertyName("value")]
    public string? Value { get; init; }
    [JsonPropertyName("acceptedWrites")]
    public int AcceptedWrites { get; init; }
    [JsonPropertyName("restored")]
    public bool Restored { get; init; }
    [JsonPropertyName("complete")]
    public bool Complete { get; init; }
}

public static class ClipboardFixtureValidation
{
    public static bool IsNonce(string? value) => value is { Length: 51 }
        && value.StartsWith("terminfo-osc52-", StringComparison.Ordinal)
        && Guid.TryParseExact(value.AsSpan(15), "D", out var nonce)
        && value.AsSpan(15).SequenceEqual(nonce.ToString("D"));

    public static bool IsRunId(string? value) => value is { Length: 32 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool IsValid(ClipboardFixtureParams input)
    {
        if (input.PaneId == Guid.Empty || !IsRunId(input.RunId)) return false;
        return input.Operation switch
        {
            "begin" => input.FixtureId == null && input.Value == null,
            "armWrite" or "seed" => input.FixtureId is { } id && id != Guid.Empty && IsNonce(input.Value),
            "read" or "close" => input.FixtureId is { } id && id != Guid.Empty && input.Value == null,
            _ => false,
        };
    }
}
