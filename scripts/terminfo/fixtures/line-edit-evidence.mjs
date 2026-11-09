// Synthetic, fully valid line-editing grading evidence for portable tests. Every frame, run-log entry and
// pinned result is produced through the same production validators used for real measurements. The expected
// rows are written out here by hand, independently of the contracts' line-editing model, so the fixtures are a
// second oracle, not a mirror.
import { lineEditContracts, lineEditReset, lineEditObservation, gradeLineEditCase } from '../line-edit-contracts.mjs';
import { replayLineEditPinned } from '../line-edit-grading.mjs';
import { lineEditAdapterId } from '../line-edit-grading-collector.mjs';
import { paletteBinding, colorCell, plainCell, literalChannel, paletteChannel, syntheticColorEvidence, syntheticSgrPaletteScreen,
    sgrPaletteCols } from './sgr-palette-evidence.mjs';

export { paletteBinding, colorCell, plainCell } from './sgr-palette-evidence.mjs';

/** The synthetic screen height: above the contract's minimum of ten rows and the observer's floor. */
export const lineEditRows = 12;
/** The blue pen background as the App stores a filled blank (literal) versus the pen cell (palette index 4). */
const filledBlue = literalChannel(0, 0, 238);

/** A row of cells, one per character; spaces are plain blank cells. */
export const rowOf = (text, spec = {}) => [...text].map(char => (char === ' ' ? plainCell(' ') : colorCell(char, spec)));
/** A whole painted blank row (a new blank line under a background pen). */
const paintedRow = bg => Array.from({ length: sgrPaletteCols }, () => colorCell(' ', { bg }));
/** Rows of text to cell rows, '' being an empty row. */
export const rowsOfText = texts => texts.map(text => (text === '' ? [] : rowOf(text)));

/** A synthetic screen: `rows` is an array of cell-spec rows (empty = blank row), `cursor` is [row, col]. */
export function syntheticLineEditScreen(rows, cursor, { margins = [0, lineEditRows - 1], origin = false, insert = false, scrollback = 0 } = {}, binding = paletteBinding()) {
    const leading = [];
    for (let index = 0; index < lineEditRows; index++) {
        const cells = rows[index] ?? [];
        leading.push(...cells, ...Array.from({ length: sgrPaletteCols - cells.length }, () => plainCell(' ')));
    }
    const screen = syntheticSgrPaletteScreen(leading, binding, { rows: lineEditRows, cols: sgrPaletteCols });
    const lines = Array.from({ length: lineEditRows }, (_, index) => (rows[index] ?? []).map(cell => cell.char).join('').trimEnd());
    return { ...screen, lines, cursorRow: cursor[0], cursorCol: cursor[1],
        bufferState: { scrollTop: margins[0], scrollBottom: margins[1], originMode: origin, autoWrap: true, insertMode: insert, pendingWrap: false,
            alternateScreen: true, scrollbackLines: scrollback },
        history: { startLine: 0, totalLines: scrollback, lines: Array.from({ length: scrollback }, () => '') } };
}

