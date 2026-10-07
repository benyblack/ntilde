using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Ntilde.Shell;

namespace Ntilde.Tests.Shell;

/// <summary>
/// The askpass helper answers from the vault only a prompt for the profile's own target (Phase 4
/// task 13 review). Every ssh the exec transport starts inherits the askpass environment - the
/// <c>ssh -W</c> of each ProxyJump hop too - so answering any "password" prompt would hand the
/// target's password to a jump host.
/// </summary>
public sealed class SshAskPassTargetPromptTests
{
    private static TerminalProfile Target() => new()
    {
        Id = Guid.Parse("e15099d2-ac29-40cb-bf1f-f466eb2622b7"),
        Type = ConnectionType.SSH,
        SshUser = "ops",
        SshHost = "Prod.Internal",
    };

    [Theory]
    [InlineData("ops@prod.internal's password: ")]
    [InlineData("OPS@PROD.INTERNAL's Password: ")]
    [InlineData("(ops@prod.internal) Password: ")]
    public void The_targets_password_prompt_is_answered_from_the_vault(string prompt)
    {
        Assert.True(SshAskPassCommand.IsTargetPasswordPrompt(prompt, Target()));
    }

    [Theory]
    [InlineData("jumpuser@jumphost's password: ")]
    [InlineData("ops@jump.internal's password: ")]
    [InlineData("(jumpuser@jumphost) Password: ")]
    public void A_jump_hosts_password_prompt_is_not(string prompt)
    {
        Assert.False(SshAskPassCommand.IsTargetPasswordPrompt(prompt, Target()));
    }

    [Fact]
    public void A_jump_server_cannot_forge_the_target_inside_its_own_prompt()
    {
        // Keyboard-interactive text is the server's; only the "(user@host) " prefix is ssh's own.
        Assert.False(SshAskPassCommand.IsTargetPasswordPrompt("(jumpuser@jumphost) ops@prod.internal's password: ", Target()));
    }

    [Theory]
    [InlineData("Enter passphrase for key '/home/ops/.ssh/id_ed25519': ")]
    [InlineData("(ops@prod.internal) Verification code: ")]
    [InlineData("The authenticity of host 'prod.internal' can't be established.\nAre you sure you want to continue connecting (yes/no/[fingerprint])? ")]
    public void A_prompt_that_is_not_for_a_password_is_not(string prompt)
    {
        Assert.False(SshAskPassCommand.IsTargetPasswordPrompt(prompt, Target()));
    }

    /// <summary>
    /// Keyboard-interactive text is the server's, and may mention a password while asking for something else: only text
    /// that ends asking for one (<c>password:</c>, whatever its case, trailing blanks aside), or is exactly the password
    /// for one account (below), is the target's password.
    /// </summary>
    [Theory]
    [InlineData("(ops@prod.internal) Your password expires soon. Verification code: ")]
    [InlineData("(ops@prod.internal) Password expired. Enter the code we sent: ")]
    [InlineData("(ops@prod.internal) Password")]
    public void Keyboard_interactive_text_that_does_not_end_asking_for_a_password_is_not(string prompt)
    {
        Assert.False(SshAskPassCommand.IsTargetPasswordPrompt(prompt, Target()));
    }

    [Theory]
    [InlineData("(ops@prod.internal) PASSWORD:")]
    [InlineData("(ops@prod.internal) New password:  \n")]
    public void Keyboard_interactive_text_ending_with_password_is(string prompt)
    {
        Assert.True(SshAskPassCommand.IsTargetPasswordPrompt(prompt, Target()));
    }

    /// <summary>
    /// pam_unix on FreeBSD and its derivatives (pfSense, OPNsense, TrueNAS CORE, whose sshd offers keyboard-interactive
    /// only) asks <c>Password for user@host:</c>, and Kerberos <c>Password for user@REALM:</c>: the whole text, one account
    /// name with no blank in it, whatever the case.
    /// </summary>
    [Theory]
    [InlineData("admin", "192.168.1.1", "(admin@192.168.1.1) Password for admin@pfSense.home.arpa:")]
    [InlineData("u", "h", "(u@h) Password for u@EXAMPLE.COM: ")]
    [InlineData("u", "h", "(u@h) PASSWORD FOR u@example.com:")]
    [InlineData("u", "h", "(u@h) password for u:")]
    public void Keyboard_interactive_password_for_one_account_is(string user, string host, string prompt)
    {
        Assert.True(SshAskPassCommand.IsTargetPasswordPrompt(prompt, Profile(user, host)));
    }

    [Theory]
    [InlineData("(u@h) One-time password for u:")]
    [InlineData("(u@h) Password for u: (expired)")]
    [InlineData("(u@h) Password for two words:")]
    [InlineData("(u@h) New password for u@h:")]
    [InlineData("(u@h) Password for :")]
    public void Keyboard_interactive_text_that_is_not_exactly_password_for_one_account_is_not(string prompt)
    {
        Assert.False(SshAskPassCommand.IsTargetPasswordPrompt(prompt, Profile("u", "h")));
    }

    /// <summary>The dialog offers to remember only the target's password, so it must know FreeBSD's prompt for it too.</summary>
    [AvaloniaFact]
    public void The_dialog_offers_to_remember_the_password_FreeBSD_asks_for()
    {
        TerminalProfile pfSense = Profile("admin", "192.168.1.1");

        Assert.True(RememberOffered("(admin@192.168.1.1) Password for admin@pfSense.home.arpa:", pfSense));
        Assert.False(RememberOffered("(admin@192.168.1.1) Verification code:", pfSense));
    }

    private static TerminalProfile Profile(string user, string host) => new()
    {
        Id = Guid.Parse("e15099d2-ac29-40cb-bf1f-f466eb2622b7"),
        Type = ConnectionType.SSH,
        SshUser = user,
        SshHost = host,
    };

    private static bool RememberOffered(string prompt, TerminalProfile profile)
    {
        var window = new SshAskPassCommand.AskPassWindow(new SshAskPassCommand.AskPassState(prompt, profile), shutdown: () => { });
        var panel = (StackPanel)((Border)window.Content!).Child!;
        return panel.Children.OfType<CheckBox>().Single().IsVisible;
    }

    [Fact]
    public void A_profile_without_a_user_never_auto_fills()
    {
        // ssh then logs in as the local account, whose name the profile does not know.
        TerminalProfile profile = Target();
        profile.SshUser = "";

        Assert.False(SshAskPassCommand.IsTargetPasswordPrompt("behna@prod.internal's password: ", profile));
        Assert.False(SshAskPassCommand.IsTargetPasswordPrompt("@prod.internal's password: ", profile));
    }
}
