using System.Text;

namespace Ntilde.VT
{
    /// <summary>
    /// The multipart form of iTerm2 inline images, which <c>imgcat</c> sends by default since iTerm2 3.5:
    /// <c>OSC 1337 ; MultipartFile=args ST</c>, then any number of <c>OSC 1337 ; FilePart=base64 ST</c>,
    /// then <c>OSC 1337 ; FileEnd ST</c>. The parts are concatenated and the result placed exactly as the
    /// payload of a single <c>OSC 1337 ; File=args:base64 ST</c> would be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The transfer spans several sequences, so it is parser state: it is exported with
    /// <see cref="ExportState"/> and survives a persistent-session snapshot taken between parts, and the
    /// daemon and every client assemble the same payload from the same bytes.
    /// </para>
    /// <para>
    /// The rules, each the safe reading of a stream that does not follow the protocol:
    /// <list type="bullet">
    /// <item>Other output between parts (text, controls, other images) leaves the transfer pending, as
    /// it does a chunked Kitty upload: tmux redrawing or a shell's job notice can land between parts.
    /// The image is placed where the cursor is at <c>FileEnd</c>.</item>
    /// <item>A <c>MultipartFile</c> while one is pending drops the pending one and starts afresh, so an
    /// interrupted <c>imgcat</c> that never sent <c>FileEnd</c> cannot spoil the next image.</item>
    /// <item><c>FilePart</c> and <c>FileEnd</c> outside a transfer are ignored.</item>
    /// <item>The payload is capped at the limit of a single <c>File=</c> payload. Past it the parts so
    /// far are freed at once, later parts are ignored, and <c>FileEnd</c> places nothing.</item>
    /// <item>A part that is not base64, or one that leaves the whole off a base64 boundary, makes the
    /// payload undecodable, and <c>FileEnd</c> places nothing.</item>
    /// <item>A header that is not <c>inline=1</c> (iTerm2 would offer a download) or has a dimension
    /// longer than <see cref="ITerm2MaxDimensionChars"/> starts no transfer.</item>
    /// <item>RIS ends the transfer, as does turning images or <see cref="AllowITerm2Multipart"/> off
    /// (nothing is accumulated without them).</item>
    /// </list>
    /// </para>
    /// </remarks>
    public partial class AnsiParser
    {
        /// <summary>The longest base64 payload an iTerm2 image may have, in one sequence or assembled from parts.</summary>
        private const int ITerm2MaxPayloadChars = 10 * 1024 * 1024;

        /// <summary>
        /// The longest <c>width</c> or <c>height</c> a multipart header may carry. Real values are a few
        /// characters (<c>auto</c>, <c>40</c>, <c>320px</c>, <c>50%</c>); the bound keeps the exported
        /// transfer small and lets an import refuse what no stream could have produced.
        /// </summary>
        internal const int ITerm2MaxDimensionChars = 64;

        /// <summary>
        /// Whether the multipart form is assembled (with <see cref="AllowNonSixelImages"/>); when false it
        /// is ignored, as before it was supported. On by default. A persistent session sets it from its
        /// image policy (<see cref="PersistentImagePolicy.Apply"/>), so the daemon and every client agree,
        /// including a session whose peers predate the multipart form.
        /// </summary>
        public bool AllowITerm2Multipart { get; set; } = true;

        private ITerm2ImageArguments? _iTerm2Multipart;
        private StringBuilder _iTerm2MultipartPayload = new();
        private bool _iTerm2MultipartOverflow;

        /// <summary>
        /// The multipart payload limit: a single <c>File=</c> payload's, and never more than this parser
        /// accumulates for one string sequence.
        /// </summary>
        private int ITerm2MultipartPayloadLimit => Math.Max(0, Math.Min(ITerm2MaxPayloadChars, MaxStringSequenceChars));

        /// <summary>Routes the OSC 1337 multipart sequences. Returns false for anything else.</summary>
        private bool HandleITerm2Multipart(string osc)
        {
            const string Begin = "1337;MultipartFile=", Part = "1337;FilePart=", End = "1337;FileEnd";
            if (osc.StartsWith(Begin, StringComparison.Ordinal)) BeginITerm2Multipart(osc.Substring(Begin.Length));
            else if (osc.StartsWith(Part, StringComparison.Ordinal)) AppendITerm2Part(osc.AsSpan(Part.Length));
            else if (osc == End) EndITerm2Multipart();
            else return false;
            return true;
        }

