// Versioned probe contracts for the erase-with-attributes / erase-with-margins qualification batch:
// erase.el-with-attrs (EL with a measured non-default background) and erase.ed-scroll-region (ED while a scroll region is configured).
//
// Pinned terminfo.dev@4.0.0 definitions (bundle SHA-256
// aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6, probeHash 16a690f34c0e). Both termless probes feed a fixed
// string, read stored cells, feed ONE erase with the OMITTED parameter spelling and read again; both end in a `finally` feed:
//   erase.el-with-attrs      feed ESC[42m ESC[1;1H XXXXX ESC[1;1H; before = getCell(0,0), which must be "X" with a non-null background (else
//                            inconclusive, "Colored X setup was not measured"); feed ESC[K; after = getCell(0,0); the verdict is decided only
//                            when the char is a string and the background is non-null (else inconclusive, "Erased cell background metadata
//                            unavailable"): supported iff the char is blank AND after.bg r/g/b equal before.bg r/g/b; finally feed ESC[0m.
//                            It reads one cell twice. It never reads the cursor, any other cell, the storage representation or any mode.
//   erase.ed-scroll-region   needs a measured 10x10 screen; feed ESC[1;1H KEEP! ESC[3;1H ERASE ESC[3;10r ESC[3;1H; before = the first five
//                            cells of rows 0 and 2 plus the cursor, which must read KEEP!, ERASE and (x 0, y 2) (else inconclusive, "Erase
//                            region seed or cursor setup failed"); feed ESC[J; after = the first five cells of rows 0 and 2; the verdict is
//                            decided only when row 0 still reads KEEP! (else inconclusive, "Preceding row or cell metadata was not preserved"):
//                            supported iff all five target cells are blank; finally feed ESC[r. It never reads the cursor after the erase,
//                            the margins, a cell past column 4, any mode, or whether anything below or beside the target changed.
//
// The pinned terminal pixel/query paths of both cases (the second function of each registration) seed their own bytes, ask for a cursor
// position report and return unmeasuredCellResult (pass:false, inconclusive): they never carry a verdict, so there is no pixel criterion to
// qualify. This batch grades ONLY the termless criteria; stored-state qualification does not establish rendered appearance, and the
// derivation pins both registrations verbatim so a future upstream change that gave a terminal path a verdict would be refused.
//
// SPELLINGS ARE DISTINCT. Both pinned fixtures use the omitted parameter (ESC[K, ESC[J). The explicit-0 spelling runs as a separate labeled
// observation, never a grade. erase.line.* (EL 0/1/2), erase.screen.* (ED 0/1/2), erase.character (ECH) and CSI 3J are different cases and are
// not substituted or re-graded here.
//
// STORED COLOR IS NOT RESOLVED COLOR. The pinned criterion compares RESOLVED background RGB (the cell's exposed {r,g,b}), never the stored
// representation. An erased blank may store its background as a literal ARGB while the X it replaced stored palette index 2; that is the same
// resolved color and satisfies the criterion. The contract therefore requires resolved-RGB equality for cell (0,0) and only OBSERVES the stored
// representation (index versus literal), whether the rest of the erased row carries the same color, and whether the erased background follows
// the pen active at erase time (the pinned fixture leaves the same pen active, so it cannot tell a current-pen fill from a preserved cell
// background; two labeled controls observe that). No requirement that an erased cell keep any other previous style is invented.
//
// SCROLL REGIONS ARE NOT ASSUMED TO CLIP ED. The pinned criterion only requires the preceding row to survive and the target cells to be blank
// with ED 0 issued at the region's top row. The contract adds, only ever making the verdict stricter than the pinned one: the whole screen is
// compared (ink is exactly KEEP!), the cursor stays at its cell, every stored mode flag and the margins stay as seeded (2..9), no saved line
// is added, and the pinned `ESC[r` restores the margins to the full screen. A full-screen marker fill with the same margins and cursor records
// where ED stopped (inside the region, below its bottom margin) as a labeled observation: nothing here claims that ED must or must not be
// clipped to the margins.
import { planFeed, planRead, validateExactFeeds } from './graded-plan.mjs';
import { briefInk } from './erase-line-contracts.mjs';
import { sgrAttrsReset, sgrAttrsClear, sgrAttrsPredicates } from './sgr-attrs-contracts.mjs';
import { eraseDisplayInk, eraseDisplayCellAt, eraseDisplayScrollback, validateEraseDisplayScreen } from './erase-display-contracts.mjs';

