using Ntilde.Platform.Ssh.Interactions;
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
        Assert.True((await automatic.HandleAsync(Keyboard, Ct)).IsCanceled);
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
        Assert.True((await handler.BeginAttempt(interactive: false, passwordsReplayable: false).HandleAsync(Password, Ct)).IsCanceled);
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

        Assert.True((await attempt.HandleAsync(Password, Ct)).IsCanceled);
        Assert.True((await attempt.HandleAsync(HostKey("SHA256:trusted"), Ct)).IsAccepted);
        Assert.True((await attempt.HandleAsync(HostKey("SHA256:new"), Ct)).IsCanceled);
    }
}
