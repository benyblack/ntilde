using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.Native;

namespace Ntilde.Shell.Mux.Remote;

/// <summary>
/// The SSH prompts of one remote host's connections, on the native backend (Phase 4 spec §8.3; ruling:
/// automatic reconnects are non-interactive). One per remote <see cref="MuxConnectionHost"/>, through its
/// <see cref="RemoteMuxConnector"/>, which begins an <see cref="Attempt"/> per connect.
/// </summary>
/// <remarks>
/// <para>
/// A password or passphrase that got a connection in is remembered, in memory only and per prompt kind,
/// until <see cref="Forget"/> (the host's dispose). Every attempt offers it first: a network drop then
/// reconnects without asking again, as a plain native SSH tab's reconnect does.
/// </para>
/// <para>
/// A remembered secret that may have been refused is forgotten, never replayed: replaying a stale
/// password every few seconds is a lockout waiting to happen. The native layer asks for a password once
/// and then falls to keyboard-interactive, so a refusal shows as another auth prompt after it, or as the
/// attempt failing SSH (<see cref="Attempt.Refused"/>); see <see cref="Attempt"/>.
/// </para>
/// <para>
/// A native password prompt does not say which hop of a jump chain asks, and each hop asks the same
/// "Password:". So for a profile with jump hops a password is never remembered or replayed (a
/// passphrase is: it unlocks a local key, whichever hop asks).
/// </para>
/// <para>
/// Past memory, an interactive attempt (a user is waiting) asks the window's handler, which may show a
/// dialog. An automatic one never does: it accepts a host key only when the known-hosts store already
/// trusts it - the native layer asks about the host key on every connect, known or not - and leaves
/// every other prompt unanswered (see <see cref="Attempt"/>), so the attempt fails quietly. One that
/// failed for want of a secret only the user can give fails as needing the user, which stops the
/// reconnect loop; a refused host key leaves it backing off.
/// </para>
/// <para>
/// The one exception (the user's choice after the Phase 4 smoke test): the profile's password saved in the vault. An
/// automatic attempt may sign in with it, with no UI, once - <see cref="Attempt.MaySignInWithSavedPassword"/> - unless
/// the profile has jump hops (the same rule as for a remembered password) or the host's destination moved
/// (the connector then names no profile). A native attempt answers its first password prompt with it; an OpenSSH one
/// hands it to ssh's askpass (<see cref="Attempt.OfferSavedPassword"/>). Refused, it is not offered again by this host's
/// automatic attempts until one gets in (<see cref="Attempt.SavedPasswordRefused"/>): a wrong saved password must not
/// add a failed login on every reconnect or kill delivery. The same holds once a remembered password was refused: the
/// server's password changed, and the saved one is likely as stale.
/// </para>
/// </remarks>
internal sealed class RemoteMuxInteractionHandler
{
    private readonly ISshInteractionHandler? _user;
    private readonly Func<SshInteractionRequest, bool> _isTrustedHostKey;
    private readonly Func<SshProfile, string?>? _savedPassword;
    private readonly object _gate = new();
    private readonly Dictionary<SshInteractionKind, string> _remembered = []; // guarded by _gate
    private int _generation;                                                  // guarded by _gate; bumped by Forget
    private bool _passwordRefused;                                            // guarded by _gate; until an attempt gets in

    /// <param name="user">The window's handler (dialogs, the vault, the known-hosts store), or null for none.</param>
    /// <param name="isTrustedHostKey">
    /// Whether a host-key request names a key the user already trusts; the app's native known-hosts store
    /// by default, the one the window's handler records accepted keys in.
    /// </param>
    /// <param name="savedPassword">
    /// Reads the profile's password saved in the vault, or null for none (<see cref="RemoteMuxHostFactory.ReadSavedPassword"/>
    /// in the app). Called off the UI thread, only for an automatic attempt that may use it. Null: automatic attempts
    /// never sign in with a saved password.
    /// </param>
    public RemoteMuxInteractionHandler(
        ISshInteractionHandler? user,
        Func<SshInteractionRequest, bool>? isTrustedHostKey = null,
        Func<SshProfile, string?>? savedPassword = null)
    {
        _user = user;
        _isTrustedHostKey = isTrustedHostKey ?? IsTrustedInTheAppsKnownHosts;
        _savedPassword = savedPassword;
    }

