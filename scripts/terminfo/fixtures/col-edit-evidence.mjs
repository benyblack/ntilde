// Synthetic, fully valid column-editing grading evidence for portable tests. Every frame, run-log entry and
// pinned result is produced through the same production validators used for real measurements. The expected
// rows are derived here with string slicing written independently of the contracts' array model, so the
// fixtures are a second oracle, not a mirror.
import { colEditContracts, colEditReset, colEditObservation, gradeColEditCase } from '../col-edit-contracts.mjs';
import { replayColEditPinned } from '../col-edit-grading.mjs';
import { colEditAdapterId } from '../col-edit-grading-collector.mjs';
import { paletteBinding, colorCell, plainCell, paletteChannel, syntheticColorEvidence, syntheticSgrPaletteScreen,
    sgrPaletteCols } from './sgr-palette-evidence.mjs';

export { paletteBinding, colorCell, plainCell } from './sgr-palette-evidence.mjs';

/** The synthetic screen height: the contract's minimum of thirteen rows. */
export const colEditRows = 13;
/** The synthetic screen width. */
export const colEditCols = sgrPaletteCols;
const W = colEditCols;

/** A synthetic screen: `rows` is an array of cell-spec rows (empty = blank row), `guarded` the zero-based protected [row, col] cells. */
export function syntheticColEditScreen(rows, cursor, { margins = [0, colEditRows - 1], origin = false, insert = false } = {}, guarded = [], binding = paletteBinding()) {
    const leading = [];
    for (let index = 0; index < colEditRows; index++) {
        const cells = rows[index] ?? [];
        leading.push(...cells, ...Array.from({ length: W - cells.length }, () => plainCell(' ')));
    }
    const screen = syntheticSgrPaletteScreen(leading, binding, { rows: colEditRows, cols: W });
    const lines = Array.from({ length: colEditRows }, (_, index) => (rows[index] ?? []).map(cell => cell.char).join('').trimEnd());
    const positions = new Set(guarded.map(([row, col]) => row * W + col));
    return { ...screen, lines, cursorRow: cursor[0], cursorCol: cursor[1], cellDetails: annotate(screen.cellDetails, positions),
        bufferState: { scrollTop: margins[0], scrollBottom: margins[1], originMode: origin, autoWrap: true, insertMode: insert, pendingWrap: false,
            alternateScreen: false, scrollbackLines: 0 },
        history: { startLine: 0, totalLines: 0, lines: [] } };
}

/** Add per-cell protection to stored detail runs: expand, annotate, coalesce canonically. */
function annotate(details, positions) {
    const key = run => JSON.stringify(Object.entries(run).filter(([field]) => field !== 'count').sort(([a], [b]) => a.localeCompare(b)));
    const runs = [];
    let position = 0;
    for (const run of details) {
        for (let index = 0; index < run.count; index++, position++) {
            const value = { ...run, count: 1, protected: positions.has(position) };
            const previous = runs.at(-1);
            if (previous && key(previous) === key(value)) previous.count += 1;
            else runs.push(value);
        }
    }
    return runs;
}

// ---------------------------------------------------------------------------------------------------------------------------
// String-level expectations (independent of the contract model).

const L = ['ABCDEFGHIJ', 'KLMNOPQRST', 'UVWXYZabcd', 'efghijklmn', 'opqrstuvwx', 'yz01234567', '89!#%&*+-='];
const R = ['0123456789', '9876543210', 'zyxwvutsrq', 'ponmlkjihg', 'fedcbaZYXW', 'VUTSRQPONM', 'LKJIHGFEDC'];
const blank = ' '.repeat(W);
/** The block seed as full-width rows. */
const block = () => Array.from({ length: colEditRows }, (_, row) => (row < L.length ? L[row] + ' '.repeat(W - 20) + R[row] : blank));
const sl = (text, n) => text.slice(n) + ' '.repeat(n);
const sr = (text, n) => ' '.repeat(n) + text.slice(0, W - n);
const ic = (text, at, n) => text.slice(0, at) + ' '.repeat(n) + text.slice(at, W - n);
const dc = (text, at, n) => text.slice(0, at) + text.slice(at + n) + ' '.repeat(n);
/** One edit of `caseId` on full-width rows `from`..`to` (inclusive) with the cursor at column `col`; the count already clamped by the caller. */
function edit(caseId, rows, { col, n, from = 0, to = colEditRows - 1 }) {
    return rows.map((text, row) => {
        if (row < from || row > to) return text;
        if (caseId === 'editing.sl') return sl(text, n);
        if (caseId === 'editing.sr') return sr(text, n);
        return caseId === 'editing.decic' ? ic(text, col, n) : dc(text, col, n);
    });
}
const atCursor = caseId => caseId === 'editing.decic' || caseId === 'editing.decdc';
const clamp = (caseId, col, n) => Math.min(n, W - (atCursor(caseId) ? col : 0));
const cellsOf = texts => texts.map(text => [...text.trimEnd()].map(char => plainCell(char)));

