using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Shell;
using Ntilde.Shell.Mux;
using Ntilde.Shell.Mux.Remote;

namespace Ntilde
{
    /// <summary>
    /// A daemon from another build (Phase 5 Task 23). An update keeps a compatible daemon running (R9, R10), and the remote
    /// install flow replaces ntilde-mux's binary under a daemon that keeps running: either way the window can be served by
    /// another build's daemon. It says so once per launch for each endpoint, offering a restart, and runs that restart.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>
        /// This build's version: what its own daemon reports, and the ntilde-mux version its install flow installs (the
        /// source <see cref="RemoteMuxStatusText"/> compares against). A seam so tests pin it.
        /// </summary>
        internal string MuxThisBuildVersion { get; set; } = AppVersionInfo.Version;

        /// <summary>
        /// What this launch has offered, and which restarts run: the process's, shared by its windows. A seam so each test is
        /// a launch of its own.
        /// </summary>
        internal MuxPreviousBuildNotice.Launch MuxPreviousBuildLaunch { get; set; } = MuxPreviousBuildNotice.Launch.Process;

        /// <summary>
        /// The restart's question, given the daemon's host (null for this computer's) and how many shells it runs (-1 when
        /// unknown). A seam so tests answer without a modal; the constructor assigns <see cref="ShowMuxRestartDialogAsync"/>.
        /// </summary>
        internal Func<string?, int, Task<bool>> ConfirmMuxRestart { get; set; }

        /// <summary>
        /// Test seam: terminates the local daemon a descriptor names, as <c>kill-server --force</c> does
        /// (<see cref="Ntilde.Mux.Daemon.MuxDaemonStop.Terminate"/>: re-verified to still be that daemon, then waited for up
        /// to 5 s); true once it is gone. Blocking: called off the UI thread.
        /// </summary>
        internal Func<MuxEndpointDescriptor, bool> MuxTerminateDaemon { get; set; } = TerminateLocalMuxDaemon;

        /// <summary>Test seam: sends <c>shutdown</c> to a remote daemon over its host's connection. Called on the pool.</summary>
        internal Func<MuxClient, CancellationToken, Task> MuxShutdownRemoteDaemon { get; set; } = static (client, ct) => client.ShutdownServerAsync(ct);

        /// <summary>Test seam: told of each "from another build" notice as it is raised - its endpoint, merge key, words and action.</summary>
        internal Action<MuxEndpointId, string, string, PersistenceNoticeAction>? MuxRestartOfferedForTest { get; set; }

        /// <summary>Tests: how many connections of <paramref name="endpoint"/> the window has finished deciding about.</summary>
        internal int MuxRestartDecisionsForTest(MuxEndpointId endpoint) => _muxRestartDecisions.GetValueOrDefault(endpoint);

        /// <summary>Tests: a restart this window started is still asking or stopping a daemon.</summary>
        internal bool IsMuxRestartRunningForTest => _muxRestartsRunning > 0;

        private readonly Dictionary<MuxEndpointId, int> _muxRestartDecisions = []; // UI thread
        private bool _muxDaemonVersionsWatched; // UI thread
        private int _muxRestartsRunning;        // UI thread

        /// <summary>Once <see cref="_muxHosts"/> exists (it is never replaced): every connection of every host is checked.</summary>
        private void WatchMuxDaemonVersions()
        {
            if (_muxDaemonVersionsWatched || _muxHosts is not { } hosts) return;
            _muxDaemonVersionsWatched = true;
            hosts.HostConnected += OnMuxHostConnected;
        }

        /// <summary>A host connected (on the pool, from its event queue, which must not wait): decided on the UI thread.</summary>
        private void OnMuxHostConnected(MuxEndpointId id, MuxConnectionHost host, MuxClient client) =>
            Dispatcher.UIThread.Post(() => _ = OfferMuxRestartAsync(id, host, client));