const { neutral } = sgrAttrsPredicates;

/** Contracts v1 is the first measured semantics of this batch. */
export const eraseAttrsRegionContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const eraseAttrsRegionSuiteId = 'ntilde-owned-erase-attrs-region-graded-v1';
/** The nonce prefix binding worker output to this suite. */
export const eraseAttrsRegionNoncePrefix = 'NTILDE_EA_';
/** The measured reset every plan begins with: DECSCNM, DECOM, margins, IRM and SGR off, screen cleared, cursor home. */
export const eraseAttrsRegionReset = sgrAttrsReset;
const clear = sgrAttrsClear;

/** The pinned note texts, byte for byte. */
export const pinnedElSetupNote = 'Colored X setup was not measured';
export const pinnedElUnavailableNote = 'Erased cell background metadata unavailable';
export const pinnedEdGeometryNote = 'Need a measured 10x10 fixture';
export const pinnedEdSetupNote = 'Erase region seed or cursor setup failed';
export const pinnedEdPreservedNote = 'Preceding row or cell metadata was not preserved';
/** The contract's own refusal: the controls are pinned to the owned 40x140 screen. */
export const contractGeometryNote = 'The measured geometry is not the contracted 40x140 screen, so the controls do not apply';

/** The measured geometry every contracted plan assumes. */
export const eraseAttrsRegionGeometry = Object.freeze({ rows: 40, cols: 140 });
const { rows: screenRows, cols: screenCols } = eraseAttrsRegionGeometry;
const lastRow = screenRows - 1;
const at = (row, col) => `\x1b[${row + 1};${col + 1}H`;

// ---- pinned bytes -------------------------------------------------------------------------------------------------
/** erase.el-with-attrs: setup, erase and the pinned `finally` feed. */
export const elSetup = '\x1B[42m\x1B[1;1HXXXXX\x1B[1;1H';
export const elErase = '\x1B[K';
export const elFinally = '\x1B[0m';
/** erase.ed-scroll-region: setup, erase and the pinned `finally` feed (CSI 3;10 r is the margin configuration). */
export const edSetup = '\x1B[1;1HKEEP!\x1B[3;1HERASE\x1B[3;10r\x1B[3;1H';
export const edErase = '\x1B[J';
export const edFinally = '\x1B[r';
/** The margins the pinned setup configures (zero-based, inclusive) and the cursor it leaves. */
const edMargins = Object.freeze([2, 9]);
const edCursor = Object.freeze([2, 0]);
const elCursor = Object.freeze([0, 0]);

// ---- seeds --------------------------------------------------------------------------------------------------------
const place = (row, text) => [...text].map((char, col) => [row, col, char]);
const byPosition = (a, b) => (a[0] - b[0]) || (a[1] - b[1]);
const elPlacements = Object.freeze(place(0, 'XXXXX'));
const edPlacements = Object.freeze([...place(0, 'KEEP!'), ...place(2, 'ERASE')]);
const edKept = Object.freeze(place(0, 'KEEP!'));
/** A distinct ten-character marker on each row plus a marker in the last column. */
const fillText = row => `ROW${String(row).padStart(2, '0')}-ABCD`;
const tailMarker = '|';
const fillRows = rows => rows.map(row => at(row, 0) + fillText(row) + at(row, screenCols - 1) + tailMarker).join('');
const fillPlacementsFor = rows => rows.flatMap(row => [...place(row, fillText(row)), [row, screenCols - 1, tailMarker]]);
const rangeRows = (from, to) => Array.from({ length: to - from + 1 }, (_, index) => from + index);
const belowRows = rangeRows(1, lastRow);
const allRows = rangeRows(0, lastRow);
const elFillSeed = fillRows(belowRows);
const elFillPlacements = Object.freeze(fillPlacementsFor(belowRows));
const edFillSeed = fillRows(allRows) + '\x1b[3;10r\x1b[3;1H';
const edFillPlacements = Object.freeze(fillPlacementsFor(allRows));
/** The pen controls: the X are written under SGR 42, then the pen changes (or resets) before the erase. */
const penChangeSetup = '\x1B[42m\x1B[1;1HXXXXX\x1B[44m\x1B[1;1H';
const penResetSetup = '\x1B[42m\x1B[1;1HXXXXX\x1B[0m\x1B[1;1H';
/** The restoration witness: after the pinned `finally` feed a written cell must carry neutral storage. */
const witness = 'W';
const margins = '\x1b[r';

