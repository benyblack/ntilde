using Ntilde.Shell;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia;
using Ntilde.VT;
using Ntilde.CommandAssist.Application;
using Ntilde.CommandAssist.ViewModels;

namespace Ntilde.Controls
{
    /// <summary>
    /// Where the Command Assist bubble and popup sit: anchoring them to the prompt, sizing them, and the
    /// correction passes that place them again after layout.
    /// </summary>
    public partial class TerminalPane
    {
        private string? _lastCommandAssistAnchorDiagnosticSignature;
        private string? _lastCommandAssistAnchorAppliedSignature;
        private string? _lastCommandAssistAnchorCorrectionSignature;
        private bool _suppressSshAssistOverlayUntilSettled;

        // Last value of IsCommandAssistOverlayRendered the controller was told about. Starts false, which
        // matches the overlay host's IsVisible="False" in the XAML.
        private bool _wasCommandAssistOverlayRendered;
        private int _sshAssistCorrectionPassCount;
        private int _commandAssistPlacementCorrectionPasses;
        private const double CommandAssistBubbleWidth = 420;
        private const double CommandAssistBubbleHeight = 36;
        private const double CommandAssistPopupWidth = 520;
        private const double CommandAssistPopupHeight = 220;

        // ---- Content-sized popup height (UX round 7).
        //
        // Every number below is read off CommandAssistPopupView.axaml rather than tuned by eye, and
        // CommandAssistLayoutTests.TerminalPane_PopupHeightEstimate_MatchesTheRenderedTemplate
        // measures the real control against them - so a template change that invalidates the estimate
        // fails in the suite instead of showing up as a popup with a gap under its last row.
        //
        // Chrome, top to bottom:
        //   outer Border BorderThickness 1, twice                            2
        //   outer Border Padding 12, twice                                  24
        //   root Grid RowSpacing 10, two gaps between its three rows        20
        //   header line (mode label / query at the default font size)       19
        //   results Border Padding 8, twice                                 16
        //   footer line (FontSize 11)                                       15
        private const double CommandAssistPopupChromeHeight = 96;

        // One row: a default-size line box (19) inside Border Padding "4,2" (4) and Margin "0,1" (2).
        private const double CommandAssistPopupRowHeight = 25;

        // The attribution credit, when there is one: StackPanel Spacing 3 plus one 10pt line.
        private const double CommandAssistPopupAttributionHeight = 19;

        // The "no matches" popup is a caption, not a list: one default-size line box (19), with none
        // of a row's padding or margin - PopupEmptyStateTextBlock is a bare TextBlock.
        private const double CommandAssistPopupEmptyStateHeight = 19;
        private const double ConservativeRemotePromptBandStartRatio = 0.55;
        private const int ConservativeRemoteMinVisibleRows = 8;
        private const double ConservativeRemoteShortPaneHeightThreshold = 300;
        private const int MaxSshAssistCorrectionPasses = 6;

        internal CommandAssistAnchorLayout? CalculateCommandAssistAnchorLayoutForTest()
        {
            return TryCalculateCommandAssistAnchorLayout();
        }

