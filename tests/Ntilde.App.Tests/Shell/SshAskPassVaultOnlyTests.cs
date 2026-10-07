using Ntilde.Platform.Ssh.Exec;
using Ntilde.Shell;

namespace Ntilde.Tests.Shell;

/// <summary>
/// The askpass helper's vault-only mode (<see cref="SshAskPassEnvironment.VaultOnlyVariable"/>), which an automatic
/// reconnect of a profile with a saved password runs it in: it answers the target's own password prompt from the vault
/// and nothing else, and never builds any UI - no Avalonia app, no window. Without the mode, the helper is as before:
/// the vault for the target's password, a dialog for everything else. The dialog is the seam
/// (<see cref="SshAskPassCommand.Execute(string[], TextWriter, TextWriter, Func{string, string?}, Func{TerminalProfile, string?}, Func{SshAskPassCommand.AskPassState, string?})"/>):
/// production's starts the Avalonia app, these tests' records that it was asked.
/// </summary>
public sealed class SshAskPassVaultOnlyTests
{
    private const string TargetPrompt = "ops@prod.internal's password: ";

    private static readonly Dictionary<string, string> Interactive = new(StringComparer.Ordinal)
    {
        [SshAskPassEnvironment.ModeVariable] = "1",
        [SshAskPassEnvironment.ProfileIdVariable] = "e15099d2-ac29-40cb-bf1f-f466eb2622b7",
        [SshAskPassEnvironment.ProfileNameVariable] = "Prod box",
        [SshAskPassEnvironment.ProfileUserVariable] = "ops",
        [SshAskPassEnvironment.ProfileHostVariable] = "prod.internal",
        [SshAskPassEnvironment.ProfilePortVariable] = "2222",
    };

    private static readonly Dictionary<string, string> VaultOnly = new(Interactive, StringComparer.Ordinal)
    {
        [SshAskPassEnvironment.VaultOnlyVariable] = "1",
    };

    private sealed class Run(IReadOnlyDictionary<string, string> environment, string? saved, string? typed = null)
    {
        public List<TerminalProfile> VaultReads { get; } = [];
        public List<string> Dialogs { get; } = [];
        public StringWriter Stdout { get; } = new();
        public StringWriter Stderr { get; } = new();

        public int Execute(string prompt) =>
            SshAskPassCommand.Execute(
                [prompt],
                Stdout,
                Stderr,
                name => environment.GetValueOrDefault(name),
                profile =>
                {
                    VaultReads.Add(profile);
                    return saved;
                },
                state =>
                {
                    Dialogs.Add(state.Prompt);
                    return typed;
                });
    }

    [Fact]
    public void Vault_only_answers_the_targets_password_prompt_from_the_vault()
    {
        var run = new Run(VaultOnly, saved: "s3cret");

        int exit = run.Execute(TargetPrompt);

        Assert.Equal(0, exit);
        Assert.Equal("s3cret" + Environment.NewLine, run.Stdout.ToString());
        Assert.Empty(run.Dialogs);
        TerminalProfile read = Assert.Single(run.VaultReads);
        Assert.Equal(Guid.Parse("e15099d2-ac29-40cb-bf1f-f466eb2622b7"), read.Id);
        Assert.Equal(("Prod box", "ops", "prod.internal", 2222), (read.Name, read.SshUser, read.SshHost, read.SshPort));
    }

    [Fact]
    public void Vault_only_answers_a_keyboard_interactive_password_prompt_for_the_target_too()
    {
        var run = new Run(VaultOnly, saved: "s3cret");

        Assert.Equal(0, run.Execute("(ops@prod.internal) Password: "));
        Assert.Equal("s3cret" + Environment.NewLine, run.Stdout.ToString());
        Assert.Empty(run.Dialogs);
    }

    /// <summary>
    /// Everything that is not the target's password is turned away at once, with no UI built and the vault not even
    /// read: a host key, a key's passphrase, a jump host's password, keyboard-interactive text that asks for no password.
    /// </summary>
    [Theory]
    [InlineData("The authenticity of host 'prod.internal' can't be established.\nAre you sure you want to continue connecting (yes/no/[fingerprint])? ")]
    [InlineData("Enter passphrase for key '/home/ops/.ssh/id_ed25519': ")]
    [InlineData("jumpuser@bastion's password: ")]
    [InlineData("(ops@prod.internal) Verification code: ")]
    public void Vault_only_refuses_every_other_prompt_without_any_ui(string prompt)
    {
        var run = new Run(VaultOnly, saved: "s3cret", typed: "typed");

        int exit = run.Execute(prompt);

        Assert.NotEqual(0, exit);
        Assert.Equal(string.Empty, run.Stdout.ToString());
        Assert.Empty(run.Dialogs);
        Assert.Empty(run.VaultReads);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Vault_only_with_nothing_saved_refuses_the_targets_password_without_any_ui(string? saved)
    {
        var run = new Run(VaultOnly, saved, typed: "typed");

        int exit = run.Execute(TargetPrompt);

        Assert.NotEqual(0, exit);
        Assert.Equal(string.Empty, run.Stdout.ToString());
        Assert.Empty(run.Dialogs);
    }

    /// <summary>A user is waiting: the vault fills the target's password, as before, and anything else goes to the dialog.</summary>
    [Fact]
    public void Without_vault_only_the_helper_still_asks_the_user_for_other_prompts()
    {
        var saved = new Run(Interactive, saved: "s3cret", typed: "typed");
        var asked = new Run(Interactive, saved: "s3cret", typed: "typed");
        var cancelled = new Run(Interactive, saved: null, typed: null);

        Assert.Equal(0, saved.Execute(TargetPrompt));
        Assert.Equal(0, asked.Execute("Enter passphrase for key '/home/ops/.ssh/id_ed25519': "));
        Assert.Equal(1, cancelled.Execute(TargetPrompt));

        Assert.Equal("s3cret" + Environment.NewLine, saved.Stdout.ToString());
        Assert.Empty(saved.Dialogs);
        Assert.Equal("typed" + Environment.NewLine, asked.Stdout.ToString());
        Assert.Single(asked.Dialogs);
        Assert.Equal(TargetPrompt, Assert.Single(cancelled.Dialogs));
        Assert.Equal(string.Empty, cancelled.Stdout.ToString());
    }

    [Fact]
    public void Only_one_turns_vault_only_on()
    {
        var run = new Run(new Dictionary<string, string>(Interactive, StringComparer.Ordinal) { [SshAskPassEnvironment.VaultOnlyVariable] = "0" }, saved: null, typed: "typed");

        Assert.Equal(0, run.Execute("Enter passphrase for key '/home/ops/.ssh/id_ed25519': "));
        Assert.Single(run.Dialogs);
    }
}
