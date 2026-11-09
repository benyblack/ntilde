// Versioned probe contracts for the selective-erasure qualification batch:
// erase.selective (DECSED, CSI ? 2 J) and editing.decsera (DECSERA, CSI 1;1;1;5 $ {).
//
// Pinned terminfo.dev@4.0.0 definitions (bundle SHA-256
// aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6, probeHash 16a690f34c0e):
//   selectiveEraseResult(ctx, "\x1B[?2J", false)        erase.selective
//   selectiveEraseResult(ctx, "\x1B[1;1;1;5${", true)   editing.decsera
// The termless path needs six measured columns, then feeds ONE seed string
//   ESC[1;1H ESC[1"q P ESC[0"q ABCDZ
// (position row 0 column 0; DECSCA 1 protects P; DECSCA 0 leaves ABCDZ unprotected), reads the characters of the first six cells of
// row 0 and requires exactly "PABCDZ", feeds the erase sequence and reads the six characters again. The controls hold when P survives
// (and, for DECSERA only, Z survives); then it is supported iff the remaining characters are blank (ABCDZ for DECSED, ABCD for
// DECSERA; a blank is "" or " "). When P (or Z for DECSERA) was lost it is INCONCLUSIVE, not unsupported: "Protection or
// outside-rectangle control failed; cannot attribute selective erase". A `finally` feeds ESC[0"q (DECSCA 0) whatever happened. It never
// reads the cursor, attributes, modes, scrollback or any cell outside row 0 columns 0-5, and it never reads protection itself.
//
// The pinned terminal pixel path (the second registration argument of each case) writes a different fixture
// (erase.selective: ESC[1;1H ESC[2K ABCDE ESC[?2J; editing.decsera: ESC[1;1H ESC[1;1;3;5${), queries only the cursor position and
// returns pass:false / inconclusive "insufficient-evidence" through unmeasuredCellResult: it never carries a verdict, so there is no
// pixel criterion to qualify. This batch grades only the termless criterion; stored-state qualification does NOT establish pixel
// rendering. The derivation pins both terminal paths verbatim so a later upstream change that gave one a verdict would be refused.
//
// The contract adds, only ever making the verdict stricter than the pinned one, and unlike the pinned probe it OBSERVES protection:
//   - the observer reports each cell's stored DECSCA protection (opt-in readScreen includeProtection), so survival of P is never
//     inferred from a marker merely remaining: the seed must read P protected and ABCDZ unprotected, and the survivors must still be
//     protected;
//   - the cursor and every mode flag stay as seeded, every cell outside the erased region keeps its text, neutral attributes and
//     protection, every erased cell reads blank and unprotected, and saved lines are untouched;
//   - an ORDINARY erase of the same fixture (ED 2 / DECERA) must erase the protected P: protection is not read-only text;
//   - controls that attribute a verdict: a protection-off control (DECSCA 0 changes later writes, never existing cells), an overwrite
//     control (a character written over a protected cell takes the pen's protection), a restoration control (the pinned finalizer returns
//     the pen to unprotected), a full-screen fill (accidental whole-screen clearing or partial erasure), an edge control (the cursor
//     stays, bottom-right cell, whole-page rectangle), a bold attribute control (retained stored tuple; background-color erase observed,
//     not required), a main-screen history control and, per case, incorrect-bounds (DECSERA) or other-mode (DECSED, observed) controls.
// A separate contract may conclude unsupported only with independently valid controls establishing the defect; a lost P or lost outside
// Z with valid seeds is such a defect (the pinned probe calls it inconclusive: the divergence is recorded).
// ASCII only: wide cells, combining text and OSC 66 are not part of this batch.
import { planFeed, planRead, planFeeds, planReads, validateExactFeeds } from './graded-plan.mjs';
import { briefInk } from './erase-line-contracts.mjs';
import { sgrAttrsReset, sgrAttrsClear, validateSgrAttrsScreen, sgrAttrsPredicates, sgrAttrsCellAt, sgrAttrsExpand, sgrAttrsFlagBits } from './sgr-attrs-contracts.mjs';

const { neutral } = sgrAttrsPredicates;

/** Contracts v1 is the first measured semantics of this batch. */
export const selectiveEraseContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const selectiveEraseSuiteId = 'ntilde-owned-selective-erase-graded-v1';
/** The nonce prefix binding worker output to this suite. */
export const selectiveEraseNoncePrefix = 'NTILDE_SE_';
/** DECSCA off, then the measured reset every plan begins with: DECSCNM, DECOM, margins, IRM and SGR off, screen cleared, cursor home. */
export const selectiveEraseReset = '\x1b[0"q' + sgrAttrsReset;
const clear = '\x1b[0"q' + sgrAttrsClear;

