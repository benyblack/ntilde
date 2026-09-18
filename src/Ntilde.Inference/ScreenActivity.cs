using System.Text.Json.Serialization;

namespace Ntilde.Inference;

/// <summary>What the classifier is shown: the redacted visible text of one pane and its size. Nothing else.</summary>
public sealed record ScreenSample(string Text, int Rows, int Cols);

/// <summary>The JSON state object sent as <c>state</c>. Exactly three fields, by design.</summary>
public sealed class ScreenState
{
    [JsonPropertyName("screen")]
    public string Screen { get; set; } = string.Empty;

    [JsonPropertyName("rows")]
    public int Rows { get; set; }

    [JsonPropertyName("cols")]
    public int Cols { get; set; }
}

public enum ScreenActivity
{
    CommandRunning,
    AgentWorking,
    WaitingForUser,
    IdleShellPrompt,
    UnknownBlank,
}

public sealed record ScreenActivityAnswer(
    ScreenActivity Activity,
    double Confidence,
    double NeedsAttention,
    double LastCommandFailed,
    int InputTokens);

public enum ScreenClassificationOutcome
{
    Answered,
    NoKey,
    Unauthorized,
    RateLimited,
    Overloaded,
    Rejected,
    TransportFailure,
    Malformed,
}

public sealed record ScreenClassificationResult(ScreenClassificationOutcome Outcome, ScreenActivityAnswer? Answer, string? Detail);

public interface IScreenActivityClassifier
{
    /// <summary>False when no key is configured, so callers can skip capturing a screen at all.</summary>
    bool HasCredentials { get; }

    Task<ScreenClassificationResult> ClassifyAsync(ScreenSample sample, CancellationToken cancellationToken);
}
