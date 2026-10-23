using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;

namespace Ntilde
{
    /// <summary>
    /// Focus mode: a per-window toggle that hides the app chrome around the selected tab behind a
    /// slim strip. This file owns the flag and the window-level chrome (the tab header area and the
    /// title bar items); nothing here touches selection, zoom, splits or any persisted setting.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>Height of the focus-mode strip, which stands in for the 36px title band.</summary>
        internal const double FocusStripHeight = 24;

        private bool _isFocusMode;

        internal bool IsFocusMode => _isFocusMode;

        /// <summary>The top band that drags the window: the strip in focus mode, the title band otherwise.</summary>
        internal static double WindowDragBandHeight(bool focusMode) => focusMode ? FocusStripHeight : 36;

        /// <summary>What a double-click on the strip does: restore a maximised window, maximise anything else.</summary>
        internal static WindowState ToggledMaximizeState(WindowState current) =>
            current == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        internal void EnterFocusMode()
        {
            if (_isFocusMode) return;
            _isFocusMode = true;
            WindowDecorationsTheme = (ControlTheme)this.FindResource("NtildeFocusWindowDecorationsTheme")!;
            foreach (var pane in AllPanes()) pane.FocusChromeHidden = true;
            ApplyFocusModeChrome();
            RefreshAgentObserveIndicator();
            HoldShownUpdateToast();
        }

        internal void LeaveFocusMode()
        {
            if (!_isFocusMode) return;
            _isFocusMode = false;
            WindowDecorationsTheme = (ControlTheme)this.FindResource("NtildeWindowDecorationsTheme")!;
            // Regenerates every generated title bar item with its own visibility.
            RebuildTitleBar();
            foreach (var pane in AllPanes()) pane.FocusChromeHidden = false;
            ApplyFocusModeChrome();
            // A closed pane can never be looked at; the tab strip coming back is the acknowledgement.
            _focusCloseMarks.Clear();
            RefreshAgentObserveIndicator();
            ReleaseHeldNotices();
        }

        // Spec section 6: notices that arrive on their own wait for focus mode to end.
        private string? _heldUpdateVersion;
        private string? _shownUpdateVersion;

        /// <summary>
        /// A prompted toast shows now; an unprompted one waits in focus mode. The key makes a repeat replace the
        /// held one, so only the latest is shown on leave.
        /// </summary>
        private void ShowUnpromptedToast(string title, string message, string key)
        {
            if (_isFocusMode)
            {
                DismissNotice(key);
                EnqueueNotice(title, message, key: key);
                return;
            }

            ShowRecordingToast(title, message, filePath: null, folderPath: null, autoHide: true);
        }

        private void ShowUpdateToastUnprompted(string version)
        {
            if (_isFocusMode) _heldUpdateVersion = version;
            else ShowUpdateToast(version);
        }

        /// <summary>An update toast already on screen goes away for focus mode and comes back on leave.</summary>
        private void HoldShownUpdateToast()
        {
            var toast = this.FindControl<Avalonia.Controls.Border>("UpdateToast");
            if (toast is not { IsVisible: true }) return;
            _heldUpdateVersion = _shownUpdateVersion;
            toast.IsVisible = false;
        }

        private void ReleaseHeldNotices()
        {
            if (_heldUpdateVersion is { } version)
            {
                _heldUpdateVersion = null;
                ShowUpdateToast(version);
            }

            FlushPersistenceNotices();
        }

        /// <summary>Every user action that goes to another tab calls this first.</summary>
        private void LeaveFocusModeForNavigation()
        {
            if (_isFocusMode) LeaveFocusMode();
        }

        /// <summary>An agent's new tab takes the selection only outside focus mode.</summary>
        internal static bool ShouldAgentSpawnSelect(bool focusMode) => !focusMode;

