// Bounded owned DECRQCRA exchanges. The raw reply (one DCS string, or its explicit absence) is retained byte for byte; whether it
// is the right checksum is the contract's business, never this parser's. A complete DA reply orders the exchange and establishes
// an explicitly absent reply.
const esc = '\x1b';
/** A complete DA reply: ESC, then [?, parameters and c (the ESC is checked outside the pattern). */
const isBarrier = text => typeof text === 'string' && text.startsWith(esc) && /^\[\?[\d;]+c$/.test(text.slice(1));
/** The longest DCS body (the bytes between ESC P and ST) the exchange retains. */
const bodyLimit = 48;
/** Whether `text` is ESC P, at most 48 bytes without ESC, then (when `closed`) ST or (when not) at most a lone trailing ESC. */
function dcsShape(text, closed) {
    if (!text.startsWith(esc + 'P')) return false;
    let body = text.slice(2);
    if (closed) {
        if (!body.endsWith(esc + '\\')) return false;
        body = body.slice(0, -2);
    } else if (body.endsWith(esc)) {
        body = body.slice(0, -1);
    }
    return body.length <= bodyLimit && !body.includes(esc);
}
/** The longest exchange: a bounded DCS reply plus a DA barrier. */
const exchangeLimit = 192;

/** The fence's expectation tag for one checksum exchange (identity-compared, never parsed). */
export const checksumReportExpectation = Object.freeze({ kind: 'rectangle-checksum' });

/** Refuse anything but one complete bounded DCS string. */
export function parseChecksumReply(raw) {
    if (typeof raw !== 'string' || !dcsShape(raw, true)) throw new Error('Unexpected rectangle checksum reply.');
    return { raw };
}

/**
 * Parse the accumulated bytes of one exchange: an optional DCS reply followed by the DA barrier. Null means incomplete. A complete
 * DA without a reply is an explicitly absent reply, never a reply guessed from state.
 */
export function parseChecksumReportFrame(bytes) {
    if (typeof bytes !== 'string' || bytes.length > exchangeLimit) throw new Error('Oversized rectangle checksum barrier.');
    let rest = bytes, reply = null;
    if (!rest || rest === esc) return null;
    if (rest.startsWith(esc + 'P')) {
        const end = rest.indexOf(esc + '\\');
        if (end < 0) {
            if (!dcsShape(rest, false)) throw new Error('Unexpected rectangle checksum bytes.');
            return null;
        }
        reply = parseChecksumReply(rest.slice(0, end + 2));
        rest = rest.slice(end + 2);
    }
    if (!rest || (esc + '[?').startsWith(rest)) return null;
    if (isBarrier(rest)) return { barrier: rest, reply };
    if (!rest.startsWith(esc) || !/^\[\?[\d;]*$/.test(rest.slice(1))) throw new Error('Unexpected rectangle checksum barrier bytes.');
    return null;
}

/** A retained exchange: its raw reply (or explicit absence) and DA barrier. */
export function validateChecksumReport(value) {
    if (!value || typeof value !== 'object' || Object.keys(value).length !== 2 || !isBarrier(value.barrier)
        || value.barrier.length > 128 || !Object.hasOwn(value, 'reply')) {
        throw new Error('Invalid retained rectangle checksum report.');
    }
    if (value.reply === null) return { barrier: value.barrier, reply: null };
    if (!value.reply || typeof value.reply !== 'object' || Object.keys(value.reply).length !== 1) throw new Error('Invalid retained rectangle checksum report.');
    return { barrier: value.barrier, reply: parseChecksumReply(value.reply.raw) };
}