const pen = { attrs: ['italic'], fg: paletteChannel(3), bg: paletteChannel(2) };
const penBlank = { fg: paletteChannel(3), bg: paletteChannel(2) };
const bold = { attrs: ['bold'] }, redOnBlue = { fg: paletteChannel(1), bg: paletteChannel(4) };

/** The attribute and protection seed as cell specs plus protection flags per position. */
function attrSeedCells() {
    const rows = Array.from({ length: colEditRows }, () => Array.from({ length: W }, () => ({ spec: plainCell(' '), guarded: false })));
    const put = (row, text, spec, from = 0, guarded = () => false) => [...text].forEach((char, index) => { rows[row][from + index] = { spec: colorCell(char, spec), guarded: guarded(from + index) }; });
    put(0, 'KEEP-TOP--', {});
    put(0, 'XYZ', {}, W - 3, () => true);
    put(1, 'BOLD-ROW--', bold);
    put(2, 'RED-ON-BLU', redOnBlue);
    put(3, 'PLAIN-ROW-', {}, 0, col => col >= 6 && col <= 8);
    return rows;
}
/** Shift one row of {spec, guarded} cells like the string helpers, filling with `fill`. */
function shiftCells(line, caseId, col, n, fill) {
    const blanks = Array.from({ length: n }, () => ({ ...fill }));
    if (caseId === 'editing.sl') return [...line.slice(n), ...blanks];
    if (caseId === 'editing.sr') return [...blanks, ...line.slice(0, W - n)];
    if (caseId === 'editing.decic') return [...line.slice(0, col), ...blanks, ...line.slice(col, W - n)];
    return [...line.slice(0, col), ...line.slice(col + n), ...blanks];
}
/** A screen read from {spec, guarded} rows. */
const fromCells = (rows, cursor) => ({ rows: rows.map(line => {
    let end = line.length;
    while (end > 0 && line[end - 1].spec.char === ' ' && !line[end - 1].guarded && line[end - 1].spec.fg === plainCell(' ').fg && line[end - 1].spec.bg === plainCell(' ').bg) end--;
    return line.slice(0, end).map(cell => cell.spec);
}), cursor, guarded: rows.flatMap((line, row) => line.flatMap((cell, col) => (cell.guarded ? [[row, col]] : []))) });

/** The pinned seeds, setup cursors and edits of each case, written out by hand. */
const pinnedReads = Object.freeze({
    'editing.sl': { rows: ['ABCDEFGHIJK', 'JKLMNOPQRST'], cursor: [1, 11], setup: [0, 0], after: ['CDEFGHIJK', 'LMNOPQRST'] },
    'editing.sr': { rows: ['ABCDEFGHI', 'JKLMNOPQR'], cursor: [1, 9], setup: [0, 0], after: ['  ABCDEFGHI', '  JKLMNOPQR'] },
    'editing.decic': { rows: ['ABCDEFGH', 'IJKLMNOP', 'QRSTUVWX'], cursor: [2, 8], setup: [1, 2], after: ['AB  CDEFGH', 'IJ  KLMNOP', 'QR  STUVWX'] },
    'editing.decdc': { rows: ['ABCDEFGHIJ', 'IJKLMNOPQR', 'QRSTUVWXab'], cursor: [2, 10], setup: [1, 2], after: ['ABEFGHIJ', 'IJMNOPQR', 'QRUVWXab'] },
});

