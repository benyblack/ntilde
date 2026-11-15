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

/** The most bytes one retained reply (an OSC string, or an unexpected chunk of other bytes) may hold. */
const replyLimit = 160;
/** A DA reply anywhere in a string: the barrier that orders an exchange. */
const barrierAnywhere = new RegExp(`${esc}\\[\\?[\\d;]+c`);
/** A trailing partial DA reply (ESC, ESC [, ESC [ ? and digits): a barrier that is still arriving. */
const partialBarrier = new RegExp(`${esc}(?:\\[(?:\\?[\\d;]*)?)?$`);

/** Refuse anything but one bounded reply: a complete OSC string, or an unexpected chunk of other bytes (a push or pop that answers with anything is evidence, not an error). Neither may hold a DA barrier. */
export function parseXcsReply(raw) {
    if (typeof raw !== 'string' || raw.length === 0 || raw.length > replyLimit || barrierAnywhere.test(raw)) throw new Error('Unexpected xterm-color-stack reply.');
    return { raw };
}

/** Where one OSC reply ends in `rest` (at its ST or its BEL, whichever comes first), or -1 while incomplete. */
function replyEnd(rest) {
    const st = rest.indexOf(esc + '\\'), bel = rest.indexOf('\x07');
    if (st < 0 && bel < 0) return -1;
    if (bel >= 0 && (st < 0 || bel < st)) return bel + 1;
    return st + 2;
}

/** What to do with the bytes accumulated so far: wait for more, take `take` bytes as one reply, or finish at the barrier. Bytes that are not a complete OSC string are retained as an unexpected chunk up to the barrier. */
function nextToken(rest) {
    const osc = rest.startsWith(esc + ']');
    const end = osc ? replyEnd(rest) : -1;
    if (end >= 0) return { take: end };
    if (!osc) {
        if (!rest || (esc + '[?').startsWith(rest)) return { wait: true };
        if (isBarrier(rest)) return { barrier: rest };
    }
    const found = barrierAnywhere.exec(rest);
    if (found === null) return waitFor(rest, osc);
    if (found.index > 0) return { take: found.index };
    // A complete DA reply followed by more bytes is not a single barrier.
    if (rest.length !== found[0].length) throw new Error('Unexpected xterm-color-stack barrier bytes.');
    return { barrier: rest };
}

/** No barrier yet: an unterminated OSC string must stay within bounds (ignoring a barrier that may still be arriving), other bytes within the reply limit. */
function waitFor(rest, osc) {
    if (osc ? !stringShape(rest.replace(partialBarrier, ''), false) : rest.length > replyLimit + 32) throw new Error(osc ? 'Unexpected xterm-color-stack bytes.' : 'Oversized xterm-color-stack reply.');
    return { wait: true };
}

/**
 * Parse the accumulated bytes of one exchange: any number of complete bounded replies followed by the DA barrier. A reply is a complete OSC string (ST or BEL terminated) or, for other bytes, one unexpected
 * chunk up to the barrier (retained byte for byte: the pinned probe treats any output as an invalid reply and the contract as a mismatch). Null means incomplete. A complete DA without a reply is an
 * explicitly absent reply list, never a reply guessed from state.
 */
export function parseXcsFrame(bytes) {
    if (typeof bytes !== 'string' || bytes.length > exchangeLimit) throw new Error('Oversized xterm-color-stack barrier.');
    const replies = [];
    let rest = bytes;
    for (;;) {
        const token = nextToken(rest);
        if (token.wait) return null;
        if (token.barrier !== undefined) return { barrier: token.barrier, replies };
        if (replies.length === xcsReplyLimit) throw new Error('Too many xterm-color-stack replies.');
        replies.push(parseXcsReply(rest.slice(0, token.take)).raw);
        rest = rest.slice(token.take);
    }
}

/** A retained exchange: its raw replies (or explicit absence) and DA barrier. */
export function validateXcsExchange(value) {
    if (!value || typeof value !== 'object' || Object.keys(value).length !== 2 || !isBarrier(value.barrier)
        || value.barrier.length > 128 || !Array.isArray(value.replies) || value.replies.length > xcsReplyLimit) {
        throw new Error('Invalid retained xterm-color-stack report.');
    }
    return { barrier: value.barrier, replies: value.replies.map(raw => parseXcsReply(raw).raw) };
}
