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
using NoticeOffer = Ntilde.Shell.Mux.MuxPreviousBuildNotice.NoticeOffer;
using RestartOutcome = Ntilde.Shell.Mux.MuxPreviousBuildNotice.RestartOutcome;

namespace Ntilde
{
    /// <summary>
    /// A daemon from another build (Phase 5 Task 23). An update keeps a compatible daemon running (R9, R10), and the remote
    /// install flow replaces ntilde-mux's binary under a daemon that keeps running: either way the window can be served by
    /// another build's daemon. It says so once per launch for each endpoint, offering a restart, and runs that restart. A
    /// remote daemon is judged against the ntilde-mux installed on its host, which is what a restart there starts (final
    /// review I1): one as old as the installed binary but older than this app is offered the update instead.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>
        /// This build's version: what its own daemon reports, and the ntilde-mux version its install flow installs (the
        /// source <see cref="RemoteMuxStatusText"/> compares against). A local daemon is judged against it, and a remote one
        /// is offered the update when older. A seam so tests pin it.
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

        /// <summary>Test seam: told of each multiplexer notice as it is raised - an offer, or a restart's outcome.</summary>
        internal Action<MuxPreviousBuildNotice.Raised>? MuxNoticeRaisedForTest { get; set; }

        /// <summary>Test seam: runs inside a restart right after the panes let go of their shells, before <c>shutdown</c> is sent.</summary>
        internal Action? MuxRestartFaultForTest { get; set; }

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

        /// <summary>
        /// A host connected (on the pool, from its event queue, which must not wait): decided on the UI thread, posted
        /// through this window's own dispatcher - never the <c>Dispatcher.UIThread</c> static, whose read off the UI thread
        /// is what poisoned headless test runs (#81): a test window's host can connect after its test returned.
        /// </summary>
        private void OnMuxHostConnected(MuxEndpointId id, MuxConnectionHost host, MuxClient client)
        {
            // Release hardening (optional): once per launch, when this launch had to start the local daemon from the install
            // folder (MuxDaemonImage.Resolve could not stage its copy), so every update stops it - said here, not only logged.
            if (id.IsLocal && MuxDaemonImage.TakeInstallFolderNotice())
            {
                Dispatcher.Post(() =>
                {
                    if (!_teardownDone) EnqueueNotice(SessionPersistenceNoticeTitle, MuxDaemonImage.InstallFolderNotice);
                });
            }

            Dispatcher.Post(() => _ = OfferMuxRestartAsync(id, host, client));
        }

        /// <summary>The title of the install-folder notice: the setting it is about.</summary>
        private const string SessionPersistenceNoticeTitle = "Keep shells running";