    /// <summary>
    /// The handler for one connect attempt; <paramref name="interactive"/> when a user is waiting on it.
    /// <paramref name="passwordsReplayable"/> is false for a profile with jump hops: its passwords are
    /// neither offered from memory nor remembered, and a password remembered before is dropped.
    /// <paramref name="savedPasswordProfile"/> is the attempt's profile when its saved password may sign an automatic
    /// attempt in to that destination - null when the host's destination moved away from what the profile names now; the
    /// attempt then may only when it is automatic, its passwords are replayable, and no password was refused on this host
    /// since an attempt last got in. An interactive attempt begun while one was refused avoids the saved password
    /// altogether (<see cref="Attempt.AvoidsSavedPassword"/>): the user is asked.
    /// </summary>
    public Attempt BeginAttempt(bool interactive, bool passwordsReplayable = true, SshProfile? savedPasswordProfile = null)
    {
        lock (_gate)
        {
            if (!passwordsReplayable) _remembered.Remove(SshInteractionKind.Password);
            SshProfile? savedFor = !interactive && passwordsReplayable && !_passwordRefused && _savedPassword is not null ? savedPasswordProfile : null;
            return new Attempt(this, interactive, passwordsReplayable, _generation, savedFor, avoidsSavedPassword: interactive && _passwordRefused);
        }
    }

