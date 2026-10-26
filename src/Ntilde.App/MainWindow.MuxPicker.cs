using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ntilde.Shell;
using Avalonia;
using Avalonia.Media;
using Avalonia.Layout;
using Ntilde.Pty;

using Ntilde.Controls;
using Ntilde.Platform.Ssh.Launch;

namespace Ntilde
{
    /// <summary>
    /// The "Attach to session" picker across the local and remote daemons, and adopting the shells no window
    /// shows.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>UI thread: an "Attach to session…" runs - its listing, picker or connect - so another is not started (review minor 1).</summary>
        private bool _muxAttachRunning;

        /// <summary>UI thread: the picker dialog while it is shown, so "Attach to session…" asked again brings it forward.</summary>
        private Window? _muxPickerDialog;

        /// <summary>
        /// "Attach to session…" (spec §7.2, Phase 5 spec §5). Lists the sessions of the local daemon and of every connected
        /// remote host off the UI thread, offers a "Connect to …" row for each profile that keeps its remote sessions but
        /// has no connection now, lets the user pick, and opens the chosen session in a new tab attached shared. A session
        /// this window already shows is focused instead: one connection cannot hold two views of one session.
        /// </summary>
        /// <remarks>
        /// One runs at a time. Asked again meanwhile, it brings its picker forward if one is open, and otherwise does
        /// nothing: modal pickers never stack, and a second connect never starts.
        /// </remarks>
        internal async Task AttachToMuxSessionAsync()
        {
            if (!IsMuxPersistenceActive || _muxHosts is not { } hosts) return;
            if (_muxAttachRunning)
            {
                _muxPickerDialog?.Activate();
                return;
            }

            _muxAttachRunning = true;
            try
            {
                await OfferMuxSessionsAsync(hosts);
            }
            catch (Exception ex)
            {
                // Fire-and-forget from the palette and the shortcut: nothing else observes a throw.
                AppLogger.Log($"[MainWindow] Attach to session failed: {ex.Message}");
            }
            finally
            {
                _muxAttachRunning = false;
            }
        }

        /// <summary>What the picker offers: each listed host's sessions (or why not), and a connect row for each profile with no connection.</summary>
        private sealed record MuxPickerSources(
            IReadOnlyList<Ntilde.Shell.Mux.MuxPickerHostListing> Hosts,
            IReadOnlyList<Ntilde.Shell.Mux.MuxSessionPickerConnectRow> Connect);

        /// <summary>
        /// UI thread. Lists every host, opens the picker, and acts on its choice. Every remote host it may list is held from
        /// before the listing until the choice is acted on (review minor 2): a release pass meanwhile - its last pane closing,
        /// say - must not let go of a connection whose rows are on screen, and a row chosen from it then attaches over it.
        /// </summary>
        private async Task OfferMuxSessionsAsync(Ntilde.Shell.Mux.MuxConnectionHosts hosts)
        {
            IReadOnlyList<(Ntilde.Shell.Mux.MuxEndpointId Id, Ntilde.Shell.Mux.MuxConnectionHost Host)> candidates = hosts.AllByEndpoint;
            List<Ntilde.Shell.Mux.MuxEndpointId> held = [.. candidates.Select(c => c.Id).Where(id => !id.IsLocal)];
            _muxPickerHeldHosts.AddRange(held);
            try
            {
                MuxPickerSources sources = await ListMuxPickerSourcesAsync(candidates, MuxPreviousBuildLaunch);
                if (_teardownDone) return;

                // Pending ids count too: an adopted background tab has not attached (spawned) yet. Keyed by endpoint: one id
                // on two daemons is two sessions (Phase 4 spec §5).
                var openHere = new HashSet<(Ntilde.Shell.Mux.MuxEndpointId, Guid)>();
                foreach (TerminalPane p in AllPanes())
                {
                    Ntilde.Shell.Mux.MuxEndpointId endpoint = Ntilde.Shell.Mux.MuxEndpointId.Parse(p.MuxEndpoint);
                    if (p.Session is Ntilde.Mux.MuxClientSession m) openHere.Add((endpoint, m.Id));
                    if (p.MuxSessionIdToRestore is Guid pending) openHere.Add((endpoint, pending));
                }

                List<Ntilde.Shell.Mux.MuxPickerItem> items = [.. Ntilde.Shell.Mux.MuxSessionPicker.BuildRows(sources.Hosts, openHere), .. sources.Connect];
                if (!items.Exists(i => i.IsSelectable))
                {
                    // Nothing to choose: the hosts that could not be listed say why, in the error rows' words.
                    string[] errors = [.. items.OfType<Ntilde.Shell.Mux.MuxSessionPickerErrorRow>().Select(r => r.Display)];
                    EnqueueNotice("Attach to session", errors.Length > 0 ? string.Join("\n", errors) : "No sessions are running in the multiplexer.", prompted: true);
                    return;
                }

                Ntilde.Shell.Mux.MuxPickerItem? chosen = await PickMuxSession(items);
                if (_teardownDone) return;
                switch (chosen)
                {
                    case Ntilde.Shell.Mux.MuxSessionPickerConnectRow connect:
                        await ConnectForMuxPickerAsync(hosts, connect);
                        break;
                    case Ntilde.Shell.Mux.MuxSessionPickerRow row:
                        OpenMuxPickerRow(row, sources.Hosts);
                        break;
                }
            }
            finally
            {
                // A tab opened from a row needs its host by now (its pending id); the others go unless a pane uses them.
                foreach (Ntilde.Shell.Mux.MuxEndpointId id in held) _muxPickerHeldHosts.Remove(id);
                if (held.Count > 0) ScheduleRemoteMuxHostRelease();
            }
        }