/** The pinned note texts, byte for byte. */
export const pinnedColumnsNote = 'Need six measured columns';
export const pinnedSeedNote = 'Selective erase seed was not measured';
export const pinnedControlNote = 'Protection or outside-rectangle control failed; cannot attribute selective erase';
export const pinnedExpectedText = 'DECSCA-protected P survives while selective erase clears ABCD';
/** The contract's own refusals. */
export const contractGeometryNote = 'The measured geometry is not the contracted 40x140 screen, so the full-screen controls do not apply';
export const protectionUnavailableNote = 'Per-cell protection was not exposed by the observer, so protected survival cannot be told from a surviving marker';

/** The measured geometry every contracted plan assumes (the full-screen controls fill all of it). */
export const selectiveEraseGeometry = Object.freeze({ rows: 40, cols: 140 });
const { rows: screenRows, cols: screenCols } = selectiveEraseGeometry;
const middleRow = Math.floor(screenRows / 2);
const lastRow = screenRows - 1;
const lastCol = screenCols - 1;

const at = (row, col) => `\x1b[${row + 1};${col + 1}H`;
/** The pinned seed string, byte for byte, and the pinned finalizer. */
export const pinnedSeed = '\x1B[1;1H\x1B[1"qP\x1B[0"qABCDZ';
export const pinnedFinalizer = '\x1B[0"q';
const seedCursor = Object.freeze([0, 6]);
const protectOn = '\x1b[1"q', protectOff = '\x1b[0"q';

/** A seed cell is [row, col, char, protected]. */
const pinnedPlacements = Object.freeze([[0, 0, 'P', true], [0, 1, 'A', false], [0, 2, 'B', false], [0, 3, 'C', false], [0, 4, 'D', false], [0, 5, 'Z', false]]);
/** The cells a selective erase leaves: every protected cell, and every cell outside the rectangle (null rectangle: the whole screen is in it). */
const selectiveSurvivors = (cells, rect = null) => cells.filter(([row, col, , guarded]) => guarded || (rect !== null && outside(rect, row, col)));
/** The cells an ordinary rectangle erase leaves: every cell outside it. */
const ordinarySurvivors = (cells, rect) => cells.filter(([row, col]) => outside(rect, row, col));
const outside = (rect, row, col) => row < rect.top || row > rect.bottom || col < rect.left || col > rect.right;
const rectOf = (top, left, bottom, right) => Object.freeze({ top, left, bottom, right });

/** The full-screen fill: `ROWnn-ABCD` on every row and a tail marker in the last column, with protection on a pattern the erase must honor. */
const fillText = row => `ROW${String(row).padStart(2, '0')}-ABCD`;
const tailMarker = '|';
const tailProtectedRows = Object.freeze([0, middleRow, lastRow]);
const fillProtected = (row, col) => (col === 0 && row % 4 === 0) || (col === 5 && row % 3 === 0) || (col === lastCol && tailProtectedRows.includes(row));
const fillCells = Object.freeze(Array.from({ length: screenRows }, (_, row) => [
    ...[...fillText(row)].map((char, col) => [row, col, char, fillProtected(row, col)]),
    [row, lastCol, tailMarker, fillProtected(row, lastCol)],
]).flat());
/** The fill's bytes: one write per row, toggling DECSCA only where the protection pattern changes, then the pen off. */
const fillWrites = Array.from({ length: screenRows }, (_, row) => {
    let bytes = at(row, 0), pen = false;
    const put = (col, char) => {
        const want = fillProtected(row, col);
        if (want !== pen) { bytes += want ? protectOn : protectOff; pen = want; }
        bytes += char;
    };
    [...fillText(row)].forEach((char, col) => put(col, char));
    bytes += at(row, lastCol);
    put(lastCol, tailMarker);
    return bytes + (pen ? protectOff : '');
}).join('');
const fillCursor = Object.freeze([middleRow, 4]);
const edgeCursor = Object.freeze([lastRow, lastCol]);
const fillSeed = cursor => fillWrites + at(cursor[0], cursor[1]);
/** The rectangle of the fill control: rows 11-31, columns 4-10 (one-based), which covers `nn-ABCD` of rows 10-30 including protected dashes. */
const fillRect = rectOf(10, 3, 30, 9);
const wholePage = rectOf(0, 0, lastRow, lastCol);

/** The protection-off control: A and B written protected, then C and D unprotected. */
const penSeed = '\x1b[1;1H' + protectOn + 'AB' + protectOff + 'CD';
const penCells = Object.freeze([[0, 0, 'A', true], [0, 1, 'B', true], [0, 2, 'C', false], [0, 3, 'D', false]]);
const penCursor = Object.freeze([0, 4]);
/** The overwrite control: the same, then an unprotected X written over the protected A. */
const overwriteSeed = '\x1b[1;1H' + protectOn + 'AB' + protectOff + '\x1b[1;1HX';
const overwriteCells = Object.freeze([[0, 0, 'X', false], [0, 1, 'B', true]]);
const overwriteCursor = Object.freeze([0, 1]);
/** The restoration control: protection on, the pinned finalizer, then a character that must be unprotected. */
const restoreFeeds = Object.freeze([clear, protectOn, pinnedFinalizer, '\x1b[1;1HQ']);
const restoreCells = Object.freeze([[0, 0, 'Q', false]]);
const restoreCursor = Object.freeze([0, 1]);

