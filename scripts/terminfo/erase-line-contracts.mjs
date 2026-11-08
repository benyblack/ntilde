// Versioned probe contracts for the erase-in-line qualification batch:
// erase.line.right (EL 0), erase.line.left (EL 1) and erase.line.all (EL 2).
//
// Pinned terminfo.dev@4.0.0 definitions (bundle SHA-256
// aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6, probeHash 16a690f34c0e):
//   eraseRowResult(ctx, "ABCDE\x1B[3G", "\x1B[K",  [A,B,blank,blank,blank], false, 2)   erase.line.right
//   eraseRowResult(ctx, "ABCDE\x1B[3G", "\x1B[1K", [blank,blank,blank,D,E], false, 2)   erase.line.left
//   eraseRowResult(ctx, "ABCDE\r\nKEEP!\x1B[1;3H", "\x1B[2K", [blank x5], true)         erase.line.all
// The termless path feeds the setup, reads row 0 (and row 1 for the all case) and, for right/left, the cursor;
// it feeds the erase sequence and reads row 0 (and row 1) again. It is supported iff the five row-0 cells match
// (blank is "" or " "), row 1 still reads KEEP! (all case), and the setup cursor was (2,0); a missing fixture is
// inconclusive. It never reads the cursor after the erase, retained-cell attributes, other rows or columns.
//
// SPELLINGS ARE DISTINCT. The pinned right case erases with the OMITTED parameter (ESC [ K). The pinned terminal
// pixel path for the same case (captureEraseFixture) uses the EXPLICIT 0 spelling (ESC [ 0 K) and, whatever the
// pixels show, returns pass:false / inconclusive "insufficient-evidence" (capture only, "no erase support
// judgment"): it never carries a supported or unsupported verdict. This batch grades ONLY the termless criterion;
// the pixel path is not qualified, and no other spelling is substituted for a pinned one. The other spellings run as
// separate, labeled controls (omitted, explicit 0, explicit 1, explicit 2) that are observations, never grades.
//
// The contract adds, only ever making the verdict stricter than the pinned one: the cursor must stay at its seeded
// cell (EL never moves it); every other cell of the screen stays blank with default storage (unaffected rows and
// columns, including a wide-row control past column 5); retained cells keep their stored text and attributes
// (bold, checked against a seed read); and every erased cell reads blank. Whether erased cells take the current
// background color (BCE) is OBSERVED, not required: no pinned criterion constrains it.
//
// Naming: the pinned ids are exactly `erase.line.right`, `erase.line.left` and `erase.line.all`. Ntilde's own
// ntilde-owned-ascii-cells-v1 suite (scripts/terminfo/cells.mjs) carries identically named experiments with
// DIFFERENT bytes (explicit 0/1/2 spelling, seed ABCDE cursor col 2, no neighbor row); those are Ntilde-owned
// observations, not these fixtures and not benchmark grades. erase.display.*, erase.characters.*, selective
// erasure (DECSEL), ICH/DCH/IL/DL editing and the ED3 saved-lines case are different cases and are not substituted.
import { planFeed, planRead, planFeeds, planReads, validateExactFeeds } from './graded-plan.mjs';
import { sgrAttrsCellAt, sgrAttrsReset, sgrAttrsClear, validateSgrAttrsScreen, sgrAttrsPredicates } from './sgr-attrs-contracts.mjs';
import { expandCellDetails } from './cell-details.mjs';

const { neutral } = sgrAttrsPredicates;

/** Contracts v1 is the first measured semantics of this batch. */
export const eraseLineContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const eraseLineSuiteId = 'ntilde-owned-erase-line-graded-v1';
/** The nonce prefix binding worker output to this suite. */
export const eraseLineNoncePrefix = 'NTILDE_EL_';
/** The measured reset every plan begins with: DECSCNM, DECOM, margins, IRM and SGR off, screen cleared, cursor home. */
export const eraseLineReset = sgrAttrsReset;
/** The attribute suite's screen validation, so attribute runs, stored details and resolved colors must agree. */
export const validateEraseLineScreen = validateSgrAttrsScreen;
const clear = sgrAttrsClear;

