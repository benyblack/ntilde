using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ntilde.Shell;
using Avalonia;
using Avalonia.Media;
using Avalonia.Layout;
using Ntilde.Pty;

using Ntilde.Controls;

namespace Ntilde
{
    /// <summary>
    /// Closing the window and quitting: the first-close question, ending or keeping local shells, and tearing
    /// the window down.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>The first-close question's three outcomes (spec R1): Escape or the dialog's X is <see cref="FirstCloseAction.Cancel"/>.</summary>
        internal enum FirstCloseAction { Cancel, Keep, Close }

        /// <summary>An answer to the first-close question; <paramref name="Remember"/> is its "Don't ask again".</summary>
        internal readonly record struct FirstCloseAnswer(FirstCloseAction Action, bool Remember);

        /// <summary>
        /// The first-close question (spec R1), given how many local shells the close would leave running. A seam so
        /// tests answer without a modal; the constructor assigns <see cref="ShowFirstCloseDialogAsync"/>.
        /// </summary>
        internal Func<int, Task<FirstCloseAnswer>> ConfirmFirstClose { get; set; }

        /// <summary>
        /// The "Quit and close all shells" confirmation (Task 17), given how many shells the local daemon is running
        /// (-1 when that is unknown). A seam so tests answer without a modal; the constructor assigns
        /// <see cref="ShowQuitAndCloseAllDialogAsync"/>.
        /// </summary>
        internal Func<int, Task<bool>> ConfirmQuitAndCloseAll { get; set; }

        /// <summary>Where "Don't ask again" keeps the first-close answer (R1). A seam so tests use a scratch root.</summary>
        internal Ntilde.Shell.Mux.MuxCloseChoiceStore MuxCloseChoiceStore { get; set; } = Ntilde.Shell.Mux.MuxCloseChoiceStore.Default;

        /// <summary>Test seam: runs inside <see cref="PerformAppTeardown"/> right after its one-shot guard.</summary>
        internal Action? TeardownFaultForTest { get; set; }

        private void ResetTabCollections()
        {
            _activePaneByTab.Clear();
            _paneZoomStateByTab.Clear();
            _zoomedPaneIdByTab.Clear();
            _broadcastEnabledTabs.Clear();
            _tabIds.Clear();
            _layoutModelByTab.Clear();
            _tabMru.Clear();
            _tabStateByTab.Clear();
            _pendingVisualRefreshTabs.Clear();
        }

        /// <summary>
        /// Disposes every pane this window owns, and with them the PTY and child shell behind
        /// each one, then lets the window go: its hooks released (<see cref="ReleaseWindowHooks"/>)
        /// and the window closed without the app's teardown. For tests, which build a real window
        /// and then abandon it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Nothing else reclaims the shells. <c>Window.Close()</c> runs <see cref="PerformAppTeardown"/>,
        /// which saves the session and stops timers but never walks the tabs, and a test that only
        /// drops its reference leaves the shells running - a full run finished with 53 of them
        /// alive.
        /// </para>
        /// <para>
        /// Zoom is why this cannot be a walk of <c>ti.Content</c> from outside. Zooming moves the
        /// tab's real root off the visual tree into <c>_paneZoomStateByTab</c> and puts the zoomed
        /// pane in its place, so disposing the content of a zoomed tab reaches exactly one pane and
        /// abandons its siblings. <see cref="CloseTab"/> exits zoom first for the same reason, and
        /// this mirrors it.
        /// </para>
        /// <para>
        /// The window is closed because nothing else lets it go (#543). Most tests leave their window open, and Avalonia's
        /// input manager holds every open window until it closes; so did its armed update-check timer and its handlers on
        /// the agent registry's events. Every window a run built stayed in memory, and the App.Tests host reached 11 GB.
        /// The close runs with the teardown marked done, because <see cref="PerformAppTeardown"/> is the app's, not the
        /// test's: it saves the session to whatever app-data root is current - the developer's own, when a test took none
        /// - and stops the process-wide <c>AgentHostService</c> singleton, which the next test would inherit.
        /// </para>
        /// </remarks>
        internal void TearDownForTest()
        {
            DisposeAllPanes();
            // Last: disposing the panes runs this window's own handlers, and one of them could start a timer again.
            ReleaseWindowHooks();
            _teardownDone = true;
            Close();
        }

        /// <summary>Disposes every pane of every tab, zoomed or not, and any pane whose tab has left the strip.</summary>
        private void DisposeAllPanes()
        {
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return;

            foreach (var ti in tabs.Items.Cast<TabItem>().ToList())
            {
                if (_paneZoomStateByTab.ContainsKey(ti))
                {
                    ExitPaneZoom(ti, publishEvent: false, forTeardown: true);
                }

                if (ti.Content is Control content)
                {
                    DisposeControlTree(content);
                }
            }

            // Whatever the walk above did not reach is still registered here, because
            // DisposeControlTree unwires every pane it disposes and unwiring is what removes it
            // from this map. That is how a pane whose tab has left the collection is still found:
            // MainWindowStartupTests calls tabs.Items.Clear() to build its own strip, and the
            // constructor's live tab - shell and all - goes with it.
            foreach (var orphan in _paneOwnerTab.Keys.ToList())
            {
                DisposeControlTree(orphan);
            }
        }

