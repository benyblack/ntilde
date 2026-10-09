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
/// <param name="TargetScope">
/// The persisted remote host's password scope <paramref name="Target"/> was read from (Phase 5 spec R8), or
/// <see cref="Guid.Empty"/> when it came from anywhere else (a plain session's own, the vault) or the chain has jump hops,
/// where a refusal cannot be told apart from a hop's. What <see cref="ForgetRefusedTarget"/> forgets it from.
/// </param>
internal sealed record NativeHopPasswords(string? Target, IReadOnlyList<string?> JumpHops, Guid TargetScope = default)
{
    public static NativeHopPasswords None { get; } = new(null, Array.Empty<string?>());

    /// <summary>
    /// Release hardening item 5: the connection was refused at sign-in (<see cref="NativeSshAuthenticationRefusedException"/>),
    /// so a target password read from a host's scope is stale - the server rotated it while the host's long-lived mux link,
    /// which never signs in again, kept it. Forgets it from that scope, but only while the scope still holds this very value
    /// (<see cref="ActiveSshSessionRegistry.RemoveRuntimePassword"/>): a password typed since, in the host's reconnect, stays,
    /// and a host gone since took its scope with it. Anything else is left as it was.
    /// </summary>
    public void ForgetRefusedTarget(ActiveSshSessionRegistry? sessionRegistry, string host, int port, string user)
    {
        if (sessionRegistry is null || TargetScope == Guid.Empty || Target is null) return;
        sessionRegistry.RemoveRuntimePassword(TargetScope, host, port, user, Target);
    }
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

        // A persisted remote tab's session keeps its passwords under its host's scope (Phase 5 spec R8).
        Guid scope = sessionRegistry == null || sessionId == Guid.Empty ? Guid.Empty : sessionRegistry.PasswordScopeOf(sessionId);
        string? fromSession = SessionPassword(sessionRegistry, scope, baseOptions.Host, baseOptions.Port, baseOptions.User);
        string? target = fromSession ?? savedTargetPassword();
        string?[] jumpHops = baseOptions.JumpHops
            .Select(hop => SessionPassword(sessionRegistry, scope, hop.Host, hop.Port, hop.User))
            .ToArray();
        // Only a host's scope (a persisted remote tab's: not the session's own id), and only without jump hops.
        bool targetFromHostScope = fromSession is not null && scope != sessionId && baseOptions.JumpHops.Count == 0;

        return new NativeHopPasswords(NullIfBlank(target), jumpHops, targetFromHostScope ? scope : Guid.Empty);
    }

    private static string? SessionPassword(
        ActiveSshSessionRegistry? sessionRegistry,
        Guid scope,
        string host,
        int port,
        string user)
    {
        if (sessionRegistry == null || scope == Guid.Empty)
        {
            return null;
        }

        return sessionRegistry.TryGetRuntimePassword(scope, host, port, user, out string? password)
            ? NullIfBlank(password)
            : null;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