        /// <summary>
        /// Closing <paramref name="pane"/> closes its tab, which moves the user to another one. Zoom-aware: a zoomed
        /// split still has its other panes.
        /// </summary>
        private bool IsOnlyPaneOfItsTab(Controls.TerminalPane pane) =>
            ResolveOwningTabForPane(pane) is { } tab && PanesInTab(tab).Count() == 1;

        // Panes agents closed in focus mode, in the order they went. A closed pane's agent registration goes with it, so
        // the write the agent host notes for the close reaches no indicator; the light keeps these instead, until focus
        // mode is left.
        private readonly List<FocusWriteMark> _focusCloseMarks = [];

        private readonly HashSet<Guid> _agentClosingPaneIds = [];
        private bool _disposingAllPanes;

        /// <summary>
        /// Called by DisposeControlTree for every pane that goes, before it is unwired (which drops its agent registration
        /// and the Wrote mark with it). In focus mode an unseen agent write into the pane stays on the light as a
        /// "since closed" mark until focus mode is left. Not for window teardown, and not for an agent's own close, which
        /// leaves its close mark.
        /// </summary>
        private void NoteUnseenWriteOfClosingPane(Controls.TerminalPane pane)
        {
            if (!_isFocusMode || _teardownDone || _disposingAllPanes) return;
            if (_agentClosingPaneIds.Contains(pane.PaneId)) return;

            var registration = AgentHost.AgentSessionRegistry.Instance.GetRegistrations().FirstOrDefault(r => r.PaneId == pane.PaneId);
            if (registration == null) return;
            var snapshot = registration.AttentionMachine.Snapshot();
            if (snapshot.Tier != AgentHost.AgentAttentionTier.Wrote) return;

            TabItem? tab = ResolveOwningTabForPane(pane);
            tab ??= this.FindControl<TabControl>("Tabs")?.Items.OfType<TabItem>()
                .FirstOrDefault(t => registration.TabId is { } id && GetPersistentTabId(t) == id);
            string tabTitle = tab != null ? GetTabHeaderText(tab) : "Terminal";
            // The close path has usually taken the pane out of its tab's layout by now, so count it back in.
            string? paneTitle = null;
            if (tab != null)
            {
                var remaining = PanesInTab(tab).ToList();
                int before = remaining.Count + (remaining.Contains(pane) ? 0 : 1);
                if (before > 1) paneTitle = pane.GetBaseTabTitle();
            }
            _focusCloseMarks.Add(new FocusWriteMark(tabTitle, paneTitle, snapshot.LastWriteUtc, SinceClosed: true));
            RefreshAgentObserveIndicator();
        }

