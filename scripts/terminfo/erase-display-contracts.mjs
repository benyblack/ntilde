// Versioned probe contracts for the erase-in-display qualification batch:
// erase.screen.below (ED 0), erase.screen.above (ED 1) and erase.screen.all (ED 2).
//
// Pinned terminfo.dev@4.0.0 definitions (bundle SHA-256
// aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6, probeHash 16a690f34c0e):
//   eraseScreenResult(ctx, "\x1B[0J", ["AAAAA", "BB   ", "     "])   erase.screen.below
//   eraseScreenResult(ctx, "\x1B[1J", ["     ", "   BB", "CCCCC"])   erase.screen.above
//   eraseScreenResult(ctx, "\x1B[2J", ["     ", "     ", "     "])   erase.screen.all
// The termless path reads the measured row count `lines`, picks middle = floor(lines / 2), feeds ONE setup string
//   ESC[1;1H AAAAA ESC[middle+1;1H BBBBB ESC[lines;1H CCCCC ESC[middle+1;3H
// (so the fixture bytes depend on the measured geometry: this contract pins the 40-row owned screen, middle = 20, rows 0, 20 and 39),
// reads the first five cells of rows [0, middle, lines - 1], the cursor and the scrollback counts, feeds the erase sequence and reads
// them again. It is supported iff every expected cell matches (a blank is "" or " ") AND the cursor is exactly where it was before the
// erase. It is inconclusive when the fixture was not seeded (cells, cursor at column 2 of the middle row, totalLines >= lines). It never
// reads cells beyond column 4, rows other than the three, attributes, modes, or whether scrollback changed.
//
// SPELLINGS ARE DISTINCT. All three pinned fixtures use the EXPLICIT parameter (ESC [ 0 J, ESC [ 1 J, ESC [ 2 J); none uses the omitted
// spelling (ESC [ J). The pinned terminal pixel path (captureEraseFixture, kind "screen") uses the same explicit spellings, erases at
// ESC[middle;3H with middle = floor((rows + 1) / 2) (a different row from the termless path), and whatever the pixels show it returns
// pass:false / inconclusive "insufficient-evidence" (capture only, "no erase support judgment"): it never carries a verdict. This batch
// grades ONLY the termless criterion; the pixel path is not qualified. The omitted spelling and the two other explicit spellings run as
// separate, labeled controls that are observations, never grades. CSI 3J (erase.screen.scrollback, saved lines) is a different case, is not
// qualified here, and is never fed as an erase under test; it appears only as the cleanup that returns the disposable main screen to blank.
//
// The contract adds, only ever making the verdict stricter than the pinned one: every cell of the whole 40x140 screen outside the erased
// region keeps its stored text and neutral stored attributes, every cell inside it reads blank, the cursor and every mode flag stay as seeded
// (no cursor homing), a full-screen fill control (a distinct row marker on every row) with the cursor inside the screen, a boundary control
// with the cursor on the first or last column, a bold-seeded attribute control, and a main-screen history control: erasing the viewport must
// not remove scrollback lines (ED 2 is not ED 3). Whether erased cells take the current background color (BCE) is OBSERVED, not required: no
// pinned criterion constrains it.
import { planFeed, planRead, planFeeds, planReads, validateExactFeeds } from './graded-plan.mjs';
import { sgrAttrsReset, sgrAttrsClear, validateSgrAttrsScreen, sgrAttrsPredicates, sgrAttrsCellAt } from './sgr-attrs-contracts.mjs';
import { eraseLineInk } from './erase-line-contracts.mjs';

const { neutral } = sgrAttrsPredicates;

/** Contracts v1 is the first measured semantics of this batch. */
export const eraseDisplayContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const eraseDisplaySuiteId = 'ntilde-owned-erase-display-graded-v1';
/** The nonce prefix binding worker output to this suite. */
export const eraseDisplayNoncePrefix = 'NTILDE_ED_';
/** The measured reset every plan begins with: DECSCNM, DECOM, margins, IRM and SGR off, screen cleared, cursor home. */
export const eraseDisplayReset = sgrAttrsReset;
const clear = sgrAttrsClear;

