using Ntilde.Platform.Ssh.Exec;
using Ntilde.Platform.Ssh.Models;

namespace Ntilde.Shell;

/// <summary>
/// Whether the OpenSSH askpass helper may fill the profile's vault password for a user's attempt (Phase 5 spec, Task 2).
/// The helper recognises the target's password prompt by the <c>user@host</c> in its text, which a jump host can forge in
/// two cases: an ssh before 8.4 puts no <c>(user@host)</c> in front of a keyboard-interactive prompt, so a bastion's
/// <c>ops@prod.internal's password: </c> reads as the target's; and a hop whose <c>user@host</c> equals the target's
/// is indistinguishable, since ssh prompts omit the port. Either way, when the connection goes through a jump host the
/// helper runs without the vault (<see cref="SshAskPassEnvironment.NoVaultVariable"/>) and the user types the password.
/// </summary>
/// <remarks>
/// A ProxyJump that only the user's own <c>~/.ssh/config</c> supplies is invisible here, and so is not covered.
/// </remarks>
internal static class SshAskPassVaultPolicy
{
    /// <summary>
    /// Whether the connection to <paramref name="profile"/> goes through a jump host as ntilde can see it: the profile's
    /// <see cref="SshProfile.JumpHops"/>, or its extra ssh arguments naming a proxy (<c>-J</c>, <c>ProxyJump</c>,
    /// <c>ProxyCommand</c>; <see cref="OpenSshExecCommandLine.ExtraArgumentsNameAProxy"/>).
    /// </summary>
    public static bool GoesThroughJumpHost(SshProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.JumpHops is { Count: > 0 }) return true;
        return OpenSshExecCommandLine.ExtraArgumentsNameAProxy(profile.ExtraSshArgs);
    }

    /// <summary>
    /// False when the connection goes through a jump host and either the local ssh does not prefix its keyboard-interactive
    /// prompts (<paramref name="sshPrefixesKeyboardInteractivePrompts"/>; false for an unknown version) or a hop's
    /// <c>User@Host</c> equals the target's - compared case-insensitively, a hop's empty user being the profile's, the port
    /// not at all.
    /// </summary>
    public static bool MayOfferVault(SshProfile profile, bool sshPrefixesKeyboardInteractivePrompts)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!GoesThroughJumpHost(profile)) return true;
        if (!sshPrefixesKeyboardInteractivePrompts) return false;

        string targetUser = (profile.User ?? string.Empty).Trim();
        string targetHost = (profile.Host ?? string.Empty).Trim();
        foreach (SshJumpHop hop in profile.JumpHops ?? [])
        {
            string user = string.IsNullOrWhiteSpace(hop.User) ? targetUser : hop.User.Trim();
            if (string.Equals(user, targetUser, StringComparison.OrdinalIgnoreCase)
                && string.Equals((hop.Host ?? string.Empty).Trim(), targetHost, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
