using System.Text.Json;
using Ntilde.AgentHost;
using Ntilde.AgentHost.Contracts;
using Ntilde.VT;

namespace Ntilde.AppTests.AgentHost;

public sealed class AgentHostMetadataProtocolTests
{
    private sealed class Observer : IAgentPaneObserver
    {
        internal required Func<AgentSessionRegistration, CancellationToken, Task<PaneMetadataResult?>> Read { get; init; }
        public Task<PaneMetadataResult?> ReadPaneMetadataAsync(AgentSessionRegistration registration, CancellationToken cancellationToken) => Read(registration, cancellationToken);
    }
    private static AgentSessionRegistration Register(AgentSessionRegistry registry)
    {
        var value = new AgentSessionRegistration(Guid.NewGuid(), new TerminalBuffer(80, 4), "title", "local", "local", true);
        value.SetLifecycle(new InputStubSession()); registry.Register(value); return value;
    }
    private static PaneMetadataResult Result(AgentSessionRegistration registration)
    {
        Assert.True(AgentPaneMetadataCapture.TryRead(registration.Buffer, out var buffer));
        return new PaneMetadataResult
        {
            PaneId = registration.PaneId,
            DisplayTitle = "actual",
            Buffer = buffer!,
            Presentation = new PanePresentationMetadata
            {
                WindowActive = false,
                WindowVisible = true,
                PaneActive = true,
                FocusMode = false,
                NotificationsEnabled = false,
                ProgressEnabled = true,
                TabText = "actual",
                TabTextVisible = true,
                ToastVisible = false,
                ToastTitle = "",
                ToastMessage = "",
            },
        };
    }
    private static Task<AgentHostResponse> Request(AgentHostService service, Guid paneId, CancellationToken cancellationToken)
        => service.HandleRequestLineAsync("{\"v\":1,\"id\":1,\"method\":\"readPaneMetadata\",\"params\":{\"paneId\":\"" + paneId + "\"}}", cancellationToken);

    [Fact]
    public async Task Read_requires_no_act_permission_and_returns_observed_facts_with_read_attention_only()
    {
        var registry = new AgentSessionRegistry(); var registration = Register(registry);
        using var service = new AgentHostService(registry);
        Assert.False(service.ActEnabled);
        service.SetPaneObserver(new Observer { Read = (value, _) => Task.FromResult<PaneMetadataResult?>(Result(value)) });
        var response = await Request(service, registration.PaneId, TestContext.Current.CancellationToken);
        Assert.Null(response.Error);
        var observed = response.Result!.Value.Deserialize(AgentHostJsonContext.Default.PaneMetadataResult)!;
        Assert.Equal("actual", observed.DisplayTitle); Assert.False(observed.Presentation.WindowActive);
        Assert.Equal(AgentAttentionTier.Watched, registration.AttentionMachine.Snapshot().Tier);
        Assert.Empty(Assert.IsType<InputStubSession>(registration.ObservationSession).Inputs);
    }

    [Fact]
    public async Task Missing_observer_or_session_returns_unavailable_without_read_attention()
    {
        var registry = new AgentSessionRegistry(); var registration = Register(registry);
        using var service = new AgentHostService(registry);
        Assert.Equal(AgentHostProtocol.ErrorCodes.PaneMetadataUnavailable, (await Request(service, registration.PaneId, TestContext.Current.CancellationToken)).Error?.Code);
        service.SetPaneObserver(new Observer { Read = (_, _) => throw new InvalidOperationException("must not run") });
        registration.SetLifecycle(null);
        Assert.Equal(AgentHostProtocol.ErrorCodes.PaneMetadataUnavailable, (await Request(service, registration.PaneId, TestContext.Current.CancellationToken)).Error?.Code);
        Assert.Equal(AgentAttentionTier.Idle, registration.AttentionMachine.Snapshot().Tier);
    }

    [Fact]
    public async Task Unknown_or_empty_identity_is_refused()
    {
        using var service = new AgentHostService(new AgentSessionRegistry());
        Assert.Equal(AgentHostProtocol.ErrorCodes.SessionNotFound, (await Request(service, Guid.NewGuid(), TestContext.Current.CancellationToken)).Error?.Code);
        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, (await Request(service, Guid.Empty, TestContext.Current.CancellationToken)).Error?.Code);
        var missing = await service.HandleRequestLineAsync("{\"v\":1,\"id\":1,\"method\":\"readPaneMetadata\",\"params\":{}}", TestContext.Current.CancellationToken);
        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, missing.Error?.Code);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("registration")]
    [InlineData("observer")]
    [InlineData("paneId")]
    public async Task A_changed_identity_cannot_return_a_stale_success(string changed)
    {
        var registry = new AgentSessionRegistry(); var registration = Register(registry);
        using var service = new AgentHostService(registry);
        var result = Result(registration);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<PaneMetadataResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SetPaneObserver(new Observer { Read = (_, _) => { entered.SetResult(); return completed.Task; } });
        var pending = Request(service, registration.PaneId, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        switch (changed)
        {
            case "session": registration.SetLifecycle(new InputStubSession()); break;
            case "registration": registry.Unregister(registration.PaneId); break;
            case "observer": service.SetPaneObserver(null); break;
            case "paneId": result = result with { PaneId = Guid.NewGuid() }; break;
        }
        completed.SetResult(result);
        Assert.Equal(AgentHostProtocol.ErrorCodes.PaneMetadataUnavailable, (await pending).Error?.Code);
        Assert.Equal(AgentAttentionTier.Idle, registration.AttentionMachine.Snapshot().Tier);
    }

    [Fact]
    public async Task A_nonresponsive_observer_cannot_hold_the_endpoint_after_its_deadline()
    {
        var registry = new AgentSessionRegistry(); var registration = Register(registry);
        using var service = new AgentHostService(registry);
        var completed = new TaskCompletionSource<PaneMetadataResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SetPaneObserver(new Observer { Read = (_, _) => completed.Task });
        var pending = Request(service, registration.PaneId, TestContext.Current.CancellationToken);
        var response = await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(AgentHostProtocol.ErrorCodes.PaneMetadataUnavailable, response.Error?.Code);
        completed.SetResult(Result(registration));
        Assert.Equal(AgentAttentionTier.Idle, registration.AttentionMachine.Snapshot().Tier);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_never_notes_a_read()
    {
        var registry = new AgentSessionRegistry(); var registration = Register(registry);
        using var service = new AgentHostService(registry);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        service.SetPaneObserver(new Observer { Read = (_, token) => Task.Delay(Timeout.Infinite, token).ContinueWith<PaneMetadataResult?>(_ => null, token) });
        var pending = Request(service, registration.PaneId, cancelled.Token); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        Assert.Equal(AgentAttentionTier.Idle, registration.AttentionMachine.Snapshot().Tier);
    }

    [Fact]
    public async Task Observer_failure_does_not_disclose_exception_details_or_report_empty_success()
    {
        var registry = new AgentSessionRegistry(); var registration = Register(registry);
        using var service = new AgentHostService(registry);
        service.SetPaneObserver(new Observer { Read = (_, _) => throw new InvalidOperationException("private exception") });
        var response = await Request(service, registration.PaneId, TestContext.Current.CancellationToken);
        Assert.Equal(AgentHostProtocol.ErrorCodes.PaneMetadataUnavailable, response.Error?.Code);
        Assert.DoesNotContain("private", response.Error!.Message); Assert.Null(response.Result);
    }
}