/** The pinned note texts, byte for byte. */
export const pinnedGeometryNote = 'Screen fixture needs at least three measured rows and six columns';
export const pinnedFixtureNote = 'Screen fixture, cursor setup, or cell readback unavailable';
/** The contract's own refusal: the fixture bytes are geometry-dependent and are pinned to the owned 40x140 screen. */
export const contractGeometryNote = 'The measured geometry is not the contracted 40x140 screen, so the pinned fixture bytes and the controls do not apply';

/** The measured geometry every contracted plan assumes (the pinned setup string depends on the row count). */
export const eraseDisplayGeometry = Object.freeze({ rows: 40, cols: 140 });
const { rows: screenRows, cols: screenCols } = eraseDisplayGeometry;
const middleRow = Math.floor(screenRows / 2);
const lastRow = screenRows - 1;
const seedColumn = 2;
/** The seeded cursor cell of the pinned fixture: column 2 of the middle row. */
const seedCursor = Object.freeze([middleRow, seedColumn]);

const at = (row, col) => `\x1b[${row + 1};${col + 1}H`;
/** The pinned setup string, as the pinned probe builds it for `lines` measured rows. */
export const pinnedSetupFor = lines => {
    const middle = Math.floor(lines / 2);
    return `\x1B[1;1HAAAAA\x1B[${middle + 1};1HBBBBB\x1B[${lines};1HCCCCC\x1B[${middle + 1};3H`;
};
const pinnedSetup = pinnedSetupFor(screenRows);
/** The pinned seed text and row of every seeded run. */
const pinnedSeed = Object.freeze([[0, 'AAAAA'], [middleRow, 'BBBBB'], [lastRow, 'CCCCC']]);

/** The full-screen fill control: a distinct ten-character marker on every row plus a marker in the last column, then the cursor. */
const fillText = row => `ROW${String(row).padStart(2, '0')}-ABCD`;
const tailMarker = '|';
const fillWrites = Array.from({ length: screenRows }, (_, row) => at(row, 0) + fillText(row) + at(row, screenCols - 1) + tailMarker).join('');
const fillSeed = cursor => fillWrites + at(cursor[0], cursor[1]);
/** The cursor of the fill control and of the boundary control of each mode. */
const fillCursor = Object.freeze([middleRow, 4]);
const edgeCursors = Object.freeze({ 0: [middleRow, 0], 1: [middleRow, screenCols - 1], 2: [lastRow, screenCols - 1] });

/** The bold-seeded attribute control: the pinned seed written bold, the pen reset, then the blue background pen active when the erase runs. */
const bceSeed = '\x1b[1m\x1b[1;1HAAAAA' + at(middleRow, 0) + 'BBBBB' + at(lastRow, 0) + 'CCCCC\x1b[0m' + at(middleRow, seedColumn) + '\x1b[44m';
/** The reference write: one cell written under the same SGR 44 pen, so its stored background is the contracted pen background. */
const penReference = '\x1b[44mX';

/** The history control: scrollback lines made on the MAIN screen (no scrollback exists on the alternate screen) and the same pinned setup. */
const historyLines = Object.freeze(['HIST-A', 'HIST-B', 'HIST-C']);
const mainEnter = '\x1b[?1049l' + eraseDisplayReset + '\x1b[3J';
const historySeed = historyLines.join('\r\n') + '\r\n' + '\r\n'.repeat(screenRows);
/** Cleanup only: blank the disposable main screen and its saved lines, then return to the alternate screen. Never graded. */
const mainLeave = '\x1b[0m\x1b[2J\x1b[3J\x1b[H\x1b[?1049h';

/** The four ED spellings: omitted and explicit 0 are the same mode, spelled differently. */
const spellings = Object.freeze({ omitted: { bytes: '\x1b[J', mode: 0 }, explicit0: { bytes: '\x1b[0J', mode: 0 },
    explicit1: { bytes: '\x1b[1J', mode: 1 }, explicit2: { bytes: '\x1b[2J', mode: 2 } });

