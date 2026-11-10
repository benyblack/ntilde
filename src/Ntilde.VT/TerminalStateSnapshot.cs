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
    /// Two things are deliberately excluded, and a reader who finds them missing should read
    /// this rather than assume an oversight.
    ///
    /// <b>Bitmap handles</b>: handles are decoder-owned and not transferable. Opted-in persistent
    /// sessions carry bounded SIXEL source and placement, and the bytes Kitty and iTerm2 images were
    /// sent as (#838), instead of pixels; the importer decodes them again.
    ///
    /// <b>The OSC 133 shell-integration marks</b> (<c>TerminalBuffer</c>'s tracked command-start
    /// and command-output-start marks, and the accepting-input flag). A mark is anchored by a
    /// <c>ScrollbackPages.Generation</c> value, which is a process-local epoch: the number means
    /// nothing in the importing process, so a transferred mark would resolve to an arbitrary row
    /// or to none. They are also the one piece of excluded state that repairs itself - the next
    /// <c>OSC 133;B</c> the shell emits re-arms them, which for an interactive shell is the next
    /// prompt. Carrying a process-local anchor to buy back less than one prompt of accuracy is
    /// the wrong trade, so <see cref="TerminalBuffer.ImportState"/> clears them instead.
    /// </remarks>
    public sealed class TerminalStateSnapshot
    {
        /// <summary>Atomic OSC 66 rectangles. Main coordinates include the exported scrollback.</summary>
        [JsonPropertyName("text_sizing"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<TerminalTextSizingState>? TextSizing { get; set; }

        /// <summary>Bump when a field's meaning changes, not when one is added.</summary>
        public const int CurrentVersion = 1;

        /// <summary>Absent/zero is legacy. Required semantics are checked before mutation.</summary>
        [JsonPropertyName("required_sixel_raster"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int RequiredSixelRasterPolicy { get; set; }

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

        /// <summary>DECSLRM left margin, written only while narrower than the page; absent means the first column.</summary>
        [JsonPropertyName("lr_left"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? LeftMargin { get; set; }

        /// <summary>DECSLRM right margin, written only while narrower than the page; absent means the last column.</summary>
        [JsonPropertyName("lr_right"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? RightMargin { get; set; }
        [JsonPropertyName("tabs")] public bool[] TabStops { get; set; } = [];

        [JsonPropertyName("saved_main")] public TerminalCursorState SavedCursorMain { get; set; } = new();
        [JsonPropertyName("saved_alt")] public TerminalCursorState SavedCursorAlt { get; set; } = new();
        [JsonPropertyName("screen_main")] public TerminalCursorState ScreenCursorMain { get; set; } = new();
        [JsonPropertyName("screen_alt")] public TerminalCursorState ScreenCursorAlt { get; set; } = new();
        [JsonPropertyName("restore_main_on_alt_exit")] public bool RestoreMainCursorOnAltExit { get; set; }

        /// <summary>Stream-owned color overrides and bounded stack; absent legacy state resets both.</summary>
        [JsonPropertyName("colors"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TerminalRuntimeColors? Colors { get; set; }

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

        /// <summary>
        /// Every underline style and color an exported cell carries (#270), one entry per distinct value.
        /// Cells carry an index into it, and the cells naming one index get one instance back.
        /// </summary>
        [JsonPropertyName("underlines")] public List<TerminalUnderlineEntry> Underlines { get; set; } = [];

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

        /// <summary>Bounded SIXEL source and placement; absent on legacy or graphics-free snapshots.</summary>
        [JsonPropertyName("sixels"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<TerminalSixelImageState>? Sixels { get; set; }

        /// <summary>
        /// Retained Kitty images (#838): the bytes as sent and how, on either screen, whether placed or
        /// only transmitted. Absent where the exporter does not retain image sources.
        /// </summary>
        [JsonPropertyName("kitty_images"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<TerminalKittyImageState>? KittyImages { get; set; }

        /// <summary>Placements of <see cref="KittyImages"/>, each naming its image by index.</summary>
        [JsonPropertyName("kitty_placements"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<TerminalKittyPlacementState>? KittyPlacements { get; set; }

        /// <summary>
        /// The Kitty image-id allocator (the last id handed to an <c>I=</c>-only transmission), so ids
        /// allocated after a restore match the ones the source would have allocated.
        /// </summary>
        [JsonPropertyName("kitty_next_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public uint KittyNextImageId { get; set; }

        /// <summary>Retained iTerm2 inline images (#838) and their placements.</summary>
        [JsonPropertyName("iterm_images"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<TerminalITermImageState>? ITermImages { get; set; }

    }

    /// <summary>A retained Kitty image: what was sent, and the size its header declared.</summary>
    public sealed class TerminalKittyImageState
    {
        [JsonPropertyName("id")] public uint Id { get; set; }
        [JsonPropertyName("number")] public uint Number { get; set; }
        [JsonPropertyName("alt")] public bool Alt { get; set; }
        /// <summary>Kitty <c>f=</c>: 24 or 32 for raw pixels, 0 or 100 for a container.</summary>
        [JsonPropertyName("format")] public int Format { get; set; }
        /// <summary>Kitty <c>o=z</c>: <see cref="Data"/> is a zlib stream.</summary>
        [JsonPropertyName("compressed")] public bool Compressed { get; set; }
        [JsonPropertyName("raw_w")] public int RawWidth { get; set; }
        [JsonPropertyName("raw_h")] public int RawHeight { get; set; }
        /// <summary>Pixel size from the header (or <c>s=</c>/<c>v=</c>); checked again on import.</summary>
        [JsonPropertyName("w")] public int Width { get; set; }
        [JsonPropertyName("h")] public int Height { get; set; }
        /// <summary>
        /// The bytes as sent; empty when the source budget evicted them. The image still exists, holding its cells and
        /// drawing nothing, so it is carried anyway: a later placement must find it on every side.
        /// </summary>
        [JsonPropertyName("data")] public byte[] Data { get; set; } = [];
        /// <summary>Position in the source budget's eviction order (oldest first); absent when unknown.</summary>
        [JsonPropertyName("src"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? SourceOrder { get; set; }
    }

    /// <summary>A placement of a retained Kitty image, in the same coordinates as SIXEL placements.</summary>
    public sealed class TerminalKittyPlacementState
    {
        /// <summary>Index into <see cref="TerminalStateSnapshot.KittyImages"/>.</summary>
        [JsonPropertyName("image")] public int Image { get; set; }
        /// <summary>Kitty <c>p=</c>; 0 when the placement has none.</summary>
        [JsonPropertyName("placement")] public uint Placement { get; set; }
        [JsonPropertyName("x")] public int X { get; set; }
        [JsonPropertyName("y")] public int Y { get; set; }
        [JsonPropertyName("cw")] public int Width { get; set; }
        [JsonPropertyName("ch")] public int Height { get; set; }
        [JsonPropertyName("top")] public int ClipTop { get; set; }
        [JsonPropertyName("bottom")] public int ClipBottom { get; set; }
        [JsonPropertyName("right")] public int ClipRight { get; set; }
        [JsonPropertyName("preserve")] public bool PreserveAspectRatio { get; set; }
        /// <summary>
        /// Position among all exported placements (SIXEL, Kitty, iTerm2), so a restore keeps which image
        /// is drawn over which; absent on older snapshots.
        /// </summary>
        [JsonPropertyName("z"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Order { get; set; }
    }

    /// <summary>A retained iTerm2 inline image and its placement.</summary>
    public sealed class TerminalITermImageState
    {
        /// <summary>The bytes as sent; empty when the source budget evicted them (the placement still holds its cells).</summary>
        [JsonPropertyName("data")] public byte[] Data { get; set; } = [];
        /// <summary>Position in the source budget's eviction order (oldest first); absent when unknown.</summary>
        [JsonPropertyName("src"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? SourceOrder { get; set; }

        /// <summary>Pixel size from the header; checked again on import.</summary>
        [JsonPropertyName("w")] public int PixelWidth { get; set; }
        [JsonPropertyName("h")] public int PixelHeight { get; set; }
        [JsonPropertyName("x")] public int X { get; set; }
        [JsonPropertyName("y")] public int Y { get; set; }
        [JsonPropertyName("cw")] public int Width { get; set; }
        [JsonPropertyName("ch")] public int Height { get; set; }
        [JsonPropertyName("alt")] public bool Alt { get; set; }
        [JsonPropertyName("top")] public int ClipTop { get; set; }
        [JsonPropertyName("bottom")] public int ClipBottom { get; set; }
        [JsonPropertyName("right")] public int ClipRight { get; set; }
        [JsonPropertyName("preserve")] public bool PreserveAspectRatio { get; set; }
        /// <summary>
        /// Position among all exported placements (SIXEL, Kitty, iTerm2), so a restore keeps which image
        /// is drawn over which; absent on older snapshots.
        /// </summary>
        [JsonPropertyName("z"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Order { get; set; }
    }

    public sealed class TerminalSixelImageState
    {
        /// <summary>Absent legacy placements retain whole-cell sizing. Native imports remain disabled.</summary>
        [JsonPropertyName("native_raster"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SixelRasterIntent? NativeRaster { get; set; }

        [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;
        [JsonPropertyName("x")] public int X { get; set; }
        [JsonPropertyName("y")] public int Y { get; set; }
        [JsonPropertyName("w")] public int Width { get; set; }
        [JsonPropertyName("h")] public int Height { get; set; }
        [JsonPropertyName("alt")] public bool Alt { get; set; }
        [JsonPropertyName("top")] public int ClipTop { get; set; }
        [JsonPropertyName("bottom")] public int ClipBottom { get; set; }
        [JsonPropertyName("right")] public int ClipRight { get; set; }
        /// <summary>
        /// Position among all exported placements (SIXEL, Kitty, iTerm2), so a restore keeps which image
        /// is drawn over which; absent on older snapshots.
        /// </summary>
        [JsonPropertyName("z"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Order { get; set; }
    }

    /// <summary>One screen's (or the scrollback's) cells and per-row side tables.</summary>
    public sealed class TerminalScreenState
    {
        /// <summary>Row-major <c>TerminalCell[]</c> blob, base64 of the blittable bytes.</summary>
        [JsonPropertyName("cells")] public string? CellsBase64 { get; set; }

        [JsonPropertyName("wrap")] public bool[]? RowWraps { get; set; }

        /// <summary>Content columns of soft-wrapped rows; -1 means legacy/full row.</summary>
        [JsonPropertyName("wrap_cols")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int[]? RowWrapColumns { get; set; }

        /// <summary>Conservative authored extent per row; absent legacy data retains every column.</summary>
        [JsonPropertyName("written_cols")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int[]? RowWrittenColumns { get; set; }

        /// <summary>Extended grapheme clusters keyed by <c>row * cols + col</c>.</summary>
        [JsonPropertyName("ext")] public Dictionary<int, string>? ExtendedText { get; set; }

        /// <summary>
        /// Index into <see cref="TerminalStateSnapshot.Hyperlinks"/> keyed by
        /// <c>row * cols + col</c>.
        /// </summary>
        [JsonPropertyName("links")] public Dictionary<int, int>? LinkIds { get; set; }

        /// <summary>
        /// Index into <see cref="TerminalStateSnapshot.Underlines"/> keyed by <c>row * cols + col</c> (#270).
        /// </summary>
        [JsonPropertyName("ul")] public Dictionary<int, int>? UnderlineIds { get; set; }

        /// <summary>DECSCA-protected cells as ascending <c>row * cols + col</c> keys; absent when none.</summary>
        [JsonPropertyName("prot"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int[]? ProtectedCells { get; set; }
    }

    /// <summary>A DECSC-style saved cursor, mirroring <see cref="CursorState"/>.</summary>
    public sealed class TerminalCursorState
    {
        [JsonPropertyName("decom"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? OriginMode { get; set; }
        [JsonPropertyName("gsets"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int[]? Charsets { get; set; }
        [JsonPropertyName("gl")] public int Gl { get; set; }
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
        [JsonPropertyName("ol"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool IsOverline { get; set; }

        /// <summary>DECSCA protection of newly written characters; absent (false) in snapshots made before it.</summary>
        [JsonPropertyName("dsca"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool IsCharacterProtected { get; set; }

        /// <summary>The underline's style (#270), an <c>UnderlineStyle</c> value; 1 (straight) when absent.</summary>
        [JsonPropertyName("uls")] public byte UnderlineStyle { get; set; } = 1;

        /// <summary>SGR 58's RGB underline color, as <c>TermColor.ToUint</c> gives it, or null.</summary>
        [JsonPropertyName("ulc")] public uint? UnderlineColor { get; set; }

        /// <summary>SGR 58's palette underline color, or -1.</summary>
        [JsonPropertyName("uli")] public short UnderlineColorIndex { get; set; } = -1;
        [JsonPropertyName("blink")] public bool IsBlink { get; set; }
        [JsonPropertyName("strike")] public bool IsStrikethrough { get; set; }
        [JsonPropertyName("hidden")] public bool IsHidden { get; set; }
    }

    /// <summary>
    /// Every property of <see cref="ModeState"/> except the kitty stacks and the host-owned
    /// <see cref="ModeState.AlternateScrollDefault"/> (modifyOtherKeys included).
    /// </summary>
    public sealed class TerminalModeState
    {
        [JsonPropertyName("m_9")] public bool MouseModeX10PressOnly { get; set; }
        [JsonPropertyName("m_x10")] public bool MouseModeX10 { get; set; }
        [JsonPropertyName("m_btn")] public bool MouseModeButtonEvent { get; set; }
        [JsonPropertyName("m_any")] public bool MouseModeAnyEvent { get; set; }
        [JsonPropertyName("m_sgr")] public bool MouseModeSGR { get; set; }
        [JsonPropertyName("ckm")] public bool IsApplicationCursorKeys { get; set; }
        [JsonPropertyName("kap")] public bool IsApplicationKeypad { get; set; }
        [JsonPropertyName("awm")] public bool IsAutoWrapMode { get; set; } = true;
        /// <summary>Reverse-wraparound (?45). Absent in older payloads, which reads as off.</summary>
        [JsonPropertyName("rwm")] public bool IsReverseWrapMode { get; set; }
        [JsonPropertyName("decom")] public bool IsOriginMode { get; set; }
        /// <summary>DECSCNM (?5). Absent in older payloads, which reads as normal video.</summary>
        [JsonPropertyName("decscnm")] public bool IsReverseVideo { get; set; }
        [JsonPropertyName("sixel_display")] public bool IsSixelDisplayMode { get; set; }
        [JsonPropertyName("focus")] public bool IsFocusEventReporting { get; set; }
        [JsonPropertyName("bp")] public bool IsBracketedPasteMode { get; set; }
        [JsonPropertyName("mime_paste")] public bool IsClipboardPasteMode { get; set; }
        [JsonPropertyName("cv")] public bool IsCursorVisible { get; set; } = true;
        [JsonPropertyName("cblink")] public bool IsCursorBlinkEnabled { get; set; } = true;
        [JsonPropertyName("cstyle")] public int CursorStyle { get; set; }
        [JsonPropertyName("cdefault"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? CursorStyleIsDefault { get; set; }
        [JsonPropertyName("irm")] public bool IsInsertMode { get; set; }
        [JsonPropertyName("lnm")] public bool IsLineFeedNewLineMode { get; set; }
        [JsonPropertyName("srm")] public bool IsEchoEnabled { get; set; } = true;

        /// <summary>
        /// xterm modifyOtherKeys level of the main screen (0-2). Optional: absent from legacy
        /// peers, which never negotiated it, and imported as 0. Which screen's level is live
        /// follows <see cref="TerminalStateSnapshot.IsAltScreenActive"/>.
        /// </summary>
        [JsonPropertyName("mok_main"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? ModifyOtherKeysMain { get; set; }

        /// <summary>xterm modifyOtherKeys level of the alternate screen (0-2); see <see cref="ModifyOtherKeysMain"/>.</summary>
        [JsonPropertyName("mok_alt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? ModifyOtherKeysAlt { get; set; }

        /// <summary>
        /// ?1007 as the application set it; null (absent) means "never set since RIS", so the
        /// importing host's own default applies. Legacy payloads lack it and read as null.
        /// </summary>
        [JsonPropertyName("alt_scroll"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? AlternateScrollOverride { get; set; }

        /// <summary>
        /// DECSACE rectangle extent (Ps 2). Written only while the rectangle is selected; absent means the stream default,
        /// which is what every legacy payload read as.
        /// </summary>
        [JsonPropertyName("decsace_rect"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? RectangularAttributeExtent { get; set; }

        /// <summary>DECLRMM (?69). Written only while set; absent means reset, which is what every legacy payload read as.</summary>
        [JsonPropertyName("declrmm"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? LeftRightMarginMode { get; set; }
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

    /// <summary>One sparse underline/overline decoration, as a <c>CellUnderline</c> holds it.</summary>
    public sealed class TerminalUnderlineEntry
    {
        [JsonPropertyName("s")] public byte Style { get; set; } = 1;
        [JsonPropertyName("ol"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Overline { get; set; }
        [JsonPropertyName("rgb")] public uint? Color { get; set; }
        [JsonPropertyName("idx")] public short ColorIndex { get; set; } = -1;
    }

    /// <summary>One OSC 8 link identity: the (URI, id) pair that defines it.</summary>
    public sealed class TerminalHyperlinkEntry
    {
        [JsonPropertyName("uri")] public string Uri { get; set; } = string.Empty;
        [JsonPropertyName("id")] public string? Id { get; set; }
    }

    [JsonSourceGenerationOptions(WriteIndented = false, AllowDuplicateProperties = false)]
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
