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
        Assert.Equal(9, environment.Count);
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
        Assert.Equal(10, environment.Count);
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
