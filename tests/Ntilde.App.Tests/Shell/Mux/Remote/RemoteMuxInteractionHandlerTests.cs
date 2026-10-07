using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Platform.Ssh.Native;
using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Tests.Shell.Mux.Remote;

/// <summary>
/// A remote host's prompts (Phase 4 ruling: automatic reconnects are non-interactive). A user-started
/// attempt may ask the user, and a password or passphrase that got a connection in is remembered in
/// memory for the host's lifetime. An automatic attempt never asks: it answers from that memory, accepts
/// only a host key already trusted, and cancels everything else so it fails quietly. A remembered secret
/// that may have been refused is forgotten, never replayed (review fix round 1): the native layer asks
/// for a password once, then falls to keyboard-interactive, so "asked again" never happens.
/// </summary>
public sealed class RemoteMuxInteractionHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SshInteractionRequest Password { get; } = new() { Kind = SshInteractionKind.Password, Prompt = "Password:" };
    private static SshInteractionRequest Passphrase { get; } = new() { Kind = SshInteractionKind.Passphrase, Prompt = "Passphrase:" };
    private static SshInteractionRequest Keyboard { get; } = new()
    {
        Kind = SshInteractionKind.KeyboardInteractive,
        KeyboardPrompts = [new SshKeyboardPrompt("Verification code:", echo: false)],
    };

    private static SshInteractionRequest Secret(string kind) =>
        Enum.Parse<SshInteractionKind>(kind) == SshInteractionKind.Password ? Password : Passphrase;

    private static SshInteractionRequest HostKey(string fingerprint) => new()
    {
        Kind = SshInteractionKind.UnknownHostKey,
        Host = "fake-host",
        Port = 22,
        Algorithm = "ssh-ed25519",
        Fingerprint = fingerprint,
    };

    private static RemoteMuxInteractionHandler Handler(ISshInteractionHandler? user) =>
        new(user, request => request.Fingerprint == "SHA256:trusted");

    /// <summary>The user's next answer to <paramref name="prompt"/>, remembered as a successful user-started attempt leaves it.</summary>
    private static async Task RememberAsync(RemoteMuxInteractionHandler handler, SshInteractionRequest prompt)
    {
        RemoteMuxInteractionHandler.Attempt attempt = handler.BeginAttempt(interactive: true);
        await attempt.HandleAsync(prompt, Ct);
        attempt.Succeeded();
        Assert.True(handler.Remembers(prompt.Kind));
    }

    [Theory]
    [InlineData(nameof(SshInteractionKind.Password))]
    [InlineData(nameof(SshInteractionKind.Passphrase))]
    public async Task A_secret_that_got_a_connection_in_answers_later_automatic_attempts_without_asking(string kind)
    {
        var user = new ScriptedUser(SshInteractionResponse.FromSecret("hunter2"));
        RemoteMuxInteractionHandler handler = Handler(user);
        RemoteMuxInteractionHandler.Attempt first = handler.BeginAttempt(interactive: true);
        Assert.Equal("hunter2", (await first.HandleAsync(Secret(kind), Ct)).Secret);
        first.Succeeded();

        SshInteractionResponse replayed = await handler.BeginAttempt(interactive: false).HandleAsync(Secret(kind), Ct);

        Assert.Equal("hunter2", replayed.Secret);
        Assert.False(replayed.IsCanceled);
        Assert.False(replayed.RememberPasswordInVault);   // memory only: never written anywhere
        Assert.Single(user.Asked);
    }

    [Fact]
    public async Task An_answer_from_an_attempt_that_never_succeeded_is_not_remembered()
    {
        RemoteMuxInteractionHandler handler = Handler(new ScriptedUser(SshInteractionResponse.FromSecret("typo")));
        RemoteMuxInteractionHandler.Attempt failed = handler.BeginAttempt(interactive: true);
        await failed.HandleAsync(Password, Ct);

        Assert.False(handler.Remembers(SshInteractionKind.Password));
        await Assert.ThrowsAsync<RemoteMuxPromptAbortedException>(() => handler.BeginAttempt(interactive: false).HandleAsync(Password, Ct));
    }

    [Fact]
    public async Task An_automatic_attempt_never_asks_the_user()
    {
        var user = new ScriptedUser(SshInteractionResponse.FromSecret("x"), SshInteractionResponse.AcceptHostKey());
        RemoteMuxInteractionHandler.Attempt automatic = Handler(user).BeginAttempt(interactive: false);

        await Assert.ThrowsAsync<RemoteMuxPromptAbortedException>(() => automatic.HandleAsync(Password, Ct));
        Assert.True((await automatic.HandleAsync(Passphrase, Ct)).IsCanceled);
        await Assert.ThrowsAsync<RemoteMuxPromptAbortedException>(() => automatic.HandleAsync(Keyboard, Ct));
        Assert.True((await automatic.HandleAsync(HostKey("SHA256:new"), Ct)).IsCanceled);
        Assert.Empty(user.Asked);
    }

    /// <summary>
    /// Review fix round 2: a cancelled password or keyboard-interactive prompt is submitted as an empty
    /// answer, which the server counts as a failed login - on every reconnect. With nothing to answer, an
    /// automatic attempt aborts instead: the native channel then closes the session without answering
    /// (NativeSshExecTransport's contract), and rusty_ssh stops before sending anything.
    /// </summary>
    [Fact]
    public async Task With_nothing_to_answer_a_password_or_keyboard_prompt_aborts_the_attempt_instead_of_sending_an_empty_answer()
    {
        RemoteMuxInteractionHandler.Attempt password = Handler(new ScriptedUser()).BeginAttempt(interactive: false);
        RemoteMuxInteractionHandler.Attempt keyboard = Handler(new ScriptedUser()).BeginAttempt(interactive: false);

        var passwordAbort = await Assert.ThrowsAsync<RemoteMuxPromptAbortedException>(() => password.HandleAsync(Password, Ct));
        var keyboardAbort = await Assert.ThrowsAsync<RemoteMuxPromptAbortedException>(() => keyboard.HandleAsync(Keyboard, Ct));

        Assert.Equal(SshInteractionKind.Password, passwordAbort.Prompt);
        Assert.Equal(SshInteractionKind.Password, password.AbortedPrompt);
        Assert.Equal(SshInteractionKind.KeyboardInteractive, keyboardAbort.Prompt);
        Assert.Equal(SshInteractionKind.KeyboardInteractive, keyboard.AbortedPrompt);
    }

    /// <summary>
    /// What still gets an empty answer: a passphrase (it only unlocks a local key; nothing reaches the
    /// server) and a keyboard-interactive round with no questions (an empty reply is the protocol's own).
    /// An untrusted host key is cancelled, which the native layer submits as a rejection.
    /// </summary>
    [Fact]
    public async Task A_passphrase_an_empty_keyboard_round_and_an_untrusted_host_key_are_still_answered()
    {
        RemoteMuxInteractionHandler.Attempt automatic = Handler(new ScriptedUser()).BeginAttempt(interactive: false);
        var noQuestions = new SshInteractionRequest { Kind = SshInteractionKind.KeyboardInteractive };

        SshInteractionResponse passphrase = await automatic.HandleAsync(Passphrase, Ct);
        SshInteractionResponse keyboard = await automatic.HandleAsync(noQuestions, Ct);
        SshInteractionResponse hostKey = await automatic.HandleAsync(HostKey("SHA256:unknown"), Ct);

        Assert.True(passphrase.IsCanceled);
        Assert.Empty(keyboard.KeyboardResponses);
        Assert.Equal("""{"accept":false}""", System.Text.Encoding.UTF8.GetString(
            NativeSshInteractionJson.BuildResponsePayload(NativeSshResponseKind.HostKeyDecision, hostKey)));
        Assert.Null(automatic.AbortedPrompt);
    }

    /// <summary>
    /// The native layer asks about the host key on every connect, known or not (the window's handler
    /// answers a known one from the known-hosts store without a dialog). Refusing them all would make
    /// every automatic reconnect fail; accepting only a trusted key keeps it quiet and safe.
    /// </summary>
    [Fact]
    public async Task An_automatic_attempt_accepts_only_a_host_key_already_trusted()
    {
        RemoteMuxInteractionHandler.Attempt automatic = Handler(new ScriptedUser()).BeginAttempt(interactive: false);

        SshInteractionResponse trusted = await automatic.HandleAsync(HostKey("SHA256:trusted"), Ct);
        SshInteractionResponse changed = await automatic.HandleAsync(HostKey("SHA256:changed"), Ct);

        Assert.True(trusted.IsAccepted);
        Assert.False(changed.IsAccepted);
        Assert.True(changed.IsCanceled);
    }

    [Fact]
    public async Task An_interactive_attempt_takes_host_keys_and_keyboard_prompts_to_the_user()
    {
        var user = new ScriptedUser(SshInteractionResponse.AcceptHostKey(), SshInteractionResponse.FromKeyboardResponses("123456"));
        RemoteMuxInteractionHandler handler = Handler(user);
        RemoteMuxInteractionHandler.Attempt attempt = handler.BeginAttempt(interactive: true);

        Assert.True((await attempt.HandleAsync(HostKey("SHA256:new"), Ct)).IsAccepted);
        Assert.Equal(new[] { "123456" }, (await attempt.HandleAsync(Keyboard, Ct)).KeyboardResponses);
        attempt.Succeeded();

        Assert.Equal(2, user.Asked.Count);
        // A one-time code is never replayed: an automatic attempt ends instead.
        await Assert.ThrowsAsync<RemoteMuxPromptAbortedException>(() => handler.BeginAttempt(interactive: false).HandleAsync(Keyboard, Ct));
    }

    /// <summary>
    /// Review fix round 1, the native shape: rusty_ssh asks for the password once, then tries
    /// keyboard-interactive, then fails. After a server-side password change, the remembered password is
    /// refused, the automatic attempt cancels keyboard-interactive and fails SshFailed: the connector
    /// calls <see cref="RemoteMuxInteractionHandler.Attempt.Refused"/>, and the next user attempt asks.
    /// </summary>
    [Theory]
    [InlineData(nameof(SshInteractionKind.Password))]
    [InlineData(nameof(SshInteractionKind.Passphrase))]
    public async Task A_remembered_secret_is_forgotten_when_the_attempt_that_offered_it_fails_auth(string kind)
    {
        var user = new ScriptedUser(SshInteractionResponse.FromSecret("old"), SshInteractionResponse.FromSecret("new"));
        RemoteMuxInteractionHandler handler = Handler(user);
        await RememberAsync(handler, Secret(kind));

        RemoteMuxInteractionHandler.Attempt automatic = handler.BeginAttempt(interactive: false);
        Assert.Equal("old", (await automatic.HandleAsync(Secret(kind), Ct)).Secret);
        await Assert.ThrowsAsync<RemoteMuxPromptAbortedException>(() => automatic.HandleAsync(Keyboard, Ct));
        automatic.Refused();

        Assert.False(handler.Remembers(Secret(kind).Kind));
        Assert.Equal("new", (await handler.BeginAttempt(interactive: true).HandleAsync(Secret(kind), Ct)).Secret);
        Assert.Equal(2, user.Asked.Count);
    }

    /// <summary>
    /// The remembered password was refused, and keyboard-interactive then got the user in. The stale
    /// password must not be committed again, and goes: it is not what got the connection in.
    /// </summary>
    [Theory]
    [InlineData(nameof(SshInteractionKind.Password))]
    [InlineData(nameof(SshInteractionKind.Passphrase))]
    public async Task A_remembered_secret_followed_by_another_auth_prompt_is_forgotten_even_when_the_attempt_succeeds(string kind)
    {
        var user = new ScriptedUser(SshInteractionResponse.FromSecret("old"), SshInteractionResponse.FromKeyboardResponses("new"));
        RemoteMuxInteractionHandler handler = Handler(user);
        await RememberAsync(handler, Secret(kind));

        RemoteMuxInteractionHandler.Attempt retry = handler.BeginAttempt(interactive: true);
        Assert.Equal("old", (await retry.HandleAsync(Secret(kind), Ct)).Secret);   // no dialog
        Assert.Equal(new[] { "new" }, (await retry.HandleAsync(Keyboard, Ct)).KeyboardResponses);
        retry.Succeeded();

        Assert.False(handler.Remembers(Secret(kind).Kind));
    }

    [Fact]
    public async Task A_user_answer_followed_by_another_auth_prompt_is_not_remembered()
    {
        var user = new ScriptedUser(SshInteractionResponse.FromSecret("typo"), SshInteractionResponse.FromKeyboardResponses("code"));
        RemoteMuxInteractionHandler handler = Handler(user);
        RemoteMuxInteractionHandler.Attempt attempt = handler.BeginAttempt(interactive: true);

        await attempt.HandleAsync(Password, Ct);
        await attempt.HandleAsync(Keyboard, Ct);
        attempt.Succeeded();

        Assert.False(handler.Remembers(SshInteractionKind.Password));
    }

    [Fact]
    public async Task A_remembered_secret_is_not_offered_twice_to_the_same_hop()
    {
        var user = new ScriptedUser(SshInteractionResponse.FromSecret("old"), SshInteractionResponse.FromSecret("new"));
        RemoteMuxInteractionHandler handler = Handler(user);
        await RememberAsync(handler, Password);

        RemoteMuxInteractionHandler.Attempt retry = handler.BeginAttempt(interactive: true);
        Assert.Equal("old", (await retry.HandleAsync(Password, Ct)).Secret);
        Assert.Equal("new", (await retry.HandleAsync(Password, Ct)).Secret);   // asked again: the user, not "old" again
        retry.Succeeded();

        Assert.Equal(2, user.Asked.Count);
        Assert.Equal("new", (await handler.BeginAttempt(interactive: false).HandleAsync(Password, Ct)).Secret);
    }

    /// <summary>
    /// A host-key prompt starts the next hop's connection, so the answers before it got the previous hop
    /// in: one key's passphrase, asked by each hop of a jump chain, is offered to each and kept.
    /// </summary>
    [Fact]
    public async Task A_passphrase_is_offered_to_every_hop_and_kept()
    {
        var user = new ScriptedUser(SshInteractionResponse.FromSecret("key-pass"));
        RemoteMuxInteractionHandler handler = Handler(user);
        await RememberAsync(handler, Passphrase);

        RemoteMuxInteractionHandler.Attempt automatic = handler.BeginAttempt(interactive: false, passwordsReplayable: false);
        Assert.True((await automatic.HandleAsync(HostKey("SHA256:trusted"), Ct)).IsAccepted);   // the jump host
        Assert.Equal("key-pass", (await automatic.HandleAsync(Passphrase, Ct)).Secret);
        Assert.True((await automatic.HandleAsync(HostKey("SHA256:trusted"), Ct)).IsAccepted);   // the target
        Assert.Equal("key-pass", (await automatic.HandleAsync(Passphrase, Ct)).Secret);
        automatic.Succeeded();

        Assert.True(handler.Remembers(SshInteractionKind.Passphrase));
        Assert.Single(user.Asked);
    }

    /// <summary>
    /// Review fix round 1: a native password prompt does not say which hop asks, and each hop asks the
    /// same "Password:", so across a jump chain a remembered password could reach the jump host. A profile
    /// with jump hops therefore never remembers or replays a password.
    /// </summary>
    [Fact]
    public async Task With_jump_hops_a_password_is_neither_remembered_nor_replayed()
    {
        var user = new ScriptedUser(SshInteractionResponse.FromSecret("pw"), SshInteractionResponse.FromSecret("other"));
        RemoteMuxInteractionHandler handler = Handler(user);
        await RememberAsync(handler, Password);   // remembered while the profile had no jump hop

        RemoteMuxInteractionHandler.Attempt viaJumpHost = handler.BeginAttempt(interactive: true, passwordsReplayable: false);
        Assert.False(handler.Remembers(SshInteractionKind.Password));   // dropped as soon as hops appear
        Assert.Equal("other", (await viaJumpHost.HandleAsync(Password, Ct)).Secret);
        viaJumpHost.Succeeded();

        Assert.False(handler.Remembers(SshInteractionKind.Password));
        await Assert.ThrowsAsync<RemoteMuxPromptAbortedException>(
            () => handler.BeginAttempt(interactive: false, passwordsReplayable: false).HandleAsync(Password, Ct));
    }

    [Fact]
    public async Task A_cancelled_prompt_remembers_nothing()
    {
        RemoteMuxInteractionHandler handler = Handler(new ScriptedUser(SshInteractionResponse.Cancel()));
        RemoteMuxInteractionHandler.Attempt attempt = handler.BeginAttempt(interactive: true);

        Assert.True((await attempt.HandleAsync(Password, Ct)).IsCanceled);
        attempt.Succeeded();

        Assert.False(handler.Remembers(SshInteractionKind.Password));
    }

    [Fact]
    public async Task Forget_drops_every_remembered_secret_and_a_late_success_does_not_bring_one_back()
    {
        RemoteMuxInteractionHandler handler = Handler(new ScriptedUser(
            SshInteractionResponse.FromSecret("p"), SshInteractionResponse.FromSecret("q"), SshInteractionResponse.FromSecret("late")));
        await RememberAsync(handler, Password);
        await RememberAsync(handler, Passphrase);
        RemoteMuxInteractionHandler.Attempt late = handler.BeginAttempt(interactive: true);

        handler.Forget();
        Assert.Equal("late", (await late.HandleAsync(Password, Ct)).Secret);   // memory is empty: the user
        late.Succeeded();

        Assert.False(handler.Remembers(SshInteractionKind.Password));
        Assert.False(handler.Remembers(SshInteractionKind.Passphrase));
    }

    [Fact]
    public async Task Without_a_user_an_interactive_attempt_answers_as_an_automatic_one()
    {
        RemoteMuxInteractionHandler.Attempt attempt = Handler(user: null).BeginAttempt(interactive: true);

        await Assert.ThrowsAsync<RemoteMuxPromptAbortedException>(() => attempt.HandleAsync(Password, Ct));
        Assert.True((await attempt.HandleAsync(HostKey("SHA256:trusted"), Ct)).IsAccepted);
        Assert.True((await attempt.HandleAsync(HostKey("SHA256:new"), Ct)).IsCanceled);
    }

    /// <summary>
    /// Review fix round 2: a remembered secret followed by another auth prompt is forgotten at once, not
    /// only when the attempt ends - an attempt that then dies some other way (a dialog timing out ends as
    /// ProxyFailed) must not leave it for one more replay.
    /// </summary>
    [Fact]
    public async Task A_superseded_remembered_secret_is_forgotten_at_once()
    {
        var user = new ScriptedUser(SshInteractionResponse.FromSecret("old"), SshInteractionResponse.FromKeyboardResponses("code"));
        RemoteMuxInteractionHandler handler = Handler(user);
        await RememberAsync(handler, Password);
        RemoteMuxInteractionHandler.Attempt attempt = handler.BeginAttempt(interactive: true);
        Assert.Equal("old", (await attempt.HandleAsync(Password, Ct)).Secret);

        await attempt.HandleAsync(Keyboard, Ct);

        Assert.False(handler.Remembers(SshInteractionKind.Password));
    }

    /// <summary>Review fix round 2: once the greeting arrived, what the attempt offered got it in; a later failure (the hello) does not make it wrong.</summary>
    [Fact]
    public async Task Refused_after_Succeeded_forgets_nothing()
    {
        RemoteMuxInteractionHandler handler = Handler(new ScriptedUser(SshInteractionResponse.FromSecret("pw")));
        await RememberAsync(handler, Password);
        RemoteMuxInteractionHandler.Attempt attempt = handler.BeginAttempt(interactive: false);
        Assert.Equal("pw", (await attempt.HandleAsync(Password, Ct)).Secret);
        attempt.Succeeded();

        attempt.Refused();

        Assert.True(handler.Remembers(SshInteractionKind.Password));
    }

    /// <summary>The vault as the app reads it for a remote host (<see cref="RemoteMuxHostFactory.ReadSavedPassword"/>): a value, and every read counted.</summary>
    private sealed class SavedPasswords(string? value)
    {
        private int _reads;

        public string? Value { get; set; } = value;

        public int Reads => Volatile.Read(ref _reads);

        public string? Read(Ntilde.Platform.Ssh.Models.SshProfile profile)
        {
            Interlocked.Increment(ref _reads);
            return Value;
        }
    }

    private static RemoteMuxInteractionHandler Handler(ISshInteractionHandler? user, SavedPasswords saved) =>
        new(user, request => request.Fingerprint == "SHA256:trusted", saved.Read);

    private static readonly Ntilde.Platform.Ssh.Models.SshProfile Box = RemoteMuxConnectorTests.Profile();

    /// <summary>
    /// The user's choice after the smoke test: an automatic attempt signs in with the profile's saved password, with no
    /// UI - once. A second password prompt in the same attempt means the server refused it: the attempt aborts (NeedsUser
    /// in the connector) rather than offer it again, or send an empty answer.
    /// </summary>
    [Fact]
    public async Task An_automatic_password_prompt_is_answered_once_from_the_saved_password()
    {
        var saved = new SavedPasswords("s3cret");
        var user = new ScriptedUser(SshInteractionResponse.FromSecret("never asked"));
        RemoteMuxInteractionHandler.Attempt automatic = Handler(user, saved).BeginAttempt(interactive: false, savedPasswordProfile: Box);
        Assert.True(automatic.MaySignInWithSavedPassword);
        Assert.False(automatic.SavedPasswordOffered);

        SshInteractionResponse first = await automatic.HandleAsync(Password, Ct);

        Assert.Equal("s3cret", first.Secret);
        Assert.False(first.IsCanceled);
        Assert.False(first.RememberPasswordInVault);   // it came from there
        Assert.True(automatic.SavedPasswordOffered);
        var second = await Assert.ThrowsAsync<RemoteMuxPromptAbortedException>(() => automatic.HandleAsync(Password, Ct));
        Assert.Equal(SshInteractionKind.Password, second.Prompt);
        Assert.Equal(1, saved.Reads);
        Assert.Empty(user.Asked);
    }

    /// <summary>The saved password lives in the vault: an attempt that got in with it does not copy it into the host's memory.</summary>
    [Fact]
    public async Task The_saved_password_is_not_remembered_in_memory()
    {
        var saved = new SavedPasswords("s3cret");
        RemoteMuxInteractionHandler handler = Handler(new ScriptedUser(), saved);
        RemoteMuxInteractionHandler.Attempt automatic = handler.BeginAttempt(interactive: false, savedPasswordProfile: Box);

        await automatic.HandleAsync(Password, Ct);
        automatic.Succeeded();

        Assert.False(handler.Remembers(SshInteractionKind.Password));
    }

    /// <summary>
    /// Keyboard-interactive keeps its rule (any round with questions aborts), a passphrase is still declined, and with
    /// nothing saved a password prompt aborts as before - none of them reads the vault for the password's sake.
    /// </summary>
    [Fact]
    public async Task Keyboard_interactive_a_passphrase_and_an_empty_vault_keep_their_rules()
    {
        var saved = new SavedPasswords(null);
        RemoteMuxInteractionHandler handler = Handler(new ScriptedUser(), saved);
        RemoteMuxInteractionHandler.Attempt automatic = handler.BeginAttempt(interactive: false, savedPasswordProfile: Box);

        Assert.True((await automatic.HandleAsync(Passphrase, Ct)).IsCanceled);
        await Assert.ThrowsAsync<RemoteMuxPromptAbortedException>(() => automatic.HandleAsync(Keyboard, Ct));
        await Assert.ThrowsAsync<RemoteMuxPromptAbortedException>(() => automatic.HandleAsync(Password, Ct));

        Assert.False(automatic.SavedPasswordOffered);
        Assert.Equal(1, saved.Reads);   // the password prompt's look, which found nothing
    }

    /// <summary>
    /// A native password prompt does not say which hop asks: with jump hops (passwords not replayable), the saved
    /// password is never offered, as a remembered one never is. The vault is not even read.
    /// </summary>
    [Fact]
    public async Task With_jump_hops_the_saved_password_is_never_offered()
    {
        var saved = new SavedPasswords("s3cret");
        RemoteMuxInteractionHandler.Attempt viaJumpHost = Handler(new ScriptedUser(), saved)
            .BeginAttempt(interactive: false, passwordsReplayable: false, savedPasswordProfile: Box);

        Assert.False(viaJumpHost.MaySignInWithSavedPassword);
        await Assert.ThrowsAsync<RemoteMuxPromptAbortedException>(() => viaJumpHost.HandleAsync(Password, Ct));
        Assert.False(viaJumpHost.OfferSavedPassword());
        Assert.False(viaJumpHost.SavedPasswordOffered);
        Assert.Equal(0, saved.Reads);
    }

    /// <summary>A user is waiting: the window's handler answers (from the vault itself, or a dialog); this one reads nothing.</summary>
    [Fact]
    public async Task An_interactive_attempt_leaves_the_saved_password_to_the_windows_handler()
    {
        var saved = new SavedPasswords("s3cret");
        var user = new ScriptedUser(SshInteractionResponse.FromSecret("typed"));
        RemoteMuxInteractionHandler.Attempt attempt = Handler(user, saved).BeginAttempt(interactive: true, savedPasswordProfile: Box);

        Assert.Equal("typed", (await attempt.HandleAsync(Password, Ct)).Secret);

        Assert.False(attempt.MaySignInWithSavedPassword);
        Assert.False(attempt.OfferSavedPassword());
        Assert.Equal(0, saved.Reads);
        Assert.Single(user.Asked);
    }

    /// <summary>
    /// A password the host remembered comes first. Refused, it is not followed by the saved one in the same attempt: one
    /// password per automatic attempt, whatever its source.
    /// </summary>
    [Fact]
    public async Task A_remembered_password_comes_first_and_a_refused_one_is_not_followed_by_the_saved_one()
    {
        var saved = new SavedPasswords("s3cret");
        RemoteMuxInteractionHandler handler = Handler(new ScriptedUser(SshInteractionResponse.FromSecret("remembered")), saved);
        await RememberAsync(handler, Password);
        RemoteMuxInteractionHandler.Attempt automatic = handler.BeginAttempt(interactive: false, savedPasswordProfile: Box);

        Assert.Equal("remembered", (await automatic.HandleAsync(Password, Ct)).Secret);
        await Assert.ThrowsAsync<RemoteMuxPromptAbortedException>(() => automatic.HandleAsync(Password, Ct));

        Assert.False(automatic.SavedPasswordOffered);
        Assert.Equal(0, saved.Reads);
    }

    /// <summary>
    /// The OpenSSH shape: the attempt's transport is built once, and its askpass helper answers the password itself.
    /// Offering it reads the vault (and keeps nothing), and counts it as offered only when there is one.
    /// </summary>
    [Fact]
    public void Offering_the_saved_password_to_an_askpass_reads_the_vault_and_counts_only_when_there_is_one()
    {
        var saved = new SavedPasswords(null);
        RemoteMuxInteractionHandler handler = Handler(new ScriptedUser(), saved);
        RemoteMuxInteractionHandler.Attempt nothingSaved = handler.BeginAttempt(interactive: false, savedPasswordProfile: Box);
        RemoteMuxInteractionHandler.Attempt noProfile = handler.BeginAttempt(interactive: false);

        Assert.False(nothingSaved.OfferSavedPassword());
        Assert.False(nothingSaved.SavedPasswordOffered);
        saved.Value = "s3cret";
        RemoteMuxInteractionHandler.Attempt withSaved = handler.BeginAttempt(interactive: false, savedPasswordProfile: Box);
        Assert.True(withSaved.OfferSavedPassword());
        Assert.True(withSaved.SavedPasswordOffered);
        Assert.False(noProfile.MaySignInWithSavedPassword);   // the connector names no profile for a retargeted attempt
        Assert.False(noProfile.OfferSavedPassword());
        Assert.Equal(2, saved.Reads);
    }

    /// <summary>
    /// A refused saved password is tried once per host, not once per attempt: until an attempt gets in, the host's later
    /// automatic attempts - a kill's delivery, the next loss - offer it no more. Once one gets in, they may again.
    /// </summary>
    [Fact]
    public async Task After_a_refusal_the_host_offers_the_saved_password_no_more_until_an_attempt_gets_in()
    {
        var saved = new SavedPasswords("wrong");
        RemoteMuxInteractionHandler handler = Handler(new ScriptedUser(SshInteractionResponse.FromSecret("right")), saved);
        RemoteMuxInteractionHandler.Attempt refused = handler.BeginAttempt(interactive: false, savedPasswordProfile: Box);
        await refused.HandleAsync(Password, Ct);

        refused.SavedPasswordRefused();

        RemoteMuxInteractionHandler.Attempt next = handler.BeginAttempt(interactive: false, savedPasswordProfile: Box);
        Assert.False(next.MaySignInWithSavedPassword);
        Assert.False(next.OfferSavedPassword());
        await Assert.ThrowsAsync<RemoteMuxPromptAbortedException>(() => next.HandleAsync(Password, Ct));
        Assert.Equal(1, saved.Reads);

        RemoteMuxInteractionHandler.Attempt enter = handler.BeginAttempt(interactive: true, savedPasswordProfile: Box);
        await enter.HandleAsync(Password, Ct);
        enter.Succeeded();
        saved.Value = "right";
        Assert.True(handler.BeginAttempt(interactive: false, savedPasswordProfile: Box).MaySignInWithSavedPassword);
    }

    /// <summary>
    /// A remembered password the server refused (its attempt failed SSH) says the server's password changed: the saved
    /// one is likely as stale, so the host's automatic attempts do not try it after it either.
    /// </summary>
    [Fact]
    public async Task A_refused_remembered_password_stops_the_saved_one_too()
    {
        var saved = new SavedPasswords("s3cret");
        RemoteMuxInteractionHandler handler = Handler(new ScriptedUser(SshInteractionResponse.FromSecret("old")), saved);
        await RememberAsync(handler, Password);
        RemoteMuxInteractionHandler.Attempt automatic = handler.BeginAttempt(interactive: false, savedPasswordProfile: Box);
        Assert.Equal("old", (await automatic.HandleAsync(Password, Ct)).Secret);

        automatic.Refused();

        Assert.False(handler.BeginAttempt(interactive: false, savedPasswordProfile: Box).MaySignInWithSavedPassword);
        Assert.Equal(0, saved.Reads);
    }

    /// <summary>The native password prompt as rusty_ssh's first one reaches the window's handler: vault reuse allowed.</summary>
    private static SshInteractionRequest VaultReusablePassword { get; } = new()
    {
        Kind = SshInteractionKind.Password,
        Prompt = "Password:",
        ProfileId = Box.Id,
        ProfileName = Box.Name,
        ProfileUser = Box.User,
        ProfileHost = Box.Host,
        AllowVaultPasswordReuse = true,
        RememberPasswordInVault = true,
    };

    /// <summary>
    /// The coordinator's follow-up: after the host's saved password was refused, the user's Enter must not send it again
    /// through the window's handler, which answers a first password prompt from the vault. The attempt passes the prompt
    /// on with vault reuse off, so the dialog comes at once; its "Remember" still saves (the request still allows that).
    /// </summary>
    [Fact]
    public async Task After_a_refusal_a_user_attempt_asks_without_the_vault()
    {
        var user = new ScriptedUser(SshInteractionResponse.FromSecret("typed"));
        RemoteMuxInteractionHandler handler = Handler(user, new SavedPasswords("stale"));
        RemoteMuxInteractionHandler.Attempt automatic = handler.BeginAttempt(interactive: false, savedPasswordProfile: Box);
        await automatic.HandleAsync(Password, Ct);
        automatic.SavedPasswordRefused();

        RemoteMuxInteractionHandler.Attempt enter = handler.BeginAttempt(interactive: true, savedPasswordProfile: Box);
        SshInteractionResponse answer = await enter.HandleAsync(VaultReusablePassword, Ct);

        Assert.True(enter.AvoidsSavedPassword);
        Assert.Equal("typed", answer.Secret);
        SshInteractionRequest asked = Assert.Single(user.Asked);
        Assert.False(asked.AllowVaultPasswordReuse);
        Assert.True(asked.RememberPasswordInVault);
        Assert.Equal(Box.Id, asked.ProfileId);
    }

    /// <summary>Without a refusal on the host, a user's attempt passes the prompt on as it came: the vault may answer it once.</summary>
    [Fact]
    public async Task Without_a_refusal_a_user_attempt_leaves_vault_reuse_as_it_came()
    {
        var user = new ScriptedUser(SshInteractionResponse.FromSecret("typed"));
        RemoteMuxInteractionHandler.Attempt enter = Handler(user, new SavedPasswords("s3cret")).BeginAttempt(interactive: true, savedPasswordProfile: Box);

        await enter.HandleAsync(VaultReusablePassword, Ct);

        Assert.False(enter.AvoidsSavedPassword);
        Assert.True(Assert.Single(user.Asked).AllowVaultPasswordReuse);
    }

    /// <summary>Once a user's attempt got in, signing in works again: the next user attempt may use the vault, as before.</summary>
    [Fact]
    public async Task A_user_attempt_that_gets_in_ends_the_avoidance()
    {
        RemoteMuxInteractionHandler handler = Handler(new ScriptedUser(SshInteractionResponse.FromSecret("typed")), new SavedPasswords("stale"));
        RemoteMuxInteractionHandler.Attempt automatic = handler.BeginAttempt(interactive: false, savedPasswordProfile: Box);
        await automatic.HandleAsync(Password, Ct);
        automatic.SavedPasswordRefused();
        RemoteMuxInteractionHandler.Attempt enter = handler.BeginAttempt(interactive: true, savedPasswordProfile: Box);
        await enter.HandleAsync(VaultReusablePassword, Ct);

        enter.Succeeded();

        Assert.False(handler.BeginAttempt(interactive: true, savedPasswordProfile: Box).AvoidsSavedPassword);
    }

    /// <summary>A passphrase refused is no reason to stop offering the saved password: it never reached the server.</summary>
    [Fact]
    public async Task A_refused_remembered_passphrase_leaves_the_saved_password_alone()
    {
        var saved = new SavedPasswords("s3cret");
        RemoteMuxInteractionHandler handler = Handler(new ScriptedUser(SshInteractionResponse.FromSecret("key-pass")), saved);
        await RememberAsync(handler, Passphrase);
        RemoteMuxInteractionHandler.Attempt automatic = handler.BeginAttempt(interactive: false, savedPasswordProfile: Box);
        await automatic.HandleAsync(Passphrase, Ct);

        automatic.Refused();

        Assert.True(handler.BeginAttempt(interactive: false, savedPasswordProfile: Box).MaySignInWithSavedPassword);
    }
}
