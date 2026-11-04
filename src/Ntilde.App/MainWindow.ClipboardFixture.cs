using Avalonia.Threading;
using Ntilde.AgentHost;
using Ntilde.AgentHost.Contracts;

namespace Ntilde;

public partial class MainWindow
{
    async Task<ClipboardFixtureResult?> IAgentActionExecutor.ClipboardFixtureAsync(ClipboardFixtureParams input,
        AgentSessionRegistration registration, Func<bool> isCurrent, CancellationToken cancellationToken)
    {
        if (!ClipboardFixtureValidation.IsValid(input)) return null;
        return await Dispatcher.InvokeAsync(() => ApplyClipboardFixture(input, registration, isCurrent, cancellationToken),
            DispatcherPriority.Normal, cancellationToken);
    }

    private ClipboardFixtureResult? ApplyClipboardFixture(ClipboardFixtureParams input,
        AgentSessionRegistration registration, Func<bool> isCurrent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pane = _paneOwnerTab.Keys.FirstOrDefault(candidate => candidate.PaneId == input.PaneId);
        if (pane == null || !CanAgentResizePane(pane) || !pane.IsCurrentAgentRegistration(registration)) return null;
        var mode = pane.PrivateClipboardQa;
        var parser = pane.Parser;
        var session = registration.ObservationSession;
        bool BoundIdentity() => isCurrent() && pane.IsCurrentAgentRegistration(registration)
            && ReferenceEquals(pane.Buffer, registration.Buffer) && ReferenceEquals(pane.Parser, parser)
            && ReferenceEquals(pane.Session, session) && ReferenceEquals(registration.ObservationSession, session);
        if (!mode.IsEnabled || mode.RunId != input.RunId || parser == null || !BoundIdentity()) return null;
        cancellationToken.ThrowIfCancellationRequested();
        if (input.Operation == "begin") return mode.Begin(input.RunId, pane, BoundIdentity)?.Observe();
        return mode.For(pane)?.Apply(input);
    }
}
