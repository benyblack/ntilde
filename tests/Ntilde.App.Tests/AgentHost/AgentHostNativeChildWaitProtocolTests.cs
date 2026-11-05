using System.Text.Json;
using Avalonia.Headless.XUnit;
using Ntilde.AgentHost;
using Ntilde.AgentHost.Contracts;
using Ntilde.AppTests.Shell;
using Ntilde.Shell.Clipboard;
using Ntilde.VT;

namespace Ntilde.AppTests.AgentHost;

[Collection(Ntilde.Tests.Infra.PrivateClipboardProcessScope.Name)]
public sealed class AgentHostNativeChildWaitProtocolTests
{
    private const string Run = PrivateClipboardQaModeTests.Run;
    private sealed class Observer : IAgentPaneObserver
    {
        internal int Calls;
        internal required Func<ReadNativeChildWaitParams, AgentSessionRegistration, CancellationToken, Task<NativeChildWaitResult?>> Read { get; init; }
        public Task<PaneMetadataResult?> ReadPaneMetadataAsync(AgentSessionRegistration registration, CancellationToken token) => Task.FromResult<PaneMetadataResult?>(null);
        public Task<NativeChildWaitResult?> ReadNativeChildWaitAsync(ReadNativeChildWaitParams input, AgentSessionRegistration registration, CancellationToken token)
        { Calls++; return Read(input, registration, token); }
    }
    private sealed class LegacyObserver : IAgentPaneObserver
    {
        public Task<PaneMetadataResult?> ReadPaneMetadataAsync(AgentSessionRegistration registration, CancellationToken token)
            => throw new InvalidOperationException("This method must not fall back to ordinary metadata.");
    }
    private static AgentSessionRegistration Register(AgentSessionRegistry registry, bool running = false)
    {
        var registration = new AgentSessionRegistration(Guid.NewGuid(), new TerminalBuffer(80, 4), "private", "private", "local", true);
        registration.SetLifecycle(new InputStubSession(running)); registry.Register(registration); return registration;
    }
    private static ReadNativeChildWaitParams Input(AgentSessionRegistration registration) => new()
    { PaneId = registration.PaneId, RunId = Run, ExpectedChildPid = 123, ExpectedNativeSessionId = registration.ObservationSession!.Id };
    private static NativeChildWaitResult Result(AgentSessionRegistration registration, bool observed = true, int code = 7) => new()
    {
        PaneId = registration.PaneId,
        NativeSessionId = registration.ObservationSession!.Id,
        OriginalChildPid = 123,
        NativeWaitObserved = observed,
        ExitCode = observed ? code : null
    };
    private static Task<AgentHostResponse> Request(AgentHostService service, ReadNativeChildWaitParams input, CancellationToken token = default)
    {
        string parameters = JsonSerializer.Serialize(input, AgentHostJsonContext.Default.ReadNativeChildWaitParams);
        return service.HandleRequestLineAsync("{\"v\":1,\"id\":1,\"method\":\"readNativeChildWait\",\"params\":" + parameters + "}", token);
    }

