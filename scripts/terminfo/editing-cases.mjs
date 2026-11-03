// Independent bounded ASCII expectations; only the proved private worker executes fixture bytes.
const at = (row, col) => `\x1b[${row + 1};${col + 1}H`;
const normal = () => ({ overline: false, flags: 768, fg: -1, bg: -1, foregroundArgb: null, backgroundArgb: null,
    underlineStyle: 0, underlineColorIndex: -1, underlineArgb: null });
const cell = (text = ' ') => ({ text, ...normal() });
const clone = grid => grid.map(row => row.map(value => ({ ...value })));
const reset = '\x1b[!p\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[H';
export function makeEditingCases(rows, cols) {
    if (!Number.isSafeInteger(rows) || rows < 10 || rows > 128 || !Number.isSafeInteger(cols) || cols < 48 || cols > 256 || rows * cols > 8192) throw new Error('Invalid fixture grid.');
    const rowText = row => `ROW${String(row).padStart(3, '0')}-ABCDEFGH`;
    const seed = Array.from({ length: rows }, (_, row) => Array.from({ length: cols }, (_, col) => cell(rowText(row)[col] ?? ' ')));
    const writes = seed.map((_, row) => at(row, 0) + rowText(row).padEnd(cols, ' ')).join('');
    const fixtures = [];
    function add(id, sequence, transform, { row = 2, col = 4, top = 0, bottom = rows - 1, alternate = true, prefix = '', beforeTransform = () => {}, colorPen = false } = {}) {
        const before = clone(seed); beforeTransform(before);
        const pen = '\x1b[38;2;204;1;77;48;2;1;44;199m';
        if (colorPen) before[0][0] = { ...cell('P'), flags: 0, foregroundArgb: 0xffcc014d, backgroundArgb: 0xff012cc7 };
        const after = clone(before), cursor = { row, col };
        transform(after, cursor);
        fixtures.push({ id, sequence, setup: reset + writes + (colorPen ? at(0, 0) + pen + 'P\x1b[0m' : '') + (top || bottom !== rows - 1 ? `\x1b[${top + 1};${bottom + 1}r` : '') + at(row, col) + prefix + (colorPen ? pen : ''),
            before: { grid: before, row, col, top, bottom, alternate }, after: { grid: after, ...cursor, top, bottom, alternate } });
    }
    addDisplayCases(add, rows, cols);
    addEraseCharacterCases(add, cols);
    addCharacterEditingCases(add, cols);
    addLineEditingCases(add, cols);
    addRepeatCases(add);
    addAlignmentCase(add);
    addMarginEraseCases(add, rows, cols);
    addSavedLinesCase(fixtures, seed, writes, rows);
    // Known nonempty history is an independent control even for viewport-wide operations.
    // Prime it without ED3, and keep the main viewport empty before entering the alt screen.
    const history = ['HISTORY0', 'HISTORY1'];
    const prime = '\x1b[?1049l' + reset + history.join('\r\n') + '\r\n'.repeat(rows) + at(0, 0) + '\x1b[?1049h';
    for (const item of fixtures) {
        item.before.history = [...history];
        if (item.id !== 'erase.saved-lines') item.after.history = [...history];
    }
    for (const prefix of ['erase.', 'editing.']) {
        const first = fixtures.find(item => item.id.startsWith(prefix));
        first.setup = prime + first.setup;
    }
    return fixtures;
}

function addDisplayCases(add, rows, cols) {
    for (const parameter of ['', '0', '1', '2']) add(`erase.display.${parameter || 'default'}`, `\x1b[${parameter}J`, (grid, cursor) => {
        const position = cursor.row * cols + cursor.col;
        for (let index = 0; index < rows * cols; index++) {
            let erased = index >= position;
            if (parameter === '1') erased = index <= position;
            else if (parameter === '2') erased = true;
            if (erased) grid[Math.floor(index / cols)][index % cols] = cell();
        }
    });
}

function addEraseCharacterCases(add, cols) {
    for (const parameter of ['', '0', '3', '999']) add(`erase.characters.${parameter || 'default'}`, `\x1b[${parameter}X`, (grid, cursor) => {
        const count = Math.min(cols - cursor.col, Number(parameter) || 1);
        for (let col = cursor.col; col < cursor.col + count; col++) grid[cursor.row][col] = cell();
    });
}

function addCharacterEditingCases(add, cols) {
    for (const command of ['@', 'P']) for (const parameter of ['', '0', '2', '999']) add(`editing.${command === '@' ? 'insert' : 'delete'}-characters.${parameter || 'default'}`, `\x1b[${parameter}${command}`, (grid, cursor) => {
        const line = grid[cursor.row], count = Math.min(cols - cursor.col, Number(parameter) || 1), blanks = Array.from({ length: count }, () => cell());
        if (command === '@') { line.splice(cursor.col, 0, ...blanks); line.length = cols; }
        else { line.splice(cursor.col, count); line.push(...blanks); }
    });
}

