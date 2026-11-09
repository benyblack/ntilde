// Synthetic, fully valid erase-in-display grading evidence for portable tests. Every frame, run-log
// entry and pinned result is produced through the same production validators used for real
// measurements. The expected erased cells are computed here independently of the contracts (a row-major
// linear-position oracle) so the fixtures are a second oracle, not a mirror.
import { eraseDisplayContracts, eraseDisplayReset, eraseDisplayObservation, gradeEraseDisplayCase } from '../erase-display-contracts.mjs';
import { replayEraseDisplayPinned } from '../erase-display-grading.mjs';
import { eraseDisplayAdapterId } from '../erase-display-grading-collector.mjs';
import { paletteBinding, colorCell, plainCell, syntheticColorEvidence, syntheticSgrPaletteScreen } from './sgr-palette-evidence.mjs';

export { paletteBinding, colorCell, plainCell } from './sgr-palette-evidence.mjs';

/** The owned geometry the contracts are pinned to. */
export const displayGeometry = Object.freeze({ rows: 40, cols: 140 });
const { rows: ROWS, cols: COLS } = displayGeometry;

/** The independent oracle: whether linear position (row * COLS + col) is erased by ED `mode` with the cursor at `cursor`. */
export const erasedBy = (mode, cursor, row, col) => {
    const position = row * COLS + col, cursorPosition = cursor[0] * COLS + cursor[1];
    return mode === 2 || (mode === 0 && position >= cursorPosition) || (mode === 1 && position <= cursorPosition);
};
/** The surviving [row, col, spec] cells of a seed after ED `mode`. */
export const survivors = (cells, mode, cursor) => cells.filter(([row, col]) => !erasedBy(mode, cursor, row, col));
const modes = { omitted: 0, explicit0: 0, explicit1: 1, explicit2: 2 };

/** The default buffer state of the alternate screen (the pinned fixtures run there) and of the main screen. */
export const bufferOf = (overrides = {}) => ({ scrollTop: 0, scrollBottom: ROWS - 1, originMode: false, autoWrap: true, insertMode: false,
    pendingWrap: false, alternateScreen: true, scrollbackLines: 0, ...overrides });
/** The History payload that matches a buffer state. */
export const historyOf = lines => ({ startLine: 0, totalLines: lines.length, lines });

/** Text laid on a row from column 0 as [row, col, spec] cells; blanks are omitted. */
export const textCells = (row, text, attrs = []) => [...text].flatMap((char, col) => (char === ' ' ? [] : [[row, col, colorCell(char, { attrs })]]));

/** A synthetic 40x140 screen. `cells` are [row, col, spec] triples (later ones win); every other cell is a plain blank.
 * The lines are the right-trimmed text of each row, as the App reports them. */
export function syntheticDisplayScreen(cells, cursor, { binding = paletteBinding(), buffer = bufferOf(), history = [], cursorVisible = true } = {}) {
    // One shared blank spec: the screen builder computes each distinct spec object once.
    const blank = plainCell(' ');
    const grid = Array.from({ length: ROWS * COLS }, () => blank);
    for (const [row, col, spec] of cells) grid[row * COLS + col] = spec;
    const screen = syntheticSgrPaletteScreen(grid, binding, displayGeometry);
    const lines = Array.from({ length: ROWS }, (_, row) => grid.slice(row * COLS, (row + 1) * COLS).map(spec => spec.char).join('').trimEnd());
    return { ...screen, lines, cursorRow: cursor[0], cursorCol: cursor[1], cursorVisible,
        bufferState: { ...buffer, scrollbackLines: history.length }, history: historyOf(history) };
}
/** Add the buffer state and history every frame of this suite carries. */
const decorate = screen => ({ ...screen, bufferState: bufferOf(), history: historyOf([]) });