/** The bold attribute control: the pinned seed written bold, the pen reset, then a blue background pen active when the erase runs. */
const attrSeed = '\x1b[1m\x1b[1;1H' + protectOn + 'P' + protectOff + 'ABCDZ\x1b[0m' + at(0, 6) + '\x1b[44m';

/** The history control: scrollback lines made on the MAIN screen (no scrollback exists on the alternate screen) and then the pinned seed. */
const historyLines = Object.freeze(['HIST-A', 'HIST-B', 'HIST-C']);
const mainEnter = '\x1b[?1049l' + selectiveEraseReset + '\x1b[3J';
const historySeed = historyLines.join('\r\n') + '\r\n' + '\r\n'.repeat(screenRows);
/** Cleanup only: blank the disposable main screen and its saved lines, then return to the alternate screen. Never graded. */
const mainLeave = '\x1b[0"q\x1b[0m\x1b[2J\x1b[3J\x1b[H\x1b[?1049h';

const byPosition = (a, b) => (a[0] - b[0]) || (a[1] - b[1]);

/** One case: the pinned fixture plus its discriminating controls, in a fixed plan. */
const case_ = ({ caseId, sequence, rectangular, variants, ordinary, fillErase, edgeErase, penErase, minimum, terminalWrites }) => {
    const wideSeed = fillSeed(fillCursor), edgeSeed = fillSeed(edgeCursor);
    const groups = [
        { role: 'initial', feeds: [] },
        { role: 'seeded', feeds: [clear, pinnedSeed] },
        { role: 'erased', feeds: [sequence] },
        { role: 'finalized', feeds: [pinnedFinalizer] },
        { role: 'ordinary', feeds: [clear, pinnedSeed, ordinary.bytes] },
        ...variants.map(variant => ({ role: variant.role, feeds: [clear, pinnedSeed, ...variant.feeds] })),
        { role: 'penBefore', feeds: [clear, penSeed] },
        { role: 'pen', feeds: [penErase] },
        { role: 'overwriteBefore', feeds: [clear, overwriteSeed] },
        { role: 'overwrite', feeds: [penErase] },
        { role: 'restore', feeds: restoreFeeds },
        { role: 'fillBefore', feeds: [clear, wideSeed] },
        { role: 'fill', feeds: [fillErase.bytes] },
        { role: 'edgeBefore', feeds: [clear, edgeSeed] },
        { role: 'edge', feeds: [edgeErase.bytes] },
        { role: 'attrBefore', feeds: [clear, attrSeed] },
        { role: 'attr', feeds: [sequence] },
        { role: 'historyBefore', feeds: [mainEnter, historySeed, pinnedSeed] },
        { role: 'history', feeds: [sequence] },
    ];
    const plan = [planFeed(selectiveEraseReset)];
    for (const group of groups) {
        for (const feed of group.feeds) plan.push(planFeed(feed));
        plan.push(planRead('cells'));
    }
    plan.push(planFeed(mainLeave));
    const roles = Object.freeze(groups.map(group => group.role));
    return Object.freeze({
        caseId, probeId: caseId, sequence, rectangular, ordinary, fillErase, edgeErase, penErase, variants: Object.freeze(variants), roles,
        pinnedPath: 'termless', initialRead: roles.indexOf('initial'), setupRead: roles.indexOf('seeded'), pinnedRead: roles.indexOf('erased'),
        finalRead: roles.indexOf('finalized'),
        pinnedExpected: pinnedExpectedText,
        pinnedCriterion: `after the seed "PABCDZ" (P written under DECSCA 1, ABCDZ under DECSCA 0) ${sequence === '\x1B[?2J' ? 'CSI ? 2 J' : 'CSI 1;1;1;5 $ {'} must leave `
            + `P in row 0 column 0${rectangular ? ' and Z in column 5' : ''} and read ${rectangular ? 'ABCD' : 'ABCDZ'} blank; a lost P${rectangular ? ' or Z' : ''} is inconclusive, not unsupported`,
        pinnedFeeds: Object.freeze([pinnedSeed, sequence, pinnedFinalizer]),
        plan: Object.freeze(plan),
        terminalFixture: { writes: terminalWrites, demand: 'cursor-position response only (never a verdict)', minimum,
            note: 'upstream pixel path only queries the cursor position through unmeasuredCellResult and never carries a verdict' },
        appearanceScope: 'stored cells only: exact erased and retained characters, stored per-cell protection, neutral stored attributes, untouched cursor, modes and saved lines; '
            + 'background-color erase is observed, not required; the pinned pixel path is not claimed',
        grade: observation => gradeSelective(contract_(caseId), observation),
        observed: observation => briefSelective(contract_(caseId), observation),
    });
};

