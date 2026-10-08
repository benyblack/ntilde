using System.Security.Cryptography;
using System.Text;
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
/// attempt failing SSH (<see cref="Attempt.Refused"/>); see <see cref="Attempt"/>. Once the native transport
/// said sign-in was over (<see cref="Attempt.Authenticated"/>), a failure counts against nothing it offered.
/// </para>
/// <para>
/// For a profile with jump hops a password is never remembered or replayed (a passphrase is: it unlocks a
/// local key, whichever hop asks). Native prompts name the hop that asks
/// (<see cref="SshInteractionRequest.IsJumpHop"/>), and the window's handler fills and saves the profile's
/// password only for its target; this handler keeps the stricter rule regardless.
/// </para>
/// <para>
/// Past memory, an interactive attempt (a user is waiting) asks the window's handler, which may show a
/// dialog. An automatic one never does: it accepts a host key only when the known-hosts store already
/// trusts it - the native layer asks about the host key on every connect, known or not - and leaves
/// every other prompt unanswered (see <see cref="Attempt"/>), so the attempt fails quietly. One that
/// failed for want of a secret only the user can give, or that rejected a host key the user does not
/// trust (<see cref="Attempt.HostKeyRejected"/>), fails as needing the user, which stops the reconnect loop.
/// </para>
/// <para>
/// The one exception (the user's choice after the Phase 4 smoke test): the profile's password saved in the vault. An
/// automatic attempt may sign in with it, with no UI, once - <see cref="Attempt.MaySignInWithSavedPassword"/> - unless
/// the profile has jump hops (the same rule as for a remembered password) or the host's destination moved away from
/// what the profile names. A native attempt answers its first password prompt with it; an OpenSSH one hands it to ssh's
/// askpass (<see cref="Attempt.OfferSavedPassword"/>).
/// </para>
/// <para>
/// Refused (<see cref="Attempt.SavedPasswordRefused"/>; also a remembered password the server refused), the value is
/// remembered as a keyed hash - an HMAC under a key made for this process, never the value - and the host does not use
/// that value again: its automatic attempts do not offer it, and its user attempts keep it from the window's handler and
/// ssh's askpass, so the user is asked at once (<see cref="Attempt.AvoidsSavedPassword"/>). A different saved value (the
/// user saved a new one) is used again; so is the refused one once an attempt signs in with it. A user signing in with a
/// typed password does not re-arm a stale saved one. When the saved password alone cannot sign in - a second factor
/// follows it (<see cref="Attempt.SavedPasswordNotEnough"/>) - automatic attempts stop offering it, while user attempts
/// still fill it and ask only for the rest.
/// </para>
/// </remarks>
internal sealed class RemoteMuxInteractionHandler
{
    private readonly ISshInteractionHandler? _user;
    private readonly Func<SshInteractionRequest, bool> _isTrustedHostKey;
    private readonly Func<SshProfile, string?>? _savedPassword;
    private readonly SshAskPassSessionMarkers? _askPassRecords;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly Dictionary<SshInteractionKind, string> _remembered = []; // guarded by _gate
    private int _generation;                                                  // guarded by _gate; bumped by Forget
    private readonly List<byte[]> _refusedPasswords = [];                     // guarded by _gate; HashOf each refused password, newest last
    private bool _savedPasswordNotEnough;                                     // guarded by _gate; a second factor follows it
    private int _unrecordableLogged;                                          // 1 once "cannot record" was logged

    /// <summary>
    /// How many refused passwords a host remembers (re-review item 4): a refused typed or remembered password must not push
    /// a refused saved one out and re-arm it. The oldest goes first.
    /// </summary>
    private const int RefusedPasswordsKept = 4;

    /// <summary>The key of <see cref="HashOf"/>: made for this process, never stored.</summary>
    private static readonly byte[] HashKey = RandomNumberGenerator.GetBytes(32);

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
    /// <param name="askPassRecords">
    /// Reads what the askpass helper did for an attempt's ssh (<see cref="SshAskPassSessionMarkers.Read"/> in the app): whether
    /// it filled the saved password, or declined a second factor. Null: an OpenSSH attempt's saved password never counts
    /// as refused.
    /// </param>
    /// <param name="log">Where a saved password that cannot be read is logged.</param>
    public RemoteMuxInteractionHandler(
        ISshInteractionHandler? user,
        Func<SshInteractionRequest, bool>? isTrustedHostKey = null,
        Func<SshProfile, string?>? savedPassword = null,
        SshAskPassSessionMarkers? askPassRecords = null,
        Action<string>? log = null)
    {
        _user = user;
        _isTrustedHostKey = isTrustedHostKey ?? IsTrustedInTheAppsKnownHosts;
        _savedPassword = savedPassword;
        _askPassRecords = askPassRecords;
        _log = log;
    }

