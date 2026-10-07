using Ntilde.Platform.Ssh.Exec;
using Ntilde.Platform.Ssh.Models;

namespace Ntilde.Platform.Tests.Ssh.Exec;

/// <summary>The askpass contract from 009b1e3, restored for the exec transport (Phase 4 spec §8.2).</summary>
public sealed class SshAskPassEnvironmentTests
{
    private static SshProfile Profile() => new()
    {
        Id = Guid.Parse("e15099d2-ac29-40cb-bf1f-f466eb2622b7"),
        Name = "Prod box",
        User = "ops",
        Host = "prod.internal",
        Port = 2222,
    };

    [Fact]
    public void The_variable_names_are_the_ones_the_helper_reads()
    {
        Assert.Equal("NTILDE_SSH_ASKPASS", SshAskPassEnvironment.ModeVariable);
        Assert.Equal("NTILDE_SSH_ASKPASS_PROFILE_ID", SshAskPassEnvironment.ProfileIdVariable);
        Assert.Equal("NTILDE_SSH_ASKPASS_PROFILE_NAME", SshAskPassEnvironment.ProfileNameVariable);
        Assert.Equal("NTILDE_SSH_ASKPASS_PROFILE_USER", SshAskPassEnvironment.ProfileUserVariable);
        Assert.Equal("NTILDE_SSH_ASKPASS_PROFILE_HOST", SshAskPassEnvironment.ProfileHostVariable);
        Assert.Equal("NTILDE_SSH_ASKPASS_PROFILE_PORT", SshAskPassEnvironment.ProfilePortVariable);
        Assert.Equal("NTILDE_SSH_ASKPASS_VAULT_ONLY", SshAskPassEnvironment.VaultOnlyVariable);
        Assert.Equal("NTILDE_SSH_ASKPASS_NO_VAULT", SshAskPassEnvironment.NoVaultVariable);
        Assert.Equal("NTILDE_SSH_ASKPASS_SESSION", SshAskPassEnvironment.SessionVariable);
    }

