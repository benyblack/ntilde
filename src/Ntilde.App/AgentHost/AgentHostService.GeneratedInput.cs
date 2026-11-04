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
        // Session publication is UI-owned. The executor invokes this fact observer synchronously
        // on that UI thread, making the identity check/marker one UI operation without a new lease.
        void NoteSubmission() => NoteGeneratedSubmissionForSession(registration, session, method);
        GeneratePaneInputResult? result;
        try
        {
            result = await executor.GeneratePaneInputAsync(p, registration, IsCurrent, NoteSubmission, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(AgentHostProtocol.ErrorCodes.GeneratedInputUnavailable, "The generated event deadline expired; dispatch/submission is indeterminate. Never automatically retry.");
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
        if (result == null)
            return Failure(AgentHostProtocol.ErrorCodes.GeneratedInputUnavailable, "The pane or its input lease is unavailable.");
        if (!IsCurrent()) result = WithoutGeneratedInputObservation(result);
        // The ordinary action journal retains method/target/outcome only, never text or encoded bytes.
        return Journaled(request, method, p.PaneId, "synthetic event", Ok(request.Id,
            JsonSerializer.SerializeToElement(result, AgentHostJsonContext.Default.GeneratePaneInputResult)));
    }
    private static void NoteGeneratedSubmissionForSession(AgentSessionRegistration registration, Ntilde.Pty.ITerminalSession? session, string method)
    {
        if (ReferenceEquals(registration.ObservationSession, session)) TryNoteWrote(registration, method);
    }

    /// <summary>Keep completed event facts while withholding geometry/modes for a target no longer current.</summary>
    private static GeneratePaneInputResult WithoutGeneratedInputObservation(GeneratePaneInputResult result) => result with
    {
        ObservationAvailable = false,
        Rows = null,
        Cols = null,
        ViewRows = null,
        ViewCols = null,
        ApplicationKeypad = null,
        MouseTracking = null,
        MouseSgr = null,
        KittyKeyboardFlags = null,
    };
}