/** The independent expectation of every read group of one synthetic case (a conclusive, supported run). */
export function syntheticLineEditReads(caseId) {
    const insert = caseId === 'editing.insert-lines';
    const seed = ['AAAAA', 'BBBBB', 'CCCCC', 'DDDDD'];
    const edited = insert ? ['AAAAA', '', 'BBBBB', 'CCCCC', 'DDDDD'] : ['AAAAA', 'CCCCC', 'DDDDD'];
    const two = insert ? ['AAAAA', '', '', 'BBBBB', 'CCCCC', 'DDDDD'] : ['AAAAA', 'DDDDD'];
    const cleared = ['AAAAA'];
    const top = 'TOP-ROW---', next = 'NEXT-LAST-', last = 'LAST-ROW--';
    const bottomBefore = [top, '', '', '', '', '', '', '', '', '', next, last];
    const bottomAfter = insert ? [top, '', '', '', '', '', '', '', '', '', '', next] : [top, '', '', '', '', '', '', '', '', '', last, ''];
    const regionBefore = ['AAAAAAAAAA', 'BBBBBBBBBB', 'CCCCCCCCCC', 'DDDDDDDDDD', 'EEEEEEEEEE', 'FFFFFFFFFF', 'GGGGGGGGGG'];
    const regionAfter = insert ? ['AAAAAAAAAA', 'BBBBBBBBBB', 'CCCCCCCCCC', '', 'DDDDDDDDDD', 'FFFFFFFFFF', 'GGGGGGGGGG']
        : ['AAAAAAAAAA', 'BBBBBBBBBB', 'CCCCCCCCCC', 'EEEEEEEEEE', '', 'FFFFFFFFFF', 'GGGGGGGGGG'];
    const regionBig = ['AAAAAAAAAA', 'BBBBBBBBBB', 'CCCCCCCCCC', '', '', 'FFFFFFFFFF', 'GGGGGGGGGG'];
    const bold = { attrs: ['bold'] }, redOnBlue = { fg: paletteChannel(1), bg: paletteChannel(4) };
    const attrBefore = [rowOf('KEEP-TOP--'), rowOf('BOLD-ROW--', bold), rowOf('RED-ON-BLU', redOnBlue), rowOf('PLAIN-ROW-')];
    const blankLine = paintedRow(filledBlue);
    const attrAfter = insert ? [attrBefore[0], blankLine, attrBefore[1], attrBefore[2], attrBefore[3]]
        : [attrBefore[0], attrBefore[2], attrBefore[3], ...Array.from({ length: 8 }, () => []), blankLine];
    const region = { margins: [2, 4] };
    const read = (texts, cursor, state) => ({ rows: rowsOfText(texts), cursor, state });
    return {
        initial: read([], [0, 0]),
        before: read(seed, [3, 5]), setup: read(seed, [1, 0]), edited: read(edited, [1, 0]),
        omitted: read(edited, [1, 0]), explicit0: read(edited, [1, 0]), explicit2: read(two, [1, 0]), oversized: read(cleared, [1, 0]),
        bottomBefore: read(bottomBefore, [10, 10]), bottom: read(bottomAfter, [10, 0]),
        regionBefore: read(regionBefore, [3, 2], region), region: read(regionAfter, [3, 0], region), regionBig: read(regionBig, [3, 0], region),
        regionAbove: read(regionBefore, [1, 0], region), regionBelow: read(regionBefore, [5, 0], region),
        originBefore: read(regionBefore, [3, 2], { ...region, origin: true }), origin: read(regionAfter, [3, 0], { ...region, origin: true }),
        attrBefore: { rows: attrBefore, cursor: [1, 0] }, attr: { rows: attrAfter, cursor: [1, 0] },
        pen: { rows: [[colorCell('X', { bg: paletteChannel(4) })]], cursor: [0, 1] },
    };
}

const lineEditSuite = Object.freeze({ noncePrefix: 'NTILDE_LE_', prefix: 'ledt', contracts: lineEditContracts,
    adapterId: lineEditAdapterId, derivedFrom: 'ntilde-owned-erase-display-grading-v1', reset: lineEditReset,
    geometry: { rows: lineEditRows, cols: sgrPaletteCols },
    decorate: screen => ({ ...screen, bufferState: { scrollTop: 0, scrollBottom: lineEditRows - 1, originMode: false, autoWrap: true, insertMode: false,
        pendingWrap: false, alternateScreen: true, scrollbackLines: 0 }, history: { startLine: 0, totalLines: 0, lines: [] } }),
    screens: (caseId, binding, readsOf) => {
        const reads = readsOf?.(caseId) ?? syntheticLineEditReads(caseId);
        const contract = lineEditContracts.find(item => item.caseId === caseId);
        return contract.roles.map(role => syntheticLineEditScreen(reads[role].rows, reads[role].cursor, reads[role].state, binding));
    },
    replay: replayLineEditPinned });

/** The screens of every read group of one synthetic case. */
export const syntheticLineEditScreens = (caseId, binding = paletteBinding(), readsOf = null) => lineEditSuite.screens(caseId, binding, readsOf);

/** A complete, valid synthetic run: frames, worker run log and adapter facts. */
export const syntheticLineEditEvidence = (runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64),
    binding = paletteBinding(), readsOf = null) => syntheticColorEvidence(lineEditSuite, runId, bundleSha256, binding, readsOf);

/** The independent contract verdict for one synthetic case. */
export function syntheticLineEditGrade(caseId, binding = paletteBinding(), readsOf = null) {
    const contract = lineEditContracts.find(item => item.caseId === caseId);
    return gradeLineEditCase(contract, lineEditObservation(contract, syntheticLineEditScreens(caseId, binding, readsOf)));
}
