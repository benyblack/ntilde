using System.Text.Json;
using Ntilde.AgentHost.Contracts;

namespace Ntilde.AgentHost;

public sealed partial class AgentHostService
{
    private async Task<AgentHostResponse> HandleGeneratePaneInputAsync(AgentHostRequest request, CancellationToken cancellationToken)
    {
        const string method = AgentHostProtocol.Methods.GeneratePaneInput;
        GeneratePaneInputParams? p;
        try { p = DeserializeParams(request, AgentHostJsonContext.Default.GeneratePaneInputParams); }
        catch (JsonException) { p = null; }
        AgentHostResponse Failure(string code, string message) => Journaled(request, method, p?.PaneId, "synthetic event", Error(request.Id, code, message));
        if (p == null || !GeneratePaneInputValidation.IsValid(p))
            return Failure(AgentHostProtocol.ErrorCodes.MalformedRequest, "generatePaneInput requires one bounded keypad or mouse event.");
        if (!_actEnabled) return Failure(AgentHostProtocol.ErrorCodes.ActDisabled, "Acting is disabled. Enable Agent access (act), then retry.");
        if (!_registry.TryGet(p.PaneId, out var registration))
            return Failure(AgentHostProtocol.ErrorCodes.SessionNotFound, "No visible pane has the requested paneId.");
        var session = registration.ObservationSession;
        var executor = _actionExecutor;
        bool IsCurrent() => _actEnabled && ReferenceEquals(_actionExecutor, executor)
            && _registry.TryGet(p.PaneId, out var current) && ReferenceEquals(current, registration)
            && ReferenceEquals(registration.ObservationSession, session) && session?.IsProcessRunning == true
            && registration.IsActive && registration.Kind == "local";
        if (registration.Kind != "local" || !IsCurrent())
            return Failure(AgentHostProtocol.ErrorCodes.GeneratedInputUnavailable, "Only the current live direct local pane can receive generated events.");
        if (executor == null) return Failure(AgentHostProtocol.ErrorCodes.ActUnavailable, "The App event executor is unavailable.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        GeneratePaneInputResult? result;
        try
        {
            result = await executor.GeneratePaneInputAsync(p, registration, IsCurrent, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(AgentHostProtocol.ErrorCodes.GeneratedInputUnavailable, "The generated event deadline expired; no deferred event is authorized.");
        }
        catch (OperationCanceledException)
        {
            _journal.Record(method, p.PaneId, "synthetic event", "cancelled");
            throw;
        }
        catch (Exception)
        {
            return Failure(AgentHostProtocol.ErrorCodes.Internal, "The App could not observe the generated event result.");
        }
        if (result == null || !IsCurrent())
            return Failure(AgentHostProtocol.ErrorCodes.GeneratedInputUnavailable, "The pane or its input lease is unavailable.");
        TryNoteWrote(registration, method);
        // The ordinary action journal retains method/target/outcome only, never text or encoded bytes.
        return Journaled(request, method, p.PaneId, "synthetic event", Ok(request.Id,
            JsonSerializer.SerializeToElement(result, AgentHostJsonContext.Default.GeneratePaneInputResult)));
    }
}
