namespace Ntilde.Platform.Ssh.Exec;

/// <summary>
/// The argv of an OpenSSH exec (Phase 4 spec §8.2): the profile's launch plan, wrapped in the options
/// an exec channel needs, with the remote command last.
/// </summary>
public static class OpenSshExecCommandLine
{
    /// <summary>
    /// <c>[...diagnostics, -T, -o ClearAllForwardings=yes, -o BatchMode=no|yes, -o ControlMaster=no, ...plan, --, command]</c>,
    /// with any PTY request (<c>-t</c>, <c>-tt</c>, <c>-T</c>) dropped from the plan.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>-T</c>: no PTY, so the channel carries bytes, not a terminal. ssh's <c>-t</c>/<c>-T</c>
    /// are last-wins, unlike <c>-o</c>, so a <c>-tt</c> in the profile's ExtraSshArgs would turn the
    /// PTY back on and corrupt the binary mux stream: such tokens are dropped from the plan (and
    /// logged). Only standalone tokens are recognised; clustered flags are not parsed.</item>
    /// <item><c>ClearAllForwardings=yes</c>: the profile's forwards do not ride the mux channel
    /// (they belong to its interactive sessions), matching the native transport.</item>
    /// <item><c>BatchMode=no</c>: prompts stay possible; they reach the user through askpass. With
    /// <paramref name="batchMode"/> it is <c>BatchMode=yes</c> in the same place instead, so ssh never
    /// prompts at all: for an attempt nobody is waiting on (an automatic reconnect). Replaced, not
    /// appended, because ssh keeps an option's first value.</item>
    /// <item><c>ControlMaster=no</c>: a profile with connection sharing (<c>ControlMaster auto</c> in
    /// the generated config) still reuses an existing master, but this hidden ssh never becomes one.
    /// Were it the master, a visible tab of the same profile would multiplex through it, and the
    /// channel's Dispose - or a liveness kill - would take that tab down.</item>
    /// <item>The <c>-o</c> options come before the plan: for ssh the first value of an option wins,
    /// over the plan's ExtraSshArgs and over the generated config alike.</item>
    /// <item><c>--</c> then the command as one element: ssh joins what follows the destination into
    /// the remote command line, which the remote shell parses. Nothing after <c>--</c> is read as an
    /// option, however the command starts.</item>
    /// </list>
    /// </remarks>
    /// <param name="diagnosticsArguments">ssh's verbosity flags (<c>-v</c>, <c>-vv</c>), or none.</param>
    /// <param name="planArguments"><c>SshLaunchPlanner</c>'s output: <c>["-F", cfg, alias, ...ExtraSshArgs]</c>.</param>
    /// <param name="remoteCommand">The command line the remote shell runs.</param>
    /// <param name="log">Told about each PTY token dropped from the plan.</param>
    /// <param name="batchMode">True for <c>BatchMode=yes</c>: ssh fails instead of prompting.</param>
    public static IReadOnlyList<string> Build(
        IReadOnlyList<string> diagnosticsArguments,
        IReadOnlyList<string> planArguments,
        string remoteCommand,
        Action<string>? log = null,
        bool batchMode = false)
    {
        ArgumentNullException.ThrowIfNull(diagnosticsArguments);
        ArgumentNullException.ThrowIfNull(planArguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteCommand);

        var argv = new List<string>(diagnosticsArguments.Count + planArguments.Count + 9);
        argv.AddRange(diagnosticsArguments);
        argv.AddRange(["-T", "-o", "ClearAllForwardings=yes", "-o", batchMode ? "BatchMode=yes" : "BatchMode=no", "-o", "ControlMaster=no"]);
        foreach (string argument in planArguments)
        {
            if (IsPtyRequest(argument))
            {
                log?.Invoke($"[OpenSshExec] Ignoring '{argument}' from the profile's ssh arguments: the exec channel never has a PTY.");
                continue;
            }

            argv.Add(argument);
        }

        argv.Add("--");
        argv.Add(remoteCommand);
        return argv;
    }

    /// <summary><c>-T</c>, or <c>-t</c> repeated (<c>-t</c>, <c>-tt</c>, …), as a token of its own.</summary>
    private static bool IsPtyRequest(string argument) =>
        argument == "-T" || (argument.Length >= 2 && argument[0] == '-' && argument.AsSpan(1).IndexOfAnyExcept('t') < 0);
}
