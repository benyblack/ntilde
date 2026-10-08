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
    /// The window's wall clock (UTC) for its startup restore, which compares the session file's save time with the
    /// machine's boot time (spec R2). A seam here, not on the window: the restore runs in the window's constructor.
    /// </summary>
    internal Func<DateTime> UtcNow { get; init; } = static () => DateTime.UtcNow;

    /// <summary>Milliseconds since the machine booted, for the same restore: with <see cref="UtcNow"/>, the boot time.</summary>
    internal Func<long> TickCount64 { get; init; } = static () => Environment.TickCount64;
}