/** The pinned note texts, byte for byte. */
export const pinnedGeometryNote = 'Measured geometry cannot fit the erase fixture';
export const pinnedFixtureNote = 'Erase fixture, cursor setup, or cell metadata unavailable';

/** The measured geometry the wide control needs (the pinned probe needs only 6 columns and 1-2 rows). */
const minimum = Object.freeze({ rows: 3, cols: 10 });
/** The seeded cursor cell of every group: row 0 (wide: row 1), column 2. */
const seedColumn = 2;
const seed = 'ABCDE';
const wideSeed = '\x1b[1;1HABOVE-ROW!\x1b[2;1HABCDEFGHIJ\x1b[3;1HBELOW-ROW!\x1b[2;3H';
const wideText = 'ABCDEFGHIJ';
const bceSeed = '\x1b[1mABCDE\x1b[0m\x1b[1;3H\x1b[44m';
/** The reference write: one cell written under the same SGR 44 pen, so its stored background is the contracted pen background. */
const penReference = '\x1b[44mX';
/** The four EL spellings: omitted and explicit 0 are the same mode, spelled differently. */
const spellings = Object.freeze({ omitted: { bytes: '\x1b[K', mode: 0 }, explicit0: { bytes: '\x1b[0K', mode: 0 },
    explicit1: { bytes: '\x1b[1K', mode: 1 }, explicit2: { bytes: '\x1b[2K', mode: 2 } });

/** Whether a column survives an EL mode with the cursor at `seedColumn` (the cursor cell is included in modes 0 and 1). */
const survives = (mode, col) => (mode === 0 ? col < seedColumn : mode === 1 ? col > seedColumn : false);
/** The expected text of one erased row: retained characters at their columns, blank elsewhere. */
const erasedText = (text, mode) => [...text].map((char, col) => (survives(mode, col) ? char : ' ')).join('');

/** One case: the pinned fixture plus its discriminating controls, in a fixed plan. */
const case_ = ({ caseId, spelling, setup, neighbor }) => {
    const { bytes: sequence, mode } = spellings[spelling];
    const others = Object.keys(spellings).filter(name => name !== spelling);
    const groups = [
        { role: 'initial', feeds: [] },
        { role: 'setup', feeds: [clear, setup] },
        { role: 'erased', feeds: [sequence] },
        ...others.map(name => ({ role: name, feeds: [clear, setup, spellings[name].bytes] })),
        { role: 'wideBefore', feeds: [clear, wideSeed] },
        { role: 'wide', feeds: [sequence] },
        { role: 'bceBefore', feeds: [clear, bceSeed] },
        { role: 'bce', feeds: [sequence] },
        { role: 'pen', feeds: [clear, penReference] },
    ];
    const plan = [planFeed(eraseLineReset)];
    for (const group of groups) {
        for (const feed of group.feeds) plan.push(planFeed(feed));
        plan.push(planRead('cells'));
    }
    const pinnedCells = Object.freeze([...erasedText(seed, mode)].map(char => (char === ' ' ? 'blank' : char)));
    const expectedCells = `row 0 ${pinnedCells.join(',')}`
        + `${neighbor ? '; row 1 KEEP!' : ''}${neighbor ? '' : `; cursor before ${seedColumn},0`}`;
    return Object.freeze({
        caseId, probeId: caseId, spelling, sequence, mode, setup, neighbor,
        roles: Object.freeze(groups.map(group => group.role)), others: Object.freeze(others),
        pinnedPath: 'termless', pinnedRead: 2, setupRead: 1,
        pinnedExpected: expectedCells, pinnedCells,
        pinnedCriterion: `row 0 ${spelling === 'omitted' ? 'EL 0 (omitted parameter)' : `EL ${mode}`} erases the contracted cells${neighbor ? ' while row 1 keeps KEEP!' : ''}`
            + `${neighbor ? '' : ' after a setup cursor of 2,0'}`,
        pinnedFeeds: Object.freeze([setup, sequence]),
        plan: Object.freeze(plan),
        terminalFixture: { writes: [setup, sequence], demand: 'erased cells blank, retained cells unchanged', minimum: { rows: 2, cols: 6 },
            note: 'upstream pixel path is capture-only (explicit 0 spelling for the right case) and never a grade' },
        appearanceScope: 'stored cells only: exact erased and retained characters, neutral stored attributes, untouched cursor and unaffected rows/columns; '
            + 'background-color erase is observed, not required; the pinned pixel capture path is capture-only and is not claimed',
        grade: observation => gradeErase(contract_(caseId), observation),
        observed: observation => briefErase(contract_(caseId), observation),
    });
};

