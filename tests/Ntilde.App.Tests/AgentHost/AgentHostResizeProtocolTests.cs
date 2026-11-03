using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ntilde.AgentHost;
using Ntilde.AgentHost.Contracts;

namespace Ntilde.AppTests.AgentHost;

/// <summary>Permission and admission tests for controlled visible-pane resizing.</summary>
public sealed class AgentHostResizeProtocolTests
{
    private static AgentHostService Service(AgentSessionRegistry registry, AgentActivityJournal journal)
        => new(registry, AgentHostTestEndpoint.CreateEndpoint(System.IO.Path.GetTempPath()), System.IO.Path.GetTempPath(), null, journal);

    private static AgentSessionRegistration Register(AgentSessionRegistry registry, string kind = "local")
    {
        var registration = new AgentSessionRegistration(Guid.NewGuid(), new Ntilde.VT.TerminalBuffer(80, 24),
            "title", "Profile", kind, isActive: true);
        registration.SetLifecycle(new InputStubSession());
        registry.Register(registration);
        return registration;
    }

    private static string Request(Guid paneId, int rows = 20, int cols = 70)
    {
        string parameters = $"\"paneId\":\"{paneId}\",\"rows\":{rows},\"cols\":{cols}";
        return "{\"v\":1,\"id\":1,\"method\":\"resizePane\",\"params\":{" + parameters + "}}";
    }

    private static AgentHostResponse Handle(AgentHostService service, string line)
        => service.HandleRequestLineAsync(line, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    [Fact]
    public void Resize_requires_the_separate_act_opt_in_and_journals_the_denial()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry);
        var journal = new AgentActivityJournal();
        using var service = Service(registry, journal);

        var response = Handle(service, Request(registration.PaneId));