/** Whether (row, col) is inside the region ED `mode` erases with the cursor at `cursor` (the cursor cell is included in modes 0 and 1). */
const erasedAt = (mode, cursor, row, col) => mode === 2
    || (mode === 0 && (row > cursor[0] || (row === cursor[0] && col >= cursor[1])))
    || (mode === 1 && (row < cursor[0] || (row === cursor[0] && col <= cursor[1])));
/** The placements (blanks omitted) of one text laid on a row from column 0. */
const place = (row, text) => [...text].flatMap((char, col) => (char === ' ' ? [] : [[row, col, char]]));
const byPosition = (a, b) => (a[0] - b[0]) || (a[1] - b[1]);
const pinnedPlacements = Object.freeze(pinnedSeed.flatMap(([row, text]) => place(row, text)));
const fillPlacements = Object.freeze(Array.from({ length: screenRows }, (_, row) => [...place(row, fillText(row)), [row, screenCols - 1, tailMarker]]).flat());
/** The placements that survive an ED mode with the cursor at `cursor`. */
const surviving = (placements, mode, cursor) => placements.filter(([row, col]) => !erasedAt(mode, cursor, row, col));

/** The five-cell strings the pinned probe expects, per mode, for rows 0, middle and last. */
const pinnedRows = mode => pinnedSeed.map(([row, text]) => [...text].map((char, col) => (erasedAt(mode, seedCursor, row, col) ? ' ' : char)).join(''));

/** One case: the pinned fixture plus its discriminating controls, in a fixed plan. */
const case_ = ({ caseId, spelling }) => {
    const { bytes: sequence, mode } = spellings[spelling];
    const others = Object.keys(spellings).filter(name => name !== spelling);
    const wideSetup = fillSeed(fillCursor), edgeSetup = fillSeed(edgeCursors[mode]);
    const groups = [
        { role: 'initial', feeds: [] },
        { role: 'setup', feeds: [clear, pinnedSetup] },
        { role: 'erased', feeds: [sequence] },
        ...others.map(name => ({ role: name, feeds: [clear, pinnedSetup, spellings[name].bytes] })),
        { role: 'wideBefore', feeds: [clear, wideSetup] },
        { role: 'wide', feeds: [sequence] },
        { role: 'edgeBefore', feeds: [clear, edgeSetup] },
        { role: 'edge', feeds: [sequence] },
        { role: 'bceBefore', feeds: [clear, bceSeed] },
        { role: 'bce', feeds: [sequence] },
        { role: 'pen', feeds: [clear, penReference] },
        { role: 'historyBefore', feeds: [mainEnter, historySeed, pinnedSetup] },
        { role: 'history', feeds: [sequence] },
    ];
    const plan = [planFeed(eraseDisplayReset)];
    for (const group of groups) {
        for (const feed of group.feeds) plan.push(planFeed(feed));
        plan.push(planRead('cells'));
    }
    plan.push(planFeed(mainLeave));
    const pinnedCells = Object.freeze(pinnedRows(mode));
    const pinnedExpected = `rows 0,${middleRow},${lastRow}: ${pinnedCells.join('/')}; cursor preserved`;
    return Object.freeze({
        caseId, probeId: caseId, spelling, sequence, mode, setup: pinnedSetup,
        roles: Object.freeze(groups.map(group => group.role)), others: Object.freeze(others),
        pinnedPath: 'termless', initialRead: 0, setupRead: 1, pinnedRead: 2,
        pinnedExpected, pinnedCells, pinnedRows: Object.freeze([0, middleRow, lastRow]),
        pinnedCriterion: `rows 0, ${middleRow} and ${lastRow} read ${pinnedCells.map(cells => `"${cells}"`).join(', ')} after ED ${mode} (explicit parameter spelling) with the cursor `
            + `unmoved from column ${seedColumn} of row ${middleRow}`,
        pinnedFeeds: Object.freeze([pinnedSetup, sequence]),
        plan: Object.freeze(plan),
        terminalFixture: { writes: [pinnedSetup, sequence], demand: 'erased region blank, retained cells and cursor unchanged', minimum: { rows: 3, cols: 6 },
            note: 'upstream pixel path is capture-only (erases at the middle = floor((rows + 1) / 2) row) and never a grade' },
        appearanceScope: 'stored cells only: exact erased and retained characters on the whole screen, neutral stored attributes, untouched cursor and modes, scrollback not removed; '
            + 'background-color erase is observed, not required; the pinned pixel capture path is capture-only and is not claimed',
        grade: observation => gradeErase(contract_(caseId), observation),
        observed: observation => briefErase(contract_(caseId), observation),
    });
};