        /// <summary>
        /// Off the UI thread: every host's sessions, each within its own wait and all at once, so a slow or failed host gives
        /// its error row and holds up nobody else; complete once each has answered or timed out. The local daemon is listed
        /// as before (connected, or spawned, for it). A remote host is listed only while it is connected - listing never
        /// starts a connect, which could prompt - and while its profile keeps its remote sessions: read from the store now,
        /// so a profile whose setting was turned off since gives neither rows nor a connect row (a tab opened from either
        /// would run plain SSH with the id pending). A daemon being restarted (Phase 5 Task 23) is not listed: its sessions
        /// are about to end.
        /// </summary>
        /// <param name="candidates">The hosts to list, as <see cref="OfferMuxSessionsAsync"/> took and holds them.</param>
        private Task<MuxPickerSources> ListMuxPickerSourcesAsync(
            IReadOnlyList<(Ntilde.Shell.Mux.MuxEndpointId Id, Ntilde.Shell.Mux.MuxConnectionHost Host)> candidates,
            Ntilde.Shell.Mux.MuxPreviousBuildNotice.Launch launch) => Task.Run(async () =>
        {
            List<(Guid Id, string Host)> persisted = PersistedRemoteMuxProfiles();
            var listings = new List<Task<Ntilde.Shell.Mux.MuxPickerHostListing>>();
            var connected = new HashSet<Guid>();
            foreach ((Ntilde.Shell.Mux.MuxEndpointId id, Ntilde.Shell.Mux.MuxConnectionHost host) in candidates)
            {
                Ntilde.Mux.MuxClient? client = null;
                if (id.SshProfileId is Guid profileId)
                {
                    if (!persisted.Exists(p => p.Id == profileId) || (client = host.CurrentClient) is null) continue;
                    connected.Add(profileId);
                }

                listings.Add(launch.IsRestarting(id)
                    ? Task.FromResult(new Ntilde.Shell.Mux.MuxPickerHostListing(id, host.Policy.DisplayName, null, Ntilde.Shell.Mux.MuxPickerHostError.Restarting))
                    : Task.Run(() => ListMuxSessionsForPickerAsync(id, host, client)));
            }

            Ntilde.Shell.Mux.MuxPickerHostListing[] listed = await Task.WhenAll(listings).ConfigureAwait(false);
            List<Ntilde.Shell.Mux.MuxSessionPickerConnectRow> connect = [.. persisted
                .Where(p => !connected.Contains(p.Id))
                .Select(p => new Ntilde.Shell.Mux.MuxSessionPickerConnectRow(p.Id, p.Host))];
            return new MuxPickerSources(listed, connect);
        });

