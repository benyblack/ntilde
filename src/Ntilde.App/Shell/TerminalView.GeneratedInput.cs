using Avalonia.Interactivity;

namespace Ntilde.Shell;

public partial class TerminalView
{
    internal bool MatchesGeneratedInputContext(Ntilde.VT.TerminalBuffer buffer, Ntilde.Pty.ITerminalSession? session)
        => ReferenceEquals(_buffer, buffer) && ReferenceEquals(_session, session);

    private bool _generatedInputInProgress;
    private Func<bool>? _generatedInputLease;
    private bool CanApplyGeneratedInput => !_generatedInputInProgress || _generatedInputLease?.Invoke() == true;

    /// <summary>Keep routing and encoding intact while excluding synthetic input from user-command history observers.</summary>
    internal void RaiseGeneratedInput(RoutedEventArgs args, Func<bool> isCurrent)
    {
        if (_generatedInputInProgress) throw new InvalidOperationException("A synthetic event is already being dispatched.");
        _generatedInputInProgress = true;
        _generatedInputLease = isCurrent;
        try { RaiseEvent(args); }
        finally { _generatedInputInProgress = false; _generatedInputLease = null; }
    }
}