const contracts = [];
const contract_ = caseId => contracts.find(item => item.caseId === caseId);
contracts.push(
    case_({ caseId: 'erase.selective', sequence: '\x1B[?2J', rectangular: false,
        ordinary: { bytes: '\x1b[2J', name: 'ED 2' },
        variants: [
            { role: 'mode0', feeds: [at(0, 3), '\x1b[?0J'], mode: 0, name: 'DECSED 0 from column 3' },
            { role: 'mode1', feeds: [at(0, 3), '\x1b[?1J'], mode: 1, name: 'DECSED 1 through column 3' },
        ],
        fillErase: { bytes: '\x1B[?2J', rect: null }, edgeErase: { bytes: '\x1B[?2J', rect: null }, penErase: '\x1B[?2J',
        minimum: { rows: 1, cols: 6 }, terminalWrites: ['\x1B[1;1H\x1B[2K', 'ABCDE', '\x1B[?2J'] }),
    case_({ caseId: 'editing.decsera', sequence: '\x1B[1;1;1;5${', rectangular: true,
        ordinary: { bytes: '\x1b[1;1;1;5$z', name: 'DECERA 1;1;1;5' },
        variants: [
            { role: 'rectInterior', feeds: ['\x1b[1;2;1;4${'], rect: rectOf(0, 1, 0, 3), name: 'DECSERA 1;2;1;4' },
            { role: 'rectWide', feeds: ['\x1b[1;1;1;6${'], rect: rectOf(0, 0, 0, 5), name: 'DECSERA 1;1;1;6' },
            { role: 'rectReversed', feeds: ['\x1b[1;5;1;2${'], rect: null, name: 'DECSERA 1;5;1;2 (reversed)' },
        ],
        fillErase: { bytes: '\x1b[11;4;31;10${', rect: fillRect }, edgeErase: { bytes: '\x1b[1;1;40;140${', rect: wholePage }, penErase: '\x1B[1;1;1;5${',
        minimum: { rows: 3, cols: 5 }, terminalWrites: ['\x1B[1;1H', '\x1B[1;1;3;5${'] }),
);
/** The two pinned cases, in suite order. */
export const selectiveEraseContracts = Object.freeze(contracts);
/** Contract case ids in suite order. */
export const selectiveEraseCaseIds = Object.freeze(selectiveEraseContracts.map(contract => contract.caseId));

/** The pinned getCell(0, col).char projection of one observed screen. */
export const selectiveEraseCellAt = sgrAttrsCellAt;

/** Validate one observation screen: stored attributes, per-cell protection, buffer state and history together. */
export function validateSelectiveEraseScreen(screen) {
    validateSgrAttrsScreen(screen);
    if (!screen.bufferState || !screen.history || screen.history.totalLines !== screen.bufferState.scrollbackLines) {
        throw new Error('Selective-erase screens need buffer state and complete history.');
    }
    if (!Array.isArray(screen.cellDetails) || screen.cellDetails.some(run => typeof run.protected !== 'boolean')) {
        throw new Error('Selective-erase screens need per-cell protection state.');
    }
}

/** Whether one stored cell detail is the default tuple (no attribute, default colors, no underline or overline color). */
const defaultDetail = detail => detail.flags === 0x300 && detail.fg === -1 && detail.bg === -1 && detail.foregroundArgb == null
    && detail.backgroundArgb == null && detail.overline === false && detail.underlineStyle === 0
    && detail.underlineColorIndex === -1 && detail.underlineArgb == null;

/** Every cell of the screen that carries text, a non-default stored tuple or protection, in row-major order with stored detail.
 * A cell absent from this list is an unprotected blank with default storage. */
export function selectiveEraseInk(screen) {
    validateSelectiveEraseScreen(screen);
    const expanded = sgrAttrsExpand(screen);
    const ink = [];
    for (let position = 0; position < screen.rows * screen.cols; position++) {
        const row = Math.floor(position / screen.cols), col = position % screen.cols;
        const char = (screen.lines[row] ?? '')[col] ?? ' ';
        const detail = expanded.details[position];
        const underlined = (detail.flags & sgrAttrsFlagBits.underline) !== 0;
        if (detail.flags !== expanded.flags[position] || underlined !== (detail.underlineStyle !== 0)) {
            throw new Error(`Contradictory stored cell projections at ${row},${col}.`);
        }
        if (typeof detail.protected !== 'boolean') throw new Error(`Missing stored protection at ${row},${col}.`);
        if (char === ' ' && defaultDetail(detail) && !detail.protected) continue;
        ink.push({ row, col, ...sgrAttrsCellAt(screen, row, col, expanded) });
    }
    return ink;
}

/** The pinned getScrollback shape: viewport rows and rows plus saved lines (the pinned probe never reads it). */
export const selectiveEraseScrollback = screen => ({ screenLines: screen.rows, totalLines: screen.rows + screen.bufferState.scrollbackLines, viewportY: 0 });