        /// <summary>The light's mark for an agent about to close <paramref name="pane"/>, read while its tab still exists.</summary>
        private FocusWriteMark DescribeAgentClose(Controls.TerminalPane pane)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (ResolveOwningTabForPane(pane) is not { } tab) return new FocusWriteMark("Terminal", null, now, Closed: true);
            string? paneTitle = PanesInTab(tab).Count() > 1 ? pane.GetBaseTabTitle() : null;
            return new FocusWriteMark(GetTabHeaderText(tab), paneTitle, now, Closed: true);
        }

        /// <summary>
        /// An agent's closeSession in focus mode: the pane closes where it is, its tab not selected first, and the strip
        /// light keeps a mark of it. Closing the last pane of the user's own tab cannot avoid moving them to another
        /// shell, so focus mode is left then, and the tab strip shows the change. True when the pane went.
        /// </summary>
        private async Task<bool> CloseAgentTargetInFocusModeAsync(Controls.TerminalPane target)
        {
            FocusWriteMark mark = DescribeAgentClose(target);
            bool emptiesSelectedTab = IsOnlyPaneOfItsTab(target)
                && ReferenceEquals(ResolveOwningTabForPane(target), this.FindControl<TabControl>("Tabs")?.SelectedItem);

            // Resolved from the pane, so the user's tab stays selected. In a split, ClosePaneCoreAsync also leaves the
            // user's active pane and keyboard alone while focus mode is on.
            // The disposal hook would also mark an unseen write into this pane; the close mark below covers it.
            _agentClosingPaneIds.Add(target.PaneId);
            try { await ClosePaneAsync(target, skipConfirm: true); }
            finally { _agentClosingPaneIds.Remove(target.PaneId); }
            if (_paneOwnerTab.ContainsKey(target)) return false;

            _focusCloseMarks.Add(mark);
            if (emptiesSelectedTab && !_teardownDone) LeaveFocusMode();
            RefreshAgentObserveIndicator();
            return true;
        }

        internal void ToggleFocusMode()
        {
            if (_isFocusMode) LeaveFocusMode();
            else EnterFocusMode();
        }

        // ---- the agent light (spec section 7) ----

        private const string DefaultObserveLightTooltip = "Agent access is enabled. Click to open the agent activity journal.";
        private const string JournalHint = "Click to open the agent activity journal.";

        /// <summary>
        /// A pane an agent typed into that the user has not yet looked at, or (<paramref name="Closed"/>) a pane an agent
        /// closed in focus mode, as the light names it.
        /// </summary>
        internal sealed record FocusWriteMark(string TabTitle, string? PaneTitle, DateTimeOffset? LastWriteUtc, bool Closed = false, bool SinceClosed = false);

        /// <summary>
        /// Amber fill for an unseen write, blue for a read or poll, grey when idle. A write and a read at
        /// once keep the amber fill and add a blue ring, so a pending write never hides a read.
        /// </summary>
        internal static (string Fill, string? Ring) ObserveLightAppearance(bool active, bool unseenWrite)
            => unseenWrite
                ? ("#E8A33D", active ? "#4FB0D4" : null)
                : (active ? "#4FB0D4" : "#6B737F", null);

        internal static string BuildFocusLightTooltip(IReadOnlyList<FocusWriteMark> marks, bool active = false)
        {
            static string Target(FocusWriteMark m) => m.PaneTitle is null ? m.TabTitle : $"{m.TabTitle} › {m.PaneTitle}";
            static string At(FocusWriteMark m) => m.LastWriteUtc is { } t
                ? " at " + t.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                : "";

            if (marks.Count == 0) return DefaultObserveLightTooltip;
            const string reading = "An agent is reading now.";
            if (marks.Count == 1)
            {
                FocusWriteMark m = marks[0];
                string one = m.Closed
                    ? $"An agent closed {Target(m)}{At(m)}. {JournalHint}"
                    : m.SinceClosed
                        ? $"An agent typed into {Target(m)}{At(m)}; it has since closed. {JournalHint}"
                        : $"An agent typed into {Target(m)}{At(m)}. {JournalHint}";
                return active ? reading + "\n" + one : one;
            }

            var lines = new List<string>();
            if (active) lines.Add(reading);
            lines.Add(marks.Any(m => m.Closed) ? "Agents typed into or closed:" : "Agents typed into:");
            lines.AddRange(marks.Select(m => m.Closed ? $"- {Target(m)} closed{At(m)}"
                : m.SinceClosed ? $"- {Target(m)} typed into{At(m)}, since closed"
                : $"- {Target(m)}{At(m)}"));
            lines.Add(JournalHint);
            return string.Join("\n", lines);
        }

        /// <summary>
        /// Panes whose attention tier is still Wrote, in tab order, then the panes agents closed in focus mode, in the
        /// order they went. Only focus mode has a use for them: outside it the pane segment and tab badge carry the
        /// write, and the user sees a closed tab go, so this is empty.
        /// </summary>
        internal IReadOnlyList<FocusWriteMark> CollectUnseenWriteMarks()
        {
            if (!_isFocusMode) return Array.Empty<FocusWriteMark>();

            var tabs = this.FindControl<TabControl>("Tabs")?.Items.OfType<TabItem>().ToList() ?? [];
            var found = new List<(int Order, FocusWriteMark Mark)>();

            // Start from the registrations, not from a walk of the layout: a pane that cannot be found in
            // any tab (a zoom stash, a pane mid-creation) must still be reported. A redundant mark is
            // acceptable; a silent write is not.
            foreach (var registration in AgentHost.AgentSessionRegistry.Instance.GetRegistrations())
            {
                var snapshot = registration.AttentionMachine.Snapshot();
                if (snapshot.Tier != AgentHost.AgentAttentionTier.Wrote) continue;

                Controls.TerminalPane? pane = _paneOwnerTab.Keys.FirstOrDefault(p => p.PaneId == registration.PaneId)
                    ?? AllPanes().FirstOrDefault(p => p.PaneId == registration.PaneId);
                TabItem? tab = pane != null ? ResolveOwningTabForPane(pane) : null;
                tab ??= tabs.FirstOrDefault(t => registration.TabId is { } id && GetPersistentTabId(t) == id);

                if (tab == null)
                {
                    found.Add((int.MaxValue, new FocusWriteMark("Terminal", null, snapshot.LastWriteUtc)));
                    continue;
                }

                string? paneTitle = pane != null && PanesInTab(tab).Count() > 1 ? pane.GetBaseTabTitle() : null;
                found.Add((tabs.IndexOf(tab) is var i && i >= 0 ? i : int.MaxValue,
                    new FocusWriteMark(GetTabHeaderText(tab), paneTitle, snapshot.LastWriteUtc)));
            }

            return [.. found.OrderBy(f => f.Order).Select(f => f.Mark), .. _focusCloseMarks];
        }

        private void ApplyFocusModeChrome()
        {
            UpdateTabHeaderViewport();
            ApplyFocusModeToTitleBar();
            UpdateFocusStrip();
        }

        private void ApplyFocusModeToTitleBar()
        {
            var host = this.FindControl<StackPanel>("TitleBarItemsHost");
            var titleBar = this.FindControl<Grid>("TitleBar");
            if (host == null || titleBar == null) return;

            if (this.FindControl<MenuItem>("MenuCustomizeTitleBar") is { } customize)
                customize.IsVisible = !_isFocusMode;
            if (this.FindControl<MenuItem>("MenuTitleBarExitFocusMode") is { } exit)
                exit.IsVisible = _isFocusMode;

            var margin = titleBar.Margin;
            if (_isFocusMode)
            {
                // The agent light stays: it is a safety surface with no off switch.
                foreach (Control child in host.Children.OfType<Control>()
                             .Where(c => c.Name != "AgentObserveIndicator"))
                {
                    child.IsVisible = false;
                }

                titleBar.Height = FocusStripHeight;
                titleBar.Margin = new Thickness(margin.Left, 0, margin.Right, margin.Bottom);
            }
            else
            {
                var newTab = this.FindControl<Button>(Shell.TitleBar.TitleBarViewFactory.NewTabButtonName);
                if (newTab != null) newTab.IsVisible = true;

                titleBar.Height = 32;
                titleBar.Margin = new Thickness(margin.Left, 4, margin.Right, margin.Bottom);
            }
        }

        private void UpdateFocusStrip()
        {
            var strip = this.FindControl<Grid>("FocusStrip");
            var title = this.FindControl<TextBlock>("FocusStripTitle");
            if (strip == null || title == null) return;

            strip.IsVisible = _isFocusMode;
            if (!_isFocusMode) return;

            var tabs = this.FindControl<TabControl>("Tabs");
            title.Text = tabs?.SelectedItem is TabItem selected ? GetTabHeaderText(selected) : "";
            title.Foreground = TitleBarContrastForeground();

            double left = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? MacOsTrafficLightReserve : 10;
            double titleBarRight = this.FindControl<Grid>("TitleBar")?.Margin.Right ?? 0;
            // The extra 40 clears the agent light, which sits at the right end of the title bar.
            strip.Margin = new Thickness(left, 0, titleBarRight + 40, 0);
        }
    }
}
