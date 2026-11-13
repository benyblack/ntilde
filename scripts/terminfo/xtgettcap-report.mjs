// Bounded owned XTGETTCAP exchanges. A multi-name request earns one reply per name, so unlike the single-DCS checksum exchange this
// parser retains a bounded *sequence* of complete DCS replies followed by the DA barrier that orders the exchange. Every reply is
// retained byte for byte; whether it is the right reply for the request is the contract's business, never this parser's. A complete
// DA barrier with no reply before it is an explicitly absent reply, never a reply guessed from state.
const esc = '\x1b';
/** A complete DA reply: ESC, then [?, parameters and c (the ESC is checked outside the pattern). */
const isBarrier = text => typeof text === 'string' && text.startsWith(esc) && /^\[\?[\d;]+c$/.test(text.slice(1));
/** The longest DCS body (the bytes between ESC P and ST) one retained reply may hold. */
const bodyLimit = 128;
/** The most replies one exchange may retain (the implementation's documented XTGETTCAP name limit). */
export const xtgettcapReplyLimit = 16;
/** The longest exchange: bounded DCS replies plus a DA barrier. */
const exchangeLimit = 512;

/** The fence's expectation tag for one XTGETTCAP exchange (identity-compared, never parsed). */
export const xtgettcapReportExpectation = Object.freeze({ kind: 'xtgettcap' });

/** Whether `text` is ESC P, at most 128 bytes without ESC, then (when `closed`) ST or (when not) at most a lone trailing ESC. */
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

/** Refuse anything but one complete bounded DCS string. */
export function parseXtgettcapReply(raw) {
    if (typeof raw !== 'string' || !dcsShape(raw, true)) throw new Error('Unexpected XTGETTCAP reply.');
    return { raw };
}

/**
 * Parse the accumulated bytes of one exchange: any number of complete bounded DCS replies (up to the documented name limit)
 * followed by the DA barrier. Null means incomplete. A complete DA without a reply is an explicitly absent reply list, never a
 * reply guessed from state.
 */
export function parseXtgettcapFrame(bytes) {
    if (typeof bytes !== 'string' || bytes.length > exchangeLimit) throw new Error('Oversized XTGETTCAP barrier.');
    const replies = [];
    let rest = bytes;
    while (rest.startsWith(esc + 'P')) {
        const end = rest.indexOf(esc + '\\');
        if (end < 0) {
            if (!dcsShape(rest, false)) throw new Error('Unexpected XTGETTCAP bytes.');
            return null;
        }
        if (replies.length === xtgettcapReplyLimit) throw new Error('Too many XTGETTCAP replies.');
        replies.push(parseXtgettcapReply(rest.slice(0, end + 2)).raw);
        rest = rest.slice(end + 2);
    }
    if (!rest || (esc + '[?').startsWith(rest)) return null;
    if (isBarrier(rest)) return { barrier: rest, replies };
    if (!rest.startsWith(esc) || !/^\[\?[\d;]*$/.test(rest.slice(1))) throw new Error('Unexpected XTGETTCAP barrier bytes.');
    return null;
}

/** A retained exchange: its raw replies (or explicit absence) and DA barrier. */
export function validateXtgettcapExchange(value) {
    if (!value || typeof value !== 'object' || Object.keys(value).length !== 2 || !isBarrier(value.barrier)
        || value.barrier.length > 128 || !Array.isArray(value.replies) || value.replies.length > xtgettcapReplyLimit) {
        throw new Error('Invalid retained XTGETTCAP report.');
    }
    return { barrier: value.barrier, replies: value.replies.map(raw => parseXtgettcapReply(raw).raw) };
}
