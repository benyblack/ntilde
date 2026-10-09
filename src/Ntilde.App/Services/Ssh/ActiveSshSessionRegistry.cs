using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Ntilde.Platform.Ssh.Models;

namespace Ntilde.Services.Ssh;

public sealed class ActiveSshSessionRegistry
{
    private static readonly Lazy<ActiveSshSessionRegistry> Shared = new(() => new ActiveSshSessionRegistry());

    /// <summary>
    /// Every live registration of each session id, oldest first; a lookup sees the latest. A plain session has one. A remote
    /// daemon session can have several (Task 28 fix round 1): the owner's window and a share opened in another window each
    /// register it, with their own host's password scope. When one lets go, the one before it is what a lookup sees again.
    /// </summary>
    private readonly Dictionary<Guid, List<ActiveSshSessionDescriptor>> _sessions = new(); // guarded by _sessionsGate
    private readonly object _sessionsGate = new();

    /// <summary>
    /// Session passwords, held as UTF-8 in pinned buffers rather than as <see cref="string"/>.
    /// </summary>
    /// <remarks>
    /// #121: this used to be a <c>ConcurrentDictionary&lt;Guid, string&gt;</c>, which meant plaintext
    /// credentials sat on the managed heap for the whole lifetime of a session — hours — in a process
    /// that also renders untrusted terminal output. A <see cref="string"/> cannot be cleared, and the
    /// GC is free to copy it while compacting, so the old copy could not even be reliably
    /// *over*written.
    ///
    /// Two properties change here, and both are about the long-lived copy specifically:
    ///
    /// <list type="bullet">
    /// <item><b>Clearable.</b> Bytes are zeroed on overwrite and on <see cref="Unregister(Guid)"/>, so the
    /// window shrinks from "process lifetime" to "session lifetime".</item>
    /// <item><b>Not relocatable.</b> The buffer is allocated pinned, so compaction cannot leave a stale
    /// copy elsewhere in the heap that nothing has a reference to and nothing can clear.</item>
    /// </list>
    ///
    /// What this does <em>not</em> do, stated plainly so nobody reads more into it: transient
    /// <see cref="string"/> copies still exist. <see cref="TryGetRuntimePassword"/> has to return one
    /// because every consumer needs one — <c>NativeSshConnectionOptions.Password</c> is a string and the
    /// interop marshals it as <c>LPUTF8Str</c>. Those copies are short-lived and eligible for collection
    /// immediately, which is a materially different exposure from one that persists for the session.
    /// Removing them means changing the FFI signature to take a buffer; that is tracked on #121 and is
    /// deliberately not bundled here.
    ///
    /// A plain dictionary under an explicit lock, not a <see cref="ConcurrentDictionary{TKey,TValue}"/>:
    /// zeroing a buffer that a concurrent reader is mid-decode would hand that reader a half-zeroed
    /// password and fail its auth for no visible reason. Reads, writes and clears all happen inside the
    /// lock so the bytes are never observed while being wiped. Contention is irrelevant — this is touched
    /// on auth and on teardown, not per keystroke.
    ///
    /// Keyed by session AND server (host, port, user), not by session alone: a session through a jump
    /// chain authenticates several servers, and a password belongs to exactly one of them. Keyed by
    /// session, a bastion's password was replayed to the target (and handed to every hop of a later
    /// transfer). A lookup has to name the server, so a password can only be returned for the server
    /// it was entered for.
    ///
    /// The "session" of a key is a scope: a plain session's own id, or (Phase 5 spec R8) a remote host's
    /// password scope, which the persisted tabs on that host share (<see cref="PasswordScopeOf"/>).
    /// </remarks>
    private readonly Dictionary<RuntimePasswordKey, byte[]> _runtimePasswords = new();
    private readonly object _runtimePasswordGate = new();

    public static ActiveSshSessionRegistry Instance => Shared.Value;

    /// <summary>
    /// Adds <paramref name="descriptor"/> as its session's latest registration; registrations made before it stay, and are
    /// seen again once it is unregistered (<see cref="Unregister(ActiveSshSessionDescriptor)"/>). The same instance
    /// registered again moves to the latest place.
    /// </summary>
    public void Register(ActiveSshSessionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        lock (_sessionsGate)
        {
            if (!_sessions.TryGetValue(descriptor.SessionId, out List<ActiveSshSessionDescriptor>? live))
            {
                _sessions[descriptor.SessionId] = live = new List<ActiveSshSessionDescriptor>(1);
            }

            live.RemoveAll(registered => ReferenceEquals(registered, descriptor));
            live.Add(descriptor);
        }
    }

