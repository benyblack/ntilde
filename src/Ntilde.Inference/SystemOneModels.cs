using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ntilde.Inference;

/// <summary>Body of <c>POST /v1/systemone</c>. See https://docs.typesafe.ai/api.</summary>
public sealed class SystemOneRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = SystemOneClient.DefaultModel;

    /// <summary>Pre-serialized state. Callers serialize their own typed state through a context so this assembly stays state-agnostic.</summary>
    [JsonPropertyName("state")]
    public JsonElement State { get; set; }

    [JsonPropertyName("questions")]
    public Dictionary<string, SystemOneQuestion> Questions { get; set; } = new();
}

public sealed class SystemOneQuestion
{
    /// <summary>"choice", "noul" or "score".</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "noul";

    [JsonPropertyName("instructions")]
    public string Instructions { get; set; } = string.Empty;

    /// <summary>Choice: option → description. Noul: "true"/"false" → description. Omitted when null.</summary>
    [JsonPropertyName("criteria")]
    public Dictionary<string, string>? Criteria { get; set; }
}

public sealed class SystemOneResponse
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("answers")]
    public Dictionary<string, SystemOneAnswer> Answers { get; set; } = new();

    [JsonPropertyName("usage")]
    public SystemOneUsage? Usage { get; set; }
}

public sealed class SystemOneAnswer
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("noul")]
    public double? Noul { get; set; }

    [JsonPropertyName("choice")]
    public string? Choice { get; set; }

    [JsonPropertyName("probabilities")]
    public Dictionary<string, double>? Probabilities { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    [JsonPropertyName("score")]
    public double? Score { get; set; }
}

public sealed class SystemOneUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }
}

public enum SystemOneOutcome
{
    Ok,
    /// <summary>No API key configured; no request was made.</summary>
    NoKey,
    /// <summary>HTTP 401.</summary>
    Unauthorized,
    /// <summary>HTTP 422: the request failed validation.</summary>
    Rejected,
    /// <summary>HTTP 429.</summary>
    RateLimited,
    /// <summary>HTTP 529.</summary>
    Overloaded,
    /// <summary>Any other non-success status, a thrown transport exception, or a timeout.</summary>
    TransportFailure,
    /// <summary>200 with a body that did not parse.</summary>
    Malformed,
}

public sealed record SystemOneResult(SystemOneOutcome Outcome, SystemOneResponse? Response, string? Detail);
