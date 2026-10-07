using Ntilde.Platform.Ssh.Exec;
using Ntilde.Shell;

namespace Ntilde.Tests.Shell;

/// <summary>
/// The askpass helper's vault-only mode (<see cref="SshAskPassEnvironment.VaultOnlyVariable"/>), which an automatic
/// reconnect of a profile with a saved password runs it in: it answers the target's own password prompt from the vault
/// and nothing else, and never builds any UI - no Avalonia app, no window. Without the mode, the helper is as before:
/// the vault for the target's password - once per ssh process (<see cref="SshAskPassEnvironment.SessionVariable"/>), never
/// with <see cref="SshAskPassEnvironment.NoVaultVariable"/> - and a dialog for everything else. The dialog is the seam
/// (<see cref="SshAskPassCommand.Execute(string[], TextWriter, TextWriter, Func{string, string?}, Func{TerminalProfile, string?}, Func{SshAskPassCommand.AskPassState, string?}, SshAskPassSessionMarkers)"/>):
/// production's starts the Avalonia app, these tests' records that it was asked.
/// </summary>
public sealed class SshAskPassVaultOnlyTests : IDisposable
{
    private const string TargetPrompt = "ops@prod.internal's password: ";

    /// <summary>This test's own marker directory (the helper's record of the ssh processes it filled from the vault).</summary>
    private readonly string _markers = Path.Combine(Path.GetTempPath(), "ntilde-askpass-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_markers)) Directory.Delete(_markers, recursive: true);
    }

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

    private Run NewRun(IReadOnlyDictionary<string, string> environment, string? saved, string? typed = null) =>
        new(environment, saved, typed, _markers);