    /// <summary>
    /// The handler for one connect attempt; <paramref name="interactive"/> when a user is waiting on it.
    /// <paramref name="passwordsReplayable"/> is false for a profile with jump hops: its passwords are
    /// neither offered from memory nor remembered, and a password remembered before is dropped.
    /// <paramref name="savedPasswordProfile"/> is the attempt's profile, whose saved password is meant; null for none.
    /// <paramref name="destinationMoved"/>: the host's destination is not the one the profile names now (pinned, then
    /// retargeted), so the saved password is not for it. An automatic attempt may sign in with the saved password when its
    /// passwords are replayable, the destination did not move, and the saved password alone is known to be enough; whether
    /// it is the refused value is checked when it is read. A user's attempt avoids it when the destination moved, or the
    /// saved value is the refused one (<see cref="Attempt.AvoidsSavedPassword"/>).
    /// </summary>
    public Attempt BeginAttempt(bool interactive, bool passwordsReplayable = true, SshProfile? savedPasswordProfile = null, bool destinationMoved = false)
    {
        lock (_gate)
        {
            if (!passwordsReplayable) _remembered.Remove(SshInteractionKind.Password);
            bool mayUseSaved = !interactive && passwordsReplayable && !destinationMoved && !_savedPasswordNotEnough && _savedPassword is not null;
            return new Attempt(this, interactive, passwordsReplayable, _generation, savedPasswordProfile, mayUseSaved, destinationMoved);
        }
    }