/** A group's observation: its ink, cursor, geometry, buffer state and history. */
const group = screen => ({ ink: selectiveEraseInk(screen), cursor: [screen.cursorRow, screen.cursorCol], rows: screen.rows, cols: screen.cols,
    cursorVisible: screen.cursorVisible, buffer: screen.bufferState, history: screen.history.lines });
const sameCursor = (state, cursor) => state.cursor[0] === cursor[0] && state.cursor[1] === cursor[1];

/** Ink equals exactly these [row, col, char, protected] cells: same text, same stored protection, neutral stored attributes. */
function inkIs(state, placements) {
    const expected = [...placements].sort(byPosition);
    if (state.ink.length !== expected.length) return false;
    return expected.every(([row, col, char, guarded], index) => {
        const cell = state.ink[index];
        return cell.row === row && cell.col === col && cell.char === char && cell.protected === guarded && neutral(cell);
    });
}
/** The stored cursor visibility and every buffer-state mode flag are unchanged between two reads (saved lines compared unless allowed to grow). */
const modesUnchanged = (before, after, allowSavedLines = false) => {
    const flags = state => (allowSavedLines ? { ...state.buffer, scrollbackLines: null } : state.buffer);
    return before.cursorVisible === after.cursorVisible && JSON.stringify(flags(before)) === JSON.stringify(flags(after));
};
const bold = cell => cell.bold === true && !cell.dim && !cell.italic && !cell.inverse && !cell.hidden && !cell.strikethrough
    && !cell.blink && !cell.overline && cell.underlineStyle === 0 && cell.fgIndex === -1 && cell.bgIndex === -1
    && cell.foregroundArgb === null && cell.backgroundArgb === null && cell.underlineColorIndex === -1 && cell.underlineArgb === null;

/** The cells this case's selective erase leaves from a seed, given the erase's rectangle (null for the whole-screen DECSED). */
const leaves = (contract, cells, rect) => (contract.rectangular ? selectiveSurvivors(cells, rect) : selectiveSurvivors(cells));

/** The attribute control: retained cells keep their bold-only tuple and protection; every erased cell is a blank. Background-color
 * erase is described, not required. */
function attributeState(contract, before, after) {
    const seeded = before.ink.length === pinnedPlacements.length
        && before.ink.every((cell, index) => cell.row === pinnedPlacements[index][0] && cell.col === pinnedPlacements[index][1]
            && cell.char === pinnedPlacements[index][2] && cell.protected === pinnedPlacements[index][3] && bold(cell)) && sameCursor(before, seedCursor);
    const expected = leaves(contract, pinnedPlacements, rectOf(0, 0, 0, 4));
    const text = after.ink.filter(cell => cell.char !== ' ');
    const retainedOk = text.length === expected.length
        && text.every((cell, index) => cell.row === expected[index][0] && cell.col === expected[index][1] && cell.char === expected[index][2]
            && cell.protected === expected[index][3] && bold(cell));
    // The region this case erases: the whole screen for DECSED, the rectangle for DECSERA. Every unprotected cell of it becomes a blank
    // (with the current background, if the pen paints erased cells), so blanks may lie anywhere in the region but never outside it.
    const rect = contract.rectangular ? rectOf(0, 0, 0, 4) : null;
    const inRegion = (row, col) => rect === null || !outside(rect, row, col);
    const blanks = after.ink.filter(cell => cell.char === ' ');
    const blanksOk = blanks.every(cell => inRegion(cell.row, cell.col) && cell.protected === false);
    let regionCells = 0;
    for (let row = 0; row < after.rows; row++) for (let col = 0; col < after.cols; col++) if (inRegion(row, col)) regionCells++;
    const retainedInRegion = expected.filter(([row, col]) => inRegion(row, col)).length;
    const erasedCells = regionCells - retainedInRegion;
    const painted = blanks.filter(cell => cell.bgIndex >= 0 || cell.backgroundArgb !== null);
    const backgroundColorErase = erasedCells === 0 ? null : painted.length === 0 ? 'erased cells keep the default background'
        : painted.length === erasedCells ? 'erased cells carry the current background' : 'mixed';
    const detail = { erasedCells, paintedCells: painted.length,
        firstPainted: painted[0] === undefined ? null : { bgIndex: painted[0].bgIndex, backgroundArgb: painted[0].backgroundArgb, resolved: painted[0].bg } };
    return { seeded, retainedOk, blanksOk, cursorOk: sameCursor(after, seedCursor), backgroundColorErase, detail };
}

/** The history control: the lines seeded on the main screen, and whether the erase kept every one of them in place. */
function historyState(before, after) {
    const seeded = before.buffer.alternateScreen === false && before.history.length >= historyLines.length
        && historyLines.every((line, index) => before.history[index] === line);
    const kept = after.history.length >= before.history.length && before.history.every((line, index) => after.history[index] === line);
    return { seeded, kept, grew: after.history.length - before.history.length };
}

