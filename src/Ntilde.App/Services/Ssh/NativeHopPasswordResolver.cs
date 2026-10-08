using System;
using System.Collections.Generic;
using System.Linq;
using Ntilde.Platform.Ssh.Native;

namespace Ntilde.Services.Ssh;

/// <summary>
/// The passwords a non-interactive native connection (an SFTP transfer, a remote listing) may send,
/// one per server of the chain.
/// </summary>
/// <param name="Target">The target's password, or null.</param>
/// <param name="JumpHops">Each jump hop's own password, index-aligned with the connection's hops.</param>
internal sealed record NativeHopPasswords(string? Target, IReadOnlyList<string?> JumpHops)
{
    public static NativeHopPasswords None { get; } = new(null, Array.Empty<string?>());
}

/// <summary>
/// Chooses which password each server of a non-interactive connection may receive.
/// </summary>
/// <remarks>
/// A transfer or listing opens its own connection and cannot prompt, so it replays what the
/// interactive session already learned. It used to send a single password — the session's, or the
/// profile's saved one — to every hop, which put the target's credential on the bastion. Now every
/// server gets only a password that was entered for THAT server:
/// <list type="bullet">
/// <item>the target: the password this session entered for it, else the profile's saved password
/// (which belongs to the target);</item>
/// <item>each jump hop: the password this session entered for that hop, else none. A hop never
/// falls back to the target's password, nor to the profile's saved one.</item>
/// </list>
/// So a password-only bastion keeps working for transfers once the terminal session has
/// authenticated it, and fails (rather than leaking another server's password) when it has not.
/// </remarks>
internal static class NativeHopPasswordResolver
{
    public static NativeHopPasswords Resolve(
        NativeSshConnectionOptions baseOptions,
        ActiveSshSessionRegistry? sessionRegistry,
        Guid sessionId,
        Func<string?> savedTargetPassword)
    {
        ArgumentNullException.ThrowIfNull(baseOptions);
        ArgumentNullException.ThrowIfNull(savedTargetPassword);

        // An identity file keeps its precedence over passwords, unchanged: nothing is offered to
        // any server, exactly as before per-hop passwords existed.
        if (!string.IsNullOrWhiteSpace(baseOptions.IdentityFilePath))
        {
            return NativeHopPasswords.None;
        }

        string? target = SessionPassword(sessionRegistry, sessionId, baseOptions.Host, baseOptions.Port, baseOptions.User)
            ?? savedTargetPassword();
        string?[] jumpHops = baseOptions.JumpHops
            .Select(hop => SessionPassword(sessionRegistry, sessionId, hop.Host, hop.Port, hop.User))
            .ToArray();

        return new NativeHopPasswords(NullIfBlank(target), jumpHops);
    }

    private static string? SessionPassword(
        ActiveSshSessionRegistry? sessionRegistry,
        Guid sessionId,
        string host,
        int port,
        string user)
    {
        if (sessionRegistry == null || sessionId == Guid.Empty)
        {
            return null;
        }

        return sessionRegistry.TryGetRuntimePassword(sessionId, host, port, user, out string? password)
            ? NullIfBlank(password)
            : null;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