// ---- cell projections ---------------------------------------------------------------------------------------------
/** The pinned rowCells projection: the first five stored characters of a row. */
export const eraseAttrsRegionCellAt = eraseDisplayCellAt;
export { eraseDisplayScrollback as eraseAttrsRegionScrollback, validateEraseDisplayScreen as validateEraseAttrsRegionScreen };
const sameCursor = (state, cursor) => state.cursor[0] === cursor[0] && state.cursor[1] === cursor[1];
const sameRgb = (a, b) => a !== null && b !== null && a !== undefined && b !== undefined && a.r === b.r && a.g === b.g && a.b === b.b;
const rgbOf = argb => ({ r: (argb >> 16) & 255, g: (argb >> 8) & 255, b: argb & 255 });

/** A group's observation: its ink, cursor, geometry, buffer state, history and the App's palette green (index 2). */
const group = screen => ({ ink: eraseDisplayInk(screen), cursor: [screen.cursorRow, screen.cursorCol], rows: screen.rows, cols: screen.cols,
    cursorVisible: screen.cursorVisible, buffer: screen.bufferState, history: screen.history.lines,
    paletteGreen: rgbOf(screen.colorBinding.ansiPaletteArgb[2]) });

/** Ink equals exactly these [row, col, char] cells, all with neutral stored attributes. */
function inkIs(state, placements) {
    const expected = [...placements].sort(byPosition);
    if (state.ink.length !== expected.length) return false;
    return expected.every(([row, col, char], index) => {
        const cell = state.ink[index];
        return cell.row === row && cell.col === col && cell.char === char && neutral(cell);
    });
}
/** Whether the stored cursor visibility and every buffer-state flag, the margins and the saved-line count are unchanged between two reads. */
const modesUnchanged = (before, after) => before.cursorVisible === after.cursorVisible && JSON.stringify(before.buffer) === JSON.stringify(after.buffer);
const marginsAre = (state, [top, bottom]) => state.buffer.scrollTop === top && state.buffer.scrollBottom === bottom;
/** Whether the stored margins span the whole screen. */
const fullScreenMargins = state => marginsAre(state, [0, lastRow]);

// ---- erase.el-with-attrs ----------------------------------------------------------------------------------------
/** One stored cell of the colored seed: text X, palette index 2 as the background, no attribute, default foreground. */
const greenX = cell => cell.char === 'X' && !cell.bold && !cell.dim && !cell.italic && !cell.inverse && !cell.hidden && !cell.strikethrough
    && !cell.blink && !cell.overline && cell.underlineStyle === 0 && cell.fgIndex === -1 && cell.foregroundArgb === null
    && cell.fgSource === 'default' && cell.bgSource === 'palette' && cell.bgIndex === 2 && cell.backgroundArgb === null && cell.bg !== null
    && cell.underlineColorIndex === -1 && cell.underlineArgb === null;
/** The seeded colored X: five green-background X in row 0 columns 0-4, every other cell blank with default storage, all one resolved color. */
function seededGreen(state) {
    if (state.ink.length !== 5) return false;
    const first = state.ink[0];
    return state.ink.every((cell, index) => cell.row === 0 && cell.col === index && greenX(cell) && sameRgb(cell.bg, first.bg))
        && sameRgb(first.bg, state.paletteGreen);
}
/** Whether a state carries only blanks (any storage) in row 0 and nothing at all elsewhere. */
const rowZeroBlanksOnly = state => state.ink.every(cell => cell.row === 0 && cell.char === ' ');
const painted = cell => cell !== undefined && cell.bg !== null;
/** The stored background of one cell for the report. */
const storedOf = cell => (cell === undefined ? null : { bgIndex: cell.bgIndex, backgroundArgb: cell.backgroundArgb, resolved: cell.bg, source: cell.bgSource });
const cellAt0 = (state, col = 0) => state.ink.find(cell => cell.row === 0 && cell.col === col);

