using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Ntilde.VT
{
    /// <summary>
    /// Every <see cref="AnsiParser"/> field that survives a <c>Process()</c> call, so a second
    /// parser can be dropped into the first one's position mid-escape-sequence and see the same
    /// stream the same way.
    /// </summary>
    /// <remarks>
    /// Read-only configuration is deliberately absent: the image decoder, general cell metrics, default
    /// colours, the image-protocol policy (including <c>AllowITerm2Multipart</c>), the kitty kill switch, the string-sequence cap, and the ConPTY-filtering flag are
    /// host settings, not stream position. Two parsers exchanging state must be constructed with
    /// the same <c>forceConPtyFiltering</c>, because that one changes how a sequence is
    /// interpreted rather than what has been seen.
    ///
    /// The authoritative SIXEL cell-size override is included: shared-session layout must use
    /// the daemon's pixel metrics after attach, rather than a receiving view's local font size.
    /// Native/raw-only replay hosts still choose their own general cell metrics.
    ///
    /// The hyperlink registry is also absent by design: link identity is reference equality, so
    /// it travels with the buffer's hyperlink table (<see cref="TerminalStateSnapshot"/>) where
    /// cells can point at the same instances. Re-interning here would mint fresh instances for
    /// ids the table had already resolved, and adjacent cells would stop grouping.
    /// </remarks>
    public sealed class AnsiParserState
    {
        /// <summary>Authoritative SIXEL layout metrics; absent in legacy snapshots.</summary>
        [JsonPropertyName("sixel_cw"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? SixelCellWidthPx { get; set; }
        [JsonPropertyName("sixel_ch"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? SixelCellHeightPx { get; set; }

        /// <summary>Escape state-machine position, as the ordinal of <c>AnsiParser.State</c>.</summary>
        [JsonPropertyName("state")] public int State { get; set; }

        /// <summary>The live CSI parameter/intermediate run, empty when not inside a CSI.</summary>
        [JsonPropertyName("csi")] public string CsiParams { get; set; } = string.Empty;

        /// <summary>The CSI run overflowed its cap, so the surviving prefix cannot be classified.</summary>
        [JsonPropertyName("csi_trunc")] public bool CsiTruncated { get; set; }

        [JsonPropertyName("osc")] public string OscBuffer { get; set; } = string.Empty;
        [JsonPropertyName("apc")] public string ApcBuffer { get; set; } = string.Empty;
        [JsonPropertyName("dcs")] public string DcsBuffer { get; set; } = string.Empty;

        /// <summary>Input was discarded from this DCS; absent legacy state cannot prove completeness.</summary>
        [JsonPropertyName("dcs_trunc"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? DcsTruncated { get; set; }

        /// <summary>Accumulated payload of a chunked (<c>m=1</c>) kitty graphics transmission.</summary>
        [JsonPropertyName("kitty_payload")] public string KittyPayload { get; set; } = string.Empty;

        /// <summary>Latched once a chunked kitty payload exceeded its cap.</summary>
        [JsonPropertyName("kitty_overflow")] public bool KittyPayloadOverflow { get; set; }

        /// <summary>Control parameters of the in-flight chunked kitty transmission.</summary>
        [JsonPropertyName("kitty_params")]
        public Dictionary<string, string> KittyPendingParams { get; set; } = new();

        /// <summary>
        /// The iTerm2 multipart image transfer between <c>OSC 1337 ; MultipartFile</c> and
        /// <c>OSC 1337 ; FileEnd</c>, or null when none is in progress (and in legacy snapshots).
        /// </summary>
        [JsonPropertyName("iterm_multipart"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ITerm2MultipartState? ITerm2Multipart { get; set; }

        /// <summary>G0..G3 designations, as ordinals of <c>AnsiParser.Charset</c>. Length 4.</summary>
        [JsonPropertyName("charsets")] public int[] Charsets { get; set; } = new int[4];

        /// <summary>Which of <see cref="Charsets"/> is shifted to GL (0..3).</summary>
        [JsonPropertyName("gl")] public int Gl { get; set; }

        /// <summary>Slot awaiting its designation byte, or -1.</summary>
        [JsonPropertyName("pending_charset")] public int PendingCharsetSlot { get; set; } = -1;

        [JsonPropertyName("swallow_nl")] public bool SwallowNextNewline { get; set; }
        [JsonPropertyName("voffset")] public int VerticalOffset { get; set; }

        /// <summary>What <c>CSI Ps b</c> would repeat, or null.</summary>
        [JsonPropertyName("last_graphic")] public string? LastGraphicChar { get; set; }

        /// <summary>DECSET 2031 - dark/light theme-change reports. Absent legacy state defaults off.</summary>
        [JsonPropertyName("color_scheme_reports")] public bool ColorSchemeReportsEnabled { get; set; }

        /// <summary>DECSET 2048 - kitty in-band resize reports.</summary>
        [JsonPropertyName("inband_resize")] public bool InBandResizeReportsEnabled { get; set; }

        /// <summary>
        /// The raw window title the stream last requested (OSC 0/2), or restored from the title
        /// stack. Empty before any request and in legacy snapshots; null when it was too long to
        /// retain. Importing it raises no title callback.
        /// </summary>
        [JsonPropertyName("title")] public string? WindowTitle { get; set; } = string.Empty;

        /// <summary>The raw icon name (OSC 0/1), with the same conventions as <see cref="WindowTitle"/>.</summary>
        [JsonPropertyName("icon")] public string? IconName { get; set; } = string.Empty;

        /// <summary>XTWINOPS 22/23 title stack, oldest first; null when empty.</summary>
        [JsonPropertyName("title_stack"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<SavedTitleEntry>? TitleStack { get; set; }

        /// <summary>
        /// Length of the parser's text-batching buffer at export. Always 0 in practice - the
        /// buffer is flushed before every state-affecting branch - and exported purely so that
        /// claim can be asserted rather than assumed. Not re-imported.
        /// </summary>
        [JsonPropertyName("pending_text_len")] public int PendingTextLength { get; set; }

        /// <summary>
        /// A stable, human-readable rendering for assertions and failure messages. Not a
        /// serialization format - use <c>TerminalStateSerializer</c> for that.
        /// </summary>
        public string ToDebugString()
        {
            var sb = new StringBuilder();
            sb.Append("state=").Append(State);
            sb.Append(" csi=").Append(CsiParams).Append(" trunc=").Append(CsiTruncated);
            sb.Append(" osc=").Append(OscBuffer);
            sb.Append(" apc=").Append(ApcBuffer);
            sb.Append(" dcs=").Append(DcsBuffer).Append(" trunc=").Append(DcsTruncated);
            sb.Append(" kitty=").Append(KittyPayload).Append(" ovf=").Append(KittyPayloadOverflow);
            sb.Append(" kparams=");

            // Sorted, not in dictionary order: this rendering is the equality oracle the
            // snapshot-parity tests compare two parsers with, across hundreds of cut points.
            // Dictionary enumeration order is unspecified, so an unsorted rendering would make
            // two identical parsers occasionally disagree - a parity failure indistinguishable
            // from a real divergence. Only the rendering sorts; AnsiParser's own dictionary is
            // copied verbatim, which is the point of exporting it.
            var kittyKeys = new List<string>(KittyPendingParams.Keys);
            kittyKeys.Sort(StringComparer.Ordinal);
            foreach (string key in kittyKeys)
            {
                sb.Append(key).Append('=').Append(KittyPendingParams[key]).Append(',');
            }

            sb.Append(" iterm_multipart=");
            if (ITerm2Multipart is { } multipart)
            {
                sb.Append("(w=").Append(Quote(multipart.Width)).Append(",h=").Append(Quote(multipart.Height))
                    .Append(",aspect=").Append(multipart.PreserveAspectRatio).Append(",ovf=").Append(multipart.Overflow)
                    .Append(",payload=").Append(Quote(multipart.Payload)).Append(')');
            }
            else
            {
                sb.Append("<none>");
            }

            sb.Append(" charsets=");
            foreach (int c in Charsets)
            {
                sb.Append(c).Append(',');
            }

            sb.Append(" gl=").Append(Gl).Append(" pend=").Append(PendingCharsetSlot);
            sb.Append(" swallow=").Append(SwallowNextNewline);
            sb.Append(" voff=").Append(VerticalOffset);
            sb.Append(" lastgfx=").Append(LastGraphicChar ?? "<null>");
            sb.Append(" color_scheme_reports=").Append(ColorSchemeReportsEnabled);
            sb.Append(" inband=").Append(InBandResizeReportsEnabled);
            sb.Append(" title=").Append(Quote(WindowTitle));
            sb.Append(" icon=").Append(Quote(IconName));
            sb.Append(" title_stack=");
            foreach (SavedTitleEntry entry in TitleStack ?? new List<SavedTitleEntry>())
            {
                sb.Append('(').Append(Quote(entry?.Icon)).Append(',').Append(Quote(entry?.Title)).Append(')');
            }

            sb.Append(" sixel_cw=").Append(SixelCellWidthPx?.ToString("R", CultureInfo.InvariantCulture) ?? "<null>");
            sb.Append(" sixel_ch=").Append(SixelCellHeightPx?.ToString("R", CultureInfo.InvariantCulture) ?? "<null>");
            return sb.ToString();
        }

        private static string Quote(string? value) => value is null ? "<null>" : "\"" + value + "\"";
    }

    /// <summary>
    /// An iTerm2 multipart image transfer in progress: the image arguments its <c>MultipartFile</c>
    /// header gave, and the base64 its <c>FilePart</c>s have delivered so far.
    /// </summary>
    public sealed class ITerm2MultipartState
    {
        /// <summary>The header's <c>width</c> as sent (cells, <c>Npx</c>, <c>N%</c> or <c>auto</c>); empty when absent.</summary>
        [JsonPropertyName("width")] public string Width { get; set; } = string.Empty;

        /// <summary>The header's <c>height</c>, as <see cref="Width"/>.</summary>
        [JsonPropertyName("height")] public string Height { get; set; } = string.Empty;

        /// <summary>The header's <c>preserveAspectRatio</c>; true unless it was <c>0</c>.</summary>
        [JsonPropertyName("aspect")] public bool PreserveAspectRatio { get; set; } = true;

        /// <summary>The base64 the parts have delivered, concatenated. Empty once <see cref="Overflow"/> is set.</summary>
        [JsonPropertyName("payload")] public string Payload { get; set; } = string.Empty;

        /// <summary>The parts passed the payload limit, so <c>FileEnd</c> places nothing.</summary>
        [JsonPropertyName("overflow")] public bool Overflow { get; set; }
    }

    /// <summary>One XTWINOPS title-stack entry. A null half was not saved and has nothing to restore.</summary>
    public sealed class SavedTitleEntry
    {
        [JsonPropertyName("icon")] public string? Icon { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
    }
}