/** Why the erased screen did not match, as an unsupported note; the diagnosis only picks the words, never the verdict. */
function diagnose(contract, state) {
    if (inkIs(state, pinnedPlacements)) return 'The screen was unchanged after the erase sequence: the selective erase was a no-op';
    if (state.ink.length === 0) return 'Everything was erased, including the protected P: the erase ignored protection (it behaved as an ordinary erase)';
    const protectedSurvivors = state.ink.filter(cell => cell.protected);
    if (protectedSurvivors.length === 0) return 'Protected characters did not survive the selective erase, and nothing reads protected afterwards';
    return contract.rectangular ? 'The erased or retained cells differ from the contracted rectangle: bounds or protection were handled wrongly'
        : 'The erased or retained cells differ from the contracted screen';
}

/** The seeds of every control the verdict leans on, in the order their failure is reported as inconclusive. */
function controlSeeds(o) {
    const fillSeeded = inkIs(o.fillBefore, fillCells) && sameCursor(o.fillBefore, fillCursor);
    const edgeSeeded = inkIs(o.edgeBefore, fillCells) && sameCursor(o.edgeBefore, edgeCursor);
    const penSeeded = inkIs(o.penBefore, penCells) && sameCursor(o.penBefore, penCursor);
    const overwriteSeeded = inkIs(o.overwriteBefore, overwriteCells) && sameCursor(o.overwriteBefore, overwriteCursor);
    return [
        [fillSeeded, 'The full-screen fill control seed was not measured, so unaffected rows and protected cells cannot be distinguished'],
        [edgeSeeded, 'The edge control seed was not measured, so the cursor and corner cells cannot be distinguished'],
        [penSeeded, 'The protection-off control seed was not measured, so existing cells cannot be told from later writes'],
        [overwriteSeeded, 'The overwrite control seed was not measured, so a character written over a protected cell cannot be distinguished'],
    ].map(([ok, note]) => ({ ok, note }));
}

/** The shared control walk, then the exact erase verdict. */
function gradeSelective(contract, observation) {
    const o = observation;
    const { initial, seeded, erased, finalized } = o;
    if (seeded.cols < 6) return { outcome: 'inconclusive', note: pinnedColumnsNote };
    if (seeded.rows !== screenRows || seeded.cols !== screenCols) return { outcome: 'inconclusive', note: contractGeometryNote };
    if (o.reverseVideo !== false) return { outcome: 'inconclusive', note: 'DECSCNM reverse video was not proven off, so stored attributes cannot be separated from it' };
    if (initial.ink.length !== 0) return { outcome: 'inconclusive', note: 'Blank neutral screen control was not measured (the screen was not empty or held protected cells)' };
    if (!sameCursor(seeded, seedCursor) || !inkIs(seeded, pinnedPlacements)) return { outcome: 'inconclusive', note: pinnedSeedNote };
    for (const seed of controlSeeds(o)) if (!seed.ok) return { outcome: 'inconclusive', note: seed.note };
    const attributes = attributeState(contract, o.attrBefore, o.attr);
    if (!attributes.seeded) return { outcome: 'inconclusive', note: 'The attribute control seed was not measured, so retained attributes cannot be distinguished' };
    const retainedHistory = historyState(o.historyBefore, o.history);
    if (!retainedHistory.seeded || !inkIs(o.historyBefore, pinnedPlacements) || !sameCursor(o.historyBefore, seedCursor)) {
        return { outcome: 'inconclusive', note: 'The main-screen history control seed was not measured, so scrollback preservation cannot be distinguished' };
    }
    const wholeRect = contract.rectangular ? rectOf(0, 0, 0, 4) : null;
    if (!inkIs(erased, leaves(contract, pinnedPlacements, wholeRect))) return { outcome: 'unsupported', note: diagnose(contract, erased) };
    if (!sameCursor(erased, seedCursor)) return { outcome: 'unsupported', note: 'The cursor moved during the selective erase: it must stay at its cell' };
    if (!modesUnchanged(seeded, erased)) return { outcome: 'unsupported', note: 'A stored mode, margin, scrollback count or the cursor visibility changed during the erase' };
    if (!inkIs(finalized, leaves(contract, pinnedPlacements, wholeRect)) || !sameCursor(finalized, seedCursor) || !modesUnchanged(erased, finalized)) {
        return { outcome: 'unsupported', note: 'The pinned finalizer (DECSCA 0) changed the screen, the cursor or a stored mode' };
    }
    const ordinaryRect = contract.rectangular ? rectOf(0, 0, 0, 4) : null;
    const ordinaryExpected = contract.rectangular ? ordinarySurvivors(pinnedPlacements, ordinaryRect).map(([row, col, char]) => [row, col, char, false]) : [];
    if (!inkIs(o.ordinary, ordinaryExpected) || !sameCursor(o.ordinary, seedCursor)) {
        return { outcome: 'unsupported', note: `The ordinary ${contract.ordinary.name} control did not erase the protected P: protection must not make text read-only` };
    }
    const penLeaves = leaves(contract, penCells, contract.rectangular ? rectOf(0, 0, 0, 4) : null);
    if (!inkIs(o.pen, penLeaves) || !sameCursor(o.pen, penCursor) || !modesUnchanged(o.penBefore, o.pen)) {
        return { outcome: 'unsupported', note: 'The protection-off control differs: DECSCA 0 reclassified existing cells, or later writes were protected, or the erase moved the cursor' };
    }
    const overwriteLeaves = leaves(contract, overwriteCells, contract.rectangular ? rectOf(0, 0, 0, 4) : null);
    if (!inkIs(o.overwrite, overwriteLeaves) || !sameCursor(o.overwrite, overwriteCursor)) {
        return { outcome: 'unsupported', note: 'The overwrite control differs: a character written over a protected cell did not take the pen\'s protection' };
    }
    if (!inkIs(o.restore, restoreCells) || !sameCursor(o.restore, restoreCursor)) {
        return { outcome: 'unsupported', note: 'The restoration control differs: a character written after the pinned finalizer was protected' };
    }
    const fillLeaves = leaves(contract, fillCells, contract.fillErase.rect);
    if (!inkIs(o.fill, fillLeaves) || !sameCursor(o.fill, fillCursor) || !modesUnchanged(o.fillBefore, o.fill)) {
        return { outcome: 'unsupported', note: 'The full-screen fill control differs: unprotected cells survived, protected cells or cells outside the region were erased, or the cursor moved' };
    }
    const edgeLeaves = leaves(contract, fillCells, contract.edgeErase.rect);
    if (!inkIs(o.edge, edgeLeaves) || !sameCursor(o.edge, edgeCursor) || !modesUnchanged(o.edgeBefore, o.edge)) {
        return { outcome: 'unsupported', note: 'The edge control differs: the corner cells or the last row and column were handled wrongly, or the cursor moved' };
    }
    const boundsNote = contract.rectangular ? rectangleBounds(contract, o) : null;
    if (boundsNote !== null) return { outcome: 'unsupported', note: boundsNote };
    if (!modesUnchanged(o.attrBefore, o.attr)) return { outcome: 'unsupported', note: 'A stored mode, margin, scrollback count or the cursor visibility changed during the erase in the attribute control' };
    if (!attributes.retainedOk || !attributes.blanksOk || !attributes.cursorOk) {
        return { outcome: 'unsupported', note: 'Retained cells lost their stored text, attributes or protection, an erased blank stayed protected or landed outside the erased region, or the cursor moved, in the attribute control' };
    }
    if (!modesUnchanged(o.historyBefore, o.history, true)) return { outcome: 'unsupported', note: 'A stored mode, margin or the cursor visibility changed during the erase on the main screen' };
    if (!retainedHistory.kept || !inkIs(o.history, leaves(contract, pinnedPlacements, wholeRect)) || !sameCursor(o.history, seedCursor)) {
        return { outcome: 'unsupported', note: 'The main-screen control lost saved lines or changed the viewport differently: a selective erase must not touch saved lines' };
    }
    return { outcome: 'supported' };
}