const contracts = [];
const contract_ = caseId => contracts.find(item => item.caseId === caseId);

contracts.push(
    case_({ caseId: 'erase.line.right', spelling: 'omitted', setup: 'ABCDE\x1b[3G', neighbor: false }),
    case_({ caseId: 'erase.line.left', spelling: 'explicit1', setup: 'ABCDE\x1b[3G', neighbor: false }),
    case_({ caseId: 'erase.line.all', spelling: 'explicit2', setup: 'ABCDE\r\nKEEP!\x1b[1;3H', neighbor: true }),
);
/** The three pinned cases, in suite order. */
export const eraseLineContracts = Object.freeze(contracts);
/** Contract case ids in suite order. */
export const eraseLineCaseIds = Object.freeze(eraseLineContracts.map(contract => contract.caseId));

/** The pinned rowCells projection: the first five stored characters of a row. */
export const eraseLineCellAt = sgrAttrsCellAt;

/** Whether one stored cell detail is the default tuple (no attribute, default colors, no underline or overline color). */
const defaultDetail = detail => detail.flags === 0x300 && detail.fg === -1 && detail.bg === -1 && detail.foregroundArgb == null
    && detail.backgroundArgb == null && detail.overline === false && detail.underlineStyle === 0
    && detail.underlineColorIndex === -1 && detail.underlineArgb == null;

/** Every cell of the screen that carries text or a non-default stored tuple, in row-major order with stored detail.
 * A cell absent from this list is a blank cell with default storage. */
export function eraseLineInk(screen) {
    validateEraseLineScreen(screen);
    const total = screen.rows * screen.cols;
    const details = expandCellDetails(screen.cellDetails, total, true);
    const ink = [];
    for (let position = 0; position < total; position++) {
        const row = Math.floor(position / screen.cols), col = position % screen.cols;
        const char = (screen.lines[row] ?? '')[col] ?? ' ';
        if (char === ' ' && defaultDetail(details[position])) continue;
        ink.push({ row, col, ...sgrAttrsCellAt(screen, row, col) });
    }
    return ink;
}

/** A group's observation: its ink, cursor and geometry. */
const group = screen => ({ ink: eraseLineInk(screen), cursor: [screen.cursorRow, screen.cursorCol], rows: screen.rows, cols: screen.cols });
const sameCursor = (state, row, col) => state.cursor[0] === row && state.cursor[1] === col;

/** Ink equals exactly these [row, col, char] cells, all with neutral stored attributes. */
function inkIs(state, placements) {
    if (state.ink.length !== placements.length) return false;
    return placements.every(([row, col, char], index) => {
        const cell = state.ink[index];
        return cell.row === row && cell.col === col && cell.char === char && neutral(cell);
    });
}
/** The placements of one text laid on a row from column 0, blanks omitted. */
const place = (row, text) => [...text].flatMap((char, col) => (char === ' ' ? [] : [[row, col, char]]));
const byPosition = (a, b) => (a[0] - b[0]) || (a[1] - b[1]);

