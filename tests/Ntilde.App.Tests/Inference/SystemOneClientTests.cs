using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ntilde.Inference;

namespace Ntilde.AppTests.Inference;

public class SystemOneClientTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OkBody, Encoding.UTF8, "application/json") };
        public Exception? Throw { get; set; }
        public Func<CancellationToken, Task>? Delay { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (Throw != null) throw Throw;
            if (Delay != null) await Delay(cancellationToken);
            return Respond(request);
        }
    }

    private sealed class FixedKey(string? key) : IApiKeySource
    {
        public string? TryGetKey() => key;
    }

    private const string OkBody =
        """
        {"model":"jev-1.13.0","answers":{
          "activity":{"type":"choice","choice":"waiting_for_user","confidence":0.92,
                      "probabilities":{"waiting_for_user":0.94,"agent_working":0.05,"idle_shell_prompt":0.01}},
          "needs_attention":{"type":"noul","noul":0.79},
          "last_command_failed":{"type":"noul","noul":0.04}},
         "usage":{"input_tokens":1073,"output_tokens":59}}
        """;

    private static SystemOneRequest SampleRequest() => new()
    {
        Model = SystemOneClient.DefaultModel,
        State = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["screen"] = "$ " }, InferenceJsonContext.Default.DictionaryStringString),
        Questions = new Dictionary<string, SystemOneQuestion>
        {
            ["activity"] = new() { Type = "choice", Instructions = "What?", Criteria = new() { ["a"] = "A", ["b"] = "B" } },
            ["needs_attention"] = new() { Type = "noul", Instructions = "Need?" },
        },
    };

    private static (SystemOneClient Client, FakeHandler Handler) Make(string? key = "k-test")
    {
        var handler = new FakeHandler();
        var client = new SystemOneClient(new HttpClient(handler), new FixedKey(key));
        return (client, handler);
    }

    [Fact]
    public async Task Posts_to_the_endpoint_with_bearer_and_json_body()
    {
        var (client, handler) = Make();

        var result = await client.EvaluateAsync(SampleRequest(), CancellationToken.None);

        Assert.Equal(SystemOneOutcome.Ok, result.Outcome);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal(SystemOneClient.DefaultEndpoint, handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("k-test", handler.LastRequest.Headers.Authorization.Parameter);
        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("jev-latest", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("$ ", doc.RootElement.GetProperty("state").GetProperty("screen").GetString());
        var q = doc.RootElement.GetProperty("questions");
        Assert.Equal("choice", q.GetProperty("activity").GetProperty("type").GetString());
        Assert.Equal("A", q.GetProperty("activity").GetProperty("criteria").GetProperty("a").GetString());
        Assert.False(q.GetProperty("needs_attention").TryGetProperty("criteria", out _), "null criteria must be omitted");
    }

    [Fact]
    public async Task Parses_choice_and_noul_answers_and_usage()
    {
        var (client, _) = Make();

        var result = await client.EvaluateAsync(SampleRequest(), CancellationToken.None);

        var answers = result.Response!.Answers;
        Assert.Equal("waiting_for_user", answers["activity"].Choice);
        Assert.Equal(0.92, answers["activity"].Confidence!.Value, 3);
        Assert.Equal(0.94, answers["activity"].Probabilities!["waiting_for_user"], 3);
        Assert.Equal(0.79, answers["needs_attention"].Noul!.Value, 3);
        Assert.Equal(1073, result.Response.Usage!.InputTokens);
    }

    [Fact]
    public async Task Missing_key_is_reported_without_a_request()
    {
        var (client, handler) = Make(key: null);

        var result = await client.EvaluateAsync(SampleRequest(), CancellationToken.None);

        Assert.Equal(SystemOneOutcome.NoKey, result.Outcome);
        Assert.Null(handler.LastRequest);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, SystemOneOutcome.Unauthorized)]
    [InlineData(HttpStatusCode.UnprocessableEntity, SystemOneOutcome.Rejected)]
    [InlineData(HttpStatusCode.TooManyRequests, SystemOneOutcome.RateLimited)]
    [InlineData((HttpStatusCode)529, SystemOneOutcome.Overloaded)]
    [InlineData(HttpStatusCode.InternalServerError, SystemOneOutcome.TransportFailure)]
    public async Task Maps_status_codes_to_outcomes(HttpStatusCode code, SystemOneOutcome expected)
    {
        var (client, handler) = Make();
        handler.Respond = _ => new HttpResponseMessage(code) { Content = new StringContent("{\"detail\":\"x\"}") };

        var result = await client.EvaluateAsync(SampleRequest(), CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task Transport_exception_is_a_transport_failure_not_a_throw()
    {
        var (client, handler) = Make();
        handler.Throw = new HttpRequestException("boom");

        var result = await client.EvaluateAsync(SampleRequest(), CancellationToken.None);

        Assert.Equal(SystemOneOutcome.TransportFailure, result.Outcome);
        Assert.Contains("boom", result.Detail);
    }

    [Fact]
    public async Task Cancellation_is_a_transport_failure()
    {
        var (client, handler) = Make();
        handler.Throw = new TaskCanceledException("timed out");

        var result = await client.EvaluateAsync(SampleRequest(), CancellationToken.None);

        Assert.Equal(SystemOneOutcome.TransportFailure, result.Outcome);
    }

    [Fact]
    public async Task Malformed_success_body_is_malformed()
    {
        var (client, handler) = Make();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") };

        var result = await client.EvaluateAsync(SampleRequest(), CancellationToken.None);

        Assert.Equal(SystemOneOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public void Constructor_does_not_touch_the_http_client_timeout()
    {
        var handler = new FakeHandler();
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(100) };
        var originalTimeout = http.Timeout;

        var client = new SystemOneClient(http, new FixedKey("k-test"));

        Assert.Equal(originalTimeout, http.Timeout);
    }

    [Fact]
    public async Task Per_call_timeout_is_a_transport_failure()
    {
        var handler = new FakeHandler();
        handler.Delay = async (ct) => await Task.Delay(Timeout.Infinite, ct);
        var clientWithShortTimeout = new SystemOneClient(new HttpClient(handler), new FixedKey("k-test"), timeout: TimeSpan.FromMilliseconds(50));

        var result = await clientWithShortTimeout.EvaluateAsync(SampleRequest(), CancellationToken.None);

        Assert.Equal(SystemOneOutcome.TransportFailure, result.Outcome);
    }
}
