using System.Text.Json;
using Ntilde.AgentHost;
using Ntilde.AgentHost.Contracts;

namespace Ntilde.AppTests.AgentHost;

public sealed class AgentHostClipboardFixtureProtocolTests
{
    private const string Run = "0123456789abcdef0123456789abcdef";
    private static string Request(Guid paneId, string fields = "\"operation\":\"begin\"")
        => "{\"v\":1,\"id\":1,\"method\":\"clipboardFixture\",\"params\":{\"paneId\":\"" + paneId
            + "\",\"runId\":\"" + Run + "\"," + fields + "}}";
    private static (AgentHostService Service, AgentSessionRegistry Registry, AgentSessionRegistration Registration, AgentActivityJournal Journal, StubExecutor Executor) Setup(bool act = true, string kind = "local", bool active = true, bool running = true)
    {
        var registry = new AgentSessionRegistry();
        var registration = new AgentSessionRegistration(Guid.NewGuid(), new Ntilde.VT.TerminalBuffer(80, 24), "title", "Profile", kind, active);
        registration.SetLifecycle(new InputStubSession(running));
        registry.Register(registration);
        var journal = new AgentActivityJournal();
        var service = new AgentHostService(registry, AgentHostTestEndpoint.CreateEndpoint(Path.GetTempPath()), Path.GetTempPath(), null, journal);
        service.ActEnabled = act;
        var executor = new StubExecutor { OnClipboardFixture = (_, _, _, _) => Task.FromResult<ClipboardFixtureResult?>(new() { FixtureId = Guid.NewGuid(), State = "active" }) };
        service.SetActionExecutor(executor);
        return (service, registry, registration, journal, executor);
    }

    [Fact]
    public async Task Default_permission_denies_without_dispatch()
    {
        var s = Setup(act: false); using var service = s.Service;
        var response = await service.HandleRequestLineAsync(Request(s.Registration.PaneId), TestContext.Current.CancellationToken);
        Assert.Equal(AgentHostProtocol.ErrorCodes.ActDisabled, response.Error?.Code);
        Assert.Equal(0, s.Executor.ClipboardFixtureCalls);
    }