        Assert.Equal(AgentHostProtocol.ErrorCodes.ActDisabled, response.Error?.Code);
        var entry = Assert.Single(journal.Snapshot());
        Assert.Equal("resizePane", entry.Method);
        Assert.Equal(AgentHostProtocol.ErrorCodes.ActDisabled, entry.Outcome);
    }

    [Theory]
    [InlineData(0, 70)]
    [InlineData(-1, 70)]
    [InlineData(129, 70)]
    [InlineData(20, 0)]
    [InlineData(20, -1)]
    [InlineData(20, 257)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void Invalid_dimensions_are_rejected_before_UI_work(int rows, int cols)
    {
        var journal = new AgentActivityJournal();
        using var service = Service(new AgentSessionRegistry(), journal);
        service.ActEnabled = true;

        var response = Handle(service, Request(Guid.NewGuid(), rows, cols));

        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, response.Error?.Code);
        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, Assert.Single(journal.Snapshot()).Outcome);
    }

    [Fact]
    public void Unknown_pane_is_not_found_even_with_act_enabled()
    {
        using var service = Service(new AgentSessionRegistry(), new AgentActivityJournal());
        service.ActEnabled = true;

        var response = Handle(service, Request(Guid.NewGuid()));

        Assert.Equal(AgentHostProtocol.ErrorCodes.SessionNotFound, response.Error?.Code);
    }

    [Fact]
    public void A_visible_local_registration_requires_the_UI_executor()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry);
        using var service = Service(registry, new AgentActivityJournal());
        service.ActEnabled = true;

        var response = Handle(service, Request(registration.PaneId));

        Assert.Equal(AgentHostProtocol.ErrorCodes.ActUnavailable, response.Error?.Code);
    }

    [Fact]
    public void Remote_registration_cannot_be_resized_through_the_local_window_operation()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry, "ssh");
        using var service = Service(registry, new AgentActivityJournal());
        service.ActEnabled = true;

        var response = Handle(service, Request(registration.PaneId));

        Assert.Equal("resizeUnavailable", response.Error?.Code);
    }
    [Theory]
    [InlineData(20, 70, true, true)]
    [InlineData(21, 70, true, false)]
    [InlineData(20, 71, true, false)]
    [InlineData(20, 70, false, false)]
    public void Reply_reports_measured_geometry_and_never_substitutes_the_requested_size(int rows, int cols, bool settled, bool exact)
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry);
        var journal = new AgentActivityJournal();
        using var service = Service(registry, journal);
        service.ActEnabled = true;
        var executor = new StubExecutor
        {
            OnResize = (_, _, _, _) => Task.FromResult<AgentPaneResizeResult?>(new(rows, cols, settled)),
        };
        service.SetActionExecutor(executor);

        var response = Handle(service, Request(registration.PaneId));

        Assert.Null(response.Error);
        var result = response.Result!.Value.Deserialize(AgentHostJsonContext.Default.ResizePaneResult)!;
        Assert.Equal((rows, cols, settled, exact), (result.Rows, result.Cols, result.LayoutSettled, result.Exact));
        Assert.Equal((registration.PaneId, 70, 20), executor.LastResize);
        Assert.Equal(1, executor.ResizeCalls);
        Assert.Equal("ok", Assert.Single(journal.Snapshot()).Outcome);
    }

    [Fact]
    public void Unsupported_executor_reports_unavailable_without_fabricated_geometry()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry);
        using var service = Service(registry, new AgentActivityJournal());
        service.ActEnabled = true;
        service.SetActionExecutor(new StubExecutor());

        var response = Handle(service, Request(registration.PaneId));

        Assert.Equal("resizeUnavailable", response.Error?.Code);
        Assert.Null(response.Result);
    }

    [Theory]
    [InlineData("rows")]
    [InlineData("cols")]
    public void Missing_required_dimension_is_malformed_and_does_not_call_UI(string field)
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry);
        using var service = Service(registry, new AgentActivityJournal());
        service.ActEnabled = true;
        var executor = new StubExecutor();
        service.SetActionExecutor(executor);
        string line = Request(registration.PaneId);
        string missing = field == "rows" ? ",\"rows\":20" : ",\"cols\":70";

        var response = Handle(service, line.Replace(missing, "", StringComparison.Ordinal));

        Assert.Equal(AgentHostProtocol.ErrorCodes.MalformedRequest, response.Error?.Code);
        Assert.Equal(0, executor.ResizeCalls);
    }

    [Fact]
    public async Task Cancellation_is_journaled_once_and_propagates_to_the_connection()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry);
        var journal = new AgentActivityJournal();
        using var service = Service(registry, journal);
        service.ActEnabled = true;
        service.SetActionExecutor(new StubExecutor
        {
            OnResize = (_, _, _, _) => Task.FromCanceled<AgentPaneResizeResult?>(new CancellationToken(true)),
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.HandleRequestLineAsync(Request(registration.PaneId), TestContext.Current.CancellationToken));

        Assert.Equal("cancelled", Assert.Single(journal.Snapshot()).Outcome);
    }

    [Fact]
    public void Executor_failure_is_journaled_once_without_fabricated_geometry_or_exception_details()
    {
        var registry = new AgentSessionRegistry();
        var registration = Register(registry);
        var journal = new AgentActivityJournal();
        using var service = Service(registry, journal);
        service.ActEnabled = true;
        service.SetActionExecutor(new StubExecutor
        {
            OnResize = (_, _, _, _) => Task.FromException<AgentPaneResizeResult?>(new InvalidOperationException("private diagnostic")),
        });

        var response = Handle(service, Request(registration.PaneId));

        Assert.Equal(AgentHostProtocol.ErrorCodes.Internal, response.Error?.Code);
        Assert.DoesNotContain("private diagnostic", response.Error!.Message, StringComparison.Ordinal);
        Assert.Null(response.Result);
        Assert.Equal(AgentHostProtocol.ErrorCodes.Internal, Assert.Single(journal.Snapshot()).Outcome);
    }

}
