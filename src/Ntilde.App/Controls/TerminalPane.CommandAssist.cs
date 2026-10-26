using Ntilde.Shell;
using Avalonia.Styling;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Ntilde.VT;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Ntilde.CommandAssist.Application;
using Ntilde.CommandAssist.Domain;
using Ntilde.CommandAssist.Models;
using Ntilde.CommandAssist.ViewModels;

namespace Ntilde.Controls
{
    /// <summary>
    /// Command Assist in a pane: building and binding its view models, routing keys to it, inserting a
    /// suggestion, and feeding it each command's context and outcome.
    /// </summary>
    public partial class TerminalPane
    {
        private void InitializeCommandAssist()
        {
            if (!IsCommandAssistFeatureEnabled())
            {
                _commandAssistController?.Dismiss();
                ClearCommandAssistBindings();

                return;
            }

            if (_commandAssistController != null)
            {
                BindCommandAssistViews(_commandAssistController.ViewModel);

                ApplyCommandAssistFeaturePolicy(_commandAssistController);
                _commandAssistController.HandleAltScreenChanged(Buffer?.IsAltScreenActive ?? false);
                UpdateCommandAssistContext();
                return;
            }

            TerminalSettings settings = _settings!;
            CommandAssistServices services = RequireCommandAssistServices();
            services.ApplyHistoryRetentionLimit(settings.CommandAssistMaxHistoryEntries);

            // This pane's own dispatcher, not Dispatcher.UIThread, and not read inside the
            // dispatch delegate below either. Both halves of that matter (#81).
            //
            // Dispatcher.UIThread is a mutable static whose getter binds the UI-thread identity
            // to *the calling thread* whenever the backing field is null (Dispatcher.cs, the
            // `s_uiThread ?? CurrentDispatcher` slow path). Under headless test isolation that
            // field is nulled by ResetGlobalState() at every test boundary, so *any* read of it
            // off the dispatch thread can decide who the UI thread is - and both directions of
            // that have now been observed. A suggestion pass still in flight from a finished
            // test reached this delegate in the gap between two tests and made a threadpool
            // thread the UI thread, after which the next test's Compositor ctor ->
            // DefaultRenderLoop.Add -> VerifyAccess() threw inside EnsureIsolatedApplication(),
            // which sits outside the try/catch in HeadlessUnitTestSession.DispatchCore, killing
            // the one dispatcher loop the assembly shares. And reading the static *eagerly*
            // here is no safer: it leaves s_uiThread pointing at whichever thread built the
            // pane, and the plain-[Fact] tests that marshal onto Dispatcher.UIThread rely on
            // finding it null so they can be their own UI thread - see the note in
            // TestAppBuilder.cs, and SshInteractionServiceTests, which hangs forever otherwise.
            //
            // AvaloniaObject.Dispatcher costs nothing on either count. It is captured by every
            // AvaloniaObject at construction (`= Dispatcher.CurrentDispatcher`), so the pane
            // already holds it before this line runs: reading it binds nothing that `new
            // TerminalPane()` had not already bound. A late pass then posts to the dispatcher
            // the pane was born with - inert once that test is over - and no read of the global
            // happens at all. It is also the more correct reading in production, where a pane
            // belongs to one dispatcher for its whole life.
            Dispatcher paneDispatcher = Dispatcher;
            _commandAssistController = new CommandAssistController(
                services.HistoryStore,
                services.SecretsFilter,
                services.SuggestionEngine,
                services.SnippetStore,
                services.CommandDocsProvider,
                services.RecipeProvider,
                services.ErrorInsightService,
                modeRouter: null,
                resultBuilder: null,
                // The grid-truth seam. Command Assist may not reference Ntilde.VT (see
                // ProjectFileLayeringTests), so the reader's GridCommandLine is mapped to the
                // assist assembly's own AssistQuerySnapshot right here, at the one boundary that
                // can see both types. Everything downstream sees plain data.
                queryProvider: TryReadAssistQuerySnapshot,

                // The lifecycle half of the same pair, and it has to come from the same place the
                // mark does or the two can disagree - which is exactly what #448 cost. The buffer
                // owns both and the parser publishes both in one step, so this controller reads the
                // window rather than keeping its own opinion of it.
                //
                // Combined with the consumption switch, which is the third path that needs it
                // (Codex P2 on #448). The parser writes the window for every session that emits
                // marks, tracker or no tracker, so without this an instrumented remote host would
                // hand grid-backed queries and Enter capture to a user who had turned shell
                // integration off - and a remote host is exactly where they cannot uninstall the
                // emitter instead.
                commandInputGateProbe: () =>
                    IsShellIntegrationConsumptionEnabled && (Buffer?.IsAcceptingCommandInput ?? false),

                // The other seam the controller cannot see for itself: whether the overlay it believes
                // is up is actually on screen. This pane hides it (no layout) and dims it (placement
                // correction) on its own authority, and an armed Enter on a zero-pixel surface is the
                // PR #290 review's first blocker.
                renderedSurfaceProbe: () => IsCommandAssistOverlayRendered,
                dispatch: action =>
                {
                    if (paneDispatcher.CheckAccess())
                    {
                        action();
                    }
                    else
                    {
                        paneDispatcher.Post(action);
                    }
                });

            BindCommandAssistViews(_commandAssistController.ViewModel);

            ApplyCommandAssistFeaturePolicy(_commandAssistController);
            _commandAssistController.HandleAltScreenChanged(Buffer?.IsAltScreenActive ?? false);
            UpdateCommandAssistContext();
        }

