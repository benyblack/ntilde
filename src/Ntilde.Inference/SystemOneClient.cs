using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Ntilde.Inference;

/// <summary>
/// One POST to the TypeSafe System One endpoint. Typed outcomes, never throws for a server or
/// transport failure. No retry and no backoff: that policy belongs to the caller so it can be
/// tested with a fake clock.
/// </summary>
public sealed class SystemOneClient
{
    public const string DefaultEndpoint = "https://api.typesafe.ai/v1/systemone";
    public const string DefaultModel = "jev-latest";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly IApiKeySource _keySource;
    private readonly Uri _endpoint;

    public SystemOneClient(HttpClient http, IApiKeySource keySource, Uri? endpoint = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _keySource = keySource ?? throw new ArgumentNullException(nameof(keySource));
        _endpoint = endpoint ?? new Uri(DefaultEndpoint);
        if (_http.Timeout == Timeout.InfiniteTimeSpan || _http.Timeout > DefaultTimeout)
        {
            _http.Timeout = DefaultTimeout;
        }
    }

    /// <summary>True when a key is available right now; lets callers skip work before capturing anything.</summary>
    public bool HasCredentials => !string.IsNullOrEmpty(_keySource.TryGetKey());

    public async Task<SystemOneResult> EvaluateAsync(SystemOneRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = _keySource.TryGetKey();
        if (string.IsNullOrEmpty(key))
        {
            return new SystemOneResult(SystemOneOutcome.NoKey, null, "no API key configured");
        }

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var json = JsonSerializer.Serialize(request, InferenceJsonContext.Default.SystemOneRequest);
            message.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize(body, InferenceJsonContext.Default.SystemOneResponse);
                    return parsed == null
                        ? new SystemOneResult(SystemOneOutcome.Malformed, null, "empty body")
                        : new SystemOneResult(SystemOneOutcome.Ok, parsed, null);
                }
                catch (JsonException ex)
                {
                    return new SystemOneResult(SystemOneOutcome.Malformed, null, ex.Message);
                }
            }

            var outcome = (int)response.StatusCode switch
            {
                401 => SystemOneOutcome.Unauthorized,
                422 => SystemOneOutcome.Rejected,
                429 => SystemOneOutcome.RateLimited,
                529 => SystemOneOutcome.Overloaded,
                _ => SystemOneOutcome.TransportFailure,
            };
            return new SystemOneResult(outcome, null, $"HTTP {(int)response.StatusCode}: {Truncate(body)}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
        {
            return new SystemOneResult(SystemOneOutcome.TransportFailure, null, ex.Message);
        }
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200];
}
