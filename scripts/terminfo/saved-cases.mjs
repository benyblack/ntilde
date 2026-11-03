// Independent bounded input/expected-state plan; only the proved private worker executes these bytes.
const at = (row, col) => `\x1b[${row + 1};${col + 1}H`;
// Do not rely on the reset operation under test to establish independent controls.
const clean = '\x1b[!p\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[3J\x1b[H';
const fixtureReset = '\x1b[?1049l' + clean + '\x1b[?1049h' + clean + '\x1b[?47l' + clean;

const normal = { flags: 768, fg: -1, bg: -1, foregroundArgb: null, backgroundArgb: null,
    underlineStyle: 0, underlineColorIndex: -1, underlineArgb: null, overline: false };
const red = { ...normal, flags: 16897, fg: 1 }, green = { ...normal, flags: 16897, fg: 2 };
const clone = value => structuredClone(value);
function state(rows, cols, alternate) {
    return { grid: Array.from({ length: rows }, () => Array.from({ length: cols }, () => ({ text: ' ', ...normal }))),
        row: 0, col: 0, top: 0, bottom: rows - 1, alternate, originMode: false, autoWrap: true,
        insertMode: false, pendingWrap: false, history: [], cursorMode: { style: 2, blink: true, isDefault: true } };
}
function write(value, row, col, text, pen = normal) {
    for (const [offset, char] of [...text].entries()) value.grid[row][col + offset] = { text: char, ...pen };
    value.row = row; value.col = col + text.length; return value;
}
function validateGeometry(rows, cols) {
    if (!Number.isSafeInteger(rows) || rows < 10 || rows > 128 || !Number.isSafeInteger(cols)
        || cols < 48 || cols > 256 || rows * cols > 8192) throw Object.assign(new Error('Unsupported saved-state fixture grid.'), { code: 'savedGeometryUnavailable' });
}
export function makeAlternateCases(rows, cols) {
    validateGeometry(rows, cols);
    const main = write(state(rows, cols, false), 0, 0, 'MAIN000'); main.row = 1; main.col = 5;
    const alt = write(state(rows, cols, true), 2, 3, 'ALT000', red);
    const setup = fixtureReset + 'MAIN000' + at(1, 5) + '\x1b[32;1m\x1b[?47h' + at(2, 3)
        + '\x1b[31;1mALT000\x1b[?47l';
    const values = [];
    for (const mode of [47, 1047]) {
        const active = write(clone(alt), 2, 9, 'Z', red);
        const returned = mode === 47 ? clone(active) : state(rows, cols, true);
        returned.row = 2; returned.col = 10;
        values.push({ id: `saved.alternate.${mode}`, setup, stages: [
            { id: 'control', sequence: '', expected: clone(main) },
            { id: 'entry', sequence: `\x1b[?${mode}h`, expected: clone(alt) },
            { id: 'active', sequence: 'Z', expected: active },
            { id: 'exit', sequence: `\x1b[?${mode}l`, expected: clone(main) },
            { id: 'main-pen', sequence: 'M', expected: write(clone(main), 1, 5, 'M', green) },
            { id: 'reentry', sequence: '\x1b[?47h', expected: clone(returned) },
            { id: 'alternate-pen', sequence: 'T', expected: write(clone(returned), 2, 10, 'T', red) },
        ] });
    }
    const cleared = state(rows, cols, true), active = write(clone(cleared), 0, 0, 'Z');
    write(active, 3, 4, 'B', red);
    values.push({ id: 'saved.alternate.1049', setup, stages: [
        { id: 'control', sequence: '', expected: clone(main) },
        { id: 'entry', sequence: '\x1b[?1049h', expected: cleared },
        { id: 'active', sequence: 'Z' + at(3, 4) + '\x1b[31;1mB', expected: clone(active) },
        { id: 'repeated-entry', sequence: '\x1b[?1049h', expected: clone(active) },
        { id: 'exit', sequence: '\x1b[?1049l', expected: clone(main) },
        { id: 'main-pen', sequence: 'M', expected: write(clone(main), 1, 5, 'M', green) },
        { id: 'reentry', sequence: '\x1b[?47h', expected: clone(active) },
        { id: 'alternate-pen', sequence: 'T', expected: write(clone(active), 3, 5, 'T', red) },
    ] });
    return values;
}