    [Fact]
    public void Contract_rejects_selectors_invalid_binding_and_conflicting_wait_code()
    {
        var registry = new AgentSessionRegistry(); var registration = Register(registry); var valid = Input(registration);
        Assert.True(NativeChildWaitValidation.IsValid(valid with { ExpectedNativeSessionId = null }));
        foreach (var input in new[] { valid with { PaneId = Guid.Empty }, valid with { RunId = "anything" },
            valid with { ExpectedChildPid = 0 }, valid with { ExpectedChildPid = -1 }, valid with { ExpectedNativeSessionId = Guid.Empty } })
            Assert.False(NativeChildWaitValidation.IsValid(input));
        string json = JsonSerializer.Serialize(valid, AgentHostJsonContext.Default.ReadNativeChildWaitParams);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json[..^1] + ",\"selector\":\"other\"}", AgentHostJsonContext.Default.ReadNativeChildWaitParams));
        Assert.True(NativeChildWaitValidation.IsCoherent(Result(registration, code: 0)));
        Assert.True(NativeChildWaitValidation.IsCoherent(Result(registration, observed: false)));
        Assert.False(NativeChildWaitValidation.IsCoherent(Result(registration) with { NativeWaitObserved = false }));
        Assert.False(NativeChildWaitValidation.IsCoherent(Result(registration) with { ExitCode = null }));
        Assert.False(NativeChildWaitValidation.IsCoherent(Result(registration) with { Source = "EOF" }));
    }

    [AvaloniaFact]
    public async Task Ordinary_and_owner_marker_only_process_refuse_before_observer_or_attention()
    {
        foreach (var args in new[] { Array.Empty<string>(), new[] { "--ntilde-terminfo-owner=" + Run } })
        {
            using var mode = PrivateClipboardQaMode.OverrideProcessForTesting(PrivateClipboardQaMode.FromStartup(args, Run, null));
            var registry = new AgentSessionRegistry(); var registration = Register(registry);
            using var service = new AgentHostService(registry);
            var observer = new Observer { Read = (_, _, _) => throw new InvalidOperationException("Must not observe an ordinary process.") };
            service.SetPaneObserver(observer);
            Assert.Equal(AgentHostProtocol.ErrorCodes.NativeChildWaitUnavailable, (await Request(service, Input(registration))).Error?.Code);
            Assert.Equal(0, observer.Calls); Assert.Equal(AgentAttentionTier.Idle, registration.AttentionMachine.Snapshot().Tier);
        }
    }

    [AvaloniaFact]
    public async Task Explicit_QA_read_accepts_exited_original_session_without_act_and_journals_only_fixed_metadata()
    {
        using var startup = new PrivateClipboardQaModeTests(); var qa = startup.Create();
        using var mode = PrivateClipboardQaMode.OverrideProcessForTesting(qa);
        var registry = new AgentSessionRegistry(); var registration = Register(registry);
        var journal = new AgentActivityJournal(); using var service = new AgentHostService(registry, journal: journal);
        service.SetPaneObserver(new Observer { Read = (_, value, _) => Task.FromResult<NativeChildWaitResult?>(Result(value, code: 0)) });
        Assert.False(service.ActEnabled);
        var response = await Request(service, Input(registration)); Assert.Null(response.Error);
        var result = response.Result!.Value.Deserialize(AgentHostJsonContext.Default.NativeChildWaitResult)!;
        Assert.True(result.NativeWaitObserved); Assert.Equal(0, result.ExitCode);
        Assert.Equal(registration.ObservationSession!.Id, result.NativeSessionId);
        Assert.Empty(Assert.IsType<InputStubSession>(registration.ObservationSession).Inputs);
        Assert.Equal(AgentAttentionTier.Watched, registration.AttentionMachine.Snapshot().Tier);
        var entry = Assert.Single(journal.Snapshot()); Assert.Equal("private QA child wait", entry.Target);
        string logged = JsonSerializer.Serialize(entry);
        Assert.DoesNotContain(Run, logged); Assert.DoesNotContain(result.NativeSessionId.ToString(), logged);
        Assert.Null(qa.For(registration)); // The passive method never begins a clipboard transaction.
        Assert.Null(qa.ResolveHostClipboard(() => throw new InvalidOperationException("Never resolve host clipboard.")));
    }

    [AvaloniaFact]
    public async Task Unknown_has_explicit_null_code_and_bootstrap_returns_actual_generation()
    {
        using var startup = new PrivateClipboardQaModeTests(); using var mode = PrivateClipboardQaMode.OverrideProcessForTesting(startup.Create());
        var registry = new AgentSessionRegistry(); var registration = Register(registry, running: true);
        using var service = new AgentHostService(registry);
        service.SetPaneObserver(new Observer { Read = (_, value, _) => Task.FromResult<NativeChildWaitResult?>(Result(value, observed: false)) });
        var response = await Request(service, Input(registration) with { ExpectedNativeSessionId = null }); Assert.Null(response.Error);
        Assert.False(response.Result!.Value.GetProperty("nativeWaitObserved").GetBoolean());
        Assert.Equal(JsonValueKind.Null, response.Result.Value.GetProperty("exitCode").ValueKind);
        Assert.Equal(registration.ObservationSession!.Id, response.Result.Value.GetProperty("nativeSessionId").GetGuid());
    }

    [AvaloniaTheory]
    [InlineData("pid")]
    [InlineData("generation")]
    [InlineData("run")]
    [InlineData("ssh")]
    [InlineData("inactive")]
    public async Task Wrong_binding_or_remote_inactive_identity_never_discloses_success(string defect)
    {
        using var startup = new PrivateClipboardQaModeTests(); using var mode = PrivateClipboardQaMode.OverrideProcessForTesting(startup.Create());
        var registry = new AgentSessionRegistry(); var registration = Register(registry);
        using var service = new AgentHostService(registry);
        var observer = new Observer { Read = (_, value, _) => Task.FromResult<NativeChildWaitResult?>(Result(value)) }; service.SetPaneObserver(observer);
        var input = Input(registration);
        if (defect == "pid") input = input with { ExpectedChildPid = 124 };
        if (defect == "generation") input = input with { ExpectedNativeSessionId = Guid.NewGuid() };
        if (defect == "run") input = input with { RunId = new string('f', 32) };
        if (defect == "ssh") registration.UpdateSnapshot("private", "private", "ssh", true);
        if (defect == "inactive") registration.UpdateSnapshot("private", "private", "local", false);
        Assert.Equal(AgentHostProtocol.ErrorCodes.NativeChildWaitUnavailable, (await Request(service, input)).Error?.Code);
        Assert.Equal(AgentAttentionTier.Idle, registration.AttentionMachine.Snapshot().Tier);
    }

    [AvaloniaTheory]
    [InlineData("session")]
    [InlineData("registration")]
    [InlineData("observer")]
    [InlineData("journalReentry")]
    public async Task Replacement_during_or_after_read_cannot_publish_old_success(string change)
    {
        using var startup = new PrivateClipboardQaModeTests(); using var mode = PrivateClipboardQaMode.OverrideProcessForTesting(startup.Create());
        var registry = new AgentSessionRegistry(); var registration = Register(registry); var original = Result(registration);
        var journal = new AgentActivityJournal(); using var service = new AgentHostService(registry, journal: journal);
        void Replace()
        {
            if (change is "session" or "journalReentry") registration.SetLifecycle(new InputStubSession());
            if (change == "registration") { registry.Unregister(registration.PaneId); registry.Register(new AgentSessionRegistration(registration.PaneId, registration.Buffer, "replacement", "replacement", "local", true)); }
            if (change == "observer") service.SetPaneObserver(null);
        }
        service.SetPaneObserver(new Observer { Read = (_, _, _) => { if (change != "journalReentry") Replace(); return Task.FromResult<NativeChildWaitResult?>(original); } });
        journal.EntryAdded += _ => { if (change == "journalReentry") Replace(); };
        Assert.Equal(AgentHostProtocol.ErrorCodes.NativeChildWaitUnavailable, (await Request(service, Input(registration))).Error?.Code);
    }

    [AvaloniaFact]
    public async Task Caller_cancellation_does_not_publish_a_late_observer_result()
    {
        using var startup = new PrivateClipboardQaModeTests(); using var mode = PrivateClipboardQaMode.OverrideProcessForTesting(startup.Create());
        var registry = new AgentSessionRegistry(); var registration = Register(registry); using var service = new AgentHostService(registry);
        using var cancelled = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<NativeChildWaitResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SetPaneObserver(new Observer { Read = (_, _, _) => { entered.SetResult(); return release.Task; } });
        var read = Request(service, Input(registration), cancelled.Token); await entered.Task;
        cancelled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        release.SetResult(Result(registration));
        Assert.Equal(AgentAttentionTier.Idle, registration.AttentionMachine.Snapshot().Tier);
    }

    [AvaloniaTheory]
    [InlineData("attention", "caller")]
    [InlineData("attention", "deadline")]
    [InlineData("journal", "caller")]
    [InlineData("journal", "deadline")]
    public async Task Notification_reentry_cannot_publish_after_cancellation_or_deadline(string notification, string expiry)
    {
        using var startup = new PrivateClipboardQaModeTests(); using var mode = PrivateClipboardQaMode.OverrideProcessForTesting(startup.Create());
        var registry = new AgentSessionRegistry(); var registration = Register(registry);
        var journal = new AgentActivityJournal(); using var service = new AgentHostService(registry, journal: journal);
        using var cancelled = new CancellationTokenSource(); CancellationToken deadline = default; int callbacks = 0;
        service.SetPaneObserver(new Observer
        {
            Read = (_, value, token) =>
        { deadline = token; return Task.FromResult<NativeChildWaitResult?>(Result(value)); }
        });
        void Expire()
        {
            callbacks++;
            if (expiry == "caller") cancelled.Cancel();
            else Assert.True(deadline.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)), "The existing source-selected read deadline did not expire.");
        }
        if (notification == "attention") registration.AttentionMachine.Changed += _ => Expire();
        else journal.EntryAdded += _ => Expire();
        if (expiry == "caller")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Request(service, Input(registration), cancelled.Token));
        else
            Assert.Equal(AgentHostProtocol.ErrorCodes.NativeChildWaitUnavailable, (await Request(service, Input(registration))).Error?.Code);
        Assert.Equal(1, callbacks);
    }

    [AvaloniaFact]
    public async Task Missing_legacy_observer_and_windowless_identity_have_no_native_wait_fallback()
    {
        using var startup = new PrivateClipboardQaModeTests(); using var mode = PrivateClipboardQaMode.OverrideProcessForTesting(startup.Create());
        var registry = new AgentSessionRegistry(); var registration = Register(registry); using var service = new AgentHostService(registry);
        Assert.Equal(AgentHostProtocol.ErrorCodes.NativeChildWaitUnavailable, (await Request(service, Input(registration))).Error?.Code);
        service.SetPaneObserver(new LegacyObserver());
        Assert.Equal(AgentHostProtocol.ErrorCodes.NativeChildWaitUnavailable, (await Request(service, Input(registration))).Error?.Code);
        Assert.Equal(AgentHostProtocol.ErrorCodes.NativeChildWaitUnavailable,
            (await Request(service, Input(registration) with { PaneId = Guid.NewGuid() })).Error?.Code);
        Assert.Equal(AgentAttentionTier.Idle, registration.AttentionMachine.Snapshot().Tier);
    }

    [AvaloniaFact]
    public async Task Source_selected_read_deadline_refuses_and_ignores_later_completion()
    {
        using var startup = new PrivateClipboardQaModeTests(); using var mode = PrivateClipboardQaMode.OverrideProcessForTesting(startup.Create());
        var registry = new AgentSessionRegistry(); var registration = Register(registry); using var service = new AgentHostService(registry);
        var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<NativeChildWaitResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SetPaneObserver(new Observer { Read = (_, _, token) => { token.Register(() => expired.TrySetResult()); return release.Task; } });
        var pending = Request(service, Input(registration));
        await expired.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(AgentHostProtocol.ErrorCodes.NativeChildWaitUnavailable, (await pending).Error?.Code);
        release.SetResult(Result(registration));
        Assert.Equal(AgentAttentionTier.Idle, registration.AttentionMachine.Snapshot().Tier);
    }
}
