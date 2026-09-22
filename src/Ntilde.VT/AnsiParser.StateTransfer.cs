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

            _state = (State)state.State;

            EnsureParamCapacity(state.CsiParams.Length);
            state.CsiParams.AsSpan().CopyTo(_paramBuffer);
            _paramLen = state.CsiParams.Length;
            _csiTruncated = state.CsiTruncated;

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

            for (int i = 0; i < 4 && i < state.Charsets.Length; i++)
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
            // calls - AnsiParserStateTests asserts it), the per-batch cursor-visibility flags
            // (reset inside Process()), and the hyperlink interning registry (identity travels
            // with the buffer's link table instead).
            _textBuffer.Clear();
            _sawCursorHideInBatch = false;
            _sawCursorShowAfterHideInBatch = false;
        }

        /// <summary>Grows <see cref="_paramBuffer"/> to hold at least <paramref name="length"/> chars.</summary>
        private void EnsureParamCapacity(int length)
        {
            if (_paramBuffer.Length < length)
            {
                _paramBuffer = new char[Math.Max(length, _paramBuffer.Length * 2)];
            }
        }
    }
}