const full = { ...normal, flags: 255, foregroundArgb: 0xff112233, backgroundArgb: 0xff445566,
    underlineStyle: 3, underlineArgb: 0xff778899, overline: true };
const fullPen = '\x1b[1;2;3;4:3;5;7;8;9;53;38;2;17;34;51;48;2;68;85;102;58;2;119;136;153m';
export function makeCursorCases(rows, cols) {
    validateGeometry(rows, cols);
    return [
        { id: 'saved.cursor.main-1048', alt: false, save: '\x1b[?1048h', restore: '\x1b8' },
        { id: 'saved.cursor.alternate-dec', alt: true, save: '\x1b7', restore: '\x1b[?1048l' },
        { id: 'saved.cursor.main-ansi', alt: false, save: '\x1b[s', restore: '\x1b[u' },
    ].map(spec => {
        const initial = write(state(rows, cols, spec.alt), 2, cols - 1, '─', full);
        Object.assign(initial, { top: 1, bottom: rows - 2, originMode: true, row: 2, col: cols - 1, pendingWrap: true });
        const changed = write(clone(initial), 0, 0, 'D');
        Object.assign(changed, { originMode: false, autoWrap: false, pendingWrap: false });
        const restored = clone(changed);
        Object.assign(restored, { originMode: true, row: 2, col: cols - 1, pendingWrap: true });
        // Re-enable wrapping before consuming the saved latch; do not assume wrap-off latch semantics.
        const wrapped = write(clone(restored), 3, 0, '─', full);
        Object.assign(wrapped, { autoWrap: true, pendingWrap: false });
        const ascii = write(clone(wrapped), 3, 1, 'q', full);
        return { id: spec.id,
            setup: fixtureReset + (spec.alt ? '\x1b[?47h' : '') + `\x1b[2;${rows - 1}r\x1b[?6h\x1b[2;${cols}H`
                + fullPen + '\x1b(B\x1b)0\x0eq' + spec.save,
            stages: [
                { id: 'control', sequence: '', expected: initial },
                { id: 'mutated', sequence: '\x1b[?6l\x1b[?7l\x1b(0\x1b)B\x0f\x1b[0m\x1b[1;1HD', expected: changed },
                { id: 'restored', sequence: spec.restore, expected: restored },
                { id: 'pending-wrap-pen-g1-witness', sequence: '\x1b[?7hq', expected: wrapped },
                { id: 'g0-witness', sequence: '\x0fq', expected: ascii },
            ] };
    });
}
export const makeSavedCases = (rows, cols) => [...makeAlternateCases(rows, cols), ...makeCursorCases(rows, cols), ...makeBankCases(rows, cols)];

