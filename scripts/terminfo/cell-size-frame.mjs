// Bounded transport grammar only. A complete DA establishes ordered absence;
// wrong but fully framed replies remain available for the independent oracle.
export function parseCellSizeFrame(bytes) {
    if (typeof bytes !== 'string' || bytes.length > 512) throw new Error('Invalid cell-size frame.');
    let rest = bytes;
    const replies = [];
    while (rest.startsWith('\x1b]') || rest.startsWith('\x1b[6;')) {
        const match = rest.startsWith('\x1b]') ? /^\x1b\][\x20-\x7e]{0,128}?(?:\x07|\x1b\\)/.exec(rest)
            : /^\x1b\[6;[0-9;]{1,32}t/.exec(rest);
        if (!match) {
            if (rest.length > 160 || /[^\x1b\x20-\x7e]/.test(rest)) throw new Error('Invalid cell-size reply.');
            return null;
        }
        replies.push(match[0]);
        if (replies.length > 4) throw new Error('Too many cell-size replies.');
        rest = rest.slice(match[0].length);
    }
    if (/^\x1b\[\?[0-9;]+c$/.test(rest)) return { replies, barrier: rest };
    if (rest === '' || '\x1b[6;'.startsWith(rest) || '\x1b[?'.startsWith(rest) || /^\x1b\[\?[0-9;]*$/.test(rest)) return null;
    throw new Error('Unexpected cell-size exchange bytes.');
}
export function validateCellSizeExchange(value) {
    if (!value || Object.keys(value).sort().join(',') !== 'barrier,replies' || !Array.isArray(value.replies)
        || typeof value.barrier !== 'string') throw new Error('Invalid retained cell-size exchange.');
    const result = parseCellSizeFrame(value.replies.join('') + value.barrier);
    if (JSON.stringify(result) !== JSON.stringify({ replies: value.replies, barrier: value.barrier })) throw new Error('Incomplete cell-size exchange.');
    return result;
}
