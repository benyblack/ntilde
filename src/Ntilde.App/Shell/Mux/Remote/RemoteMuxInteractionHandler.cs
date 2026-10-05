using Ntilde.Platform.Ssh.Interactions;
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
/// trusts it - the native layer asks about the host key on every connect, known or not - and cancels
/// every other prompt, so the attempt fails quietly and the reconnect loop keeps backing off.
/// </para>
/// </remarks>
internal sealed class RemoteMuxInteractionHandler
{
    private readonly ISshInteractionHandler? _user;
    private readonly Func<SshInteractionRequest, bool> _isTrustedHostKey;
    private readonly object _gate = new();
    private readonly Dictionary<SshInteractionKind, string> _remembered = []; // guarded by _gate
    private int _generation;                                                  // guarded by _gate; bumped by Forget

    /// <param name="user">The window's handler (dialogs, the vault, the known-hosts store), or null for none.</param>
    /// <param name="isTrustedHostKey">
    /// Whether a host-key request names a key the user already trusts; the app's native known-hosts store
    /// by default, the one the window's handler records accepted keys in.
    /// </param>
    public RemoteMuxInteractionHandler(ISshInteractionHandler? user, Func<SshInteractionRequest, bool>? isTrustedHostKey = null)
    {
        _user = user;
        _isTrustedHostKey = isTrustedHostKey ?? IsTrustedInTheAppsKnownHosts;
    }

    /// <summary>
    /// The handler for one connect attempt; <paramref name="interactive"/> when a user is waiting on it.
    /// <paramref name="passwordsReplayable"/> is false for a profile with jump hops: its passwords are
    /// neither offered from memory nor remembered, and a password remembered before is dropped.
    /// </summary>
    public Attempt BeginAttempt(bool interactive, bool passwordsReplayable = true)
    {
        lock (_gate)
        {
            if (!passwordsReplayable) _remembered.Remove(SshInteractionKind.Password);
            return new Attempt(this, interactive, passwordsReplayable, _generation);
        }
    }

    /// <summary>Drops every remembered secret, and any an attempt still running would have remembered.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _remembered.Clear();
            _generation++;
        }
    }

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
    /// Each secret answered is tracked. Another auth prompt after it (password, passphrase,
    /// keyboard-interactive) means it did not get that hop in, so it is superseded: never remembered,
    /// and, if it came from memory, forgotten - and not offered again in this attempt. A host-key prompt
    /// after it means the next hop's connection has begun, so it did get its hop in: it is proven, and the
    /// same secret may be offered to the next hop. <see cref="Succeeded"/> remembers what was not
    /// superseded; <see cref="Refused"/> forgets everything this attempt offered from memory.
    /// </remarks>
    internal sealed class Attempt : ISshInteractionHandler
    {
        private readonly RemoteMuxInteractionHandler _owner;
        private readonly bool _passwordsReplayable;
        private readonly int _generation;
        private readonly object _gate = new();
        private readonly List<Answer> _answers = []; // guarded by _gate; in the order given

        internal Attempt(RemoteMuxInteractionHandler owner, bool interactive, bool passwordsReplayable, int generation)
        {
            _owner = owner;
            Interactive = interactive;
            _passwordsReplayable = passwordsReplayable;
            _generation = generation;
        }

        /// <summary>True when a user is waiting on this attempt: prompts may reach them.</summary>
        public bool Interactive { get; }

        private ISshInteractionHandler? User => Interactive ? _owner._user : null;

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

                if (User is not { } user) return SshInteractionResponse.Cancel();

                SshInteractionResponse response = await user.HandleAsync(request, cancellationToken).ConfigureAwait(false);
                if (IsRememberable(kind) && !response.IsCanceled && !string.IsNullOrEmpty(response.Secret))
                {
                    Track(new Answer(kind, response.Secret, FromMemory: false));
                }

                return response;
            }

            if (User is { } asked) return await asked.HandleAsync(request, cancellationToken).ConfigureAwait(false);

            // Nobody to ask: a known host key only, nothing else.
            return IsHostKeyPrompt(kind) && _owner._isTrustedHostKey(request)
                ? SshInteractionResponse.AcceptHostKey()
                : SshInteractionResponse.Cancel();
        }

        /// <summary>
        /// The connection got past auth (the remote command runs): every secret that was not followed by
        /// another auth prompt got its hop in, so the host remembers it - unless the host forgot everything
        /// since this attempt began. A remembered secret that was followed by one is forgotten.
        /// </summary>
        public void Succeeded()
        {
            List<Answer> answers;
            lock (_gate) answers = [.. _answers];

            HashSet<(SshInteractionKind, string)> kept = [.. answers.Where(a => a.State != AnswerState.Superseded).Select(a => (a.Kind, a.Secret))];
            _owner.Settle(
                _generation,
                forget: answers.Where(a => a.State == AnswerState.Superseded && a.FromMemory && !kept.Contains((a.Kind, a.Secret))).Select(a => (a.Kind, a.Secret)),
                remember: answers.Where(a => a.State != AnswerState.Superseded && !a.FromMemory).Select(a => (a.Kind, a.Secret)));
        }

        /// <summary>
        /// The attempt failed SSH (auth, or the connection before the command ran): every secret it offered
        /// from memory may be the reason, so each is forgotten. The next user attempt asks instead.
        /// </summary>
        public void Refused()
        {
            List<Answer> answers;
            lock (_gate) answers = [.. _answers];
            _owner.Settle(_generation, forget: answers.Where(a => a.FromMemory).Select(a => (a.Kind, a.Secret)), remember: []);
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
        /// and superseded when it is another auth prompt on the same hop.
        /// </summary>
        private void Advance(bool newHop)
        {
            lock (_gate)
            {
                foreach (Answer answer in _answers)
                {
                    if (answer.State == AnswerState.Open) answer.State = newHop ? AnswerState.Proven : AnswerState.Superseded;
                }
            }
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