        /// <summary>
        /// Disposes every pane of every tab, for the paths that replace the whole strip.
        /// </summary>
        /// <remarks>
        /// Zoom is exited first, for the reason <c>CloseTab</c> does it: <see cref="EnterPaneZoom"/>
        /// moves the tab's real root off the visual tree into <c>_paneZoomStateByTab</c> and leaves
        /// only the zoomed pane in <c>Content</c>, so disposing the content of a zoomed tab reaches
        /// one pane and abandons its siblings. Here that abandonment is permanent rather than merely
        /// untidy: <see cref="ResetTabCollections"/> runs on the very next line of
        /// <see cref="ApplySessionSnapshot"/> and clears the map that held the off-tree root, after
        /// which nothing can reach those panes at all and their shells outlive the workspace that
        /// owned them. Zoom survives a save/restore cycle, and every caller of this is a user action
        /// - open a workspace bundle, load a workspace, apply a template - that a zoomed tab is
        /// perfectly free to be in the middle of.
        /// </remarks>
        private void DisposeAllTabs(TabControl tabs)
        {
            foreach (var item in tabs.Items.Cast<TabItem>().ToList())
            {
                if (_paneZoomStateByTab.ContainsKey(item))
                {
                    ExitPaneZoom(item, publishEvent: false, forTeardown: true);
                }

                if (item.Content is Control content)
                {
                    DisposeControlTree(content);
                }
            }
        }

        private async Task<FirstCloseAnswer> ShowFirstCloseDialogAsync(int count)
        {
            // A close from the taskbar can reach a minimized window, and a dialog it owns would open out of sight:
            // the close would seem to do nothing. (A hidden owner makes ShowDialog throw, which closes as Keep.)
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            (Window dialog, Task<FirstCloseAnswer> result) = BuildFirstCloseDialog(count);
            await dialog.ShowDialog(this);
            return await result;
        }