/** The expected ink of one pinned-fixture state: row 0 text, plus KEEP! on row 1 for the all case. */
const fixtureInk = (contract, text) => [...place(0, text), ...(contract.neighbor ? place(1, 'KEEP!') : [])].sort(byPosition);
/** The expected ink of one wide-control state: three rows, the middle one erased in the contracted mode. */
const wideInk = text => [...place(0, 'ABOVE-ROW!'), ...place(1, text), ...place(2, 'BELOW-ROW!')].sort(byPosition);

/** The BCE control: retained cells keep their bold-only tuple, every other ink cell is a blank inside the erased range of the
 * whole row (EL 0 erases through the last column, EL 1 up to the cursor, EL 2 the full row). */
function bceState(contract, before, after, pen) {
    const cols = after.cols;
    const retained = [...erasedText(seed, contract.mode)].flatMap((char, col) => (char === ' ' ? [] : [col]));
    const erased = Array.from({ length: cols }, (_, col) => col).filter(col => !survives(contract.mode, col) && (col >= seed.length || !retained.includes(col)));
    const bold = cell => cell.bold === true && !cell.dim && !cell.italic && !cell.inverse && !cell.hidden && !cell.strikethrough
        && !cell.blink && !cell.overline && cell.underlineStyle === 0 && cell.fgIndex === -1 && cell.bgIndex === -1
        && cell.foregroundArgb === null && cell.backgroundArgb === null && cell.underlineColorIndex === -1 && cell.underlineArgb === null;
    const seeded = before.ink.length === 5 && before.ink.every((cell, col) => cell.row === 0 && cell.col === col && cell.char === seed[col] && bold(cell))
        && sameCursor(before, 0, seedColumn);
    const text = after.ink.filter(cell => cell.char !== ' ');
    const blanks = after.ink.filter(cell => cell.char === ' ');
    const retainedOk = text.length === retained.length && text.every((cell, index) => cell.row === 0 && cell.col === retained[index]
        && cell.char === seed[cell.col] && bold(cell));
    const blanksOk = blanks.every(cell => cell.row === 0 && erased.includes(cell.col));
    const painted = erased.map(col => blanks.find(cell => cell.col === col));
    const isPainted = cell => cell !== undefined && (cell.bgIndex >= 0 || cell.backgroundArgb !== null);
    // The pen reference: the stored background of a cell written under the same SGR 44 pen (never the default).
    const penCell = pen.ink.find(cell => cell.char === 'X');
    const penMeasured = penCell !== undefined && (penCell.bgIndex >= 0 || penCell.backgroundArgb !== null);
    // Resolved color and stored representation are compared separately: the same blue may be stored as a palette index or a literal.
    const sameResolved = cell => isPainted(cell) && JSON.stringify(cell.bg) === JSON.stringify(penCell.bg);
    const sameStored = cell => sameResolved(cell) && cell.bgIndex === penCell.bgIndex && cell.backgroundArgb === penCell.backgroundArgb;
    const bceApplied = erased.length === 0 ? null : painted.every(cell => cell === undefined) ? 'erased cells keep the default background'
        : !penMeasured ? 'unmeasured: the reference pen cell was not stored with a background'
            : painted.every(sameStored) ? 'erased cells carry the current background'
                : painted.every(sameResolved) ? 'erased cells carry the current background color, stored differently from the pen cell (palette index versus literal ARGB)'
                    : painted.every(isPainted) && !painted.some(sameResolved) ? 'erased cells carry a different non-default background color than the current pen'
                        : 'mixed';
    const storedOf = cell => (cell === undefined ? null : { bgIndex: cell.bgIndex, backgroundArgb: cell.backgroundArgb, resolved: cell.bg });
    const bceDetail = { pen: penMeasured ? storedOf(penCell) : null, firstErased: storedOf(painted[0]) };
    return { seeded, retainedOk, blanksOk, cursorOk: sameCursor(after, 0, seedColumn), bceApplied, bceDetail };
}