        /// <summary>
        /// One host's sessions: <paramref name="connected"/>'s (a remote host's live client, asked within its policy's request
        /// wait), or for the local daemon its host's client, as before. A failure is logged and kept as a kind only.
        /// </summary>
        private static async Task<Ntilde.Shell.Mux.MuxPickerHostListing> ListMuxSessionsForPickerAsync(
            Ntilde.Shell.Mux.MuxEndpointId id, Ntilde.Shell.Mux.MuxConnectionHost host, Ntilde.Mux.MuxClient? connected)
        {
            string name = host.Policy.DisplayName;
            Ntilde.Mux.MuxClient? client = connected ?? host.GetClient(TimeSpan.FromSeconds(5));
            if (client is null) return new(id, name, null, Ntilde.Shell.Mux.MuxPickerHostError.ConnectionFailed);
            using var cts = new System.Threading.CancellationTokenSource(id.IsLocal ? TimeSpan.FromSeconds(5) : host.Policy.RpcTimeout);
            try
            {
                return new(id, name, await client.ListSessionsAsync(cts.Token).ConfigureAwait(false), null);
            }
            catch (Exception ex)
            {
                // Any failure: one host's must never stop the others from being listed.
                AppLogger.Log($"[MainWindow] listing the sessions on {name} failed: {ex.Message}");
                return new(id, name, null, Ntilde.Shell.Mux.MuxSessionPicker.ErrorOf(ex));
            }
        }