        /// <summary>
        /// The first-close question (spec R1), not yet shown; the task completes with the answer when it closes.
        /// Enter keeps the shells (the safe default) wherever focus is, with "Don't ask again" as it stands; Escape or
        /// closing the dialog cancels the window's close; Close them (which ends the shells) is never the default and
        /// never answered by Enter. Separate from <see cref="ShowFirstCloseDialogAsync"/> so a headless test can show
        /// it and press keys.
        /// </summary>
        internal (Window Window, Task<FirstCloseAnswer> Result) BuildFirstCloseDialog(int count)
        {
            var answer = new FirstCloseAnswer(FirstCloseAction.Cancel, Remember: false);
            var dialog = CreateThemedDialogWindow("Close Ntilde", 480, 250, canResize: false);
            var dontAskAgain = new CheckBox { Content = "Don't ask again" };
            var keep = new Button { Content = "Keep running", MinWidth = 110, IsDefault = true };
            var close = new Button { Content = "Close them", MinWidth = 110 };
            var closed = new TaskCompletionSource<FirstCloseAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
            dialog.Closed += (_, _) => closed.TrySetResult(answer);
            dialog.Opened += (_, _) => keep.Focus();
            // Tunnel, so the dialog sees the key before whatever holds focus: after a click on "Don't ask again" that is
            // the CheckBox, which would take Enter for itself (toggling the box) before IsDefault is consulted. Enter
            // always keeps, with the box as it stands - nothing ends the shells by key; Space still presses a button.
            dialog.AddHandler(InputElement.KeyDownEvent, (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    answer = new FirstCloseAnswer(FirstCloseAction.Keep, dontAskAgain.IsChecked == true);
                }
                else if (e.Key == Key.Escape)
                {
                    answer = new FirstCloseAnswer(FirstCloseAction.Cancel, Remember: false);
                }
                else
                {
                    return;
                }

                e.Handled = true;
                dialog.Close();
            }, RoutingStrategies.Tunnel);
            keep.Click += (_, _) => { answer = new FirstCloseAnswer(FirstCloseAction.Keep, dontAskAgain.IsChecked == true); dialog.Close(); };
            close.Click += (_, _) => { answer = new FirstCloseAnswer(FirstCloseAction.Close, dontAskAgain.IsChecked == true); dialog.Close(); };
            string body = count > 1 ? $"Reopen Ntilde to get them back. ({count} shells)" : "Reopen Ntilde to get them back.";
            dialog.Content = new Border
            {
                Padding = new Thickness(16),
                Child = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = "Your shells keep running in the background.", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
                        dontAskAgain,
                        new TextBlock { Text = "Turn this off in Settings → Keep shells running when the window closes.", FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { keep, close },
                        },
                    },
                },
            };

            return (dialog, closed.Task);
        }
        private bool _teardownDone;

        /// <summary>
        /// This close is settled - the first-close question was answered (R1), or Task 17's quit was confirmed - so
        /// the close that follows goes straight to the teardown without asking.
        /// </summary>
        private bool _closeConfirmed;

        /// <summary>The first-close question is posted or on screen: another close request meanwhile is held, not asked again.</summary>
        private bool _firstCloseQuestionOpen;

        /// <summary>Local shells <see cref="EndLocalSessionsOnTeardown"/> has ended: neither counted as kept nor saved for reattach.</summary>
        private readonly HashSet<Guid> _localSessionsEndedOnClose = [];

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
            if (!ProceedWithClose(e.CloseReason)) e.Cancel = true;
        }

        /// <summary>
        /// Test seam: what <see cref="OnClosing"/> does for a close with <paramref name="reason"/> (only Avalonia can
        /// build the event args, and only its lifetime raises the shutdown reasons). True when the close was held.
        /// </summary>
        internal bool HandleClosingForTest(WindowCloseReason reason) => !ProceedWithClose(reason);

        /// <summary>
        /// Release hardening item 2 (amends R19): the application lifetime's shutdown request - macOS Cmd+Q, the usual way to
        /// quit there - asks the first-close question as a window close does, instead of keeping the shells unasked. The
        /// lifetime cannot wait for a modal, so App cancels the request when this returns true; the window then closes the way
        /// its close button does (asked, or a remembered "Close them" waiting for the daemon), and once that close went
        /// through, <paramref name="shutdown"/> quits for real. Cancel leaves the window open and the application running.
        /// False - nothing would be held: no live shells, a remembered answer that needs no daemon, the question already
        /// settled - lets the shutdown go on, and the window's ApplicationShutdown close applies what it always did. An OS
        /// shutdown is never held (R19). Called on the UI thread.
        /// </summary>
        internal bool HoldShutdownForFirstClose(bool isOSShutdown, Action shutdown)
        {
            ArgumentNullException.ThrowIfNull(shutdown);
            if (isOSShutdown || !HoldCloseForFirstCloseQuestion(WindowCloseReason.WindowClosing, dryRun: true)) return false;
            _ = CloseThenShutdownAsync(shutdown);
            return true;
        }

        /// <summary>
        /// <see cref="HoldShutdownForFirstClose"/>'s close: a window close, settled - <see cref="_heldCloseSettled"/> completes
        /// once the posted question or the remembered answer's work is done - then <paramref name="shutdown"/> when the window
        /// did close. Never throws.
        /// </summary>
        private async Task CloseThenShutdownAsync(Action shutdown)
        {
            try
            {
                Close();
                await _heldCloseSettled;
                if (_teardownDone) shutdown();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[MainWindow] settling the close before quitting failed; not quitting: {ex}");
            }
        }

        /// <summary>
        /// The work a held close posted (the question, or a remembered "Close them" waiting for the daemon), completed once it
        /// settled: closed again, or left open. Completed when nothing is held. UI thread.
        /// </summary>
        private Task _heldCloseSettled = Task.CompletedTask;

        /// <summary>Posts <paramref name="settle"/>, the held close's work, and tracks it in <see cref="_heldCloseSettled"/>.</summary>
        private void PostHeldClose(Func<Task> settle)
        {
            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _heldCloseSettled = settled.Task;
            Dispatcher.Post(async () =>
            {
                try { await settle(); }
                finally { settled.TrySetResult(); }
            });
        }

        /// <summary>OnClosing's body: false holds the close while the first-close question is asked; otherwise the teardown runs.</summary>
        private bool ProceedWithClose(WindowCloseReason reason)
        {
            if (HoldCloseForFirstCloseQuestion(reason)) return false;
            PerformAppTeardown();
            return true;
        }

        /// <summary>
        /// Spec R1: a window closing with live local shells asks whether they keep running, unless the answer was
        /// remembered ("Don't ask again"), which is then applied without asking. Not asked when this close is already
        /// settled, when persistence is off (the setting, and the factory with it: with "Off" nothing new appears), or
        /// when no local shell would be left running (remote shells never ask).
        /// <para>
        /// Only a window close asks; a shutdown is never held, since holding it would cancel the shutdown itself. macOS Cmd+Q,
        /// the usual way to quit there, is asked before it gets here (release hardening item 2): App cancels the lifetime's
        /// shutdown request while <see cref="HoldShutdownForFirstClose"/> closes the window this way, then quits. A shutdown
        /// that reaches this anyway (<see cref="WindowCloseReason.ApplicationShutdown"/>: one that needed no question, or
        /// <c>TryShutdown</c>) applies a remembered answer - a remembered "close" ends the shells - and with none keeps them. The OS ending the session (<see cref="WindowCloseReason.OSShutdown"/>)
        /// never kills, whatever was remembered: it behaves like Keep, as every close did before R1.
        /// </para>
        /// <para>
        /// While the question is open another window close is held, not asked again - unless nothing is left to keep
        /// (the last tab closed because its shell exited): that close goes through, and the late answer is dropped,
        /// rather than leave a Cancel with a window that has no tabs.
        /// </para>
        /// <para>
        /// The shells of tabs not shown yet count too (final review M3, <see cref="PendingLocalMuxSessionIds"/>): their
        /// panes hold the ids pending, and the shells run all the same. Only the daemon can say whether each still runs and
        /// whether another client shows it, so with any pending the close is held while it is asked - for the question, or
        /// for a remembered "Close them" - except by a shutdown, which cannot be held: it ends the live shells only.
        /// </para>
        /// True holds this close: the question is posted, never asked inside OnClosing, and its answer closes again.
        /// </summary>
        /// <param name="dryRun">
        /// Only whether this close would be held, with nothing done - no question posted, no shell ended: what
        /// <see cref="HoldShutdownForFirstClose"/> asks before it cancels a shutdown request.
        /// </param>
        private bool HoldCloseForFirstCloseQuestion(WindowCloseReason reason, bool dryRun = false)
        {
            if (_closeConfirmed || _teardownDone || reason == WindowCloseReason.OSShutdown) return false;
            if (IsMuxPersistenceTurnedOff) return HoldCloseToEndShellsWithPersistenceOff(reason, dryRun);
            if (!IsMuxPersistenceActive || !Ntilde.Shell.Mux.SessionPersistenceMode.IsKeepOnClose(_settings.SessionPersistence)) return false;
            bool applicationShutdown = reason == WindowCloseReason.ApplicationShutdown;
            int live = CountKeptLocalSessions();
            HashSet<Guid> pending = PendingLocalMuxSessionIds();
            if (_firstCloseQuestionOpen && !applicationShutdown) return live + pending.Count > 0; // one question at a time
            if (live + pending.Count == 0) return false;

            switch (MuxCloseChoiceStore.Read())
            {
                case Ntilde.Shell.Mux.MuxCloseChoice.Keep:
                    return false;
                case Ntilde.Shell.Mux.MuxCloseChoice.Close:
                    if (pending.Count == 0 || applicationShutdown)
                    {
                        if (dryRun) return false;
                        if (pending.Count > 0) AppLogger.Log($"[MainWindow] {pending.Count} local session(s) of tabs not shown yet left running: a shutdown cannot wait for the multiplexer to say who shows them");
                        EndLocalSessionsOnTeardown();
                        return false;
                    }

                    if (dryRun) return true;
                    _firstCloseQuestionOpen = true;
                    // Posted, as the question is: it closes the window again once the daemon has answered.
                    PostHeldClose(() => EndLocalSessionsAndCloseAsync(pending));
                    return true;
                default:
                    if (applicationShutdown) return false; // never asked: kept
                    if (dryRun) return true;
                    _firstCloseQuestionOpen = true;
                    // Posted: the answer closes the window again, and Close() must never re-enter this OnClosing.
                    PostHeldClose(() => AskFirstCloseAsync(live, pending));
                    return true;
            }
        }

        /// <summary>
        /// Release hardening item 1 (<see cref="IsMuxPersistenceTurnedOff"/>): with Off nothing is asked, and the close ends
        /// the local shells as a remembered "Close them" would - a share, or a shell another client also shows, detaches. The
        /// shells of tabs not shown yet end too once the daemon has said no other client shows them, so the close is held for
        /// that answer; a shutdown cannot be held, and ends the live ones only (<see cref="PerformAppTeardown"/> does that for
        /// every teardown, an OS shutdown's included). True holds this close.
        /// </summary>
        private bool HoldCloseToEndShellsWithPersistenceOff(WindowCloseReason reason, bool dryRun)
        {
            HashSet<Guid> pending = PendingLocalMuxSessionIds();
            if (_firstCloseQuestionOpen && reason != WindowCloseReason.ApplicationShutdown) return CountKeptLocalSessions() + pending.Count > 0;
            if (pending.Count == 0 || reason == WindowCloseReason.ApplicationShutdown)
            {
                if (dryRun) return false;
                if (pending.Count > 0) AppLogger.Log($"[MainWindow] persistence is off: {pending.Count} local session(s) of tabs not shown yet left running: a shutdown cannot wait for the multiplexer to say who shows them");
                EndLocalSessionsOnTeardown();
                return false;
            }

            if (dryRun) return true;
            _firstCloseQuestionOpen = true;
            // Posted, as a remembered "Close them" is: it closes the window again once the daemon has answered.
            PostHeldClose(() => EndLocalSessionsAndCloseAsync(pending));
            return true;
        }

        /// <summary>
        /// Asks the first-close question (R1) and settles the held close with the answer: Cancel leaves the window
        /// open; Keep closes it again, and Close ends the local shells first; either is remembered first when "Don't
        /// ask again" was ticked. A question that could not be asked closes as Keep - nothing is lost, as before R1 -
        /// rather than leave a window that cannot be closed. A close that happened meanwhile by another way (an OS
        /// shutdown, an update restart) already applied its own rules, and the answer is dropped.
        /// <para>
        /// <paramref name="live"/> is how many live shells the close would keep; <paramref name="pending"/> the ids tabs not
        /// shown yet hold (final review M3). Those count, and "Close them" ends them, only once the daemon has said it runs
        /// them and no other client shows them (<see cref="UnshownLocalMuxSessionsAsync"/>); when that leaves nothing to
        /// keep, the window closes without asking, as it does with nothing to ask about. The question can stay open for
        /// minutes, so "Close them" asks the daemon again just before it ends any of them (residual N2): one another client
        /// opened meanwhile keeps running.
        /// </para>
        /// <para>
        /// Residual N3: whatever fails on the way, the hold is let go (<see cref="_firstCloseQuestionOpen"/>), so the next
        /// close is never held behind a question that is gone.
        /// </para>
        /// </summary>
        private async Task AskFirstCloseAsync(int live, IReadOnlySet<Guid> pending)
        {
            try
            {
                await SettleFirstCloseAsync(live, pending);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[MainWindow] settling the first-close question failed; the window stays open: {ex}");
            }
            finally
            {
                _firstCloseQuestionOpen = false;
            }
        }

        /// <summary><see cref="AskFirstCloseAsync"/>'s body, the hold still on throughout.</summary>
        private async Task SettleFirstCloseAsync(int live, IReadOnlySet<Guid> pending)
        {
            // Overtaken before the post ran (an OS shutdown, an update restart): never show the question at all.
            if (_teardownDone) return;

            IReadOnlySet<Guid> unshown = pending.Count == 0 ? NoMuxSessionIds : await UnshownLocalMuxSessionsAsync(pending);
            if (_teardownDone) return;

            int count = live + unshown.Count;
            if (count == 0)
            {
                _closeConfirmed = true;
                Close();
                return;
            }

            FirstCloseAnswer answer;
            try
            {
                answer = await ConfirmFirstClose(count);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[MainWindow] the first-close question failed; keeping the shells running: {ex.Message}");
                answer = new FirstCloseAnswer(FirstCloseAction.Keep, Remember: false);
            }

            if (_teardownDone || answer.Action is not (FirstCloseAction.Keep or FirstCloseAction.Close)) return;

            bool end = answer.Action == FirstCloseAction.Close;
            if (answer.Remember) MuxCloseChoiceStore.Remember(end ? Ntilde.Shell.Mux.MuxCloseChoice.Close : Ntilde.Shell.Mux.MuxCloseChoice.Keep);
            if (end && unshown.Count > 0)
            {
                // Residual N2: a client may have opened one of them while the question was open.
                unshown = await UnshownLocalMuxSessionsAsync(unshown);
                if (_teardownDone) return;
            }

            if (end) EndLocalSessionsOnTeardown(unshown);
            _closeConfirmed = true;
            Close();
        }

        /// <summary>
        /// UI thread. Final review M3: a remembered "Close them" (R1) on a close with tabs not shown yet. Their shells end
        /// with the live ones once the daemon has said which it runs and no other client shows; then the window closes.
        /// A close that happened meanwhile by another way already applied its own rules. Whatever fails, the hold is let go
        /// (residual N3).
        /// </summary>
        private async Task EndLocalSessionsAndCloseAsync(IReadOnlySet<Guid> pending)
        {
            try
            {
                if (_teardownDone) return;
                IReadOnlySet<Guid> unshown = await UnshownLocalMuxSessionsAsync(pending);
                if (_teardownDone) return;
                EndLocalSessionsOnTeardown(unshown);
                _closeConfirmed = true;
                Close();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[MainWindow] closing with a remembered \"Close them\" failed: {ex}");
            }
            finally
            {
                _firstCloseQuestionOpen = false;
            }
        }

        private static readonly IReadOnlySet<Guid> NoMuxSessionIds = new HashSet<Guid>();

        /// <summary>
        /// The local panes whose shells a close would leave running in the daemon: what the first-close question (R1)
        /// counts and the teardown logs. Shells <see cref="EndLocalSessionsOnTeardown"/> has ended are not kept. The
        /// shells of tabs not shown yet are <see cref="PendingLocalMuxSessionIds"/>.
        /// </summary>
        private int CountKeptLocalSessions() => _paneOwnerTab.Keys.Count(p => ShowsLocalMuxEndpoint(p)
            && p.Session is Ntilde.Mux.MuxClientSession { IsConnected: true, IsProcessRunning: true } mux
            && !_localSessionsEndedOnClose.Contains(mux.Id));

        /// <summary>
        /// Final review M3: the local shells panes of this window are about to reopen - a restored tab not shown yet, a
        /// placeholder holding a pending id (<see cref="TerminalPane.MuxSessionIdToRestore"/>) - which a close leaves running
        /// as surely as a live pane's. PR #511 review (Greptile P1): also those of a startup tab not built yet, whose ids are
        /// only in its saved tree (<see cref="HeldLocalMuxSessionIds"/>). Not a share's: "Attach to session…" joined it
        /// because another client shows it, and a close never ends a share. Not one a reboot or logoff ended
        /// (<see cref="TerminalPane.MuxQuietPreviousLost"/>), one a pane shows live, or one already ended. Whether each still
        /// runs, and whether another client shows it, only the daemon can say (<see cref="UnshownLocalMuxSessionsAsync"/>).
        /// </summary>
        private HashSet<Guid> PendingLocalMuxSessionIds()
        {
            HashSet<Guid> pending = HeldLocalMuxSessionIds().Where(h => !h.Shared && !h.QuietLost).Select(h => h.Id).ToHashSet();
            pending.ExceptWith(_localSessionsEndedOnClose);
            return pending;
        }

        /// <summary>
        /// Every local daemon session this window names but no pane shows live: a pane's pending id
        /// (<see cref="TerminalPane.MuxSessionIdToRestore"/>), and each id in the saved tree of a startup tab not built yet -
        /// a placeholder (CreateStartupPlaceholderTab), which has no pane at all until the restore's background pass builds
        /// it, and whose tree the session save writes back as it was. Each with whether it is a share and whether a reboot or
        /// logoff already ended it (spec R2), as the pane would be told. An id a pane shows live is left out.
        /// </summary>
        private List<(Guid Id, bool Shared, bool QuietLost)> HeldLocalMuxSessionIds()
        {
            var live = new HashSet<Guid>();
            var held = new List<(Guid Id, bool Shared, bool QuietLost)>();
            foreach (TerminalPane pane in _paneOwnerTab.Keys)
            {
                if (!ShowsLocalMuxEndpoint(pane)) continue;
                if (pane.Session is Ntilde.Mux.MuxClientSession mux) live.Add(mux.Id);
                else if (pane.MuxSessionIdToRestore is Guid id) held.Add((id, pane.MuxAttachSharedToRestore, pane.MuxQuietPreviousLost));
            }

            if (this.FindControl<TabControl>("Tabs") is { } tabs)
            {
                foreach (TabItem tab in tabs.Items.OfType<TabItem>())
                {
                    // A placeholder as the save tells one: no pane tree to capture, and the restored tree in its Tag.
                    if (GetLayoutRootForTab(tab) is TerminalPane or Grid || tab.Tag is not TabSession { Root: { } root }) continue;
                    var nodes = new Stack<PaneNode>([root]);
                    while (nodes.TryPop(out PaneNode? node))
                    {
                        // A hand-edited or merged file can hold a null child, as DedupeMuxIds and WithoutMuxIds allow for.
                        if (node is null) continue;
                        foreach (PaneNode child in node.Children) nodes.Push(child);
                        if (Guid.TryParse(node.MuxSessionId, out Guid id) && Ntilde.Shell.Mux.MuxEndpointId.Parse(node.MuxEndpoint).IsLocal)
                        {
                            held.Add((id, node.MuxShared, node.MuxQuietPreviousLost));
                        }
                    }
                }
            }

            held.RemoveAll(h => live.Contains(h.Id));
            return held;
        }

        /// <summary>How long a closing window waits for the local daemon to list its shells (final review M3).</summary>
        internal static readonly TimeSpan PendingShellsListTimeout = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Test seam: how a closing window lists the local daemon's shells - for the first-close question
        /// (<see cref="UnshownLocalMuxSessionsAsync"/>) and before "Quit and close all shells" (<see cref="QuitAndCloseAllShellsAsync"/>).
        /// Called off the UI thread.
        /// </summary>
        internal Func<Ntilde.Mux.MuxClient, System.Threading.CancellationToken, Task<IReadOnlyList<Ntilde.Mux.Contracts.SessionSummary>>> MuxListSessionsForClose { get; set; } =
            static (client, ct) => client.ListSessionsAsync(ct);

        /// <summary>
        /// Final review M3: of <paramref name="pending"/> (<see cref="PendingLocalMuxSessionIds"/>), the shells the local
        /// daemon runs and no interactive client shows - what "Close them" may end, as it ends a live pane's shell only
        /// when no other client shows it. Asked off the UI thread over the local host's current connection only - a
        /// closing window never connects, let alone starts a daemon - within <see cref="PendingShellsListTimeout"/>. None
        /// when the daemon cannot say: those shells keep running, as with Keep, and stay named for the next launch.
        /// </summary>
        private async Task<IReadOnlySet<Guid>> UnshownLocalMuxSessionsAsync(IReadOnlySet<Guid> pending)
        {
            if (_muxHosts?.Local.CurrentClient is not { IsConnected: true } client) return NoMuxSessionIds;
            Func<Ntilde.Mux.MuxClient, System.Threading.CancellationToken, Task<IReadOnlyList<Ntilde.Mux.Contracts.SessionSummary>>> list = MuxListSessionsForClose;
            try
            {
                return await Task.Run(async () =>
                {
                    using var cts = new System.Threading.CancellationTokenSource(PendingShellsListTimeout);
                    IReadOnlyList<Ntilde.Mux.Contracts.SessionSummary> all = await list(client, cts.Token).ConfigureAwait(false);
                    bool v2 = client.ProtocolVersion >= Ntilde.Mux.Contracts.MuxProtocol.SessionEventsVersion;
                    return (IReadOnlySet<Guid>)all
                        .Where(s => pending.Contains(s.SessionId) && Ntilde.Shell.Mux.MuxOrphans.IsUnshown(s, v2))
                        .Select(s => s.SessionId)
                        .ToHashSet();
                });
            }
            catch (Exception ex)
            {
                // Any failure (residual N3): none is the safe answer - those shells keep running, as with Keep.
                AppLogger.Log($"[MainWindow] listing the shells of tabs not shown yet failed; they keep running: {ex.GetType().Name}: {ex.Message}");
                return NoMuxSessionIds;
            }
        }

        /// <summary>
        /// Ends every local pane's live shell as the window closes: R1's "Close them" (asked or remembered), and Task
        /// 17's quit. Each kill goes to the local host (<see cref="Ntilde.Shell.Mux.MuxConnectionHost.KillWhenConnected"/>),
        /// sent at once and tracked, and the teardown's <see cref="Ntilde.Shell.Mux.MuxConnectionHosts.Dispose"/> waits
        /// for the replies before it disconnects, so none is dropped behind the close. The ended shells are not saved for
        /// reattach either: the next launch starts fresh shells quietly, as with persistence off, rather than report
        /// them lost. Remote shells are left alone, and so is a shell another client is also typing into, or a share whose
        /// other clients cannot be known yet (Phase 5 Task 26): a pane close never ends one without asking (the shared-close
        /// question), so it detaches, as Keep does, and stays counted as kept. Idempotent.
        /// </summary>
        /// <param name="pending">
        /// Final review M3: shells of tabs not shown yet that the daemon said it runs and no other client shows
        /// (<see cref="UnshownLocalMuxSessionsAsync"/>), ended the same way - each only while a pane, or a startup tab not
        /// built yet, still holds it pending (one that has spawned since is a live pane's, and judged as one).
        /// </param>
        private void EndLocalSessionsOnTeardown(IReadOnlySet<Guid>? pending = null)
        {
            if (_muxHosts is not { } hosts) return;
            int ended = 0;
            int shared = 0;
            foreach (TerminalPane pane in _paneOwnerTab.Keys)
            {
                if (!ShowsLocalMuxEndpoint(pane)
                    || pane.Session is not Ntilde.Mux.MuxClientSession { IsConnected: true, IsProcessRunning: true } mux
                    || _localSessionsEndedOnClose.Contains(mux.Id))
                {
                    continue;
                }

                // The session's cached count (read-only observers excluded) or the pane's, whichever knows of another client -
                // or a share none of them can speak for yet (its attach in flight: Phase 5 Task 26).
                if (mux.InteractiveOthers > 0 || pane.MuxOtherClients > 0 || pane.IsMuxShareWithSharingUnknown)
                {
                    shared++;
                    continue;
                }

                _localSessionsEndedOnClose.Add(mux.Id);
                hosts.Local.KillWhenConnected(mux.Id);
                ended++;
            }

            if (pending is { Count: > 0 })
            {
                foreach (Guid id in PendingLocalMuxSessionIds())
                {
                    if (!pending.Contains(id)) continue;
                    _localSessionsEndedOnClose.Add(id);
                    hosts.Local.KillWhenConnected(id);
                    ended++;
                }
            }

            if (ended > 0) AppLogger.Log($"[MainWindow] ending {ended} local session(s) as the window closes");
            if (shared > 0) AppLogger.Log($"[MainWindow] {shared} shared local session(s) left running for the other clients that show them");
        }

        private bool _quitAndCloseAllInProgress;

        /// <summary>
        /// "Quit and close all shells" (Task 17): ends EVERY shell in the local daemon - this window's panes, shells
        /// shared with other clients, and detached ones - shuts the daemon down, and closes the window. Remote shells
        /// are untouched. Asks first, naming the daemon's running count. The ended shells are not saved for reattach,
        /// so the next launch starts fresh ones quietly - those of tabs not shown yet only once they are known gone (the
        /// stop went through, or each was killed by name). A daemon that cannot be reached or does not stop is logged and
        /// the window closes anyway.
        /// </summary>
        internal async Task QuitAndCloseAllShellsAsync()
        {
            if (_quitAndCloseAllInProgress || _teardownDone || !IsMuxPersistenceActive || _muxHosts?.Local is not { } host) return;
            _quitAndCloseAllInProgress = true;
            try
            {
                // Off the UI thread: GetClient blocks while a connect is in flight.
                Func<Ntilde.Mux.MuxClient, System.Threading.CancellationToken, Task<IReadOnlyList<Ntilde.Mux.Contracts.SessionSummary>>> list = MuxListSessionsForClose;
                IReadOnlyList<Guid>? running = await Task.Run(async () =>
                {
                    Ntilde.Mux.MuxClient? client = host.GetClient(TimeSpan.FromSeconds(3));
                    if (client is null) return null;
                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try
                    {
                        IReadOnlyList<Ntilde.Mux.Contracts.SessionSummary> all = await list(client, cts.Token).ConfigureAwait(false);
                        return (IReadOnlyList<Guid>)all.Where(s => s.Running).Select(s => s.SessionId).ToList();
                    }
                    catch (Exception ex) when (ex is Ntilde.Mux.Contracts.MuxProtocolException or IOException or TimeoutException or OperationCanceledException or ObjectDisposedException)
                    {
                        AppLogger.Log($"[MainWindow] listing mux sessions before quit failed: {ex.Message}");
                        return null;
                    }
                });

                if (running is null || running.Count > 0)
                {
                    bool confirmed;
                    try
                    {
                        confirmed = await ConfirmQuitAndCloseAll(running?.Count ?? -1);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Log($"[MainWindow] the quit confirmation failed; not quitting: {ex.Message}");
                        return;
                    }

                    if (!confirmed) return;
                }

                // The panes' shells go through the host (tracked, flushed at teardown); then every other shell the daemon
                // runs - shared and detached ones - is killed on this connection, and the daemon is shut down.
                EndLocalSessionsOnTeardown();
                var others = running?.Where(id => !_localSessionsEndedOnClose.Contains(id)).ToList() ?? [];
                MarkLivePaneShellsEnded();

                HashSet<Guid> killed = await Task.Run(async () =>
                {
                    var done = new HashSet<Guid>();
                    if (others.Count > 0 && host.GetClient(TimeSpan.FromSeconds(3)) is { } client)
                    {
                        foreach (Guid id in others)
                        {
                            try
                            {
                                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));
                                await client.KillAsync(id, cts.Token).ConfigureAwait(false);
                                done.Add(id);
                            }
                            catch (Exception ex)
                            {
                                AppLogger.Log($"[MainWindow] killing session {id} before quit failed: {ex.Message}");
                            }
                        }
                    }

                    return done;
                });

                LocalDaemonStop stop = await ShutdownLocalDaemonAsync(TimeSpan.FromSeconds(5), "quitting");

                // PR #511 residual M1: the shells of tabs not shown yet are left out of the session only once they are known
                // to be gone - the daemon stopped (or was sent shutdown with no process to watch), or each was killed by name.
                // Otherwise they may still run, and stay named, so the next launch reopens them in their tabs. Marked before
                // Close(): its OnClosing runs the teardown, whose save is the one that reads the marks.
                MarkHeldLocalShellsEnded(stop is LocalDaemonStop.Gone or LocalDaemonStop.StopUnconfirmed ? null : killed);

                _closeConfirmed = true;
                Close();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[MainWindow] quit and close all shells failed: {ex}");
            }
            finally
            {
                _quitAndCloseAllInProgress = false;
            }
        }

        /// <summary>The production "Quit and close all shells" question; <paramref name="count"/> is -1 when unknown.</summary>
        private Task<bool> ShowQuitAndCloseAllDialogAsync(int count)
        {
            string what = count switch
            {
                < 0 => "All shells running in the background will be closed, including detached and shared ones.",
                1 => "1 shell running in the background will be closed, including detached and shared ones.",
                _ => $"{count} shells running in the background will be closed, including detached and shared ones.",
            };
            bool remote = _paneOwnerTab.Keys.Any(p => !ShowsLocalMuxEndpoint(p) && p.Session is Ntilde.Mux.MuxClientSession);
            if (remote) what += " Remote shells keep running.";
            return ShowConfirmationDialogAsync("Quit Ntilde", "Close every shell?", what, "Quit and close", 140);
        }

        /// <summary>
        /// The teardown a normal close performs: save the session, stop the toast timers, and
        /// dispose/stop what OnOpened set up. Extracted so <see cref="ApplyStagedUpdate"/> can run
        /// it too - an update restart terminates the process directly and never reaches
        /// <see cref="OnClosing"/>, so without calling this first, taking the update would
        /// silently skip all of it.
        /// </summary>
        private void PerformAppTeardown()
        {
            // Idempotent: an update restart runs this and the window's own close can follow it.
            // A second pass would re-save a session whose connection is already gone.
            if (_teardownDone) return;
            _teardownDone = true;
            TeardownFaultForTest?.Invoke();

            // Release hardening item 1: every way to tear down - an OS shutdown, an update restart - ends the live local shells
            // when persistence was turned off, before the save, so none is named for a reattach Off never makes.
            if (IsMuxPersistenceTurnedOff) EndLocalSessionsOnTeardown();
            if (!_sessionSavedBeforeUpdate) SaveSessionWithoutEndedShells();

            if (_muxHosts is { } muxHosts)
            {
                // The agent host stops asking these hosts before they close (the Stop below clears it again).
                AgentHost.AgentHostService.Instance.SetWindowlessSource(null);
                // Local panes only: the log line points at `ntilde mux ls`, which lists the local daemon.
                int kept = CountKeptLocalSessions();
                // Explicit detach: closing each connection makes its daemon drop this client's
                // subscriptions and keep every shell running. Panes are deliberately not disposed
                // (that would kill them). Each host flushes what is already queued first, so a kill
                // from a pane closed just before (the last tab) still reaches its daemon. The remote
                // hosts go first, together; the local one last (Phase 4 spec §5).
                muxHosts.Dispose();
                if (kept > 0) AppLogger.Log($"[MainWindow] {kept} session(s) kept running; `ntilde mux ls` lists them");
            }
            _globalHotkey?.Dispose();
            // Dispose the snapshot scheduler before the agent-host teardown below: its Dispose
            // performs a best-effort final flush, which writes a snapshot, which logs — and the
            // agent-host stop path below is not something a snapshot write should race.
            _snapshotScheduler?.Dispose();
            _snapshotScheduler = null;
            ReleaseWindowHooks();
            AgentHost.AgentHostService.Instance.Stop();
        }

        /// <summary>
        /// Lets go of what outside this window holds it: its own timers, which the dispatcher roots while they run, and its
        /// handlers on process-wide events. Nothing more - no save, no shell ended, no service stopped - so the test teardown
        /// can use it before a close that skips the app's teardown (#543). Idempotent. UI thread.
        /// </summary>
        private void ReleaseWindowHooks()
        {
            _recordingToastTimer.Stop();
            // Armed by OnOpened for one tick 10 s later; a window torn down sooner would leave it armed.
            _updateCheckTimer.Stop();
            _tabStatusTimer?.Stop();
            // A mid-drag close can beat the release/capture-lost cleanup, and a live
            // DispatcherTimer would keep ticking (and rooting) the closed window.
            _tabDragAutoScrollTimer?.Stop();
            AgentHost.AgentHostService.Instance.ObserveActivityChanged -= OnAgentObserveActivityChanged;
            AgentHost.AgentSessionRegistry.Instance.SessionRegistered -= OnAgentSessionRegisteredForAttention;
            AgentHost.AgentSessionRegistry.Instance.SessionUnregistered -= OnAgentSessionUnregisteredForAttention;
            foreach (var registration in AgentHost.AgentSessionRegistry.Instance.GetRegistrations())
            {
                registration.AttentionMachine.Changed -= OnAgentAttentionChangedForTabs;
            }
        }
    }
}
