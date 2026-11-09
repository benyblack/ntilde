// Synthetic, fully valid character-editing grading evidence for portable tests. Every frame, run-log entry and
// pinned result is produced through the same production validators used for real measurements. The expected rows
// are computed here with plain string operations independent of the contracts, so the fixtures are a second
// oracle, not a mirror.
import { charEditContracts, charEditReset, charEditObservation, gradeCharEditCase } from '../char-edit-contracts.mjs';
import { replayCharEditPinned } from '../char-edit-grading.mjs';
import { charEditAdapterId } from '../char-edit-grading-collector.mjs';
import { paletteBinding, colorCell, plainCell, syntheticColorEvidence, sgrPaletteCols } from './sgr-palette-evidence.mjs';
import { rowOf, syntheticEraseScreen } from './erase-line-evidence.mjs';

export { paletteBinding, colorCell, plainCell } from './sgr-palette-evidence.mjs';
export { rowOf, syntheticEraseScreen as syntheticCharEditScreen } from './erase-line-evidence.mjs';

/** The independent oracle for ICH/DCH: `text` is the full row text, `col` the cursor column, `count` the number of cells. */
export function shifted(op, text, col, count, width = sgrPaletteCols) {
    const row = text.padEnd(width, ' ');
    const next = op === 'ich' ? row.slice(0, col) + ' '.repeat(count) + row.slice(col) : row.slice(0, col) + row.slice(col + count) + ' '.repeat(count);
    return next.slice(0, width).trimEnd();
}
/** The independent oracle for REP: write X at column 1 of AX   Z, then repeat it `count` times (REP overwrites). */
export function repeated(count) {
    const cells = [...'AX   Z'.padEnd(sgrPaletteCols, ' ')];
    for (let col = 1; col <= 1 + count; col++) cells[col] = 'X';
    return cells.join('').trimEnd();
}

const edgeText = 'W'.padStart(sgrPaletteCols - 2, ' ') + 'XY';
const edgeStart = sgrPaletteCols - 3;
const bold = char => colorCell(char, { attrs: ['bold'] });
const pen = { kind: 'palette', index: 4 };
const counts = { omitted: 1, explicit0: 1, explicit2: 2, explicit999: 999, explicit1: 1, explicit5: 5 };

/** The cell grids of every read group of one synthetic shift case (a conclusive, supported run), by role. */
function shiftReads(contract) {
    const op = contract.op;
    const edit = (text, col, count) => rowOf(shifted(op, text, col, count));
    const reads = { initial: { rows: [], cursor: [0, 0] }, seed: { rows: [rowOf('ABCDEZQH')], cursor: [0, 8] },
        cursor: { rows: [rowOf('ABCDEZQH')], cursor: [0, 2] }, edited: { rows: [edit('ABCDEZQH', 2, 1)], cursor: [0, 2] },
        edgeBefore: { rows: [rowOf(edgeText)], cursor: [0, edgeStart] }, edge: { rows: [edit(edgeText, edgeStart, 1)], cursor: [0, edgeStart] },
        wideBefore: { rows: [rowOf('ABOVE-ROW!'), rowOf('ABCDEFGHIJ'), rowOf('BELOW-ROW!')], cursor: [1, 2] },
        wide: { rows: [rowOf('ABOVE-ROW!'), edit('ABCDEFGHIJ', 2, 1), rowOf('BELOW-ROW!')], cursor: [1, 2] },
        attrBefore: { rows: [[plainCell('A'), plainCell('B'), plainCell('C'), bold('D'), bold('E'), plainCell('Z'), plainCell('Q'), plainCell('H')]], cursor: [0, 2] },
        attr: { rows: [op === 'ich' ? [plainCell('A'), plainCell('B'), plainCell(' '), plainCell('C'), bold('D'), bold('E'), plainCell('Z'), plainCell('Q'), plainCell('H')]
            : [plainCell('A'), plainCell('B'), bold('D'), bold('E'), plainCell('Z'), plainCell('Q'), plainCell('H')]], cursor: [0, 2] },
        pen: { rows: [[colorCell('X', { bg: pen })]], cursor: [0, 1] } };
    for (const name of contract.others) reads[name] = { rows: [edit('ABCDEZQH', 2, counts[name])], cursor: [0, 2] };
    return reads;
}

/** The cell grids of every read group of the synthetic repeat case (a conclusive, supported run), by role. */
function repeatReads(contract) {
    const flank = text => rowOf(text);
    const reads = { initial: { rows: [], cursor: [0, 0] }, seed: { rows: [flank('AX   Z')], cursor: [0, 6] }, target: { rows: [flank('AX   Z')], cursor: [0, 1] },
        before: { rows: [flank('AX   Z')], cursor: [0, 2] }, split: { rows: [flank('AX   Z')], cursor: [0, 2] },
        repeated: { rows: [flank(repeated(3))], cursor: [0, 5] },
        wideBefore: { rows: [rowOf('ABOVE-ROW!'), flank('AX   ZQRST'), rowOf('BELOW-ROW!')], cursor: [1, 1] },
        wide: { rows: [rowOf('ABOVE-ROW!'), flank('AXXXXZQRST'), rowOf('BELOW-ROW!')], cursor: [1, 5] },
        attrBefore: { rows: [flank('AX   Z')], cursor: [0, 1] },
        attr: { rows: [[plainCell('A'), ...Array.from({ length: 4 }, () => colorCell('X', { attrs: ['bold'], bg: pen })), plainCell('Z')]], cursor: [0, 5] },
        pen: { rows: [[colorCell('X', { attrs: ['bold'], bg: pen })]], cursor: [0, 1] } };
    for (const name of contract.others) reads[name] = { rows: [flank(repeated(counts[name]))], cursor: [0, 2 + counts[name]] };
    return reads;
}

/** The cell grids of every read group of one synthetic case (a conclusive, supported run), by role. */
export function syntheticCharEditReads(caseId) {
    const contract = charEditContracts.find(item => item.caseId === caseId);
    return contract.family === 'repeat' ? repeatReads(contract) : shiftReads(contract);
}

const charEditSuite = Object.freeze({ noncePrefix: 'NTILDE_CE_', prefix: 'ched', contracts: charEditContracts,
    adapterId: charEditAdapterId, derivedFrom: 'ntilde-owned-erase-line-grading-v1', reset: charEditReset,
    screens: (caseId, binding, readsOf) => {
        const reads = readsOf?.(caseId) ?? syntheticCharEditReads(caseId);
        const contract = charEditContracts.find(item => item.caseId === caseId);
        return contract.roles.map(role => syntheticEraseScreen(reads[role].rows, reads[role].cursor, binding));
    },
    replay: replayCharEditPinned });

/** The screens of every read group of one synthetic case. */
export const syntheticCharEditScreens = (caseId, binding = paletteBinding(), readsOf = null) => charEditSuite.screens(caseId, binding, readsOf);

/** A complete, valid synthetic run: frames, worker run log and adapter facts. */
export const syntheticCharEditEvidence = (runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64),
    binding = paletteBinding(), readsOf = null) => syntheticColorEvidence(charEditSuite, runId, bundleSha256, binding, readsOf);

/** The independent contract verdict for one synthetic case. */
export function syntheticCharEditGrade(caseId, binding = paletteBinding(), readsOf = null) {
    const contract = charEditContracts.find(item => item.caseId === caseId);
    return gradeCharEditCase(contract, charEditObservation(contract, syntheticCharEditScreens(caseId, binding, readsOf)));
}
