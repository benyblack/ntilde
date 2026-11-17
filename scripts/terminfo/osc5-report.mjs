// Bounded owned exchanges of the rxvt special-color qualification. One request earns a bounded sequence of complete OSC 5 (or legacy color) replies followed by the DA barrier that orders the
// exchange; a request with no reply earns the barrier alone, an explicitly absent reply list. The framing extends the device-query suite's: the pinned probe's own frame pattern accepts a reply
// ended by BEL as well as ST, so this parser accepts both terminators and the pinned verdict never depends on the stricter effect-contract terminator.
const esc = '\x1b', bel = '\x07';
/** The most replies one exchange may retain. */
export const osc5ReplyLimit = 8;
/** The longest string-reply body (the bytes between the introducer and the terminator) one retained reply may hold. */
const bodyLimit = 128;
/** The longest exchange: bounded replies plus a DA barrier. */
const exchangeLimit = 1024;
/** A complete DA reply: ESC, then [?, parameters and c (the ESC is checked outside the pattern). */
const isBarrier = text => typeof text === 'string' && text.startsWith(esc) && /^\[\?[\d;]+c$/.test(text.slice(1));

/** End of one bounded OSC reply, or -1 for a valid incomplete string. The contract grades its body. */
function replyEnd(rest) {
    if (!rest.startsWith(esc + ']')) throw new Error('Unexpected special-color reply bytes.');
    const stAt = rest.indexOf(esc + '\\'), belAt = rest.indexOf(bel);
    const endsWithSt = stAt >= 0 && (belAt < 0 || stAt < belAt);
    const end = endsWithSt ? stAt : belAt;
    let body = rest.slice(2, end < 0 ? undefined : end);
    // A chunk can stop halfway through ST. An embedded ESC is otherwise forbidden.
    if (end < 0 && body.endsWith(esc)) body = body.slice(0, -1);
    if (body.length > bodyLimit || body.includes(esc)) throw new Error('Unexpected special-color reply bytes.');
    return end < 0 ? -1 : end + (endsWithSt ? 2 : 1);
}

/** Require exactly one complete bounded OSC reply, ended by BEL or ST. */
export function parseOsc5Reply(raw) {
    if (typeof raw !== 'string' || raw.length > bodyLimit + 4 || replyEnd(raw) !== raw.length) throw new Error('Unexpected special-color reply.');
    return raw;
}

/** Parse the accumulated bytes of one exchange: bounded replies, each ended by BEL or ST, then the DA barrier. Null while incomplete; throws on bytes no reply or the barrier starts with. */
export function parseOsc5Frame(bytes) {
    if (typeof bytes !== 'string' || bytes.length > exchangeLimit) throw new Error('Oversized special-color exchange.');
    const replies = [];
    let rest = bytes;
    for (;;) {
        if (!rest.startsWith(esc + ']')) {
            // The exchange ends when the remaining bytes are exactly the DA barrier. An incomplete barrier
            // prefix parses to null and anything else is refused.
            if (!rest || (esc + '[?').startsWith(rest) || (rest.length <= 128 && /^\x1b\[\?[\d;]*$/.test(rest))) return null;
            if (isBarrier(rest)) return { barrier: rest, replies };
            throw new Error('Unexpected special-color barrier bytes.');
        }
        if (replies.length === osc5ReplyLimit) throw new Error('Too many special-color replies.');
        const end = replyEnd(rest);
        if (end === -1) return null;
        replies.push(rest.slice(0, end));
        rest = rest.slice(end);
    }
}

/** A retained exchange: its raw replies (or explicit absence) and the DA barrier that ordered them. */
export function validateOsc5Exchange(value) {
    if (!value || typeof value !== 'object' || Object.keys(value).length !== 2 || !isBarrier(value.barrier)
        || value.barrier.length > 128 || !Array.isArray(value.replies) || value.replies.length > osc5ReplyLimit) {
        throw new Error('Invalid retained special-color report.');
    }
    return { barrier: value.barrier, replies: value.replies.map(raw => parseOsc5Reply(raw)) };
}
