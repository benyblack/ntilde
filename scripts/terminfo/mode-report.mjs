// Bounded owned DECRPM exchanges (DECRQM, `CSI ? Pd $ p`, answered by `CSI ? Pd ; Ps $ y`). The raw reply is retained byte for byte
// beside the mode it answers; a complete DA reply orders the exchange, and is never itself a DECRPM reply. A reply for another mode,
// a malformed status or a second reply is refused rather than adapted, and a missing reply is an explicit absence, never a guess.
const esc = '\x1b';
const reply = /^\x1b\[\?(\d{1,4});(\d)\$y$/;
const barrierPattern = /^\x1b\[\?[\d;]+c$/;
/** DECRPM status values (DEC STD 070 / ECMA-48 private form): 0 not recognized, 1 set, 2 reset, 3 permanently set, 4 permanently reset. */
export const modeStatusNames = Object.freeze(['not-recognized', 'set', 'reset', 'permanently-set', 'permanently-reset']);

const validMode = mode => Number.isSafeInteger(mode) && mode >= 1 && mode <= 9999;

/** The DECRQM request bytes for one private mode. */
export function modeReportQuery(mode) {
    if (!validMode(mode)) throw new Error('Invalid owned mode report request.');
    return `${esc}[?${mode}$p`;
}

/** The fence's expectation tag for one mode exchange (the mode is part of the contract: a reply for another mode is refused). */
export function modeReportExpectation(mode) {
    if (!validMode(mode)) throw new Error('Invalid owned mode report request.');
    return Object.freeze({ kind: 'mode-report', mode });
}

/** Parse one raw DECRPM reply for exactly `mode`, or refuse it. */
export function parseModeReport(raw, mode) {
    const match = typeof raw === 'string' && raw.length <= 16 && validMode(mode) ? reply.exec(raw) : null;
    if (!match) throw new Error('Unexpected mode report.');
    const answered = Number(match[1]), status = Number(match[2]);
    if (answered !== mode) throw new Error('Mode report answers a different mode.');
    if (status > 4) throw new Error('Unexpected mode report status.');
    return { raw, mode: answered, status };
}

/**
 * Parse the accumulated bytes of one exchange: an optional DECRPM reply followed by the DA barrier. Null means incomplete. A
 * complete DA without a reply is an explicitly absent reply; a DA reply is never accepted in place of the mode reply.
 */
export function parseModeReportFrame(bytes, mode) {
    if (typeof bytes !== 'string' || bytes.length > 160) throw new Error('Oversized mode report barrier.');
    if (!bytes || bytes === esc) return null;
    let rest = bytes, report = null;
    const complete = /^\x1b\[\?\d{1,4};\d\$y/.exec(rest);
    if (complete) {
        report = parseModeReport(complete[0], mode);
        rest = rest.slice(complete[0].length);
    } else if (/^\x1b\[\?[\d;]*\$?$/.test(rest)) {
        return null; // a prefix of a reply (or of the barrier) that is not complete yet
    }
    if (!rest || (esc + '[?').startsWith(rest)) return null;
    if (barrierPattern.test(rest)) return { barrier: rest, reply: report };
    if (!/^\x1b\[\?[\d;]*$/.test(rest)) throw new Error('Unexpected mode report barrier bytes.');
    return null;
}

/** A retained exchange: its mode, raw reply (or explicit absence) and DA barrier. */
export function validateModeReport(value) {
    if (!value || typeof value !== 'object' || !validMode(value.mode) || !barrierPattern.test(value.barrier ?? '') || value.barrier.length > 128
        || !Object.hasOwn(value, 'reply')) {
        throw new Error('Invalid retained mode report.');
    }
    if (value.reply === null) return { mode: value.mode, barrier: value.barrier, reply: null };
    const parsed = parseModeReport(value.reply?.raw, value.mode);
    if (value.reply.mode !== parsed.mode || value.reply.status !== parsed.status) throw new Error('Retained mode report disagrees with its raw bytes.');
    return { mode: value.mode, barrier: value.barrier, reply: parsed };
}
