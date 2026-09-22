using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ntilde.VT
{
    /// <summary>
    /// Complete, transferable terminal state: enough for a second
    /// <see cref="TerminalBuffer"/> + <see cref="AnsiParser"/> to be dropped into a running
    /// session's position and see every subsequent byte the same way the original does.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="ReplaySnapshot"/>, which stays exactly as it is so recorded
    /// replay files keep working. That one covers the active viewport and a handful of modes,
    /// which is right for "redraw what the screen looked like"; this one covers the inactive
    /// screen, scrollback, tab stops, saved cursors, synchronized output, the kitty keyboard
    /// stacks, hyperlink identity and the parser's own position, which is what "resume applying
    /// bytes" needs.
    ///
    /// Inline images are deliberately excluded: the handles are decoder-owned and not
    /// transferable, and carrying the pixels would dominate the payload.
    /// </remarks>
    public sealed class TerminalStateSnapshot
    {
        /// <summary>Bump when a field's meaning changes, not when one is added.</summary>
        public const int CurrentVersion = 1;

        [JsonPropertyName("v")] public int Version { get; set; } = CurrentVersion;
        [JsonPropertyName("cols")] public int Cols { get; set; }
        [JsonPropertyName("rows")] public int Rows { get; set; }

        /// <summary><c>Unsafe.SizeOf&lt;TerminalCell&gt;()</c> at export, validated on import.</summary>
        [JsonPropertyName("cells_sizeof")] public int CellsSizeOf { get; set; }

        /// <summary><c>TerminalCell.TerminalCellLayoutId</c> at export, validated on import.</summary>
        [JsonPropertyName("cells_layout_id")] public string? CellsLayoutId { get; set; }

        [JsonPropertyName("main")] public TerminalScreenState Main { get; set; } = new();
        [JsonPropertyName("alt")] public TerminalScreenState Alt { get; set; } = new();
        [JsonPropertyName("alt_active")] public bool IsAltScreenActive { get; set; }

        [JsonPropertyName("sb")] public TerminalScreenState Scrollback { get; set; } = new();
        [JsonPropertyName("sb_rows")] public int ScrollbackRowCount { get; set; }

        /// <summary>The export hit its row cap, so older rows are absent.</summary>
        [JsonPropertyName("sb_trunc")] public bool ScrollbackTruncated { get; set; }

        /// <summary>How many of the oldest scrollback rows the cap dropped.</summary>
        [JsonPropertyName("sb_dropped")] public int ScrollbackRowsDropped { get; set; }

        [JsonPropertyName("cx")] public int CursorCol { get; set; }
        [JsonPropertyName("cy")] public int CursorRow { get; set; }
        [JsonPropertyName("pw")] public bool IsPendingWrap { get; set; }
        [JsonPropertyName("sr_top")] public int ScrollTop { get; set; }
        [JsonPropertyName("sr_bottom")] public int ScrollBottom { get; set; }
        [JsonPropertyName("tabs")] public bool[] TabStops { get; set; } = [];

        [JsonPropertyName("saved_main")] public TerminalCursorState SavedCursorMain { get; set; } = new();
        [JsonPropertyName("saved_alt")] public TerminalCursorState SavedCursorAlt { get; set; } = new();
        [JsonPropertyName("screen_main")] public TerminalCursorState ScreenCursorMain { get; set; } = new();
        [JsonPropertyName("screen_alt")] public TerminalCursorState ScreenCursorAlt { get; set; } = new();
        [JsonPropertyName("restore_main_on_alt_exit")] public bool RestoreMainCursorOnAltExit { get; set; }

        [JsonPropertyName("sgr")] public TerminalSgrState Sgr { get; set; } = new();
        [JsonPropertyName("modes")] public TerminalModeState Modes { get; set; } = new();
        [JsonPropertyName("kitty_kbd")] public TerminalKittyKeyboardState KittyKeyboard { get; set; } = new();

        [JsonPropertyName("sync")] public bool IsSynchronizedOutput { get; set; }

        /// <summary><c>DateTime.UtcNow.Ticks</c> at the last BSU; drives the sync-output timeout.</summary>
        [JsonPropertyName("sync_start_ticks")] public long LastSyncStartUtcTicks { get; set; }

        [JsonPropertyName("grapheme")] public TerminalGraphemeState Grapheme { get; set; } = new();

        /// <summary>
        /// Every hyperlink any exported cell points at. Cells carry an index into this list, which
        /// is how OSC 8's reference-equality identity survives the wire: two cells with the same
        /// index are re-attached to the same instance on import.
        /// </summary>
        [JsonPropertyName("links")] public List<TerminalHyperlinkEntry> Hyperlinks { get; set; } = [];

        /// <summary>Index into <see cref="Hyperlinks"/> of the link the cursor is writing under, or -1.</summary>
        [JsonPropertyName("cur_link")] public int CurrentHyperlinkId { get; set; } = -1;

        /// <summary>The parser's position in the stream. Applied with <c>AnsiParser.ImportState</c>.</summary>
        [JsonPropertyName("parser")] public AnsiParserState Parser { get; set; } = new();

        /// <summary>
        /// The partial UTF-8 code point the source's decoder was holding when this was taken;
        /// feed it to the new decoder before the tail bytes. See <c>Utf8ChunkDecoder.PendingTail</c>.
        /// </summary>
        [JsonPropertyName("tail")] public byte[]? DecoderTail { get; set; }

        /// <summary>
        /// Byte offset into the session's output stream that this snapshot corresponds to:
        /// <c>Utf8ChunkDecoder.ConsumedBytes</c>. The next unseen byte is at
        /// <c>StreamSeq + (DecoderTail?.Length ?? 0)</c>.
        /// </summary>
        [JsonPropertyName("seq")] public long StreamSeq { get; set; }
    }

    /// <summary>One screen's (or the scrollback's) cells and per-row side tables.</summary>
    public sealed class TerminalScreenState
    {
        /// <summary>Row-major <c>TerminalCell[]</c> blob, base64 of the blittable bytes.</summary>
        [JsonPropertyName("cells")] public string? CellsBase64 { get; set; }

        [JsonPropertyName("wrap")] public bool[]? RowWraps { get; set; }

        /// <summary>Extended grapheme clusters keyed by <c>row * cols + col</c>.</summary>
        [JsonPropertyName("ext")] public Dictionary<int, string>? ExtendedText { get; set; }

        /// <summary>
        /// Index into <see cref="TerminalStateSnapshot.Hyperlinks"/> keyed by
        /// <c>row * cols + col</c>.
        /// </summary>
        [JsonPropertyName("links")] public Dictionary<int, int>? LinkIds { get; set; }
    }

    /// <summary>A DECSC-style saved cursor, mirroring <see cref="CursorState"/>.</summary>
    public sealed class TerminalCursorState
    {
        [JsonPropertyName("row")] public int Row { get; set; }
        [JsonPropertyName("col")] public int Col { get; set; }
        [JsonPropertyName("sgr")] public TerminalSgrState Sgr { get; set; } = new();
        [JsonPropertyName("pw")] public bool IsPendingWrap { get; set; }
    }

    /// <summary>The SGR attribute set, shared by the live style and the saved cursors.</summary>
    public sealed class TerminalSgrState
    {
        [JsonPropertyName("fg")] public uint Foreground { get; set; }
        [JsonPropertyName("bg")] public uint Background { get; set; }
        [JsonPropertyName("fgi")] public short FgIndex { get; set; } = -1;
        [JsonPropertyName("bgi")] public short BgIndex { get; set; } = -1;
        [JsonPropertyName("dfg")] public bool IsDefaultForeground { get; set; } = true;
        [JsonPropertyName("dbg")] public bool IsDefaultBackground { get; set; } = true;
        [JsonPropertyName("inv")] public bool IsInverse { get; set; }
        [JsonPropertyName("bold")] public bool IsBold { get; set; }
        [JsonPropertyName("faint")] public bool IsFaint { get; set; }
        [JsonPropertyName("italic")] public bool IsItalic { get; set; }
        [JsonPropertyName("ul")] public bool IsUnderline { get; set; }
        [JsonPropertyName("blink")] public bool IsBlink { get; set; }
        [JsonPropertyName("strike")] public bool IsStrikethrough { get; set; }
        [JsonPropertyName("hidden")] public bool IsHidden { get; set; }
    }

    /// <summary>Every property of <see cref="ModeState"/> except the kitty stacks.</summary>
    public sealed class TerminalModeState
    {
        [JsonPropertyName("m_x10")] public bool MouseModeX10 { get; set; }
        [JsonPropertyName("m_btn")] public bool MouseModeButtonEvent { get; set; }
        [JsonPropertyName("m_any")] public bool MouseModeAnyEvent { get; set; }
        [JsonPropertyName("m_sgr")] public bool MouseModeSGR { get; set; }
        [JsonPropertyName("ckm")] public bool IsApplicationCursorKeys { get; set; }
        [JsonPropertyName("awm")] public bool IsAutoWrapMode { get; set; } = true;
        [JsonPropertyName("decom")] public bool IsOriginMode { get; set; }
        [JsonPropertyName("focus")] public bool IsFocusEventReporting { get; set; }
        [JsonPropertyName("bp")] public bool IsBracketedPasteMode { get; set; }
        [JsonPropertyName("cv")] public bool IsCursorVisible { get; set; } = true;
        [JsonPropertyName("cblink")] public bool IsCursorBlinkEnabled { get; set; } = true;
        [JsonPropertyName("cstyle")] public int CursorStyle { get; set; }
        [JsonPropertyName("irm")] public bool IsInsertMode { get; set; }
        [JsonPropertyName("lnm")] public bool IsLineFeedNewLineMode { get; set; }
        [JsonPropertyName("srm")] public bool IsEchoEnabled { get; set; } = true;
    }

    /// <summary>Both kitty keyboard flag stacks and which one is live.</summary>
    public sealed class TerminalKittyKeyboardState
    {
        [JsonPropertyName("main")] public int[] MainStack { get; set; } = [];
        [JsonPropertyName("alt")] public int[] AltStack { get; set; } = [];
        [JsonPropertyName("alt_active")] public bool IsAltScreenActive { get; set; }
    }

    /// <summary>The grapheme-continuation state the write path consults for the next char.</summary>
    public sealed class TerminalGraphemeState
    {
        /// <summary>A lone high surrogate awaiting its pair, or null.</summary>
        [JsonPropertyName("hi")] public string? HighSurrogate { get; set; }

        [JsonPropertyName("col")] public int LastCharCol { get; set; } = -1;
        [JsonPropertyName("row")] public int LastCharRow { get; set; } = -1;
        [JsonPropertyName("zwj")] public bool IsAfterZwj { get; set; }
    }

    /// <summary>One OSC 8 link identity: the (URI, id) pair that defines it.</summary>
    public sealed class TerminalHyperlinkEntry
    {
        [JsonPropertyName("uri")] public string Uri { get; set; } = string.Empty;
        [JsonPropertyName("id")] public string? Id { get; set; }
    }

    [JsonSourceGenerationOptions(WriteIndented = false)]
    [JsonSerializable(typeof(TerminalStateSnapshot))]
    public partial class TerminalStateJsonContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// JSON envelope for <see cref="TerminalStateSnapshot"/>, with the cell blobs base64 inside -
    /// the same shape <see cref="ReplaySnapshot"/> uses, so one reader can handle both.
    /// </summary>
    /// <remarks>
    /// Source-generated rather than reflection-based because <c>Ntilde.App</c> publishes Native
    /// AOT with <c>IL2026</c> and <c>IL3050</c> as errors, and this type is reachable from there.
    /// </remarks>
    public static class TerminalStateSerializer
    {
        public static byte[] ToBytes(TerminalStateSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            return JsonSerializer.SerializeToUtf8Bytes(
                snapshot, TerminalStateJsonContext.Default.TerminalStateSnapshot);
        }

        public static TerminalStateSnapshot FromBytes(ReadOnlySpan<byte> bytes)
        {
            TerminalStateSnapshot? snapshot = JsonSerializer.Deserialize(
                bytes, TerminalStateJsonContext.Default.TerminalStateSnapshot);

            return snapshot ?? throw new InvalidOperationException(
                "Terminal state payload deserialized to null; the envelope is not a TerminalStateSnapshot.");
        }
    }
}