        private void BeginITerm2Multipart(string header)
        {
            ClearITerm2Multipart();
            if (!AllowNonSixelImages || !AllowITerm2Multipart) return;
            ITerm2ImageArguments arguments = ITerm2ImageArguments.Parse(header);
            if (!arguments.Inline || arguments.Width.Length > ITerm2MaxDimensionChars || arguments.Height.Length > ITerm2MaxDimensionChars) return;
            _iTerm2Multipart = arguments;
        }

        private void AppendITerm2Part(ReadOnlySpan<char> part)
        {
            if (_iTerm2Multipart is null || _iTerm2MultipartOverflow) return;
            if (!AllowNonSixelImages || !AllowITerm2Multipart)
            {
                ClearITerm2Multipart();
                return;
            }
            if (_iTerm2MultipartPayload.Length + part.Length > ITerm2MultipartPayloadLimit)
            {
                // Free what was gathered now rather than hold it until FileEnd, which places nothing.
                _iTerm2MultipartOverflow = true;
                ResetITerm2MultipartPayload();
                return;
            }
            _iTerm2MultipartPayload.Append(part);
        }

        private void EndITerm2Multipart()
        {
            if (_iTerm2Multipart is not { } arguments) return;
            bool complete = !_iTerm2MultipartOverflow && AllowNonSixelImages && AllowITerm2Multipart;
            string payload = complete ? _iTerm2MultipartPayload.ToString() : string.Empty;
            ClearITerm2Multipart();
            if (complete) PlaceITerm2Image(arguments, payload);
        }

        /// <summary>Ends any multipart transfer, keeping nothing of it.</summary>
        private void ClearITerm2Multipart()
        {
            _iTerm2Multipart = null;
            _iTerm2MultipartOverflow = false;
            ResetITerm2MultipartPayload();
        }

        private void ResetITerm2MultipartPayload()
        {
            if (_iTerm2MultipartPayload.Capacity > RetainedStringBufferChars) _iTerm2MultipartPayload = new StringBuilder();
            else _iTerm2MultipartPayload.Clear();
        }

        private ITerm2MultipartState? ExportITerm2Multipart() => _iTerm2Multipart is { } arguments
            ? new ITerm2MultipartState
            {
                Width = arguments.Width,
                Height = arguments.Height,
                PreserveAspectRatio = arguments.PreserveAspectRatio,
                Payload = _iTerm2MultipartOverflow ? string.Empty : _iTerm2MultipartPayload.ToString(),
                Overflow = _iTerm2MultipartOverflow,
            }
            : null;

        /// <summary>
        /// Adopts an exported transfer. A payload over this parser's limit becomes an overflowed transfer
        /// with nothing kept, as the live path would have left it.
        /// </summary>
        private void ImportITerm2Multipart(ITerm2MultipartState? state)
        {
            ClearITerm2Multipart();
            if (state is null) return;
            _iTerm2Multipart = new ITerm2ImageArguments(state.Width, state.Height, Inline: true, state.PreserveAspectRatio);
            _iTerm2MultipartOverflow = state.Overflow || state.Payload.Length > ITerm2MultipartPayloadLimit;
            if (!_iTerm2MultipartOverflow) _iTerm2MultipartPayload.Append(state.Payload);
        }

        private static void ValidateITerm2Multipart(ITerm2MultipartState? state)
        {
            if (state is null) return;
            StateTransferValidation.RequirePresent(state.Width, "parser iTerm2 multipart width");
            StateTransferValidation.RequirePresent(state.Height, "parser iTerm2 multipart height");
            StateTransferValidation.RequirePresent(state.Payload, "parser iTerm2 multipart payload");
            if (state.Width.Length > ITerm2MaxDimensionChars || state.Height.Length > ITerm2MaxDimensionChars)
                throw StateTransferValidation.Reject("parser iTerm2 multipart dimensions", $"longer than {ITerm2MaxDimensionChars} characters");
        }
    }
}
