using System.Text.Json;
using Ntilde.AgentHost;
using Ntilde.AgentHost.Contracts;

namespace Ntilde.AppTests.AgentHost;

public sealed class AgentHostGeneratedInputProtocolTests
{
    private static string Request(Guid id, string fields = "\"kind\":\"keyDown\",\"key\":\"7\"")
        => "{\"v\":1,\"id\":1,\"method\":\"generatePaneInput\",\"params\":{\"paneId\":\"" + id + "\"," + fields + "}}";

    private static (AgentHostService Service, AgentSessionRegistry Registry, AgentSessionRegistration Registration, AgentActivityJournal Journal, StubExecutor Executor) Setup(bool act = true, string kind = "local", bool active = true, bool running = true)
    {
        var registry = new AgentSessionRegistry();
        var registration = new AgentSessionRegistration(Guid.NewGuid(), new Ntilde.VT.TerminalBuffer(80, 24), "title", "Profile", kind, active);
        registration.SetLifecycle(new InputStubSession(running));
        registry.Register(registration);
        var journal = new AgentActivityJournal();
        var service = new AgentHostService(registry, AgentHostTestEndpoint.CreateEndpoint(Path.GetTempPath()), Path.GetTempPath(), null, journal);
        service.ActEnabled = act;
        var executor = new StubExecutor { OnGenerateInput = (_, _, _, _) => Task.FromResult<GeneratePaneInputResult?>(Observed()) };
        service.SetActionExecutor(executor);
        return (service, registry, registration, journal, executor);
    }

    private static GeneratePaneInputResult Observed() => new()
    {
        EventDispatched = true,
        ObservationAvailable = true,
        Handled = false,
        Rows = 21,
        Cols = 71,
        ViewRows = 21,
        ViewCols = 71,
        Modifiers = 0,
        ApplicationKeypad = true,
        MouseTracking = 0,
        MouseSgr = false,
        KittyKeyboardFlags = 0,
    };

    [Fact]
    public async Task Default_permission_denies_and_journals_without_dispatch()
    {
        var s = Setup(act: false); using var service = s.Service;
        var response = await service.HandleRequestLineAsync(Request(s.Registration.PaneId), TestContext.Current.CancellationToken);
        Assert.Equal(AgentHostProtocol.ErrorCodes.ActDisabled, response.Error?.Code);
        Assert.Equal(0, s.Executor.GenerateInputCalls);
        Assert.Equal("generatePaneInput", Assert.Single(s.Journal.Snapshot()).Method);
    }

