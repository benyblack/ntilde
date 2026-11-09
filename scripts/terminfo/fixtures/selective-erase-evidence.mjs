// Synthetic, fully valid selective-erasure grading evidence for portable tests. Every frame, run-log entry and
// pinned result is produced through the same production validators used for real measurements. The expected screens are
// computed here independently of the contracts (a small explicit model, not the contracts' own helpers) so the fixtures are a
// second oracle, not a mirror. Every cell is [row, col, spec, protected].
import { selectiveEraseContracts, selectiveEraseReset, selectiveEraseObservation, gradeSelectiveEraseCase } from '../selective-erase-contracts.mjs';
import { replaySelectiveErasePinned } from '../selective-erase-grading.mjs';
import { selectiveEraseAdapterId } from '../selective-erase-grading-collector.mjs';
import { paletteBinding, colorCell, plainCell, syntheticColorEvidence } from './sgr-palette-evidence.mjs';
import { displayGeometry, syntheticDisplayScreen, bufferOf, historyOf, historySeed } from './erase-display-evidence.mjs';

export { paletteBinding, colorCell, plainCell } from './sgr-palette-evidence.mjs';

const { rows: ROWS, cols: COLS } = displayGeometry;
const MIDDLE = Math.floor(ROWS / 2), LAST = ROWS - 1, TAIL = COLS - 1;

/** A cell: text, optional attributes, protection. */
export const guarded = (row, col, char, attrs = []) => [row, col, colorCell(char, { attrs }), true];
export const open = (row, col, char, attrs = []) => [row, col, colorCell(char, { attrs }), false];

/** The pinned seed: protected P, then unprotected ABCDZ, on row 0. */
export const seedCells = (attrs = []) => [guarded(0, 0, 'P', attrs), ...[...'ABCDZ'].map((char, index) => open(0, index + 1, char, attrs))];
/** The cells of a seed list that a selective erase leaves: the protected ones, plus those outside a rectangle (columns of row 0 only). */
const keepProtected = cells => cells.filter(cell => cell[3]);
const keepOutsideColumns = (cells, left, right) => cells.filter(cell => cell[3] || cell[1] < left || cell[1] > right);
const dropColumns = (cells, left, right) => cells.filter(cell => cell[1] < left || cell[1] > right);
const asUnprotected = cells => cells.map(([row, col, spec]) => [row, col, spec, false]);

/** The fill: ROWnn-ABCD on every row, a tail marker in the last column, with the contract's protection pattern written out literally. */
const protectedFill = (row, col) => (col === 0 && row % 4 === 0) || (col === 5 && row % 3 === 0) || (col === TAIL && (row === 0 || row === MIDDLE || row === LAST));
export const fillSyntheticCells = () => Array.from({ length: ROWS }, (_, row) => [
    ...[...`ROW${String(row).padStart(2, '0')}-ABCD`].map((char, col) => [row, col, colorCell(char), protectedFill(row, col)]),
    [row, TAIL, colorCell('|'), protectedFill(row, TAIL)],
]).flat();
/** Whether a fill cell lies inside the rectangle rows 10-30, columns 3-9. */
const insideFillRect = ([row, col]) => row >= 10 && row <= 30 && col >= 3 && col <= 9;

/** Add the per-cell protection to the stored details of a synthetic screen: expand to cells, annotate, coalesce canonically. */
function annotate(screen, positions) {
    const cells = [];
    for (const run of screen.cellDetails) for (let index = 0; index < run.count; index++) cells.push({ ...run, count: 1 });
    const key = run => JSON.stringify(Object.entries(run).filter(([field]) => field !== 'count').sort(([a], [b]) => a.localeCompare(b)));
    const runs = [];
    for (const [position, detail] of cells.entries()) {
        const value = { ...detail, protected: positions.has(position) };
        const previous = runs.at(-1);
        if (previous && key(previous) === key(value)) previous.count += 1;
        else runs.push(value);
    }
    return { ...screen, cellDetails: runs };
}

/** One synthetic screen from a read: placements, cursor, optional main-screen history. */
export function syntheticSelectiveScreen(read, binding = paletteBinding()) {
    const screen = syntheticDisplayScreen(read.cells.map(([row, col, spec]) => [row, col, spec]), read.cursor,
        { binding, history: read.history ?? [], buffer: bufferOf({ alternateScreen: read.main !== true, ...(read.buffer ?? {}) }), cursorVisible: read.cursorVisible ?? true });
    return annotate(screen, new Set(read.cells.filter(cell => cell[3]).map(([row, col]) => row * COLS + col)));
}

