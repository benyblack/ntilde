// Bounded ASCII fixtures. The parent derives these expectations; the proved worker writes the bytes.
const at = (row, col) => `\x1b[${row + 1};${col + 1}H`;
const reset = '\x1b[!p\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[H';
const main = '\x1b[?1049l' + reset + '\x1b[2J\x1b[3J';
const normalCell = (text = ' ') => ({ text, overline: false, flags: 768, fg: -1, bg: -1,
    foregroundArgb: null, backgroundArgb: null, underlineStyle: 0, underlineColorIndex: -1, underlineArgb: null });
const copy = state => structuredClone(state);
const text = row => row.map(value => value.text).join('').trimEnd();
function scroll(state, count, up) {
    const { top, bottom, grid } = state, region = grid.slice(top, bottom + 1);
    const amount = Math.min(count, region.length);
    const blanks = Array.from({ length: amount }, () => Array.from({ length: grid[0].length }, () => normalCell()));
    if (up && !state.alternate && top === 0 && bottom === grid.length - 1) state.history.push(...region.slice(0, amount).map(text));
    const changed = up ? [...region.slice(amount), ...blanks] : [...blanks, ...region.slice(0, region.length - amount)];
    grid.splice(top, region.length, ...changed);
}
function addLineFeedCases(add, rows) {
    for (const [id, sequence, col] of [['lf', '\n', 4], ['crlf', '\r\n', 0], ['index', '\x1bD', 4]]) {
        add('history.accumulate.' + id, sequence, state => { scroll(state, 1, true); state.col = col; },
            { row: rows - 1, history: [] });
    }
}
function addScrollVariants(add, command, up, alternate) {
    const direction = up ? 'up' : 'down', screen = alternate ? 'alt' : 'main';
    const prefix = `history.scroll-${direction}.${screen}`;
    for (const parameter of ['', '0', '2']) {
        add(`${prefix}.${parameter || 'default'}`, `\x1b[${parameter}${command}`,
            state => scroll(state, Number(parameter) || 1, up), { alternate });
    }
    add(prefix + '.margins', `\x1b[2${command}`, state => scroll(state, 2, up), { alternate, top: 1, bottom: 6 });
}
function addScrollingCases(add) {
    for (const [command, up] of [['S', true], ['T', false]]) {
        for (const alternate of [false, true]) addScrollVariants(add, command, up, alternate);
    }
}
function addReverseIndexCases(add) {
    const reverse = state => { if (state.row === state.top) scroll(state, 1, false); else state.row = Math.max(0, state.row - 1); };
    for (const alternate of [false, true]) add(`history.reverse-index.${alternate ? 'alt' : 'main'}.top`, '\x1bM', reverse, { row: 0, alternate });
    for (const [name, row] of [['top', 1], ['middle', 3], ['outside', 0]]) {
        add('history.reverse-index.margins.' + name, '\x1bM', reverse, { row, top: 1, bottom: 6 });
    }
}
function addMarginCases(add, rows) {
    add('history.margins.set', '\x1b[2;7r', state => { state.top = 1; state.bottom = 6; state.row = 0; state.col = 0; });
    for (const [name, sequence] of [['default', '\x1b[r'], ['zero', '\x1b[0;0r']]) {
        add('history.margins.' + name, sequence, state => { state.top = 0; state.bottom = rows - 1; state.row = 0; state.col = 0; }, { top: 1, bottom: 6 });
    }
    // The cursor is already home: these cases qualify rejection of invalid margins only.
    for (const [name, sequence] of [['reversed', '\x1b[7;2r'], ['equal', '\x1b[3;3r']]) {
        add('history.margins.invalid-' + name, sequence, () => {}, { top: 1, bottom: 6, row: 0, col: 0 });
    }
    add('history.margins.origin-home', '\x1b[3;8r', state => { state.top = 2; state.bottom = 7; state.row = 2; state.col = 0; },
        { top: 1, bottom: 6, row: 3, originMode: true });
}
export function makeHistoryCases(rows, cols) {
    if (!Number.isSafeInteger(rows) || rows < 10 || rows > 128 || !Number.isSafeInteger(cols) || cols < 48 || cols > 256 || rows * cols > 8192) throw new Error('Invalid history fixture grid.');
    const rowText = row => `HROW${String(row).padStart(3, '0')}-ABCDEFGH`;
    const grid = Array.from({ length: rows }, (_, row) => Array.from({ length: cols }, (_, col) => normalCell(rowText(row)[col] ?? ' ')));
    const writes = grid.map((_, row) => at(row, 0) + rowText(row).padEnd(cols, ' ')).join('');
    const fixtures = [];
    function add(id, sequence, transform, options = {}) {
        const before = { grid: copy(grid), row: 2, col: 4, top: 0, bottom: rows - 1, alternate: false,
            originMode: false, history: ['HISTORY0', 'HISTORY1'], ...options };
        const after = copy(before); transform(after);
        const prime = before.history.length ? before.history.join('\r\n') + '\r\n'.repeat(rows) + at(0, 0) : '';
        const screen = before.alternate ? '\x1b[?1049h' : '';
        const margins = `\x1b[${before.top + 1};${before.bottom + 1}r`;
        const origin = before.originMode ? '\x1b[?6h' : '';
        const cursor = at(before.row - (before.originMode ? before.top : 0), before.col);
        fixtures.push({ id, sequence, setup: main + prime + screen + reset + writes + margins + origin + cursor, before, after });
    }
    addLineFeedCases(add, rows); addScrollingCases(add); addReverseIndexCases(add); addMarginCases(add, rows);
    return fixtures;
}
export const historySuiteId = 'ntilde-owned-history-scrolling-v1';

const finalRestore = '\x1b[?1049l' + reset + '\x1b[2J\x1b[3J\x1b[H\x1b[?25h';
export async function collectHistory({ config, fence, owner }) {
    let initial;
    try { initial = await owner('history-start'); }
    catch (error) {
        if (error.code === 'screenDetailsUnavailable') return { status: 'unavailable', reason: error.code };
        throw error;
    }
    const { pristineEditing } = await import('./editing-capture.mjs');
    if (!pristineEditing(initial.screen)) throw new Error('History fixture requires a pristine private main screen.');
    const cases = makeHistoryCases(initial.screen.rows, initial.screen.cols);
    try {
        await owner('history-bind', await fence.write('\x1b[?1049h' + reset + 'NTILDE_HS_' + config.runId));
        await cases.reduce((previous, item) => previous.then(async () => {
            await owner('history-read', await fence.write(item.setup), { caseId: item.id, stageId: 'control' });
            await owner('history-read', await fence.write(item.sequence), { caseId: item.id, stageId: 'after' });
        }), Promise.resolve());
    } finally {
        if (fence.failed) await fence.writeCleanup(finalRestore);
        else await owner('history-finish', await fence.write(finalRestore));
    }
}