    [Theory]
    [InlineData("\"kind\":\"keyDown\",\"key\":\"A\"")]
    [InlineData("\"kind\":\"keyDown\",\"key\":\"7\",\"modifiers\":16")]
    [InlineData("\"kind\":\"text\",\"text\":\"secret\"")]
    [InlineData("\"kind\":\"text\",\"text\":\"7\",\"modifiers\":1")]
    [InlineData("\"kind\":\"mousePress\",\"button\":\"left\",\"column\":0,\"row\":1")]
    [InlineData("\"kind\":\"mouseMove\",\"button\":\"none\",\"column\":257,\"row\":1")]
    [InlineData("\"kind\":\"mouseWheel\",\"wheelDelta\":2,\"column\":1,\"row\":1")]
    [InlineData("\"kind\":\"keyDown\",\"key\":\"7\",\"selector\":\"any\"")]
    [InlineData("\"kind\":\"keyDown\",\"key\":\"7\",\"text\":\"7\",\"modifiers\":1")]
    [InlineData("\"kind\":\"keyDown\",\"key\":\"7\",\"text\":\"A\"")]
    [InlineData("\"kind\":\"keyDown\",\"key\":\"7\",\"text\":\"77\"")]
    [InlineData("\"kind\":\"keyUp\",\"key\":\"7\",\"text\":\"7\"")]
    [InlineData("\"kind\":\"keyDown\",\"key\":\"7\",\"text\":\"7\",\"column\":1,\"row\":1")]
    public async Task Invalid_or_combined_events_never_reach_the_executor(string fields)
    {
        var s = Setup(); using var service = s.Service;
        var response = await service.HandleRequestLineAsync(Request(s.Registration.PaneId, fields), TestContext.Current.CancellationToken);
        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, response.Error?.Code);
        Assert.Equal(0, s.Executor.GenerateInputCalls);
        Assert.Single(s.Journal.Snapshot());
    }

    [Theory]
    [InlineData("\"kind\":\"keyDown\",\"key\":\"7\",\"text\":\"7\",\"column\":1,\"row\":1")]
    [InlineData("\"kind\":\"keyDown\",\"key\":\"A\"")]
    [InlineData("\"kind\":\"keyDown\",\"key\":\"capslock\"")]
    [InlineData("\"kind\":\"keyDown\",\"key\":\"a\",\"text\":\"a\",\"modifiers\":2")]
    [InlineData("\"kind\":\"keyDown\",\"key\":\"a\",\"text\":\"A\",\"modifiers\":5")]
    [InlineData("\"kind\":\"keyDown\",\"key\":\"space\",\"text\":\"x\"")]
    [InlineData("\"kind\":\"keyDown\",\"key\":\"space\",\"text\":\" \",\"modifiers\":5")]
    [InlineData("\"kind\":\"keyDown\",\"key\":\"digit1\",\"text\":\"1\",\"modifiers\":1")]
    [InlineData("\"kind\":\"keyDown\",\"key\":\"digit1\",\"text\":\"!\"")]
    [InlineData("\"kind\":\"keyDown\",\"key\":\"return\",\"text\":\"\\r\"")]
    [InlineData("\"kind\":\"keyDown\",\"key\":\"backspace\",\"text\":\"\\u007f\"")]
    [InlineData("\"kind\":\"keyUp\",\"key\":\"a\",\"text\":\"a\"")]
    public async Task Invalid_or_uncorrelatable_ordinary_events_never_reach_the_executor(string fields)
    {
        var s = Setup(); using var service = s.Service;
        var response = await service.HandleRequestLineAsync(Request(s.Registration.PaneId, fields), TestContext.Current.CancellationToken);
        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, response.Error?.Code);
        Assert.Equal(0, s.Executor.GenerateInputCalls);
        Assert.Single(s.Journal.Snapshot());
    }

    [Theory]
    [InlineData("\"kind\":\"keyDown\",\"key\":\"a\",\"text\":\"a\"", "a", "a", 0)]
    [InlineData("\"kind\":\"keyDown\",\"key\":\"a\",\"text\":\"A\",\"modifiers\":1", "a", "A", 1)]
    [InlineData("\"kind\":\"keyDown\",\"key\":\"space\",\"text\":\" \",\"modifiers\":1", "space", " ", 1)]
    [InlineData("\"kind\":\"keyDown\",\"key\":\"digit1\",\"text\":\"!\",\"modifiers\":1", "digit1", "!", 1)]
    [InlineData("\"kind\":\"keyDown\",\"key\":\"digit1\",\"text\":\"1\"", "digit1", "1", 0)]
    [InlineData("\"kind\":\"keyDown\",\"key\":\"a\",\"modifiers\":2", "a", null, 2)]
    [InlineData("\"kind\":\"keyUp\",\"key\":\"a\",\"modifiers\":2", "a", null, 2)]
    public async Task Valid_correlated_ordinary_events_reach_the_executor(string fields, string key, string? text, int modifiers)
    {
        var s = Setup(); using var service = s.Service;
        GeneratePaneInputParams? seen = null;
        s.Executor.OnGenerateInput = (input, _, _, _) => { seen = input; return Task.FromResult<GeneratePaneInputResult?>(Observed()); };
        var response = await service.HandleRequestLineAsync(Request(s.Registration.PaneId, fields), TestContext.Current.CancellationToken);
        Assert.Null(response.Error);
        Assert.Equal((key, text, modifiers), (seen!.Key, seen.Text, seen.Modifiers));
        Assert.Equal(1, s.Executor.GenerateInputCalls);
    }

    [Fact]
    public async Task One_keypad_key_down_may_carry_its_backend_text_as_a_single_bounded_action()
    {
        var s = Setup(); using var service = s.Service;
        GeneratePaneInputParams? seen = null;
        s.Executor.OnGenerateInput = (input, _, _, _) => { seen = input; return Task.FromResult<GeneratePaneInputResult?>(Observed() with { TextHandled = true }); };
        var response = await service.HandleRequestLineAsync(Request(s.Registration.PaneId, "\"kind\":\"keyDown\",\"key\":\"7\",\"text\":\"7\""), TestContext.Current.CancellationToken);
        Assert.Null(response.Error);
        Assert.Equal(("keyDown", "7", "7", 0), (seen!.Kind, seen.Key, seen.Text, seen.Modifiers));
        Assert.Equal(1, s.Executor.GenerateInputCalls);
        var result = response.Result!.Value.Deserialize(AgentHostJsonContext.Default.GeneratePaneInputResult)!;
        Assert.True(result.TextHandled);
        Assert.Contains("\"textHandled\":true", response.Result!.Value.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ssh", true, true)]
    [InlineData("local", false, true)]
    [InlineData("local", true, false)]
    public async Task Nonlocal_inactive_or_dead_registration_cannot_dispatch(string kind, bool active, bool running)
    {
        var s = Setup(kind: kind, active: active, running: running); using var service = s.Service;
        var response = await service.HandleRequestLineAsync(Request(s.Registration.PaneId), TestContext.Current.CancellationToken);
        Assert.Equal(AgentHostProtocol.ErrorCodes.GeneratedInputUnavailable, response.Error?.Code);
        Assert.Equal(0, s.Executor.GenerateInputCalls);
    }

    [Fact]
    public async Task Response_reports_actual_dispatch_facts_without_delivery_or_ownership_claims()
    {
        var s = Setup(); using var service = s.Service;
        var response = await service.HandleRequestLineAsync(Request(s.Registration.PaneId), TestContext.Current.CancellationToken);
        Assert.Null(response.Error);
        var dto = response.Result!.Value.Deserialize(AgentHostJsonContext.Default.GeneratePaneInputResult)!;
        Assert.Equal((21, 71, false, "avalonia-routed"), (dto.Rows, dto.Cols, dto.Handled, dto.Boundary));
        Assert.Equal("synthetic event", Assert.Single(s.Journal.Snapshot()).Target);
    }

    [Theory]
    [InlineData("registration")]
    [InlineData("session")]
    [InlineData("permission")]
    [InlineData("executor")]
    public async Task Lease_loss_preserves_dispatch_facts_and_withholds_stale_observation(string change)
    {
        var s = Setup(); using var service = s.Service;
        int injected = 0, replacementInjected = 0; s.Registration.InputInjected = () => injected++;
        s.Executor.OnGenerateInput = (_, _, guard, _) =>
        {
            Assert.True(guard());
            switch (change)
            {
                case "registration":
                    s.Registry.Unregister(s.Registration.PaneId);
                    var replacementEntry = new AgentSessionRegistration(s.Registration.PaneId, s.Registration.Buffer, "other", "Profile", "local", true)
                    { InputInjected = () => replacementInjected++ };
                    replacementEntry.SetLifecycle(new InputStubSession()); s.Registry.Register(replacementEntry);
                    break;
                case "session": s.Registration.SetLifecycle(new InputStubSession()); break;
                case "permission": service.ActEnabled = false; break;
                case "executor": service.SetActionExecutor(new StubExecutor()); break;
            }
            Assert.False(guard());
            return Task.FromResult<GeneratePaneInputResult?>(Observed() with { InputSubmitted = true });
        };
        var response = await service.HandleRequestLineAsync(Request(s.Registration.PaneId), TestContext.Current.CancellationToken);
        Assert.Null(response.Error);
        var result = response.Result!.Value.Deserialize(AgentHostJsonContext.Default.GeneratePaneInputResult)!;
        Assert.True(result.EventDispatched); Assert.True(result.InputSubmitted); Assert.False(result.ObservationAvailable);
        Assert.Null(result.Rows); Assert.Null(result.ApplicationKeypad);
        Assert.Equal(change == "session" ? AgentAttentionTier.Idle : AgentAttentionTier.Wrote, s.Registration.AttentionMachine.Snapshot().Tier);
        Assert.Equal(change is "permission" or "executor" ? 1 : 0, injected);
        Assert.Equal(0, replacementInjected);
        if (change == "registration")
        {
            Assert.True(s.Registry.TryGet(s.Registration.PaneId, out var replacement));
            Assert.Equal(AgentAttentionTier.Idle, replacement.AttentionMachine.Snapshot().Tier);
        }
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_journals_once()
    {
        var s = Setup(); using var service = s.Service;
        s.Executor.OnGenerateInput = (_, _, _, _) => Task.FromCanceled<GeneratePaneInputResult?>(new CancellationToken(true));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.HandleRequestLineAsync(Request(s.Registration.PaneId), cts.Token));
        Assert.Equal("cancelled", Assert.Single(s.Journal.Snapshot()).Outcome);
    }
    [Fact]
    public async Task Request_deadline_cancels_the_executor_token_and_returns_no_dispatch_claim()
    {
        var s = Setup(); using var service = s.Service;
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Executor.OnGenerateInput = async (_, _, _, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Observed();
        };
        var response = await service.HandleRequestLineAsync(Request(s.Registration.PaneId), TestContext.Current.CancellationToken);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(AgentHostProtocol.ErrorCodes.GeneratedInputUnavailable, response.Error?.Code);
        Assert.Null(response.Result); Assert.Single(s.Journal.Snapshot());
        Assert.Contains("indeterminate", response.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_executor_and_unknown_pane_have_explicit_errors_without_fallback()
    {
        var s = Setup(); using var service = s.Service;
        service.SetActionExecutor(null);
        var unavailable = await service.HandleRequestLineAsync(Request(s.Registration.PaneId), TestContext.Current.CancellationToken);
        Assert.Equal(AgentHostProtocol.ErrorCodes.ActUnavailable, unavailable.Error?.Code);
        var missing = await service.HandleRequestLineAsync(Request(Guid.NewGuid()), TestContext.Current.CancellationToken);
        Assert.Equal(AgentHostProtocol.ErrorCodes.SessionNotFound, missing.Error?.Code);
    }

    [Theory]
    [InlineData(false, false, AgentAttentionTier.Idle)]
    [InlineData(true, false, AgentAttentionTier.Idle)]
    [InlineData(true, true, AgentAttentionTier.Wrote)]
    public async Task Attention_write_marker_requires_a_returned_session_input_call(bool handled, bool submitted, AgentAttentionTier expected)
    {
        var s = Setup(); using var service = s.Service;
        int injected = 0; s.Registration.InputInjected = () => injected++;
        s.Executor.OnGenerateInput = (_, _, _, _) => Task.FromResult<GeneratePaneInputResult?>(Observed() with { Handled = handled, InputSubmitted = submitted });
        var response = await service.HandleRequestLineAsync(Request(s.Registration.PaneId), TestContext.Current.CancellationToken);
        Assert.Null(response.Error);
        Assert.Equal(expected, s.Registration.AttentionMachine.Snapshot().Tier);
        Assert.Equal(submitted ? 1 : 0, injected);
    }

}