/** The reads of every group of one synthetic case (a conclusive, supported run), by role. */
export function syntheticSelectiveEraseReads(caseId) {
    const rectangular = caseId === 'editing.decsera';
    const seed = seedCells(), seedCursor = [0, 6];
    // what the case's own erase (rows 0, columns 0-4 for DECSERA) leaves of any row-0 cells
    const own = cells => (rectangular ? keepOutsideColumns(cells, 0, 4) : keepProtected(cells));
    const fillCells = fillSyntheticCells();
    const fillLeaves = rectangular ? fillCells.filter(cell => cell[3] || !insideFillRect(cell)) : keepProtected(fillCells);
    const fillCursor = [MIDDLE, 4], edgeCursor = [LAST, TAIL];
    const bold = ['bold'];
    const pen = [guarded(0, 0, 'A'), guarded(0, 1, 'B'), open(0, 2, 'C'), open(0, 3, 'D')];
    const overwrite = [open(0, 0, 'X'), guarded(0, 1, 'B')];
    const reads = {
        initial: { cells: [], cursor: [0, 0] },
        seeded: { cells: seed, cursor: seedCursor },
        erased: { cells: own(seed), cursor: seedCursor },
        finalized: { cells: own(seed), cursor: seedCursor },
        ordinary: { cells: rectangular ? asUnprotected(dropColumns(seed, 0, 4)) : [], cursor: seedCursor },
        penBefore: { cells: pen, cursor: [0, 4] },
        pen: { cells: own(pen), cursor: [0, 4] },
        overwriteBefore: { cells: overwrite, cursor: [0, 1] },
        overwrite: { cells: own(overwrite), cursor: [0, 1] },
        restore: { cells: [open(0, 0, 'Q')], cursor: [0, 1] },
        fillBefore: { cells: fillCells, cursor: fillCursor },
        fill: { cells: fillLeaves, cursor: fillCursor },
        edgeBefore: { cells: fillCells, cursor: edgeCursor },
        edge: { cells: keepProtected(fillCells), cursor: edgeCursor },
        attrBefore: { cells: seedCells(bold), cursor: seedCursor },
        attr: { cells: own(seedCells(bold)), cursor: seedCursor },
        historyBefore: { cells: seed, cursor: seedCursor, main: true, history: historySeed },
        history: { cells: own(seed), cursor: seedCursor, main: true, history: historySeed },
    };
    if (rectangular) {
        reads.rectInterior = { cells: keepOutsideColumns(seed, 1, 3), cursor: seedCursor };
        reads.rectWide = { cells: keepOutsideColumns(seed, 0, 5), cursor: seedCursor };
        reads.rectReversed = { cells: seed, cursor: seedCursor };
    } else {
        // DECSED 0 from column 3 erases C, D, Z; DECSED 1 through column 3 erases A, B, C; P is protected either way. The cursor sits at (0, 3).
        reads.mode0 = { cells: seed.filter(cell => cell[3] || cell[1] < 3), cursor: [0, 3] };
        reads.mode1 = { cells: seed.filter(cell => cell[3] || cell[1] > 3), cursor: [0, 3] };
    }
    return reads;
}

const selectiveEraseSuite = Object.freeze({ noncePrefix: 'NTILDE_SE_', prefix: 'sels', contracts: selectiveEraseContracts,
    adapterId: selectiveEraseAdapterId, derivedFrom: 'ntilde-owned-erase-chars-history-grading-v1', reset: selectiveEraseReset,
    geometry: displayGeometry,
    decorate: screen => ({ ...screen, bufferState: bufferOf(), history: historyOf([]),
        cellDetails: screen.cellDetails.map(run => ({ ...run, protected: false })) }),
    screens: (caseId, binding, readsOf) => {
        const reads = readsOf?.(caseId) ?? syntheticSelectiveEraseReads(caseId);
        const contract = selectiveEraseContracts.find(item => item.caseId === caseId);
        return contract.roles.map(role => syntheticSelectiveScreen(reads[role], binding));
    },
    replay: replaySelectiveErasePinned });

/** The screens of every read group of one synthetic case. */
export const syntheticSelectiveEraseScreens = (caseId, binding = paletteBinding(), readsOf = null) => selectiveEraseSuite.screens(caseId, binding, readsOf);

/** A complete, valid synthetic run: frames, worker run log and adapter facts. */
export function syntheticSelectiveEraseEvidence(runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64), binding = paletteBinding(), readsOf = null) {
    const evidence = syntheticColorEvidence(selectiveEraseSuite, runId, bundleSha256, binding, readsOf);
    // The pinned run feeds the seed, the erase and the finalizer; an early return still feeds the finalizer.
    evidence.run.cases = evidence.run.cases.map((item, index) => {
        const contract = selectiveEraseContracts[index];
        const note = item.pinned.note;
        const pinnedFed = note === 'Need six measured columns' ? [] : note === 'Selective erase seed was not measured' ? [contract.pinnedFeeds[0], contract.pinnedFeeds[2]] : [...contract.pinnedFeeds];
        return { ...item, pinnedFed };
    });
    return evidence;
}

/** The independent contract verdict for one synthetic case. */
export function syntheticSelectiveEraseGrade(caseId, binding = paletteBinding(), readsOf = null) {
    const contract = selectiveEraseContracts.find(item => item.caseId === caseId);
    return gradeSelectiveEraseCase(contract, selectiveEraseObservation(contract, syntheticSelectiveEraseScreens(caseId, binding, readsOf)));
}