const seedCells = (attrs = []) => [...textCells(0, 'AAAAA', attrs), ...textCells(20, 'BBBBB', attrs), ...textCells(39, 'CCCCC', attrs)];
const fillCells = () => Array.from({ length: ROWS }, (_, row) => [...textCells(row, `ROW${String(row).padStart(2, '0')}-ABCD`), [row, COLS - 1, colorCell('|')]]).flat();
const boldOnly = cells => cells.map(([row, col, spec]) => [row, col, colorCell(spec.char, { attrs: ['bold'] })]);
const edgeCursor = { 0: [20, 0], 1: [20, COLS - 1], 2: [39, COLS - 1] };
const seedCursor = [20, 2], fillCursor = [20, 4];
export const historySeed = Object.freeze(['HIST-A', 'HIST-B', 'HIST-C', '']);

/** The cell grids of every read group of one synthetic case (a conclusive, supported run), by role. */
export function syntheticEraseDisplayReads(caseId) {
    const contract = eraseDisplayContracts.find(item => item.caseId === caseId);
    const mode = contract.mode, edge = edgeCursor[mode];
    const fixture = at => ({ cells: survivors(seedCells(), at, seedCursor), cursor: seedCursor });
    const reads = {
        initial: { cells: [], cursor: [0, 0] },
        setup: { cells: seedCells(), cursor: seedCursor },
        erased: fixture(mode),
        wideBefore: { cells: fillCells(), cursor: fillCursor },
        wide: { cells: survivors(fillCells(), mode, fillCursor), cursor: fillCursor },
        edgeBefore: { cells: fillCells(), cursor: edge },
        edge: { cells: survivors(fillCells(), mode, edge), cursor: edge },
        bceBefore: { cells: boldOnly(seedCells()), cursor: seedCursor },
        bce: { cells: boldOnly(survivors(seedCells(), mode, seedCursor)), cursor: seedCursor },
        pen: { cells: [[0, 0, colorCell('X', { bg: { kind: 'palette', index: 4 } })]], cursor: [0, 1] },
        historyBefore: { cells: seedCells(), cursor: seedCursor, main: true, history: historySeed },
        history: { cells: survivors(seedCells(), mode, seedCursor), cursor: seedCursor, main: true, history: historySeed },
    };
    for (const name of contract.others) reads[name] = fixture(modes[name]);
    return reads;
}

const eraseDisplaySuite = Object.freeze({ noncePrefix: 'NTILDE_ED_', prefix: 'erdi', contracts: eraseDisplayContracts,
    adapterId: eraseDisplayAdapterId, derivedFrom: 'ntilde-owned-erase-line-grading-v1', reset: eraseDisplayReset,
    geometry: displayGeometry, decorate,
    screens: (caseId, binding, readsOf) => {
        const reads = readsOf?.(caseId) ?? syntheticEraseDisplayReads(caseId);
        const contract = eraseDisplayContracts.find(item => item.caseId === caseId);
        return contract.roles.map(role => {
            const read = reads[role];
            return syntheticDisplayScreen(read.cells, read.cursor, { binding, history: read.history ?? [],
                buffer: bufferOf({ alternateScreen: read.main !== true, ...(read.buffer ?? {}) }), cursorVisible: read.cursorVisible ?? true });
        });
    },
    replay: replayEraseDisplayPinned });

/** The screens of every read group of one synthetic case. */
export const syntheticEraseDisplayScreens = (caseId, binding = paletteBinding(), readsOf = null) => eraseDisplaySuite.screens(caseId, binding, readsOf);

/** A complete, valid synthetic run: frames, worker run log and adapter facts. */
export const syntheticEraseDisplayEvidence = (runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64),
    binding = paletteBinding(), readsOf = null) => syntheticColorEvidence(eraseDisplaySuite, runId, bundleSha256, binding, readsOf);

/** The independent contract verdict for one synthetic case. */
export function syntheticEraseDisplayGrade(caseId, binding = paletteBinding(), readsOf = null) {
    const contract = eraseDisplayContracts.find(item => item.caseId === caseId);
    return gradeEraseDisplayCase(contract, eraseDisplayObservation(contract, syntheticEraseDisplayScreens(caseId, binding, readsOf)));
}
