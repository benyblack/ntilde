using System;
using System.Collections.Generic;

namespace Ntilde.VT
{
    public partial class AnsiParser
    {
        /// <summary>
        /// Captures every field that survives a <see cref="Process"/> call, so another parser can
        /// resume this one's position in the stream - mid-CSI, mid-OSC, mid-kitty-payload, mid
        /// charset designation. See <see cref="AnsiParserState"/> for what is deliberately left out.
        /// </summary>
        public AnsiParserState ExportState()
        {
            var state = new AnsiParserState
            {
                State = (int)_state,
                CsiParams = new string(_paramBuffer, 0, _paramLen),
                CsiTruncated = _csiTruncated,
                OscBuffer = new string(_oscStringBuffer.ToArray()),
                ApcBuffer = new string(_apcStringBuffer.ToArray()),
                DcsBuffer = new string(_dcsStringBuffer.ToArray()),
                KittyPayload = _kittyPayloadBuffer.ToString(),
                KittyPayloadOverflow = _kittyPayloadOverflow,
                KittyPendingParams = new Dictionary<string, string>(_kittyPendingParams),
                Charsets = new int[4],
                Gl = _gl,
                PendingCharsetSlot = _pendingCharsetSlot,
                SwallowNextNewline = _swallowNextNewline,
                VerticalOffset = _verticalOffset,
                LastGraphicChar = _lastGraphicChar?.ToString(),
                InBandResizeReportsEnabled = _inBandResizeReportsEnabled,
                PendingTextLength = _textBuffer.Length,
            };

            for (int i = 0; i < 4; i++)
            {
                state.Charsets[i] = (int)_charsets[i];
            }

            return state;
        }

        /// <summary>
        /// Adopts <paramref name="state"/> wholesale. The caller is responsible for having
        /// constructed this parser with the same <c>forceConPtyFiltering</c> as the exporter and
        /// for importing the matching <see cref="TerminalStateSnapshot"/> into this parser's
        /// buffer - the two halves are only meaningful together.
        /// </summary>
        public void ImportState(AnsiParserState state)
        {
            ArgumentNullException.ThrowIfNull(state);
            ValidateParserState(state);

            _state = (State)state.State;

            // The counts half of StateTransferValidation's rule: a parameter run longer than this
            // build's cap is truncated exactly where the accumulation path would have truncated
            // it, and the truncation is latched so the surviving prefix is not mistaken for a
            // complete, classifiable CSI.
            int csiLen = StateTransferValidation.ClampCount(state.CsiParams.Length, MaxCsiParamChars);
            EnsureParamCapacity(csiLen);
            state.CsiParams.AsSpan(0, csiLen).CopyTo(_paramBuffer);
            _paramLen = csiLen;
            _csiTruncated = state.CsiTruncated || csiLen < state.CsiParams.Length;

            _oscStringBuffer.Clear();
            _oscStringBuffer.AddRange(state.OscBuffer);
            _apcStringBuffer.Clear();
            _apcStringBuffer.AddRange(state.ApcBuffer);
            _dcsStringBuffer.Clear();
            _dcsStringBuffer.AddRange(state.DcsBuffer);

            _kittyPayloadBuffer.Clear();
            _kittyPayloadBuffer.Append(state.KittyPayload);
            _kittyPayloadOverflow = state.KittyPayloadOverflow;
            _kittyPendingParams = new Dictionary<string, string>(state.KittyPendingParams);

            for (int i = 0; i < _charsets.Length; i++)
            {
                _charsets[i] = (Charset)state.Charsets[i];
            }

            _gl = state.Gl;
            _pendingCharsetSlot = state.PendingCharsetSlot;
            _swallowNextNewline = state.SwallowNextNewline;
            _verticalOffset = state.VerticalOffset;
            _lastGraphicChar = string.IsNullOrEmpty(state.LastGraphicChar) ? null : state.LastGraphicChar![0];
            _inBandResizeReportsEnabled = state.InBandResizeReportsEnabled;

            // Deliberately not restored: the text-batching buffer (always empty between Process()
            // calls - AnsiParserStateTests asserts it) and the per-batch cursor-visibility flags
            // (reset inside Process()). The hyperlink interning registry is not in
            // AnsiParserState either, but it is not simply dropped: see
            // <see cref="SeedHyperlinkRegistry"/>, which the caller invokes with the instances
            // TerminalBuffer.ImportState rebuilt.
            _textBuffer.Clear();
            _sawCursorHideInBatch = false;
            _sawCursorShowAfterHideInBatch = false;
        }