/** The erased row's background record: every cell of the row from the cursor, compared with the seeded X color. */
function elBackground(setup, state) {
    const before = setup.ink[0];
    const row = Array.from({ length: screenCols }, (_, col) => cellAt0(state, col));
    const first = row[0];
    const resolvedMatches = painted(first) && painted(before) && sameRgb(first.bg, before.bg);
    return { before: storedOf(before), after: storedOf(first),
        resolvedMatchesBefore: resolvedMatches,
        // Resolved color and stored representation are compared separately: the same color may be stored as a palette index or a literal.
        storedRepresentationMatches: resolvedMatches && first.bgIndex === before.bgIndex && first.backgroundArgb === before.backgroundArgb && first.bgSource === before.bgSource,
        erasedCells: row.length, paintedCells: row.filter(painted).length,
        sameResolvedAsBefore: row.filter(cell => painted(cell) && painted(before) && sameRgb(cell.bg, before.bg)).length,
        lastErased: storedOf(row.at(-1)) };
}

/** A diagnosis of why the erased cell did not read as the pinned criterion requires; it only picks the words, never the verdict. */
function diagnoseEl(setup, erased) {
    if (JSON.stringify(erased.ink) === JSON.stringify(setup.ink)) return 'The screen was unchanged after the erase sequence: erase-in-line was a no-op';
    const first = cellAt0(erased, 0);
    if (first !== undefined && first.char !== ' ') return 'The cell at the cursor still carries text after the erase';
    if (!painted(first)) return 'The erased cell reads blank but its resolved background is the default: the measured background was not carried onto the erased cell';
    return 'The erased cell reads blank but its resolved background differs from the measured one';
}

/** The first failing check's outcome, or null when every check passes. Each check returns an outcome object or null. */
const firstOutcome = checks => {
    for (const check of checks) {
        const outcome = check();
        if (outcome !== null) return outcome;
    }
    return null;
};
const inconclusive = note => ({ outcome: 'inconclusive', note });
const unsupported = note => ({ outcome: 'unsupported', note });
/** An unrelated-state drift between two reads of one erase, named for the report. */
const driftNote = 'A stored mode, margin, scrollback count or the cursor visibility changed during the erase';
const withoutRowZero = state => ({ ink: state.ink.filter(cell => cell.row !== 0) });
const onlyRowZero = state => ({ ...state, ink: state.ink.filter(cell => cell.row === 0) });

/** Whether the pinned finally feed left a written cell with neutral storage. */
const penRestored = state => {
    const cell = cellAt0(state, 0);
    return cell !== undefined && cell.char === witness && neutral(cell);
};

/** The preconditions of the pinned criterion: every one failing makes the grade inconclusive, never unsupported. */
function elPreconditions({ initial, setup, restored, fillBefore, reverseVideo }) {
    return [
        () => (setup.rows === screenRows && setup.cols === screenCols ? null : inconclusive(contractGeometryNote)),
        () => (reverseVideo === false ? null : inconclusive('DECSCNM reverse video was not proven off, so stored attributes cannot be separated from it')),
        () => (initial.ink.length === 0 ? null : inconclusive('Blank neutral screen control was not measured (the screen was not empty)')),
        () => (seededGreen(setup) && sameCursor(setup, elCursor) ? null : inconclusive(pinnedElSetupNote)),
        () => (inkIs(withoutRowZero(fillBefore), elFillPlacements) && seededGreen(onlyRowZero(fillBefore)) && sameCursor(fillBefore, elCursor) ? null
            : inconclusive('The full-screen control seed was not measured, so unaffected rows cannot be distinguished from an accidental whole-screen clear')),
        () => (penRestored(restored) ? null : inconclusive('The pinned finally feed did not restore the pen: a written cell still carried stored attributes')),
    ];
}

/** The erase verdict: the pinned criterion on cell (0,0), then the strictly finer controls. */
function elVerdict({ setup, erased, fillBefore, fill }) {
    const first = cellAt0(erased, 0);
    return [
        () => (rowZeroBlanksOnly(erased) && painted(first) && sameRgb(first.bg, setup.ink[0].bg) ? null : unsupported(diagnoseEl(setup, erased))),
        () => (sameCursor(erased, elCursor) ? null : unsupported('The cursor moved during the erase: EL must leave it at its cell')),
        () => (modesUnchanged(setup, erased) ? null : unsupported(driftNote)),
        () => (rowZeroBlanksOnly(onlyRowZero(fill)) && inkIs(withoutRowZero(fill), elFillPlacements) && sameCursor(fill, elCursor) && modesUnchanged(fillBefore, fill) ? null
            : unsupported('The full-screen control differs: rows other than the erased line changed (an accidental whole-screen clear), or the cursor or a mode moved')),
    ];
}