export function makeBankCases(rows, cols) {
    validateGeometry(rows, cols);
    const main = write(state(rows, cols, false), 0, 0, 'M'); main.row = 1; main.col = 1;
    const alt = write(state(rows, cols, true), 0, 0, 'A'); alt.col = 0;
    const seed = fixtureReset + 'M' + at(2, 4) + '\x1b[31;1m\x1b7' + at(1, 1) + '\x1b[0m\x1b[?47hA'
        + at(3, 5) + '\x1b[32;1m\x1b7' + at(0, 0) + '\x1b[0m';
    const altRestored = clone(alt); altRestored.row = 3; altRestored.col = 5;
    const altPen = write(clone(altRestored), 3, 5, 'q', green);
    const mainPen = write(clone(main), 2, 4, 'q', red);
    const yellow = { ...normal, flags: 16897, fg: 3 };
    const soft = write(clone(alt), 0, 1, 'Y', yellow);
    const resetBank = write(clone(soft), 0, 0, 'q');
    const blankMain = state(rows, cols, false), blankAlt = state(rows, cols, true);
    const mainQ = write(clone(blankMain), 0, 0, 'q'), altQ = write(clone(blankAlt), 0, 0, 'q');
    return [
        { id: 'saved.banks.independent', setup: seed, stages: [
            { id: 'control', sequence: '', expected: clone(alt) },
            { id: 'alternate-restore', sequence: '\x1b8', expected: altRestored },
            { id: 'alternate-pen', sequence: 'q', expected: clone(altPen) },
            { id: 'main-live', sequence: '\x1b[?47l', expected: clone(main) },
            { id: 'main-bank-pen', sequence: '\x1b8q', expected: clone(mainPen) },
            { id: 'alternate-retained', sequence: '\x1b[?47h', expected: clone(altPen) },
        ] },
        { id: 'saved.reset.soft-active-bank', setup: seed + at(0, 1) + '\x1b[33;1mY', stages: [
            { id: 'control', sequence: '', expected: clone(soft) },
            { id: 'soft-reset', sequence: '\x1b[!p', expected: clone(soft) },
            { id: 'active-bank-default', sequence: '\x1b8q', expected: clone(resetBank) },
            { id: 'main-bank-preserved', sequence: '\x1b[?47l\x1b8q', expected: clone(mainPen) },
            { id: 'alternate-retained', sequence: '\x1b[?47h', expected: clone(resetBank) },
        ] },
        { id: 'saved.reset.full-both-banks', setup: seed, stages: [
            { id: 'control', sequence: '', expected: clone(alt) },
            { id: 'full-reset', sequence: '\x1bc', expected: blankMain },
            { id: 'main-bank-default', sequence: '\x1b8q', expected: clone(mainQ) },
            { id: 'alternate-cleared', sequence: '\x1b[?47h', expected: blankAlt },
            { id: 'alternate-bank-default', sequence: '\x1b8q', expected: altQ },
            { id: 'main-retained', sequence: '\x1b[?47l', expected: clone(mainQ) },
        ] },
    ];
}

export const savedSuiteId = 'ntilde-owned-saved-alternate-v1';
export const ownedSavedPolicy = 'paired owned active-screen facts and source-known restoration witnesses; no direct inactive-bank inspection';
export const savedReset = fixtureReset;

/** Execute the fixed private plan serially; reset failure never becomes a completion receipt. */
export async function collectSaved({ config, fence, owner }) {
    let initial;
    try { initial = await owner('saved-start'); }
    catch (error) {
        if (['screenDetailsUnavailable', 'savedGeometryUnavailable'].includes(error.code)) return { status: 'unavailable', reason: error.code };
        throw error;
    }
    const { pristineEditing } = await import('./editing-capture.mjs');
    if (!pristineEditing(initial.screen)) throw new Error('Saved-state fixture requires a pristine private main screen.');
    const cases = makeSavedCases(initial.screen.rows, initial.screen.cols);
    try {
        await owner('saved-bind', await fence.write('\x1b[?1049h\x1b[!p\x1b[HNTILDE_SV_' + config.runId));
        await cases.reduce((previous, item) => previous.then(() => item.stages.reduce((prior, stage, index) => prior.then(async () => {
            const barrier = await fence.write((index === 0 ? item.setup : '') + stage.sequence);
            await owner('saved-read', barrier, { caseId: item.id, stageId: stage.id });
        }), Promise.resolve())), Promise.resolve());
        await owner('saved-finish', await fence.write(savedReset));
    } catch (error) {
        try {
            if (fence.failed) await fence.writeCleanup(savedReset);
            else await fence.write(savedReset);
        } catch (error_) { throw new AggregateError([error, error_], 'Saved-state collection and cleanup failed.'); }
        throw error;
    }
}
