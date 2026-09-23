using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Ntilde.Pty
{
    public class NtildeSession
    {
        public int ActiveTabIndex { get; set; } = 0;
        public List<TabSession> Tabs { get; set; } = new();
    }

    public class TabSession
    {
        public string? TabId { get; set; }
        public string Title { get; set; } = "Terminal";
        public string? UserTitle { get; set; }
        public bool IsPinned { get; set; }
        public bool IsProtected { get; set; }
        public PaneNode? Root { get; set; } // The root of the layout tree
        public string? ActivePaneId { get; set; }
        public string? ZoomedPaneId { get; set; }
        public bool BroadcastInputEnabled { get; set; }
    }

    public enum NodeType
    {
        Leaf,
        Split
    }

    public class PaneNode
    {
        public NodeType Type { get; set; }

        // For Splits
        public int SplitOrientation { get; set; } // 0=Horizontal, 1=Vertical
        public List<PaneNode> Children { get; set; } = new();
        public List<string> Sizes { get; set; } = new(); // "1*", "100px" etc.

        // For Leafs
        public string? ProfileId { get; set; }
        public string? SshProfileId { get; set; }
        public string? PaneId { get; set; }

        // Fallbacks for ad-hoc panes (no profile)
        public string? Command { get; set; }
        public string? Arguments { get; set; }

        // ── Multiplexer attach coordinates (Phase 0: persisted, not yet read) ─────
        //
        // Nullable and omitted when null, because every session.json written before the
        // multiplexer exists has to keep loading. Nothing consumes these yet; they land now
        // so the persisted shape is settled before the daemon needs it.

        /// <summary>
        /// Id of the multiplexer session this pane attaches to, or <c>null</c> for a pane that
        /// owns its PTY directly (every pane today).
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MuxSessionId { get; set; }

        /// <summary>
        /// Transport address of the multiplexer that owns <see cref="MuxSessionId"/>, or
        /// <c>null</c>. Stored per pane rather than per session file so a window can hold panes
        /// from more than one daemon.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MuxEndpoint { get; set; }
    }

    public class WorkspaceBundlePackage
    {
        public int Version { get; set; } = 1;
        public string WorkspaceName { get; set; } = "workspace";
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public string? ExportedBy { get; set; }
        public string PayloadJson { get; set; } = string.Empty;
        public string PayloadHashSha256 { get; set; } = string.Empty;
    }
}
