using System.Globalization;
using Ntilde.Platform.Ssh.Models;

namespace Ntilde.Platform.Ssh.Exec;

/// <summary>
/// The environment that sends an OpenSSH client's prompts - password, passphrase, host key - to the
/// ntilde askpass helper (Phase 4 spec §8.2). The contract is the one 009b1e3 introduced and #59
/// removed with scp's askpass; it is restored for the exec transport only, which has no terminal
/// to prompt on. The helper (<c>SshAskPassCommand</c> in the App) reads the same names.
/// </summary>
/// <remarks>
/// <c>SSH_ASKPASS_REQUIRE=force</c> needs OpenSSH 8.4 or later. An older client still uses askpass
/// when it has no tty and <c>DISPLAY</c> is set, and an exec transport's ssh has no tty - hence
/// <c>DISPLAY</c>, which is only filled in when the environment has none.
/// </remarks>
public static class SshAskPassEnvironment
{
    /// <summary>"1" puts the ntilde executable into askpass mode (as <c>--ssh-askpass</c> does).</summary>
    public const string ModeVariable = "NTILDE_SSH_ASKPASS";

    /// <summary>The profile the prompt is for, so the helper can name it and use its vault password.</summary>
    public const string ProfileIdVariable = "NTILDE_SSH_ASKPASS_PROFILE_ID";
    public const string ProfileNameVariable = "NTILDE_SSH_ASKPASS_PROFILE_NAME";
    public const string ProfileUserVariable = "NTILDE_SSH_ASKPASS_PROFILE_USER";
    public const string ProfileHostVariable = "NTILDE_SSH_ASKPASS_PROFILE_HOST";
    public const string ProfilePortVariable = "NTILDE_SSH_ASKPASS_PROFILE_PORT";

    /// <summary>
    /// "1" (<see cref="ApplySavedPasswordOnly"/>): the helper answers only the target's own password prompt, and only
    /// from the profile's saved password; every other prompt, or none saved, it refuses without showing anything.
    /// </summary>
    public const string VaultOnlyVariable = "NTILDE_SSH_ASKPASS_VAULT_ONLY";

    /// <summary>
    /// "1" (<see cref="ApplyWithoutSavedPassword"/>): the helper never answers from the vault, so every prompt goes to
    /// the user's dialog - for a user's attempt after the host's saved password was refused.
    /// </summary>
    public const string NoVaultVariable = "NTILDE_SSH_ASKPASS_NO_VAULT";

    /// <summary>
    /// One ssh process's token, new for each one <see cref="Apply"/> prepares (32 hex digits): the helper fills the
    /// target's password from the vault at most once per token, so a second prompt from the same ssh - the saved password
    /// was refused - goes to the dialog instead of sending it again. A ProxyJump hop's ssh inherits it, as it is part of
    /// the same connection.
    /// </summary>
    public const string SessionVariable = "NTILDE_SSH_ASKPASS_SESSION";

    /// <summary>OpenSSH's own variables.</summary>
    public const string AskPassVariable = "SSH_ASKPASS";
    public const string AskPassRequireVariable = "SSH_ASKPASS_REQUIRE";
    public const string DisplayVariable = "DISPLAY";

    /// <summary>The placeholder <c>DISPLAY</c> for clients older than 8.4; nothing connects to it.</summary>
    public const string PlaceholderDisplay = "ntilde";

    /// <summary>
    /// Leaves an ssh started with <paramref name="environment"/> no askpass to prompt through: for a batch
    /// mode exec, which must fail rather than prompt. <c>BatchMode=yes</c> alone is not enough, because a
    /// ProxyJump hop's ssh does not inherit it, and on a desktop the user's own <c>SSH_ASKPASS</c> would
    /// then put a dialog up. So <c>SSH_ASKPASS</c> is removed and <c>SSH_ASKPASS_REQUIRE=never</c> stops
    /// OpenSSH 8.4 and later; <c>DISPLAY</c> is removed too, since an older client with no tty uses
    /// askpass whenever <c>DISPLAY</c> is set, falling back to its compiled-in helper.
    /// </summary>
    public static void Suppress(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        environment.Remove(AskPassVariable);
        environment.Remove(DisplayVariable);
        environment[AskPassRequireVariable] = "never";
    }

    /// <summary>
    /// Points <paramref name="environment"/> (a <c>ProcessStartInfo.Environment</c>) at
    /// <paramref name="helperPath"/> for <paramref name="profile"/>, with a new <see cref="SessionVariable"/> token. An
    /// inherited <c>SSH_ASKPASS</c> is replaced; an inherited non-empty <c>DISPLAY</c> is kept; an inherited
    /// <see cref="VaultOnlyVariable"/> or <see cref="NoVaultVariable"/> is removed, so a user who is waiting gets every
    /// prompt, and the vault once.
    /// </summary>
    public static void Apply(IDictionary<string, string?> environment, string helperPath, SshProfile profile)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
        ArgumentNullException.ThrowIfNull(profile);

        environment.Remove(VaultOnlyVariable);
        environment.Remove(NoVaultVariable);
        environment[SessionVariable] = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        environment[AskPassVariable] = helperPath;
        environment[AskPassRequireVariable] = "force";
        if (!environment.TryGetValue(DisplayVariable, out string? display) || string.IsNullOrEmpty(display))
        {
            environment[DisplayVariable] = PlaceholderDisplay;
        }

        environment[ModeVariable] = "1";
        environment[ProfileIdVariable] = profile.Id.ToString("D", CultureInfo.InvariantCulture);
        environment[ProfileNameVariable] = profile.Name ?? string.Empty;
        environment[ProfileUserVariable] = profile.User ?? string.Empty;
        environment[ProfileHostVariable] = profile.Host ?? string.Empty;
        environment[ProfilePortVariable] = profile.Port.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// <see cref="Apply"/>, for an attempt nobody is waiting on whose profile has a saved password (an automatic
    /// reconnect): the helper then runs in its vault-only mode (<see cref="VaultOnlyVariable"/>). It answers the
    /// target's own password prompt from the saved password, and refuses every other prompt - a host key, a key's
    /// passphrase, a jump host's password, other keyboard-interactive text - without building any UI.
    /// <c>SSH_ASKPASS_REQUIRE=force</c> and <c>DISPLAY</c> are as <see cref="Apply"/> sets them.
    /// </summary>
    public static void ApplySavedPasswordOnly(IDictionary<string, string?> environment, string helperPath, SshProfile profile)
    {
        Apply(environment, helperPath, profile);
        environment[VaultOnlyVariable] = "1";
    }

    /// <summary>
    /// <see cref="Apply"/>, for a user's attempt after the host's saved password was refused: the helper then never
    /// answers from the vault (<see cref="NoVaultVariable"/>), so the user's dialog comes at once and the refused password
    /// is not sent again.
    /// </summary>
    public static void ApplyWithoutSavedPassword(IDictionary<string, string?> environment, string helperPath, SshProfile profile)
    {
        Apply(environment, helperPath, profile);
        environment[NoVaultVariable] = "1";
    }
}
