using System.Text.Json;

namespace Ntilde.Inference;

/// <summary>
/// The three questions asked of a pane's visible text, and the mapping of the answer onto
/// <see cref="ScreenActivityAnswer"/>. The wording is the one measured on 2026-09-17 (8/8 on
/// live and constructed screens); tune it here, with the tests, never inline at a call site.
/// </summary>
public sealed class ScreenActivityClassifier : IScreenActivityClassifier
{
    public const string ActivityQuestionId = "activity";
    public const string AttentionQuestionId = "needs_attention";
    public const string FailedQuestionId = "last_command_failed";

    internal const string ChoiceCommandRunning = "command_running";
    internal const string ChoiceAgentWorking = "agent_working";
    internal const string ChoiceWaitingForUser = "waiting_for_user";
    internal const string ChoiceIdleShellPrompt = "idle_shell_prompt";
    internal const string ChoiceUnknownBlank = "unknown_blank";

    private readonly SystemOneClient _client;

    public ScreenActivityClassifier(SystemOneClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public bool HasCredentials => _client.HasCredentials;

    public async Task<ScreenClassificationResult> ClassifyAsync(ScreenSample sample, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var result = await _client.EvaluateAsync(BuildRequest(sample), cancellationToken).ConfigureAwait(false);
        if (result.Outcome != SystemOneOutcome.Ok || result.Response == null)
        {
            return new ScreenClassificationResult(Map(result.Outcome), null, result.Detail);
        }

        var answer = MapAnswer(result.Response);
        return answer == null
            ? new ScreenClassificationResult(ScreenClassificationOutcome.Malformed, null, "answer did not match the question set")
            : new ScreenClassificationResult(ScreenClassificationOutcome.Answered, answer, null);
    }

    internal static SystemOneRequest BuildRequest(ScreenSample sample)
    {
        var state = new ScreenState { Screen = sample.Text, Rows = sample.Rows, Cols = sample.Cols };
        return new SystemOneRequest
        {
            Model = SystemOneClient.DefaultModel,
            State = JsonSerializer.SerializeToElement(state, InferenceJsonContext.Default.ScreenState),
            Questions = new Dictionary<string, SystemOneQuestion>
            {
                [ActivityQuestionId] = new()
                {
                    Type = "choice",
                    Instructions = "Look at `screen`, the visible text of a terminal pane (last line is the bottom). What is happening in this pane right now?",
                    Criteria = new Dictionary<string, string>
                    {
                        [ChoiceCommandRunning] = "a shell command or build is still in progress: progress output, spinners, log lines, and NO shell prompt or input box as the final content",
                        [ChoiceAgentWorking] = "an AI coding agent (Claude Code, Codex, Aider, etc.) is actively thinking or running tools: a 'thinking/crunching' spinner with elapsed time, tool-call lines, no idle input box",
                        [ChoiceWaitingForUser] = "a program is stopped and waits for the user to type: a yes/no question, a password prompt, a pager, or an AI agent that finished its answer and shows an empty input box",
                        [ChoiceIdleShellPrompt] = "the shell prompt is the last thing on screen with nothing typed after it; nothing is running",
                        [ChoiceUnknownBlank] = "the screen is empty or has no readable content to decide from",
                    },
                },
                [AttentionQuestionId] = new()
                {
                    Type = "noul",
                    Instructions = "A user stepped away from this pane. Based on `screen`, does the pane now need them to come back and act (answer a question, review a finished result, fix an error)? Idle shell prompts with nothing new do NOT need attention.",
                    Criteria = new Dictionary<string, string>
                    {
                        ["true"] = "something finished or is asking for input and the user has not responded",
                        ["false"] = "nothing is waiting on the user; idle prompt or still working",
                    },
                },
                [FailedQuestionId] = new()
                {
                    Type = "noul",
                    Instructions = "Did the MOST RECENT completed command in `screen` end with an error? Judge only the last command, not earlier ones.",
                    Criteria = new Dictionary<string, string>
                    {
                        ["true"] = "the final command's output is an error/failure message",
                        ["false"] = "the final command succeeded or there is no command output",
                    },
                },
            },
        };
    }

    internal static ScreenActivityAnswer? MapAnswer(SystemOneResponse response)
    {
        if (!response.Answers.TryGetValue(ActivityQuestionId, out var activity) ||
            !response.Answers.TryGetValue(AttentionQuestionId, out var attention) ||
            !response.Answers.TryGetValue(FailedQuestionId, out var failed))
        {
            return null;
        }

        ScreenActivity? kind = activity.Choice switch
        {
            ChoiceCommandRunning => ScreenActivity.CommandRunning,
            ChoiceAgentWorking => ScreenActivity.AgentWorking,
            ChoiceWaitingForUser => ScreenActivity.WaitingForUser,
            ChoiceIdleShellPrompt => ScreenActivity.IdleShellPrompt,
            ChoiceUnknownBlank => ScreenActivity.UnknownBlank,
            _ => null,
        };
        if (kind == null || activity.Confidence is not { } confidence || attention.Noul is not { } needs || failed.Noul is not { } fail)
        {
            return null;
        }

        return new ScreenActivityAnswer(kind.Value, confidence, needs, fail, response.Usage?.InputTokens ?? 0);
    }

    private static ScreenClassificationOutcome Map(SystemOneOutcome outcome) => outcome switch
    {
        SystemOneOutcome.NoKey => ScreenClassificationOutcome.NoKey,
        SystemOneOutcome.Unauthorized => ScreenClassificationOutcome.Unauthorized,
        SystemOneOutcome.RateLimited => ScreenClassificationOutcome.RateLimited,
        SystemOneOutcome.Overloaded => ScreenClassificationOutcome.Overloaded,
        SystemOneOutcome.Rejected => ScreenClassificationOutcome.Rejected,
        SystemOneOutcome.Malformed => ScreenClassificationOutcome.Malformed,
        _ => ScreenClassificationOutcome.TransportFailure,
    };
}