function gradeEl(observation) {
    return firstOutcome([...elPreconditions(observation), ...elVerdict(observation)]) ?? { outcome: 'supported' };
}

/** The observed record of erase.el-with-attrs: every group's text, cursor and background, plus the labeled controls. */
function briefEl(observation) {
    const rowText = state => Array.from({ length: 10 }, (_, col) => { const cell = cellAt0(state, col); return cell === undefined || cell.char === ' ' ? '_' : cell.char; }).join('');
    const pinnedState = item => ({ row0: rowText(item), cursor: item.cursor, ink: briefInk(item.ink) });
    const { setup, erased } = observation;
    const penRecord = (before, after) => ({ seeded: seededGreen(before) && sameCursor(before, elCursor), before: pinnedState(before), after: pinnedState(after),
        background: elBackground(before, after) });
    return { geometry: { rows: setup.rows, cols: setup.cols }, reverseVideo: observation.reverseVideo, pinnedSequence: elErase,
        initial: { ink: observation.initial.ink.length, cursor: observation.initial.cursor },
        setup: pinnedState(setup), erased: pinnedState(erased), modesUnchanged: modesUnchanged(setup, erased),
        paletteGreen: setup.paletteGreen,
        background: { policy: 'required (the pinned criterion): the erased cell (0,0) reads blank with the same RESOLVED background RGB as the X it replaced; '
            + 'observed, not required: the stored representation, the rest of the erased row and which pen the fill follows',
            ...elBackground(setup, erased) },
        restoration: { policy: 'the pinned finally feed (SGR 0) must leave a written cell with neutral storage', witness: pinnedState(observation.restored),
            witnessNeutral: penRestored(observation.restored) },
        spellingControls: { policy: 'observed, not graded: the explicit-0 spelling of the same erase; the pinned fixture uses only the omitted spelling',
            explicit0: { sequence: '\x1b[0K', ...pinnedState(observation.explicit0), sameAsPinned: JSON.stringify(observation.explicit0.ink) === JSON.stringify(erased.ink) } },
        fullScreen: { policy: 'graded: rows 1-39 carry a distinct marker each and must survive; row 0 holds only blanks', cursor: [...elCursor],
            before: { cursor: observation.fillBefore.cursor, row0: rowText(observation.fillBefore), seeded: inkIs({ ink: observation.fillBefore.ink.filter(cell => cell.row !== 0) }, elFillPlacements) },
            after: { cursor: observation.fill.cursor, row0: rowText(observation.fill), markersKept: inkIs({ ink: observation.fill.ink.filter(cell => cell.row !== 0) }, elFillPlacements) } },
        penControls: { policy: 'observed, not graded: the pinned fixture erases under the pen that wrote the X, so it cannot tell a fill that follows the current pen from a preserved cell '
            + 'background; here the pen changes (SGR 44) or resets (SGR 0) between the write and the erase',
            penChanged: { pen: 'SGR 44', ...penRecord(observation.penBefore, observation.penChanged) },
            penReset: { pen: 'SGR 0', ...penRecord(observation.penResetBefore, observation.penReset) } } };
}

// ---- erase.ed-scroll-region -------------------------------------------------------------------------------------
/** The first ten columns of the given rows of a group's ink as text, `_` for blank. */
const render = (state, rows) => rows.map(row => Array.from({ length: 10 }, (_, col) => {
    const cell = state.ink.find(item => item.row === row && item.col === col);
    return cell === undefined || cell.char === ' ' ? '_' : cell.char;
}).join(''));

/** A diagnosis of why the screen did not read as ED 0; it only picks the words, never the verdict. */
function diagnoseEd(setup, erased) {
    if (JSON.stringify(erased.ink) === JSON.stringify(setup.ink)) return 'The screen was unchanged after the erase sequence: erase-in-display was a no-op';
    if (!erased.ink.some(cell => cell.row === 0 && cell.col === 0)) return 'The preceding row was erased: ED 0 must preserve the rows above the cursor';
    if (erased.ink.some(cell => cell.row === 2)) return 'The target row was not erased: the cells at and after the cursor still carry text';
    return 'The erased or retained cells differ from the contracted screen';
}

