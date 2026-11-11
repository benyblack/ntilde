using Avalonia.Controls;
using Avalonia.Threading;
using Ntilde.AgentHost;
using Ntilde.AgentHost.Contracts;
using Ntilde.Shell;

namespace Ntilde;

public partial class MainWindow
{
    /// <summary>Observe actual control properties on this window's dispatcher without activating or changing anything.</summary>
    async Task<PaneMetadataResult?> IAgentPaneObserver.ReadPaneMetadataAsync(AgentSessionRegistration registration, CancellationToken cancellationToken)
        => await Dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ReadPaneMetadata(registration);
        }, DispatcherPriority.Background, cancellationToken);

    private PaneMetadataResult? ReadPaneMetadata(AgentSessionRegistration registration)
    {
        var pane = _paneOwnerTab.Keys.FirstOrDefault(candidate => candidate.PaneId == registration.PaneId);
        if (pane == null) return null;
        if (!IsVisible || !pane.IsEffectivelyVisible || !ReferenceEquals(TopLevel.GetTopLevel(pane), this)
            || !ReferenceEquals(_currentPane, pane) || pane.Profile?.Type == ConnectionType.SSH
            || pane.TermView.DefersBufferResizeToSession || pane.Session?.IsProcessRunning != true
            || !ReferenceEquals(pane.Session, registration.ObservationSession) || !ReferenceEquals(pane.Buffer, registration.Buffer)
            || !_paneOwnerTab.TryGetValue(pane, out var tab) || !_tabStateByTab.TryGetValue(tab, out var state)) return null;
        var text = FindTabHeaderTextBlock(tab.Header);
        var toast = this.FindControl<Border>("RecordingToast");
        var toastTitle = this.FindControl<TextBlock>("RecordingToastTitle");
        var toastMessage = this.FindControl<TextBlock>("RecordingToastMessage");
        if (text == null || toast == null || toastTitle == null || toastMessage == null) return null;
        if (!AgentPaneMetadataCapture.TryRead(registration.Buffer, out var buffer, pane.Parser) || buffer == null) return null;
        var bar = FindTabHeaderDescendant<ProgressBar>(tab.Header, "TabProgressBar");
        var result = new PaneMetadataResult
        {
            PaneId = pane.PaneId,
            OscTitle = pane.CurrentOscTitle,
            WorkingDirectory = pane.CurrentWorkingDirectory,
            DisplayTitle = pane.GetBaseTabTitle(),
            Buffer = buffer,
            Presentation = new PanePresentationMetadata
            {
                WindowActive = IsActive,
                WindowVisible = IsVisible,
                PaneActive = pane.IsActivePane,
                FocusMode = _isFocusMode,
                NotificationsEnabled = _settings.Osc9NotificationsEnabled,
                ProgressEnabled = _settings.Osc9ProgressReportingEnabled,
                TabText = text.Text ?? string.Empty,
                TabTextVisible = text.IsEffectivelyVisible,
                ProgressOwner = state.ProgressPane?.PaneId,
                ProgressKind = state.Progress?.Kind.ToString(),
                ProgressPercent = state.Progress?.Percent,
                ProgressBar = bar == null ? null : new PaneProgressBarMetadata { Visible = bar.IsEffectivelyVisible, Indeterminate = bar.IsIndeterminate, Value = bar.Value },
                ToastVisible = toast.IsEffectivelyVisible,
                ToastTitle = toastTitle.Text ?? string.Empty,
                ToastMessage = toastMessage.Text ?? string.Empty,
            },
        };
        var fields = new[] { result.OscTitle, result.WorkingDirectory, buffer.RequestedIconName, result.DisplayTitle, result.Presentation.TabText,
            result.Presentation.ToastTitle, result.Presentation.ToastMessage }
            .Concat(buffer.Links.SelectMany(link => new[] { link.Uri, link.Id }));
        return AgentPaneMetadataCapture.TextFits(fields) ? result : null;
    }
}