    /// <summary>Drops every remembered secret, and any an attempt still running would have remembered.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _remembered.Clear();
            _generation++;
            _passwordRefused = false;
        }
    }

    /// <summary>A password was refused on this host: its automatic attempts offer the saved one no more until one gets in.</summary>
    private void MarkPasswordRefused(int generation)
    {
        lock (_gate)
        {
            if (generation == _generation) _passwordRefused = true;
        }
    }

    private void ClearPasswordRefused()
    {
        lock (_gate) _passwordRefused = false;
    }

    /// <summary>The profile's saved password, or null for none.</summary>
    private string? ReadSavedPassword(SshProfile profile) =>
        _savedPassword?.Invoke(profile) is { Length: > 0 } saved ? saved : null;

    internal bool Remembers(SshInteractionKind kind)
    {
        lock (_gate) return _remembered.ContainsKey(kind);
    }

    private bool TryRecall(SshInteractionKind kind, out string secret)
    {
        lock (_gate) return _remembered.TryGetValue(kind, out secret!);
    }

    /// <summary>
    /// Forgets each of <paramref name="forget"/> that is still what is remembered for its kind, then -
    /// unless the host forgot everything since <paramref name="generation"/> - remembers <paramref name="remember"/>.
    /// </summary>
    private void Settle(int generation, IEnumerable<(SshInteractionKind Kind, string Secret)> forget, IEnumerable<(SshInteractionKind Kind, string Secret)> remember)
    {
        lock (_gate)
        {
            foreach ((SshInteractionKind kind, string secret) in forget)
            {
                if (_remembered.TryGetValue(kind, out string? current) && current == secret) _remembered.Remove(kind);
            }

            if (generation != _generation) return; // forgotten meanwhile: the host is going away
            foreach ((SshInteractionKind kind, string secret) in remember) _remembered[kind] = secret;
        }
    }

    /// <summary>
    /// The app's store, bound to <see cref="AppPaths.NativeKnownHostsFilePath"/> on first use and kept for the
    /// process's life. Code that needs another store (a test with its own app-data root) passes
    /// <c>isTrustedHostKey</c> instead, through <see cref="RemoteMuxHostFactory.Create"/>.
    /// </summary>
    private static readonly Lazy<NativeKnownHostsStore> KnownHosts = new(() => new NativeKnownHostsStore(AppPaths.NativeKnownHostsFilePath));

    private static bool IsTrustedInTheAppsKnownHosts(SshInteractionRequest request) =>
        KnownHosts.Value.CheckHost(request.Host, request.Port, request.Algorithm, request.Fingerprint) == NativeKnownHostMatch.Trusted;

    private static bool IsSecretPrompt(SshInteractionKind kind) => kind is SshInteractionKind.Password or SshInteractionKind.Passphrase;

    private static bool IsHostKeyPrompt(SshInteractionKind kind) => kind is SshInteractionKind.UnknownHostKey or SshInteractionKind.ChangedHostKey;

    /// <summary>
    /// One connect attempt's prompts, in the order its connection raises them (the native transport's
    /// poll thread waits for each answer).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each secret answered is tracked. Another auth prompt after it (password, passphrase,
    /// keyboard-interactive) means it did not get that hop in, so it is superseded: never remembered and,
    /// if it came from memory, forgotten at once and not offered again in this attempt. A host-key prompt
    /// after it means the next hop's connection has begun, so it did get its hop in: it is proven, and the
    /// same secret may be offered to the next hop. <see cref="Succeeded"/> remembers what was not
    /// superseded; <see cref="Refused"/>, unless the attempt succeeded, forgets everything it offered from
    /// memory.
    /// </para>
    /// <para>
    /// With nobody to ask (an automatic attempt, or no window handler) and nothing remembered, a password
    /// prompt or a keyboard-interactive prompt with questions is not answered: <see cref="HandleAsync"/>
    /// throws <see cref="RemoteMuxPromptAbortedException"/>. A cancel would not do: the native layer
    /// submits it as an empty password or empty answers, which the server counts as a failed login - on
    /// every reconnect, until fail2ban or a lockout steps in. A thrown handler instead makes the native
    /// exec channel close the session without answering (its documented contract), and closing wakes
    /// rusty_ssh's pending prompt with no answer, so its auth stops before sending anything. A passphrase
    /// is still cancelled: it only unlocks a local key, and nothing reaches the server. It is recorded,
    /// though (<see cref="DeclinedPrompt"/>; codex D3): when the attempt then fails SSH, nothing else got
    /// in, and the connector reports it as needing the user, as it does an aborted prompt.
    /// </para>
    /// <para>
    /// An automatic attempt that <see cref="MaySignInWithSavedPassword"/> answers a password prompt it has nothing
    /// remembered for with the profile's saved password - once, and only when it has offered no other password: a second
    /// password prompt means the first answer was refused, and aborts as above. The saved password is not tracked or
    /// remembered: it stays in the vault, and the next attempt reads it there.
    /// </para>
    /// </remarks>
    internal sealed class Attempt : ISshInteractionHandler
    {
        private readonly RemoteMuxInteractionHandler _owner;
        private readonly bool _passwordsReplayable;
        private readonly int _generation;
        private readonly SshProfile? _savedPasswordProfile;
        private readonly object _gate = new();
        private readonly List<Answer> _answers = []; // guarded by _gate; in the order given
        private bool _succeeded;                     // guarded by _gate
        private SshInteractionKind? _abortedPrompt;  // guarded by _gate
        private SshInteractionKind? _declinedPrompt; // guarded by _gate
        private bool _savedPasswordOffered;          // guarded by _gate
        private bool _savedPasswordAnswered;         // guarded by _gate

        internal Attempt(
            RemoteMuxInteractionHandler owner, bool interactive, bool passwordsReplayable, int generation, SshProfile? savedPasswordProfile, bool avoidsSavedPassword)
        {
            _owner = owner;
            Interactive = interactive;
            _passwordsReplayable = passwordsReplayable;
            _generation = generation;
            _savedPasswordProfile = savedPasswordProfile;
            AvoidsSavedPassword = avoidsSavedPassword;
        }

        /// <summary>True when a user is waiting on this attempt: prompts may reach them.</summary>
        public bool Interactive { get; }

        /// <summary>
        /// This automatic attempt may sign in with the profile's saved password, if the vault holds one: no jump hops,
        /// the profile's own destination, and no password refused on this host since an attempt last got in.
        /// </summary>
        public bool MaySignInWithSavedPassword => _savedPasswordProfile is not null;

        /// <summary>
        /// This user's attempt began while a password was refused on the host (the saved one, or a remembered one): nothing
        /// answers from the vault - the window's handler gets each password prompt with vault reuse off, and OpenSSH's
        /// askpass runs without the vault (<see cref="RemoteMuxTransportRequest.WithoutSavedPassword"/>) - so the user is
        /// asked at once instead of the refused password going out again. The dialog's "Remember" replaces the saved one.
        /// </summary>
        public bool AvoidsSavedPassword { get; }

        /// <summary>
        /// The saved password was offered: handed to ssh's askpass for the whole attempt (<see cref="OfferSavedPassword"/>,
        /// OpenSSH), or given as the answer to a password prompt (native).
        /// </summary>
        public bool SavedPasswordOffered
        {
            get { lock (_gate) return _savedPasswordOffered; }
        }

        /// <summary>
        /// The saved password was given as the answer to a password prompt (native): it certainly reached the server, so
        /// an SSH failure after it is its refusal. One handed to ssh's askpass may never have been asked for.
        /// </summary>
        internal bool SavedPasswordAnswered
        {
            get { lock (_gate) return _savedPasswordAnswered; }
        }

        /// <summary>
        /// Offers the saved password to a transport that answers its own prompts - ssh's askpass in its vault-only mode -
        /// for the whole attempt: true when this attempt may (<see cref="MaySignInWithSavedPassword"/>) and the vault holds
        /// one, which is then counted as offered. It reads the vault to know, and keeps nothing.
        /// </summary>
        public bool OfferSavedPassword()
        {
            if (_savedPasswordProfile is not { } profile || _owner.ReadSavedPassword(profile) is null) return false;
            lock (_gate) _savedPasswordOffered = true;
            return true;
        }

        /// <summary>
        /// The saved password this attempt offered was refused (the connector's verdict): the host's automatic attempts do
        /// not offer it again until one gets in - unless the host forgot everything since this attempt began.
        /// </summary>
        public void SavedPasswordRefused() => _owner.MarkPasswordRefused(_generation);

        /// <summary>The prompt this attempt refused to answer, ending the connection; null when it answered every one.</summary>
        public SshInteractionKind? AbortedPrompt
        {
            get { lock (_gate) return _abortedPrompt; }
        }

        /// <summary>
        /// A secret prompt this attempt cancelled for want of anyone to ask and anything remembered - an encrypted
        /// key's passphrase - without ending the connection (codex D3); null when there was none. The attempt may still
        /// get in another way; if it fails SSH instead, signing in needs the user, as for an <see cref="AbortedPrompt"/>.
        /// </summary>
        public SshInteractionKind? DeclinedPrompt
        {
            get { lock (_gate) return _declinedPrompt; }
        }

        private ISshInteractionHandler? User => Interactive ? _owner._user : null;

        /// <exception cref="RemoteMuxPromptAbortedException">Nobody to ask and nothing to answer a password or keyboard-interactive prompt with.</exception>
        public async Task<SshInteractionResponse> HandleAsync(SshInteractionRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            SshInteractionKind kind = request.Kind;
            Advance(newHop: IsHostKeyPrompt(kind));

            if (IsSecretPrompt(kind))
            {
                if (Recall(kind) is { } remembered)
                {
                    Track(new Answer(kind, remembered, FromMemory: true));
                    return SshInteractionResponse.FromSecret(remembered);
                }

                if (User is not { } user)
                {
                    // The profile's saved password, once, with no UI: the user's choice for automatic reconnects.
                    if (kind == SshInteractionKind.Password && TakeSavedPassword() is { } saved) return SshInteractionResponse.FromSecret(saved);

                    // A password is never cancelled: rusty_ssh would send the cancel as an empty password. A passphrase
                    // is: it only unlocks a local key, so nothing reaches the server, and the agent or another key may
                    // still get in. But it is recorded, so an attempt that then fails SSH reads as needing the user.
                    if (kind == SshInteractionKind.Password) throw Abort(kind);
                    Decline(kind);
                    return SshInteractionResponse.Cancel();
                }

                // After a refusal on this host the window's handler must not answer from the vault: the user is asked.
                SshInteractionRequest forUser = AvoidsSavedPassword && kind == SshInteractionKind.Password && request.AllowVaultPasswordReuse
                    ? request.WithoutVaultPasswordReuse()
                    : request;
                SshInteractionResponse response = await user.HandleAsync(forUser, cancellationToken).ConfigureAwait(false);
                if (IsRememberable(kind) && !response.IsCanceled && !string.IsNullOrEmpty(response.Secret))
                {
                    Track(new Answer(kind, response.Secret, FromMemory: false));
                }

                return response;
            }

            if (User is { } asked) return await asked.HandleAsync(request, cancellationToken).ConfigureAwait(false);

            if (IsHostKeyPrompt(kind))
            {
                // Nobody to ask: a known host key only. A cancel is submitted as a rejection, which ends
                // the connection before any credential is sent.
                return _owner._isTrustedHostKey(request) ? SshInteractionResponse.AcceptHostKey() : SshInteractionResponse.Cancel();
            }

            // A keyboard-interactive round with questions has no answer here; one with none (some servers
            // send an empty round last) is answered with nothing, as the protocol expects.
            return kind == SshInteractionKind.KeyboardInteractive && request.KeyboardPrompts.Count > 0
                ? throw Abort(kind)
                : SshInteractionResponse.Cancel();
        }

        /// <summary>
        /// The connection got past auth (the remote command runs): every secret that was not followed by
        /// another auth prompt got its hop in, so the host remembers it - unless the host forgot everything
        /// since this attempt began. Signing in works again, so the host's automatic attempts may offer the
        /// saved password again. From now on <see cref="Refused"/> changes nothing.
        /// </summary>
        public void Succeeded()
        {
            List<Answer> answers;
            lock (_gate)
            {
                _succeeded = true;
                answers = [.. _answers];
            }

            _owner.Settle(
                _generation,
                forget: [],
                remember: answers.Where(a => a.State != AnswerState.Superseded && !a.FromMemory).Select(a => (a.Kind, a.Secret)));
            _owner.ClearPasswordRefused();
        }

        /// <summary>
        /// The attempt failed SSH (auth, or the connection before the command ran): every secret it offered
        /// from memory may be the reason, so each is forgotten, and the next user attempt asks instead. A
        /// password among them means the server's password changed: the saved one is likely as stale, so the
        /// host's automatic attempts stop offering it too, until one gets in. A no-op once the attempt
        /// <see cref="Succeeded"/>: past the greeting, what it offered got it in.
        /// </summary>
        public void Refused()
        {
            List<Answer> answers;
            lock (_gate)
            {
                if (_succeeded) return;
                answers = [.. _answers];
            }

            _owner.Settle(_generation, forget: answers.Where(a => a.FromMemory).Select(a => (a.Kind, a.Secret)), remember: []);
            if (answers.Exists(a => a.FromMemory && a.Kind == SshInteractionKind.Password)) _owner.MarkPasswordRefused(_generation);
        }

        /// <summary>
        /// The saved password for this attempt's password prompt, once: null when the attempt may not use it, when it
        /// already answered a password prompt (from memory, or with the saved password: this prompt means that answer was
        /// refused), or when the vault holds none.
        /// </summary>
        private string? TakeSavedPassword()
        {
            if (_savedPasswordProfile is not { } profile) return null;
            lock (_gate)
            {
                if (_savedPasswordOffered || _answers.Exists(a => a.Kind == SshInteractionKind.Password)) return null;
            }

            if (_owner.ReadSavedPassword(profile) is not { } saved) return null;
            lock (_gate)
            {
                if (_savedPasswordOffered) return null;
                _savedPasswordOffered = true;
                _savedPasswordAnswered = true;
            }

            return saved;
        }

        private RemoteMuxPromptAbortedException Abort(SshInteractionKind kind)
        {
            lock (_gate) _abortedPrompt ??= kind;
            return new RemoteMuxPromptAbortedException(kind);
        }

        private void Decline(SshInteractionKind kind)
        {
            lock (_gate) _declinedPrompt ??= kind;
        }

        private bool IsRememberable(SshInteractionKind kind) => kind != SshInteractionKind.Password || _passwordsReplayable;

        /// <summary>The remembered secret for <paramref name="kind"/>, unless this hop already refused one.</summary>
        private string? Recall(SshInteractionKind kind)
        {
            if (!IsRememberable(kind)) return null;
            lock (_gate)
            {
                if (_answers.Exists(a => a.Kind == kind && a.FromMemory && a.State == AnswerState.Superseded)) return null;
            }

            return _owner.TryRecall(kind, out string secret) ? secret : null;
        }

        /// <summary>
        /// A new prompt arrived: the answers still open are proven when it starts the next hop (a host key),
        /// and superseded when it is another auth prompt on the same hop. A superseded answer that came from
        /// memory is forgotten at once, so however this attempt ends, it is not replayed.
        /// </summary>
        private void Advance(bool newHop)
        {
            List<(SshInteractionKind, string)> refused = [];
            lock (_gate)
            {
                foreach (Answer answer in _answers)
                {
                    if (answer.State != AnswerState.Open) continue;
                    answer.State = newHop ? AnswerState.Proven : AnswerState.Superseded;
                    if (answer.State == AnswerState.Superseded && answer.FromMemory) refused.Add((answer.Kind, answer.Secret));
                }
            }

            if (refused.Count > 0) _owner.Settle(_generation, forget: refused, remember: []);
        }

        private void Track(Answer answer)
        {
            lock (_gate) _answers.Add(answer);
        }

        private enum AnswerState
        {
            /// <summary>The latest answer on its hop: nothing has said yet whether it worked.</summary>
            Open,

            /// <summary>The next hop's connection began after it: it worked.</summary>
            Proven,

            /// <summary>Another auth prompt followed it on the same hop: it did not work.</summary>
            Superseded,
        }

        private sealed record Answer(SshInteractionKind Kind, string Secret, bool FromMemory)
        {
            public AnswerState State { get; set; } = AnswerState.Open;
        }
    }
}

/// <summary>
/// An attempt with nobody to ask (an automatic reconnect) had nothing to answer a password or
/// keyboard-interactive prompt with, so it ended the connection rather than send an empty answer, which
/// the server would count as a failed login. The native exec channel turns it into its transport failure.
/// </summary>
internal sealed class RemoteMuxPromptAbortedException(SshInteractionKind prompt)
    : Exception($"no answer for the {Describe(prompt)} prompt without a user to ask; the connection was ended without answering it")
{
    /// <summary>The prompt left unanswered.</summary>
    public SshInteractionKind Prompt { get; } = prompt;

    internal static string Describe(SshInteractionKind prompt) => prompt switch
    {
        SshInteractionKind.KeyboardInteractive => "keyboard-interactive",
        SshInteractionKind.Passphrase => "passphrase",
        _ => "password",
    };
}