const contracts = [];
const contract_ = caseId => contracts.find(item => item.caseId === caseId);
contracts.push(
    case_({ caseId: 'erase.screen.below', spelling: 'explicit0' }),
    case_({ caseId: 'erase.screen.above', spelling: 'explicit1' }),
    case_({ caseId: 'erase.screen.all', spelling: 'explicit2' }),
);
/** The three pinned cases, in suite order. */
export const eraseDisplayContracts = Object.freeze(contracts);
/** Contract case ids in suite order. */
export const eraseDisplayCaseIds = Object.freeze(eraseDisplayContracts.map(contract => contract.caseId));

/** The pinned rowCells projection: the first five stored characters of a row. */
export const eraseDisplayCellAt = sgrAttrsCellAt;

/** The pinned getScrollback shape served from observed buffer state: the viewport row count and the viewport plus saved lines.
 * The observer has no viewport offset, so viewportY is the constant 0; the pinned probe never reads it. */
export const eraseDisplayScrollback = screen => ({ screenLines: screen.rows, totalLines: screen.rows + screen.bufferState.scrollbackLines, viewportY: 0 });

/** Validate one observation screen: attributes, stored details, resolved colors, buffer state and history together. */
export function validateEraseDisplayScreen(screen) {
    validateSgrAttrsScreen(screen);
    if (!screen.bufferState || !screen.history || screen.history.totalLines !== screen.bufferState.scrollbackLines) {
        throw new Error('Erase-in-display screens need buffer state and complete history.');
    }
}

/** Screens without buffer state or history cannot be graded. */
const missingState = () => { throw new Error('Erase-in-display screens need buffer state and complete history.'); };
/** A group's observation: its ink, cursor, geometry, buffer state and history. */
const group = screen => ({ ink: screen.bufferState && screen.history ? eraseLineInk(screen) : missingState(), cursor: [screen.cursorRow, screen.cursorCol], rows: screen.rows, cols: screen.cols,
    cursorVisible: screen.cursorVisible, buffer: screen.bufferState, history: screen.history.lines });
const sameCursor = (state, cursor) => state.cursor[0] === cursor[0] && state.cursor[1] === cursor[1];

/** Ink equals exactly these [row, col, char] cells, all with neutral stored attributes. */
function inkIs(state, placements) {
    const expected = [...placements].sort(byPosition);
    if (state.ink.length !== expected.length) return false;
    return expected.every(([row, col, char], index) => {
        const cell = state.ink[index];
        return cell.row === row && cell.col === col && cell.char === char && neutral(cell);
    });
}
/** Whether the stored cursor visibility and every buffer-state mode flag are unchanged between two reads. The saved-line count is
 * compared too unless `allowSavedLines` (the history control reports growth separately instead of failing it). */
const modesUnchanged = (before, after, allowSavedLines = false) => {
    const flags = state => (allowSavedLines ? { ...state.buffer, scrollbackLines: null } : state.buffer);
    return before.cursorVisible === after.cursorVisible && JSON.stringify(flags(before)) === JSON.stringify(flags(after));
};

/** The attribute control: retained cells keep their bold-only tuple, every other ink cell is a blank inside the erased region, and the
 * cursor stays. Background-color erase is compared with the pen reference by resolved color and by stored representation, separately. */
