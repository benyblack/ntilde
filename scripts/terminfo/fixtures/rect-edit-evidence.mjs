// Synthetic, fully valid rectangle-editing grading evidence for portable tests. Every frame, run-log entry and
// pinned result is produced through the same production validators used for real measurements. The expected
// rows are written out here by hand, independently of the contracts' rectangle model, so the fixtures are a
// second oracle, not a mirror.
import { rectEditContracts, rectEditReset, rectEditObservation, gradeRectEditCase } from '../rect-edit-contracts.mjs';
import { replayRectEditPinned } from '../rect-edit-grading.mjs';
import { rectEditAdapterId } from '../rect-edit-grading-collector.mjs';
import { paletteBinding, colorCell, plainCell, literalChannel, paletteChannel, syntheticColorEvidence, syntheticSgrPaletteScreen,
    sgrPaletteCols } from './sgr-palette-evidence.mjs';

export { paletteBinding, colorCell, plainCell } from './sgr-palette-evidence.mjs';

/** The synthetic screen height: the contract's minimum of twelve rows. */
export const rectEditRows = 12;
/** The synthetic screen width. */
export const rectEditCols = sgrPaletteCols;

/** A row of cells, one per character; spaces are plain blank cells. */
export const rowOf = (text, spec = {}) => [...text].map(char => (char === ' ' ? plainCell(' ') : colorCell(char, spec)));
/** A row made of (text, spec) segments. */
export const seg = (...parts) => parts.flatMap(([text, spec = {}]) => [...text].map(char => colorCell(char, spec)));
/** Rows of text to cell rows, '' being an empty row. */
export const rowsOfText = texts => texts.map(text => (text === '' ? [] : rowOf(text)));

/** A synthetic screen: `rows` is an array of cell-spec rows (empty = blank row), `cursor` is [row, col]. */
export function syntheticRectEditScreen(rows, cursor, { margins = [0, rectEditRows - 1], origin = false, insert = false, scrollback = 0 } = {}, binding = paletteBinding()) {
    const leading = [];
    for (let index = 0; index < rectEditRows; index++) {
        const cells = rows[index] ?? [];
        leading.push(...cells, ...Array.from({ length: rectEditCols - cells.length }, () => plainCell(' ')));
    }
    const screen = syntheticSgrPaletteScreen(leading, binding, { rows: rectEditRows, cols: rectEditCols });
    const lines = Array.from({ length: rectEditRows }, (_, index) => (rows[index] ?? []).map(cell => cell.char).join('').trimEnd());
    return { ...screen, lines, cursorRow: cursor[0], cursorCol: cursor[1],
        bufferState: { scrollTop: margins[0], scrollBottom: margins[1], originMode: origin, autoWrap: true, insertMode: insert, pendingWrap: false,
            alternateScreen: true, scrollbackLines: scrollback },
        history: { startLine: 0, totalLines: scrollback, lines: Array.from({ length: scrollback }, () => '') } };
}

const block = ['ABCDEFGHIJ', 'KLMNOPQRST', 'UVWXYZabcd', 'efghijklmn', 'opqrstuvwx', 'yz01234567', '89!@#$%^&*'];
/** The block seed with some rows replaced, by zero-based row. */
const patched = changes => block.map((text, row) => changes[row] ?? text);
const full = '+'.repeat(rectEditCols);
const region = { margins: [2, 4] };
const bold = { attrs: ['bold'] }, redOnBlue = { fg: paletteChannel(1), bg: paletteChannel(4) };
const pen = { attrs: ['italic'], fg: paletteChannel(3), bg: paletteChannel(2) };
const literalPen = { fg: literalChannel(10, 20, 30), bg: literalChannel(40, 50, 60) };
const blueBackground = { bg: paletteChannel(4) };
/** The attribute seed rows. */
const attrBefore = [rowOf('KEEP-TOP--'), rowOf('BOLD-ROW--', bold), rowOf('RED-ON-BLU', redOnBlue), rowOf('PLAIN-ROW-')];

