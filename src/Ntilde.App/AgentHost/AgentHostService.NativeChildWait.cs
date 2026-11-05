using System.Text.Json;
using Ntilde.AgentHost.Contracts;
using Ntilde.Shell.Clipboard;

namespace Ntilde.AgentHost;

public sealed partial class AgentHostService
{
    private async Task<AgentHostResponse> HandleReadNativeChildWaitAsync(AgentHostRequest request, CancellationToken cancellationToken)
    {
        var input = DeserializeParams(request, AgentHostJsonContext.Default.ReadNativeChildWaitParams);
        if (input == null || !NativeChildWaitValidation.IsValid(input))
            return Error(request.Id, AgentHostProtocol.ErrorCodes.MalformedRequest, "A private QA pane, run, child PID and optional native session are required.");
        AgentHostResponse Unavailable() => Error(request.Id, AgentHostProtocol.ErrorCodes.NativeChildWaitUnavailable,
            "A complete current private QA native child-wait observation is unavailable.");
        var mode = PrivateClipboardQaMode.Process;
        if (!mode.IsEnabled || mode.RunId != input.RunId) return Unavailable();
        if (!_registry.TryGet(input.PaneId, out var registration)) return Unavailable();
        var observer = _paneObserver;
        var session = registration.ObservationSession;
        bool IsCurrent() => ReferenceEquals(mode, PrivateClipboardQaMode.Process)
            && mode.IsEnabled && mode.RunId == input.RunId && ReferenceEquals(observer, _paneObserver)
            && _registry.TryGet(input.PaneId, out var current) && ReferenceEquals(registration, current)
            && ReferenceEquals(session, registration.ObservationSession) && registration.Kind == "local" && registration.IsActive;
        if (observer == null || session == null || !IsCurrent()) return Unavailable();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        NativeChildWaitResult? result;
        try
        {
            result = await observer.ReadNativeChildWaitAsync(input, registration, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Unavailable(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return Unavailable(); }
        cancellationToken.ThrowIfCancellationRequested();
        if (deadline.IsCancellationRequested) return Unavailable();
        if (result == null || !IsCurrent() || !NativeChildWaitValidation.IsCoherent(result)
            || result.PaneId != input.PaneId || result.OriginalChildPid != input.ExpectedChildPid
            || result.NativeSessionId != session.Id
            || input.ExpectedNativeSessionId is { } expectedId && result.NativeSessionId != expectedId) return Unavailable();
        TryNoteRead(registration);
        _journal.RecordRead(AgentHostProtocol.Methods.ReadNativeChildWait, input.PaneId, "private QA child wait", "ok");
        if (!IsCurrent()) return Unavailable();
        return Ok(request.Id, JsonSerializer.SerializeToElement(result, AgentHostJsonContext.Default.NativeChildWaitResult));
    }
}
