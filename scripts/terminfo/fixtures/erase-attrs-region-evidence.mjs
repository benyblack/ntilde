// Synthetic, fully valid erase attributes / erase margins grading evidence for portable tests. Every frame, run-log entry and
// pinned result is produced through the same production validators used for real measurements. The expected screens are written
// out here independently of the contracts (literal cells and a row-major oracle), so the fixtures are a second oracle, not a mirror.
import { eraseAttrsRegionContracts, eraseAttrsRegionReset, eraseAttrsRegionObservation, gradeEraseAttrsRegionCase } from '../erase-attrs-region-contracts.mjs';
import { replayEraseAttrsRegionPinned } from '../erase-attrs-region-grading.mjs';
import { eraseAttrsRegionAdapterId } from '../erase-attrs-region-grading-collector.mjs';
import { paletteBinding, colorCell, plainCell, syntheticColorEvidence, syntheticSgrPaletteScreen } from './sgr-palette-evidence.mjs';

export { paletteBinding, colorCell, plainCell } from './sgr-palette-evidence.mjs';

/** The owned geometry the contracts are pinned to. */
export const regionGeometry = Object.freeze({ rows: 40, cols: 140 });
const { rows: ROWS, cols: COLS } = regionGeometry;

/** The default buffer state of the alternate screen (the pinned fixtures run there). */
export const bufferOf = (overrides = {}) => ({ scrollTop: 0, scrollBottom: ROWS - 1, originMode: false, autoWrap: true, insertMode: false,
    pendingWrap: false, alternateScreen: true, scrollbackLines: 0, ...overrides });
/** The History payload that matches a buffer state. */
export const historyOf = lines => ({ startLine: 0, totalLines: lines.length, lines });

/** Text laid on a row from column 0 as [row, col, spec] cells; blanks are omitted. */
export const textCells = (row, text, options = {}) => [...text].flatMap((char, col) => (char === ' ' ? [] : [[row, col, colorCell(char, options)]]));
const green = { bg: { kind: 'palette', index: 2 } };
/** Every cell of a row from `from` as a blank carrying the given background option. */
export const blankRow = (row, options = {}, from = 0) => Array.from({ length: COLS - from }, (_, index) => [row, from + index, colorCell(' ', options)]);
const fillText = row => `ROW${String(row).padStart(2, '0')}-ABCD`;
const markerRows = rows => rows.flatMap(row => [...textCells(row, fillText(row)), [row, COLS - 1, colorCell('|')]]);
const range = (from, to) => Array.from({ length: to - from + 1 }, (_, index) => from + index);

/** A synthetic 40x140 screen. `cells` are [row, col, spec] triples (later ones win); every other cell is a plain blank. */
export function syntheticRegionScreen(cells, cursor, { binding = paletteBinding(), buffer = bufferOf(), history = [], cursorVisible = true } = {}) {
    const blank = plainCell(' ');
    const grid = Array.from({ length: ROWS * COLS }, () => blank);
    for (const [row, col, spec] of cells) grid[row * COLS + col] = spec;
    const screen = syntheticSgrPaletteScreen(grid, binding, regionGeometry);
    const lines = Array.from({ length: ROWS }, (_, row) => grid.slice(row * COLS, (row + 1) * COLS).map(spec => spec.char).join('').trimEnd());
    return { ...screen, lines, cursorRow: cursor[0], cursorCol: cursor[1], cursorVisible, bufferState: { ...buffer, scrollbackLines: history.length }, history: historyOf(history) };
}
const decorate = screen => ({ ...screen, bufferState: bufferOf(), history: historyOf([]) });

const regionMargins = { scrollTop: 2, scrollBottom: 9 };
/** The cell grids of every read group of one synthetic case (a conclusive, supported run), by role. */
export function syntheticEraseAttrsRegionReads(caseId) {
    if (caseId === 'erase.el-with-attrs') {
        const seed = () => textCells(0, 'XXXXX', green), markers = markerRows(range(1, ROWS - 1));
        const erasedRow = [...blankRow(0, green)];
        return {
            initial: { cells: [], cursor: [0, 0] },
            setup: { cells: seed(), cursor: [0, 0] },
            erased: { cells: erasedRow, cursor: [0, 0] },
            restored: { cells: [...blankRow(0, green, 1), [0, 0, colorCell('W')]], cursor: [0, 1] },
            explicit0: { cells: erasedRow, cursor: [0, 0] },
            fillBefore: { cells: [...seed(), ...markers], cursor: [0, 0] },
            fill: { cells: [...erasedRow, ...markers], cursor: [0, 0] },
            penBefore: { cells: seed(), cursor: [0, 0] },
            penChanged: { cells: blankRow(0, { bg: { kind: 'palette', index: 4 } }), cursor: [0, 0] },
            penResetBefore: { cells: seed(), cursor: [0, 0] },
            penReset: { cells: [], cursor: [0, 0] },
        };
    }
    const keep = textCells(0, 'KEEP!'), seed = [...keep, ...textCells(2, 'ERASE')];
    return {
        initial: { cells: [], cursor: [0, 0] },
        setup: { cells: seed, cursor: [2, 0], buffer: regionMargins },
        erased: { cells: keep, cursor: [2, 0], buffer: regionMargins },
        restored: { cells: keep, cursor: [0, 0] },
        explicit0: { cells: keep, cursor: [2, 0], buffer: regionMargins },
        fillBefore: { cells: markerRows(range(0, ROWS - 1)), cursor: [2, 0], buffer: regionMargins },
        fill: { cells: markerRows([0, 1]), cursor: [2, 0], buffer: regionMargins },
    };
}

const eraseAttrsRegionSuite = Object.freeze({ noncePrefix: 'NTILDE_EA_', prefix: 'erar', contracts: eraseAttrsRegionContracts,
    adapterId: eraseAttrsRegionAdapterId, derivedFrom: 'ntilde-owned-saved-reset-grading-v1', reset: eraseAttrsRegionReset,
    geometry: regionGeometry, decorate,
    screens: (caseId, binding, readsOf) => {
        const reads = readsOf?.(caseId) ?? syntheticEraseAttrsRegionReads(caseId);
        const contract = eraseAttrsRegionContracts.find(item => item.caseId === caseId);
        return contract.roles.map(role => {
            const read = reads[role];
            return syntheticRegionScreen(read.cells, read.cursor, { binding, buffer: bufferOf(read.buffer ?? {}), history: read.history ?? [], cursorVisible: read.cursorVisible ?? true });
        });
    },
    replay: replayEraseAttrsRegionPinned });

/** The screens of every read group of one synthetic case. */
export const syntheticEraseAttrsRegionScreens = (caseId, binding = paletteBinding(), readsOf = null) => eraseAttrsRegionSuite.screens(caseId, binding, readsOf);

/** A complete, valid synthetic run: frames, worker run log and adapter facts. */
export const syntheticEraseAttrsRegionEvidence = (runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64),
    binding = paletteBinding(), readsOf = null) => syntheticColorEvidence(eraseAttrsRegionSuite, runId, bundleSha256, binding, readsOf);

/** The independent contract verdict for one synthetic case. */
export function syntheticEraseAttrsRegionGrade(caseId, binding = paletteBinding(), readsOf = null) {
    const contract = eraseAttrsRegionContracts.find(item => item.caseId === caseId);
    return gradeEraseAttrsRegionCase(contract, eraseAttrsRegionObservation(contract, syntheticEraseAttrsRegionScreens(caseId, binding, readsOf)));
}
