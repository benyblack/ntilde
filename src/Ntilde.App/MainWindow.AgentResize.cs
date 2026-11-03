using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Ntilde.AgentHost;
using Ntilde.Controls;
using Ntilde.Pty;
using Ntilde.Shell;

namespace Ntilde;

public partial class MainWindow
{
    private bool _agentPaneResizeInProgress;

    async Task<AgentPaneResizeResult?> IAgentActionExecutor.ResizePaneAsync(Guid paneId, int cols, int rows, CancellationToken cancellationToken)
    {
        var operation = await Dispatcher.InvokeAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pane = _paneOwnerTab.Keys.FirstOrDefault(candidate => candidate.PaneId == paneId);
            if (_agentPaneResizeInProgress || pane == null || !CanAgentResizePane(pane)) return null;
            var view = pane.TermView;
            var layout = new AgentPaneResizeLayout(ClientSize, view.Bounds.Size,
                new Size(view.Metrics.CellWidth, view.Metrics.CellHeight),
                new Size(MinWidth, MinHeight), new Size(MaxWidth, MaxHeight));
            if (!AgentPaneResizeGeometry.TryGetClientSize(layout, cols, rows, out var targetSize)) return null;

            _agentPaneResizeInProgress = true;
            try { return await new AgentPaneResizeOperation(this, pane, cols, rows, cancellationToken).RunAsync(targetSize); }
            finally { _agentPaneResizeInProgress = false; }
        }, DispatcherPriority.Normal, cancellationToken);
        return await operation.ConfigureAwait(false);
    }

    private bool CanAgentResizePane(TerminalPane pane)
    {
        if (!IsVisible || !pane.IsEffectivelyVisible || !ReferenceEquals(TopLevel.GetTopLevel(pane), this) ||
            !CanResize || WindowState != WindowState.Normal || _isFocusMode ||
            !ReferenceEquals(_currentPane, pane) || pane.Profile?.Type != ConnectionType.Local ||
            pane.Session?.IsProcessRunning != true || pane.TermView.DefersBufferResizeToSession ||
            !_paneOwnerTab.TryGetValue(pane, out var tab))
        {
            return false;
        }
        return ReferenceEquals(this.FindControl<TabControl>("Tabs")?.SelectedItem, tab)
            && _paneOwnerTab.Count(pair => ReferenceEquals(pair.Value, tab)) == 1;
    }

    /// <summary>UI-thread lease for one bounded layout operation; it never activates tabs or directly resizes VT.</summary>
    private sealed class AgentPaneResizeOperation
    {
        private readonly MainWindow _window;
        private readonly TerminalPane _pane;
        private readonly TerminalView _view;
        private readonly Ntilde.VT.TerminalBuffer _buffer;
        private readonly ITerminalSession _session;
        private readonly int _cols;
        private readonly int _rows;
        private readonly CancellationToken _cancellationToken;
        private readonly TaskCompletionSource<AgentPaneResizeResult?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal AgentPaneResizeOperation(MainWindow window, TerminalPane pane, int cols, int rows, CancellationToken cancellationToken)
        {
            _window = window; _pane = pane; _view = pane.TermView;
            _buffer = pane.Buffer!; _session = pane.Session!;
            _cols = cols; _rows = rows; _cancellationToken = cancellationToken;
        }

        internal async Task<AgentPaneResizeResult?> RunAsync(Size targetSize)
        {
            _view.ResizeDispatchCompleted += OnResizeCompleted;
            _window.Closed += OnClosed;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var cancelled = _cancellationToken.Register(() => _completion.TrySetCanceled(_cancellationToken));
            using var expired = deadline.Token.Register(() => _window.Dispatcher.Post(CompletePartial, DispatcherPriority.Background));
            try
            {
                TryCompleteExact();
                if (!_completion.Task.IsCompleted)
                {
                    _window.Width = targetSize.Width;
                    _window.Height = targetSize.Height;
                    _window.Dispatcher.Post(TryCompleteExact, DispatcherPriority.Background);
                }
                return await _completion.Task;
            }
            finally
            {
                _view.ResizeDispatchCompleted -= OnResizeCompleted;
                _window.Closed -= OnClosed;
            }
        }

        private void OnResizeCompleted(int cols, int rows)
            => _window.Dispatcher.Post(TryCompleteExact, DispatcherPriority.Background);

        private void OnClosed(object? sender, EventArgs args) => _completion.TrySetResult(null);

        private bool TrySnapshot(out AgentPaneResizeResult? observed)
        {
            observed = null;
            if (!_window.CanAgentResizePane(_pane) || !ReferenceEquals(_pane.Buffer, _buffer) || !ReferenceEquals(_pane.Session, _session))
                return true;
            int viewCols = _view.Cols, viewRows = _view.Rows;
            var delivered = _view.LastCompletedResizeDispatch;
            if (!AgentPaneResizeObservation.TryRead(_buffer, viewCols, viewRows, delivered, out var actual)) return false;
            observed = actual;
            return true;
        }

        private void TryCompleteExact()
        {
            if (_completion.Task.IsCompleted) return;
            if (!TrySnapshot(out var observed)) return;
            if (observed == null || observed.Value.LayoutSettled && observed.Value.Cols == _cols && observed.Value.Rows == _rows)
                _completion.TrySetResult(observed);
        }

        private void CompletePartial()
        {
            if (_completion.Task.IsCompleted) return;
            TrySnapshot(out var observed);
            _completion.TrySetResult(observed);
        }
    }
}
