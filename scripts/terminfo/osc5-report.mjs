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

/** Where one reply ends in `rest` (its BEL or ST terminator), or -1 while incomplete; 0 when the bytes are not a modeled reply at all; -2 when the body is malformed. */
function replyEnd(rest) {
    const osc5 = rest.startsWith(esc + ']5;');
    const legacy = !osc5 && /^\x1b\](4;\d+|10|11|12|17|19|21)/.test(rest);
    if (!osc5 && !legacy) return 0;
    const stAt = rest.indexOf(esc + '\\'); console.error('DBG replyEnd | osc5:', osc5, '| legacy:', legacy, '| stAt:', stAt, '| rest0-12:', JSON.stringify(rest.slice(0, 12)));
    const belAt = rest.indexOf(bel);
    if (stAt < 0 && belAt < 0) return -1;
    const terminator = stAt >= 0 && (belAt < 0 || stAt < belAt) ? stAt + 2 : belAt + 1;
    const prefix = osc5 ? 4 : rest.match(/^\x1b\][^;]*;/)[0].length;
    const body = rest.slice(prefix, terminator - (terminator === stAt + 2 ? 2 : 1));
    if (body.length > bodyLimit || body.includes(esc) || body.includes(bel)) return -2;
    return terminator;
}

/** Validate one retained reply: one complete OSC 5, DCS, palette-stack or legacy color string, bounded, with no embedded ESC. */
export function parseOsc5Reply(raw) {
    if (typeof raw !== 'string' || raw.length > bodyLimit + 4) throw new Error('Unexpected special-color reply.');
    if (raw.startsWith(esc + '[?') && /\d{1,2};\d{1,2}#Q$/.test(raw.slice(3))) return raw;
    if (raw.startsWith(esc + 'P') && raw.endsWith(esc + '\\')) return raw;
    if (!raw.startsWith(esc + ']')) throw new Error('Unexpected special-color reply.');
    const body = raw.slice(2, raw.endsWith(bel) ? -1 : -2);
    if (body.length > bodyLimit || body.includes(esc)) throw new Error('Unexpected special-color reply.');
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
            if (!rest || (esc + '[?').startsWith(rest)) return null;
            if (isBarrier(rest)) return { barrier: rest, replies };
            throw new Error('Unexpected special-color barrier bytes.');
        }
        if (replies.length === osc5ReplyLimit) throw new Error('Too many special-color replies.');
        const end = replyEnd(rest);
        if (end === -1) return null;
        if (end === -2) throw new Error('Unexpected special-color reply bytes.');
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
