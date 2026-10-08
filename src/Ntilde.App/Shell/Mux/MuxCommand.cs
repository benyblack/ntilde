using Ntilde.Mux.Cli;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Daemon;

namespace Ntilde.Shell.Mux;

/// <summary>
/// <c>ntilde mux serve|ls|kill|kill-server|attach</c>: the App's adapter over
/// <see cref="MuxCli"/>, which holds the verbs (Phase 4 spec §6.4). This keeps the public static
/// surface <c>Program.cs</c> and <c>CliCommandDispatchTests</c> rely on, and supplies what only the
/// App knows: its root, its name in the usage text, its session factory and its console bindings.
/// Exit codes and text are <see cref="MuxCli"/>'s.
/// </summary>
public static class MuxCommand
{
    /// <summary>The App's name for the verbs in usage text.</summary>
    private const string UsagePrefix = "ntilde mux";

    /// <summary>What makes this executable serve: the daemon launcher spawns <c>&lt;exe&gt; mux serve</c> (Phase 4 spec §6.4).</summary>
    internal static readonly IReadOnlyList<string> ServeArguments = ["mux", "serve"];

    /// <summary>Set by Program.cs: PrepareInteractive attached this (GUI) process to its parent's console.</summary>
    internal static bool AttachedToParentConsole { get; set; }

    public static bool IsSupportedCliMode(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Length > 0 && string.Equals(args[0], "mux", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsServe(string[] args) =>
        IsSupportedCliMode(args) && args.Length > 1 && string.Equals(args[1], "serve", StringComparison.OrdinalIgnoreCase);

    public static bool IsAttach(string[] args) =>
        IsSupportedCliMode(args) && args.Length > 1 && string.Equals(args[1], "attach", StringComparison.OrdinalIgnoreCase);

    /// <summary>Verbs that draw on and read from a console: attach, and probe-console, which must set the console up exactly as attach does.</summary>
    public static bool NeedsInteractiveConsole(string[] args) =>
        IsAttach(args) || (IsSupportedCliMode(args) && args.Length > 1 && string.Equals(args[1], "probe-console", StringComparison.OrdinalIgnoreCase));

    /// <param name="rootOverride">Test seam: app-data root. Null uses <see cref="MuxDiscovery.GetRootDirectory"/>.</param>
    public static int Execute(string[] args, TextWriter stdout, TextWriter stderr, string? rootOverride = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        // args[0] is "mux"; MuxCli takes the verb onwards.
        return MuxCli.Execute(args.Length > 0 ? args[1..] : [], stdout, stderr, CreateHost(rootOverride));
    }

    /// <summary>What the App supplies to <see cref="MuxCli"/>: its own root, name, verbs, shells and console bindings.</summary>
    internal static MuxCliHost CreateHost(string? rootOverride) => new()
    {
        Paths = new MuxPaths(rootOverride ?? MuxDiscovery.GetRootDirectory()),
        UsagePrefix = UsagePrefix,
        ServeArguments = ServeArguments,
        // The GUI's own daemon keeps the GUI's session factory, so local behaviour cannot change (Phase 4 spec §6.3).
        SessionFactory = () => DefaultTerminalSessionFactory.Instance,
        Verbs = MuxCliVerbs.Serve | MuxCliVerbs.Ls | MuxCliVerbs.Kill | MuxCliVerbs.KillServer | MuxCliVerbs.Attach | MuxCliVerbs.ProbeConsole,
        // Program.cs skips CliConsoleBindings.Prepare for serve (a daemon must not attach to the
        // launching console); serve --foreground binds it through this.
        PrepareForegroundConsole = CliConsoleBindings.Prepare,
        AttachedToParentConsole = AttachedToParentConsole,
        // The daemon reports this build's version so a later GUI can tell it is from an older one.
        Version = AppVersionInfo.Version,
    };
}