        /// <summary>
        /// Pushes the two Command Assist sub-settings and the resolved in-surface keyboard into the
        /// controller.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Called from both <see cref="InitializeCommandAssist"/> paths, so a settings change reaches an
        /// already-live controller as well as a fresh one - <c>ApplySettings</c> routes through the same
        /// method. Everything here is a re-application of current state rather than an event, so calling
        /// it more often than necessary is free.
        /// </para>
        /// <para>
        /// The bindings are cached in <see cref="_commandAssistKeyBindings"/> because the key router
        /// consults them on every keystroke and re-resolving would mean re-parsing chord strings in the
        /// hottest path in the feature.
        /// </para>
        /// </remarks>
        private void ApplyCommandAssistFeaturePolicy(CommandAssistController controller)
        {
            TerminalSettings? settings = _settings;

            controller.SetFeaturePolicy(
                isHistoryEnabled: settings?.CommandAssistHistoryEnabled ?? true,
                isPassiveBubbleEnabled: settings?.CommandAssistPassiveBubbleEnabled ?? true);

            AssistShortcutBindings bindings = AssistShortcutBindingResolver.Resolve(settings?.Keybindings);
            _commandAssistKeyBindings = bindings.Keys;
            controller.SetShortcutHintLabels(bindings.HintLabels);
        }

        /// <summary>
        /// Returns the injected Command Assist graph, or throws describing who was supposed to
        /// supply it.
        /// </summary>
        private CommandAssistServices RequireCommandAssistServices()
        {
            return _commandAssistServices ?? throw new InvalidOperationException(
                "TerminalPane.CommandAssistServices was not assigned before Command Assist " +
                "initialized. MainWindow.WirePane injects the instance built by AppServices at the " +
                "App composition root; a pane created outside that path must set it explicitly.");
        }

        /// <summary>
        /// Hands the bar view-model's two child view-models to the two overlay views.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Why the UI-thread hop.</strong> Everything below writes Avalonia properties -
        /// <c>DataContext</c> on the two views, and <c>Width</c>/<c>Height</c>/<c>Margin</c> through
        /// <see cref="UpdateCommandAssistOverlayPlacement"/> - and Avalonia property writes are
        /// UI-thread-only. This method is reachable off it: Command Assist is initialized lazily on
        /// first use (see <see cref="ApplySettings"/>, which deliberately does *not* construct the
        /// controller), and in a real session the first use is not a keystroke but the shell-integration
        /// event pump. <c>OSC 133;B</c> arrives on the PTY reader thread, goes onto
        /// <c>_shellIntegrationEventDispatcher</c>, and reaches
        /// <see cref="EnsureCommandAssistInitialized"/> from there - before the user has touched the
        /// keyboard.
        /// </para>
        /// <para>
        /// Without the hop the first <c>DataContext</c> write throws "The calling thread cannot access
        /// this object because a different thread owns it", the throw is swallowed by the
        /// fire-and-forget dispatch in <c>OnShellIntegrationEventObserved</c>, and - because
        /// <see cref="_boundCommandAssistViewModel"/> is already assigned by then - the pane goes on
        /// believing it is bound. Key routing, visibility and sizing all keep working, so the overlay
        /// appears, correctly sized, with a null <c>DataContext</c> on both views. Both views use
        /// <c>x:CompileBindings</c>, where a null data root is a silent no-value rather than a logged
        /// binding error, so the surfaces render their chrome and not one character of content. That is
        /// the post-3a "empty dark rectangles" regression, and it is why it was invisible to tests that
        /// drive the pane from the UI thread: with the dispatcher's semaphore uncontended the await
        /// completes synchronously and the bind never leaves the caller's thread.
        /// </para>
        /// </remarks>
        private void BindCommandAssistViews(CommandAssistBarViewModel? viewModel)
        {
            if (!this.Dispatcher.CheckAccess())
            {
                this.Dispatcher.Post(() => BindCommandAssistViews(viewModel));
                return;
            }

            if (!ReferenceEquals(_boundCommandAssistViewModel, viewModel))
            {
                if (_boundCommandAssistViewModel != null)
                {
                    _boundCommandAssistViewModel.PropertyChanged -= OnCommandAssistViewModelPropertyChanged;
                }

                _boundCommandAssistViewModel = viewModel;

                if (_boundCommandAssistViewModel != null)
                {
                    _boundCommandAssistViewModel.PropertyChanged += OnCommandAssistViewModelPropertyChanged;
                }
            }

            if (CommandAssistBubble != null)
            {
                CommandAssistBubble.DataContext = viewModel?.Bubble;
            }

            if (CommandAssistPopup != null)
            {
                CommandAssistPopup.DataContext = viewModel?.Popup;
            }

            UpdateCommandAssistOverlayPlacement();
        }

        /// <summary>
        /// Points the two overlay views at permanently-hidden view-models when the feature is off.
        /// </summary>
        /// <remarks>
        /// Marshalled for the same reason as <see cref="BindCommandAssistViews"/>: it writes
        /// <c>DataContext</c>, and <see cref="InitializeCommandAssist"/> - its only caller - runs
        /// wherever the first Command Assist entry point happened to be.
        /// </remarks>
        private void ClearCommandAssistBindings()
        {
            if (!this.Dispatcher.CheckAccess())
            {
                this.Dispatcher.Post(ClearCommandAssistBindings);
                return;
            }

            if (_boundCommandAssistViewModel != null)
            {
                _boundCommandAssistViewModel.PropertyChanged -= OnCommandAssistViewModelPropertyChanged;
                _boundCommandAssistViewModel = null;
            }

            if (CommandAssistBubble != null)
            {
                CommandAssistBubble.DataContext = _hiddenCommandAssistBubbleViewModel;
            }

            if (CommandAssistPopup != null)
            {
                CommandAssistPopup.DataContext = _hiddenCommandAssistPopupViewModel;
            }
        }