/** DECSERA bounds controls: an interior rectangle spares both ends, a wider one reaches Z, a reversed one is ignored. Null when all hold. */
function rectangleBounds(contract, o) {
    const interior = leaves(contract, pinnedPlacements, contract.variants[0].rect);
    if (!inkIs(o.rectInterior, interior) || !sameCursor(o.rectInterior, seedCursor)) {
        return 'The interior-rectangle control differs: the rectangle borders were applied wrongly (cells beyond them were erased or cells inside them survived)';
    }
    if (!inkIs(o.rectWide, leaves(contract, pinnedPlacements, contract.variants[1].rect)) || !sameCursor(o.rectWide, seedCursor)) {
        return 'The wider-rectangle control differs: a rectangle that includes Z must erase it, so the right border was applied wrongly';
    }
    if (!inkIs(o.rectReversed, pinnedPlacements) || !sameCursor(o.rectReversed, seedCursor)) {
        return 'The reversed-border control differs: an inverted rectangle must erase nothing';
    }
    return null;
}

/** The first twelve columns of row 0 of a group as text (`_` for blank) with the protected columns listed. */
const rowZero = state => ({ text: Array.from({ length: 12 }, (_, col) => {
    const cell = state.ink.find(item => item.row === 0 && item.col === col);
    return cell === undefined || cell.char === ' ' ? '_' : cell.char;
}).join(''), protectedColumns: state.ink.filter(cell => cell.row === 0 && cell.protected).map(cell => cell.col), cursor: state.cursor });
/** Counts and bounds of a full-screen group: every ink cell, the protected ones, the first and last ink cell. */
const screenSummary = state => ({ inkCells: state.ink.length, protectedCells: state.ink.filter(cell => cell.protected).length,
    unprotectedCells: state.ink.filter(cell => !cell.protected).length, first: state.ink[0] ? [state.ink[0].row, state.ink[0].col] : null,
    last: state.ink.at(-1) ? [state.ink.at(-1).row, state.ink.at(-1).col] : null, cursor: state.cursor });

