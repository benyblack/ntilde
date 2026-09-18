using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ntilde.Inference;

namespace Ntilde.AppTests.Inference;

public class ScreenActivityClassifierTests
{
    private sealed class FixedKey(string? key) : IApiKeySource { public string? TryGetKey() => key; }

    private sealed class CannedHandler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private static ScreenActivityClassifier Make(HttpStatusCode code, string body, string? key = "k")
        => new(new SystemOneClient(new HttpClient(new CannedHandler(code, body)), new FixedKey(key)));

    private static SystemOneResponse Response(string choice, double confidence, double attention, double failed) => new()
    {
        Answers = new Dictionary<string, SystemOneAnswer>
        {
            [ScreenActivityClassifier.ActivityQuestionId] = new() { Type = "choice", Choice = choice, Confidence = confidence },
            [ScreenActivityClassifier.AttentionQuestionId] = new() { Type = "noul", Noul = attention },
            [ScreenActivityClassifier.FailedQuestionId] = new() { Type = "noul", Noul = failed },
        },
        Usage = new SystemOneUsage { InputTokens = 900, OutputTokens = 50 },
    };

    [Fact]
    public void Request_has_exactly_screen_rows_cols_and_three_questions()
    {
        var request = ScreenActivityClassifier.BuildRequest(new ScreenSample("user@host:~$ ", 24, 80));

        Assert.Equal(SystemOneClient.DefaultModel, request.Model);
        var props = request.State.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "cols", "rows", "screen" }, props);
        Assert.Equal("user@host:~$ ", request.State.GetProperty("screen").GetString());
        Assert.Equal(24, request.State.GetProperty("rows").GetInt32());
        Assert.Equal(80, request.State.GetProperty("cols").GetInt32());

        Assert.Equal(3, request.Questions.Count);
        var activity = request.Questions[ScreenActivityClassifier.ActivityQuestionId];
        Assert.Equal("choice", activity.Type);
        Assert.Equal(
            new[] { "agent_working", "command_running", "idle_shell_prompt", "unknown_blank", "waiting_for_user" },
            activity.Criteria!.Keys.OrderBy(k => k).ToArray());
        Assert.Equal("noul", request.Questions[ScreenActivityClassifier.AttentionQuestionId].Type);
        Assert.Equal("noul", request.Questions[ScreenActivityClassifier.FailedQuestionId].Type);
    }

    [Fact]
    public void Criteria_texts_are_non_empty_and_distinct()
    {
        var activity = ScreenActivityClassifier.BuildRequest(new ScreenSample("x", 1, 1)).Questions[ScreenActivityClassifier.ActivityQuestionId];
        var texts = activity.Criteria!.Values.ToArray();
        Assert.All(texts, t => Assert.False(string.IsNullOrWhiteSpace(t)));
        Assert.Equal(texts.Length, texts.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("command_running", ScreenActivity.CommandRunning)]
    [InlineData("agent_working", ScreenActivity.AgentWorking)]
    [InlineData("waiting_for_user", ScreenActivity.WaitingForUser)]
    [InlineData("idle_shell_prompt", ScreenActivity.IdleShellPrompt)]
    [InlineData("unknown_blank", ScreenActivity.UnknownBlank)]
    public void Maps_each_choice_to_the_enum(string choice, ScreenActivity expected)
    {
        var answer = ScreenActivityClassifier.MapAnswer(Response(choice, 0.9, 0.2, 0.1));

        Assert.NotNull(answer);
        Assert.Equal(expected, answer!.Activity);
        Assert.Equal(0.9, answer.Confidence, 3);
        Assert.Equal(0.2, answer.NeedsAttention, 3);
        Assert.Equal(0.1, answer.LastCommandFailed, 3);
        Assert.Equal(900, answer.InputTokens);
    }

    [Fact]
    public void Unknown_choice_or_missing_question_maps_to_null()
    {
        Assert.Null(ScreenActivityClassifier.MapAnswer(Response("something_else", 0.9, 0.2, 0.1)));

        var missing = Response("waiting_for_user", 0.9, 0.2, 0.1);
        missing.Answers.Remove(ScreenActivityClassifier.AttentionQuestionId);
        Assert.Null(ScreenActivityClassifier.MapAnswer(missing));
    }

    [Fact]
    public async Task Classify_returns_answered_on_success()
    {
        var body = JsonSerializer.Serialize(Response("agent_working", 0.88, 0.24, 0.08), InferenceJsonContext.Default.SystemOneResponse);
        var classifier = Make(HttpStatusCode.OK, body);

        var result = await classifier.ClassifyAsync(new ScreenSample("✻ Crunching…", 34, 101), CancellationToken.None);

        Assert.Equal(ScreenClassificationOutcome.Answered, result.Outcome);
        Assert.Equal(ScreenActivity.AgentWorking, result.Answer!.Activity);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ScreenClassificationOutcome.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests, ScreenClassificationOutcome.RateLimited)]
    [InlineData((HttpStatusCode)529, ScreenClassificationOutcome.Overloaded)]
    [InlineData(HttpStatusCode.UnprocessableEntity, ScreenClassificationOutcome.Rejected)]
    [InlineData(HttpStatusCode.BadGateway, ScreenClassificationOutcome.TransportFailure)]
    public async Task Classify_forwards_client_outcomes(HttpStatusCode code, ScreenClassificationOutcome expected)
    {
        var classifier = Make(code, "{}");

        var result = await classifier.ClassifyAsync(new ScreenSample("x", 1, 1), CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Answer);
    }

    [Fact]
    public async Task Unmappable_success_is_malformed_and_no_key_is_no_key()
    {
        var bad = JsonSerializer.Serialize(Response("nope", 0.5, 0.5, 0.5), InferenceJsonContext.Default.SystemOneResponse);
        Assert.Equal(ScreenClassificationOutcome.Malformed,
            (await Make(HttpStatusCode.OK, bad).ClassifyAsync(new ScreenSample("x", 1, 1), CancellationToken.None)).Outcome);

        var noKey = Make(HttpStatusCode.OK, "{}", key: null);
        Assert.False(noKey.HasCredentials);
        Assert.Equal(ScreenClassificationOutcome.NoKey,
            (await noKey.ClassifyAsync(new ScreenSample("x", 1, 1), CancellationToken.None)).Outcome);
    }
}