    /// <summary>Drops every remembered secret, and any an attempt still running would have remembered.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _remembered.Clear();
            _generation++;
            _refusedPasswords.Clear();
            _savedPasswordNotEnough = false;
        }
    }

    /// <summary>The keyed hash the host keeps of a refused password instead of the value: HMAC-SHA256 under <see cref="HashKey"/>.</summary>
    internal static byte[] HashOf(string secret) => HMACSHA256.HashData(HashKey, Encoding.UTF8.GetBytes(secret));

    /// <summary>A password with this hash was refused on this host: its saved value is used no more.</summary>
    private void MarkPasswordRefused(int generation, byte[] hash)
    {
        lock (_gate)
        {
            if (generation != _generation || _refusedPasswords.Exists(refused => CryptographicOperations.FixedTimeEquals(refused, hash))) return;
            if (_refusedPasswords.Count == RefusedPasswordsKept) _refusedPasswords.RemoveAt(0);
            _refusedPasswords.Add(hash);
        }
    }

    /// <summary>A second factor follows the saved password: automatic attempts stop offering it.</summary>
    private void MarkSavedPasswordNotEnough(int generation)
    {
        lock (_gate)
        {
            if (generation == _generation) _savedPasswordNotEnough = true;
        }
    }

    private bool IsRefused(byte[] hash)
    {
        lock (_gate) return _refusedPasswords.Exists(refused => CryptographicOperations.FixedTimeEquals(refused, hash));
    }

    private bool AnyRefused()
    {
        lock (_gate) return _refusedPasswords.Count > 0;
    }

    /// <summary>An attempt got in having sent <paramref name="hashes"/>: a refused value among them works now.</summary>
    private void ClearRefusedIfAny(IEnumerable<byte[]> hashes)
    {
        byte[][] sent = [.. hashes];
        lock (_gate)
        {
            _refusedPasswords.RemoveAll(refused => Array.Exists(sent, hash => CryptographicOperations.FixedTimeEquals(refused, hash)));
        }
    }

    /// <summary>
    /// The profile's saved password, or null for none - also when the vault cannot be read (review M6): a locked keyring or
    /// a broken store is logged and counts as nothing saved, so the attempt goes on as it would without one.
    /// </summary>
    private string? ReadSavedPassword(SshProfile profile)
    {
        try
        {
            return _savedPassword?.Invoke(profile) is { Length: > 0 } saved ? saved : null;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[RemoteMux] reading the saved password of {profile.Name} failed, so none is used: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Whether the askpass helper could record what it does for an attempt's ssh (Greptile G1): a record store that can be
    /// written now. Without one an OpenSSH attempt's refused saved password could never be counted, so none is offered to
    /// an askpass. Logged once per host.
    /// </summary>
    private bool CanRecordAskPass()
    {
        bool can;
        try
        {
            can = _askPassRecords?.CanRecord() == true;
        }
        catch (Exception)
        {
            can = false;
        }

        if (!can && Interlocked.Exchange(ref _unrecordableLogged, 1) == 0)
        {
            _log?.Invoke("[RemoteMux] the askpass record folder cannot be written, so automatic OpenSSH reconnects are not offered the saved password");
        }

        return can;
    }

    /// <summary>What the askpass helper did for an attempt's ssh; nothing when it cannot be read.</summary>
    private SshAskPassRecord ReadAskPassRecord(string session)
    {
        try
        {
            return _askPassRecords?.Read(session) ?? default;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[RemoteMux] reading the askpass record failed: {ex.Message}");
            return default;
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

    /// <summary>
    /// The app's store, bound to <see cref="AppPaths.NativeKnownHostsFilePath"/> on first use and kept for the
    /// process's life. Code that needs another store (a test with its own app-data root) passes
    /// <c>isTrustedHostKey</c> instead, through <see cref="RemoteMuxHostFactory.Create"/>.
    /// </summary>
    private static readonly Lazy<NativeKnownHostsStore> KnownHosts = new(() => NativeKnownHostsStore.ForPath(AppPaths.NativeKnownHostsFilePath));

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
    /// superseded; <see cref="Refused"/>, unless the attempt succeeded or its sign-in was over
    /// (<see cref="Authenticated"/>), forgets everything it offered from memory.
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
    /// though (<see cref="DeclinedPrompt"/>; codex D3): when the attempt then fails SSH before sign-in is
    /// over, nothing else got in, and the connector reports it as needing the user, as it does an aborted
    /// prompt.
    /// </para>
    /// <para>
    /// An automatic attempt that <see cref="MaySignInWithSavedPassword"/> answers a password prompt it has nothing
    /// remembered for with the profile's saved password - once, and only when it has offered no other password: a second
    /// password prompt means the first answer was refused, and aborts as above. The saved password is not tracked or
    /// remembered: it stays in the vault, and the next attempt reads it there. The prompt after it tells the connector
    /// what happened (<see cref="SecondFactorAfterSavedPassword"/>).
    /// </para>
    /// </remarks>
    internal sealed class Attempt : ISshInteractionHandler
    {
        private readonly RemoteMuxInteractionHandler _owner;
        private readonly bool _passwordsReplayable;
        private readonly int _generation;
        private readonly SshProfile? _savedPasswordProfile;
        private readonly bool _mayUseSaved;
        private readonly bool _destinationMoved;
        private readonly Lazy<bool> _avoidance;
        private readonly object _gate = new();
        private readonly List<Answer> _answers = []; // guarded by _gate; in the order given
        private bool _succeeded;                     // guarded by _gate
        private bool _authenticated;                 // guarded by _gate; the native transport said sign-in is over
        private SshInteractionKind? _abortedPrompt;  // guarded by _gate
        private SshInteractionKind? _declinedPrompt; // guarded by _gate
        private bool _hostKeyRejected;               // guarded by _gate; nobody to ask, and the key was not trusted
        private bool _savedPasswordOffered;          // guarded by _gate
        private byte[]? _offeredHash;                // guarded by _gate; HashOf the saved password offered
        private bool _storedPasswordAnswered;        // guarded by _gate; nobody to ask, and a stored password answered
        private bool _storedFromVault;               // guarded by _gate; that password came from the vault
        private byte[]? _storedHash;                 // guarded by _gate; HashOf that password
        private bool? _secondFactorAfterSaved;       // guarded by _gate; null until the first prompt after the stored password

        internal Attempt(
            RemoteMuxInteractionHandler owner,
            bool interactive,
            bool passwordsReplayable,
            int generation,
            SshProfile? savedPasswordProfile,
            bool mayUseSaved,
            bool destinationMoved)
        {
            _owner = owner;
            Interactive = interactive;
            _passwordsReplayable = passwordsReplayable;
            _generation = generation;
            _savedPasswordProfile = savedPasswordProfile;
            _mayUseSaved = mayUseSaved;
            _destinationMoved = destinationMoved;
            _avoidance = new Lazy<bool>(DecideAvoidance);
        }

        /// <summary>True when a user is waiting on this attempt: prompts may reach them.</summary>
        public bool Interactive { get; }

        /// <summary>
        /// This attempt's askpass session token (<c>NTILDE_SSH_ASKPASS_SESSION</c>), for its ssh: the helper records what it
        /// did under it, and <see cref="ReadAskPassRecord"/> reads that back.
        /// </summary>
        public string AskPassSession { get; } = Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// Native: the prompt after the stored password (<see cref="StoredPasswordAnswered"/>) asked for something other than
        /// a password - a keyboard-interactive question with no "password" in it, a code - so the server took the password
        /// and wants a second factor (review I-1). A password prompt, or a question that asks for a password, after it means
        /// it was refused.
        /// </summary>
        internal bool SecondFactorAfterSavedPassword
        {
            get { lock (_gate) return _secondFactorAfterSaved == true; }
        }

        /// <summary>
        /// With nobody to ask, this attempt answered a password prompt with a stored password: the saved one from the vault,
        /// or one the host remembered - which is often the saved one too, filled by the window's handler on an earlier Enter
        /// (the live smoke test). What follows it says whether it was refused (<see cref="SecondFactorAfterSavedPassword"/>).
        /// </summary>
        internal bool StoredPasswordAnswered
        {
            get { lock (_gate) return _storedPasswordAnswered; }
        }

        /// <summary>
        /// Whether the stored password this attempt answered is the profile's saved one: taken from the vault, or a remembered
        /// value the vault holds too (compared by <see cref="HashOf"/>, reading the vault). Its refusal is then the saved
        /// password refused.
        /// </summary>
        internal bool StoredPasswordIsTheSavedOne()
        {
            byte[]? stored;
            lock (_gate)
            {
                if (!_storedPasswordAnswered) return false;
                if (_storedFromVault) return true;
                stored = _storedHash;
            }

            return stored is not null
                && _savedPasswordProfile is { } profile
                && _owner.ReadSavedPassword(profile) is { } saved
                && CryptographicOperations.FixedTimeEquals(HashOf(saved), stored);
        }

        /// <summary>What the askpass helper did for this attempt's ssh (OpenSSH): filled the saved password, declined a second factor.</summary>
        internal SshAskPassRecord ReadAskPassRecord() => _owner.ReadAskPassRecord(AskPassSession);

        /// <summary>
        /// The saved password alone cannot sign in - a second factor follows it (the connector's verdict): the host's
        /// automatic attempts stop offering it; its user attempts still use it, and ask only for the rest.
        /// </summary>
        public void SavedPasswordNotEnough() => _owner.MarkSavedPasswordNotEnough(_generation);

        /// <summary>
        /// This automatic attempt may sign in with the profile's saved password, if the vault holds one that was not refused
        /// on this host: no jump hops, the profile's own destination, and nothing known to follow it (a second factor).
        /// </summary>
        public bool MaySignInWithSavedPassword => _mayUseSaved && _savedPasswordProfile is not null;

        /// <summary>
        /// This user's attempt keeps the saved password away: its destination moved from what the profile names (review
        /// M4), or the vault still holds the value refused on this host (review M7). The window's handler gets each password
        /// prompt with vault reuse off, and OpenSSH's askpass runs without the vault
        /// (<see cref="RemoteMuxTransportRequest.WithoutSavedPassword"/>), so the user is asked at once instead of the
        /// refused password going out again. The dialog's "Remember" replaces the saved one. Read once, off the UI thread
        /// (it may read the vault).
        /// </summary>
        public bool AvoidsSavedPassword => _avoidance.Value;

        private bool DecideAvoidance()
        {
            if (!Interactive) return false;
            if (_destinationMoved) return true;
            if (_savedPasswordProfile is not { } profile || !_owner.AnyRefused()) return false;
            return _owner.ReadSavedPassword(profile) is { } saved && _owner.IsRefused(HashOf(saved));
        }

        /// <summary>
        /// The saved password was offered: handed to ssh's askpass for the whole attempt (<see cref="OfferSavedPassword"/>,
        /// OpenSSH), or given as the answer to a password prompt (native).
        /// </summary>
        public bool SavedPasswordOffered
        {
            get { lock (_gate) return _savedPasswordOffered; }
        }

        /// <summary>
        /// Offers the saved password to a transport that answers its own prompts - ssh's askpass in its vault-only mode -
        /// for the whole attempt: true when this attempt may (<see cref="MaySignInWithSavedPassword"/>), the helper's record
        /// of it can be written (Greptile G1: else a refusal could never be counted), and the vault holds one not refused
        /// here, which is then counted as offered. It reads the vault to know, and keeps nothing.
        /// </summary>
        public bool OfferSavedPassword()
        {
            if (!MaySignInWithSavedPassword || !_owner.CanRecordAskPass() || UnrefusedSavedPassword() is not { } offer) return false;
            lock (_gate)
            {
                _savedPasswordOffered = true;
                _offeredHash = offer.Hash;
            }

            return true;
        }

        /// <summary>
        /// The saved password this attempt offered was refused (the connector's verdict): the host uses that value no more -
        /// unless the host forgot everything since this attempt began.
        /// </summary>
        public void SavedPasswordRefused()
        {
            byte[]? hash;
            lock (_gate) hash = _offeredHash ?? _storedHash;
            if (hash is not null) _owner.MarkPasswordRefused(_generation, hash);
        }

        /// <summary>The saved password and its hash, unless the vault holds none or the value refused on this host.</summary>
        private (string Value, byte[] Hash)? UnrefusedSavedPassword()
        {
            if (_savedPasswordProfile is not { } profile || _owner.ReadSavedPassword(profile) is not { } saved) return null;
            byte[] hash = HashOf(saved);
            return _owner.IsRefused(hash) ? null : (saved, hash);
        }

        /// <summary>
        /// The attempt got past sign-in (<see cref="Succeeded"/>: the proxy's greeting arrived): a failure after it - the mux
        /// hello - says nothing about the password it sent (re-review item 1).
        /// </summary>
        internal bool HasSucceeded
        {
            get { lock (_gate) return _succeeded; }
        }

        /// <summary>
        /// The native transport saw sign-in end (<see cref="ISshInteractionHandler.Authenticated"/>): everything this attempt
        /// answered got it in, so a failure from now on - the link dropping before the greeting - counts against none of it.
        /// From now on <see cref="Refused"/> changes nothing.
        /// </summary>
        public void Authenticated()
        {
            lock (_gate) _authenticated = true;
        }

        /// <summary>The attempt's sign-in was over (<see cref="Authenticated"/>), whatever came after it.</summary>
        internal bool HasAuthenticated
        {
            get { lock (_gate) return _authenticated; }
        }

        /// <summary>The prompt this attempt refused to answer, ending the connection; null when it answered every one.</summary>
        public SshInteractionKind? AbortedPrompt
        {
            get { lock (_gate) return _abortedPrompt; }
        }

        /// <summary>
        /// A secret prompt this attempt cancelled for want of anyone to ask and anything remembered - an encrypted
        /// key's passphrase - without ending the connection (codex D3); null when there was none. The attempt may still
        /// get in another way; if it fails SSH before sign-in is over (<see cref="HasAuthenticated"/>) instead, signing in
        /// needs the user, as for an <see cref="AbortedPrompt"/>.
        /// </summary>
        public SshInteractionKind? DeclinedPrompt
        {
            get { lock (_gate) return _declinedPrompt; }
        }

        /// <summary>
        /// With nobody to ask, this attempt rejected a host key the user does not trust - one never seen, or one that changed.
        /// That ends the connection at the key exchange, before anything is sent, and the next attempt meets the same key:
        /// only the user can review it. A user's own answer in the dialog is not recorded here.
        /// </summary>
        public bool HostKeyRejected
        {
            get { lock (_gate) return _hostKeyRejected; }
        }

        private ISshInteractionHandler? User => Interactive ? _owner._user : null;

        /// <exception cref="RemoteMuxPromptAbortedException">Nobody to ask and nothing to answer a password or keyboard-interactive prompt with.</exception>
        public async Task<SshInteractionResponse> HandleAsync(SshInteractionRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            SshInteractionKind kind = request.Kind;
            Advance(newHop: IsHostKeyPrompt(kind));
            NoteWhatFollowsTheSavedPassword(request);

            if (IsSecretPrompt(kind))
            {
                if (Recall(kind) is { } remembered)
                {
                    Track(new Answer(kind, remembered, FromMemory: true));
                    if (kind == SshInteractionKind.Password && User is null) NoteStoredPassword(remembered, fromVault: false);
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

                // The window's handler must not answer from the vault when the saved password is kept away (a refused
                // value, a moved destination) or, with jump hops, when the prompt may be a jump host's (review M9).
                SshInteractionRequest forUser = kind == SshInteractionKind.Password && request.AllowVaultPasswordReuse && (AvoidsSavedPassword || !_passwordsReplayable)
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
                if (_owner._isTrustedHostKey(request)) return SshInteractionResponse.AcceptHostKey();
                lock (_gate) _hostKeyRejected = true;
                return SshInteractionResponse.Cancel();
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
        /// since this attempt began. A refused password among them works again. A typed password that got in does
        /// not re-arm a refused saved one (review M7). From now on <see cref="Refused"/> changes nothing.
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
            // A refused value works again only if it is what got in (the server took it back): a password answer that another
            // prompt superseded in this attempt was rejected again, and clears nothing (Greptile G2).
            _owner.ClearRefusedIfAny(answers
                .Where(a => a.Kind == SshInteractionKind.Password && a.State != AnswerState.Superseded)
                .Select(a => HashOf(a.Secret)));
        }

        /// <summary>
        /// The attempt failed SSH (auth, or the connection before the command ran): every secret it offered
        /// from memory may be the reason, so each is forgotten, and the next user attempt asks instead. A
        /// password among them means the server's password changed: the host uses that value no more, from the vault
        /// either (the same value saved there is likely as stale). A no-op once the attempt
        /// <see cref="Succeeded"/>, or its sign-in was over (<see cref="Authenticated"/>): what it offered got it in.
        /// </summary>
        public void Refused()
        {
            List<Answer> answers;
            lock (_gate)
            {
                if (_succeeded || _authenticated) return;
                answers = [.. _answers];
            }

            _owner.Settle(_generation, forget: answers.Where(a => a.FromMemory).Select(a => (a.Kind, a.Secret)), remember: []);
            // A second factor after it means the remembered password was taken: the attempt failed at the factor, so the
            // password is not marked refused (it may be the saved one, which the user's Enter must still fill; review I-1).
            if (!SecondFactorAfterSavedPassword && answers.LastOrDefault(a => a.FromMemory && a.Kind == SshInteractionKind.Password) is { } refusedFromMemory)
            {
                _owner.MarkPasswordRefused(_generation, HashOf(refusedFromMemory.Secret));
            }
        }

        /// <summary>
        /// The saved password for this attempt's password prompt, once: null when the attempt may not use it, when it
        /// already answered a password prompt (from memory, or with the saved password: this prompt means that answer was
        /// refused), or when the vault holds none, or the value refused on this host.
        /// </summary>
        private string? TakeSavedPassword()
        {
            if (!MaySignInWithSavedPassword) return null;
            lock (_gate)
            {
                if (_savedPasswordOffered || _answers.Exists(a => a.Kind == SshInteractionKind.Password)) return null;
            }

            if (UnrefusedSavedPassword() is not { } saved) return null;
            lock (_gate)
            {
                if (_savedPasswordOffered) return null;
                _savedPasswordOffered = true;
                _offeredHash = saved.Hash;
                _storedPasswordAnswered = true;
                _storedFromVault = true;
                _storedHash = saved.Hash;
            }

            return saved.Value;
        }

        /// <summary>
        /// Records the first prompt after a stored password (the saved one, or a remembered one) was answered with nobody to
        /// ask: a keyboard-interactive round whose questions all
        /// ask for something other than a password is a second factor; anything else - a password prompt, a question that
        /// asks for a password, a passphrase - is not, and a code question after it is none either. An empty round says
        /// nothing.
        /// </summary>
        private void NoteWhatFollowsTheSavedPassword(SshInteractionRequest request)
        {
            if (request.Kind == SshInteractionKind.KeyboardInteractive && request.KeyboardPrompts.Count == 0) return;
            bool secondFactor = request.Kind == SshInteractionKind.KeyboardInteractive
                && request.KeyboardPrompts.All(question => !question.Prompt.Contains("password", StringComparison.OrdinalIgnoreCase));
            lock (_gate)
            {
                if (!_storedPasswordAnswered || _secondFactorAfterSaved is not null) return;
                _secondFactorAfterSaved = secondFactor;
            }
        }

        /// <summary>Records the stored password this attempt answered with nobody to ask, unless it already answered one.</summary>
        private void NoteStoredPassword(string secret, bool fromVault)
        {
            lock (_gate)
            {
                if (_storedPasswordAnswered) return;
                _storedPasswordAnswered = true;
                _storedFromVault = fromVault;
                _storedHash = HashOf(secret);
            }
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