/** The independent expectation of every read group of one synthetic case (a conclusive, supported run). */
export function syntheticRectEditReads(caseId) {
    const read = (rows, cursor, state) => ({ rows: Array.isArray(rows[0]) || rows.length === 0 ? rows : rowsOfText(rows), cursor, state });
    const park = [3, 5];
    const common = { initial: read([], [0, 0]), blockBefore: read(block, [6, 10]), attrBefore: { rows: attrBefore, cursor: [3, 10] } };
    if (caseId === 'editing.decfra') {
        const boxed = patched({ 1: 'KL++++QRST', 2: 'UV++++abcd', 3: 'ef++++klmn' });
        const marginRows = patched({ 1: 'K+++OPQRST', 2: 'U+++YZabcd', 3: 'e+++ijklmn', 4: 'o+++stuvwx', 5: 'y+++234567' });
        const attrRows = [attrBefore[0], seg(['BO', bold], ['++++++', pen], ['--', bold]), seg(['RE', redOnBlue], ['++++++', pen], ['LU', redOnBlue]), attrBefore[3]];
        const literalRows = [rowOf(block[0]), seg(['KL'], ['++++', literalPen], ['QRST']), seg(['UV'], ['++++', literalPen], ['abcd']), seg(['ef'], ['++++', literalPen], ['klmn']),
            rowOf(block[4]), rowOf(block[5]), rowOf(block[6])];
        return { ...common,
            before: read(['aaaaaZ', 'bbbbbY', 'cccccW'], [2, 6]), setup: read(['aaaaaZ', 'bbbbbY', 'cccccW'], [0, 0]), edited: read(['XXXXXZ', 'XXXXXY', 'XXXXXW'], [0, 0]),
            one: read(patched({ 1: 'K+MNOPQRST' }), park), box: read(boxed, park),
            defaults: read(Array(rectEditRows).fill(full), park), zero: read(Array(rectEditRows).fill(full), park),
            clip: read([...block.slice(0, 4), `opqrst${'+'.repeat(54)}`, `yz0123${'+'.repeat(54)}`, `89!@#$${'+'.repeat(54)}`, ...Array(5).fill(`      ${'+'.repeat(54)}`)], park),
            noop: read(block, park),
            origin: read(patched({ 2: 'U+++YZabcd', 3: 'e+++ijklmn' }), [3, 2], { ...region, origin: true }),
            margins: read(marginRows, [5, 2], region), insertMode: read(boxed, park, { insert: true }),
            attr: { rows: [...attrRows, ...Array(7).fill([]), [colorCell('P', pen)]], cursor: [11, 1] },
            literal: { rows: [...literalRows, ...Array(4).fill([]), [colorCell('P', literalPen)]], cursor: [11, 1] } };
    }
    if (caseId === 'editing.decera') {
        const boxed = patched({ 1: 'KL    QRST', 2: 'UV    abcd', 3: 'ef    klmn' });
        const marginRows = patched({ 1: 'K   OPQRST', 2: 'U   YZabcd', 3: 'e   ijklmn', 4: 'o   stuvwx', 5: 'y   234567' });
        const painted = n => Array.from({ length: n }, () => colorCell(' ', blueBackground));
        const attrRows = [attrBefore[0], [...rowOf('BO', bold), ...painted(6), ...rowOf('--', bold)], [...rowOf('RE', redOnBlue), ...painted(6), ...rowOf('LU', redOnBlue)], attrBefore[3]];
        return { ...common,
            before: read(['aaaaaZ', 'bbbbbY', 'cccccX'], [2, 6]), setup: read(['aaaaaZ', 'bbbbbY', 'cccccX'], [0, 0]), edited: read(['     Z', '     Y', '     X'], [0, 0]),
            one: read(patched({ 1: 'K MNOPQRST' }), park), box: read(boxed, park), defaults: read([], park), zero: read([], park),
            clip: read([...block.slice(0, 4), 'opqrst', 'yz0123', '89!@#$'], park), noop: read(block, park),
            origin: read(patched({ 2: 'U   YZabcd', 3: 'e   ijklmn' }), [3, 2], { ...region, origin: true }),
            margins: read(marginRows, [5, 2], region), protected: read(boxed, park),
            attr: { rows: [...attrRows, ...Array(7).fill([]), [colorCell('P', blueBackground)]], cursor: [11, 1] } };
    }
    const pinnedCopy = ['ABCDE.........', 'FGHIJ.........', '..............', '..............', '.........ABCDE', '.........FGHIJ'];
    const copiedAttr = [...attrBefore, ...Array(2).fill([]), ...attrBefore, [], [colorCell('P', pen)]];
    return { ...common,
        before: read(['ABCDE.........', 'FGHIJ.........', '..............', '..............', '.........12345', '.........67890'], [5, 14]),
        setup: read(['ABCDE.........', 'FGHIJ.........', '..............', '..............', '.........12345', '.........67890'], [0, 0]),
        edited: read(pinnedCopy, [0, 0]),
        box: read(patched({ 4: 'opqrsABCDx', 5: 'yz012KLMN7' }), park), right: read(patched({ 0: 'ABABCDEFIJ' }), park), left: read(patched({ 1: 'MNOPQRQRST' }), park),
        down: read(patched({ 1: 'ABCDEPQRST', 2: 'KLMNOZabcd', 3: 'UVWXYjklmn', 4: 'efghituvwx' }), park),
        up: read(patched({ 1: 'UVWXYPQRST', 2: 'efghiZabcd', 3: 'opqrsjklmn', 4: 'yz012tuvwx' }), park),
        diagonal: read(patched({ 1: 'KABCDEQRST', 2: 'UKLMNOabcd', 3: 'eUVWXYklmn' }), park), self: read(block, park),
        cornerRow: read([...block, ...Array(4).fill(''), 'ABC'], park),
        cornerCol: read(patched({ 0: `ABCDEFGHIJ${' '.repeat(49)}A`, 1: `KLMNOPQRST${' '.repeat(49)}K` }), park),
        noop: read(block, park), pages: read(block, park),
        origin: read(patched({ 4: 'opqrUVWvwx', 5: 'yz01efg567' }), [3, 2], { ...region, origin: true }),
        margins: read(patched({ 2: 'LMNXYZabcd', 3: 'VWXhijklmn', 4: 'fghrstuvwx', 5: 'pqr1234567', 6: 'z01@#$%^&*' }), [5, 2], region),
        attr: { rows: copiedAttr, cursor: [11, 1] } };
}

