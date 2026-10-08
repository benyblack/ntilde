using Ntilde.Pty;

namespace Ntilde.Shell;

/// <summary>
/// <paramref name="Settings"/> is null in production (MainWindow loads settings.json from disk).
/// A non-null instance bypasses that load entirely: BuildForDesigner supplies fresh defaults so
/// designer previews and test-created windows never read the developer's live settings file,
/// whose contents (e.g. TabStripOrientation) would otherwise leak into layout assertions.
///
/// <paramref name="SessionFactory"/> is null everywhere today, which means
/// <see cref="DefaultTerminalSessionFactory"/>. It exists so a future multiplexer client can be
/// substituted at the composition root rather than inside the pane.
/// </summary>
public sealed record AppServiceBundle(
    StartupOrchestrator Startup,
    CommandAssistServices CommandAssist,
    TerminalSettings? Settings = null,
    ITerminalSessionFactory? SessionFactory = null)
{
    /// <summary>
    /// For the window's startup restore (spec R2): the time no live local daemon session can predate - the boot, or on
    /// Windows the later of the boot and this logon - or null when the OS will not say. A session file saved before it
    /// restores quietly. A seam here, not on the window: the restore runs in the window's constructor.
    /// </summary>
    internal Func<DateTime?> SessionsCannotPredateUtc { get; init; } = Ntilde.Shell.Native.SessionStartBoundary.Read;
}