/** Why the pinned row did not match, as an unsupported note; the diagnosis only picks the words, never the verdict. */
function diagnose(contract, state) {
    const before = fixtureInk(contract, seed);
    const same = (ink, expected) => ink.length === expected.length && expected.every(([row, col, char], index) => ink[index].row === row
        && ink[index].col === col && ink[index].char === char);
    if (same(state.ink, before)) return 'The row was unchanged after the erase sequence: erase-in-line was a no-op';
    for (const mode of [0, 1, 2]) {
        if (mode !== contract.mode && same(state.ink, fixtureInk(contract, erasedText(seed, mode)))) {
            return `The row matches EL ${mode} instead of EL ${contract.mode}: the wrong cells were erased`;
        }
    }
    if (contract.neighbor && !state.ink.some(cell => cell.row === 1)) return 'Row 1 lost KEEP!: the erase cleared beyond the cursor row';
    if (state.ink.some(cell => cell.row > (contract.neighbor ? 1 : 0))) return 'Text appeared outside the fixture rows after the erase sequence';
    return 'The erased or retained cells differ from the contracted row';
}

/** The shared control walk, then the exact erase verdict. */
function gradeErase(contract, observation) {
    const { setup, erased, wideBefore, wide, bceBefore, bce, reverseVideo, initial } = observation;
    if (setup.rows < minimum.rows || setup.cols < minimum.cols) {
        return { outcome: 'inconclusive', note: pinnedGeometryNote };
    }
    if (reverseVideo !== false) {
        return { outcome: 'inconclusive', note: 'DECSCNM reverse video was not proven off, so stored attributes cannot be separated from it' };
    }
    if (initial.ink.length !== 0) return { outcome: 'inconclusive', note: 'Blank neutral screen control was not measured (the screen was not empty)' };
    if (!inkIs(setup, fixtureInk(contract, seed)) || !sameCursor(setup, 0, seedColumn)) return { outcome: 'inconclusive', note: pinnedFixtureNote };
    if (!inkIs(wideBefore, wideInk(wideText)) || !sameCursor(wideBefore, 1, seedColumn)) {
        return { outcome: 'inconclusive', note: 'The wide-row control seed was not measured, so unaffected rows and columns cannot be distinguished' };
    }
    const bceSeeded = bceState(contract, bceBefore, bce, observation.pen);
    if (!bceSeeded.seeded) return { outcome: 'inconclusive', note: 'The attribute control seed was not measured, so retained attributes cannot be distinguished' };
    if (!inkIs(erased, fixtureInk(contract, erasedText(seed, contract.mode)))) return { outcome: 'unsupported', note: diagnose(contract, erased) };
    if (!sameCursor(erased, 0, seedColumn)) return { outcome: 'unsupported', note: 'The cursor moved during the erase: EL must leave it at its cell' };
    if (!inkIs(wide, wideInk(erasedText(wideText, contract.mode))) || !sameCursor(wide, 1, seedColumn)) {
        return { outcome: 'unsupported', note: 'The wide-row control differs: unaffected rows or columns changed, or the cursor cell was handled wrongly' };
    }
    if (!bceSeeded.retainedOk || !bceSeeded.blanksOk || !bceSeeded.cursorOk) {
        return { outcome: 'unsupported', note: 'Retained cells lost their stored text or attributes, or the cursor moved, in the attribute control' };
    }
    return { outcome: 'supported' };
}

/** A compact stored record of one cell: a `row,col,char` string when its stored tuple is neutral, else the stored fields. */
const brief = cell => (neutral(cell) ? `${cell.row},${cell.col},${cell.char}`
    : { row: cell.row, col: cell.col, char: cell.char, flags: cell.flags, fgIndex: cell.fgIndex, bgIndex: cell.bgIndex,
        foregroundArgb: cell.foregroundArgb, backgroundArgb: cell.backgroundArgb });
