// Bounded owned cursor-position reports (CPR). The raw reply is retained byte for byte beside the physical cursor the parent
// observes: under DECOM a reply is relative to the scroll region, so it can never stand in for an absolute physical row.
// A complete DA reply still orders the exchange and establishes an explicitly absent CPR.
const esc = '\x1b';
const reply = /^\x1b\[(\d{1,4});(\d{1,4})R$/;
const barrierPattern = /^\x1b\[\?[\d;]+c$/;

/** The CPR query bytes. */
export const cursorReportQuery = esc + '[6n';

/** Parse one raw CPR reply into its 1-based row and column, or refuse it. */
export function parseCursorReport(raw) {
    const match = typeof raw === 'string' && raw.length <= 16 ? reply.exec(raw) : null;
    if (!match) throw new Error('Unexpected cursor position report.');
    const row = Number(match[1]), col = Number(match[2]);
    if (row < 1 || col < 1) throw new Error('Cursor position report is not 1-based.');
    return { raw, row, col };
}

/**
 * Parse the accumulated bytes of one exchange: an optional CPR followed by the DA barrier. Null means incomplete. A complete
 * DA without a CPR is an explicitly absent reply, never a reply guessed from state.
 */
export function parseCursorReportFrame(bytes) {
    if (typeof bytes !== 'string' || bytes.length > 160) throw new Error('Oversized cursor report barrier.');
    let rest = bytes, report = null;
    if (!rest || rest === esc) return null;
    if (rest.startsWith(esc + '[') && !rest.startsWith(esc + '[?')) {
        const end = rest.indexOf('R');
        if (end < 0) {
            if (!/^\x1b\[[\d;]*$/.test(rest)) throw new Error('Unexpected cursor report bytes.');
            return null;
        }
        report = parseCursorReport(rest.slice(0, end + 1));
        rest = rest.slice(end + 1);
    }
    if (!rest || (esc + '[?').startsWith(rest)) return null;
    if (barrierPattern.test(rest)) return { barrier: rest, reply: report };
    if (!/^\x1b\[\?[\d;]*$/.test(rest)) throw new Error('Unexpected cursor report barrier bytes.');
    return null;
}

/** A retained exchange: its raw reply (or explicit absence), 1-based position and DA barrier. */
export function validateCursorReport(value) {
    if (!value || typeof value !== 'object' || !barrierPattern.test(value.barrier ?? '') || value.barrier.length > 128) {
        throw new Error('Invalid retained cursor position report.');
    }
    if (value.reply === null) return { barrier: value.barrier, reply: null };
    const parsed = parseCursorReport(value.reply?.raw);
    if (parsed.row !== value.reply.row || parsed.col !== value.reply.col) throw new Error('Retained cursor position report disagrees with its raw bytes.');
    return { barrier: value.barrier, reply: parsed };
}
