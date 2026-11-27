export const featureId = 'extensions.font-ligatures';
export const scope = Object.freeze({ id: 'ntilde-owned-font-ligatures-v1', persistence: 'Off', deadlineMs: 90000 });
export const prefix = 'ligatures-';
export const family = 'Fira Code';
export const fontSize = 26;
export const fontSha256 = '5992ab9640e2df491b2f609467b1de60e8bc39b2c28db184342a0592d98f6117';
export const resolvedFontSha256 = '17b8fa486c637b165ef747570fe84539e5c9423f1a10483423df6948f72893b1';
export const da = '\x1b[c';
export const nonce = runId => 'NTILDE_LIGATURES_' + runId;
export const fixtures = Object.freeze([
    { row: 2, start: 3, pair: '=>', text: 'L=>R', ordinary: [1578, 1580], shaped: [1457, 1461] },
    { row: 4, start: 3, pair: '!=', text: 'L!=R', ordinary: [1132, 1578], shaped: [1204, 1135] },
]);
const cup = (row, col) => '\x1b[' + (row + 1) + ';' + (col + 1) + 'H';
export const selections = [null, ...fixtures.map(({ row, start }, index) => ({
    startRow: row, startCol: start + index, endRow: row, endCol: start + index,
})), null];
export function requireLigatures(condition, reason) {
    if (!condition) throw new Error('Font ligatures: ' + reason);
}

export function planFor(runId, cursorVisible) {
    const positions = fixtures.flatMap(({ row, start }, index) => [start, start + 1].map((col, cell) => ({
        stage: 'cursor-' + index + '-' + cell, bytes: cup(row, col), query: true, cursor: [row, col],
    })));
    return [
        { stage: 'start', bytes: null, captures: [null] },
        { stage: 'bind', bytes: '\x1b[?1049h\x1b[2J\x1b[H' + nonce(runId) },
        { stage: 'fixture', bytes: '\x1b[?25l\x1b[2J' + fixtures.map(({ row, start, text }) => cup(row, start - 1) + text).join('') + cup(0, 0),
            captures: selections, cursor: [0, 0] },
        ...positions,
        { stage: 'finish', bytes: '\x1b[?1049l\x1b[?25' + (cursorVisible ? 'h' : 'l'), captures: [null] },
    ];
}
