// Bounded owned device-query exchanges. One request list earns a bounded *sequence* of complete replies (a DCS, an OSC or an xterm palette-stack CSI report)
// followed by the DA barrier that orders the exchange. Every reply is retained byte for byte; whether it is the right reply for the request is the contract's
// business, never this parser's. A complete DA barrier with no reply before it is an explicitly absent reply list, never a reply guessed from state.
const esc = '\x1b';
/** A complete DA reply: ESC, then [?, parameters and c (the ESC is checked outside the pattern). */
const isBarrier = text => typeof text === 'string' && text.startsWith(esc) && /^\[\?[\d;]+c$/.test(text.slice(1));
/** The longest string-reply body (the bytes between the introducer and ST) one retained reply may hold. */
const bodyLimit = 128;
/** The most replies one exchange may retain. */
export const devqReplyLimit = 8;
/** The longest exchange: bounded replies plus a DA barrier. */
const exchangeLimit = 1024;

/** The fence's expectation tag for one device-query exchange (identity-compared, never parsed). */
export const devqReportExpectation = Object.freeze({ kind: 'device-query' });

/** Whether `text` is ESC and an introducer, at most 128 bytes without ESC, then (when `closed`) ST or (when not) at most a lone trailing ESC. */
function stringShape(text, introducer, closed) {
    if (!text.startsWith(esc + introducer)) return false;
    let body = text.slice(2);
    if (closed) {
        if (!body.endsWith(esc + '\\')) return false;
        body = body.slice(0, -2);
    } else if (body.endsWith(esc)) {
        body = body.slice(0, -1);
    }
    return body.length <= bodyLimit && !body.includes(esc);
}

/** The xterm palette-stack report after its ESC: [ ? depth ; deepest # Q. */
const paletteReport = /^\[\?\d{1,2};\d{1,2}#Q$/;
const paletteReportStart = /^\[\?\d{1,2};\d{1,2}#Q/;

/** Refuse anything but one complete bounded DCS or OSC string, or one complete palette-stack report. */
export function parseDevqReply(raw) {
    if (typeof raw !== 'string') throw new Error('Unexpected device-query reply.');
    if (raw.startsWith(esc + '[')) {
        if (!paletteReport.test(raw.slice(1))) throw new Error('Unexpected device-query reply.');
        return { raw };
    }
    if (!stringShape(raw, 'P', true) && !stringShape(raw, ']', true)) throw new Error('Unexpected device-query reply.');
    return { raw };
}

/** Where one reply ends in `rest` (a string reply at its ST, a palette report at its Q), or -1 while incomplete; throws on bytes no reply starts with. */
function replyEnd(rest) {
    if (rest.startsWith(esc + 'P') || rest.startsWith(esc + ']')) {
        const end = rest.indexOf(esc + '\\');
        if (end < 0) {
            if (!stringShape(rest, rest[1], false)) throw new Error('Unexpected device-query bytes.');
            return -1;
        }
        return end + 2;
    }
    return -1;
}

/**
 * Parse the accumulated bytes of one exchange: any number of complete bounded replies followed by the DA barrier. Null means incomplete. A complete DA without
 * a reply is an explicitly absent reply list, never a reply guessed from state.
 */
export function parseDevqFrame(bytes) {
    if (typeof bytes !== 'string' || bytes.length > exchangeLimit) throw new Error('Oversized device-query barrier.');
    const replies = [];
    let rest = bytes;
    for (;;) {
        if (rest.startsWith(esc + 'P') || rest.startsWith(esc + ']')) {
            const length = replyEnd(rest);
            if (length < 0) return null;
            if (replies.length === devqReplyLimit) throw new Error('Too many device-query replies.');
            replies.push(parseDevqReply(rest.slice(0, length)).raw);
            rest = rest.slice(length);
            continue;
        }
        const report = rest.startsWith(esc) ? paletteReportStart.exec(rest.slice(1)) : null;
        if (report !== null) {
            if (replies.length === devqReplyLimit) throw new Error('Too many device-query replies.');
            replies.push(parseDevqReply(esc + report[0]).raw);
            rest = rest.slice(report[0].length + 1);
            continue;
        }
        break;
    }
    if (!rest || (esc + '[?').startsWith(rest)) return null;
    if (isBarrier(rest)) return { barrier: rest, replies };
    if (!rest.startsWith(esc) || !/^\[\?[\d;]*#?$/.test(rest.slice(1))) throw new Error('Unexpected device-query barrier bytes.');
    return null;
}

/** A retained exchange: its raw replies (or explicit absence) and DA barrier. */
export function validateDevqExchange(value) {
    if (!value || typeof value !== 'object' || Object.keys(value).length !== 2 || !isBarrier(value.barrier)
        || value.barrier.length > 128 || !Array.isArray(value.replies) || value.replies.length > devqReplyLimit) {
        throw new Error('Invalid retained device-query report.');
    }
    return { barrier: value.barrier, replies: value.replies.map(raw => parseDevqReply(raw).raw) };
}