        private CommandAssistAnchorLayout? TryCalculateCommandAssistAnchorLayout()
        {
            // During startup (especially SSH), TermView bounds can briefly report a partial height.
            // Anchor against the host pane bounds first so overlays don't jump to the top band.
            double paneWidth = Bounds.Width > 0 ? Bounds.Width : TermView.Bounds.Width;
            double paneHeight = Bounds.Height > 0 ? Bounds.Height : TermView.Bounds.Height;
            if (paneWidth <= 0 || paneHeight <= 0)
            {
                return null;
            }

            CommandAssistPromptHint? promptHint = TermView.GetCommandAssistPromptHint();
            CommandAssistMarkAnchorHint? markHint = TryGetCommandAssistMarkAnchorHint();
            bool hasMarkAnchor = markHint.HasValue;
            float fallbackCellHeight = TermView.Metrics.CellHeight > 0 ? TermView.Metrics.CellHeight : 18;
            int fallbackVisibleRows = TermView.Rows > 0 ? TermView.Rows : 1;
            CommandAssistSurfaceSizing sizing = CalculateCommandAssistSurfaceSizing(
                paneWidth,
                paneHeight,
                ReadCommandAssistPopupContentSize());
            bool hasReliablePromptAnchor = IsCommandAssistPromptAnchorReliable(promptHint, hasMarkAnchor);
            float anchorCellHeight = markHint?.CellHeight ?? promptHint?.CellHeight ?? fallbackCellHeight;
            int hintCursorRow = promptHint?.VisibleCursorVisualRow ?? 0;
            int hintVisibleRows = markHint?.VisibleRows ?? promptHint?.VisibleRows ?? fallbackVisibleRows;
            int paneEstimatedVisibleRows = anchorCellHeight > 0
                ? Math.Max(1, (int)Math.Floor(paneHeight / anchorCellHeight))
                : hintVisibleRows;
            // Pane-estimated rows are a startup-jitter workaround for the heuristic path only: the
            // hint's row count lags the pane's real height for a few frames over SSH. A mark hint
            // reports the row count its own row was resolved against, so overriding it would move
            // the mark relative to a viewport it was never measured in.
            bool shouldUsePaneEstimatedRows = Profile?.Type == ConnectionType.SSH &&
                                              !hasMarkAnchor &&
                                              !hasReliablePromptAnchor &&
                                              paneEstimatedVisibleRows > hintVisibleRows;
            int anchorVisibleRows = shouldUsePaneEstimatedRows ? paneEstimatedVisibleRows : hintVisibleRows;
            int anchorCursorRow = Math.Clamp(hintCursorRow, 0, Math.Max(0, anchorVisibleRows - 1));
            int anchorMarkRow = hasMarkAnchor
                ? Math.Clamp(markHint!.Value.VisibleMarkVisualRow, 0, Math.Max(0, anchorVisibleRows - 1))
                : -1;
            bool shouldSuppress = ShouldSuppressConservativeRemoteAssist(promptHint, hasReliablePromptAnchor, hasMarkAnchor, paneHeight);
            if (shouldSuppress)
            {
                LogCommandAssistAnchorDiagnostics(
                    paneWidth,
                    paneHeight,
                    hasReliablePromptAnchor,
                    hasMarkAnchor,
                    anchorMarkRow,
                    promptHint,
                    anchorCellHeight,
                    anchorCursorRow,
                    anchorVisibleRows,
                    shouldSuppress,
                    layout: null);
                return null;
            }

            CommandAssistAnchorLayout layout = _commandAssistAnchorCalculator.Calculate(new CommandAssistAnchorRequest(
                PaneWidth: paneWidth,
                PaneHeight: paneHeight,
                CellHeight: anchorCellHeight,
                CursorVisualRow: anchorCursorRow,
                VisibleRows: anchorVisibleRows,
                BubbleWidth: sizing.BubbleWidth,
                BubbleHeight: sizing.BubbleHeight,
                PopupWidth: sizing.PopupWidth,
                PopupHeight: sizing.PopupHeight,
                HasReliablePromptAnchor: hasReliablePromptAnchor,
                HasMarkAnchor: hasMarkAnchor,
                MarkVisualRow: anchorMarkRow));
            LogCommandAssistAnchorDiagnostics(
                paneWidth,
                paneHeight,
                hasReliablePromptAnchor,
                hasMarkAnchor,
                anchorMarkRow,
                promptHint,
                anchorCellHeight,
                anchorCursorRow,
                anchorVisibleRows,
                shouldSuppress,
                layout);
            return layout;
        }

        /// <summary>
        /// The newest <c>OSC 133;B</c> mark resolved to a viewport row, or <c>null</c> when there is
        /// no live mark or it is not on screen.
        /// </summary>
        /// <remarks>
        /// Re-read on every placement pass rather than cached: the answer changes with the scroll
        /// offset (<see cref="TerminalView.CommandAssistAnchorHintChanged"/> fires on scroll), and the
        /// mark itself is replaced on every prompt repaint and dropped on <c>OSC 133;D</c>.
        /// </remarks>
        private CommandAssistMarkAnchorHint? TryGetCommandAssistMarkAnchorHint()
        {
            ShellIntegrationMark? mark = LatestCommandStartMark;

            return mark is ShellIntegrationMark live
                ? TermView.GetCommandAssistMarkAnchorHint(live)
                : null;
        }