/** The stored fields two adjacent non-neutral cells must share to read as one run. */
const runKey = cell => JSON.stringify([cell.row, cell.char, cell.flags, cell.fgIndex, cell.bgIndex, cell.foregroundArgb, cell.backgroundArgb]);
/** The ink list, with each run of identical adjacent non-neutral cells (for example an erased tail painted with a background) collapsed to
 * one `{ row, cols: [first, last], ... }` entry; neutral cells stay one string each. */
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
/** The first `rows` rows of a group's ink as ten-column text, `_` for blank. */
const render = (state, rows) => Array.from({ length: rows }, (_, row) => Array.from({ length: 10 }, (__, col) => {
    const cell = state.ink.find(item => item.row === row && item.col === col);
    return cell === undefined || cell.char === ' ' ? '_' : cell.char;
}).join(''));

/** The observed record: every group's text, cursor and ink, plus the labeled spelling and background observations. */
function briefErase(contract, observation) {
    const rows = contract.neighbor ? 2 : 1;
    const state = (item, count = rows) => ({ text: render(item, count), cursor: item.cursor, ink: briefInk(item.ink) });
    const mode = name => (name === 'erased' ? contract.mode : spellings[name].mode);
    const expectedFor = name => inkIs(observation[name], fixtureInk(contract, erasedText(seed, mode(name)))) && sameCursor(observation[name], 0, seedColumn);
    const spellingRecord = name => ({ sequence: spellings[name].bytes, mode: spellings[name].mode, ...state(observation[name]),
        matchesEcma48: expectedFor(name), sameAsPinned: JSON.stringify(observation[name].ink) === JSON.stringify(observation.erased.ink) });
    const bceSeeded = bceState(contract, observation.bceBefore, observation.bce, observation.pen);
    return { geometry: { rows: observation.setup.rows, cols: observation.setup.cols }, reverseVideo: observation.reverseVideo,
        pinnedSpelling: contract.spelling, pinnedSequence: contract.sequence,
        initial: { ink: observation.initial.ink.length, cursor: observation.initial.cursor },
        setup: state(observation.setup), erased: state(observation.erased),
        spellingControls: { policy: 'observed, not graded: separate spellings of the same row, kept distinct from the pinned spelling',
            ...Object.fromEntries(contract.others.map(name => [name, spellingRecord(name)])) },
        wideRow: { policy: 'unaffected rows and columns: ten-column middle row between two full rows', before: state(observation.wideBefore, 3),
            after: state(observation.wide, 3) },
        attributes: { policy: 'retained cells keep their bold-only stored tuple (required); background-color erase is observed, not required',
            before: state(observation.bceBefore), after: state(observation.bce), penReference: state(observation.pen), retainedKeepBold: bceSeeded.retainedOk,
            backgroundColorErase: bceSeeded.bceApplied, backgroundColorEraseDetail: bceSeeded.bceDetail } };
}

/** Group the read screens of one case by role. */
export function eraseLineObservation(contract, screens) {
    if (!Array.isArray(screens) || screens.length !== contract.roles.length) throw new Error('Missing erase-in-line read captures.');
    const read = role => group(screens[contract.roles.indexOf(role)]);
    const observation = Object.fromEntries(contract.roles.map(role => [role, read(role)]));
    const binding = screens.map(screen => screen.colorBinding.reverseVideo);
    observation.reverseVideo = binding.every(value => value === false) ? false : binding.some(value => value === true) ? true : null;
    return observation;
}

/** Independent contract grader entry: mirrors the pinned criteria without the bundle. */
export function gradeEraseLineCase(contract, observation, revision = eraseLineContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown erase-in-line contracts revision ${revision}.`);
    if (!observation || contract.roles.some(role => !observation[role] || !Array.isArray(observation[role].ink))) {
        throw new Error(`Missing ${contract.caseId} read captures.`);
    }
    return contract.grade(observation);
}

export { planFeeds as eraseLineFeeds, planReads as eraseLineReads } from './graded-plan.mjs';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateEraseLineFeeds(contract, feeds) {
    validateExactFeeds(contract, feeds, 'contract');
}