    private sealed class Run(IReadOnlyDictionary<string, string> environment, string? saved, string? typed = null, string? markers = null)
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
                },
                new SshAskPassSessionMarkers(() => markers ?? throw new InvalidOperationException("this test records no marker")));
    }

    private static Dictionary<string, string> With(IReadOnlyDictionary<string, string> environment, string name, string value) =>
        new(environment, StringComparer.Ordinal) { [name] = value };

    /// <summary>
    /// The coordinator's fix to the smoke test's complaint: Enter after a refused saved password re-sent it on every
    /// prompt and never showed the dialog. Now one ssh process (its session token) gets the saved password once; when
    /// the server asks again, the user is asked.
    /// </summary>
    [Fact]
    public void A_user_attempt_fills_the_target_password_from_the_vault_once_per_ssh_then_asks()
    {
        var env = With(Interactive, SshAskPassEnvironment.SessionVariable, "0123456789abcdef0123456789abcdef");
        var first = NewRun(env, saved: "stale", typed: "typed");
        var second = NewRun(env, saved: "stale", typed: "typed");

        Assert.Equal(0, first.Execute(TargetPrompt));
        Assert.Equal(0, second.Execute(TargetPrompt));

        Assert.Equal("stale" + Environment.NewLine, first.Stdout.ToString());
        Assert.Empty(first.Dialogs);
        Assert.Equal("typed" + Environment.NewLine, second.Stdout.ToString());
        Assert.Equal(TargetPrompt, Assert.Single(second.Dialogs));
        Assert.Empty(second.VaultReads);
    }

    /// <summary>Another ssh process - the next attempt - has a new token, and gets the saved password again.</summary>
    [Fact]
    public void A_new_ssh_fills_from_the_vault_again()
    {
        var first = NewRun(With(Interactive, SshAskPassEnvironment.SessionVariable, "0123456789abcdef0123456789abcdef"), saved: "s3cret");
        var next = NewRun(With(Interactive, SshAskPassEnvironment.SessionVariable, "fedcba9876543210fedcba9876543210"), saved: "s3cret");

        Assert.Equal(0, first.Execute(TargetPrompt));
        Assert.Equal(0, next.Execute(TargetPrompt));

        Assert.Equal("s3cret" + Environment.NewLine, next.Stdout.ToString());
        Assert.Empty(next.Dialogs);
    }

    /// <summary>
    /// A user's attempt after the host's saved password was refused (<see cref="SshAskPassEnvironment.NoVaultVariable"/>):
    /// the dialog at once, and the vault is not even read. Its "Remember password" replaces the saved one, as before.
    /// </summary>
    [Fact]
    public void The_no_vault_marker_goes_straight_to_the_dialog()
    {
        var run = NewRun(With(With(Interactive, SshAskPassEnvironment.NoVaultVariable, "1"), SshAskPassEnvironment.SessionVariable, "0123456789abcdef0123456789abcdef"),
            saved: "stale", typed: "typed");

        Assert.Equal(0, run.Execute(TargetPrompt));

        Assert.Equal("typed" + Environment.NewLine, run.Stdout.ToString());
        Assert.Equal(TargetPrompt, Assert.Single(run.Dialogs));
        Assert.Empty(run.VaultReads);
    }

    /// <summary>Vault-only mode is unaffected by the token: ssh's NumberOfPasswordPrompts=1 already bounds it, and it never shows UI.</summary>
    [Fact]
    public void Vault_only_ignores_the_session_token()
    {
        var env = With(VaultOnly, SshAskPassEnvironment.SessionVariable, "0123456789abcdef0123456789abcdef");
        var first = new Run(env, saved: "s3cret");
        var second = new Run(env, saved: "s3cret");

        Assert.Equal(0, first.Execute(TargetPrompt));
        Assert.Equal(0, second.Execute(TargetPrompt));

        Assert.Equal("s3cret" + Environment.NewLine, second.Stdout.ToString());
        Assert.Empty(second.Dialogs);
    }

    /// <summary>A token that is not 32 hex digits names no file: the helper behaves as without one, and writes nothing.</summary>
    [Theory]
    [InlineData("..")]
    [InlineData("../../escape")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("")]
    public void A_token_that_is_not_one_is_ignored(string token)
    {
        var run = new Run(With(Interactive, SshAskPassEnvironment.SessionVariable, token), saved: "s3cret");

        Assert.Equal(0, run.Execute(TargetPrompt));

        Assert.Equal("s3cret" + Environment.NewLine, run.Stdout.ToString());
        Assert.Empty(run.Dialogs);
    }

    /// <summary>
    /// Where the helper cannot record that it filled the password, it asks the user instead: answering from the vault
    /// without the record would send a refused password again on the next prompt.
    /// </summary>
    [Fact]
    public void When_the_record_cannot_be_written_the_helper_asks_instead()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_markers)!);
        File.WriteAllText(_markers, "a file where the directory would go");
        try
        {
            var run = NewRun(With(Interactive, SshAskPassEnvironment.SessionVariable, "0123456789abcdef0123456789abcdef"), saved: "s3cret", typed: "typed");

            Assert.Equal(0, run.Execute(TargetPrompt));

            Assert.Equal("typed" + Environment.NewLine, run.Stdout.ToString());
            Assert.Single(run.Dialogs);
        }
        finally
        {
            File.Delete(_markers);
        }
    }

    /// <summary>Recording a fill sweeps the records older than a day; a recent one stays.</summary>
    [Fact]
    public void Recording_a_fill_sweeps_stale_records()
    {
        Directory.CreateDirectory(_markers);
        string stale = Path.Combine(_markers, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.answered");
        string recent = Path.Combine(_markers, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.answered");
        File.WriteAllText(stale, string.Empty);
        File.WriteAllText(recent, string.Empty);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromDays(2));

        var run = NewRun(With(Interactive, SshAskPassEnvironment.SessionVariable, "0123456789abcdef0123456789abcdef"), saved: "s3cret");
        Assert.Equal(0, run.Execute(TargetPrompt));

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(Path.Combine(_markers, "0123456789abcdef0123456789abcdef.answered")));
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