/** The independent expectation of every read group of one synthetic case (a conclusive, supported run). */
export function syntheticColEditReads(caseId) {
    const read = (texts, cursor, state) => ({ rows: cellsOf(texts), cursor, state, guarded: [] });
    const spec = pinnedReads[caseId];
    const start = atCursor(caseId) ? 5 : 0;
    const counts = [1, 1, 1, 3].reduce((rows, n) => edit(caseId, rows, { col: 5, n }), block());
    const region = { margins: [2, 4] };
    const reads = {
        initial: read([], [0, 0]), before: read(spec.rows, spec.cursor), setup: read(spec.rows, spec.setup), edited: read(spec.after, spec.setup),
        blockBefore: read(block(), [6, W - 1]),
        counts: read(counts, [3, 5]),
        clip: read(edit(caseId, block(), { col: 5, n: clamp(caseId, 5, 999) }), [3, 5]),
        region: read(edit(caseId, block(), { col: 5, n: 2, from: 2, to: 4 }), [6, 5], region),
        origin: read(edit(caseId, block(), { col: 2, n: 2, from: 2, to: 4 }), [3, 2], { ...region, origin: true }),
        modes: read(edit(caseId, block(), { col: 5, n: 2 }), [3, 5], { insert: true }),
    };
    if (atCursor(caseId)) reads.columns = read(edit(caseId, edit(caseId, block(), { col: 0, n: 2 }), { col: W - 1, n: 1 }), [3, W - 1]);
    const wrapped = edit(caseId, block(), { col: W - 1, n: 1 });
    wrapped[7] = 'w' + wrapped[7].slice(1);
    reads.pendingWrap = read(wrapped, [7, 1]);
    const seed = attrSeedCells();
    reads.attrBefore = fromCells(seed, [3, 10]);
    const moved = seed.map(line => shiftCells(line, caseId, start === 0 ? 0 : 2, 2, { spec: colorCell(' ', penBlank), guarded: false }));
    moved[11][2] = { spec: colorCell('P', pen), guarded: false };
    reads.attr = fromCells(moved, [11, 3]);
    return reads;
}

const colEditSuite = Object.freeze({ noncePrefix: 'NTILDE_CE_', prefix: 'cole', contracts: colEditContracts,
    adapterId: colEditAdapterId, derivedFrom: 'ntilde-owned-rectangle-editing-grading-v1', reset: colEditReset,
    geometry: { rows: colEditRows, cols: W },
    decorate: screen => ({ ...screen, cellDetails: screen.cellDetails.map(run => ({ ...run, protected: false })),
        bufferState: { scrollTop: 0, scrollBottom: colEditRows - 1, originMode: false, autoWrap: true, insertMode: false,
            pendingWrap: false, alternateScreen: false, scrollbackLines: 0 }, history: { startLine: 0, totalLines: 0, lines: [] } }),
    screens: (caseId, binding, readsOf) => {
        const reads = readsOf?.(caseId) ?? syntheticColEditReads(caseId);
        const contract = colEditContracts.find(item => item.caseId === caseId);
        return contract.roles.map(role => syntheticColEditScreen(reads[role].rows, reads[role].cursor, reads[role].state, reads[role].guarded ?? [], binding));
    },
    replay: replayColEditPinned });

/** The screens of every read group of one synthetic case. */
export const syntheticColEditScreens = (caseId, binding = paletteBinding(), readsOf = null) => colEditSuite.screens(caseId, binding, readsOf);

/** A complete, valid synthetic run: frames, worker run log and adapter facts. */
export const syntheticColEditEvidence = (runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64),
    binding = paletteBinding(), readsOf = null) => syntheticColorEvidence(colEditSuite, runId, bundleSha256, binding, readsOf);

/** The independent contract verdict for one synthetic case. */
export function syntheticColEditGrade(caseId, binding = paletteBinding(), readsOf = null) {
    const contract = colEditContracts.find(item => item.caseId === caseId);
    return gradeColEditCase(contract, colEditObservation(contract, syntheticColEditScreens(caseId, binding, readsOf)));
}