        private void LogCommandAssistAnchorDiagnostics(
            double paneWidth,
            double paneHeight,
            bool hasReliablePromptAnchor,
            bool hasMarkAnchor,
            int anchorMarkRow,
            CommandAssistPromptHint? promptHint,
            float anchorCellHeight,
            int anchorCursorRow,
            int anchorVisibleRows,
            bool shouldSuppress,
            CommandAssistAnchorLayout? layout)
        {
            if (Profile?.Type != ConnectionType.SSH)
            {
                return;
            }

            int hintCursorRow = promptHint?.VisibleCursorVisualRow ?? -1;
            int hintVisibleRows = promptHint?.VisibleRows ?? -1;
            string layoutState = layout == null
                ? "none"
                : $"bubbleY={layout.BubbleRect.Y:F0},bubbleBottom={layout.BubbleRect.Bottom:F0},promptY={layout.PromptRect.Y:F0},usesPrompt={layout.UsesPromptAnchor},usesMark={layout.UsesMarkAnchor}";
            string signature =
                $"pw={paneWidth:F0},ph={paneHeight:F0},tw={TermView.Bounds.Width:F0},th={TermView.Bounds.Height:F0},rel={hasReliablePromptAnchor},mark={hasMarkAnchor},markRow={anchorMarkRow},sup={shouldSuppress},hintRow={hintCursorRow},hintRows={hintVisibleRows},cell={anchorCellHeight:F1},anchorRow={anchorCursorRow},anchorRows={anchorVisibleRows},vmVis={_boundCommandAssistViewModel?.IsVisible == true},{layoutState}";
            if (string.Equals(signature, _lastCommandAssistAnchorDiagnosticSignature, StringComparison.Ordinal))
            {
                return;
            }

            _lastCommandAssistAnchorDiagnosticSignature = signature;
            TerminalLogger.Log($"[AssistAnchor][SSH] {signature}");
        }

        /// <summary>
        /// Hides the overlay entirely on short SSH panes whose prompt is still high in the pane —
        /// the "it might be a login banner" case.
        /// </summary>
        /// <remarks>
        /// <para>
        /// V2 Phase 2a: this is a hedge against not knowing where the prompt is, so a mark-anchored
        /// pane never reaches it. The <paramref name="hasMarkAnchor"/> early-out is redundant with
        /// <paramref name="hasReliablePromptAnchor"/> (a mark makes the anchor reliable by
        /// definition) and deliberately so: the property under test is "marks bypass suppression",
        /// and it should not depend on a second flag staying in sync.
        /// </para>
        /// <para>
        /// V2 Phase 3a adds the second bypass, and it is the fix for the owner's third report: in a tab
        /// split into two SSH panes, the assist did not appear on one of them at all. A split halves the
        /// pane height, which puts both panes under
        /// <see cref="ConservativeRemoteShortPaneHeightThreshold"/>, and on the pane whose prompt was
        /// still in the upper band this returned true — for <c>Ctrl+R</c> as readily as for a passive
        /// bubble. Suppressing a surface the user summoned is not conservative behavior; it is the
        /// feature not working. So an explicitly requested surface is never hidden here, and the worst
        /// case becomes what the anchor calculator already does without a reliable anchor: the safe
        /// lower band.
        /// </para>
        /// <para>
        /// The suppression stays exactly as it was for uninvited surfaces on markless SSH, which is the
        /// case it was written for.
        /// </para>
        /// </remarks>
        private bool ShouldSuppressConservativeRemoteAssist(
            CommandAssistPromptHint? promptHint,
            bool hasReliablePromptAnchor,
            bool hasMarkAnchor,
            double paneHeight)
        {
            if (hasMarkAnchor || IsCommandAssistSurfaceUserRequested)
            {
                return false;
            }

            if (Profile?.Type != ConnectionType.SSH || hasReliablePromptAnchor || paneHeight > ConservativeRemoteShortPaneHeightThreshold)
            {
                return false;
            }

            if (!promptHint.HasValue)
            {
                return true;
            }

            if (promptHint.Value.VisibleRows < ConservativeRemoteMinVisibleRows)
            {
                return true;
            }

            double normalizedCursorRow = promptHint.Value.VisibleCursorVisualRow / (double)Math.Max(1, promptHint.Value.VisibleRows - 1);
            return normalizedCursorRow < ConservativeRemotePromptBandStartRatio;
        }

