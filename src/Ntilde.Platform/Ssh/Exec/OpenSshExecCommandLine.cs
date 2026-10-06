using System.Text;

namespace Ntilde.Platform.Ssh.Exec;

/// <summary>
/// The argv of an OpenSSH exec (Phase 4 spec §8.2): the profile's launch plan, wrapped in the options
/// an exec channel needs, with the remote command last.
/// </summary>
public static class OpenSshExecCommandLine
{
    /// <summary>
    /// ssh's own option string (OpenSSH <c>ssh.c</c>'s getopt call): a letter followed by <c>:</c> takes an argument.
    /// </summary>
    internal const string SshOptionLetters = "1246ab:c:e:fgi:kl:m:no:p:qstvxAB:CD:E:F:GI:J:KL:MNO:P:Q:R:S:TVw:W:XYy";

    /// <summary>
    /// <c>[...diagnostics, -T, -o ClearAllForwardings=yes, -o BatchMode=no|yes, -o ControlMaster=no, ...plan, --, command]</c>,
    /// with every piece of the plan that would break the exec channel dropped (<see cref="WithoutChannelBreakers"/>).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>-T</c>: no PTY, so the channel carries bytes, not a terminal. ssh's <c>-t</c>/<c>-T</c>
    /// are last-wins, unlike <c>-o</c>, so a <c>-tt</c> in the profile's ExtraSshArgs would turn the
    /// PTY back on and corrupt the binary mux stream: such pieces are dropped from the plan (and
    /// logged), with the others that keep the proxy command from running on the channel. The plan is
    /// parsed the way ssh parses it, so a clustered <c>-tv</c> counts as much as <c>-t</c> (codex C3).</item>
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
    /// <param name="log">Told about each piece dropped from the plan, and why.</param>
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
        argv.AddRange(WithoutChannelBreakers(planArguments, log));
        argv.Add("--");
        argv.Add(remoteCommand);
        return argv;
    }

    /// <summary>
    /// The plan without the pieces that would break the exec channel, everything else kept in its order. The plan
    /// is read with ssh's own getopt rules (<see cref="SshOptionLetters"/>; codex C3), not matched against known
    /// spellings, which a clustered <c>-tv</c> or <c>-fN</c> slipped past:
    /// <list type="bullet">
    /// <item>Single letters cluster (<c>-tv</c> is <c>-t -v</c>). A letter that takes an argument takes the rest of
    /// its token, or else the next token, which is then never scanned for options: <c>-p2tN</c> is port <c>2tN</c>,
    /// and the value of <c>-o ProxyCommand=ssh -W %h:%p jump</c> stays whole.</item>
    /// <item>ssh reads options until the destination (the first word), then again after it, until <c>--</c> or the
    /// next word; from there on everything is the remote command, and nothing is an option. The plan's
    /// ExtraSshArgs come after the destination; its place is kept.</item>
    /// <item>Dropped from a cluster, the rest kept (<c>-tv</c> becomes <c>-v</c>; a cluster left empty goes):
    /// <c>t</c> and <c>T</c> (we pass <c>-T</c>), <c>N</c> (no command), <c>f</c> (background), <c>n</c> (stdin from
    /// <c>/dev/null</c>, which starves the proxy), <c>s</c> (the command becomes a subsystem name), <c>G</c> and
    /// <c>V</c> (print and exit), <c>M</c> (master mode, which beats <c>ControlMaster=no</c>).</item>
    /// <item>Dropped with their argument: <c>-W</c> (stdio forwarding, no command), <c>-O</c> (a control command, then
    /// exit), <c>-Q</c> (a query, then exit), and an <c>-o</c> whose keyword (case-insensitive, ended by whitespace or
    /// <c>=</c>) is <c>RequestTTY</c>, <c>SessionType</c>, <c>ForkAfterAuthentication</c>, <c>StdinNull</c>,
    /// <c>RemoteCommand</c> or <c>PermitLocalCommand</c> (a LocalCommand writes to ssh's stdout, the mux stream).
    /// Every other <c>-o</c> is kept as it is.</item>
    /// </list>
    /// A letter ssh does not know, or an option whose argument is missing at the end (unless dropped anyway), is kept:
    /// ssh refuses it, as it would anywhere. So is <c>--</c> or a word after the destination, with everything after it,
    /// which is logged: ssh reads it all as the remote command. Each dropped piece is logged by its letter (an
    /// <c>-o</c> by its keyword), never with its value.
    /// </summary>
    internal static List<string> WithoutChannelBreakers(IReadOnlyList<string> plan, Action<string>? log)
    {
        var kept = new List<string>(plan.Count);
        bool afterDestination = false;
        for (int i = 0; i < plan.Count; i++)
        {
            string token = plan[i];
            bool isOption = token.Length >= 2 && token[0] == '-' && token != "--";
            if (!isOption)
            {
                if (token != "--" && !afterDestination)
                {
                    afterDestination = true; // the destination
                    kept.Add(token);
                    continue;
                }

                // "--", or a word after the destination: ssh reads no option from here on - after a "--" before the
                // destination, not even after it - so the "--" this argv appends becomes part of the remote command
                // too. Kept as the user wrote it, and said.
                log?.Invoke("[OpenSshExec] The profile's ssh arguments end ssh's options early ('--', or a word after the destination): ssh reads what follows as the remote command, ahead of the exec channel's own, which will not run as given.");
                for (; i < plan.Count; i++) kept.Add(plan[i]);
                break;
            }

            var cluster = new StringBuilder("-", token.Length);
            string? nextTokenArgument = null;
            for (int j = 1; j < token.Length; j++)
            {
                char letter = token[j];
                if (!TakesArgument(letter))
                {
                    if (DroppedFlag(letter) is { } why) Dropped(log, $"-{letter}", why);
                    else cluster.Append(letter);
                    continue;
                }

                // The rest of the token is the argument, or else the next token: never scanned for letters. One missing
                // at the end would take this argv's own "--": ssh then fails, unless the option is dropped anyway.
                bool attached = j + 1 < token.Length;
                string? argument = attached ? token[(j + 1)..] : i + 1 < plan.Count ? plan[i + 1] : null;
                if (!attached && argument is not null) i++;
                if (DroppedWithArgument(letter, argument ?? string.Empty) is { } dropped)
                {
                    Dropped(log, dropped.Piece, dropped.Why);
                }
                else
                {
                    cluster.Append(letter);
                    if (attached) cluster.Append(argument);
                    else nextTokenArgument = argument;
                }

                break;
            }

            if (cluster.Length > 1) kept.Add(cluster.ToString());
            if (nextTokenArgument is not null) kept.Add(nextTokenArgument);
        }

        return kept;
    }

    private static bool TakesArgument(char letter)
    {
        int at = letter == ':' ? -1 : SshOptionLetters.IndexOf(letter, StringComparison.Ordinal);
        return at >= 0 && at + 1 < SshOptionLetters.Length && SshOptionLetters[at + 1] == ':';
    }

    private const string NoPty = "the exec channel never has a PTY";
    private const string Background = "it sends ssh to the background before the command runs, leaving the channel's pipes behind";

    /// <summary>Why a flag (an option without an argument) breaks the exec channel; null when it does not.</summary>
    private static string? DroppedFlag(char letter) => letter switch
    {
        't' or 'T' => NoPty,
        'N' => "it runs no remote command, so the proxy would never run",
        'f' => Background,
        'n' => "it reads stdin from /dev/null, which starves the proxy",
        's' => "it asks for the command as a subsystem",
        'G' => "it prints the configuration and exits instead of connecting",
        'V' => "it prints the version and exits instead of connecting",
        'M' => "it would make this hidden ssh a connection-sharing master, which a visible tab then rides on",
        _ => null,
    };

    /// <summary>The piece to name and why, when an option with this argument breaks the exec channel; null when it does not.</summary>
    private static (string Piece, string Why)? DroppedWithArgument(char letter, string argument) => letter switch
    {
        'W' => ("-W", "it forwards ssh's stdio to a TCP port instead of running the command (dropped with its argument)"),
        'O' => ("-O", "it sends a control command to a master and exits (dropped with its argument)"),
        'Q' => ("-Q", "it answers a query and exits (dropped with its argument)"),
        'o' => DroppedConfigOption(argument),
        _ => null,
    };

    private static (string Piece, string Why)? DroppedConfigOption(string option)
    {
        ReadOnlySpan<char> text = option.AsSpan().TrimStart(" \t");
        int end = text.IndexOfAny(" \t=");
        string keyword = (end < 0 ? text : text[..end]).ToString();
        string? why = keyword.ToLowerInvariant() switch
        {
            "requesttty" => NoPty,
            "sessiontype" => "it would run no command, or a subsystem, instead of the exec channel's command",
            "forkafterauthentication" => Background,
            "stdinnull" => "it reads stdin from /dev/null, which starves the proxy",
            "remotecommand" => "the exec channel runs its own command",
            "permitlocalcommand" => "a LocalCommand writes to ssh's stdout, which carries the mux stream",
            _ => null,
        };
        return why is null ? null : ($"-o {keyword}", $"{why} (dropped with its value)");
    }

    private static void Dropped(Action<string>? log, string piece, string why) =>
        log?.Invoke($"[OpenSshExec] Ignoring '{piece}' from the profile's ssh arguments: {why}.");
}