/** The observed record: every group's text, protection and cursor, plus the labeled variant, background and history observations. */
function briefSelective(contract, o) {
    const attributes = attributeState(contract, o.attrBefore, o.attr);
    const retainedHistory = historyState(o.historyBefore, o.history);
    const scrollback = item => ({ alternateScreen: item.buffer.alternateScreen, scrollbackLines: item.buffer.scrollbackLines, history: item.history });
    const variants = Object.fromEntries(contract.variants.map(variant => [variant.role, { name: variant.name, feeds: variant.feeds, ...rowZero(o[variant.role]) }]));
    return { geometry: { rows: o.seeded.rows, cols: o.seeded.cols }, reverseVideo: o.reverseVideo,
        pinnedSequence: contract.sequence, rectangular: contract.rectangular,
        initial: { ink: o.initial.ink.length, cursor: o.initial.cursor },
        seeded: { ...rowZero(o.seeded), scrollback: scrollback(o.seeded) },
        erased: { ...rowZero(o.erased), scrollback: scrollback(o.erased) },
        finalized: rowZero(o.finalized),
        modesUnchanged: modesUnchanged(o.seeded, o.erased),
        ordinaryControl: { policy: 'the ordinary erase of the same seed must erase the protected P (protection is not read-only)', name: contract.ordinary.name,
            sequence: contract.ordinary.bytes, after: rowZero(o.ordinary) },
        variants: { policy: contract.rectangular
            ? 'required bounds controls: an interior rectangle, a wider one that reaches Z, and a reversed one that erases nothing'
            : 'observed, not graded: DECSED modes 0 and 1 are other sequences than the pinned mode 2 and are not qualified here', ...variants },
        protectionOff: { policy: 'DECSCA 0 changes later writes only: A and B written protected, C and D unprotected', before: rowZero(o.penBefore), after: rowZero(o.pen) },
        overwrite: { policy: 'an unprotected X written over the protected A takes the pen: X erasable, B still protected', before: rowZero(o.overwriteBefore), after: rowZero(o.overwrite) },
        restoration: { policy: 'after DECSCA 1 and the pinned finalizer a new character is unprotected', after: rowZero(o.restore) },
        fullScreen: { policy: 'unprotected cells erased, protected cells and cells outside the region kept, cursor unmoved', erase: contract.fillErase.bytes,
            before: screenSummary(o.fillBefore), after: screenSummary(o.fill) },
        edge: { policy: 'cursor unmoved at the bottom-right cell; the whole-page region reaches the last row and column', erase: contract.edgeErase.bytes,
            before: screenSummary(o.edgeBefore), after: screenSummary(o.edge) },
        attributes: { policy: 'retained protected cells keep their bold-only stored tuple and protection (required); background-color erase is observed, not required',
            before: rowZero(o.attrBefore), after: rowZero(o.attr), retainedKeepBold: attributes.retainedOk, backgroundColorErase: attributes.backgroundColorErase,
            backgroundColorEraseDetail: attributes.detail },
        history: { policy: 'main-screen control: a selective erase must not remove saved lines',
            before: { ...rowZero(o.historyBefore), scrollback: scrollback(o.historyBefore) }, after: { ...rowZero(o.history), scrollback: scrollback(o.history) },
            seeded: retainedHistory.seeded, linesKept: retainedHistory.kept, linesAdded: retainedHistory.grew } };
}

/** Group the read screens of one case by role. */
export function selectiveEraseObservation(contract, screens) {
    if (!Array.isArray(screens) || screens.length !== contract.roles.length) throw new Error('Missing selective-erase read captures.');
    const observation = Object.fromEntries(contract.roles.map(role => [role, group(screens[contract.roles.indexOf(role)])]));
    const binding = screens.map(screen => screen.colorBinding.reverseVideo);
    observation.reverseVideo = binding.every(value => value === false) ? false : binding.some(value => value === true) ? true : null;
    return observation;
}

/** Independent contract grader entry: mirrors the pinned criteria without the bundle. */
export function gradeSelectiveEraseCase(contract, observation, revision = selectiveEraseContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown selective-erase contracts revision ${revision}.`);
    if (!observation || contract.roles.some(role => !observation[role] || !Array.isArray(observation[role].ink))) {
        throw new Error(`Missing ${contract.caseId} read captures.`);
    }
    return contract.grade(observation);
}

export { planFeeds as selectiveEraseFeeds, planReads as selectiveEraseReads } from './graded-plan.mjs';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateSelectiveEraseFeeds(contract, feeds) {
    validateExactFeeds(contract, feeds, 'contract');
}

/** The seed, fill and oracle helpers, exposed so the synthetic fixtures can build an independent oracle. */
export const selectiveEraseFixture = Object.freeze({ seedCursor, fillCursor, edgeCursor, historyLines, fillText, fillCells, fillProtected, fillRect, wholePage,
    pinnedPlacements, penCells, penCursor, overwriteCells, overwriteCursor, restoreCells, restoreCursor, middleRow, lastRow, lastCol });