const rectEditSuite = Object.freeze({ noncePrefix: 'NTILDE_RC_', prefix: 'rect', contracts: rectEditContracts,
    adapterId: rectEditAdapterId, derivedFrom: 'ntilde-owned-saved-reset-grading-v1', reset: rectEditReset,
    geometry: { rows: rectEditRows, cols: rectEditCols },
    decorate: screen => ({ ...screen, bufferState: { scrollTop: 0, scrollBottom: rectEditRows - 1, originMode: false, autoWrap: true, insertMode: false,
        pendingWrap: false, alternateScreen: true, scrollbackLines: 0 }, history: { startLine: 0, totalLines: 0, lines: [] } }),
    screens: (caseId, binding, readsOf) => {
        const reads = readsOf?.(caseId) ?? syntheticRectEditReads(caseId);
        const contract = rectEditContracts.find(item => item.caseId === caseId);
        return contract.roles.map(role => syntheticRectEditScreen(reads[role].rows, reads[role].cursor, reads[role].state, binding));
    },
    replay: replayRectEditPinned });

/** The screens of every read group of one synthetic case. */
export const syntheticRectEditScreens = (caseId, binding = paletteBinding(), readsOf = null) => rectEditSuite.screens(caseId, binding, readsOf);

/** A complete, valid synthetic run: frames, worker run log and adapter facts. */
export const syntheticRectEditEvidence = (runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64),
    binding = paletteBinding(), readsOf = null) => syntheticColorEvidence(rectEditSuite, runId, bundleSha256, binding, readsOf);

/** The independent contract verdict for one synthetic case. */
export function syntheticRectEditGrade(caseId, binding = paletteBinding(), readsOf = null) {
    const contract = rectEditContracts.find(item => item.caseId === caseId);
    return gradeRectEditCase(contract, rectEditObservation(contract, syntheticRectEditScreens(caseId, binding, readsOf)));
}
