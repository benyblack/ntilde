// Independent re-derivation of the pinned termless criterion. Its inputs must
// come from the parent-journaled fixture reads, never from mode metadata alone.
const expected = 'IRM inserts X before measured ABC instead of replacing A';
const blank = char => char === '' || char === ' ';

function result(pass, state, note) {
    const response = JSON.stringify(state);
    let outcome = 'inconclusive';
    if (pass !== null) outcome = pass ? 'supported' : 'unsupported';
    return { pass: pass === true, response, ...(note && { note }),
        observation: { outcome,
            ...(pass === null && { reason: 'insufficient-evidence' }), evidence: 'parser-state', ...(note && { note }) },
        ...(pass !== null && { assertions: [{ kind: pass ? 'positive' : 'negative', expected, observed: response }] }) };
}

/** Derive the exact pinned result from separately measured seed, cursor and result. */
export function replayInsertReplacePinned({ cols, rows, seed, origin, after }) {
    if (!Number.isSafeInteger(cols) || cols < 4 || !Number.isSafeInteger(rows) || rows < 1) {
        return result(null, { cols, rows }, 'IRM fixture needs at least 1x4');
    }
    if (!Array.isArray(seed) || seed.length !== 4 || seed.some(char => typeof char !== 'string')
        || !origin || !Number.isSafeInteger(origin.x) || !Number.isSafeInteger(origin.y) || typeof origin.visible !== 'boolean') {
        throw new Error('Missing or malformed pinned IRM seed or cursor evidence.');
    }
    if (seed[0] !== 'A' || seed[1] !== 'B' || seed[2] !== 'C' || !blank(seed[3]) || origin.x !== 0 || origin.y !== 0) {
        return result(null, { seed, origin }, 'Cannot verify IRM seed and cursor origin');
    }
    if (!Array.isArray(after) || after.length !== 4 || after.some(char => typeof char !== 'string')) {
        throw new Error('Missing or malformed pinned IRM result evidence.');
    }
    const inserted = after[0] === 'X' && after[1] === 'A' && after[2] === 'B' && after[3] === 'C';
    const replaced = after[0] === 'X' && after[1] === 'B' && after[2] === 'C' && blank(after[3]);
    const verdict = inserted || (replaced ? false : null);
    return result(verdict, { seed, origin, after },
        inserted || replaced ? undefined : 'Measured cells match neither insertion nor replacement');
}

/** Preserve the distinct terminal path: a cursor response cannot grade the cells. */
export function replayInsertReplaceTerminal({ cols, rows, position }) {
    if (!Number.isSafeInteger(rows) || !Number.isSafeInteger(cols) || rows < 1 || cols < 5) {
        return { pass: false, observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'none',
            note: `Insert-replace fixture needs at least 1x5, measured ${rows}x${cols}` } };
    }
    if (position === null) return { pass: false, observation: { outcome: 'inconclusive', reason: 'no-response', evidence: 'query' } };
    if (!position || !Number.isSafeInteger(position.row) || !Number.isSafeInteger(position.col)) {
        throw new Error('Missing or malformed terminal-path cursor evidence.');
    }
    return { pass: false, response: JSON.stringify(position), observation: { outcome: 'inconclusive', reason: 'insufficient-evidence',
        evidence: 'query', note: 'Cursor advance does not measure whether X displaced the existing cells' } };
}