/** The preconditions of the pinned criterion: every one failing makes the grade inconclusive, never unsupported. */
function edPreconditions({ initial, setup, restored, reverseVideo }) {
    return [
        () => (setup.rows >= 10 && setup.cols >= 10 ? null : inconclusive(pinnedEdGeometryNote)),
        () => (setup.rows === screenRows && setup.cols === screenCols ? null : inconclusive(contractGeometryNote)),
        () => (reverseVideo === false ? null : inconclusive('DECSCNM reverse video was not proven off, so stored attributes cannot be separated from it')),
        () => (initial.ink.length === 0 && fullScreenMargins(initial) ? null : inconclusive('Blank neutral screen control was not measured (the screen was not empty or margins were set)')),
        () => (inkIs(setup, edPlacements) && sameCursor(setup, edCursor) && marginsAre(setup, edMargins) && setup.buffer.originMode === false ? null : inconclusive(pinnedEdSetupNote)),
        // Restoration: the pinned `ESC[r` returns the margins to the full screen without disturbing the stored text.
        () => (fullScreenMargins(restored) && inkIs(restored, edKept) && restored.buffer.originMode === false ? null
            : inconclusive('The pinned finally feed did not restore the margins to the full screen, or it changed the stored text')),
    ];
}

/** The erase verdict: the pinned criterion (KEEP! kept, target blank), then the strictly finer whole-screen, cursor and mode checks. */
function edVerdict({ setup, erased }) {
    return [
        () => (inkIs(erased, edKept) ? null : unsupported(diagnoseEd(setup, erased))),
        () => (sameCursor(erased, edCursor) ? null : unsupported('The cursor moved during the erase: ED must leave it at its cell')),
        () => (modesUnchanged(setup, erased) ? null : unsupported('A stored mode, the margins, the scrollback count or the cursor visibility changed during the erase')),
    ];
}

function gradeEd(observation) {
    return firstOutcome([...edPreconditions(observation), ...edVerdict(observation)]) ?? { outcome: 'supported' };
}

/** Which rows of the marker fill an ED 0 left in place: the labeled boundary observation, never a grade. */
function boundaryObservation(before, after) {
    const present = (state, row) => state.ink.some(cell => cell.row === row && cell.col === 0 && cell.char === 'R');
    const seeded = inkIs(before, edFillPlacements) && sameCursor(before, edCursor) && marginsAre(before, edMargins);
    const rowsKept = allRows.filter(row => present(after, row));
    const group_ = (from, to) => rangeRows(from, to).every(row => !present(after, row));
    return { seeded, cursor: after.cursor, margins: [after.buffer.scrollTop, after.buffer.scrollBottom],
        rowsKept: rowsKept.length === 0 ? null : { first: rowsKept[0], last: rowsKept.at(-1), count: rowsKept.length },
        keptAboveCursor: rangeRows(0, 1).every(row => present(after, row)),
        erasedInsideRegion: group_(2, 9), erasedBelowBottomMargin: group_(10, lastRow),
        inkAfter: after.ink.length, modesUnchanged: modesUnchanged(before, after) };
}