        /// <summary>
        /// Whether Command Assist runs in this pane at all.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>The master flag alone, since V2 Phase 3b (task 3).</strong> This used to require
        /// <c>CommandAssistHistoryEnabled</c> as well, which made a privacy preference into a second
        /// master switch: a user who turned command capture off lost the bubble, the popup, path
        /// suggestions, Help and Fix - none of which read the history file. The history flag now gates
        /// exactly capture and history-sourced suggestions, inside the assist assembly
        /// (<c>AssistSessionContext.IsHistoryEnabled</c>), where both consumers live.
        /// </para>
        /// </remarks>
        private bool IsCommandAssistFeatureEnabled()
        {
            return _settings?.CommandAssistEnabled == true;
        }

        // When this returns true the controller is guaranteed non-null; the attribute lets
        // the compiler's null-flow analysis see that, so callers can dereference
        // _commandAssistController directly after the guard without CS8602.
        [MemberNotNullWhen(true, nameof(_commandAssistController))]
        private bool EnsureCommandAssistInitialized()
        {
            if (!IsCommandAssistFeatureEnabled())
            {
                return false;
            }

            if (_commandAssistController == null)
            {
                InitializeCommandAssist();
            }

            return _commandAssistController != null;
        }

        private void OnCommandAssistViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            UpdateCommandAssistOverlayPlacement();
        }

        private void UpdateCommandAssistContext()
        {
            _commandAssistController?.UpdateSessionContext(
                shellKind: DetermineShellKind(Session?.ShellCommand ?? ShellCommand),
                workingDirectory: CurrentWorkingDirectory,
                profileId: Profile?.Id.ToString(),
                sessionId: Session?.Id.ToString(),
                hostId: Profile?.Type == ConnectionType.SSH ? Profile.SshHost : null,
                isRemote: Profile?.Type == ConnectionType.SSH,

                // Two ways to be integrated, and remote sessions can only ever be the second one.
                // "We injected a bootstrap" is unreachable over SSH; "the shell is emitting marks"
                // is what the V2 Phase 2b snippets buy, and it is equally good evidence - the parser
                // has never cared who installed the thing writing OSC 133.
                isShellIntegrated: _isShellIntegrationActive || _hasObservedShellIntegrationMark);
        }

