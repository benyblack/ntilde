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

    internal NativeSshSharedConnection(
        NativeSshConnectionPool pool,
        string key,
        SshProfile profile,
        NativeSshConnectionOptions options,
        INativeSshInterop interop,
        ISshInteractionHandler? interactionHandler,
        Guid sessionId,
        Action<string> log)
    {
        _pool = pool;
        Key = key;
        _interop = interop;
        _interactionHandler = interactionHandler;
        // One responder for the connection's lifetime: it offers the vault's password once, as for any connection.
        _promptResponder = new NativeSshPromptResponder(profile, sessionId);
        _log = log;
        _target = $"{options.User}@{options.Host}:{options.Port}";

        _handle = interop.ConnectShared(options);
        try
        {
            if (profile.Forwards.Count != 0)
            {
                // The forwards are the connection's, so their warnings go to every tab on it.
                _forwards = new NativePortForwardSession(_handle, profile.Forwards, interop, log, Warn);
            }
        }
        catch
        {
            interop.Close(_handle);
            _pollCts.Dispose();
            throw;
        }

        _log($"[NativeSshSharedConnection] connecting target={_target} forwards={profile.Forwards.Count}");
        _pollTask = Task.Run(PollLoopAsync);
    }

    public string Key { get; }

    /// <summary>Opens a shell for one tab. Throws <see cref="InvalidOperationException"/> once the connection has ended.</summary>
    public NovaSshSafeHandle OpenShell(NativeSshConnectionOptions options)
    {
        lock (_gate)
        {
            if (_closed)
            {
                throw new InvalidOperationException("The shared native SSH connection has closed.");
            }
        }

        return _interop.OpenShell(_handle, options);
    }

    /// <summary>
    /// Closes the connection now, whatever its leases: for a tab whose shell it could not open, so the next attempt
    /// gets a connection of its own rather than this one again.
    /// </summary>
    public void Abandon() => Close();

    /// <summary>A new lease, or null when the connection has already closed. Cancels a pending ControlPersist close.</summary>
    internal Lease? TryAddLease(Action<string> warn, TimeSpan persist)
    {
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
            var lease = new Lease(this, warn);
            _leases.Add(lease);
            return lease;
        }
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

    private void Warn(string message)
    {
        Lease[] leases;
        lock (_gate)
        {
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
                _pollTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is TaskCanceledException or OperationCanceledException))
            {
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
                        await _promptResponder.RespondAsync(nextEvent, _interop, _handle, _interactionHandler, _pollCts.Token).ConfigureAwait(false);
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
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or AccessViolationException))
        {
            if (!_pollCts.IsCancellationRequested)
            {
                _log($"[NativeSshSharedConnection] target={_target} poll loop failed: {ex.Message}");
                Close(fromPollLoop: true);
            }
        }
    }

    /// <summary>One tab's hold on the connection. Disposing it releases the hold, once.</summary>
    internal sealed class Lease : IDisposable
    {
        private readonly Action<string> _warn;
        private int _disposed;

        internal Lease(NativeSshSharedConnection connection, Action<string> warn)
        {
            Connection = connection;
            _warn = warn;
        }

        public NativeSshSharedConnection Connection { get; }

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