function bceState(contract, before, after, pen) {
    const bold = cell => cell.bold === true && !cell.dim && !cell.italic && !cell.inverse && !cell.hidden && !cell.strikethrough
        && !cell.blink && !cell.overline && cell.underlineStyle === 0 && cell.fgIndex === -1 && cell.bgIndex === -1
        && cell.foregroundArgb === null && cell.backgroundArgb === null && cell.underlineColorIndex === -1 && cell.underlineArgb === null;
    const retained = surviving(pinnedPlacements, contract.mode, seedCursor);
    const seeded = before.ink.length === pinnedPlacements.length
        && before.ink.every((cell, index) => cell.row === pinnedPlacements[index][0] && cell.col === pinnedPlacements[index][1]
            && cell.char === pinnedPlacements[index][2] && bold(cell)) && sameCursor(before, seedCursor);
    const text = after.ink.filter(cell => cell.char !== ' ');
    const blanks = after.ink.filter(cell => cell.char === ' ');
    const retainedOk = text.length === retained.length
        && text.every((cell, index) => cell.row === retained[index][0] && cell.col === retained[index][1] && cell.char === retained[index][2] && bold(cell));
    const blanksOk = blanks.every(cell => erasedAt(contract.mode, seedCursor, cell.row, cell.col));
    const painted = new Map(blanks.map(cell => [cell.row * after.cols + cell.col, cell]));
    const erased = [];
    for (let row = 0; row < after.rows; row++) {
        for (let col = 0; col < after.cols; col++) {
            // Every erased position counts, including the seeded letters that lay inside the region: each must have become a blank too.
            if (erasedAt(contract.mode, seedCursor, row, col)) erased.push([row, col]);
        }
    }
    const cells = erased.map(([row, col]) => painted.get(row * after.cols + col));
    const isPainted = cell => cell !== undefined && (cell.bgIndex >= 0 || cell.backgroundArgb !== null);
    // The pen reference: the stored background of a cell written under the same SGR 44 pen (never the default).
    const penCell = pen.ink.find(cell => cell.char === 'X');
    const penMeasured = penCell !== undefined && (penCell.bgIndex >= 0 || penCell.backgroundArgb !== null);
    // Resolved color and stored representation are compared separately: the same blue may be stored as a palette index or a literal.
    const sameResolved = cell => isPainted(cell) && JSON.stringify(cell.bg) === JSON.stringify(penCell.bg);
    const sameStored = cell => sameResolved(cell) && cell.bgIndex === penCell.bgIndex && cell.backgroundArgb === penCell.backgroundArgb;
    const bceApplied = cells.length === 0 ? null : cells.every(cell => cell === undefined) ? 'erased cells keep the default background'
        : !penMeasured ? 'unmeasured: the reference pen cell was not stored with a background'
            : cells.every(sameStored) ? 'erased cells carry the current background'
                : cells.every(sameResolved) ? 'erased cells carry the current background color, stored differently from the pen cell (palette index versus literal ARGB)'
                    : cells.every(isPainted) && !cells.some(sameResolved) ? 'erased cells carry a different non-default background color than the current pen'
                        : 'mixed';
    const storedOf = cell => (cell === undefined ? null : { bgIndex: cell.bgIndex, backgroundArgb: cell.backgroundArgb, resolved: cell.bg });
    const bceDetail = { pen: penMeasured ? storedOf(penCell) : null, firstErased: storedOf(cells[0]), lastErased: storedOf(cells.at(-1)),
        erasedCells: cells.length, paintedCells: cells.filter(isPainted).length };
    return { seeded, retainedOk, blanksOk, cursorOk: sameCursor(after, seedCursor), bceApplied, bceDetail };
}

/** The history control: the lines seeded on the main screen, and whether the erase kept every one of them in place. */
function historyState(before, after) {
    const seeded = before.buffer.alternateScreen === false && before.history.length >= historyLines.length
        && historyLines.every((line, index) => before.history[index] === line);
    const kept = after.history.length >= before.history.length && before.history.every((line, index) => after.history[index] === line);
    return { seeded, kept, grew: after.history.length - before.history.length };
}