        /// <summary>
        /// Whether the anchor row may be trusted for prompt-adjacent placement.
        /// </summary>
        /// <remarks>
        /// V2 Phase 2a changed this from a per-session-type guess to a per-prompt fact where one is
        /// available: a live <c>OSC 133;B</c> mark in the viewport says where the prompt <i>is</i>,
        /// and that is equally true over SSH — an instrumented remote emits the same marks a local
        /// shell does. Only markless sessions fall through to the old rule, which is why the SSH
        /// clause below survives rather than being deleted.
        /// </remarks>
        private bool IsCommandAssistPromptAnchorReliable(CommandAssistPromptHint? promptHint, bool hasMarkAnchor)
        {
            if (hasMarkAnchor)
            {
                return true;
            }

            if (!promptHint.HasValue)
            {
                return false;
            }

            // Markless SSH sessions stay on the heuristic path, so cursor-row hints are not
            // trustworthy enough for prompt-adjacent anchoring.
            if (Profile?.Type == ConnectionType.SSH)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Reads the popup's current contents off the bound view-model, for the height estimate.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The row count comes from <c>CommandAssistBarViewModel.SuggestionCount</c> rather than from
        /// <c>Suggestions.Count</c>, and that is what makes the placement re-run at all. A placement
        /// pass is triggered by <see cref="OnCommandAssistViewModelPropertyChanged"/>, which listens to
        /// the bar view-model; the collection is rebuilt without any property on it changing, so
        /// before the count was published a <c>Ctrl+R</c> filter that went from six rows to two left
        /// the popup at its six-row height. Reading the same property the notification came from also
        /// means the two can never disagree.
        /// </para>
        /// <para>
        /// Not the popup view-model's copy: the pane holds the bar view-model, and the bar publishes
        /// to the popup <em>after</em> raising its own change - so a read through the popup during
        /// that notification would be one pass stale.
        /// </para>
        /// </remarks>
        private CommandAssistPopupContentSize ReadCommandAssistPopupContentSize()
        {
            CommandAssistBarViewModel? viewModel = _boundCommandAssistViewModel;
            if (viewModel == null)
            {
                return new CommandAssistPopupContentSize(0, false, false);
            }

            return new CommandAssistPopupContentSize(
                viewModel.SuggestionCount,
                viewModel.ShowEmptyState,
                !string.IsNullOrWhiteSpace(viewModel.AttributionText));
        }

        private static CommandAssistSurfaceSizing CalculateCommandAssistSurfaceSizing(
            double paneWidth,
            double paneHeight,
            CommandAssistPopupContentSize content)
        {
            double bubbleWidth = Math.Clamp(paneWidth * 0.44, 280, CommandAssistBubbleWidth);
            double popupWidth = Math.Clamp(paneWidth * 0.58, 360, CommandAssistPopupWidth);
            double popupHeight = EstimateCommandAssistPopupHeight(content, paneHeight);

            return new CommandAssistSurfaceSizing(
                BubbleWidth: bubbleWidth,
                BubbleHeight: CommandAssistBubbleHeight,
                PopupWidth: popupWidth,
                PopupHeight: popupHeight);
        }

        /// <summary>
        /// How tall the popup wants to be for the rows it is about to show.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Why the height is estimated up front instead of measured.</strong> The obvious
        /// shrink-to-fit - <c>Height = NaN</c> with a <c>MaxHeight</c> cap - is wrong here, and
        /// silently so. <c>CommandAssistAnchorCalculator.CreatePopupRect</c> places an upward popup by
        /// subtracting the <em>requested</em> height from the bubble's top edge, and the pane applies
        /// the result as <c>Margin.Top</c> inside a top-aligned grid. An auto-sized popup asked for
        /// 220 and drawn at 120 would therefore hang 100 px above the prompt, attached to nothing.
        /// Feeding the desired height into the request instead keeps the popup a fixed rectangle -
        /// one that happens to be exactly the size of its contents - and every placement rule
        /// downstream keeps working unchanged.
        /// </para>
        /// <para>
        /// The estimate is allowed to be a little generous and must never be short: too tall leaves a
        /// few pixels of card under the last row, too short clips it. The clamp keeps the old
        /// <c>paneHeight * 0.45</c> ceiling, so a long list behaves exactly as it did and scrolls.
        /// </para>
        /// </remarks>
        internal static double EstimateCommandAssistPopupHeight(CommandAssistPopupContentSize content, double paneHeight)
        {
            double body = content.ShowEmptyState
                ? CommandAssistPopupEmptyStateHeight
                : Math.Max(1, content.RowCount) * CommandAssistPopupRowHeight;
            double desired = CommandAssistPopupChromeHeight + body;
            if (content.HasAttribution)
            {
                desired += CommandAssistPopupAttributionHeight;
            }

            // The floor is one row's worth of popup - or the caption's one line - not the old 160: a
            // single-row Ctrl+R answer that reserved 160 px was most of the box the owner asked us to
            // stop drawing.
            double floor = CommandAssistPopupChromeHeight + (content.ShowEmptyState
                ? CommandAssistPopupEmptyStateHeight
                : CommandAssistPopupRowHeight);
            double ceiling = Math.Max(floor, Math.Clamp(paneHeight * 0.45, floor, CommandAssistPopupHeight));

            return Math.Clamp(desired, floor, ceiling);
        }

        private void UpdateCommandAssistOverlayPlacement()
        {
            CommandAssistAnchorLayout? layout = TryCalculateCommandAssistAnchorLayout();
            bool shouldShowOverlayHost = layout != null && (_boundCommandAssistViewModel?.IsVisible == true);
            if (!shouldShowOverlayHost || layout?.UsesMarkAnchor == true)
            {
                // Mark-anchored placement never hides the overlay while it settles: there is nothing
                // to settle. Clearing here also unwinds any suppression left over from a markless
                // frame earlier in the same session.
                //
                // A user-requested surface deliberately does *not* clear the counters here, even though
                // it is also never hidden (V2 Phase 3a). Resetting _sshAssistCorrectionPassCount on
                // every placement pass would stop MaxSshAssistCorrectionPasses from ever being reached,
                // and since a correction pass posts another placement pass, that is an unbounded render
                // loop. The bypass therefore lives on the opacity write below, which is the only thing
                // the user can see anyway.
                _suppressSshAssistOverlayUntilSettled = false;
                _sshAssistCorrectionPassCount = 0;

                // The correction-log dedup signature belongs to the run of passes being abandoned.
                // Left set, the first [Corrected] line after a later markless relapse is swallowed
                // as a duplicate of one from before the transition - and that first line is exactly
                // the diagnostic that says the mark anchor stopped working.
                _lastCommandAssistAnchorCorrectionSignature = null;
            }

            if (CommandAssistOverlayHost != null)
            {
                // The settle-suppression never applies to a surface the user asked for (V2 Phase 3a):
                // an invisible answer to Ctrl+R is indistinguishable from no answer, which is how the
                // owner experienced it on one pane of an SSH split.
                bool keepOverlayOpaque = !_suppressSshAssistOverlayUntilSettled || IsCommandAssistSurfaceUserRequested;
                CommandAssistOverlayHost.IsVisible = shouldShowOverlayHost;
                CommandAssistOverlayHost.Opacity = shouldShowOverlayHost && keepOverlayOpaque ? 1.0 : 0.0;
                NotifyCommandAssistOverlayRenderedChanged();
            }

            if (layout == null)
            {
                return;
            }

            if (CommandAssistBubble != null)
            {
                if (_boundCommandAssistViewModel != null)
                {
                    // The width budget only. Whether the query is worth showing at a width that could
                    // hold it is the view-model's call - Fix mode declines it - so this sets the
                    // permission rather than the outcome. See CommandAssistBarViewModel.AllowBubbleQueryText.
                    _boundCommandAssistViewModel.AllowBubbleQueryText = !layout.UseCompactBubbleLayout;

                    // Same rule, same reason, one more casualty of a narrow bubble: the hint strip's
                    // Auto column beat the summary's * column at the width a split SSH pane produces,
                    // so the one thing the bubble exists to show was squeezed out by a legend for it
                    // (PR #290 review). Since the UX-polish round the collapse has a middle rung -
                    // keys without verbs - because the owner's pane was wider than the compact
                    // threshold and still could not fit both. The popup footer always carries the
                    // full shortcuts.
                    _boundCommandAssistViewModel.BubbleHintDetail = layout.UseCompactBubbleLayout
                        ? AssistHintDetail.Hidden
                        : layout.UseTerseBubbleHint
                            ? AssistHintDetail.Terse
                            : AssistHintDetail.Full;
                }

                CommandAssistBubble.Width = layout.BubbleRect.Width;
                CommandAssistBubble.Height = layout.BubbleRect.Height;
                CommandAssistBubble.MinHeight = layout.BubbleRect.Height;
                CommandAssistBubble.MaxWidth = layout.BubbleRect.Width;
                CommandAssistBubble.MaxHeight = layout.BubbleRect.Height;
                CommandAssistBubble.Margin = new Thickness(
                    layout.BubbleRect.X,
                    layout.BubbleRect.Y,
                    0,
                    0);
            }

            if (CommandAssistPopup != null)
            {
                CommandAssistPopup.Width = layout.PopupRect.Width;
                CommandAssistPopup.Height = layout.PopupRect.Height;
                CommandAssistPopup.MinHeight = layout.PopupRect.Height;
                CommandAssistPopup.MaxWidth = layout.PopupRect.Width;
                CommandAssistPopup.MaxHeight = layout.PopupRect.Height;
                CommandAssistPopup.Margin = new Thickness(
                    layout.PopupRect.X,
                    layout.PopupRect.Y,
                    0,
                    0);
            }

            LogCommandAssistAnchorAppliedDiagnostics(layout);
            ScheduleCommandAssistPlacementCorrection(layout);
        }

        private void LogCommandAssistAnchorAppliedDiagnostics(CommandAssistAnchorLayout layout)
        {
            if (Profile?.Type != ConnectionType.SSH || CommandAssistBubble == null)
            {
                return;
            }

            string signature =
                $"layoutY={layout.BubbleRect.Y:F0},layoutPromptY={layout.PromptRect.Y:F0},appliedBubbleTop={CommandAssistBubble.Margin.Top:F0},appliedBubbleVis={CommandAssistBubble.IsVisible},hostVis={CommandAssistOverlayHost?.IsVisible == true},vmVis={_boundCommandAssistViewModel?.IsVisible == true},popupVm={_boundCommandAssistViewModel?.IsPopupOpen == true}";
            if (string.Equals(signature, _lastCommandAssistAnchorAppliedSignature, StringComparison.Ordinal))
            {
                return;
            }

            _lastCommandAssistAnchorAppliedSignature = signature;
            TerminalLogger.Log($"[AssistAnchor][SSH][Applied] {signature}");
        }

        /// <summary>
        /// Total placement-correction passes this pane has scheduled, ever. A test seam for the
        /// V2 Phase 2a property "a mark-anchored pane runs zero correction passes" — the production
        /// evidence for the same thing is the absence of <c>[AssistAnchor][SSH][Corrected]</c> lines
        /// in the log, which a test cannot read.
        /// </summary>
        /// <remarks>
        /// Zero is also what an under-driven test reads: the counter is only reachable with a visible
        /// bound view model on an SSH pane. <c>CommandAssistLayoutTests</c> pairs every zero-pass
        /// assertion with a markless negative control that must reach it, because without one the
        /// assertion holds with the <c>UsesMarkAnchor</c> gate deleted.
        /// </remarks>
        internal int CommandAssistPlacementCorrectionPassesForTest => _commandAssistPlacementCorrectionPasses;

        /// <summary>
        /// Whether a computed layout warrants a placement-correction pass.
        /// </summary>
        /// <remarks>
        /// Split out of <see cref="ScheduleCommandAssistPlacementCorrection"/> so the V2 Phase 2a
        /// rule — "a mark anchor runs no corrections, whatever the session type" — is assertable
        /// without a visible assist view model and a live render pass driving it.
        /// </remarks>
        private bool ShouldCorrectCommandAssistPlacement(CommandAssistAnchorLayout layout, bool assistIsVisible)
        {
            if (layout.UsesMarkAnchor)
            {
                return false;
            }

            // Deliberately *not* gated on IsCommandAssistSurfaceUserRequested. V2 Phase 3a's rule is
            // that a summoned surface is never hidden, not that it is never corrected: correcting the
            // placement is the useful half of this stack and costs the user nothing. What it must not do
            // is drop the overlay to zero opacity while it settles, which for up to six render passes
            // reads as "Ctrl+R did nothing" — so the hiding is what carries the bypass, at the two
            // places that apply it (see the opacity write in UpdateCommandAssistOverlayPlacement and
            // the suppression write inside CorrectPlacement).
            return Profile?.Type == ConnectionType.SSH && assistIsVisible;
        }

        /// <summary>
        /// Whether the assist surface currently on screen is one the user asked for by name —
        /// <c>Ctrl+Space</c>, <c>Ctrl+R</c>, Help, a confident Fix popup.
        /// </summary>
        /// <remarks>
        /// The single question every overlay-hiding heuristic in this file now asks first. The
        /// authority is <c>AssistSessionStateMachine.IsUserRequestedSurface</c>; this is only the
        /// null-safe pane-side spelling of it.
        /// </remarks>
        private bool IsCommandAssistSurfaceUserRequested =>
            _commandAssistController?.IsUserRequestedSurface == true;

        /// <summary>
        /// Whether the assist overlay this pane hosts is actually on screen right now.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The pane-side half of the PR #290 review's first blocker. Assist visibility has two
        /// authorities and they disagree: the session says "a surface is up" through
        /// <c>CommandAssistBarViewModel.IsVisible</c>, and this pane independently hides the overlay host
        /// when the conservative anchor check yields no layout, or drops it to zero opacity while a
        /// placement correction settles. Both of those bypasses are waived for a surface the user asked
        /// for by name - and a <c>PassivePopup</c> is not one, so a passive popup could hold an armed
        /// <c>Enter</c> at zero pixels while the user's command line silently failed to submit.
        /// </para>
        /// <para>
        /// Read live rather than cached: it is asked on the key path and during
        /// <c>SyncPresentationState</c>, and both want the current frame's answer.
        /// </para>
        /// </remarks>
        internal bool IsCommandAssistOverlayRendered =>
            CommandAssistOverlayHost is { IsVisible: true } host && host.Opacity > 0;

        /// <summary>
        /// Tells the controller the answer to <see cref="IsCommandAssistOverlayRendered"/> moved, so the
        /// hint strip can catch up with the routing decision.
        /// </summary>
        /// <remarks>
        /// Gated on an actual change, and not only to save work: republishing presentation state can
        /// raise <c>IsAcceptOnEnterArmed</c>, which this pane listens to, which runs another placement
        /// pass. The change check is what makes that converge on the second pass instead of recursing.
        /// </remarks>
        private void NotifyCommandAssistOverlayRenderedChanged()
        {
            bool isRendered = IsCommandAssistOverlayRendered;
            if (isRendered == _wasCommandAssistOverlayRendered)
            {
                return;
            }

            _wasCommandAssistOverlayRendered = isRendered;
            _commandAssistController?.NotifyRenderedSurfaceVisibilityChanged();
        }

        internal bool ShouldCorrectCommandAssistPlacementForTest(CommandAssistAnchorLayout layout, bool assistIsVisible)
        {
            return ShouldCorrectCommandAssistPlacement(layout, assistIsVisible);
        }

        /// <summary>
        /// Re-applies the computed margins on the next render pass when the rendered position drifted
        /// from the computed one, hiding the overlay until it agrees.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the SSH jitter mitigation from the 2026-03-11 firefight: the heuristic anchor moved
        /// between frames during remote startup, so the overlay was chasing a row that had already
        /// changed, visibly.
        /// </para>
        /// <para>
        /// V2 Phase 2a gates it off for mark-anchored layouts rather than deleting it. A mark row does
        /// not jitter — it is re-derived from the buffer on every pass and is either on screen or
        /// absent — so there is nothing for a correction pass to correct, and the opacity flicker it
        /// costs is pure loss. The stack stays for markless SSH, which is still a supported session
        /// type; it goes away with that, not with this change.
        /// </para>
        /// </remarks>
        private void ScheduleCommandAssistPlacementCorrection(CommandAssistAnchorLayout layout)
        {
            if (!ShouldCorrectCommandAssistPlacement(layout, _boundCommandAssistViewModel?.IsVisible == true))
            {
                return;
            }

            _commandAssistPlacementCorrectionPasses++;

            void CorrectPlacement()
            {
                if (CommandAssistBubble == null || CommandAssistOverlayHost == null || !CommandAssistOverlayHost.IsVisible)
                {
                    return;
                }

                // `layout` was captured a frame ago, while the pane was still markless. A 133;B mark
                // can land in between - that is the markless->mark handoff this change exists to
                // clean up - and by now the overlay has been re-placed against the mark row. Measured
                // against the stale layout that reads as drift, so the block below would hide the
                // overlay and re-apply markless margins for a frame at the exact moment the anchor
                // became exact. Re-derive and re-ask the gate instead; the answer for a mark-anchored
                // pane is "no correction", so this returns without touching anything.
                CommandAssistAnchorLayout? currentLayout = TryCalculateCommandAssistAnchorLayout();
                if (currentLayout == null ||
                    !ShouldCorrectCommandAssistPlacement(currentLayout, _boundCommandAssistViewModel?.IsVisible == true))
                {
                    // Not a bare return: a markless pass may have left the overlay hidden waiting for
                    // this pass to clear it, and nothing else will now.
                    ReleaseSshAssistOverlaySuppression();
                    return;
                }

                Control? anchorControl = CommandAssistBubble.IsVisible
                    ? CommandAssistBubble
                    : CommandAssistPopup != null && CommandAssistPopup.IsVisible
                        ? CommandAssistPopup
                        : null;
                if (anchorControl == null)
                {
                    return;
                }

                Point? anchorTopLeft = anchorControl.TranslatePoint(new Point(0, 0), this);
                if (!anchorTopLeft.HasValue)
                {
                    return;
                }

                bool anchoredToBubble = ReferenceEquals(anchorControl, CommandAssistBubble);
                double expectedTop = anchoredToBubble ? layout.BubbleRect.Y : layout.PopupRect.Y;
                double actualTop = anchorTopLeft.Value.Y;
                double drift = Math.Abs(actualTop - expectedTop);
                if (drift <= 2)
                {
                    ReleaseSshAssistOverlaySuppression();
                    return;
                }

                // Correct the placement either way; hide only what the user did not ask for.
                if (!IsCommandAssistSurfaceUserRequested)
                {
                    _suppressSshAssistOverlayUntilSettled = true;
                    CommandAssistOverlayHost.Opacity = 0.0;
                    NotifyCommandAssistOverlayRenderedChanged();
                }

                // Re-apply anchored margins if the rendered position drifted from expected.
                CommandAssistBubble.Margin = new Thickness(layout.BubbleRect.X, layout.BubbleRect.Y, 0, 0);
                if (CommandAssistPopup != null)
                {
                    CommandAssistPopup.Margin = new Thickness(layout.PopupRect.X, layout.PopupRect.Y, 0, 0);
                }

                string signature = $"anchor={(anchoredToBubble ? "bubble" : "popup")},expected={expectedTop:F0},actual={actualTop:F0},drift={drift:F0},pass={_sshAssistCorrectionPassCount}";
                if (!string.Equals(signature, _lastCommandAssistAnchorCorrectionSignature, StringComparison.Ordinal))
                {
                    _lastCommandAssistAnchorCorrectionSignature = signature;
                    TerminalLogger.Log($"[AssistAnchor][SSH][Corrected] {signature}");
                }

                if (_sshAssistCorrectionPassCount >= MaxSshAssistCorrectionPasses)
                {
                    _suppressSshAssistOverlayUntilSettled = false;
                    _sshAssistCorrectionPassCount = 0;
                    CommandAssistOverlayHost.Opacity = 1.0;
                    NotifyCommandAssistOverlayRenderedChanged();
                    TerminalLogger.Log("[AssistAnchor][SSH][Corrected] max-pass reached; showing overlay with best-known anchor.");
                    return;
                }

                _sshAssistCorrectionPassCount++;

                // Re-evaluate on the next render pass; keep host hidden until settled.
                this.Dispatcher.Post(UpdateCommandAssistOverlayPlacement, DispatcherPriority.Render);
            }

            this.Dispatcher.Post(CorrectPlacement, DispatcherPriority.Render);
        }

        /// <summary>
        /// Ends a run of placement-correction passes and un-hides the overlay if one of them hid it.
        /// </summary>
        /// <remarks>
        /// The correction stack is the only thing that sets
        /// <see cref="_suppressSshAssistOverlayUntilSettled"/>, so it is also the only thing that can
        /// clear it: every exit from a correction pass that is not "post another one" comes through
        /// here, or the overlay stays at zero opacity until the next placement pass happens to run.
        /// </remarks>
        private void ReleaseSshAssistOverlaySuppression()
        {
            _sshAssistCorrectionPassCount = 0;
            if (!_suppressSshAssistOverlayUntilSettled)
            {
                return;
            }

            _suppressSshAssistOverlayUntilSettled = false;
            if (CommandAssistOverlayHost != null)
            {
                CommandAssistOverlayHost.Opacity = 1.0;
                NotifyCommandAssistOverlayRenderedChanged();
            }
        }

        private readonly record struct CommandAssistSurfaceSizing(
            double BubbleWidth,
            double BubbleHeight,
            double PopupWidth,
            double PopupHeight);

        /// <summary>
        /// The three facts about the popup's contents that change how tall it needs to be.
        /// </summary>
        /// <remarks>
        /// Internal, and a parameter rather than something
        /// <see cref="EstimateCommandAssistPopupHeight"/> reads off the bound view-model, so the
        /// height rule is a pure function the suite can drive across row counts without a pane, a
        /// session or a render pass.
        /// </remarks>
        internal readonly record struct CommandAssistPopupContentSize(
            int RowCount,
            bool ShowEmptyState,
            bool HasAttribution);
    }
}