function addLineEditingCases(add, cols) {
    for (const command of ['L', 'M']) for (const parameter of ['', '0', '2', '999']) add(`editing.${command === 'L' ? 'insert' : 'delete'}-lines.${parameter || 'default'}`, `\x1b[${parameter}${command}`, (grid, cursor) => {
        const bottom = 6, count = Math.min(bottom - cursor.row + 1, Number(parameter) || 1);
        const region = grid.slice(cursor.row, bottom + 1), blanks = Array.from({ length: count }, () => Array.from({ length: cols }, () => cell()));
        const changed = command === 'L' ? [...blanks, ...region].slice(0, region.length) : [...region.slice(count), ...blanks];
        grid.splice(cursor.row, region.length, ...changed);
    }, { top: 1, bottom: 6 });
    for (const command of ['L', 'M']) add(`editing.${command === 'L' ? 'insert' : 'delete'}-lines.outside-margin`, `\x1b[2${command}`, () => {}, { row: 0, top: 1, bottom: 6 });
}

function addRepeatCases(add) {
    for (const parameter of ['', '0', '3']) add(`editing.repeat.${parameter || 'default'}`, `Q\x1b[${parameter}b`, (grid, cursor) => {
        const count = Number(parameter) || 1;
        for (let col = cursor.col; col < cursor.col + count + 1; col++) grid[cursor.row][col] = cell('Q');
        cursor.col += count + 1;
    }, { col: 4, prefix: at(0, 0) + 'Q' + at(2, 4), beforeTransform: grid => { grid[0][0] = cell('Q'); } });
}

function addAlignmentCase(add) {
    add('editing.screen-alignment', '\x1b#8', (grid, cursor) => {
        for (const line of grid) for (const value of line) Object.assign(value, cell('E'));
        cursor.row = 0; cursor.col = 0;
    });
}

function addMarginEraseCases(add, rows, cols) {
    for (const parameter of ['0', '1']) add(`erase.display.${parameter}.margins`, `\x1b[${parameter}J`, (grid, cursor) => {
        const position = cursor.row * cols + cursor.col;
        for (let index = 0; index < rows * cols; index++) {
            if (parameter === '0' ? index >= position : index <= position) grid[Math.floor(index / cols)][index % cols] = cell();
        }
    }, { top: 1, bottom: 6 });
    const bce = () => ({ text: ' ', bg: -1, backgroundArgb: 0xff012cc7, flagsMask: 512 | 32768, flagsValue: 0 });
    add('erase.characters.background-and-margins', '\x1b[3X', (grid, cursor) => {
        for (let col = cursor.col; col < cursor.col + 3; col++) grid[cursor.row][col] = bce();
    }, { row: 0, top: 1, bottom: 6, colorPen: true });
    add('erase.display.background-and-margins', '\x1b[0J', (grid, cursor) => {
        for (let index = cursor.row * cols + cursor.col; index < rows * cols; index++) grid[Math.floor(index / cols)][index % cols] = bce();
    }, { top: 1, bottom: 6, colorPen: true });
}

function addSavedLinesCase(fixtures, seed, writes, rows) {
    const historyBefore = clone(seed);
    fixtures.push({ id: 'erase.saved-lines', sequence: '\x1b[3J',
        setup: '\x1b[?1049l' + reset + writes + at(2, 4),
        before: { grid: historyBefore, row: 2, col: 4, top: 0, bottom: rows - 1, alternate: false, history: ['HISTORY0', 'HISTORY1'] },
        after: { grid: clone(seed), row: 2, col: 4, top: 0, bottom: rows - 1, alternate: false, history: [] } });
}

export const eraseSuiteId = 'ntilde-owned-erasure-v1';
export const editingSuiteId = 'ntilde-owned-cell-editing-v1';
const finalRestore = '\x1b[?1049l\x1b[!p\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[3J\x1b[H\x1b[?25h';
async function collectEditingSuite({ config, fence, owner }, prefix, noncePrefix) {
    let initial;
    try { initial = await owner(prefix + '-start'); }
    catch (error) {
        if (error.code === 'screenDetailsUnavailable') return { status: 'unavailable', reason: error.code };
        throw error;
    }
    const { pristineEditing } = await import('./editing-capture.mjs');
    if (!pristineEditing(initial.screen)) throw new Error('Editing requires a pristine private main screen and empty history.');
    const cases = makeEditingCases(initial.screen.rows, initial.screen.cols).filter(item => item.id.startsWith(prefix + '.'));
    try {
        await owner(prefix + '-bind', await fence.write('\x1b[?1049h' + reset + noncePrefix + config.runId));
        await cases.reduce((previous, item) => previous.then(async () => {
            await owner(prefix + '-read', await fence.write(item.setup), { caseId: item.id, stageId: 'control' });
            await owner(prefix + '-read', await fence.write(item.sequence), { caseId: item.id, stageId: 'after' });
        }), Promise.resolve());
    } finally {
        if (fence.failed) await fence.writeCleanup(finalRestore);
        else await owner(prefix + '-finish', await fence.write(finalRestore));
    }
}
export const collectErase = options => collectEditingSuite(options, 'erase', 'NTILDE_ER_');
export const collectEditing = options => collectEditingSuite(options, 'editing', 'NTILDE_EC_');
