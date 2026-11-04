using System.Text.Json;
using Ntilde.AgentHost.Contracts;

namespace Ntilde.AgentHost;

public sealed partial class AgentHostService
{
    private async Task<AgentHostResponse> HandleClipboardFixtureAsync(AgentHostRequest request, CancellationToken cancellationToken)
    {
        const string method = AgentHostProtocol.Methods.ClipboardFixture;
        ClipboardFixtureParams? input;
        try { input = DeserializeParams(request, AgentHostJsonContext.Default.ClipboardFixtureParams); }
        catch (JsonException) { input = null; }
        AgentHostResponse Failure(string code, string message)
            => Journaled(request, method, input?.PaneId, "private QA fixture", Error(request.Id, code, message));
        if (input == null || !ClipboardFixtureValidation.IsValid(input))
            return Failure(AgentHostProtocol.ErrorCodes.MalformedRequest, "A bounded private clipboard fixture operation is required.");
        if (!_actEnabled) return Failure(AgentHostProtocol.ErrorCodes.ActDisabled, "Acting is disabled.");
        if (!_registry.TryGet(input.PaneId, out var registration))
            return Failure(AgentHostProtocol.ErrorCodes.SessionNotFound, "No visible pane has the requested paneId.");
        var session = registration.ObservationSession;
        var executor = _actionExecutor;
        bool IsCurrent() => _actEnabled && ReferenceEquals(_actionExecutor, executor)
            && _registry.TryGet(input.PaneId, out var current) && ReferenceEquals(current, registration)
            && ReferenceEquals(registration.ObservationSession, session) && session?.IsProcessRunning == true
            && registration.IsActive && registration.Kind == "local";
        if (executor == null || !IsCurrent())
            return Failure(AgentHostProtocol.ErrorCodes.ActUnavailable, "The current live direct local QA pane is unavailable.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        ClipboardFixtureResult? result;
        try
        {
            result = await executor.ClipboardFixtureAsync(input, registration, IsCurrent, deadline.Token)
                .WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(AgentHostProtocol.ErrorCodes.ActUnavailable, "Private fixture operation deadline expired; completion is unknown.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return Failure(AgentHostProtocol.ErrorCodes.Internal, "The private fixture result is unavailable.");
        }
        if (result == null) return Failure(AgentHostProtocol.ErrorCodes.ActUnavailable, "Private QA fixture activation or binding is unavailable.");
        // Only fixed private seeds and explicitly declared synthetic nonces can appear in this DTO.
        // The ordinary journal stores no nonce, clipboard payload, run marker or encoded bytes.
        return Journaled(request, method, input.PaneId, "private QA fixture", Ok(request.Id,
            JsonSerializer.SerializeToElement(result, AgentHostJsonContext.Default.ClipboardFixtureResult)));
    }
}