        /// <summary>The SSH profiles that keep their remote sessions, from one read of the store now, by name, with their hosts' names.</summary>
        private List<(Guid Id, string Host)> PersistedRemoteMuxProfiles() =>
        [
            .. _sshConnectionService.GetStoredProfiles()
                .Where(p => p is { MuxOptions.PersistRemoteSessions: true })
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Id)
                .Select(p => (p.Id, Ntilde.Shell.Mux.Remote.RemoteMuxConnector.DisplayNameOf(p))),
        ];

        /// <summary>
        /// UI thread: the remote hosts an "Attach to session…" holds - those its picker lists, and one its "Connect to …" row
        /// connects - so no release pass lets them go meanwhile. A host held twice (a picker reopened after a connect) is in
        /// it twice: each holder removes its own.
        /// </summary>
        private readonly List<Ntilde.Shell.Mux.MuxEndpointId> _muxPickerHeldHosts = [];

        /// <summary>
        /// A "Connect to …" row (Phase 5 spec §5). A remote host is connected only while a pane needs it, so the shell just
        /// detached from it is listed once it is connected again. The user is waiting, so this connects the usual
        /// interactive way, off the UI thread - any prompt comes from that connect, at most as it always does - then lists
        /// every host again and reopens the picker. The host is held meanwhile, so no release pass lets it go; afterwards
        /// the usual pass (<see cref="ScheduleRemoteMuxHostRelease"/>) lets it go unless a pane now uses it.
        /// </summary>
        /// <remarks>
        /// "Connecting to user@host…" shows for as long as the connect takes (fix round 1). It is gone before the reopened
        /// picker comes up, so it never sits under that modal; a failed connect replaces it with its own line; a connect
        /// given up on (the window closing) or a throw takes it away with nothing more.
        /// </remarks>
        private async Task ConnectForMuxPickerAsync(Ntilde.Shell.Mux.MuxConnectionHosts hosts, Ntilde.Shell.Mux.MuxSessionPickerConnectRow row)
        {
            Ntilde.Shell.Mux.MuxEndpointId endpoint = Ntilde.Shell.Mux.MuxEndpointId.ForSsh(row.ProfileId);
            string connecting = $"Attach to session\nconnecting\n{endpoint}"; // by endpoint: two profiles may share a user@host
            _muxPickerHeldHosts.Add(endpoint);
            EnqueueNotice("Attach to session", $"Connecting to {row.HostDisplayName}\u2026", key: connecting, untilDismissed: true, prompted: true);
            try
            {
                bool connected = await Task.Run(() => hosts.GetOrCreate(endpoint) is { } host && host.GetClient(host.Policy.ConnectTimeout) is not null);
                if (_teardownDone) return;
                DismissNotice(connecting);
                if (!connected)
                {
                    EnqueueNotice("Attach to session", $"Could not connect to {row.HostDisplayName}.", prompted: true);
                    return;
                }

                await OfferMuxSessionsAsync(hosts);
            }
            finally
            {
                DismissNotice(connecting); // given up on, or thrown: a no-op once dismissed above
                _muxPickerHeldHosts.Remove(endpoint);
                ScheduleRemoteMuxHostRelease();
            }
        }

        /// <summary>
        /// UI thread. Opens <paramref name="row"/>'s session in a new tab attached shared, or focuses the pane already showing
        /// it. A remote session opens in an SSH tab of its profile, not a shell tab: its spawn goes to that profile's daemon
        /// and joins the session shared there (MuxTerminalSessionFactory's shared attach is the same for every endpoint).
        /// </summary>
        private void OpenMuxPickerRow(Ntilde.Shell.Mux.MuxSessionPickerRow row, IReadOnlyList<Ntilde.Shell.Mux.MuxPickerHostListing> listed)
        {
            // Re-checked, not read from the row: the picker is modal, and a tab may have opened it meanwhile, or a restart
            // of its daemon begun.
            if (FocusPaneShowingMuxSession(row.Endpoint, row.SessionId)) return;
            if (MuxPreviousBuildLaunch.IsRestarting(row.Endpoint))
            {
                EnqueueNotice("Attach to session", new Ntilde.Shell.Mux.MuxSessionPickerErrorRow(row.Endpoint, row.HostDisplayName, Ntilde.Shell.Mux.MuxPickerHostError.Restarting).Display, prompted: true);
                return;
            }

            TerminalPane pane;
            if (row.Endpoint.SshProfileId is not Guid profileId)
            {
                if (listed.FirstOrDefault(l => l.Endpoint.IsLocal)?.Sessions?.FirstOrDefault(s => s.SessionId == row.SessionId) is not { } summary) return;
                pane = new TerminalPane(ShellHelper.ResolveExecutableOrDefault(summary.Command), summary.Arguments ?? string.Empty, _settings)
                {
                    MuxSessionIdToRestore = row.SessionId,
                    MuxAttachSharedToRestore = true,
                };
            }
            else
            {
                // Read again: a profile that stopped keeping its sessions while the picker was open would open plain SSH
                // with the id pending.
                if (_sshConnectionService.GetStoredProfile(profileId) is not { MuxOptions.PersistRemoteSessions: true }
                    || _sshConnectionService.GetConnectionProfile(profileId) is not { } profile)
                {
                    EnqueueNotice("Attach to session", $"The profile for {row.HostDisplayName} no longer keeps remote sessions running.", prompted: true);
                    return;
                }

                // A fresh runtime copy, made an ssh launch as AddTab makes one. The endpoint is set up front, so the pending
                // id is on it - for "open here", saves and the host release - before the spawn runs.
                profile.Command = OperatingSystem.IsWindows() ? "ssh.exe" : "ssh";
                profile.Arguments = string.Empty;
                pane = new TerminalPane(profile, _settings, SshDiagnosticsLevel.None)
                {
                    MuxSessionIdToRestore = row.SessionId,
                    MuxAttachSharedToRestore = true,
                    MuxEndpoint = row.Endpoint.ToString(),
                };
            }

            LeaveFocusModeForNavigation();
            AddTabWithPane(pane, row.Name, select: true);
        }

        /// <summary>
        /// The pane's daemon session (shown or pending) is on the local daemon, the one orphan adoption and the teardown
        /// count speak of (Phase 4 spec §5): a remote pane's id names another daemon's session.
        /// </summary>
        private static bool ShowsLocalMuxEndpoint(TerminalPane pane) => Ntilde.Shell.Mux.MuxEndpointId.Parse(pane.MuxEndpoint).IsLocal;

        /// <summary>UI thread. Selects and focuses the pane showing (or about to attach) <paramref name="id"/> on <paramref name="endpoint"/>; false when none does.</summary>
        private bool FocusPaneShowingMuxSession(Ntilde.Shell.Mux.MuxEndpointId endpoint, Guid id)
        {
            TerminalPane? pane = AllPanes().FirstOrDefault(p => Ntilde.Shell.Mux.MuxEndpointId.Parse(p.MuxEndpoint) == endpoint
                && ((p.Session is Ntilde.Mux.MuxClientSession m && m.Id == id) || p.MuxSessionIdToRestore == id));
            if (pane is null) return false;
            TabItem? tab = ResolveOwningTabForPane(pane);
            TabControl? tabs = this.FindControl<TabControl>("Tabs");
            // Only going to another tab leaves focus mode; a pane of the tab on screen is reached where it is.
            if (!ReferenceEquals(tab, tabs?.SelectedItem)) LeaveFocusModeForNavigation();
            if (tab is not null && tabs is not null) tabs.SelectedItem = tab;
            UpdateActivePane(pane);
            FocusPaneTerminal(pane, defer: true);
            return true;
        }

        private async Task<Ntilde.Shell.Mux.MuxPickerItem?> ShowMuxSessionPickerAsync(IReadOnlyList<Ntilde.Shell.Mux.MuxPickerItem> rows)
        {
            (Window dialog, Task<Ntilde.Shell.Mux.MuxPickerItem?> result) = BuildMuxSessionPickerWindow(rows);
            _muxPickerDialog = dialog;
            try
            {
                await dialog.ShowDialog(this);
                return await result;
            }
            finally
            {
                _muxPickerDialog = null;
            }
        }

        /// <summary>
        /// The "Attach to Session" dialog, not yet shown; the task completes with the chosen line - a session, or a host
        /// to connect to - (null: cancelled) when the window closes. A line that only informs (a host that could not be
        /// listed) is shown disabled and never chosen. Enter attaches, Escape cancels. Separate from
        /// <see cref="ShowMuxSessionPickerAsync"/> so a headless test can show it and press keys.
        /// </summary>
        internal (Window Window, Task<Ntilde.Shell.Mux.MuxPickerItem?> Result) BuildMuxSessionPickerWindow(IReadOnlyList<Ntilde.Shell.Mux.MuxPickerItem> rows)
        {
            Ntilde.Shell.Mux.MuxPickerItem? chosen = null;
            var dialog = CreateThemedDialogWindow("Attach to Session", 640, 360, canResize: true);
            var list = new ListBox
            {
                ItemsSource = rows.Select(r => new ListBoxItem { Content = r.Display, IsEnabled = r.IsSelectable }).ToList(),
                SelectedIndex = rows.ToList().FindIndex(r => r.IsSelectable),
                MaxHeight = 240,
            };
            var attach = new Button { Content = "Attach", Width = 92, IsDefault = true };
            var cancel = new Button { Content = "Cancel", Width = 92, IsCancel = true };
            var closed = new TaskCompletionSource<Ntilde.Shell.Mux.MuxPickerItem?>(TaskCreationOptions.RunContinuationsAsynchronously);
            dialog.Closed += (_, _) => closed.TrySetResult(chosen);
            void Accept()
            {
                if (list.SelectedIndex < 0 || !rows[list.SelectedIndex].IsSelectable) return;
                chosen = rows[list.SelectedIndex];
                dialog.Close();
            }

            attach.Click += (_, _) => Accept();
            // Only a double-tap on a line that can be chosen accepts. A disabled line takes no input, so a double-tap on it
            // reaches the list itself, and must not accept whatever line is selected elsewhere (review minor 4).
            list.DoubleTapped += (_, e) =>
            {
                if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { IsEnabled: true }) Accept();
            };
            cancel.Click += (_, _) => dialog.Close();
            dialog.Content = new Border
            {
                Padding = new Thickness(16),
                Child = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Attach to a running session. It opens in a new tab and stays shared with its other windows.",
                            TextWrapping = TextWrapping.Wrap,
                        },
                        list,
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { cancel, attach },
                        },
                    },
                },
            };

            return (dialog, closed.Task);
        }

        /// <summary>
        /// Spec §9 orphans: running daemon sessions with no attached client that the restored session
        /// does not reference open as new tabs. The daemon is asked off the UI thread; nothing here
        /// blocks it.
        /// </summary>
        private async Task AdoptOrphanedMuxSessionsAsync(Ntilde.Shell.Mux.MuxConnectionHost host, HashSet<Guid> referenced)
        {
            try
            {
                (IReadOnlyList<Ntilde.Mux.Contracts.SessionSummary> orphans, int detached) = await Task.Run(async () =>
                {
                    Ntilde.Mux.MuxClient? client = host.GetClient(TimeSpan.FromSeconds(10));
                    if (client is null) return ((IReadOnlyList<Ntilde.Mux.Contracts.SessionSummary>)[], 0);
                    IReadOnlyList<Ntilde.Mux.Contracts.SessionSummary> all = await client.ListSessionsAsync().ConfigureAwait(false);
                    bool v2 = client.ProtocolVersion >= Ntilde.Mux.Contracts.MuxProtocol.SessionEventsVersion;
                    return (Ntilde.Shell.Mux.MuxOrphans.Select(all, referenced, v2), Ntilde.Shell.Mux.MuxOrphans.CountUserDetached(all, referenced, v2));
                }).ConfigureAwait(false);
                if (orphans.Count == 0 && detached == 0) return;

                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (orphans.Count > 0) AdoptOrphansOnUiThread(orphans);
                        if (detached > 0) AnnounceDetachedShellsOnce(detached);
                    }
                    catch (Exception ex)
                    {
                        // Posted: nothing awaits this, so a throw would reach the dispatcher unhandled.
                        AppLogger.Log($"[MainWindow] orphan adoption failed: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[MainWindow] orphan adoption failed: {ex.Message}");
            }
        }

        private bool _detachedShellsAnnounced; // UI thread: once per launch

        /// <summary>So deliberately detached shells are not forgotten (spec §7.7): one toast per launch.</summary>
        internal void AnnounceDetachedShellsOnce(int count)
        {
            if (_detachedShellsAnnounced || _teardownDone) return;
            _detachedShellsAnnounced = true;
            string message = count == 1
                ? "1 detached shell is running \u2014 Attach to session\u2026 to reopen it"
                : $"{count} detached shells are running \u2014 Attach to session\u2026 to reopen them";
            EnqueueNotice("Detached shells", message);
        }

        /// <summary>UI thread. Opens each orphan in a background tab (selection and focus stay put).</summary>
        private void AdoptOrphansOnUiThread(IReadOnlyList<Ntilde.Mux.Contracts.SessionSummary> orphans)
        {
            if (_teardownDone) return;
            // A pane this window spawned can be listed between its spawn and its attach
            // (AttachedClients still 0). Its Session is assigned on this thread as soon as the spawn
            // returns, so by now every such pane names its id here. Pending ids count too.
            // Local panes only: a remote pane's id is another daemon's session (Phase 4 spec §5).
            var live = new HashSet<Guid>();
            foreach (TerminalPane p in AllPanes())
            {
                if (!ShowsLocalMuxEndpoint(p)) continue;
                if (p.Session is Ntilde.Mux.MuxClientSession m) live.Add(m.Id);
                if (p.MuxSessionIdToRestore is Guid pending) live.Add(pending);
            }

            int adopted = 0;
            foreach (Ntilde.Mux.Contracts.SessionSummary s in orphans)
            {
                if (!live.Add(s.SessionId)) continue;
                var pane = new TerminalPane(ShellHelper.ResolveExecutableOrDefault(s.Command), s.Arguments ?? string.Empty, _settings)
                {
                    MuxSessionIdToRestore = s.SessionId,
                    MuxAdoptedOrphan = true,
                };
                // Background tab: it spawns (attaches) when first shown, and until then the session
                // file keeps its id through MuxSessionIdToRestore.
                AddTabWithPane(pane, string.IsNullOrWhiteSpace(s.Title) ? s.Command : s.Title, select: false);
                adopted++;
            }

            if (adopted == 0) return;
            AppLogger.Log($"[MainWindow] reattached {adopted} detached mux session(s)");
            EnqueueNotice("Sessions restored", $"Reattached {adopted} detached session{(adopted == 1 ? "" : "s")}");
        }
    }
}