        /// <summary>
        /// UI thread, with persistence on (nothing new appears with "Off"); once per launch for each endpoint, offered first,
        /// so a reconnect to the same daemon, or another window's connection to it, offers nothing more:
        /// <list type="bullet">
        /// <item>A daemon of another build than the one a restart would start - both versions known and different - is
        /// offered a restart. For the local daemon that is this build's; for a remote one, the version installed on its
        /// host (final review I1, <see cref="MuxPreviousBuildNotice.DecideRemote"/>). Its running shells are counted (after
        /// the panes this launch spawned there) and the notice raised, under its endpoint's own merge key. A count that
        /// cannot be had, or a connection gone by then, releases the offer: the next connection may make it.</item>
        /// <item>A remote daemon as old as the installed binary (or with none recorded) but older than this app is offered
        /// the update - the install dialog - never a restart, which would start that same old binary again.</item>
        /// </list>
        /// </summary>
        private async Task OfferMuxRestartAsync(MuxEndpointId id, MuxConnectionHost host, MuxClient client)
        {
            try
            {
                if (_teardownDone
                    || !client.IsConnected
                    || client.ServerVersion is not { } daemonVersion
                    || !SessionPersistenceMode.IsKeepOnClose(_settings.SessionPersistence))
                {
                    return;
                }

                // What a restart would start: this build for the local daemon (its binary is this app's), the binary
                // installed on the host for a remote one.
                string? restartsAs = id.IsLocal ? MuxThisBuildVersion : host.RecordedRemoteDaemonVersion;
                NoticeOffer offer;
                if (id.IsLocal)
                {
                    offer = MuxPreviousBuildNotice.IsFromAnotherBuild(daemonVersion, restartsAs) ? NoticeOffer.Restart : NoticeOffer.None;
                }
                else
                {
                    offer = MuxPreviousBuildNotice.DecideRemote(daemonVersion, restartsAs, MuxThisBuildVersion);
                }

                if (offer == NoticeOffer.None || !MuxPreviousBuildLaunch.TryOffer(id)) return;

                string where = host.Policy.DisplayName;
                // Keyed by endpoint (review item 5): two daemons whose hosts share a name raise the same words, and must not merge.
                string key = $"{MuxPreviousBuildNotice.Title}\n{id}";
                if (offer == NoticeOffer.Update)
                {
                    OfferRemoteMuxUpdate(id, where, daemonVersion, key);
                    return;
                }

                int? shells = await CountRunningMuxShellsAsync(client, host.Policy.RpcTimeout);
                if (shells is not int count || _teardownDone || !client.IsConnected)
                {
                    MuxPreviousBuildLaunch.ReleaseOffer(id);
                    return;
                }

                string message = id.IsLocal
                    ? MuxPreviousBuildNotice.LocalMessage(daemonVersion, MuxThisBuildVersion, count)
                    : MuxPreviousBuildNotice.RemoteMessage(where, daemonVersion, count);
                PersistenceNoticeAction action = id.IsLocal
                    ? new PersistenceNoticeAction(MuxPreviousBuildNotice.LocalActionLabel, () => _ = RestartLocalMuxAsync())
                    : new PersistenceNoticeAction(MuxPreviousBuildNotice.RemoteActionLabel(where), () => _ = RestartRemoteMuxAsync(id, where));
                AppLogger.Log($"[MainWindow] the multiplexer on {where} is from another build ({RemoteOutputText.Quote(daemonVersion)}, a restart starts {RemoteOutputText.Quote(restartsAs)}); offering a restart");
                MuxNoticeRaisedForTest?.Invoke(new MuxPreviousBuildNotice.Raised(id, null, key, message, action));
                EnqueueNotice(MuxPreviousBuildNotice.Title, message, action, key);
            }
            finally
            {
                _muxRestartDecisions[id] = _muxRestartDecisions.GetValueOrDefault(id) + 1;
            }
        }

        /// <summary>
        /// UI thread. Final review I1: the ntilde-mux on a remote host is older than this app, and a restart would start the
        /// same old binary again, so the notice offers "Update ntilde-mux on {host}…" - the install dialog for the profile
        /// (<see cref="OpenRemoteMuxInstall"/>; no button while there is none). The install renames the new binary over the
        /// old one and never stops the running daemon, so its shells keep running; once it is recorded the restart is offered
        /// at once (<see cref="DecideMuxNoticeAgainAfterInstall"/>). The offer is not released otherwise: once per launch for
        /// the host.
        /// </summary>
        private void OfferRemoteMuxUpdate(MuxEndpointId id, string where, string daemonVersion, string key)
        {
            string message = MuxPreviousBuildNotice.RemoteUpdateMessage(where, daemonVersion, MuxThisBuildVersion);
            PersistenceNoticeAction? action = OpenRemoteMuxInstall is { } open && id.SshProfileId is Guid profileId
                ? new PersistenceNoticeAction(TerminalPane.RemoteMuxUpdateActionLabel(RemoteOutputText.Quote(where)), () => open(profileId))
                : null;
            AppLogger.Log($"[MainWindow] ntilde-mux on {where} is older ({RemoteOutputText.Quote(daemonVersion)}) than this app ({MuxThisBuildVersion}); offering the update");
            MuxNoticeRaisedForTest?.Invoke(new MuxPreviousBuildNotice.Raised(id, null, key, message, action));
            EnqueueNotice(MuxPreviousBuildNotice.Title, message, action, key);
        }

