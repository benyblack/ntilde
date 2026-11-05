using Avalonia.Controls;
using Avalonia.Threading;
using Ntilde.AgentHost;
using Ntilde.AgentHost.Contracts;
using Ntilde.Pty;
using Ntilde.Shell;
using Ntilde.Shell.Clipboard;

namespace Ntilde;

public partial class MainWindow
{
    async Task<NativeChildWaitResult?> IAgentPaneObserver.ReadNativeChildWaitAsync(ReadNativeChildWaitParams input,
        AgentSessionRegistration registration, CancellationToken cancellationToken)
        => await Dispatcher.InvokeAsync(() => ReadNativeChildWait(input, registration, cancellationToken),
            DispatcherPriority.Background, cancellationToken);

    private NativeChildWaitResult? ReadNativeChildWait(ReadNativeChildWaitParams input,
        AgentSessionRegistration registration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!NativeChildWaitValidation.IsValid(input)) return null;
        var pane = _paneOwnerTab.Keys.FirstOrDefault(candidate => candidate.PaneId == input.PaneId);
        if (pane == null || pane.Session is not RustPtySession session) return null;
        var mode = pane.PrivateClipboardQa;
        bool IsCurrent() => mode.IsEnabled && mode.RunId == input.RunId && ReferenceEquals(mode, PrivateClipboardQaMode.Process)
            && registration.Kind == "local" && registration.IsActive && pane.IsActivePane
            && IsVisible && WindowState == WindowState.Normal && !_isFocusMode && pane.IsEffectivelyVisible
            && ReferenceEquals(TopLevel.GetTopLevel(pane), this) && ReferenceEquals(_currentPane, pane)
            && pane.Profile?.Type != ConnectionType.SSH && !pane.TermView.DefersBufferResizeToSession
            && pane.IsCurrentAgentRegistration(registration) && ReferenceEquals(pane.Buffer, registration.Buffer)
            && ReferenceEquals(pane.Session, session) && ReferenceEquals(registration.ObservationSession, session)
            && _paneOwnerTab.TryGetValue(pane, out var tab)
            && ReferenceEquals(this.FindControl<TabControl>("Tabs")?.SelectedItem, tab)
            && _paneOwnerTab.Count(pair => ReferenceEquals(pair.Value, tab)) == 1;
        if (!IsCurrent()) return null;
        var snapshot = session.ReadNativeChildWaitObservation();
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot is not { } value || !IsCurrent() || value.OriginalChildPid != input.ExpectedChildPid
            || input.ExpectedNativeSessionId is { } expectedId && value.NativeSessionId != expectedId) return null;
        return new NativeChildWaitResult
        {
            PaneId = input.PaneId,
            NativeSessionId = value.NativeSessionId,
            OriginalChildPid = value.OriginalChildPid,
            NativeWaitObserved = value.NativeWaitObserved,
            ExitCode = value.ExitCode
        };
    }
}