    [Theory]
    [InlineData("\"operation\":\"begin\",\"value\":\"personal text\"")]
    [InlineData("\"operation\":\"seed\",\"value\":\"personal text\"")]
    [InlineData("\"operation\":\"read\"")]
    [InlineData("\"operation\":\"native\"")]
    [InlineData("\"operation\":\"begin\",\"selector\":\"any\"")]
    public async Task Arbitrary_values_unknown_selectors_and_unbound_operations_are_rejected(string fields)
    {
        var s = Setup(); using var service = s.Service;
        var response = await service.HandleRequestLineAsync(Request(s.Registration.PaneId, fields), TestContext.Current.CancellationToken);
        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, response.Error?.Code);
        Assert.Equal(0, s.Executor.ClipboardFixtureCalls);
        Assert.Equal("private QA fixture", Assert.Single(s.Journal.Snapshot()).Target);
    }

    [Theory]
    [InlineData("ssh", true, true)]
    [InlineData("local", false, true)]
    [InlineData("local", true, false)]
    public async Task Remote_inactive_or_dead_panes_cannot_dispatch(string kind, bool active, bool running)
    {
        var s = Setup(kind: kind, active: active, running: running); using var service = s.Service;
        var response = await service.HandleRequestLineAsync(Request(s.Registration.PaneId), TestContext.Current.CancellationToken);
        Assert.Equal(AgentHostProtocol.ErrorCodes.ActUnavailable, response.Error?.Code);
        Assert.Equal(0, s.Executor.ClipboardFixtureCalls);
    }

    [Fact]
    public async Task Declared_nonce_never_enters_the_action_journal()
    {
        var s = Setup(); using var service = s.Service;
        const string value = "terminfo-osc52-01234567-89ab-cdef-0123-456789abcdef";
        var response = await service.HandleRequestLineAsync(Request(s.Registration.PaneId,
            "\"operation\":\"seed\",\"fixtureId\":\"" + Guid.NewGuid() + "\",\"value\":\"" + value + "\""), TestContext.Current.CancellationToken);
        Assert.Null(response.Error);
        var dto = response.Result!.Value.Deserialize(AgentHostJsonContext.Default.ClipboardFixtureResult);
        Assert.Equal("active", dto?.State);
        Assert.DoesNotContain(value, JsonSerializer.Serialize(s.Journal.Snapshot()));
        Assert.DoesNotContain(Run, JsonSerializer.Serialize(s.Journal.Snapshot()));
        Assert.Equal(1, s.Executor.ClipboardFixtureCalls);
    }

    [Fact]
    public async Task Restore_uses_the_same_act_lease_and_serializes_actual_private_storage_hash()
    {
        var s = Setup(); using var service = s.Service;
        string hash = new('a', 64);
        s.Executor.OnClipboardFixture = (input, _, current, _) =>
        {
            Assert.Equal("restore", input.Operation); Assert.Null(input.Value); Assert.True(current());
            return Task.FromResult<ClipboardFixtureResult?>(new()
            { FixtureId = input.FixtureId!.Value, State = "active", Restored = true, Complete = false, ValueSha256 = hash });
        };
        var response = await service.HandleRequestLineAsync(Request(s.Registration.PaneId,
            "\"operation\":\"restore\",\"fixtureId\":\"" + Guid.NewGuid() + "\""), TestContext.Current.CancellationToken);
        Assert.Null(response.Error);
        var restored = response.Result!.Value.Deserialize(AgentHostJsonContext.Default.ClipboardFixtureResult);
        Assert.True(restored?.Restored); Assert.False(restored?.Complete);
        Assert.Equal(hash, restored?.ValueSha256);
        Assert.DoesNotContain(hash, JsonSerializer.Serialize(s.Journal.Snapshot()));
    }

    [Fact]
    public async Task Caller_cancellation_is_journaled_without_nonce_or_run_data_and_still_propagates()
    {
        var s = Setup(); using var service = s.Service;
        using var cancelled = new CancellationTokenSource();
        s.Executor.OnClipboardFixture = (_, _, _, token) =>
        {
            cancelled.Cancel();
            return Task.FromCanceled<ClipboardFixtureResult?>(token);
        };
        const string nonce = "terminfo-osc52-01234567-89ab-cdef-0123-456789abcdef";
        string request = Request(s.Registration.PaneId, "\"operation\":\"seed\",\"fixtureId\":\"" + Guid.NewGuid()
            + "\",\"value\":\"" + nonce + "\"");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.HandleRequestLineAsync(request, cancelled.Token));
        var entry = Assert.Single(s.Journal.Snapshot());
        Assert.Equal("clipboardFixture", entry.Method);
        Assert.Equal(s.Registration.PaneId, entry.PaneId);
        Assert.Equal("private QA fixture", entry.Target);
        Assert.Equal("cancelled", entry.Outcome);
        Assert.DoesNotContain(nonce, JsonSerializer.Serialize(entry));
        Assert.DoesNotContain(Run, JsonSerializer.Serialize(entry));
    }

    [Theory]
    [InlineData("registration")]
    [InlineData("session")]
    [InlineData("permission")]
    [InlineData("executor")]
    public async Task Executor_lease_observes_identity_and_permission_loss(string change)
    {
        var s = Setup(); using var service = s.Service;
        s.Executor.OnClipboardFixture = (_, _, guard, _) =>
        {
            Assert.True(guard());
            if (change == "registration") s.Registry.Unregister(s.Registration.PaneId);
            if (change == "session") s.Registration.SetLifecycle(new InputStubSession(true));
            if (change == "permission") service.ActEnabled = false;
            if (change == "executor") service.SetActionExecutor(null);
            Assert.False(guard());
            return Task.FromResult<ClipboardFixtureResult?>(null);
        };
        var response = await service.HandleRequestLineAsync(Request(s.Registration.PaneId), TestContext.Current.CancellationToken);
        Assert.Equal(AgentHostProtocol.ErrorCodes.ActUnavailable, response.Error?.Code);
    }
}
