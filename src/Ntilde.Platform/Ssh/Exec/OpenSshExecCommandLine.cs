namespace Ntilde.Platform.Ssh.Exec;

/// <summary>
/// The argv of an OpenSSH exec (Phase 4 spec §8.2): the profile's launch plan, wrapped in the options
/// an exec channel needs, with the remote command last.
/// </summary>
public static class OpenSshExecCommandLine
{
    /// <summary>
    /// <c>[...diagnostics, -T, -o ClearAllForwardings=yes, -o BatchMode=no, ...plan, --, command]</c>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>-T</c>: no PTY, so the channel carries bytes, not a terminal.</item>
    /// <item><c>ClearAllForwardings=yes</c>: the profile's forwards do not ride the mux channel
    /// (they belong to its interactive sessions), matching the native transport.</item>
    /// <item><c>BatchMode=no</c>: prompts stay possible; they reach the user through askpass.</item>
    /// <item><c>--</c> then the command as one element: ssh joins what follows the destination into
    /// the remote command line, which the remote shell parses. Nothing after <c>--</c> is read as an
    /// option, however the command starts.</item>
    /// </list>
    /// </remarks>
    /// <param name="diagnosticsArguments">ssh's verbosity flags (<c>-v</c>, <c>-vv</c>), or none.</param>
    /// <param name="planArguments"><c>SshLaunchPlanner</c>'s output: <c>["-F", cfg, alias, ...ExtraSshArgs]</c>.</param>
    /// <param name="remoteCommand">The command line the remote shell runs.</param>
    public static IReadOnlyList<string> Build(IReadOnlyList<string> diagnosticsArguments, IReadOnlyList<string> planArguments, string remoteCommand)
    {
        ArgumentNullException.ThrowIfNull(diagnosticsArguments);
        ArgumentNullException.ThrowIfNull(planArguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteCommand);

        var argv = new List<string>(diagnosticsArguments.Count + planArguments.Count + 7);
        argv.AddRange(diagnosticsArguments);
        argv.AddRange(["-T", "-o", "ClearAllForwardings=yes", "-o", "BatchMode=no"]);
        argv.AddRange(planArguments);
        argv.Add("--");
        argv.Add(remoteCommand);
        return argv;
    }
}