function briefEd(observation) {
    const pinnedState = item => ({ text: render(item, [0, 2]), cursor: item.cursor, margins: [item.buffer.scrollTop, item.buffer.scrollBottom], ink: briefInk(item.ink) });
    const { setup, erased } = observation;
    const boundary = boundaryObservation(observation.fillBefore, observation.fill);
    return { geometry: { rows: setup.rows, cols: setup.cols }, reverseVideo: observation.reverseVideo, pinnedSequence: edErase,
        initial: { ink: observation.initial.ink.length, cursor: observation.initial.cursor, margins: [observation.initial.buffer.scrollTop, observation.initial.buffer.scrollBottom] },
        setup: { ...pinnedState(setup), originMode: setup.buffer.originMode },
        erased: { ...pinnedState(erased), originMode: erased.buffer.originMode }, modesUnchanged: modesUnchanged(setup, erased),
        preserved: { policy: 'required (the pinned criterion): the preceding row KEEP! survives and the target cells ERASE read blank; graded stricter: the whole screen is exactly KEEP!',
            controlRow: render(erased, [0])[0], targetRow: render(erased, [2])[0] },
        restoration: { policy: 'the pinned finally feed (ESC[r) must restore the margins to the full screen; the cursor after it is observed, not required',
            restored: pinnedState(observation.restored), marginsRestored: fullScreenMargins(observation.restored) },
        spellingControls: { policy: 'observed, not graded: the explicit-0 spelling of the same erase; the pinned fixture uses only the omitted spelling',
            explicit0: { sequence: '\x1b[0J', ...pinnedState(observation.explicit0), sameAsPinned: JSON.stringify(observation.explicit0.ink) === JSON.stringify(erased.ink) } },
        boundary: { policy: 'observed, not graded, no standards claim: where ED 0 stopped with the same margins (3;10) and cursor over a marker on every row; '
            + 'the pinned criterion only reads rows 0 and 2', ...boundary } };
}

// ---- plans ----------------------------------------------------------------------------------------------------
/** Build one case: fixed groups, each a list of feeds followed by one journaled read. */
const case_ = ({ caseId, groups, pinnedFeeds, pinnedFeedPaths, pinnedExpected, pinnedCriterion, terminalFixture, appearanceScope, grade, observed, readsAtFeed }) => {
    const plan = [planFeed(eraseAttrsRegionReset)];
    for (const entry of groups) {
        for (const feed of entry.feeds) plan.push(planFeed(feed));
        plan.push(planRead('cells'));
    }
    const roles = Object.freeze(groups.map(entry => entry.role));
    return Object.freeze({
        caseId, probeId: caseId, pinnedPath: 'termless', roles, pinnedFeeds: Object.freeze(pinnedFeeds), pinnedFeedPaths: Object.freeze(pinnedFeedPaths.map(path => Object.freeze([...path]))), plan: Object.freeze(plan),
        initialRead: roles.indexOf('initial'), setupRead: roles.indexOf('setup'), pinnedRead: roles.indexOf('erased'), restoredRead: roles.indexOf('restored'),
        // The read each pinned getCell/getCursor call is served from, by the number of feeds the pinned probe has issued so far.
        readsAtFeed: Object.freeze(readsAtFeed.map(role => roles.indexOf(role))),
        pinnedExpected, pinnedCriterion, terminalFixture, appearanceScope, grade, observed,
    });
};

const elGroups = [
    { role: 'initial', feeds: [] },
    { role: 'setup', feeds: [clear, elSetup] },
    { role: 'erased', feeds: [elErase] },
    { role: 'restored', feeds: [elFinally, witness] },
    { role: 'explicit0', feeds: [clear, elSetup, '\x1b[0K'] },
    { role: 'fillBefore', feeds: [clear, elFillSeed + elSetup] },
    { role: 'fill', feeds: [elErase] },
    { role: 'penBefore', feeds: [clear, penChangeSetup] },
    { role: 'penChanged', feeds: [elErase] },
    { role: 'penResetBefore', feeds: [clear, penResetSetup] },
    { role: 'penReset', feeds: [elErase] },
];
const edGroups = [
    { role: 'initial', feeds: [] },
    { role: 'setup', feeds: [clear, edSetup] },
    { role: 'erased', feeds: [edErase] },
    { role: 'restored', feeds: [edFinally] },
    { role: 'explicit0', feeds: [clear, edSetup, '\x1b[0J'] },
    { role: 'fillBefore', feeds: [margins, clear, edFillSeed] },
    { role: 'fill', feeds: [edErase] },
];

