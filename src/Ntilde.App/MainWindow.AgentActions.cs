using Avalonia.Controls;
using Avalonia.Threading;
using Ntilde.Shell;

using Ntilde.Controls;

namespace Ntilde
{
    /// <summary>
    /// The agent host's act executor: spawning and closing panes for an agent, capturing them, and the
    /// sessions this window shows.
    /// </summary>
    public partial class MainWindow
    {
        // A3 per-profile SSH allowlist probe, handed to the agent-host endpoint.
        // Reads the SSH profile store (off the UI thread on an IPC call), so it
        // must not touch Avalonia state — the store is thread-safe. Unknown or
        // non-SSH profile ids return false (fail closed).
        private bool IsSshProfileAgentAllowed(Guid profileId)
        {
            try
            {
                return _sshConnectionService?.GetStoredProfile(profileId)?.AllowAgentAccess == true;
            }
            catch
            {
                return false;
            }
        }

        // ── A3 agent act executor (spawn/close) ──────────────────────────────
        // Implemented on MainWindow because spawning/closing is inherently UI-thread
        // tab work. Published to the agent-host endpoint via SetActionExecutor; the
        // endpoint calls these from its IPC thread, so every body marshals to the UI
        // thread. Gating (act toggle) is enforced by the endpoint before we get here;
        // the SSH allowlist is enforced here since we own profile resolution.

        async Task<(AgentHost.AgentSpawnResult? Result, AgentHost.AgentSpawnError? Error)> AgentHost.IAgentActionExecutor.SpawnAsync(string? profileName)
        {
            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                TerminalProfile? profile;
                bool isSsh;

                if (string.IsNullOrWhiteSpace(profileName))
                {
                    profile = _settings.Profiles.Find(p => p.Id == _settings.DefaultProfileId)
                              ?? (_settings.Profiles.Count > 0 ? _settings.Profiles[0] : null);
                    isSsh = false;
                }
                else
                {
                    // Local settings profiles resolve before SSH store profiles on
                    // a name collision (documented in the A3 design).
                    profile = _settings.Profiles.Find(
                        p => p.Type == ConnectionType.Local &&
                             string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
                    isSsh = false;

                    if (profile == null)
                    {
                        TerminalProfile? sshProfile = _sshConnectionService.GetConnectionProfiles()
                            .FirstOrDefault(p => string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
                        if (sshProfile != null)
                        {
                            // SSH targets must be allowlisted per profile.
                            if (!IsSshProfileAgentAllowed(sshProfile.Id))
                            {
                                return ((AgentHost.AgentSpawnResult?)null, (AgentHost.AgentSpawnError?)AgentHost.AgentSpawnError.ProfileNotAllowed);
                            }
                            profile = sshProfile;
                            isSsh = true;
                        }
                    }
                }

                if (profile == null)
                {
                    return (null, AgentHost.AgentSpawnError.ProfileNotFound);
                }

                try
                {
                    AddTab(profile);
                    var pane = _currentPane;
                    if (pane == null)
                    {
                        return (null, AgentHost.AgentSpawnError.SpawnFailed);
                    }

                    Guid? tabId = _paneOwnerTab.TryGetValue(pane, out var tab)
                        ? GetPersistentTabId(tab)
                        : (Guid?)null;
                    string kind = isSsh || profile.Type == ConnectionType.SSH ? "ssh" : "local";
                    var result = new AgentHost.AgentSpawnResult(pane.PaneId, tabId, profile.Name, kind);
                    return ((AgentHost.AgentSpawnResult?)result, (AgentHost.AgentSpawnError?)null);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainWindow] Agent spawn failed: {ex.Message}");
                    return (null, AgentHost.AgentSpawnError.SpawnFailed);
                }
            });
        }

        async Task<bool> AgentHost.IAgentActionExecutor.ClosePaneAsync(Guid paneId)
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                TerminalPane? target = _paneOwnerTab.Keys.FirstOrDefault(p => p.PaneId == paneId);
                if (target == null)
                {
                    return false;
                }

                // Make the target the active pane of its (selected) tab, then reuse
                // the split-aware close path with the confirm dialog bypassed.
                if (_paneOwnerTab.TryGetValue(target, out var tab))
                {
                    var tabs = this.FindControl<TabControl>("Tabs");
                    if (tabs != null) tabs.SelectedItem = tab;
                    _activePaneByTab[tab] = target;
                }
                _currentPane = target;

                // CloseActivePaneAsync can no-op if a close is already in flight
                // (_closePaneInProgress); report the real outcome by checking the
                // pane is actually gone, so closeSession never claims a false success.
                await CloseActivePaneAsync(skipConfirm: true);
                return !_paneOwnerTab.ContainsKey(target);
            });
        }

        async Task<AgentHost.AgentLiveCapture?> AgentHost.IAgentActionExecutor.CaptureLiveAsync(Guid paneId, int maxWidth, double scale)
        {
            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                TerminalPane? target = _paneOwnerTab.Keys.FirstOrDefault(p => p.PaneId == paneId);
                return target?.CaptureLiveForAgent(maxWidth, scale);
            });
        }

        /// <summary>
        /// Every (endpoint, mux session id) a pane of this window shows, or is about to (a pending
        /// <see cref="TerminalPane.MuxSessionIdToRestore"/>: a restored tab not shown yet, an adopted orphan), on every
        /// endpoint: <see cref="OfferMuxSessionsAsync"/>'s "open here", not only the local daemon's. The windowless source
        /// asks it from the agent host's thread; this is its one touch of the window, posted to the UI thread and
        /// awaited there, never waited for. Posted through this window's own dispatcher, never the
        /// <c>Dispatcher.UIThread</c> static: an off-thread read of that is what poisoned headless test runs (#81).
        /// </summary>
        private async Task<IReadOnlySet<(string Endpoint, Guid Id)>> MuxSessionsShownHereAsync()
        {
            return await Dispatcher.InvokeAsync<IReadOnlySet<(string Endpoint, Guid Id)>>(() =>
            {
                var shown = new HashSet<(string Endpoint, Guid Id)>();
                foreach (TerminalPane p in AllPanes())
                {
                    string endpoint = Ntilde.Shell.Mux.MuxEndpointId.Parse(p.MuxEndpoint).ToString();
                    if (p.Session is Ntilde.Mux.MuxClientSession m) shown.Add((endpoint, m.Id));
                    if (p.MuxSessionIdToRestore is Guid pending) shown.Add((endpoint, pending));
                }

                return shown;
            });
        }
    }
}
