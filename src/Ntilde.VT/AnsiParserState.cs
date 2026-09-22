using System.Collections.Generic;
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
    /// Read-only configuration is deliberately absent: the image decoder, cell metrics, default
    /// colours, the kitty kill switch, the string-sequence cap, and the ConPTY-filtering flag are
    /// host settings, not stream position. Two parsers exchanging state must be constructed with
    /// the same <c>forceConPtyFiltering</c>, because that one changes how a sequence is
    /// interpreted rather than what has been seen.
    ///
    /// The hyperlink registry is also absent by design: link identity is reference equality, so
    /// it travels with the buffer's hyperlink table (<see cref="TerminalStateSnapshot"/>) where
    /// cells can point at the same instances. Re-interning here would mint fresh instances for
    /// ids the table had already resolved, and adjacent cells would stop grouping.
    /// </remarks>
    public sealed class AnsiParserState
    {
        /// <summary>Escape state-machine position, as the ordinal of <c>AnsiParser.State</c>.</summary>
        [JsonPropertyName("state")] public int State { get; set; }

        /// <summary>The live CSI parameter/intermediate run, empty when not inside a CSI.</summary>
        [JsonPropertyName("csi")] public string CsiParams { get; set; } = string.Empty;

        /// <summary>The CSI run overflowed its cap, so the surviving prefix cannot be classified.</summary>
        [JsonPropertyName("csi_trunc")] public bool CsiTruncated { get; set; }

        [JsonPropertyName("osc")] public string OscBuffer { get; set; } = string.Empty;
        [JsonPropertyName("apc")] public string ApcBuffer { get; set; } = string.Empty;
        [JsonPropertyName("dcs")] public string DcsBuffer { get; set; } = string.Empty;

        /// <summary>Accumulated payload of a chunked (<c>m=1</c>) kitty graphics transmission.</summary>
        [JsonPropertyName("kitty_payload")] public string KittyPayload { get; set; } = string.Empty;

        /// <summary>Latched once a chunked kitty payload exceeded its cap.</summary>
        [JsonPropertyName("kitty_overflow")] public bool KittyPayloadOverflow { get; set; }

        /// <summary>Control parameters of the in-flight chunked kitty transmission.</summary>
        [JsonPropertyName("kitty_params")]
        public Dictionary<string, string> KittyPendingParams { get; set; } = new();

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

        /// <summary>DECSET 2048 - kitty in-band resize reports.</summary>
        [JsonPropertyName("inband_resize")] public bool InBandResizeReportsEnabled { get; set; }

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
            sb.Append(" dcs=").Append(DcsBuffer);
            sb.Append(" kitty=").Append(KittyPayload).Append(" ovf=").Append(KittyPayloadOverflow);
            sb.Append(" kparams=");
            foreach (KeyValuePair<string, string> kv in KittyPendingParams)
            {
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append(',');
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
            sb.Append(" inband=").Append(InBandResizeReportsEnabled);
            return sb.ToString();
        }
    }
}