    /// <summary>The latest live registration of <paramref name="sessionId"/>, if it has one.</summary>
    public bool TryGet(Guid sessionId, out ActiveSshSessionDescriptor? descriptor)
    {
        lock (_sessionsGate)
        {
            descriptor = _sessions.TryGetValue(sessionId, out List<ActiveSshSessionDescriptor>? live) ? live[^1] : null;
            return descriptor is not null;
        }
    }

    public bool TryGetActiveNativeSession(Guid profileId, Guid sessionId, out ActiveSshSessionDescriptor? descriptor)
    {
        if (!TryGet(sessionId, out descriptor) ||
            descriptor is null ||
            descriptor.ProfileId != profileId ||
            descriptor.BackendKind != SshBackendKind.Native)
        {
            descriptor = null;
            return false;
        }

        return true;
    }

    /// <summary>A plain tab's teardown: every registration of <paramref name="sessionId"/> goes, with the passwords kept under its id.</summary>
    public void Unregister(Guid sessionId)
    {
        lock (_sessionsGate) _sessions.Remove(sessionId);
        ClearRuntimePasswords(sessionId);
    }

    /// <summary>
    /// Removes <paramref name="descriptor"/> - that instance, compared by reference - from its session's live registrations,
    /// and returns whether it was there. Two panes can show one remote daemon session (a share opened in another window),
    /// and each registers it: one letting go removes its own registration only, and the other's is what a lookup sees from
    /// then on. A descriptor without a <see cref="ActiveSshSessionDescriptor.PasswordScopeId"/> that was its session's last
    /// takes the session's passwords with it, as <see cref="Unregister(Guid)"/> does; a scoped one leaves them, since they
    /// are its host's (<see cref="UnregisterScope"/>).
    /// </summary>
    public bool Unregister(ActiveSshSessionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        bool wasTheLast;
        lock (_sessionsGate)
        {
            if (!_sessions.TryGetValue(descriptor.SessionId, out List<ActiveSshSessionDescriptor>? live)) return false;
            int at = live.FindLastIndex(registered => ReferenceEquals(registered, descriptor));
            if (at < 0) return false;
            live.RemoveAt(at);
            wasTheLast = live.Count == 0;
            if (wasTheLast) _sessions.Remove(descriptor.SessionId);
        }

        if (wasTheLast && descriptor.PasswordScopeId is null)
        {
            ClearRuntimePasswords(descriptor.SessionId);
        }

        return true;
    }

    /// <summary>
    /// The id <paramref name="sessionId"/>'s runtime passwords are kept under: its latest registration's
    /// <see cref="ActiveSshSessionDescriptor.PasswordScopeId"/> when it has one (a persisted remote tab: its host's), else
    /// the session's own id - also for a session that is not registered, as before scopes existed. What a lookup made
    /// for a session passes to <see cref="TryGetRuntimePassword"/>.
    /// </summary>
    public Guid PasswordScopeOf(Guid sessionId) =>
        TryGet(sessionId, out ActiveSshSessionDescriptor? descriptor) && descriptor!.PasswordScopeId is Guid scope
            ? scope
            : sessionId;

    /// <summary>
    /// Clears (and zeroes) every password held under <paramref name="scopeId"/>: a remote host's scope, when the host is
    /// disposed - which is also how its release ends. Descriptors that name the scope stay until their panes let go; a
    /// lookup through them then finds nothing, and a host built again has a scope of its own.
    /// </summary>
    public void UnregisterScope(Guid scopeId) => ClearRuntimePasswords(scopeId);

    /// <summary>
    /// Holds <paramref name="password"/> as the password of <paramref name="user"/> on
    /// <paramref name="host"/>:<paramref name="port"/>, for this session only. An empty password
    /// clears that one entry.
    /// </summary>
    public void SetRuntimePassword(Guid sessionId, string host, int port, string user, string? password)
    {
        if (sessionId == Guid.Empty || !RuntimePasswordKey.TryCreate(sessionId, host, port, user, out RuntimePasswordKey key))
        {
            return;
        }

        if (string.IsNullOrEmpty(password))
        {
            ClearRuntimePassword(key);
            return;
        }

        // Pinned so the GC cannot relocate it: a moved buffer leaves plaintext behind at the old
        // address with no reference to it, which is the one thing zeroing cannot fix afterwards.
        int byteCount = Encoding.UTF8.GetByteCount(password);
        byte[] buffer = GC.AllocateArray<byte>(byteCount, pinned: true);
        Encoding.UTF8.GetBytes(password, buffer);

        lock (_runtimePasswordGate)
        {
            if (_runtimePasswords.TryGetValue(key, out byte[]? existing))
            {
                CryptographicOperations.ZeroMemory(existing);
            }

            _runtimePasswords[key] = buffer;
        }
    }