/** Why the pinned rows did not match, as an unsupported note; the diagnosis only picks the words, never the verdict. */
function diagnose(contract, state) {
    // Mode -1 erases nothing, so it names the no-op.
    const same = mode => inkIs(state, surviving(pinnedPlacements, mode, seedCursor));
    if (same(-1)) return 'The screen was unchanged after the erase sequence: erase-in-display was a no-op';
    for (const mode of [0, 1, 2]) {
        if (mode !== contract.mode && same(mode)) {
            return mode === 2 ? `The screen matches ED 2 instead of ED ${contract.mode}: the whole screen was cleared`
                : `The screen matches ED ${mode} instead of ED ${contract.mode}: the wrong cells were erased`;
        }
    }
    return 'The erased or retained cells differ from the contracted screen';
}

/** The shared control walk, then the exact erase verdict. */
function gradeErase(contract, observation) {
    const { initial, setup, erased, wideBefore, wide, edgeBefore, edge, bceBefore, bce, pen, historyBefore, history, reverseVideo } = observation;
    if (setup.rows < 3 || setup.cols < 6) return { outcome: 'inconclusive', note: pinnedGeometryNote };
    if (setup.rows !== screenRows || setup.cols !== screenCols) return { outcome: 'inconclusive', note: contractGeometryNote };
    if (reverseVideo !== false) {
        return { outcome: 'inconclusive', note: 'DECSCNM reverse video was not proven off, so stored attributes cannot be separated from it' };
    }
    if (initial.ink.length !== 0) return { outcome: 'inconclusive', note: 'Blank neutral screen control was not measured (the screen was not empty)' };
    const setupScrollback = setup.buffer.scrollbackLines;
    if (!inkIs(setup, pinnedPlacements) || !sameCursor(setup, seedCursor) || !Number.isInteger(setupScrollback) || setupScrollback < 0) {
        return { outcome: 'inconclusive', note: pinnedFixtureNote };
    }
    if (!inkIs(wideBefore, fillPlacements) || !sameCursor(wideBefore, fillCursor)) {
        return { outcome: 'inconclusive', note: 'The full-screen fill control seed was not measured, so unaffected rows and columns cannot be distinguished' };
    }
    if (!inkIs(edgeBefore, fillPlacements) || !sameCursor(edgeBefore, edgeCursors[contract.mode])) {
        return { outcome: 'inconclusive', note: 'The boundary control seed was not measured, so the cursor-cell boundary cannot be distinguished' };
    }
    const attributes = bceState(contract, bceBefore, bce, pen);
    if (!attributes.seeded) return { outcome: 'inconclusive', note: 'The attribute control seed was not measured, so retained attributes cannot be distinguished' };
    const retainedHistory = historyState(historyBefore, history);
    if (!retainedHistory.seeded || !inkIs(historyBefore, pinnedPlacements) || !sameCursor(historyBefore, seedCursor)) {
        return { outcome: 'inconclusive', note: 'The main-screen history control seed was not measured, so scrollback preservation cannot be distinguished' };
    }
    if (!inkIs(erased, surviving(pinnedPlacements, contract.mode, seedCursor))) return { outcome: 'unsupported', note: diagnose(contract, erased) };
    if (!sameCursor(erased, seedCursor)) {
        return { outcome: 'unsupported', note: sameCursor(erased, [0, 0]) ? 'The cursor was homed during the erase: ED must leave it at its cell' : 'The cursor moved during the erase: ED must leave it at its cell' };
    }
    if (!modesUnchanged(setup, erased)) return { outcome: 'unsupported', note: 'A stored mode, margin, scrollback count or the cursor visibility changed during the erase' };
    if (!inkIs(wide, surviving(fillPlacements, contract.mode, fillCursor)) || !sameCursor(wide, fillCursor) || !modesUnchanged(wideBefore, wide)) {
        return { outcome: 'unsupported', note: 'The full-screen fill control differs: unaffected rows or columns changed, the cursor cell was handled wrongly or the cursor moved' };
    }
    const edgeCursor = edgeCursors[contract.mode];
    if (!inkIs(edge, surviving(fillPlacements, contract.mode, edgeCursor)) || !sameCursor(edge, edgeCursor) || !modesUnchanged(edgeBefore, edge)) {
        return { outcome: 'unsupported', note: 'The boundary control differs: the cursor row was erased wrongly at the first or last column, or the cursor moved' };
    }
    if (!modesUnchanged(bceBefore, bce)) {
        return { outcome: 'unsupported', note: 'A stored mode, margin, scrollback count or the cursor visibility changed during the erase in the attribute control' };
    }
    if (!attributes.retainedOk || !attributes.blanksOk || !attributes.cursorOk) {
        return { outcome: 'unsupported', note: 'Retained cells lost their stored text or attributes, an erased blank landed outside the erased region, or the cursor moved, in the attribute control' };
    }
    if (!modesUnchanged(historyBefore, history, true)) {
        return { outcome: 'unsupported', note: 'A stored mode, margin or the cursor visibility changed during the erase on the main screen' };
    }
    if (!retainedHistory.kept || !inkIs(history, surviving(pinnedPlacements, contract.mode, seedCursor)) || !sameCursor(history, seedCursor)) {
        return { outcome: 'unsupported', note: 'The main-screen control lost saved lines or changed the viewport differently: ED must not remove scrollback (that is ED 3)' };
    }
    return { outcome: 'supported' };
}

