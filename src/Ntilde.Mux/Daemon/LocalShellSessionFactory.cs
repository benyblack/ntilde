using Ntilde.Pty;

namespace Ntilde.Mux.Daemon;

/// <summary>
/// The remote daemon's <see cref="ITerminalSessionFactory"/> (Phase 4 spec §6.3): a
/// <see cref="RustPtySession"/> over the request. An empty command means "the default login shell"
/// (spec §3), a <c>~</c> working directory means <c>$HOME</c>, and SSH is refused: <c>MuxServer</c>
/// never asks for it. The GUI's own daemon keeps the App's <c>DefaultTerminalSessionFactory</c>.
/// </summary>
public sealed class LocalShellSessionFactory : ITerminalSessionFactory
{
    public static readonly LocalShellSessionFactory Instance = new();

    private readonly string? _agentLinkPath;

    public LocalShellSessionFactory() : this(null) { }

    /// <param name="agentLinkPath">
    /// The stable <see cref="AgentSocketLink"/> this daemon's shells get as <c>SSH_AUTH_SOCK</c> (null: none, so the
    /// environment is untouched). Set only by the standalone daemon, whose shells outlive the ssh connection that
    /// forwarded the agent; the GUI's own daemon never uses this factory.
    /// </param>
    public LocalShellSessionFactory(string? agentLinkPath) => _agentLinkPath = agentLinkPath;

    /// <summary>The shells that take <c>-l</c> for a login shell, by basename.</summary>
    private static readonly HashSet<string> LoginShells = new(StringComparer.Ordinal) { "bash", "zsh", "fish", "ksh", "sh", "dash" };

    public ITerminalSession Create(TerminalSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Ssh is not null)
        {
            throw new NotSupportedException("A multiplexer daemon opens local shells only; it never opens an SSH session.");
        }

        (string command, string arguments) = ResolveShell(request.Command, request.Arguments, ShellHelper.GetDefaultShell);
        string cwd = ResolveWorkingDirectory(request.StartingDirectory, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Directory.Exists);
        return new RustPtySession(
            command,
            request.Cols,
            request.Rows,
            arguments,
            cwd,
            skipPowerShellPostLaunchInit: request.SkipPowerShellPostLaunchInit,
            environmentOverrides: WithAgentLink(request.EnvironmentOverrides, _agentLinkPath, Environment.GetEnvironmentVariable, AgentSocketLink.LinkExists));
    }

    /// <summary>
    /// <paramref name="overrides"/> plus <c>SSH_AUTH_SOCK=<paramref name="linkPath"/></c>, when there is a link path and
    /// the daemon was started with an <c>SSH_AUTH_SOCK</c> or the link already exists; otherwise
    /// <paramref name="overrides"/> itself, so hosts without agent forwarding see no change.
    /// </summary>
    internal static IReadOnlyDictionary<string, string>? WithAgentLink(
        IReadOnlyDictionary<string, string>? overrides, string? linkPath, Func<string, string?> getEnvironmentVariable, Func<string, bool> linkExists)
    {
        if (string.IsNullOrEmpty(linkPath)) return overrides;
        if (string.IsNullOrEmpty(getEnvironmentVariable("SSH_AUTH_SOCK")) && !linkExists(linkPath)) return overrides;

        var merged = overrides is null ? new Dictionary<string, string>() : new Dictionary<string, string>(overrides);
        merged["SSH_AUTH_SOCK"] = linkPath;
        return merged;
    }

    /// <summary>
    /// An empty command is <paramref name="defaultShell"/> - with <c>-l</c> when it is a known login
    /// shell and no arguments were given, so it gets the login environment <c>sshd</c> would have
    /// given it (the daemon itself was started by a non-login <c>ssh</c> exec). A command the caller
    /// named passes through unchanged, arguments and all.
    /// </summary>
    internal static (string Command, string Arguments) ResolveShell(string command, string arguments, Func<string> defaultShell)
    {
        ArgumentNullException.ThrowIfNull(defaultShell);
        if (!string.IsNullOrWhiteSpace(command)) return (command, arguments);

        string shell = defaultShell();
        bool login = string.IsNullOrWhiteSpace(arguments) && LoginShells.Contains(Path.GetFileName(shell));
        return (shell, login ? "-l" : arguments);
    }

    /// <summary>
    /// Empty, <c>~</c> and <c>~/…</c> resolve against <paramref name="home"/>; a directory that does not
    /// exist falls back to <paramref name="home"/>, so a pane restored on another host still opens.
    /// </summary>
    internal static string ResolveWorkingDirectory(string? requested, string home, Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(directoryExists);
        if (string.IsNullOrWhiteSpace(requested) || requested == "~") return home;

        string resolved = requested.Length > 1 && requested[0] == '~' && (requested[1] == '/' || requested[1] == Path.DirectorySeparatorChar)
            ? Path.Combine(home, requested[2..])
            : requested;
        return directoryExists(resolved) ? resolved : home;
    }
}
