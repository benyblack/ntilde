using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
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

        internal void ToggleFocusMode()
        {
            if (_isFocusMode) LeaveFocusMode();
            else EnterFocusMode();
        }

        // ---- the agent light (spec section 7) ----

        private const string DefaultObserveLightTooltip = "Agent access is enabled. Click to open the agent activity journal.";
        private const string JournalHint = "Click to open the agent activity journal.";

        /// <summary>A pane an agent typed into that the user has not yet looked at, as the light names it.</summary>
        internal sealed record FocusWriteMark(string TabTitle, string? PaneTitle, DateTimeOffset? LastWriteUtc);

        /// <summary>Amber for an unseen write, blue for a read or poll, grey when idle.</summary>
        internal static string ObserveLightColor(bool active, bool unseenWrite)
            => unseenWrite ? "#E8A33D" : active ? "#4FB0D4" : "#6B737F";

        internal static string BuildFocusLightTooltip(IReadOnlyList<FocusWriteMark> marks)
        {
            static string Target(FocusWriteMark m) => m.PaneTitle is null ? m.TabTitle : $"{m.TabTitle} › {m.PaneTitle}";
            static string At(FocusWriteMark m) => m.LastWriteUtc is { } t
                ? " at " + t.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                : "";

            if (marks.Count == 0) return DefaultObserveLightTooltip;
            if (marks.Count == 1) return $"An agent typed into {Target(marks[0])}{At(marks[0])}. {JournalHint}";

            var lines = new List<string> { "Agents typed into:" };
            lines.AddRange(marks.Select(m => $"- {Target(m)}{At(m)}"));
            lines.Add(JournalHint);
            return string.Join("\n", lines);
        }

        /// <summary>
        /// Panes whose attention tier is still Wrote, in tab order. Only focus mode has a use for them:
        /// outside it the pane segment and tab badge carry the write, so this is empty.
        /// </summary>
        internal IReadOnlyList<FocusWriteMark> CollectUnseenWriteMarks()
        {
            if (!_isFocusMode) return Array.Empty<FocusWriteMark>();

            var byPane = AgentHost.AgentSessionRegistry.Instance.GetRegistrations()
                .GroupBy(r => r.PaneId)
                .ToDictionary(g => g.Key, g => g.First());
            var marks = new List<FocusWriteMark>();
            var tabs = this.FindControl<TabControl>("Tabs");
            if (tabs == null) return marks;

            foreach (var tab in tabs.Items.OfType<TabItem>())
            {
                var panes = EnumeratePanes(GetLayoutRootForTab(tab)).ToList();
                foreach (var pane in panes)
                {
                    if (!byPane.TryGetValue(pane.PaneId, out var registration)) continue;
                    var snapshot = registration.AttentionMachine.Snapshot();
                    if (snapshot.Tier != AgentHost.AgentAttentionTier.Wrote) continue;
                    marks.Add(new FocusWriteMark(
                        GetTabHeaderText(tab),
                        panes.Count > 1 ? pane.GetBaseTabTitle() : null,
                        snapshot.LastWriteUtc));
                }
            }

            return marks;
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