        /// <summary>
        /// UI thread. A daemon of another build - both versions known and different - is offered a restart once per launch
        /// for its endpoint, with persistence on (nothing new appears with "Off"). Offered first, so a reconnect to the same
        /// daemon, or another window's connection to it, offers nothing more; then its running shells are counted (after the
        /// panes this launch spawned there) and the notice raised, under its endpoint's own merge key. A count that cannot be
        /// had, or a connection gone by then, releases the offer: the next connection may make it.
        /// </summary>
        private async Task OfferMuxRestartAsync(MuxEndpointId id, MuxConnectionHost host, MuxClient client)
        {
            try
            {
                if (_teardownDone
                    || !client.IsConnected
                    || client.ServerVersion is not { } daemonVersion
                    || !MuxPreviousBuildNotice.IsFromAnotherBuild(daemonVersion, MuxThisBuildVersion)
                    || !SessionPersistenceMode.IsKeepOnClose(_settings.SessionPersistence)
                    || !MuxPreviousBuildLaunch.TryOffer(id))
                {
                    return;
                }

                int? shells = await CountRunningMuxShellsAsync(client, host.Policy.RpcTimeout);
                if (shells is not int count || _teardownDone || !client.IsConnected)
                {
                    MuxPreviousBuildLaunch.ReleaseOffer(id);
                    return;
                }

                string where = host.Policy.DisplayName;
                string message = id.IsLocal
                    ? MuxPreviousBuildNotice.LocalMessage(daemonVersion, MuxThisBuildVersion, count)
                    : MuxPreviousBuildNotice.RemoteMessage(where, daemonVersion, MuxThisBuildVersion, count);
                PersistenceNoticeAction action = id.IsLocal
                    ? new PersistenceNoticeAction(MuxPreviousBuildNotice.LocalActionLabel, () => _ = RestartLocalMuxAsync())
                    : new PersistenceNoticeAction(MuxPreviousBuildNotice.RemoteActionLabel(where), () => _ = RestartRemoteMuxAsync(id, where));
                // Keyed by endpoint (review item 5): two daemons whose hosts share a name raise the same words, and must not merge.
                string key = $"{MuxPreviousBuildNotice.Title}\n{id}";
                AppLogger.Log($"[MainWindow] the multiplexer on {where} is from another build ({RemoteOutputText.Quote(daemonVersion)}, this is {MuxThisBuildVersion}); offering a restart");
                MuxRestartOfferedForTest?.Invoke(id, key, message, action);
                EnqueueNotice(MuxPreviousBuildNotice.Title, message, action, key);
            }
            finally
            {
                _muxRestartDecisions[id] = _muxRestartDecisions.GetValueOrDefault(id) + 1;
            }
        }

        /// <summary>
        /// "Restart multiplexer now" for a pane's version-mismatch fallback; null with persistence off, when nothing new
        /// appears.
        /// </summary>
        private PersistenceNoticeAction? LocalMuxRestartNoticeAction() =>
            SessionPersistenceMode.IsKeepOnClose(_settings.SessionPersistence)
                ? new PersistenceNoticeAction(MuxPreviousBuildNotice.LocalActionLabel, () => _ = RestartLocalMuxAsync())
                : null;