    /// <summary>
    /// Inherited askpass settings - a desktop's own helper, a DISPLAY that makes a pre-8.4 ssh use the
    /// compiled-in default helper - all go, and SSH_ASKPASS_REQUIRE=never stops 8.4 and later outright.
    /// </summary>
    [Fact]
    public void Suppress_leaves_ssh_no_way_to_prompt_and_keeps_the_rest()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["SSH_ASKPASS"] = "/usr/lib/ssh/x11-ssh-askpass",
            ["SSH_ASKPASS_REQUIRE"] = "prefer",
            ["DISPLAY"] = ":0",
            ["PATH"] = "/usr/bin",
        };

        SshAskPassEnvironment.Suppress(environment);

        Assert.False(environment.ContainsKey("SSH_ASKPASS"));
        Assert.False(environment.ContainsKey("DISPLAY"));
        Assert.Equal("never", environment["SSH_ASKPASS_REQUIRE"]);
        Assert.Equal("/usr/bin", environment["PATH"]);
    }

    [Fact]
    public void Apply_sets_every_variable()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);

        SshAskPassEnvironment.Apply(environment, @"C:\Program Files\Ntilde\ntilde.exe", Profile());

        Assert.Equal(@"C:\Program Files\Ntilde\ntilde.exe", environment["SSH_ASKPASS"]);
        Assert.Equal("force", environment["SSH_ASKPASS_REQUIRE"]);
        Assert.Equal("ntilde", environment["DISPLAY"]);
        Assert.Equal("1", environment["NTILDE_SSH_ASKPASS"]);
        Assert.Equal("e15099d2-ac29-40cb-bf1f-f466eb2622b7", environment["NTILDE_SSH_ASKPASS_PROFILE_ID"]);
        Assert.Equal("Prod box", environment["NTILDE_SSH_ASKPASS_PROFILE_NAME"]);
        Assert.Equal("ops", environment["NTILDE_SSH_ASKPASS_PROFILE_USER"]);
        Assert.Equal("prod.internal", environment["NTILDE_SSH_ASKPASS_PROFILE_HOST"]);
        Assert.Matches("^[0-9a-f]{32}$", environment["NTILDE_SSH_ASKPASS_SESSION"]);
        Assert.Equal(10, environment.Count);
    }

    /// <summary>
    /// Each ssh the transport starts gets a session token of its own: the helper fills the target's password from the
    /// vault once per token, so a refused saved password brings the dialog, not the same password again.
    /// </summary>
    [Fact]
    public void Apply_gives_every_ssh_a_session_token_of_its_own()
    {
        var first = new Dictionary<string, string?>(StringComparer.Ordinal);
        var second = new Dictionary<string, string?>(StringComparer.Ordinal) { ["NTILDE_SSH_ASKPASS_SESSION"] = "inherited" };

        SshAskPassEnvironment.Apply(first, "/opt/ntilde/ntilde", Profile());
        SshAskPassEnvironment.Apply(second, "/opt/ntilde/ntilde", Profile());

        Assert.Matches("^[0-9a-f]{32}$", second["NTILDE_SSH_ASKPASS_SESSION"]);
        Assert.NotEqual(first["NTILDE_SSH_ASKPASS_SESSION"], second["NTILDE_SSH_ASKPASS_SESSION"]);
    }

    /// <summary>
    /// A connect attempt names its own token, so the app can read back what the helper did for that ssh - filled the
    /// saved password, or declined a second factor - and tell a refused password from one that was never asked for.
    /// </summary>
    [Fact]
    public void Apply_uses_a_given_session_token_and_refuses_one_that_is_not()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);

        SshAskPassEnvironment.ApplySavedPasswordOnly(environment, "/opt/ntilde/ntilde", Profile(), "0123456789abcdef0123456789abcdef");

        Assert.Equal("0123456789abcdef0123456789abcdef", environment[SshAskPassEnvironment.SessionVariable]);
        Assert.ThrowsAny<ArgumentException>(() => SshAskPassEnvironment.Apply(environment, "/opt/ntilde/ntilde", Profile(), "../escape"));
        Assert.ThrowsAny<ArgumentException>(() => SshAskPassEnvironment.Apply(environment, "/opt/ntilde/ntilde", Profile(), "0123456789ABCDEF0123456789ABCDEF"));
    }

    /// <summary>
    /// A user's attempt after the host's saved password was refused: everything <see cref="SshAskPassEnvironment.Apply"/>
    /// sets, plus the marker that sends the helper straight to the dialog, without the vault.
    /// </summary>
    [Fact]
    public void ApplyWithoutSavedPassword_sets_every_askpass_variable_and_the_no_vault_marker()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);

        SshAskPassEnvironment.ApplyWithoutSavedPassword(environment, "/opt/ntilde/ntilde", Profile());

        Assert.Equal("/opt/ntilde/ntilde", environment["SSH_ASKPASS"]);
        Assert.Equal("force", environment["SSH_ASKPASS_REQUIRE"]);
        Assert.Equal("1", environment["NTILDE_SSH_ASKPASS"]);
        Assert.Equal("1", environment["NTILDE_SSH_ASKPASS_NO_VAULT"]);
        Assert.False(environment.ContainsKey("NTILDE_SSH_ASKPASS_VAULT_ONLY"));
        Assert.Equal(11, environment.Count);
    }

    /// <summary>An inherited no-vault marker must not keep the vault from a later attempt that may use it.</summary>
    [Fact]
    public void Apply_and_ApplySavedPasswordOnly_clear_an_inherited_no_vault_marker()
    {
        var applied = new Dictionary<string, string?>(StringComparer.Ordinal) { ["NTILDE_SSH_ASKPASS_NO_VAULT"] = "1" };
        var savedOnly = new Dictionary<string, string?>(StringComparer.Ordinal) { ["NTILDE_SSH_ASKPASS_NO_VAULT"] = "1" };

        SshAskPassEnvironment.Apply(applied, "/opt/ntilde/ntilde", Profile());
        SshAskPassEnvironment.ApplySavedPasswordOnly(savedOnly, "/opt/ntilde/ntilde", Profile());

        Assert.False(applied.ContainsKey(SshAskPassEnvironment.NoVaultVariable));
        Assert.False(savedOnly.ContainsKey(SshAskPassEnvironment.NoVaultVariable));
    }

    /// <summary>
    /// An automatic reconnect of a profile whose password is saved: everything <see cref="SshAskPassEnvironment.Apply"/>
    /// sets, SSH_ASKPASS_REQUIRE=force and the placeholder DISPLAY included, plus the mode in which the helper answers
    /// the target's password from the vault and nothing else, with no UI.
    /// </summary>
    [Fact]
    public void ApplySavedPasswordOnly_sets_every_askpass_variable_and_the_vault_only_mode()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);

        SshAskPassEnvironment.ApplySavedPasswordOnly(environment, "/opt/ntilde/ntilde", Profile());

        Assert.Equal("/opt/ntilde/ntilde", environment["SSH_ASKPASS"]);
        Assert.Equal("force", environment["SSH_ASKPASS_REQUIRE"]);
        Assert.Equal("ntilde", environment["DISPLAY"]);
        Assert.Equal("1", environment["NTILDE_SSH_ASKPASS"]);
        Assert.Equal("1", environment["NTILDE_SSH_ASKPASS_VAULT_ONLY"]);
        Assert.Equal("e15099d2-ac29-40cb-bf1f-f466eb2622b7", environment["NTILDE_SSH_ASKPASS_PROFILE_ID"]);
        Assert.Equal("ops", environment["NTILDE_SSH_ASKPASS_PROFILE_USER"]);
        Assert.Equal("prod.internal", environment["NTILDE_SSH_ASKPASS_PROFILE_HOST"]);
        Assert.Equal(11, environment.Count);
    }

    [Fact]
    public void ApplySavedPasswordOnly_keeps_an_existing_display_as_Apply_does()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["DISPLAY"] = ":0" };

        SshAskPassEnvironment.ApplySavedPasswordOnly(environment, "/opt/ntilde/ntilde", Profile());

        Assert.Equal(":0", environment["DISPLAY"]);
    }

    /// <summary>A vault-only mode the app itself inherited must not turn a waiting user's prompt away unanswered.</summary>
    [Fact]
    public void Apply_clears_an_inherited_vault_only_mode()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["NTILDE_SSH_ASKPASS_VAULT_ONLY"] = "1" };

        SshAskPassEnvironment.Apply(environment, "/opt/ntilde/ntilde", Profile());

        Assert.False(environment.ContainsKey(SshAskPassEnvironment.VaultOnlyVariable));
    }

    [Fact]
    public void Apply_sets_the_port_as_an_invariant_string()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);

        SshAskPassEnvironment.Apply(environment, "/opt/ntilde/ntilde", Profile());

        Assert.Equal("2222", environment[SshAskPassEnvironment.ProfilePortVariable]);
    }

    [Fact]
    public void Apply_keeps_an_existing_display()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["DISPLAY"] = ":0" };

        SshAskPassEnvironment.Apply(environment, "/opt/ntilde/ntilde", Profile());

        Assert.Equal(":0", environment["DISPLAY"]);
    }

    [Fact]
    public void Apply_fills_an_empty_display()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["DISPLAY"] = "" };

        SshAskPassEnvironment.Apply(environment, "/opt/ntilde/ntilde", Profile());

        Assert.Equal("ntilde", environment["DISPLAY"]);
    }

    [Fact]
    public void Apply_overrides_an_inherited_askpass()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["SSH_ASKPASS"] = "/usr/lib/ssh/x11-ssh-askpass",
            ["SSH_ASKPASS_REQUIRE"] = "never",
        };

        SshAskPassEnvironment.Apply(environment, "/opt/ntilde/ntilde", Profile());

        Assert.Equal("/opt/ntilde/ntilde", environment["SSH_ASKPASS"]);
        Assert.Equal("force", environment["SSH_ASKPASS_REQUIRE"]);
    }

    [Fact]
    public void Apply_tolerates_unset_profile_text()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        var profile = new SshProfile { Name = null!, User = null!, Host = null! };

        SshAskPassEnvironment.Apply(environment, "/opt/ntilde/ntilde", profile);

        Assert.Equal(string.Empty, environment[SshAskPassEnvironment.ProfileNameVariable]);
        Assert.Equal(string.Empty, environment[SshAskPassEnvironment.ProfileUserVariable]);
        Assert.Equal(string.Empty, environment[SshAskPassEnvironment.ProfileHostVariable]);
    }

    [Fact]
    public void Apply_rejects_missing_inputs()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);

        Assert.Throws<ArgumentNullException>(() => SshAskPassEnvironment.Apply(null!, "/x", Profile()));
        Assert.ThrowsAny<ArgumentException>(() => SshAskPassEnvironment.Apply(environment, " ", Profile()));
        Assert.Throws<ArgumentNullException>(() => SshAskPassEnvironment.Apply(environment, "/x", null!));
        Assert.Throws<ArgumentNullException>(() => SshAskPassEnvironment.ApplySavedPasswordOnly(null!, "/x", Profile()));
        Assert.ThrowsAny<ArgumentException>(() => SshAskPassEnvironment.ApplySavedPasswordOnly(environment, " ", Profile()));
        Assert.Throws<ArgumentNullException>(() => SshAskPassEnvironment.ApplySavedPasswordOnly(environment, "/x", null!));
    }
}
