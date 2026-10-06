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
    /// <paramref name="helperPath"/> for <paramref name="profile"/>. An inherited <c>SSH_ASKPASS</c>
    /// is replaced; an inherited non-empty <c>DISPLAY</c> is kept.
    /// </summary>
    public static void Apply(IDictionary<string, string?> environment, string helperPath, SshProfile profile)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
        ArgumentNullException.ThrowIfNull(profile);

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
}
