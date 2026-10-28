using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Platform.Ssh.Models;

namespace Ntilde.Platform.Ssh.Native;

/// <summary>
/// One native SSH connection that tabs share (nova_ssh_connect_shared): it logs in once, owns the profile's port
/// forwards, and carries a shell channel per tab (<see cref="OpenShell"/>). Its poll loop answers the prompts and
/// serves the forwards; each tab polls its own shell.
/// </summary>
/// <remarks>
/// Tabs hold it through <see cref="Lease"/>s from <see cref="NativeSshConnectionPool"/>. When the last lease is
/// released the connection closes, after the profile's ControlPersist seconds when it is up and that is set. It also
/// ends on its own when the transport goes; the native side then ends every shell on it, and the pool forgets it so
/// the next tab reconnects.
/// </remarks>
internal sealed class NativeSshSharedConnection
{
    private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(25);

    private readonly NativeSshConnectionPool _pool;
    private readonly INativeSshInterop _interop;
    private readonly ISshInteractionHandler? _interactionHandler;
    private readonly NativeSshPromptResponder _promptResponder;
    private readonly Action<string> _log;
    private readonly string _target;
    private readonly CancellationTokenSource _pollCts = new();
    private readonly object _gate = new();
    private readonly List<Lease> _leases = [];
    private readonly NovaSshSafeHandle _handle;
    private readonly NativePortForwardSession? _forwards;
    private readonly Task _pollTask;
    private TimeSpan _persist;
    private Timer? _persistTimer;
    private bool _connected;
    private bool _closed;
    private string? _failure;
    private readonly List<string> _setupWarnings = [];

    internal NativeSshSharedConnection(
        NativeSshConnectionPool pool,
        string key,
        SshProfile profile,
        NativeSshConnectionOptions options,
        Opener opener)
    {
        _pool = pool;
        Key = key;
        INativeSshInterop interop = opener.Interop;
        _interop = interop;
        _interactionHandler = opener.InteractionHandler;
        // One responder for the connection's lifetime: it offers the vault's password once, as for any connection.
        _promptResponder = new NativeSshPromptResponder(profile, opener.SessionId);
        Action<string> log = opener.Log;
        _log = log;
        _target = $"{options.User}@{options.Host}:{options.Port}";

        _handle = interop.ConnectShared(options);
        try
        {
            if (profile.Forwards.Count != 0)
            {
                // The forwards are the connection's, so their warnings go to every tab on it. One that cannot be bound
                // (a busy port) is left out rather than failing the connection, and every tab that joins later is told.
                _forwards = new NativePortForwardSession(_handle, profile.Forwards, interop, log, Warn, continueOnBindFailure: true);
            }
        }
        catch
        {
            interop.Close(_handle);
            _pollCts.Dispose();
            throw;
        }

        _log($"[NativeSshSharedConnection] connecting target={_target} forwards={profile.Forwards.Count}");
        _pollTask = Task.Run(PollLoopAsync, _pollCts.Token);
    }

    /// <summary>
    /// The tab that opens a connection, as the connection needs it: the native layer it runs on, who answers its
    /// prompts and under which session id, and where it logs.
    /// </summary>
    /// <param name="SessionId">The tab's <c>ActiveSshSessionRegistry</c> session, or null for an exec, which keeps none.</param>
    internal sealed record Opener(
        INativeSshInterop Interop,
        ISshInteractionHandler? InteractionHandler,
        Guid? SessionId,
        Action<string> Log);

    public string Key { get; }

    /// <summary>Opens a shell for one tab. Throws <see cref="InvalidOperationException"/> once the connection has ended.</summary>
    public NovaSshSafeHandle OpenShell(NativeSshConnectionOptions options)
    {
        ThrowIfClosed();
        return _interop.OpenShell(_handle, options);
    }

    /// <summary>Runs <paramref name="command"/> with no PTY. Throws <see cref="InvalidOperationException"/> once the connection has ended.</summary>
    public NovaSshSafeHandle OpenExec(NativeSshConnectionOptions options, string command)
    {
        ThrowIfClosed();
        return _interop.OpenExec(_handle, options, command);
    }

    /// <summary>An SFTP transfer on a channel of this connection; false when it has ended (see <see cref="INativeSshInterop.TryRunSftpTransferShared"/>).</summary>
    public bool TryRunSftpTransfer(NativeSftpTransferOptions transfer, Action<NativeSftpTransferProgress>? progress, CancellationToken cancellationToken) =>
        !IsClosed && _interop.TryRunSftpTransferShared(_handle, transfer, progress, cancellationToken);