        private void UpdatePaneContextMenuState()
        {
            UpdateRemoteFilesSidebarEntryPointState();

            if (RootGrid.ContextMenu?.Items is not IEnumerable<object> items)
            {
                return;
            }

            MenuItem? explainSelectionItem = items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "MenuExplainSelection");
            if (explainSelectionItem != null)
            {
                bool canExplain = CanExplainSelection();
                explainSelectionItem.IsEnabled = canExplain;
                explainSelectionItem.IsVisible = canExplain;
            }
        }

        /// <summary>
        /// <c>TerminalView.KeyDownInterceptor</c>. Routes the key to Command Assist if it owns it,
        /// and observes every key either way for the markless submission accumulator.
        /// </summary>
        /// <remarks>
        /// The observation is strictly a side effect: this returns exactly what
        /// <see cref="TryRouteCommandAssistKey"/> returns, so input routing is unchanged. It has to
        /// run <em>after</em> the routing decision, because "Command Assist consumed this key" is
        /// the same fact as "the shell never saw it".
        /// </remarks>
        internal bool TryHandleCommandAssistKey(Key key, KeyModifiers modifiers)
        {
            bool handledByAssist = TryRouteCommandAssistKey(key, modifiers);
            _marklessSubmission.ApplyKey(key, modifiers, handledByAssist);
            return handledByAssist;
        }

        private bool TryRouteCommandAssistKey(Key key, KeyModifiers modifiers)
        {
            if (!IsCommandAssistFeatureEnabled())
            {
                return false;
            }

            CommandAssistController? controller = _commandAssistController;
            // Every fact here is asked of the controller rather than computed locally, including the two
            // that depend on this pane: IsAcceptOnEnterArmed folds in IsCommandAssistOverlayRendered
            // through the probe installed in InitializeCommandAssist, so the router, the hint strip and
            // this pane cannot hold three different opinions about who owns Enter.
            var keyState = new AssistKeyState(
                IsSurfaceVisible: controller?.ViewModel.IsVisible == true,
                IsAcceptOnEnterArmed: controller?.IsAcceptOnEnterArmed == true,
                IsSelectionUpOwned: controller?.IsSelectionUpOwned == true);

            // One resolution, one switch. Phase 3a asked the router "is this ours?" and then repeated
            // the whole key cascade here to find out which of ours it was; with the chords rebindable
            // (V2 Phase 3b) that repetition would be a second binding table, and the two would drift the
            // first time someone changed one of them.
            AssistKeyAction action = CommandAssistKeyRouter.Resolve(
                keyState,
                AssistKeyMapper.ToAssistKey(key),
                AssistKeyMapper.ToAssistModifiers(modifiers),
                _commandAssistKeyBindings);

            switch (action)
            {
                case AssistKeyAction.Dismiss:
                    controller?.HandleEscape();
                    return true;

                // Accept (V2 Phase 3a). The router only says yes here while the popup is open with a row
                // selected *and the overlay is rendered*, so neither the typing flow nor a surface this
                // pane has hidden or dimmed reaches this branch.
                //
                // The return value is the insertion's, not `true`, and that is the important part: when
                // insertion refuses - a poisoned markless line, an unechoed keystroke, a cursor mid-line -
                // the key falls through to the shell and submits, exactly as it did before this branch
                // existed. Consuming it instead would turn a refusal into a dead key, which is a strictly
                // worse answer than the pre-Phase-3a behavior the user is used to.
                case AssistKeyAction.Accept:
                    return TryInsertSelectedCommandAssistSuggestion();

                case AssistKeyAction.SelectionDown:
                    controller?.MoveSelectionDown();
                    return true;

                // Only reached when the router granted Up to the assist - an open popup, or a surface the
                // user summoned. In the passive states Up is never routed here at all, so the shell keeps
                // its history recall (PR #290 review).
                case AssistKeyAction.SelectionUp:
                    controller?.MoveSelectionUp();
                    return true;

                // Unlike Accept this consumes the key whether the insertion happened or not: the insert
                // chord has no shell meaning to fall through to.
                case AssistKeyAction.Insert:
                    TryInsertSelectedCommandAssistSuggestion();
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// The catalogued pin/unpin shortcut fired. Toggles the selected row's snippet pin.
        /// </summary>
        /// <remarks>
        /// <para>
        /// No longer a call into <see cref="TryRouteCommandAssistKey"/>. Until V2 Phase 3b the pin chord
        /// was <c>Ctrl+Shift+P</c> - the command palette's - and it was routed as an assist key so that
        /// <c>MainWindow</c> could try the pin, watch it decline, and open the palette instead. That made
        /// "does Ctrl+Shift+P open the command palette" depend on whether an assist row happened to be
        /// selected. Pin has its own catalogue entry now (<c>command_assist_pin</c>), so it arrives here
        /// as itself and the palette owns its chord unconditionally.
        /// </para>
        /// <para>
        /// Routing it from the window rather than through <c>AssistKey</c> also means it can be rebound to
        /// any chord at all, instead of the five keys the assist assembly's key vocabulary models.
        /// </para>
        /// <para>
        /// Returns false rather than consuming the key when there is nothing to pin, so an unusable
        /// shortcut is not a dead key. Nothing was sent to the shell either way: this arrives from the
        /// window's handler on a chord <c>TerminalView</c> left unhandled.
        /// </para>
        /// </remarks>
        public bool TryToggleCommandAssistPinShortcut()
        {
            if (!IsCommandAssistFeatureEnabled())
            {
                return false;
            }

            CommandAssistController? controller = _commandAssistController;
            if (controller == null ||
                !controller.ViewModel.IsVisible ||
                !controller.CanTogglePinSelection())
            {
                return false;
            }

            _ = controller.TogglePinSelectionAsync();
            return true;
        }

        /// <summary>
        /// The user pressed a key that edits the command line, and the PTY has been sent the bytes
        /// for it. Until the shell echoes them back and the parser paints them, the grid is a
        /// prefix of the real command line.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the one desync the grid cannot self-report. Every other stale read looks wrong -
        /// a half-erased line, a mark that went dark - but an unechoed keystroke leaves a read that
        /// is internally perfect: <c>"git st"</c> with the cursor at offset 6, every planner guard
        /// satisfied, while the shell already holds <c>"git sta"</c>. Because the stale text is
        /// always a strict <em>prefix</em> of the true line, no prefix check can detect it: the
        /// planner would compute <c>"atus"</c> against <c>"git st"</c> and the line would become
        /// <c>"git staatus"</c>.
        /// </para>
        /// <para>
        /// So insertion refuses while this is set, consistent with the planner's other rules -
        /// refusal on doubt, never a guess. Ranking is deliberately left alone: a one-character-
        /// stale query ranks slightly worse rows and the next trigger fixes it, which is a cost
        /// worth paying to keep suggestions live while typing.
        /// </para>
        /// <para>
        /// The clear is approximate in one direction only. Any session output clears the flag, so
        /// unrelated output (a background job printing) can clear it before the echo lands, leaving
        /// the original window open. It cannot go the other way: output that has been parsed is
        /// output that is in the grid.
        /// </para>
        /// <para>
        /// <strong>That window is wider than it was, and is a known follow-up.</strong> Under the
        /// additive rule a read taken inside it produced <c>git staatus</c> - a wrong append. Under
        /// <see cref="CommandAssistInsertionStyle.ReplaceTypedPrefix"/> it produces a wrong
        /// <em>count</em>, so the deletes stop one character short and the command lands against the
        /// survivor. Same pre-existing hole, larger blast radius; closing it needs a signal that
        /// distinguishes "the bytes I sent came back" from "some output arrived", which the parser
        /// does not offer today.
        /// </para>
        /// </remarks>
        internal void NoteInputAwaitingEcho() => _hasUnechoedInput = true;

        /// <summary>Session bytes have been parsed into the grid, so the grid has caught up.</summary>
        internal void NoteSessionOutputApplied() => _hasUnechoedInput = false;

        /// <summary>Whether an edit keystroke is still waiting for the shell's echo.</summary>
        internal bool HasUnechoedInput => _hasUnechoedInput;

        /// <summary>
        /// A popup row was clicked once: select it, exactly as <c>Up</c>/<c>Down</c> would.
        /// </summary>
        /// <remarks>
        /// Selecting rather than accepting is deliberate. A single click that ran an insertion would
        /// make the list unbrowsable by mouse — there would be no way to look at a row's detail panel
        /// without committing to it — and it would put a destructive action one stray click away on a
        /// surface that overlays the terminal. Accept needs a second, deliberate act: a double click, or
        /// a click on the row that is already selected.
        /// </remarks>
        internal void OnCommandAssistSuggestionPointerSelected(int index)
        {
            _commandAssistController?.TrySelectSuggestionAt(index);
        }

        /// <summary>A popup row was double-clicked, or clicked while already selected: accept it.</summary>
        internal void OnCommandAssistSuggestionPointerAccepted(int index)
        {
            CommandAssistController? controller = _commandAssistController;
            if (controller == null)
            {
                return;
            }

            // Select first even on the accept path: a double click on an unselected row must insert the
            // row that was clicked, not whatever the keyboard had highlighted.
            if (!controller.TrySelectSuggestionAt(index))
            {
                return;
            }

            TryInsertSelectedCommandAssistSuggestion();
        }

        private bool TryInsertSelectedCommandAssistSuggestion()
        {
            if (_commandAssistController == null || Session == null)
            {
                return false;
            }

            // The echo race: a fresh read taken now can be a self-consistent snapshot of a command
            // line the shell has already moved past. See NoteInputAwaitingEcho.
            if (_hasUnechoedInput)
            {
                return false;
            }

            // Read the line before accepting: accepting dismisses the surface, and the planner needs
            // to know what is on the command line *now* rather than what the last ranking pass saw.
            AssistQuerySnapshot? existingQuery = TryReadInsertionQuerySnapshot();

            // Everything above and below this line is non-mutating, and that ordering is the point.
            // TryAcceptSelection accepts *and* dismisses; calling it first meant that every refusal
            // after it - a degraded session, a cursor mid-line, a multiline entry - tore the list
            // down and sent nothing, so Ctrl+Enter read as "the feature is broken" rather than as
            // "not here". The plan is computed first and the surface is only touched once there is
            // text to send.
            if (!_commandAssistController.TryGetInsertionText(out string? insertionText) ||
                string.IsNullOrWhiteSpace(insertionText))
            {
                return false;
            }

            // The style is session state, so the controller answers it rather than the pane guessing
            // from what happens to be on screen: accepting a row out of explicit Ctrl+R history search
            // replaces the typed filter, and every other surface stays strictly additive. See
            // CommandAssistController.AcceptReplacesTypedQuery.
            CommandAssistInsertionStyle style = _commandAssistController.AcceptReplacesTypedQuery
                ? CommandAssistInsertionStyle.ReplaceTypedPrefix
                : CommandAssistInsertionStyle.Append;

            // The emptiness guard is on the *plan*, not on a string. A plan of pure deletes with no
            // text would read as non-empty to string.IsNullOrEmpty and would erase the user's line for
            // nothing; a plan with neither is the no-op the planner promises never to return, and this
            // is where that promise is checked rather than trusted.
            if (!CommandAssistInsertionPlanner.TryCreatePlan(existingQuery, insertionText, style, out CommandAssistInsertionPlan plan) ||
                (plan.BackspaceCount == 0 && plan.TextToSend.Length == 0))
            {
                return false;
            }

            if (!_commandAssistController.TryAcceptSelection(out _))
            {
                return false;
            }

            _lastRelevantCommandText = insertionText;

            // Text on the command line the user did not type. Insertion is only reachable when the
            // grid can be read, so the accumulator is not the capture source here anyway - but it
            // is still holding a line it can no longer describe, and Ctrl+Enter is the one key the
            // interceptor deliberately does not poison on (Command Assist owns it).
            _marklessSubmission.Poison();

            // One SendInput, and that is a requirement rather than tidiness. Each call UTF-8 encodes
            // and enqueues one byte[] onto the session's single-consumer writer thread, and
            // Parser.OnResponse writes to that same session from the *parse* thread (device-report
            // replies, which ConPTY and Clink both provoke). Two calls would leave a window in which a
            // reply could be interleaved between the deletes and the text - i.e. between erasing the
            // user's line and putting the command back on it.
            //
            // \x7f (DEL) is this codebase's backspace byte on the wire; see TerminalView's key
            // handling. No bracketed paste for either half: bracketed content is literal by
            // definition, so a DEL inside it would be inserted as a character rather than executed as
            // an erase.
            TermView.ScrollToInputLine();
            Session.SendInput(new string('\x7f', plan.BackspaceCount) + plan.TextToSend);

            // The grid is now behind the shell by everything we just sent, and the next accept must
            // not be planned against it. Omitting this was a latent bug even under the additive rule
            // (two fast Ctrl+Enters would compute the second delta against a pre-insertion line); under
            // replace it is the difference between a stale count and an erased command line.
            NoteInputAwaitingEcho();
            return true;
        }

        /// <summary>
        /// The command line the insertion planner is measured against: grid truth where it exists, and
        /// in a markless session an <em>observed-empty</em> line when - and only when - the accumulator
        /// can prove the line is empty.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>V2 Phase 3a: the "browse-only in degraded sessions" rule is narrowed, not dropped.</strong>
        /// Phase 1c refused all insertion without a snapshot, for a good reason: appending a whole
        /// command to an unknown prefix is how <c>git sgit status</c> happens. But "unknown" was doing
        /// two jobs. In the case the owner actually hit - open a markless SSH pane, press
        /// <c>Ctrl+R</c>, pick a row - the prefix is not unknown at all. It is empty, and the pane can
        /// prove it: the accumulator was reset by the last <c>Enter</c> (or <c>Ctrl+C</c>) and has
        /// observed nothing since, and it poisons on every edit it cannot model. So this returns the
        /// snapshot that says "the line was read and it is empty", which the planner already handles
        /// as a fact rather than an absence, and the whole command is sent.
        /// </para>
        /// <para>
        /// <strong>Why this is safe.</strong> Four independent things all have to agree, and each of
        /// them fails closed:
        /// </para>
        /// <list type="number">
        /// <item>the accumulator is <em>not poisoned</em> - so no arrow key, <c>Home</c>, <c>Delete</c>,
        /// <c>Tab</c>, paste, prior insertion, agent injection or unrecognised chord has touched the
        /// line since it was reset (the classification is an allow-list, so an unknown key poisons);</item>
        /// <item>the accumulator is <em>empty</em> - the user has typed nothing since the reset, so
        /// there is no prefix for the appended text to corrupt;</item>
        /// <item><see cref="_hasUnechoedInput"/> is clear, checked by the caller before this runs - so
        /// there are no keystrokes in flight to the shell that the pane has not seen come back. This is
        /// the condition that closes the "typed a character and hit Enter in the same frame" window,
        /// and it is the same gate the echo-race fix uses;</item>
        /// <item>the controller's own gates still apply - a suppressed (pasted) submission and an alt
        /// screen both refuse upstream of here.</item>
        /// </list>
        /// <para>
        /// If any of them is in doubt the answer is <see langword="null"/> and the planner refuses,
        /// exactly as before. And the failure mode of the remaining risk is bounded in a way the
        /// original refusal's was not: insertion sends text to the shell's line editor and stops. The
        /// user sees the command sitting on their prompt and has to press <c>Enter</c> themselves, so
        /// the worst case is a visible, editable, deletable line - not a command that ran.
        /// </para>
        /// <para>
        /// One honest cost: "the accumulator is clean and empty" is not the same as "the shell is at a
        /// prompt". A markless pane running a program that reads stdin (<c>cat</c>, a REPL) satisfies
        /// every condition, so an accepted row is typed into that program instead. That is what typing
        /// the command by hand would also have done, and it is visible either way; the alternative -
        /// refusing forever in every un-instrumented session - is the bug being fixed.
        /// </para>
        /// <para>
        /// <strong>Unchanged by the replace style, and deliberately so.</strong> The synthetic snapshot
        /// below has <c>Text == ""</c>, so its typed prefix is empty and a replace plans zero deletes:
        /// the degraded path is bit-identical under both styles, and no new gate is needed here. The
        /// reason it needs none is worth stating, because the obvious instinct is to add one. Replace
        /// needs a <em>count</em>, which is strictly more than append needed; in a markless session the
        /// pane can prove <em>whether</em> the line is empty but never <em>how many</em> characters a
        /// non-empty line holds - and the only markless case where the count is knowable is the case
        /// where it is zero, which is exactly the case where the two styles already agree. So there is
        /// nothing for an extra gate to catch. <c>CommandAssistGridTruthTests</c> and
        /// <c>PaneAssistInsertionTests</c> both pin the degraded refusal under the replace style.
        /// </para>
        /// </remarks>
        private AssistQuerySnapshot? TryReadInsertionQuerySnapshot()
        {
            AssistQuerySnapshot? gridTruth = _commandAssistController?.TryReadQuerySnapshot();
            if (gridTruth.HasValue)
            {
                return gridTruth;
            }

            if (!_marklessSubmission.IsCleanAndEmpty)
            {
                return null;
            }

            // Deliberately constructed rather than read: the cursor is at offset 0 of an empty line,
            // the entry is not multiline and no right prompt was trimmed, because none of those things
            // can be true of a line with no characters in it. Going through the planner rather than
            // sending the text directly keeps one refusal discipline for both paths.
            return new AssistQuerySnapshot(
                Text: string.Empty,
                CursorOffset: 0,
                IsMultiline: false,
                RightPromptTrimmed: false);
        }

        public void ToggleCommandAssist()
        {
            if (!EnsureCommandAssistInitialized())
            {
                return;
            }

            _commandAssistController?.ToggleAssist();
        }

        /// <summary>
        /// Takes this pane's assist surface down, if it has one. Used when something outside the pane
        /// invalidates what the surface is showing - today, Settings clearing command history.
        /// </summary>
        /// <remarks>
        /// Deliberately does not initialize Command Assist: a pane that never built a controller has no
        /// surface to dismiss, and forcing one into existence to tell it to hide would be the opposite of
        /// what the caller wants.
        /// </remarks>
        internal void DismissCommandAssistSurface() => _commandAssistController?.Dismiss();

        public bool OpenCommandAssistHelp()
        {
            if (!EnsureCommandAssistInitialized())
            {
                return false;
            }

            return _commandAssistController?.OpenHelp() ?? false;
        }

        public bool OpenCommandAssistHistorySearch()
        {
            if (!EnsureCommandAssistInitialized())
            {
                return false;
            }

            return _commandAssistController?.OpenHistorySearch() ?? false;
        }

        public void NotifyCommandAssistPaste(string text)
        {
            // Poisons before the feature gate: the accumulator is fed unconditionally, so it must
            // be invalidated unconditionally. See NotifyPasteObserved for how poison and
            // suppression divide the work.
            _marklessSubmission.Poison();

            if (!EnsureCommandAssistInitialized())
            {
                return;
            }

            // The text is kept for the failure-analysis path (Fix mode needs a command to analyse
            // and a paste is often the whole of one), but it is no longer handed to Command Assist
            // as query state: NotifyPastedInput carries provenance only. See
            // CommandAssistController.NotifyPastedInput.
            if (!string.IsNullOrWhiteSpace(text))
            {
                _lastRelevantCommandText = text.Trim();
            }

            _commandAssistController?.NotifyPastedInput();
        }

        /// <summary>
        /// Reads the live command line out of the terminal grid: the cells between the newest
        /// <c>OSC 133;B</c> mark and the cursor.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The Phase 1b seam. Combines the three things the reader needs and the pane is the
        /// only place that has all of: the newest mark, the buffer, and the buffer's read lock
        /// (taken by <see cref="GridQueryReader"/> itself).
        /// </para>
        /// <para>
        /// <b>Lifecycle.</b> The mark is dropped on <c>OSC 133;D</c> (command finished), so
        /// between one command's end and the next prompt's <c>B</c> this returns <c>false</c>
        /// rather than serving that command's output as a command line. It is deliberately kept
        /// across <c>OSC 133;C</c>: C fires the instant the user submits, while the input line is
        /// still on screen and still exactly what the mark describes.
        /// <see cref="GridQueryReader.MaxSpanRows"/> remains as a backstop for shells that emit
        /// <c>B</c> without a matching <c>D</c>.
        /// </para>
        /// <para>
        /// <b>The mark is only half the gate.</b> Whether the cells between it and the cursor are a
        /// command line the user is editing or the output of a command that already ran is a
        /// lifecycle fact, and Command Assist holds it
        /// (<c>AssistSessionContext.IsAcceptingCommandInput</c>, opened by <c>133;B</c> and closed
        /// by <c>133;C</c>). This seam answers only "can the grid be read from the newest mark";
        /// <see cref="TryReadAssistQuerySnapshot"/> is what the orchestrator calls, and the
        /// orchestrator applies the gate before calling it.
        /// </para>
        /// </remarks>
        /// <returns><c>false</c> when there is no live mark or the grid cannot be read.</returns>
        internal bool TryGetGridCommandLine(out GridCommandLine line)
        {
            line = default;

            var buffer = Buffer;
            if (buffer == null)
            {
                return false;
            }

            ShellIntegrationMark? mark = buffer.CommandStartMark;

            return mark is ShellIntegrationMark live
                && GridQueryReader.TryReadCommandLine(buffer, live, out line);
        }

        /// <summary>
        /// The App-boundary mapping: <see cref="GridCommandLine"/> (VT) to
        /// <see cref="AssistQuerySnapshot"/> (Command Assist). This is the provider handed to the
        /// controller, and the only place the two type systems meet.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Callable from any thread, and it is: the suggestion orchestrator invokes it from the
        /// worker its refresh pass runs on, deliberately, so the read lands behind the queue hop
        /// rather than on the keystroke that triggered it. Both things it touches are safe for
        /// that - the mark is read under the buffer's tracked-mark lock (see
        /// <see cref="TerminalBuffer.CommandStartMark"/>) and <see cref="GridQueryReader"/> takes
        /// the buffer's own read lock.
        /// </para>
        /// <para>
        /// The span's <c>StartRow</c>/<c>EndRow</c> are dropped rather than carried across. Nothing
        /// on the far side has a use for buffer coordinates, and Phase 2's anchoring work will take
        /// the <c>133;A</c> row through the anchor calculator instead.
        /// </para>
        /// </remarks>
        internal AssistQuerySnapshot? TryReadAssistQuerySnapshot()
        {
            return TryGetGridCommandLine(out GridCommandLine line)
                ? new AssistQuerySnapshot(
                    Text: line.Text,
                    CursorOffset: line.CursorOffset,
                    IsMultiline: line.IsMultiline,
                    RightPromptTrimmed: line.RightPromptTrimmed,
                    TextAfterCursorIsGhost: line.TextAfterCursorIsGhost)
                : null;
        }

        /// <summary>
        /// The query as Command Assist actually sees it: the grid read with the lifecycle gate
        /// applied.
        /// </summary>
        /// <remarks>
        /// Deliberately distinct from <see cref="TryReadAssistQuerySnapshot"/>, which is the raw
        /// seam and is ungated on purpose. The <c>133;B</c> mark survives <c>133;C</c> (it is only
        /// dropped on <c>D</c>), so the seam keeps answering while a command runs, and the thing
        /// that says its answer must not be believed is
        /// <c>AssistSessionContext.IsAcceptingCommandInput</c>, applied inside
        /// <c>SuggestionOrchestrator</c>. A test asserting "the gate closed" has to ask on this side
        /// of it; asking the seam would assert the mark lifecycle instead and pass for the wrong
        /// reason.
        /// </remarks>
        internal AssistQuerySnapshot? TryReadGatedAssistQuerySnapshotForTest() =>
            _commandAssistController?.TryReadQuerySnapshot();

        /// <summary>
        /// Records where the running command's output begins, at the <c>OSC 133;C</c> edge.
        /// </summary>
        /// <remarks>
        /// Called on the PTY parse thread. Cheap by construction - one bounded grid walk to find
        /// the last row of the input line - and unconditional, because the thing being recorded is
        /// a coordinate, not content: skipping it for successful commands would mean not knowing,
        /// at <c>D</c>, whether the command that just failed had one.
        /// </remarks>
        private void CaptureCommandOutputRegionStart()
        {
            TerminalBuffer? buffer = Buffer;
            if (buffer == null)
            {
                return;
            }

            ShellIntegrationMark? commandLineMark = buffer.CommandStartMark;

            bool captured = CommandOutputReader.TryCaptureOutputStart(
                buffer, commandLineMark, out ShellIntegrationMark outputStart);

            buffer.CommandOutputStartMark = captured ? outputStart : null;

            // The Agent Output tracker starts streaming here too: it resolves the region from the
            // same buffer mark at read time, so this notification only flips it into streaming
            // state and clears any markless heuristic start.
            _agentOutputTracker?.NotifyCommandAccepted();
        }

        /// <summary>
        /// The redacted tail of a failing command's output, read at the <c>OSC 133;D</c> edge.
        /// </summary>
        /// <returns>
        /// Null for a success, for a missing exit code, for a session with no <c>C</c> mark to
        /// bound the region, and for any grid the reader will not vouch for. Never a partial or
        /// speculative answer.
        /// </returns>
        /// <remarks>
        /// <para>
        /// <strong>The cap comes before the redaction, and that ordering is the point.</strong>
        /// <see cref="CommandOutputReader"/> stops walking after 40 logical lines or 8 KB, so the
        /// regex pass below runs over at most 8 KB however much the command printed. Redacting
        /// first and truncating afterwards would make a failing <c>terraform apply</c> pay for
        /// megabytes of scrollback on the parse thread.
        /// </para>
        /// <para>
        /// <strong>Redaction happens here, not downstream.</strong> This is the boundary: above it
        /// the text is raw grid content owned by the VT layer, below it the text is a plain string
        /// inside <c>CommandFailureContext</c> that Phase 5's provider seam may eventually send off
        /// the machine. One call site is one thing to audit, and
        /// <c>PaneCommandOutputCaptureTests</c> fails if it is removed.
        /// </para>
        /// </remarks>
        private string? TryCaptureFailureOutputTail(int? exitCode)
        {
            if (exitCode is null or 0)
            {
                return null;
            }

            TerminalBuffer? buffer = Buffer;
            if (buffer == null)
            {
                return null;
            }

            ShellIntegrationMark? outputStart = buffer.CommandOutputStartMark;

            if (outputStart is not ShellIntegrationMark region)
            {
                return null;
            }

            if (!CommandOutputReader.TryReadOutputTail(buffer, region, out string tail) ||
                string.IsNullOrWhiteSpace(tail))
            {
                return null;
            }

            ISecretsFilter filter = _commandAssistServices?.SecretsFilter ?? FallbackSecretsFilter;
            return filter.Redact(tail).RedactedText;
        }

        /// <summary>
        /// Used when no services graph has been attached (headless tests, a pane created before
        /// composition). <see cref="SecretsFilter"/> is stateless, so one instance is enough - and
        /// the alternative, skipping redaction when there is no graph, would make the guarantee
        /// conditional on wiring.
        /// </summary>
        private static readonly ISecretsFilter FallbackSecretsFilter = new SecretsFilter();

        /// <summary>The tail captured for the last failing command. Test seam; see the field.</summary>
        internal string? LastFailureOutputTailForTest => _lastFailureOutputTailForTest;

        internal async Task HandleCommandAssistCompletionAsync(int? exitCode, string? outputTail = null)
        {
            if (!EnsureCommandAssistInitialized())
            {
                return;
            }

            // Only when nothing else will patch the entry. An armed tracker turns this same OSC 133;D
            // into a CommandFinished event that patches the exit code *and* the duration, and it is
            // the better of the two; running both means the first one clears the pending entry and
            // the second silently does nothing, which loses the duration.
            //
            // Keyed on the tracker rather than on _isShellIntegrationActive since V2 Phase 2b: for a
            // remote session the latter is false while the tracker is armed, and the old condition
            // would have raced the structured patch on every SSH command.
            if (_shellLifecycleTracker == null)
            {
                await _commandAssistController.HandleCommandFinishedAsync(exitCode);
            }

            if (!exitCode.HasValue || exitCode.Value == 0 || Buffer?.IsAltScreenActive == true)
            {
                return;
            }

            string commandText = _lastRelevantCommandText?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(commandText))
            {
                return;
            }

            var context = new CommandFailureContext(
                CommandText: commandText,
                ExitCode: exitCode,
                ShellKind: DetermineShellKind(Session?.ShellCommand ?? ShellCommand),
                WorkingDirectory: CurrentWorkingDirectory,

                // V2 Phase 4a task 1: was a hard-coded null, which made two of the three branches
                // in HeuristicErrorInsightService unreachable and Fix mode a typo corrector with no
                // evidence. Captured at D on the parse thread and redacted there; see
                // TryCaptureFailureOutputTail.
                OutputTail: outputTail,
                IsRemote: Profile?.Type == ConnectionType.SSH,
                SelectedText: null);

            await _commandAssistController.HandleCommandFailureAsync(context);
        }
    }
}