        /// <summary>How many shells <paramref name="client"/>'s daemon runs; null when it cannot say within <paramref name="timeout"/>. Off the caller's thread.</summary>
        private static Task<int?> CountRunningMuxShellsAsync(MuxClient client, TimeSpan timeout) => Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                IReadOnlyList<SessionSummary> sessions = await client.ListSessionsAsync(cts.Token).ConfigureAwait(false);
                return (int?)sessions.Count(s => s.Running);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[MainWindow] counting the multiplexer's shells failed: {ex.Message}");
                return null;
            }
        });

        /// <summary>The restart's question; a question that cannot be asked is a no.</summary>
        private async Task<bool> AskMuxRestartAsync(string? host, int shells)
        {
            try
            {
                return await ConfirmMuxRestart(host, shells);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[MainWindow] the multiplexer restart question failed; not restarting: {ex.Message}");
                return false;
            }
        }

        /// <summary>A restart's outcome, when it did not happen as asked: a line of its own on the toast.</summary>
        private void RaiseMuxRestartOutcome(string message)
        {
            AppLogger.Log($"[MainWindow] multiplexer restart: {message}");
            EnqueueNotice(MuxPreviousBuildNotice.Title, message, action: null, key: $"{MuxPreviousBuildNotice.Title}\n{message}");
        }

        /// <summary>
        /// "Restart multiplexer now". UI thread. One restart of the local daemon at a time, process-wide; then:
        /// <list type="number">
        /// <item>a daemon the window is connected to that is this build's after all (another window restarted it) is left alone;</item>
        /// <item>it asks, naming how many shells the daemon runs - declined, nothing is sent, and the notice stays dismissed;</item>
        /// <item>it stops the daemon (<see cref="ShutdownLocalDaemonAsync"/>: <c>shutdown</c> and up to 5 s for the exit, then
        /// by pid when it is still there or could not be asked, as <c>kill-server --force</c> does). Just before
        /// <c>shutdown</c> it looks at the daemon the probe reached once more, leaving one of this build alone, and lets this
        /// window's panes go of their shells (<see cref="LetGoOfLocalShellsForRestart"/>);</item>
        /// <item>it warms the host up, without waiting out a failure cooldown, so a daemon of this build starts.</item>
        /// </list>
        /// Every way it does not happen as asked says why, and every way but a declined question releases the offer.
        /// </summary>
        private async Task RestartLocalMuxAsync()
        {
            if (_teardownDone || _muxHosts?.Local is not { } host) return;
            MuxEndpointId local = MuxEndpointId.Local;
            if (!MuxPreviousBuildLaunch.TryBeginRestart(local))
            {
                RaiseMuxRestartOutcome(MuxPreviousBuildNotice.AlreadyRestarting(null));
                return;
            }

            _muxRestartsRunning++;
            bool declined = false;
            try
            {
                MuxClient? old = host.CurrentClient;
                if (old is not null && !MuxPreviousBuildNotice.IsFromAnotherBuild(old.ServerVersion, MuxThisBuildVersion))
                {
                    RaiseMuxRestartOutcome(MuxPreviousBuildNotice.NothingToRestart(null, old.ServerVersion));
                    return;
                }

                int shells = old is null ? -1 : await CountRunningMuxShellsAsync(old, host.Policy.RpcTimeout) ?? -1;
                if (!await AskMuxRestartAsync(null, shells))
                {
                    declined = true;
                    return;
                }

                AppLogger.Log("[MainWindow] restarting the multiplexer");
                string? leftAlone = null;
                LocalDaemonStop stop = await ShutdownLocalDaemonAsync(TimeSpan.FromSeconds(5), "the restart", (daemon, versionMismatch) =>
                {
                    // The last look (review item 3): the daemon about to be stopped must still be another build's.
                    bool stillAnotherBuild = daemon is null
                        ? versionMismatch
                        : MuxPreviousBuildNotice.IsFromAnotherBuild(daemon.ServerVersion, MuxThisBuildVersion);
                    if (!stillAnotherBuild)
                    {
                        leftAlone = daemon is null
                            ? MuxPreviousBuildNotice.NotRunning
                            : MuxPreviousBuildNotice.NothingToRestart(null, daemon.ServerVersion);
                        return false;
                    }

                    LetGoOfLocalShellsForRestart();
                    return true;
                });

                if (stop == LocalDaemonStop.LeftAlone)
                {
                    RaiseMuxRestartOutcome(leftAlone ?? MuxPreviousBuildNotice.NotRunning);
                    return;
                }

                if (stop == LocalDaemonStop.NotConfirmed)
                {
                    RaiseMuxRestartOutcome(MuxPreviousBuildNotice.NotStopped);
                }
                else if (old is not null)
                {
                    // Its connection drops just after the process is gone: the warm-up must not find it still up.
                    await WhenMuxClientDisconnectedAsync(old, TimeSpan.FromSeconds(2));
                }
                host.EndFailureCooldown();
                host.WarmUp();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[MainWindow] restarting the multiplexer failed: {ex}");
            }
            finally
            {
                if (!declined) MuxPreviousBuildLaunch.ReleaseOffer(local);
                MuxPreviousBuildLaunch.EndRestart(local);
                _muxRestartsRunning--;
            }
        }

        /// <summary>
        /// UI thread, just before the restart sends <c>shutdown</c> (review item 6). The daemon's stop kills every shell, and
        /// its <c>exited</c> can reach a pane before the connection drops - and an exit takes the exit policy's path, which may
        /// close the pane, its tab, and rewrite the saved layout. So this window's panes let go of their shells first
        /// (<see cref="TerminalPane.LetGoOfMuxSessionForRestart"/>): a plain detach, then the "multiplexer disconnected"
        /// banner, and Enter starts a new shell on the new daemon. The shells are marked ended and the session saved without
        /// them, as for an update that closes the daemon: the next launch starts fresh shells in the same layout, quietly.
        /// Another process's windows on the same daemon cannot be reached from here: theirs take whatever the daemon's stop
        /// delivers.
        /// </summary>
        private void LetGoOfLocalShellsForRestart()
        {
            MarkLocalShellsEnded();
            int letGo = 0;
            foreach (TerminalPane pane in _paneOwnerTab.Keys)
            {
                if (ShowsLocalMuxEndpoint(pane) && pane.LetGoOfMuxSessionForRestart()) letGo++;
            }

            SaveSessionWithoutEndedShells();
            AppLogger.Log($"[MainWindow] {letGo} pane(s) let go of their shells for the multiplexer restart");
        }

        /// <summary>Completes once <paramref name="client"/> has disconnected, or after <paramref name="timeout"/>.</summary>
        private static async Task WhenMuxClientDisconnectedAsync(MuxClient client, TimeSpan timeout)
        {
            var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Disconnected += _ => gone.TrySetResult();
            if (!client.IsConnected) gone.TrySetResult();
            await Task.WhenAny(gone.Task, Task.Delay(timeout));
        }

        /// <summary>
        /// "Restart ntilde-mux on {host}". UI thread. One restart of that daemon at a time, process-wide; then it needs the
        /// host's connection, and a daemon still of another version, both when it asks and again just before it sends
        /// <c>shutdown</c> over that connection - nothing else is touched: not the local daemon, not another host's. The
        /// daemon exits and its proxy with it, so the host reports it stopped and its panes offer Enter, whose connect's
        /// proxy starts the installed ntilde-mux. Every way it does not happen as asked says why, and every way but a
        /// declined question releases the offer.
        /// </summary>
        private async Task RestartRemoteMuxAsync(MuxEndpointId id, string where)
        {
            if (_teardownDone) return;
            if (!MuxPreviousBuildLaunch.TryBeginRestart(id))
            {
                RaiseMuxRestartOutcome(MuxPreviousBuildNotice.AlreadyRestarting(where));
                return;
            }

            _muxRestartsRunning++;
            bool declined = false;
            try
            {
                if (!TryGetRemoteDaemonToRestart(id, where, out MuxConnectionHost? host, out MuxClient? client)) return;
                int shells = await CountRunningMuxShellsAsync(client, host.Policy.RpcTimeout) ?? -1;
                if (!await AskMuxRestartAsync(where, shells))
                {
                    declined = true;
                    return;
                }

                // The last look (review item 3): the question may have taken minutes.
                if (!TryGetRemoteDaemonToRestart(id, where, out host, out client)) return;
                AppLogger.Log($"[MainWindow] restarting ntilde-mux on {where}");
                try
                {
                    // From the pool: on a stalled link the client's send queue may be full, and the send waits for it.
                    TimeSpan timeout = host.Policy.RpcTimeout;
                    await Task.Run(async () =>
                    {
                        using var cts = new CancellationTokenSource(timeout);
                        await MuxShutdownRemoteDaemon(client, cts.Token).ConfigureAwait(false);
                    });
                }
                catch (Exception ex)
                {
                    RaiseMuxRestartOutcome(MuxPreviousBuildNotice.ShutdownFailed(where, ex.Message));
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[MainWindow] restarting ntilde-mux on {where} failed: {ex}");
            }
            finally
            {
                if (!declined) MuxPreviousBuildLaunch.ReleaseOffer(id);
                MuxPreviousBuildLaunch.EndRestart(id);
                _muxRestartsRunning--;
            }
        }

        /// <summary>The remote host's live connection, to a daemon still of another version; otherwise false, with the notice that says why.</summary>
        private bool TryGetRemoteDaemonToRestart(
            MuxEndpointId id,
            string where,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MuxConnectionHost? host,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MuxClient? client)
        {
            host = _muxHosts?.TryGet(id);
            client = host?.CurrentClient;
            if (host is null || client is null)
            {
                RaiseMuxRestartOutcome(MuxPreviousBuildNotice.NotConnected(where));
                return false;
            }

            if (!MuxPreviousBuildNotice.IsFromAnotherBuild(client.ServerVersion, MuxThisBuildVersion))
            {
                RaiseMuxRestartOutcome(MuxPreviousBuildNotice.NothingToRestart(where, client.ServerVersion));
                return false;
            }

            return true;
        }

        /// <summary>The production <see cref="ConfirmMuxRestart"/>; <paramref name="shells"/> is -1 when unknown.</summary>
        private Task<bool> ShowMuxRestartDialogAsync(string? host, int shells) =>
            ShowConfirmationDialogAsync(
                MuxPreviousBuildNotice.ConfirmTitle(host),
                MuxPreviousBuildNotice.ConfirmHeading(host),
                MuxPreviousBuildNotice.ConfirmMessage(host, shells),
                MuxPreviousBuildNotice.ConfirmButton,
                92);

        /// <summary>The production <see cref="MuxTerminateDaemon"/>: kill-server --force's step, on the GUI's own root.</summary>
        private static bool TerminateLocalMuxDaemon(MuxEndpointDescriptor daemon)
        {
            Ntilde.Mux.Daemon.MuxDaemonStop.TerminateResult result = Ntilde.Mux.Daemon.MuxDaemonStop.Terminate(
                MuxDiscovery.GetDescriptorPath(), daemon, TimeSpan.FromSeconds(5), out string? failure);
            AppLogger.Log(result == Ntilde.Mux.Daemon.MuxDaemonStop.TerminateResult.Terminated
                ? $"[MainWindow] terminated the multiplexer (pid {daemon.Pid})"
                : $"[MainWindow] terminating the multiplexer (pid {daemon.Pid}): {failure}");
            return result == Ntilde.Mux.Daemon.MuxDaemonStop.TerminateResult.Terminated;
        }
    }
}
