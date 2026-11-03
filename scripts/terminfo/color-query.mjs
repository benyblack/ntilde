// Bounded owned OSC color replies; DA establishes ordering and explicit reply absence.
const esc = '\x1b';
export function querySpec(value) {
    if (!value || ![4, 10, 11, 12, 17, 19].includes(value.code)
        || Object.keys(value).some(key => !['code', 'index'].includes(key))) throw new Error('Invalid owned color query.');
    if (value.code === 4) {
        if (!Number.isSafeInteger(value.index) || value.index < 0 || value.index > 255) throw new Error('Invalid palette query.');
        return { code: 4, index: value.index };
    }
    if (value.index !== undefined) throw new Error('Unexpected palette index.');
    return { code: value.code };
}
export function querySequence(value) {
    const spec = querySpec(value);
    const index = spec.code === 4 ? spec.index + ';' : '';
    return `${esc}]${spec.code};${index}?${esc}\\`;
}
export function parseColorReply(raw, expected) {
    const spec = querySpec(expected);
    const prefix = `${esc}]${spec.code};${spec.code === 4 ? spec.index + ';' : ''}`;
    if (typeof raw !== 'string' || raw.length > 128 || !raw.startsWith(prefix) || !raw.endsWith(esc + '\\')) throw new Error('Unexpected color reply.');
    const match = /^rgb:([\da-f]{4})\/([\da-f]{4})\/([\da-f]{4})$/i.exec(raw.slice(prefix.length, -2));
    if (!match) throw new Error('Malformed color reply.');
    return { raw, channels16: match.slice(1).map(value => Number.parseInt(value, 16)) };
}
/** Null means incomplete. A complete DA can establish an explicitly absent OSC reply. */
export function parseQueryFrame(bytes, expected) {
    const spec = querySpec(expected);
    if (typeof bytes !== 'string' || bytes.length > 512) throw new Error('Oversized color barrier.');
    let rest = bytes, reply = null;
    if (!rest || rest === esc) return null;
    if (rest.startsWith(esc + ']')) {
        const end = rest.indexOf(esc + '\\', 2);
        if (end < 0) {
            if (rest.includes('\x07')) throw new Error('Unexpected color terminator.');
            return null;
        }
        reply = parseColorReply(rest.slice(0, end + 2), spec);
        rest = rest.slice(end + 2);
    }
    if (!rest || (esc + '[?').startsWith(rest)) return null;
    if (/^\x1b\[\?[\d;]+c$/.test(rest)) return { barrier: rest, reply };
    if (!/^\x1b\[\?[\d;]*$/.test(rest)) throw new Error('Unexpected color barrier bytes.');
    return null;
}
