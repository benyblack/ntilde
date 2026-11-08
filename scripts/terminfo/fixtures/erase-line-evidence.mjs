// Synthetic, fully valid erase-in-line grading evidence for portable tests. Every frame, run-log
// entry and pinned result is produced through the same production validators used for real
// measurements. The expected erased text is computed here independently of the contracts so the
// fixtures are a second oracle, not a mirror.
import { eraseLineContracts, eraseLineReset, eraseLineObservation, gradeEraseLineCase } from '../erase-line-contracts.mjs';
import { replayEraseLinePinned } from '../erase-line-grading.mjs';
import { eraseLineAdapterId } from '../erase-line-grading-collector.mjs';
import { paletteBinding, colorCell, plainCell, syntheticColorEvidence, syntheticSgrPaletteScreen,
    sgrPaletteCols } from './sgr-palette-evidence.mjs';

export { paletteBinding, colorCell, plainCell } from './sgr-palette-evidence.mjs';

/** A row of cells, one per character; spaces are plain blank cells. */
export const rowOf = (text, attrs = []) => [...text].map(char => (char === ' ' ? plainCell(' ') : colorCell(char, { attrs })));

/** A synthetic screen: `rows` is an array of cell-spec rows (null = empty row), `cursor` is [row, col]. The lines are the
 * right-trimmed text of each row, as the App reports them. */
export function syntheticEraseScreen(rows, cursor, binding = paletteBinding()) {
    const leading = [];
    for (const row of rows) {
        const cells = row ?? [];
        leading.push(...cells, ...Array.from({ length: sgrPaletteCols - cells.length }, () => plainCell(' ')));
    }
    const screen = syntheticSgrPaletteScreen(leading, binding);
    const lines = Array.from({ length: screen.rows }, (_, index) => (rows[index] ?? []).map(cell => cell.char).join('').trimEnd());
    return { ...screen, lines, cursorRow: cursor[0], cursorCol: cursor[1] };
}

/** The independent oracle: which columns of `text` survive an EL mode with the cursor at column 2 (the cursor cell is erased). */
export const survivors = (text, mode) => [...text].map((char, col) => ((mode === 0 && col < 2) || (mode === 1 && col > 2) ? char : ' ')).join('');
const modes = { omitted: 0, explicit0: 0, explicit1: 1, explicit2: 2 };

/** The cell grids of every read group of one synthetic case (a conclusive, supported run), by role. */
export function syntheticEraseLineReads(caseId) {
    const contract = eraseLineContracts.find(item => item.caseId === caseId);
    const neighbor = contract.neighbor ? [rowOf('KEEP!')] : [];
    const fixture = mode => ({ rows: [rowOf(survivors('ABCDE', mode)), ...neighbor], cursor: [0, 2] });
    const reads = { initial: { rows: [], cursor: [0, 0] }, setup: { rows: [rowOf('ABCDE'), ...neighbor], cursor: [0, 2] },
        erased: fixture(contract.mode),
        wideBefore: { rows: [rowOf('ABOVE-ROW!'), rowOf('ABCDEFGHIJ'), rowOf('BELOW-ROW!')], cursor: [1, 2] },
        wide: { rows: [rowOf('ABOVE-ROW!'), rowOf(survivors('ABCDEFGHIJ', contract.mode)), rowOf('BELOW-ROW!')], cursor: [1, 2] },
        bceBefore: { rows: [rowOf('ABCDE', ['bold'])], cursor: [0, 2] },
        bce: { rows: [rowOf(survivors('ABCDE', contract.mode)).map((cell, col) => (cell.char === ' ' ? cell : colorCell(cell.char, { attrs: ['bold'] })))],
            cursor: [0, 2] },
        pen: { rows: [[colorCell('X', { bg: { kind: 'palette', index: 4 } })]], cursor: [0, 1] } };
    for (const name of contract.others) reads[name] = fixture(modes[name]);
    return reads;
}

const eraseLineSuite = Object.freeze({ noncePrefix: 'NTILDE_EL_', prefix: 'erln', contracts: eraseLineContracts,
    adapterId: eraseLineAdapterId, derivedFrom: 'ntilde-owned-sgr-selective-reset-grading-v1', reset: eraseLineReset,
    screens: (caseId, binding, readsOf) => {
        const reads = readsOf?.(caseId) ?? syntheticEraseLineReads(caseId);
        const contract = eraseLineContracts.find(item => item.caseId === caseId);
        return contract.roles.map(role => syntheticEraseScreen(reads[role].rows, reads[role].cursor, binding));
    },
    replay: replayEraseLinePinned });

/** The screens of every read group of one synthetic case. */
export const syntheticEraseLineScreens = (caseId, binding = paletteBinding(), readsOf = null) => eraseLineSuite.screens(caseId, binding, readsOf);

/** A complete, valid synthetic run: frames, worker run log and adapter facts. */
export const syntheticEraseLineEvidence = (runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64),
    binding = paletteBinding(), readsOf = null) => syntheticColorEvidence(eraseLineSuite, runId, bundleSha256, binding, readsOf);

/** The independent contract verdict for one synthetic case. */
export function syntheticEraseLineGrade(caseId, binding = paletteBinding(), readsOf = null) {
    const contract = eraseLineContracts.find(item => item.caseId === caseId);
    return gradeEraseLineCase(contract, eraseLineObservation(contract, syntheticEraseLineScreens(caseId, binding, readsOf)));
}