        /// <summary>
        /// UI thread. The install flow just recorded a new ntilde-mux for <paramref name="profileId"/> (from a notice's button
        /// or the connection editor): a new event, so that host's offer is released - whatever claimed it, an update notice or
        /// a restart already run (residual round, item 2) - and its live connection, if any, is decided again now. The daemon
        /// still runs the old binary, so the restart is offered in this session, under the same endpoint key, replacing the
        /// update notice.
        /// </summary>
        private void DecideMuxNoticeAgainAfterInstall(Guid profileId)
        {
            if (_teardownDone) return;
            MuxEndpointId id = MuxEndpointId.ForSsh(profileId);
            MuxPreviousBuildLaunch.ReleaseOffer(id);
            if (_muxHosts?.TryGet(id) is { CurrentClient: { } client } host) _ = OfferMuxRestartAsync(id, host, client);
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

        /// <summary>
        /// A restart's outcome, when it did not happen as asked: a line of its own on the toast, worded from
        /// <paramref name="outcome"/> alone (<see cref="MuxPreviousBuildNotice.Outcome"/>). <paramref name="host"/> is null for
        /// this computer's daemon.
        /// </summary>
        private void RaiseMuxRestartOutcome(MuxEndpointId id, RestartOutcome outcome, string? host)
        {
            string message = MuxPreviousBuildNotice.Outcome(outcome, host);
            string key = $"{MuxPreviousBuildNotice.Title}\n{message}";
            AppLogger.Log($"[MainWindow] multiplexer restart on {host ?? "this computer"}: {outcome}");
            MuxNoticeRaisedForTest?.Invoke(new MuxPreviousBuildNotice.Raised(id, outcome, key, message, null));
            EnqueueNotice(MuxPreviousBuildNotice.Title, message, action: null, key: key);
        }

        /// <summary>
        /// "Restart multiplexer now". UI thread. One restart of the local daemon at a time, process-wide; then:
        /// <list type="number">
        /// <item>a daemon the window is connected to that is this build's after all (another window restarted it) is left alone;</item>
        /// <item>it asks, naming how many shells the daemon runs - declined, nothing is sent, and the notice stays dismissed;</item>
        /// <item>it stops the daemon (<see cref="ShutdownLocalDaemonAsync"/>: <c>shutdown</c> and up to 5 s for the exit, then
        /// by pid when it is still there or could not be asked, as <c>kill-server --force</c> does). Just before
        /// <c>shutdown</c> it looks at what the probe found once more - leaving alone a daemon of this build, one that does
        /// not answer, or none at all - and this window's panes let go of their shells (<see cref="LetGoOfShellsForRestart"/>);</item>
        /// <item>it warms the host up, without waiting out a failure cooldown, so a daemon of this build starts.</item>
        /// </list>
        /// Every way it does not happen as asked says why (<see cref="RestartOutcome"/>), every way but a declined question
        /// releases the offer, and the panes' Enter waits until it is over.
        /// </summary>
        private async Task RestartLocalMuxAsync()
        {
            if (_teardownDone || _muxHosts?.Local is not { } host) return;
            MuxEndpointId local = MuxEndpointId.Local;
            if (!MuxPreviousBuildLaunch.TryBeginRestart(local))
            {
                RaiseMuxRestartOutcome(local, RestartOutcome.AlreadyRestarting, null);
                return;
            }

            _muxRestartsRunning++;
            bool declined = false;
            List<TerminalPane> letGo = [];
            try
            {
                MuxClient? old = host.CurrentClient;
                if (old is not null && !MuxPreviousBuildNotice.IsFromAnotherBuild(old.ServerVersion, MuxThisBuildVersion))
                {
                    RaiseMuxRestartOutcome(local, MuxPreviousBuildNotice.NothingToRestart(old.ServerVersion), null);
                    return;
                }

                int shells = old is null ? -1 : await CountRunningMuxShellsAsync(old, host.Policy.RpcTimeout) ?? -1;
                if (!await AskMuxRestartAsync(null, shells))
                {
                    declined = true;
                    return;
                }

                AppLogger.Log("[MainWindow] restarting the multiplexer");
                RestartOutcome? leftAlone = null;
                LocalDaemonStop stop = await ShutdownLocalDaemonAsync(TimeSpan.FromSeconds(5), "the restart", (daemon, found) =>
                {
                    // The last look (review item 3, round 2 M3): what is about to be stopped must still be another build's.
                    leftAlone = found switch
                    {
                        LocalDaemonProbe.Reached when !MuxPreviousBuildNotice.IsFromAnotherBuild(daemon!.ServerVersion, MuxThisBuildVersion)
                            => MuxPreviousBuildNotice.NothingToRestart(daemon.ServerVersion),
                        LocalDaemonProbe.Reached or LocalDaemonProbe.VersionMismatch => null,
                        LocalDaemonProbe.NotRunning => RestartOutcome.NotRunning,
                        _ => RestartOutcome.Unreachable,
                    };
                    if (leftAlone is not null) return false;

                    LetGoOfShellsForRestart(local, letGo);
                    MuxRestartFaultForTest?.Invoke();
                    return true;
                });

                switch (stop)
                {
                    case LocalDaemonStop.LeftAlone:
                        RaiseMuxRestartOutcome(local, leftAlone ?? RestartOutcome.NotRunning, null);
                        return;
                    case LocalDaemonStop.NotStopped:
                        RaiseMuxRestartOutcome(local, RestartOutcome.NotStopped, null);
                        break;
                    default:
                        if (stop == LocalDaemonStop.StopUnconfirmed) RaiseMuxRestartOutcome(local, RestartOutcome.StopUnconfirmed, null);
                        // Its connection drops just after the process is gone: the warm-up must not find it still up.
                        if (old is not null) await WhenMuxClientDisconnectedAsync(old, TimeSpan.FromSeconds(2));
                        break;
                }

                host.EndFailureCooldown();
                host.WarmUp();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[MainWindow] restarting the multiplexer failed: {ex}");
                RaiseMuxRestartOutcome(local, RestartOutcome.Failed, null);
            }
            finally
            {
                if (!declined) MuxPreviousBuildLaunch.ReleaseOffer(local);
                MuxPreviousBuildLaunch.EndRestart(local);
                _muxRestartsRunning--;
                foreach (TerminalPane pane in letGo) pane.EndMuxRestartHold();
            }
        }

        /// <summary>
        /// UI thread, just before a restart sends <c>shutdown</c> to <paramref name="endpoint"/>'s daemon (review item 6, and
        /// round 2 item 6 for a remote one). The daemon's stop kills every shell, and its <c>exited</c> can reach a pane before
        /// the connection drops - an exit takes the exit policy's path, which may close a local pane, its tab, and rewrite the
        /// saved layout, and makes a remote one claim its SSH session ended. So this window's panes on that endpoint let go of
        /// their shells first (<see cref="TerminalPane.LetGoOfMuxSessionForRestart"/>): a plain detach, then the banner the
        /// daemon's end shows anyway, and Enter starts a new shell once the restart is over. They keep no session and no id,
        /// so the session saved now - and every save after - names none of those shells, and the next launch starts fresh
        /// ones in the same layout, quietly; nothing is marked ended (round 2 M2), so a shell that outlives a failed stop is
        /// like any other once reopened. Another process's windows on the same daemon cannot be reached from here: theirs
        /// take whatever the daemon's stop delivers.
        /// </summary>
        /// <param name="letGo">
        /// The restart's list of panes whose hold it ends when it is over. Each pane joins it before it lets go, so a throw
        /// part way through never leaves a pane held for good (round 3); ending the hold of a pane that let go of nothing
        /// does nothing.
        /// </param>
        private void LetGoOfShellsForRestart(MuxEndpointId endpoint, List<TerminalPane> letGo)
        {
            int count = 0;
            foreach (TerminalPane pane in _paneOwnerTab.Keys.ToList())
            {
                if (MuxEndpointId.Parse(pane.MuxEndpoint) != endpoint) continue;
                letGo.Add(pane);
                if (pane.LetGoOfMuxSessionForRestart()) count++;
            }

            SaveSessionWithoutEndedShells();
            AppLogger.Log($"[MainWindow] {count} pane(s) let go of their shells for the restart of the multiplexer on {endpoint}");
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
        /// host's connection, and a daemon still older than the version installed on the host (final review I1, residual N1),
        /// both when it asks and again just before it sends
        /// <c>shutdown</c> over that connection - nothing else is touched: not the local daemon, not another host's. Just
        /// before, this window's panes on that host let go of their shells (<see cref="LetGoOfShellsForRestart"/>). The daemon
        /// exits and its proxy with it, so the host reports it stopped, and the panes' Enter - once that connection is gone -
        /// starts a new shell, whose connect's proxy starts the installed ntilde-mux. Every way it does not happen as asked
        /// says why (<see cref="RestartOutcome"/>). The offer is released only where nothing was stopped - refused by a look,
        /// not connected, a failed send - not after a declined question, and not once <c>shutdown</c> went out (residual N1,
        /// ruling (b)): a record of the installed version that is stale the other way then costs one restart per launch, not
        /// a restart loop.
        /// </summary>
        private async Task RestartRemoteMuxAsync(MuxEndpointId id, string where)
        {
            if (_teardownDone) return;
            if (!MuxPreviousBuildLaunch.TryBeginRestart(id))
            {
                RaiseMuxRestartOutcome(id, RestartOutcome.AlreadyRestarting, where);
                return;
            }

            _muxRestartsRunning++;
            bool declined = false;
            bool shutdownSent = false;
            List<TerminalPane> letGo = [];
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
                LetGoOfShellsForRestart(id, letGo);
                MuxRestartFaultForTest?.Invoke();
                TimeSpan timeout = host.Policy.RpcTimeout;
                try
                {
                    // From the pool: on a stalled link the client's send queue may be full, and the send waits for it.
                    await Task.Run(async () =>
                    {
                        using var cts = new CancellationTokenSource(timeout);
                        await MuxShutdownRemoteDaemon(client, cts.Token).ConfigureAwait(false);
                    });
                    shutdownSent = true;
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[MainWindow] sending shutdown to ntilde-mux on {where} failed: {ex.Message}");
                    RaiseMuxRestartOutcome(id, RestartOutcome.ShutdownFailed, where);
                    return;
                }

                // Until the daemon's stop ends this connection, a shell started over it would end with the rest.
                await WhenMuxClientDisconnectedAsync(client, timeout);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[MainWindow] restarting ntilde-mux on {where} failed: {ex}");
                RaiseMuxRestartOutcome(id, RestartOutcome.Failed, where);
            }
            finally
            {
                // Residual N1 (b): once shutdown went out, the host's offer stays claimed for this launch.
                if (!declined && !shutdownSent) MuxPreviousBuildLaunch.ReleaseOffer(id);
                MuxPreviousBuildLaunch.EndRestart(id);
                _muxRestartsRunning--;
                foreach (TerminalPane pane in letGo) pane.EndMuxRestartHold();
            }
        }

        /// <summary>
        /// The remote host's live connection, to a daemon still older than the version installed on the host now - what a
        /// restart would start (final review I1, residual N1); otherwise false, with the notice that says why. A daemon that is
        /// the installed version or newer, or a host with no installed version known, is never restarted.
        /// </summary>
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
                RaiseMuxRestartOutcome(id, RestartOutcome.NotConnected, where);
                return false;
            }

            string? installed = host.RecordedRemoteDaemonVersion;
            if (!MuxPreviousBuildNotice.IsBehindInstalled(client.ServerVersion, installed))
            {
                RaiseMuxRestartOutcome(id, MuxPreviousBuildNotice.NothingToRestartRemote(client.ServerVersion, installed), where);
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
