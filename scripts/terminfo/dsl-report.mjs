// Bounded owned exchanges of the DECSCLM qualification. One request earns a bounded sequence of complete DECRPM replies (CSI ? Ps ; Pm $ y) followed by the DA barrier that orders the exchange; a request
// with no reply earns the barrier alone, an explicitly absent reply list. Every reply is retained byte for byte; whether it is the right reply for the request is the contract's business, never this
// parser's. The shared query-probe framing accepts string replies only (OSC, DCS), so this suite frames its own mode reports.
const esc = '\x1b';
/** The most replies one exchange may retain. */
export const dslReplyLimit = 8;
/** The longest exchange: bounded replies plus a DA barrier. */
const exchangeLimit = 1024;
/** A complete DA reply: ESC, then [?, parameters and c (the ESC is checked outside the pattern). */
const isBarrier = text => typeof text === 'string' && text.startsWith(esc) && /^\[\?[\d;]+c$/.test(text.slice(1));
const replyAt = /^\x1b\[\?\d{1,5};[0-4]\$y/;
const wholeReply = /^\x1b\[\?\d{1,5};[0-4]\$y$/;

/** Refuse anything but one complete DECPM mode report (private mode, status 0-4). */
export function parseDslReply(raw) {
    if (typeof raw !== 'string' || !wholeReply.test(raw)) throw new Error('Unexpected decsclm reply.');
    return raw;
}

/** What follows the last reply: the DA barrier that completes the exchange, a prefix of a reply or of the barrier (null: incomplete), or bytes that are neither (refused). */
function finishFrame(rest, replies) {
    if (!rest || (esc + '[?').startsWith(rest)) return null;
    if (isBarrier(rest)) return { barrier: rest, replies };
    if (rest.length <= 128 && /^\x1b\[\?[\d;]*\$?$/.test(rest)) return null;
    throw new Error('Unexpected decsclm barrier bytes.');
}

/** Parse the accumulated bytes of one exchange: any number of complete mode reports followed by the DA barrier. Null means incomplete. A complete DA without a reply is an explicitly absent reply list,
 *  never a reply guessed from state. */
export function parseDslFrame(bytes) {
    if (typeof bytes !== 'string' || bytes.length > exchangeLimit) throw new Error('Oversized decsclm exchange.');
    const replies = [];
    let rest = bytes;
    for (let match = replyAt.exec(rest); match !== null; match = replyAt.exec(rest)) {
        if (replies.length === dslReplyLimit) throw new Error('Too many decsclm replies.');
        replies.push(match[0]);
        rest = rest.slice(match[0].length);
    }
    return finishFrame(rest, replies);
}

/** A retained exchange: its raw replies (or explicit absence) and DA barrier. */
export function validateDslExchange(value) {
    if (!value || typeof value !== 'object' || Object.keys(value).length !== 2 || !isBarrier(value.barrier)
        || value.barrier.length > 128 || !Array.isArray(value.replies) || value.replies.length > dslReplyLimit) {
        throw new Error('Invalid retained decsclm report.');
    }
    return { barrier: value.barrier, replies: value.replies.map(raw => parseDslReply(raw)) };
}
