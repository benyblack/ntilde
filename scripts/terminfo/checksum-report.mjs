// Bounded owned DECRQCRA exchanges. The raw reply (one DCS string, or its explicit absence) is retained byte for byte; whether it
// is the right checksum is the contract's business, never this parser's. A complete DA reply orders the exchange and establishes
// an explicitly absent reply.
const esc = '\x1b';
const barrierPattern = /^\x1b\[\?[\d;]+c$/;
/** Any one complete DCS string: ESC P, at most 48 bytes without ESC, then ST. */
const dcsPattern = /^\x1bP[^\x1b]{0,48}\x1b\\$/;
/** The longest exchange: a bounded DCS reply plus a DA barrier. */
const exchangeLimit = 192;

/** The fence's expectation tag for one checksum exchange (identity-compared, never parsed). */
export const checksumReportExpectation = Object.freeze({ kind: 'rectangle-checksum' });

/** Refuse anything but one complete bounded DCS string. */
export function parseChecksumReply(raw) {
    if (typeof raw !== 'string' || !dcsPattern.test(raw)) throw new Error('Unexpected rectangle checksum reply.');
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
            if (!/^\x1bP[^\x1b]{0,48}\x1b?$/.test(rest)) throw new Error('Unexpected rectangle checksum bytes.');
            return null;
        }
        reply = parseChecksumReply(rest.slice(0, end + 2));
        rest = rest.slice(end + 2);
    }
    if (!rest || (esc + '[?').startsWith(rest)) return null;
    if (barrierPattern.test(rest)) return { barrier: rest, reply };
    if (!/^\x1b\[\?[\d;]*$/.test(rest)) throw new Error('Unexpected rectangle checksum barrier bytes.');
    return null;
}

/** A retained exchange: its raw reply (or explicit absence) and DA barrier. */
export function validateChecksumReport(value) {
    if (!value || typeof value !== 'object' || Object.keys(value).length !== 2 || !barrierPattern.test(value.barrier ?? '')
        || value.barrier.length > 128 || !Object.hasOwn(value, 'reply')) {
        throw new Error('Invalid retained rectangle checksum report.');
    }
    if (value.reply === null) return { barrier: value.barrier, reply: null };
    if (!value.reply || typeof value.reply !== 'object' || Object.keys(value.reply).length !== 1) throw new Error('Invalid retained rectangle checksum report.');
    return { barrier: value.barrier, reply: parseChecksumReply(value.reply.raw) };
}
