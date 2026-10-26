using Avalonia.Controls;
using Ntilde.Shell;

namespace Ntilde
{
    /// <summary>
    /// The text of a tab's label: its titles, truncation, and the attention, agent and shared markers.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>Tab-label marker for "an agent typed into a pane in this tab".</summary>
        internal const string AgentWroteGlyph = "\u2328";  // keyboard

        /// <summary>Tab-label marker for "an agent is reading a pane in this tab".</summary>
        internal const string AgentWatchedGlyph = "\U0001F441";  // eye

        /// <summary>Tab-label marker for "a pane in this tab is attached from another window too".</summary>
        internal const string SharedGlyph = "\u29C9";  // two joined squares: one shell, several windows

        internal static string TruncateTabLabel(string value, int maxLength = 40)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
            if (maxLength < 5) return value.Substring(0, maxLength);
            return value.Substring(0, maxLength - 1) + "\u2026";
        }

        internal static string TruncateTabLabelWithSuffix(string value, int maxLength, string suffix)
        {
            if (string.IsNullOrEmpty(suffix))
            {
                return TruncateTabLabel(value, maxLength);
            }

            if (maxLength <= suffix.Length)
            {
                return suffix.Substring(0, maxLength);
            }

            int available = maxLength - suffix.Length;
            string prefix = value;
            if (prefix.Length > available)
            {
                prefix = available < 5
                    ? prefix.Substring(0, available)
                    : prefix.Substring(0, available - 1) + "\u2026";
            }

            return prefix + suffix;
        }

        private string GetTabPrimaryTitle(TabItem tab)
        {
            var state = GetOrCreateTabState(tab);
            var pane = ResolvePaneForTab(tab);
            return ResolveTabPrimaryTitle(state.UserTitle, pane?.GetBaseTabTitle(), null);
        }

        internal static string ResolveTabPrimaryTitle(string? userTitle, string? paneBaseTitle, string? fallbackHeader)
        {
            if (!string.IsNullOrWhiteSpace(userTitle))
            {
                return userTitle;
            }

            if (!string.IsNullOrWhiteSpace(paneBaseTitle))
            {
                return paneBaseTitle;
            }

            if (!string.IsNullOrWhiteSpace(fallbackHeader))
            {
                return fallbackHeader;
            }

            return "Terminal";
        }

        internal string GetTabPersistedTitle(TabItem tab)
        {
            var state = GetOrCreateTabState(tab);
            var pane = ResolvePaneForTab(tab);
            return ResolveTabPrimaryTitle(state.UserTitle, pane?.GetBaseTabTitle(), GetTabHeaderText(tab));
        }

        /// <summary>
        /// The tab label without its trailing attention marker (bell, activity,
        /// or agent tier): primary title, forwarding badge, and the pinned/
        /// protected prefixes. Split out from <see cref="BuildFullTabLabel"/>
        /// so <see cref="BuildTabDisplayLabels"/> can truncate this part alone
        /// and append the marker afterwards — the marker must never land in
        /// the truncated region, or it silently disappears from a long tab's
        /// visible header (it would still show in the tooltip, since that
        /// reads the untruncated <see cref="BuildFullTabLabel"/> result, but
        /// the always-visible header text is the surface that matters).
        /// </summary>
        private string BuildBaseTabLabel(TabItem tab)
        {
            var state = GetOrCreateTabState(tab);
            var pane = ResolvePaneForTab(tab);
            string label = GetTabPrimaryTitle(tab);

            if (pane?.Profile != null)
            {
                var forwards = pane.Profile.Forwards;
                int activeCount = forwards.Count(f => f.Status == ForwardingStatus.Active);
                int startingCount = forwards.Count(f => f.Status == ForwardingStatus.Starting);
                bool hasFailed = forwards.Any(f => f.Status == ForwardingStatus.Failed);

                if (activeCount > 0 || startingCount > 0)
                {
                    string badge = activeCount.ToString();
                    if (startingCount > 0) badge += $" ({startingCount})";
                    label = $"{label} \U0001F501 {badge}";
                }
                else if (hasFailed)
                {
                    label = $"{label} \u26A0\uFE0F";
                }
            }

            if (state.IsPinned)
            {
                label = "\U0001F4CC " + label;
            }
            if (state.IsProtected)
            {
                label = "\U0001F512 " + label;
            }

            return label;
        }

        /// <summary>
        /// The trailing attention-marker suffix for a tab: bell and activity
        /// are mutually exclusive (a bell always wins over mere activity), and
        /// the agent tier is independent of both and can accompany either —
        /// preserving exactly the precedence and combination rules the old,
        /// inline version of this logic had in <c>BuildFullTabLabel</c>.
        /// </summary>
        private string GetAttentionMarkerSuffix(TabRuntimeState state)
        {
            string suffix = string.Empty;

            if (state.HasBell)
            {
                suffix += " \U0001F514";
            }
            else if (state.HasActivity)
            {
                suffix += " \u2022";
            }

            if (state.AgentTier == AgentHost.AgentAttentionTier.Wrote)
            {
                suffix += " " + AgentWroteGlyph;
            }
            else if (state.AgentTier == AgentHost.AgentAttentionTier.Watched)
            {
                suffix += " " + AgentWatchedGlyph;
            }

            if (state.IsShared) suffix += " " + SharedGlyph;
            // OSC 9;4 progress (#271) rides the same trailing-suffix contract in
            // horizontal mode (" 42%", " ✖ 42%", " ⋯"); vertical mode renders it as the
            // header's thin bar instead (UpdateVerticalTabExtras). Gated at render time
            // so the setting hides existing reports without discarding them — the stored
            // report survives and reappears when the toggle comes back on.
            suffix += TabProgressPresentation.FormatMarkerSuffix(
                _settings.Osc9ProgressReportingEnabled ? state.Progress : null);

            return suffix;
        }

        private string BuildFullTabLabel(TabItem tab)
        {
            var state = GetOrCreateTabState(tab);
            return BuildBaseTabLabel(tab) + GetAttentionMarkerSuffix(state);
        }

        /// <summary>
        /// Truncates every tab's label to <paramref name="maxLength"/> for the
        /// visible header, keeping the attention marker (bell/activity/agent)
        /// out of the truncated region by treating it as a suffix reserved
        /// ahead of time — the same mechanism <see cref="TruncateTabLabelWithSuffix"/>
        /// already used for the collision-disambiguation hint below, just
        /// applied unconditionally instead of only once tabs collide.
        ///
        /// When a truncated label collides with another tab's, the
        /// disambiguation hint is appended *after* the marker rather than
        /// before it: the marker is content about the tab's own live state
        /// (matches its position in the untruncated <see cref="BuildFullTabLabel"/>,
        /// immediately after the title), while the hint is a synthetic
        /// disambiguator bolted on only when two tabs would otherwise render
        /// identically. Putting the hint last also means the one case that
        /// forces both to compete for space (an extremely small maxLength)
        /// degrades by dropping hint characters before marker characters —
        /// <see cref="TruncateTabLabelWithSuffix"/>'s degenerate-case branch
        /// returns the front of the combined suffix, and the marker occupies
        /// the front.
        ///
        /// <paramref name="includeMarkers"/> false is the vertical-header mode:
        /// attention renders as trailing chips there (UpdateVerticalTabExtras),
        /// so the marker suffix is neither appended nor reserved during
        /// truncation — the prefixes (pinned/protected/forwarding) live in the
        /// base label and stay in both modes. The tooltip
        /// (<see cref="BuildFullTabLabel"/>), tab-list flyout
        /// (<see cref="GetTabMenuLabel"/>) and automation labels keep their
        /// markers regardless of mode.
        /// </summary>
        private Dictionary<TabItem, string> BuildTabDisplayLabels(IReadOnlyList<TabItem> tabs, int maxLength, bool includeMarkers = true)
            => ResolveTabDisplayLabels(
                tabs,
                t => BuildBaseTabLabel(t),
                t => GetAttentionMarkerSuffix(GetOrCreateTabState(t)),
                t => "~" + GetTabId(t).ToString("N").Substring(0, 4),
                maxLength,
                includeMarkers);

        /// <summary>
        /// Pure core of <see cref="BuildTabDisplayLabels"/>: same truncation, marker
        /// reservation, and collision-disambiguation behavior, with the per-tab string
        /// sources injected so tests can drive it as plain facts without a window (the
        /// instance method only contributes state-derived strings).
        /// </summary>
        internal static Dictionary<TTab, string> ResolveTabDisplayLabels<TTab>(
            IReadOnlyList<TTab> tabs,
            Func<TTab, string> baseLabelOf,
            Func<TTab, string> markerSuffixOf,
            Func<TTab, string> collisionHintOf,
            int maxLength,
            bool includeMarkers)
        {
            var baseLabels = tabs.ToDictionary(t => t, baseLabelOf);
            var markers = includeMarkers
                ? tabs.ToDictionary(t => t, markerSuffixOf)
                : tabs.ToDictionary(t => t, static _ => string.Empty);
            var truncated = tabs.ToDictionary(t => t, t => TruncateTabLabelWithSuffix(baseLabels[t], maxLength, markers[t]));

            var collisions = tabs
                .GroupBy(t => truncated[t], StringComparer.Ordinal)
                .Where(g => g.Count() > 1);

            foreach (var group in collisions)
            {
                foreach (var tab in group)
                {
                    truncated[tab] = TruncateTabLabelWithSuffix(baseLabels[tab], maxLength, markers[tab] + collisionHintOf(tab));
                }
            }

            return truncated;
        }
    }
}
