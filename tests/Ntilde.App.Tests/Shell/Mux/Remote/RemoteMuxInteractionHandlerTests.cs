using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Tests.Shell.Mux.Remote;

/// <summary>
/// A remote host's prompts (Phase 4 ruling: automatic reconnects are non-interactive). A user-started
/// attempt may ask the user, and a password or passphrase it used successfully is remembered in memory
/// for the host's lifetime. An automatic attempt never asks: it answers from that memory, accepts only a
/// host key already trusted, and cancels everything else so it fails quietly.
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

    [Fact]
    public async Task A_secret_used_by_a_successful_attempt_answers_later_automatic_attempts_without_asking()
    {
        var user = new ScriptedUser(SshInteractionResponse.FromSecret("hunter2"), SshInteractionResponse.FromSecret("key-pass"));
        RemoteMuxInteractionHandler handler = Handler(user);
        RemoteMuxInteractionHandler.Attempt first = handler.BeginAttempt(interactive: true);
        Assert.Equal("hunter2", (await first.HandleAsync(Password, Ct)).Secret);
        Assert.Equal("key-pass", (await first.HandleAsync(Passphrase, Ct)).Secret);
        first.Succeeded();

        RemoteMuxInteractionHandler.Attempt automatic = handler.BeginAttempt(interactive: false);
        SshInteractionResponse password = await automatic.HandleAsync(Password, Ct);
        SshInteractionResponse passphrase = await automatic.HandleAsync(Passphrase, Ct);

        Assert.Equal("hunter2", password.Secret);
        Assert.False(password.IsCanceled);
        Assert.False(password.RememberPasswordInVault);   // memory only: never written anywhere
        Assert.Equal("key-pass", passphrase.Secret);
        Assert.Equal(2, user.Asked.Count);
    }

    [Fact]
    public async Task An_answer_from_an_attempt_that_never_succeeded_is_not_remembered()
    {
        RemoteMuxInteractionHandler handler = Handler(new ScriptedUser(SshInteractionResponse.FromSecret("typo")));
        RemoteMuxInteractionHandler.Attempt failed = handler.BeginAttempt(interactive: true);
        await failed.HandleAsync(Password, Ct);

        Assert.False(handler.Remembers(SshInteractionKind.Password));
        Assert.True((await handler.BeginAttempt(interactive: false).HandleAsync(Password, Ct)).IsCanceled);
    }

    [Fact]
    public async Task An_automatic_attempt_never_asks_the_user()
    {
        var user = new ScriptedUser(SshInteractionResponse.FromSecret("x"), SshInteractionResponse.AcceptHostKey());
        RemoteMuxInteractionHandler.Attempt automatic = Handler(user).BeginAttempt(interactive: false);

        Assert.True((await automatic.HandleAsync(Password, Ct)).IsCanceled);
        Assert.True((await automatic.HandleAsync(Passphrase, Ct)).IsCanceled);
        Assert.True((await automatic.HandleAsync(Keyboard, Ct)).IsCanceled);
        Assert.True((await automatic.HandleAsync(HostKey("SHA256:new"), Ct)).IsCanceled);
        Assert.Empty(user.Asked);
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
        // A one-time code is never replayed.
        Assert.True((await handler.BeginAttempt(interactive: false).HandleAsync(Keyboard, Ct)).IsCanceled);
    }

    [Fact]
    public async Task Asked_again_after_a_remembered_answer_means_it_was_wrong_so_it_is_forgotten_and_cancelled()
    {
        RemoteMuxInteractionHandler handler = Handler(new ScriptedUser(SshInteractionResponse.FromSecret("old")));
        RemoteMuxInteractionHandler.Attempt first = handler.BeginAttempt(interactive: true);
        await first.HandleAsync(Password, Ct);
        first.Succeeded();

        RemoteMuxInteractionHandler.Attempt automatic = handler.BeginAttempt(interactive: false);
        Assert.Equal("old", (await automatic.HandleAsync(Password, Ct)).Secret);
        SshInteractionResponse again = await automatic.HandleAsync(Password, Ct);   // the server refused it

        Assert.True(again.IsCanceled);
        Assert.False(handler.Remembers(SshInteractionKind.Password));
    }

    [Fact]
    public async Task An_interactive_attempt_offers_the_remembered_secret_first_and_asks_once_it_is_refused()
    {
        var user = new ScriptedUser(SshInteractionResponse.FromSecret("old"), SshInteractionResponse.FromSecret("new"));
        RemoteMuxInteractionHandler handler = Handler(user);
        RemoteMuxInteractionHandler.Attempt first = handler.BeginAttempt(interactive: true);
        await first.HandleAsync(Password, Ct);
        first.Succeeded();

        RemoteMuxInteractionHandler.Attempt retry = handler.BeginAttempt(interactive: true);
        Assert.Equal("old", (await retry.HandleAsync(Password, Ct)).Secret);   // no dialog
        Assert.Single(user.Asked);
        Assert.Equal("new", (await retry.HandleAsync(Password, Ct)).Secret);   // refused: now the user
        retry.Succeeded();

        Assert.Equal(2, user.Asked.Count);
        Assert.Equal("new", (await handler.BeginAttempt(interactive: false).HandleAsync(Password, Ct)).Secret);
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
    public async Task Forget_drops_every_remembered_secret_and_a_late_success_does_not_bring_it_back()
    {
        RemoteMuxInteractionHandler handler = Handler(new ScriptedUser(SshInteractionResponse.FromSecret("p"), SshInteractionResponse.FromSecret("q")));
        RemoteMuxInteractionHandler.Attempt first = handler.BeginAttempt(interactive: true);
        await first.HandleAsync(Password, Ct);
        await first.HandleAsync(Passphrase, Ct);
        first.Succeeded();
        RemoteMuxInteractionHandler.Attempt late = handler.BeginAttempt(interactive: true);
        await late.HandleAsync(Password, Ct);   // answered from memory

        handler.Forget();
        late.Succeeded();

        Assert.False(handler.Remembers(SshInteractionKind.Password));
        Assert.False(handler.Remembers(SshInteractionKind.Passphrase));
    }

    [Fact]
    public async Task Without_a_user_an_interactive_attempt_answers_as_an_automatic_one()
    {
        RemoteMuxInteractionHandler.Attempt attempt = Handler(user: null).BeginAttempt(interactive: true);

        Assert.True((await attempt.HandleAsync(Password, Ct)).IsCanceled);
        Assert.True((await attempt.HandleAsync(HostKey("SHA256:trusted"), Ct)).IsAccepted);
        Assert.True((await attempt.HandleAsync(HostKey("SHA256:new"), Ct)).IsCanceled);
    }
}
