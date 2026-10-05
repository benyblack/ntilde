using Ntilde.Platform.Ssh.Models;

namespace Ntilde.Platform.Ssh.Native;

/// <summary>
/// The native backend's connection options for a profile: the one builder behind
/// <see cref="Sessions.NativeSshSession"/> (a PTY shell) and <see cref="Exec.NativeSshExecTransport"/>
/// (a command, Phase 4 spec §8.3). It covers the jump chain, the identity file, the agent, the
/// keepalive, and the shell session's probe and cwd bootstraps, which an exec session validates but
/// ignores.
/// </summary>
/// <remarks>
/// Known hosts and passwords are not options. The native layer asks for them with prompt events, and
/// the <see cref="Interactions.ISshInteractionHandler"/> answers from the known-hosts store and the vault.
/// </remarks>
public static class NativeSshConnectionOptionsFactory
{
    /// <summary>The terminal size a caller with no terminal passes; an exec session has no PTY to size.</summary>
    public const int DefaultCols = 120;

    /// <inheritdoc cref="DefaultCols"/>
    public const int DefaultRows = 30;

    private const string ShellDetectionCommand = "sh -lc 'printf \"%s\" \"${SHELL##*/}\"' 2>/dev/null";

    private static readonly NativeJumpHostConnector JumpHostConnector = new();

    /// <summary>The options for <paramref name="profile"/> at the default size: what an exec transport passes.</summary>
    public static NativeSshConnectionOptions Create(SshProfile profile) => Create(profile, DefaultCols, DefaultRows);

    public static NativeSshConnectionOptions Create(SshProfile profile, int cols, int rows)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return Create(JumpHostConnectPlan.Create(profile), profile, cols, rows);
    }

    public static NativeSshConnectionOptions Create(JumpHostConnectPlan connectPlan, SshProfile profile, int cols, int rows)
    {
        ArgumentNullException.ThrowIfNull(connectPlan);
        ArgumentNullException.ThrowIfNull(profile);

        NativeSshConnectionOptions baseOptions = JumpHostConnector.CreateConnectionOptions(connectPlan, profile, cols, rows);
        RemoteShellKind remoteShellKind = profile.RemoteShellKind;

        return new NativeSshConnectionOptions
        {
            Host = baseOptions.Host,
            User = baseOptions.User,
            Port = baseOptions.Port,
            Cols = baseOptions.Cols,
            Rows = baseOptions.Rows,
            Term = baseOptions.Term,
            Password = baseOptions.Password,
            IdentityFilePath = baseOptions.IdentityFilePath,
            UseAgent = baseOptions.UseAgent,
            KnownHostsFilePath = baseOptions.KnownHostsFilePath,
            JumpHops = baseOptions.JumpHops,
            KeepAliveIntervalSeconds = baseOptions.KeepAliveIntervalSeconds,
            KeepAliveCountMax = baseOptions.KeepAliveCountMax,
            RemoteShellKind = remoteShellKind,
            ShellDetectionCommand = remoteShellKind == RemoteShellKind.Auto
                ? ShellDetectionCommand
                : null,
            BashCwdBootstrap = string.Join(
                "\n",
                "__ntilde_emit_cwd() {",
                "  printf '\\033]7;%s\\007' \"$PWD\"",
                "}",
                "PROMPT_COMMAND=\"__ntilde_emit_cwd${PROMPT_COMMAND:+;$PROMPT_COMMAND}\""),
            ZshCwdBootstrap = string.Join(
                "\n",
                "autoload -Uz add-zsh-hook",
                "__ntilde_emit_cwd() {",
                "  printf '\\033]7;%s\\007' \"$PWD\"",
                "}",
                "add-zsh-hook precmd __ntilde_emit_cwd"),
            FishCwdBootstrap = string.Join(
                "\n",
                "functions -q fish_prompt; and functions -c fish_prompt __ntilde_original_fish_prompt",
                "function fish_prompt",
                "    printf '\\033]7;%s\\007' \"$PWD\"",
                "    if functions -q __ntilde_original_fish_prompt",
                "        __ntilde_original_fish_prompt",
                "    end",
                "end")
        };
    }
}
