namespace Ntilde.Shell.Mux;

/// <summary>A pane-close decision: the shared-close prompt's three buttons, and the plain close/keep answer.</summary>
internal enum SharedCloseChoice
{
    Cancel,
    Close,
    Detach,
}