/** A compact stored record of one cell: a `row,col,char` string when its stored tuple is neutral, else the stored fields. */
const brief = cell => (neutral(cell) ? `${cell.row},${cell.col},${cell.char}`
    : { row: cell.row, col: cell.col, char: cell.char, flags: cell.flags, fgIndex: cell.fgIndex, bgIndex: cell.bgIndex,
        foregroundArgb: cell.foregroundArgb, backgroundArgb: cell.backgroundArgb });
/** The stored fields two adjacent non-neutral cells must share to read as one run. */
const runKey = cell => JSON.stringify([cell.row, cell.char, cell.flags, cell.fgIndex, cell.bgIndex, cell.foregroundArgb, cell.backgroundArgb]);
/** The ink list, with each run of identical adjacent non-neutral cells (an erased region painted with a background) collapsed to one
 * `{ row, cols: [first, last], ... }` entry; neutral cells stay one string each. */
function briefInk(ink) {
    const out = [];
    let last = null;
    for (const cell of ink) {
        if (neutral(cell)) { out.push(brief(cell)); last = null; continue; }
        if (last !== null && last.row === cell.row && last.col + 1 === cell.col && runKey(last) === runKey(cell)) {
            const entry = out.at(-1);
            if (entry.cols === undefined) { entry.cols = [entry.col, cell.col]; delete entry.col; } else entry.cols[1] = cell.col;
        } else out.push(brief(cell));
        last = cell;
    }
    return out;
}
/** The first ten columns of the given rows of a group's ink as text, `_` for blank. */
const render = (state, rows) => rows.map(row => Array.from({ length: 10 }, (_, col) => {
    const cell = state.ink.find(item => item.row === row && item.col === col);
    return cell === undefined || cell.char === ' ' ? '_' : cell.char;
}).join(''));
const pinnedRowIndexes = [0, middleRow, lastRow];
const allRows = Array.from({ length: screenRows }, (_, row) => row);