        /// <summary>
        /// Re-seeds the OSC 8 interning registry with the link identities
        /// <see cref="TerminalBuffer.ImportState"/> rebuilt, so an <c>id=</c> the restored cells
        /// already carry resolves to those same instances when the tail re-references it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Call after both halves of the transfer: <c>buffer.ImportState(snapshot)</c> hands back
        /// the array, <c>parser.ImportState(snapshot.Parser)</c> restores the parse position, and
        /// this closes the gap between them. Skipping it is not a crash but a silent grouping bug:
        /// a later <c>OSC 8 ;id=alpha;...</c> finds an empty registry, mints a fresh
        /// <see cref="Links.Hyperlink"/>, and the cells it writes stop grouping with the restored
        /// ones that were the same anchor.
        /// </para>
        /// <para>
        /// A separate call rather than a field of <see cref="AnsiParserState"/>: putting the
        /// registry in the payload would give link identity two independent sources - the parser's
        /// table and the buffer's - which would mint different instances for the same (URI, id)
        /// and reintroduce the same bug from the other side. Seeding from the buffer's rebuilt
        /// table keeps one source of identity, which is what the design rests on.
        /// </para>
        /// </remarks>
        /// <param name="links">
        /// The identity table from <see cref="TerminalBuffer.ImportState"/>. Entries without an
        /// explicit <c>id</c> are ignored, because those are never interned.
        /// </param>
        public void SeedHyperlinkRegistry(IReadOnlyList<Links.Hyperlink>? links) =>
            _hyperlinks.SeedInterned(links);

        /// <summary>
        /// Refuses a parser state this build cannot resume from, before any field is written.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The structure half of <see cref="StateTransferValidation"/>'s rule, applied to the one
        /// payload in the transfer whose fields are almost all indices rather than sizes.
        /// <c>_state</c> chooses a branch of the escape state machine; <c>_gl</c> and
        /// <c>_pendingCharsetSlot</c> index <see cref="_charsets"/>; the charset ordinals choose a
        /// glyph mapping. None of those has a meaningful clamp, and none of them fails where it is
        /// set: an out-of-range <c>_gl</c> throws <see cref="IndexOutOfRangeException"/> on the
        /// next printable character, arbitrarily far from the import that caused it.
        /// </para>
        /// <para>
        /// The null checks are here for the same reason. The state object is deserialized, so a
        /// payload that simply omits <c>"csi"</c> or <c>"kitty_params"</c> produces nulls that
        /// would surface as a <see cref="NullReferenceException"/> from inside the import.
        /// </para>
        /// </remarks>
        private void ValidateParserState(AnsiParserState state)
        {
            StateTransferValidation.RequirePresent(state.CsiParams, "parser CSI run");
            StateTransferValidation.RequirePresent(state.OscBuffer, "parser OSC accumulator");
            StateTransferValidation.RequirePresent(state.ApcBuffer, "parser APC accumulator");
            StateTransferValidation.RequirePresent(state.DcsBuffer, "parser DCS accumulator");
            StateTransferValidation.RequirePresent(state.KittyPayload, "parser kitty payload");
            StateTransferValidation.RequirePresent(state.KittyPendingParams, "parser kitty parameters");
            StateTransferValidation.RequirePresent(state.Charsets, "parser charset designations");

            // Enum.IsDefined rather than a hand-written upper bound: a bound spelled as a constant
            // goes stale the moment a state is appended to the enum, and the failure mode is a
            // legitimate payload being refused.
            if (!Enum.IsDefined((State)state.State))
            {
                throw StateTransferValidation.Reject(
                    "parser state-machine position", $"state={state.State} is not a defined position");
            }

            if (state.Charsets.Length != _charsets.Length)
            {
                throw StateTransferValidation.Reject(
                    "parser charset designations",
                    $"{state.Charsets.Length} slots, expected {_charsets.Length}");
            }

            for (int i = 0; i < state.Charsets.Length; i++)
            {
                if (!Enum.IsDefined((Charset)state.Charsets[i]))
                {
                    throw StateTransferValidation.Reject(
                        "parser charset designation",
                        $"G{i}={state.Charsets[i]} is not a defined charset");
                }
            }

            StateTransferValidation.RequireInRange(state.Gl, 0, _charsets.Length - 1, "parser GL slot");
            StateTransferValidation.RequireInRange(
                state.PendingCharsetSlot, -1, _charsets.Length - 1, "parser pending charset slot");
        }

        /// <summary>
        /// Grows <see cref="_paramBuffer"/> to hold at least <paramref name="length"/> chars, never
        /// past <see cref="MaxCsiParamChars"/> - the same ceiling
        /// <c>EnsureCsiParamCapacity</c> enforces on the accumulation path, so an imported run
        /// cannot buy a buffer a parsed one could not.
        /// </summary>
        private void EnsureParamCapacity(int length)
        {
            if (_paramBuffer.Length < length)
            {
                _paramBuffer = new char[Math.Min(
                    MaxCsiParamChars, Math.Max(length, _paramBuffer.Length * 2))];
            }
        }
    }
}
