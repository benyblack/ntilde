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
/// A password or passphrase that a successful attempt used is remembered, in memory only and per prompt
/// kind, until <see cref="Forget"/> (the host's dispose). Every attempt offers it first, once: a network
/// drop then reconnects without asking again, as a plain native SSH tab's reconnect does. Asked again
/// within the same attempt means the server refused it, so it is forgotten.
/// </para>
/// <para>
/// Past that, an interactive attempt (a user is waiting) asks the window's handler, which may show a
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

    /// <summary>The handler for one connect attempt; <paramref name="interactive"/> when a user is waiting on it.</summary>
    public Attempt BeginAttempt(bool interactive)
    {
        lock (_gate) return new Attempt(this, interactive, _generation);
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

    private void ForgetIfStill(SshInteractionKind kind, string secret)
    {
        lock (_gate)
        {
            if (_remembered.TryGetValue(kind, out string? current) && current == secret) _remembered.Remove(kind);
        }
    }

    private void Remember(int generation, IReadOnlyDictionary<SshInteractionKind, string> answers)
    {
        lock (_gate)
        {
            if (generation != _generation) return; // forgotten meanwhile: the host is going away
            foreach ((SshInteractionKind kind, string secret) in answers) _remembered[kind] = secret;
        }
    }

    private static readonly Lazy<NativeKnownHostsStore> KnownHosts = new(() => new NativeKnownHostsStore(AppPaths.NativeKnownHostsFilePath));

    private static bool IsTrustedInTheAppsKnownHosts(SshInteractionRequest request) =>
        KnownHosts.Value.CheckHost(request.Host, request.Port, request.Algorithm, request.Fingerprint) == NativeKnownHostMatch.Trusted;

    private static bool IsSecretPrompt(SshInteractionKind kind) => kind is SshInteractionKind.Password or SshInteractionKind.Passphrase;

    private static bool IsHostKeyPrompt(SshInteractionKind kind) => kind is SshInteractionKind.UnknownHostKey or SshInteractionKind.ChangedHostKey;

    /// <summary>
    /// One connect attempt's prompts, in the order its connection raises them (the native transport's
    /// poll thread waits for each answer). <see cref="Succeeded"/> commits the secrets it used.
    /// </summary>
    internal sealed class Attempt : ISshInteractionHandler
    {
        private readonly RemoteMuxInteractionHandler _owner;
        private readonly int _generation;
        private readonly object _gate = new();
        private readonly Dictionary<SshInteractionKind, string> _answered = [];   // guarded by _gate: the latest secret per kind
        private readonly HashSet<SshInteractionKind> _offeredRemembered = [];     // guarded by _gate

        internal Attempt(RemoteMuxInteractionHandler owner, bool interactive, int generation)
        {
            _owner = owner;
            Interactive = interactive;
            _generation = generation;
        }

        /// <summary>True when a user is waiting on this attempt: prompts may reach them.</summary>
        public bool Interactive { get; }

        private ISshInteractionHandler? User => Interactive ? _owner._user : null;

        public async Task<SshInteractionResponse> HandleAsync(SshInteractionRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (IsSecretPrompt(request.Kind))
            {
                if (OfferRemembered(request.Kind) is { } remembered) return SshInteractionResponse.FromSecret(remembered);
                if (User is not { } user) return SshInteractionResponse.Cancel();

                SshInteractionResponse response = await user.HandleAsync(request, cancellationToken).ConfigureAwait(false);
                Record(request.Kind, response);
                return response;
            }

            if (User is { } asked) return await asked.HandleAsync(request, cancellationToken).ConfigureAwait(false);

            // Nobody to ask: a known host key only, nothing else.
            return IsHostKeyPrompt(request.Kind) && _owner._isTrustedHostKey(request)
                ? SshInteractionResponse.AcceptHostKey()
                : SshInteractionResponse.Cancel();
        }

        /// <summary>
        /// The connection got past auth (the remote command runs): the secrets this attempt answered with
        /// were right, so the host remembers them - unless it was told to forget since this attempt began.
        /// </summary>
        public void Succeeded()
        {
            Dictionary<SshInteractionKind, string> answered;
            lock (_gate) answered = new Dictionary<SshInteractionKind, string>(_answered);
            if (answered.Count > 0) _owner.Remember(_generation, answered);
        }

        /// <summary>
        /// The remembered secret for <paramref name="kind"/>, the first time this attempt is asked for one.
        /// Asked again, the server refused it: it is forgotten, and null sends the prompt on.
        /// </summary>
        private string? OfferRemembered(SshInteractionKind kind)
        {
            lock (_gate)
            {
                if (_offeredRemembered.Add(kind))
                {
                    if (!_owner.TryRecall(kind, out string secret)) return null;
                    _answered[kind] = secret;
                    return secret;
                }

                if (_answered.Remove(kind, out string? refused)) _owner.ForgetIfStill(kind, refused);
                return null;
            }
        }

        private void Record(SshInteractionKind kind, SshInteractionResponse response)
        {
            lock (_gate)
            {
                if (!response.IsCanceled && !string.IsNullOrEmpty(response.Secret)) _answered[kind] = response.Secret;
                else _answered.Remove(kind);
            }
        }
    }
}
