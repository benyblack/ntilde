using Ntilde.Platform.Ssh.Exec;
using Ntilde.Shell;

namespace Ntilde.Tests.Shell;

/// <summary>
/// The askpass helper's vault-only mode (<see cref="SshAskPassEnvironment.VaultOnlyVariable"/>), which an automatic
/// reconnect of a profile with a saved password runs it in: it answers the target's own password prompt from the vault,
/// once per ssh process, and nothing else, and never builds any UI - no Avalonia app, no window. Without the mode, the helper is as before:
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

    private const string Token = "0123456789abcdef0123456789abcdef";

    /// <summary>Vault-only, for the ssh whose token is <see cref="Token"/>: how an automatic reconnect runs the helper.</summary>
    private static readonly Dictionary<string, string> VaultOnlySsh = With(VaultOnly, SshAskPassEnvironment.SessionVariable, Token);

    /// <summary>
    /// One ssh's prompts in order, each answered by a new helper process (as ssh runs it) with "s3cret" saved: what each
    /// exited with and wrote.
    /// </summary>
    private (int Exit, string Answer)[] Converse(IReadOnlyDictionary<string, string> environment, params string[] prompts) =>
        prompts.Select(prompt =>
        {
            var run = NewRun(environment, saved: "s3cret");
            int exit = run.Execute(prompt);
            Assert.Empty(run.Dialogs);
            return (exit, run.Stdout.ToString());
        }).ToArray();

    private SshAskPassRecord RecordOf(string token) => new SshAskPassSessionMarkers(() => _markers).Read(token);

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

    /// <summary>
    /// Vault-only mode, too, fills the target's password at most once per ssh process (its token), and never shows UI:
    /// ssh's NumberOfPasswordPrompts=1 bounds a method's attempts, not the prompts of one keyboard-interactive round. The
    /// same ssh asking again gets no answer, and the vault is not even read.
    /// </summary>
    [Fact]
    public void Vault_only_answers_the_target_prompt_once_per_token()
    {
        var first = NewRun(VaultOnlySsh, saved: "s3cret");
        var second = NewRun(VaultOnlySsh, saved: "s3cret");

        Assert.Equal(0, first.Execute(TargetPrompt));
        Assert.Equal(1, second.Execute(TargetPrompt));

        Assert.Equal("s3cret" + Environment.NewLine, first.Stdout.ToString());
        Assert.Equal(string.Empty, second.Stdout.ToString());
        Assert.Empty(second.Dialogs);
        Assert.Empty(second.VaultReads);
    }

    /// <summary>
    /// A PAM expired-password conversation is one keyboard-interactive round: after the saved password, the server asks for
    /// a new one and then for it again. Those get nothing - an unattended process must never type the saved password into
    /// a new-password field - and are recorded declined: more than the saved password was asked for.
    /// </summary>
    [Fact]
    public void Vault_only_fills_a_PAM_expired_password_conversation_once_and_records_the_rest_declined()
    {
        var answers = Converse(
            VaultOnlySsh,
            "(ops@prod.internal) Password: ",
            "(ops@prod.internal) New password: ",
            "(ops@prod.internal) Retype new password: ");

        Assert.Equal([(0, "s3cret" + Environment.NewLine), (1, string.Empty), (1, string.Empty)], answers);
        Assert.Equal(new SshAskPassRecord(Answered: true, Declined: true), RecordOf(Token));
    }

    /// <summary>
    /// A server that offers keyboard-interactive and password auth: after a refused keyboard-interactive fill, ssh's next
    /// method asks in its own form (<c>user@host's password:</c>). That is the refusal asking again, not more being asked
    /// for: no answer, and nothing recorded declined, so the app still reads the saved password as refused.
    /// </summary>
    [Fact]
    public void Vault_only_after_a_keyboard_interactive_fill_the_password_method_asking_again_is_no_second_factor()
    {
        var answers = Converse(VaultOnlySsh, "(ops@prod.internal) Password: ", TargetPrompt);

        Assert.Equal([(0, "s3cret" + Environment.NewLine), (1, string.Empty)], answers);
        Assert.Equal(new SshAskPassRecord(Answered: true, Declined: false), RecordOf(Token));
    }

    private const string PfSensePrompt = "(admin@192.168.1.1) Password for admin@pfSense.home.arpa:";

    /// <summary>A pfSense box (FreeBSD's sshd, keyboard-interactive only) the profile reaches by address.</summary>
    private static Dictionary<string, string> PfSense(IReadOnlyDictionary<string, string> environment) =>
        With(With(environment, SshAskPassEnvironment.ProfileUserVariable, "admin"), SshAskPassEnvironment.ProfileHostVariable, "192.168.1.1");

    /// <summary>
    /// FreeBSD's pam_unix asks <c>Password for user@host:</c> - all such a host wants. An automatic reconnect fills it,
    /// once, and records no decline: the sign-in is not taken for one that needs a second factor.
    /// </summary>
    [Fact]
    public void Vault_only_fills_the_password_FreeBSD_asks_for_and_records_no_decline()
    {
        var answers = Converse(PfSense(VaultOnlySsh), PfSensePrompt);

        Assert.Equal([(0, "s3cret" + Environment.NewLine)], answers);
        Assert.Equal(new SshAskPassRecord(Answered: true, Declined: false), RecordOf(Token));
    }

    [Fact]
    public void A_user_attempt_fills_the_password_FreeBSD_asks_for_from_the_vault()
    {
        var run = NewRun(PfSense(With(Interactive, SshAskPassEnvironment.SessionVariable, Token)), saved: "s3cret", typed: "typed");

        Assert.Equal(0, run.Execute(PfSensePrompt));

        Assert.Equal("s3cret" + Environment.NewLine, run.Stdout.ToString());
        Assert.Empty(run.Dialogs);
    }

    /// <summary>Without a session token nothing could hold the fill to one per ssh, so vault-only gives none.</summary>
    [Fact]
    public void Vault_only_without_a_session_token_answers_nothing()
    {
        var run = new Run(VaultOnly, saved: "s3cret");

        Assert.Equal(1, run.Execute(TargetPrompt));

        Assert.Equal(string.Empty, run.Stdout.ToString());
        Assert.Empty(run.Dialogs);
    }

    /// <summary>
    /// Vault-only, a fill that cannot be recorded is not made: without the record, a later prompt from the same ssh could
    /// not tell that it came second, and would get the saved password again.
    /// </summary>
    [Fact]
    public void Vault_only_when_the_record_cannot_be_written_answers_nothing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_markers)!);
        File.WriteAllText(_markers, "a file where the directory would go");
        try
        {
            var run = NewRun(VaultOnlySsh, saved: "s3cret");

            Assert.Equal(1, run.Execute(TargetPrompt));

            Assert.Equal(string.Empty, run.Stdout.ToString());
            Assert.Empty(run.Dialogs);
        }
        finally
        {
            File.Delete(_markers);
        }
    }

    /// <summary>
    /// On Enter, keyboard-interactive text that mentions a password but asks for something else goes to the dialog, not
    /// the vault: only a prompt that ends asking for one is the target's password.
    /// </summary>
    [Fact]
    public void A_user_attempt_takes_a_keyboard_interactive_prompt_not_ending_in_password_to_the_dialog()
    {
        const string prompt = "(ops@prod.internal) Your password expires soon. Verification code: ";
        var run = NewRun(With(Interactive, SshAskPassEnvironment.SessionVariable, Token), saved: "s3cret", typed: "123456");

        Assert.Equal(0, run.Execute(prompt));

        Assert.Equal("123456" + Environment.NewLine, run.Stdout.ToString());
        Assert.Equal(prompt, Assert.Single(run.Dialogs));
        Assert.Empty(run.VaultReads);
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

    /// <summary>
    /// Review I-1: the app counts a saved password as refused only when the helper filled it for that ssh and declined no
    /// second factor. So in vault-only mode the helper records the fill under the attempt's token.
    /// </summary>
    [Fact]
    public void Vault_only_records_that_it_filled_the_password_for_this_ssh()
    {
        const string token = "0123456789abcdef0123456789abcdef";
        var run = NewRun(With(VaultOnly, SshAskPassEnvironment.SessionVariable, token), saved: "s3cret");

        Assert.Equal(0, run.Execute(TargetPrompt));

        Assert.Equal(new SshAskPassRecord(Answered: true, Declined: false), new SshAskPassSessionMarkers(() => _markers).Read(token));
    }

    /// <summary>
    /// Review I-1: a prompt that names the target but asks for no password - a second factor after the saved password - is
    /// declined (ssh then sends it empty) and recorded, so the app does not count the sign-in's failure as a refused
    /// password. A prompt that does not name the target (a host key, a jump host's password, a passphrase) records nothing.
    /// </summary>
    [Fact]
    public void Vault_only_records_a_declined_prompt_that_names_the_target()
    {
        const string token = "0123456789abcdef0123456789abcdef";
        var markers = new SshAskPassSessionMarkers(() => _markers);
        var env = With(VaultOnly, SshAskPassEnvironment.SessionVariable, token);

        Assert.NotEqual(0, NewRun(env, saved: "s3cret").Execute("Enter passphrase for key '/home/ops/.ssh/id_ed25519': "));
        Assert.NotEqual(0, NewRun(env, saved: "s3cret").Execute("jumpuser@bastion's password: "));
        Assert.Equal(default, markers.Read(token));

        Assert.NotEqual(0, NewRun(env, saved: "s3cret").Execute("(ops@prod.internal) Verification code: "));

        Assert.Equal(new SshAskPassRecord(Answered: false, Declined: true), markers.Read(token));
    }

    /// <summary>
    /// Greptile G1: the app asks up front whether the helper could record what it does - a folder it can create a file in -
    /// and offers no saved password to an automatic attempt when it could not (a refusal would then never be counted). The
    /// probe leaves nothing behind.
    /// </summary>
    [Fact]
    public void CanRecord_says_whether_the_record_folder_takes_a_new_file()
    {
        var writable = new SshAskPassSessionMarkers(() => _markers);
        string blockedPath = _markers + "-blocked";
        Directory.CreateDirectory(Path.GetDirectoryName(_markers)!);
        File.WriteAllText(blockedPath, "a file where the folder would go");
        try
        {
            Assert.True(writable.CanRecord());
            Assert.Empty(Directory.EnumerateFileSystemEntries(_markers));
            Assert.False(new SshAskPassSessionMarkers(() => blockedPath).CanRecord());
        }
        finally
        {
            File.Delete(blockedPath);
        }
    }

    /// <summary>Review M5: claiming a fill is atomic - the first claim of a token wins, a second (or a race) finds it taken.</summary>
    [Fact]
    public void A_fill_is_claimed_once()
    {
        var markers = new SshAskPassSessionMarkers(() => _markers);

        Assert.True(markers.TryClaim("0123456789abcdef0123456789abcdef"));
        Assert.False(markers.TryClaim("0123456789abcdef0123456789abcdef"));
        Assert.True(markers.Read("0123456789abcdef0123456789abcdef").Answered);
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
        var run = NewRun(VaultOnlySsh, saved: "s3cret");

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
        var run = NewRun(VaultOnlySsh, saved: "s3cret");

        Assert.Equal(0, run.Execute("(ops@prod.internal) Password: "));
        Assert.Equal("s3cret" + Environment.NewLine, run.Stdout.ToString());
        Assert.Empty(run.Dialogs);
    }

    /// <summary>
    /// Everything that is not the target's password is turned away at once, with no UI built and the vault not even
    /// read: a host key, a key's passphrase, a jump host's password, keyboard-interactive text that asks for no password -
    /// even text that mentions one but does not end asking for it.
    /// </summary>
    [Theory]
    [InlineData("The authenticity of host 'prod.internal' can't be established.\nAre you sure you want to continue connecting (yes/no/[fingerprint])? ")]
    [InlineData("Enter passphrase for key '/home/ops/.ssh/id_ed25519': ")]
    [InlineData("jumpuser@bastion's password: ")]
    [InlineData("(ops@prod.internal) Verification code: ")]
    [InlineData("(ops@prod.internal) Your password expires soon. Verification code: ")]
    public void Vault_only_refuses_every_other_prompt_without_any_ui(string prompt)
    {
        var run = NewRun(VaultOnlySsh, saved: "s3cret", typed: "typed");

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
        var run = NewRun(VaultOnlySsh, saved, typed: "typed");

        int exit = run.Execute(TargetPrompt);

        Assert.NotEqual(0, exit);
        Assert.Equal(string.Empty, run.Stdout.ToString());
        Assert.Empty(run.Dialogs);
        Assert.Equal(default, RecordOf(Token)); // no fill, so no claim: a claim would read as a refused password
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