/** The two pinned cases, in suite order. */
export const eraseAttrsRegionContracts = Object.freeze([
    case_({ caseId: 'erase.el-with-attrs', groups: elGroups, pinnedFeeds: [elSetup, elErase, elFinally],
        // The early return of an unmeasured colored-X setup still runs the pinned `finally` feed.
        pinnedFeedPaths: [[elSetup, elErase, elFinally], [elSetup, elFinally]],
        // fed=0 never reads; the first getCell follows the setup feed, the second the erase feed.
        readsAtFeed: ['initial', 'setup', 'erased', 'restored'],
        pinnedExpected: 'EL erases X while preserving its measured non-default background',
        pinnedCriterion: 'after SGR 42 and XXXXX at the home cell with the cursor back at column 0, ESC[K leaves cell (0,0) blank with the same resolved background RGB the X had; '
            + 'the termless probe reads that one cell twice and compares resolved RGB only',
        terminalFixture: { writes: ['\x1B[1;1H\x1B[2K', '\x1B[42m', 'XXXXX', '\x1B[1;1H', '\x1B[K', '\x1B[0m'], demand: 'cursor position report only (never a verdict)', minimum: { rows: 1, cols: 6 },
            note: 'upstream terminal path is a cursor-query path (unmeasuredCellResult) and never a grade' },
        appearanceScope: 'stored cells and App-resolved background RGB only: the erased cell (0,0) is blank with the resolved background of the X it replaced; the stored '
            + 'representation (palette index versus literal) is observed, not required; rendered appearance is not claimed',
        grade: gradeEl, observed: briefEl }),
    case_({ caseId: 'erase.ed-scroll-region', groups: edGroups, pinnedFeeds: [edSetup, edErase, edFinally],
        // An unmeasured seed returns early but still runs the `finally` feed; a screen below the 10x10 floor feeds nothing.
        pinnedFeedPaths: [[edSetup, edErase, edFinally], [edSetup, edFinally], []],
        readsAtFeed: ['initial', 'setup', 'erased', 'restored'],
        pinnedExpected: 'ED0 erases measured cells below the cursor and preserves the preceding row',
        pinnedCriterion: 'with KEEP! on row 0, ERASE on row 2, margins 3;10 and the cursor at row 2 column 0, ESC[J leaves row 0 reading KEEP! and the first five cells of row 2 blank; '
            + 'the termless probe reads rows 0 and 2 (first five cells) and the cursor before the erase only',
        terminalFixture: { writes: ['\x1B[2J\x1B[H', 'KEEP_THIS\r\n', 'row1\r\n', '\x1B[3;10r', '\x1B[3;1H', '\x1B[J', '\x1B[r'], demand: 'cursor position report only (never a verdict)', minimum: { rows: 10, cols: 10 },
            note: 'upstream terminal path is a cursor-query path (unmeasuredCellResult) and never a grade' },
        appearanceScope: 'stored cells, cursor, margins and mode flags only: the preceding row is preserved and the cells from the cursor read blank; whether ED is clipped to the margins '
            + 'is not claimed (a full-screen marker fill is observed separately); rendered appearance is not claimed',
        grade: gradeEd, observed: briefEd }),
]);
/** Contract case ids in suite order. */
export const eraseAttrsRegionCaseIds = Object.freeze(eraseAttrsRegionContracts.map(contract => contract.caseId));

/** Group the read screens of one case by role. */
export function eraseAttrsRegionObservation(contract, screens) {
    if (!Array.isArray(screens) || screens.length !== contract.roles.length) throw new Error('Missing erase-attributes read captures.');
    const read = role => group(screens[contract.roles.indexOf(role)]);
    const observation = Object.fromEntries(contract.roles.map(role => [role, read(role)]));
    const binding = screens.map(screen => screen.colorBinding.reverseVideo);
    observation.reverseVideo = binding.every(value => value === false) ? false : binding.some(value => value === true) ? true : null;
    return observation;
}

/** Independent contract grader entry: mirrors the pinned criteria without the bundle. */
export function gradeEraseAttrsRegionCase(contract, observation, revision = eraseAttrsRegionContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown erase-attributes contracts revision ${revision}.`);
    if (!observation || contract.roles.some(role => !observation[role] || !Array.isArray(observation[role].ink))) {
        throw new Error(`Missing ${contract.caseId} read captures.`);
    }
    return contract.grade(observation);
}

export { planFeeds as eraseAttrsRegionFeeds, planReads as eraseAttrsRegionReads } from './graded-plan.mjs';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateEraseAttrsRegionFeeds(contract, feeds) {
    validateExactFeeds(contract, feeds, 'contract');
}

/** The seeds and fill helpers, exposed so the synthetic fixtures can build an independent oracle. */
export const eraseAttrsRegionFixture = Object.freeze({ elPlacements, edPlacements, edKept, edMargins, edCursor, elCursor, elFillPlacements, edFillPlacements,
    fillText, witness, lastRow, screenRows, screenCols });