    /// <summary>A listing on a channel of this connection; null when it has ended.</summary>
    public IReadOnlyList<NativeRemotePathEntry>? TryListRemoteDirectory(string remotePath, CancellationToken cancellationToken) =>
        IsClosed ? null : _interop.TryListRemoteDirectoryShared(_handle, remotePath, cancellationToken);

    /// <summary>Logged in, and not closed: what a caller that cannot answer a prompt (SFTP) waits for before it joins.</summary>
    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _connected && !_closed;
            }
        }
    }

    /// <summary>
    /// Why the connection ended, when the managed side ended it: the prompt handler threw. The native side then reports
    /// only that the connection closed, so a channel on it reports this instead, as an unshared exec reports its own
    /// handler's failure. Null otherwise: the native side's own error already names the cause.
    /// </summary>
    public string? Failure => Volatile.Read(ref _failure);

    private bool IsClosed
    {
        get
        {
            lock (_gate)
            {
                return _closed;
            }
        }
    }

    private void ThrowIfClosed()
    {
        if (IsClosed)
        {
            throw new InvalidOperationException("The shared native SSH connection has closed.");
        }
    }

    /// <summary>
    /// Closes the connection now, whatever its leases: for a tab whose shell it could not open, so the next attempt
    /// gets a connection of its own rather than this one again.
    /// </summary>
    public void Abandon() => Close();

    /// <summary>A new lease, or null when the connection has already closed. Cancels a pending ControlPersist close.</summary>
    /// <param name="handler">The joining tab's prompt handler: told when a prompt it was not asked is refused (#610).</param>
    internal Lease? TryAddLease(Action<string> warn, TimeSpan persist, ISshInteractionHandler? handler = null)
    {
        Lease lease;
        string[] setupWarnings;
        lock (_gate)
        {
            if (_closed)
            {
                return null;
            }

            _persistTimer?.Dispose();
            _persistTimer = null;
            // The profile as the latest tab saw it decides how long the connection lingers.
            _persist = persist;
            lease = new Lease(this, warn, handler);
            _leases.Add(lease);
            setupWarnings = [.. _setupWarnings];
        }

        // Outside the lock: a tab's warn writes to its terminal, which takes locks of its own.
        foreach (string message in setupWarnings)
        {
            lease.Warn(message);
        }

        return lease;
    }

    private void Release(Lease lease)
    {
        bool closeNow;
        lock (_gate)
        {
            if (!_leases.Remove(lease) || _leases.Count != 0 || _closed)
            {
                return;
            }

            // A connection still logging in has nothing worth keeping: its prompts were for a tab that is gone.
            closeNow = !_connected || _persist <= TimeSpan.Zero;
            if (!closeNow)
            {
                _log($"[NativeSshSharedConnection] idle target={_target}; closing in {_persist.TotalSeconds:0}s unless a tab joins");
                _persistTimer = new Timer(_ => Close(onlyIfIdle: true), null, _persist, Timeout.InfiniteTimeSpan);
            }
        }

        if (closeNow)
        {
            Close();
        }
    }

    /// <summary>
    /// <see cref="NativeSshPromptResponder"/>'s answer to one of this connection's prompts, before it is sent. A refusal ends
    /// the connection, and every shell on it, but only the opener's handler was asked: each other tab's is told what was
    /// refused (#610), before its shell can end, so it can say why.
    /// </summary>
    private void OnPromptAnswered(SshInteractionRequest request, SshInteractionResponse response)
    {
        if (!response.IsCanceled)
        {
            return;
        }

        ISshInteractionHandler[] joined;
        lock (_gate)
        {
            joined = [.. _leases.Select(lease => lease.Handler).OfType<ISshInteractionHandler>()
                .Where(handler => !ReferenceEquals(handler, _interactionHandler)).Distinct()];
        }

        foreach (ISshInteractionHandler handler in joined)
        {
            try
            {
                handler.RefusedOnSharedConnection(request);
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or AccessViolationException))
            {
                _log($"[NativeSshSharedConnection] target={_target} a tab's handler failed on a refused prompt: {ex.Message}");
            }
        }
    }

    private void Warn(string message)
    {
        Lease[] leases;
        lock (_gate)
        {
            // Before the first lease exists, only setup warns: a forward left out for the connection's whole life.
            if (_leases.Count == 0)
            {
                _setupWarnings.Add(message);
            }

            leases = [.. _leases];
        }

        foreach (Lease lease in leases)
        {
            lease.Warn(message);
        }
    }

    /// <param name="onlyIfIdle">
    /// The ControlPersist timer's close: skipped when a tab joined after the timer fired but before this ran. Checked
    /// under the same lock that marks the connection closed, so a joining tab either cancels the close or is refused
    /// its lease and gets a new connection.
    /// </param>
    private void Close(bool fromPollLoop = false, bool onlyIfIdle = false)
    {
        lock (_gate)
        {
            if (_closed || (onlyIfIdle && _leases.Count != 0))
            {
                return;
            }

            _closed = true;
            _persistTimer?.Dispose();
            _persistTimer = null;
        }

        _pool.Remove(this);
        _log($"[NativeSshSharedConnection] closing target={_target}");
        _pollCts.Cancel();

        // As in NativeSshSession.Dispose: the poll loop is the only caller of the forward session, so it is drained
        // before the forwards are disposed. The poll loop itself cannot wait for itself.
        if (!fromPollLoop)
        {
            try
            {
                _pollTask.Wait(TimeSpan.FromSeconds(2), CancellationToken.None);
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is TaskCanceledException or OperationCanceledException))
            {
                // The loop ended by being cancelled, which is what was asked of it.
            }
        }

        _forwards?.Dispose();
        // nova_ssh_close ends every shell still on the connection; each tab still sees its own Closed.
        _interop.Close(_handle);
        if (!fromPollLoop)
        {
            _pollCts.Dispose();
        }
    }

    private async Task PollLoopAsync()
    {
        try
        {
            while (!_pollCts.IsCancellationRequested)
            {
                NativeSshEvent? nextEvent = _interop.PollEvent(_handle);
                if (nextEvent == null)
                {
                    await Task.Delay(PollDelay, _pollCts.Token).ConfigureAwait(false);
                    continue;
                }

                switch (nextEvent.Kind)
                {
                    case NativeSshEventKind.Connected:
                        lock (_gate)
                        {
                            _connected = true;
                        }

                        _log($"[NativeSshSharedConnection] connected target={_target}");
                        _forwards?.NotifySessionEstablished();
                        break;
                    case NativeSshEventKind.ForwardChannelData:
                    case NativeSshEventKind.ForwardChannelEof:
                    case NativeSshEventKind.ForwardChannelClosed:
                    case NativeSshEventKind.ForwardChannelIncoming:
                        if (_forwards != null)
                        {
                            _forwards.HandleEvent(nextEvent);
                        }
                        else if (nextEvent.Kind == NativeSshEventKind.ForwardChannelIncoming)
                        {
                            // Unsolicited: see NativeSshSession.RefuseUnsolicitedForwardChannel.
                            _interop.CloseChannel(_handle, nextEvent.StatusCode);
                        }

                        break;
                    case NativeSshEventKind.HostKeyPrompt:
                    case NativeSshEventKind.PasswordPrompt:
                    case NativeSshEventKind.PassphrasePrompt:
                    case NativeSshEventKind.KeyboardInteractivePrompt:
                        await _promptResponder.RespondAsync(nextEvent, _interop, _handle, _interactionHandler, _pollCts.Token, OnPromptAnswered).ConfigureAwait(false);
                        break;
                    case NativeSshEventKind.Error:
                        // Each tab's shell reports the failure in its own terminal; this is for the log.
                        _log($"[NativeSshSharedConnection] target={_target} error: {System.Text.Encoding.UTF8.GetString(nextEvent.Payload)}");
                        break;
                    case NativeSshEventKind.Closed:
                        _log($"[NativeSshSharedConnection] ended target={_target}");
                        Close(fromPollLoop: true);
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Close cancelled the loop.
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or AccessViolationException))
        {
            if (!_pollCts.IsCancellationRequested)
            {
                _log($"[NativeSshSharedConnection] target={_target} poll loop failed: {ex.Message}");
                Volatile.Write(ref _failure, $"the native session failed: {ex.Message}");
                Close(fromPollLoop: true);
            }
        }
    }

    /// <summary>One tab's hold on the connection. Disposing it releases the hold, once.</summary>
    internal sealed class Lease : IDisposable
    {
        private readonly Action<string> _warn;
        private int _disposed;

        internal Lease(NativeSshSharedConnection connection, Action<string> warn, ISshInteractionHandler? handler)
        {
            Connection = connection;
            _warn = warn;
            Handler = handler;
        }

        public NativeSshSharedConnection Connection { get; }

        /// <summary>The prompt handler of the tab holding the lease (<see cref="OnPromptAnswered"/>), or null.</summary>
        internal ISshInteractionHandler? Handler { get; }

        internal void Warn(string message)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _warn(message);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Connection.Release(this);
            }
        }
    }
}
