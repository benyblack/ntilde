using Ntilde.Mux.Daemon;
using Ntilde.Pty;

namespace Ntilde.Mux.Cli;

/// <summary>
/// What an executable supplies to host the mux verbs (Phase 4 spec §6.2): the App's
/// <c>ntilde mux</c> adapter (spec §6.4) and the standalone <c>ntilde-mux</c>. Everything the verbs
/// would otherwise take from the App - its paths, its name, its shells, its console - comes from here.
/// </summary>
public sealed class MuxCliHost
{
    /// <summary>The daemon's root: where <c>serve</c> listens and every other verb looks.</summary>
    public required MuxPaths Paths { get; init; }

    /// <summary>How the usage text names the command: <c>"ntilde mux"</c> or <c>"ntilde-mux"</c>.</summary>
    public required string UsagePrefix { get; init; }

    /// <summary>What this executable is given to serve: <c>["mux","serve"]</c> or <c>["serve"]</c>.</summary>
    public required IReadOnlyList<string> ServeArguments { get; init; }

    /// <summary>The daemon's shells. The GUI's own daemon keeps the GUI's factory, so local behaviour cannot change (spec §6.3).</summary>
    public required Func<ITerminalSessionFactory> SessionFactory { get; init; }

    /// <summary>The verbs this executable offers; any other prints the usage and exits 2.</summary>
    public required MuxCliVerbs Verbs { get; init; }

    /// <summary>
    /// Binds a console for <c>serve --foreground</c>: the executable binds none for <c>serve</c>, since a
    /// daemon must not attach to the launching console. Null: there is nothing to bind.
    /// </summary>
    public Action? PrepareForegroundConsole { get; init; }

    /// <summary>The executable attached to its parent's console for <c>attach</c> (Windows): the keyboard may be shared.</summary>
    public bool AttachedToParentConsole { get; init; }

    /// <summary>The GUI's executable, for <c>attach</c>'s console hint (removed with the <c>cmd /c</c> hint, spec §11.4).</summary>
    public bool IsGuiExecutable { get; init; }

    /// <summary>This build's version, for the <c>version</c> verb.</summary>
    public string Version { get; init; } = "";
}