    /// <summary>
    /// Removes (and zeroes) the password <paramref name="sessionId"/> holds for <paramref name="user"/> on
    /// <paramref name="host"/>:<paramref name="port"/> while it is still <paramref name="refused"/>, and returns whether it
    /// did: that server refused the value (Codex review of PR #511, P1), so nothing that reads this scope may offer it again.
    /// A different value - one written after the refused one was offered - stays, as does every other server's.
    /// </summary>
    public bool RemoveRuntimePassword(Guid sessionId, string host, int port, string user, string refused)
    {
        if (sessionId == Guid.Empty || string.IsNullOrEmpty(refused) || !RuntimePasswordKey.TryCreate(sessionId, host, port, user, out RuntimePasswordKey key))
        {
            return false;
        }

        byte[] candidate = Encoding.UTF8.GetBytes(refused);
        try
        {
            lock (_runtimePasswordGate)
            {
                if (!_runtimePasswords.TryGetValue(key, out byte[]? stored) || !CryptographicOperations.FixedTimeEquals(stored, candidate))
                {
                    return false;
                }

                _runtimePasswords.Remove(key);
                CryptographicOperations.ZeroMemory(stored);
                return true;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidate);
        }
    }

    /// <summary>
    /// Returns the password this session holds for <paramref name="user"/> on
    /// <paramref name="host"/>:<paramref name="port"/>, or <c>null</c> when none is held. Never
    /// returns a password entered for a different server of the same session.
    /// </summary>
    /// <remarks>
    /// The returned string is a transient copy — see the note on <c>_runtimePasswords</c>. Callers should
    /// use it and let it go rather than storing it anywhere with a longer life than the operation.
    /// </remarks>
    public bool TryGetRuntimePassword(Guid sessionId, string host, int port, string user, out string? password)
    {
        password = null;
        if (!RuntimePasswordKey.TryCreate(sessionId, host, port, user, out RuntimePasswordKey key))
        {
            return false;
        }

        lock (_runtimePasswordGate)
        {
            if (!_runtimePasswords.TryGetValue(key, out byte[]? stored))
            {
                return false;
            }

            // Decoded under the lock so a concurrent Unregister cannot zero the bytes mid-decode and
            // hand back a truncated password that would fail auth for no discoverable reason.
            password = Encoding.UTF8.GetString(stored);
            return true;
        }
    }

    private void ClearRuntimePassword(RuntimePasswordKey key)
    {
        lock (_runtimePasswordGate)
        {
            if (_runtimePasswords.Remove(key, out byte[]? removed))
            {
                CryptographicOperations.ZeroMemory(removed);
            }
        }
    }

    private void ClearRuntimePasswords(Guid sessionId)
    {
        lock (_runtimePasswordGate)
        {
            List<RuntimePasswordKey>? keys = null;
            foreach (RuntimePasswordKey key in _runtimePasswords.Keys)
            {
                if (key.SessionId == sessionId)
                {
                    (keys ??= new List<RuntimePasswordKey>()).Add(key);
                }
            }

            if (keys == null)
            {
                return;
            }

            foreach (RuntimePasswordKey key in keys)
            {
                if (_runtimePasswords.Remove(key, out byte[]? removed))
                {
                    CryptographicOperations.ZeroMemory(removed);
                }
            }
        }
    }

    /// <summary>
    /// One server of one session. The host is compared case-insensitively (DNS names are), the user
    /// exactly (Unix account names are case-sensitive), and port 0 means 22 — the same defaults the
    /// connect path applies, so the key a prompt stores under is the key a transfer looks up.
    /// </summary>
    private readonly record struct RuntimePasswordKey(Guid SessionId, string Host, int Port, string User)
    {
        public static bool TryCreate(Guid sessionId, string? host, int port, string? user, out RuntimePasswordKey key)
        {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user) || port is < 0 or > 65535)
            {
                key = default;
                return false;
            }

            key = new RuntimePasswordKey(
                sessionId,
                host.Trim().ToLowerInvariant(),
                port == 0 ? 22 : port,
                user);
            return true;
        }
    }
}

public sealed class ActiveSshSessionDescriptor
{
    public ActiveSshSessionDescriptor(Guid sessionId, Guid profileId, SshBackendKind backendKind, Guid? passwordScopeId = null)
    {
        SessionId = sessionId;
        ProfileId = profileId;
        BackendKind = backendKind;
        PasswordScopeId = passwordScopeId;
    }

    public Guid SessionId { get; }
    public Guid ProfileId { get; }
    public SshBackendKind BackendKind { get; }

    /// <summary>
    /// Where this session's runtime passwords are kept, when not under <see cref="SessionId"/>: a persisted remote tab's
    /// (Phase 5 spec R8) are its remote host's, which every tab on that host shares and which outlives any one of them, so
    /// they are kept under the host's scope (<see cref="ActiveSshSessionRegistry.UnregisterScope"/>). Null for a plain
    /// session, whose own id is its scope.
    /// </summary>
    public Guid? PasswordScopeId { get; }
}
