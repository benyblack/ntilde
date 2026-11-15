// Bounded owned xterm color-stack exchanges. One request list earns a bounded *sequence* of complete OSC color replies followed by the DA barrier that orders the exchange. Every reply is retained
// byte for byte; whether it is the right reply for the request is the contract's business, never this parser's. A complete DA barrier with no reply before it is an explicitly absent reply list
// (the silence of a push or pop), never a reply guessed from state.
const esc = '\x1b';
/** A complete DA reply: ESC, then [?, parameters and c (the ESC is checked outside the pattern). */
const isBarrier = text => typeof text === 'string' && text.startsWith(esc) && /^\[\?[\d;]+c$/.test(text.slice(1));
/** The longest string-reply body (the bytes between the introducer and ST) one retained reply may hold. */
const bodyLimit = 128;
/** The most replies one exchange may retain. */
export const xcsReplyLimit = 8;
/** The longest exchange: bounded replies plus a DA barrier. */
const exchangeLimit = 1024;

/** The fence's expectation tag for one xterm color-stack exchange (identity-compared, never parsed). */
export const xcsReportExpectation = Object.freeze({ kind: 'xterm-color-stack' });

/** Whether `text` is ESC ], at most 128 bytes without ESC, then (when `closed`) ST or (when not) at most a lone trailing ESC. */
function stringShape(text, closed) {
    if (!text.startsWith(esc + ']')) return false;
    let body = text.slice(2);
    if (closed) {
        if (!body.endsWith(esc + '\\')) return false;
        body = body.slice(0, -2);
    } else if (body.endsWith(esc)) {
        body = body.slice(0, -1);
    }
    return body.length <= bodyLimit && !body.includes(esc);
}

/** Refuse anything but one complete bounded OSC string. */
export function parseXcsReply(raw) {
    if (typeof raw !== 'string' || !stringShape(raw, true)) throw new Error('Unexpected xterm-color-stack reply.');
    return { raw };
}

/** Where one OSC reply ends in `rest` (at its ST), or -1 while incomplete; throws on bytes no reply starts with. */
function replyEnd(rest) {
    const end = rest.indexOf(esc + '\\');
    if (end < 0) {
        if (!stringShape(rest, false)) throw new Error('Unexpected xterm-color-stack bytes.');
        return -1;
    }
    return end + 2;
}

/**
 * Parse the accumulated bytes of one exchange: any number of complete bounded replies followed by the DA barrier. Null means incomplete. A complete DA without a reply is an explicitly absent
 * reply list, never a reply guessed from state.
 */
export function parseXcsFrame(bytes) {
    if (typeof bytes !== 'string' || bytes.length > exchangeLimit) throw new Error('Oversized xterm-color-stack barrier.');
    const replies = [];
    let rest = bytes;
    while (rest.startsWith(esc + ']')) {
        const length = replyEnd(rest);
        if (length < 0) return null;
        if (replies.length === xcsReplyLimit) throw new Error('Too many xterm-color-stack replies.');
        replies.push(parseXcsReply(rest.slice(0, length)).raw);
        rest = rest.slice(length);
    }
    if (!rest || (esc + '[?').startsWith(rest)) return null;
    if (isBarrier(rest)) return { barrier: rest, replies };
    if (!rest.startsWith(esc) || !/^\[\?[\d;]*$/.test(rest.slice(1))) throw new Error('Unexpected xterm-color-stack barrier bytes.');
    return null;
}

/** A retained exchange: its raw replies (or explicit absence) and DA barrier. */
export function validateXcsExchange(value) {
    if (!value || typeof value !== 'object' || Object.keys(value).length !== 2 || !isBarrier(value.barrier)
        || value.barrier.length > 128 || !Array.isArray(value.replies) || value.replies.length > xcsReplyLimit) {
        throw new Error('Invalid retained xterm-color-stack report.');
    }
    return { barrier: value.barrier, replies: value.replies.map(raw => parseXcsReply(raw).raw) };
}
