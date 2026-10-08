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
        /// Which multiplexer owns <see cref="MuxSessionId"/> (Phase 4 spec §5): <c>local</c>, or
        /// <c>ssh:&lt;sshProfileId&gt;</c> with the profile id as a Guid in <c>N</c> format. <c>null</c>
        /// (and, from files written before Phase 4, the local daemon's pipe or socket name) means
        /// local. Stored per pane rather than per session file so a window can hold panes from more
        /// than one daemon.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MuxEndpoint { get; set; }

        /// <summary>
        /// True when the pane joined <see cref="MuxSessionId"/> as a deliberate share (Phase 4 spec §4,
        /// carry-over 3): the next launch attaches shared too, rather than exclusively and losing the
        /// race to the window that still holds it. Omitted when false, so older files load unchanged.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool MuxShared { get; set; }

        /// <summary>
        /// True when <see cref="MuxSessionId"/> (a local daemon session) was saved before a reboot or logoff that ended
        /// it (spec R2): the pane starts its fresh shell without a "previous session lost" notice. Kept with the id
        /// while the pane has not spawned, so a later launch in the same boot - whose file is newer than the boot -
        /// still opens it quietly. Omitted when false, so older files load unchanged.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool MuxQuietPreviousLost { get; set; }
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