/** The observed record: every group's text, cursor and ink, plus the labeled spelling, background and history observations. */
function briefErase(contract, observation) {
    const pinnedState = item => ({ text: render(item, pinnedRowIndexes), cursor: item.cursor, ink: briefInk(item.ink) });
    const fillState = item => ({ text: render(item, allRows), cursor: item.cursor });
    const expectedFor = name => inkIs(observation[name], surviving(pinnedPlacements, spellings[name].mode, seedCursor)) && sameCursor(observation[name], seedCursor);
    const spellingRecord = name => ({ sequence: spellings[name].bytes, mode: spellings[name].mode, ...pinnedState(observation[name]),
        matchesEcma48: expectedFor(name), sameAsPinned: JSON.stringify(observation[name].ink) === JSON.stringify(observation.erased.ink) });
    const attributes = bceState(contract, observation.bceBefore, observation.bce, observation.pen);
    const retainedHistory = historyState(observation.historyBefore, observation.history);
    const scrollback = item => ({ alternateScreen: item.buffer.alternateScreen, scrollbackLines: item.buffer.scrollbackLines, history: item.history });
    return { geometry: { rows: observation.setup.rows, cols: observation.setup.cols }, reverseVideo: observation.reverseVideo,
        pinnedSpelling: contract.spelling, pinnedSequence: contract.sequence, pinnedRows: [...contract.pinnedRows],
        initial: { ink: observation.initial.ink.length, cursor: observation.initial.cursor },
        setup: { ...pinnedState(observation.setup), scrollback: scrollback(observation.setup) },
        erased: { ...pinnedState(observation.erased), scrollback: scrollback(observation.erased) },
        modesUnchanged: modesUnchanged(observation.setup, observation.erased),
        spellingControls: { policy: 'observed, not graded: separate spellings of the same screen; the pinned fixtures use only the explicit spelling',
            ...Object.fromEntries(contract.others.map(name => [name, spellingRecord(name)])) },
        fullScreen: { policy: 'unaffected rows and columns: a distinct ten-character marker on every row, cursor inside the screen', cursor: [...fillCursor],
            before: fillState(observation.wideBefore), after: fillState(observation.wide) },
        boundary: { policy: 'the cursor cell is erased at the first and last column', cursor: [...edgeCursors[contract.mode]],
            before: fillState(observation.edgeBefore), after: fillState(observation.edge) },
        attributes: { policy: 'retained cells keep their bold-only stored tuple (required); background-color erase is observed, not required',
            before: pinnedState(observation.bceBefore), after: pinnedState(observation.bce), penReference: pinnedState(observation.pen),
            retainedKeepBold: attributes.retainedOk, backgroundColorErase: attributes.bceApplied, backgroundColorEraseDetail: attributes.bceDetail },
        history: { policy: 'main-screen control: saved lines must not be removed by ED (ED 3 is a different case and is not fed)',
            before: { ...pinnedState(observation.historyBefore), scrollback: scrollback(observation.historyBefore) },
            after: { ...pinnedState(observation.history), scrollback: scrollback(observation.history) },
            seeded: retainedHistory.seeded, linesKept: retainedHistory.kept, linesAdded: retainedHistory.grew } };
}

/** Group the read screens of one case by role. */
export function eraseDisplayObservation(contract, screens) {
    if (!Array.isArray(screens) || screens.length !== contract.roles.length) throw new Error('Missing erase-in-display read captures.');
    const read = role => group(screens[contract.roles.indexOf(role)]);
    const observation = Object.fromEntries(contract.roles.map(role => [role, read(role)]));
    const binding = screens.map(screen => screen.colorBinding.reverseVideo);
    observation.reverseVideo = binding.every(value => value === false) ? false : binding.some(value => value === true) ? true : null;
    return observation;
}

/** Independent contract grader entry: mirrors the pinned criteria without the bundle. */
export function gradeEraseDisplayCase(contract, observation, revision = eraseDisplayContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown erase-in-display contracts revision ${revision}.`);
    if (!observation || contract.roles.some(role => !observation[role] || !Array.isArray(observation[role].ink))) {
        throw new Error(`Missing ${contract.caseId} read captures.`);
    }
    return contract.grade(observation);
}

export { planFeeds as eraseDisplayFeeds, planReads as eraseDisplayReads } from './graded-plan.mjs';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateEraseDisplayFeeds(contract, feeds) {
    validateExactFeeds(contract, feeds, 'contract');
}

/** The seed and fill helpers, exposed so the synthetic fixtures can build an independent oracle. */
export const eraseDisplayFixture = Object.freeze({ middleRow, lastRow, seedCursor, fillCursor, edgeCursors, historyLines, fillText, pinnedSeed });
