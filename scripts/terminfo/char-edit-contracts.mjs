// Versioned probe contracts for the character-editing qualification batch:
// editing.insert-chars (ICH), editing.delete-chars (DCH) and editing.repeat-char (REP).
//
// Pinned terminfo.dev@4.0.0 definitions (bundle SHA-256
// aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6, probeHash 16a690f34c0e). Each termless path
// feeds, reads and gates exactly as follows; the three pinned `term` (pixel/query) paths are different code:
// they write other bytes and end in a cursor-position query whose result is the fixed
// "Cursor response does not measure edited cells or attributes" inconclusive, so they never carry a verdict.
//   editing.insert-chars  feed `ESC[1;1H ESC[2K ABCDEZQH`; read cells 0..7 (must be ABCDEZQH); feed `ESC[1;3H`;
//                         read the cursor (must be 2,0); feed `ESC[1@` (EXPLICIT 1); read cells 0..7;
//                         the prefix control is after[0]=A and after[1]=B; supported iff after[2] is blank and
//                         after[3..7] = CDEZQ.
//   editing.delete-chars  the same seed and cursor; feed `ESC[1P` (EXPLICIT 1); read cells 0..6; same prefix control;
//                         supported iff after[2..6] = DEZQH.
//   editing.repeat-char   feed `ESC[1;1H ESC[2K AX   Z`; read cells 0..5 (must be AX   Z); feed `ESC[1;2H`; read the
//                         cursor (must be 1,0); feed `X`; read cells 0..5 (still AX   Z) and the cursor (must be 2,0);
//                         feed `ESC[3b`; read cells 0..5; flank control after[0]=A and after[5]=Z;
//                         supported iff after[1..4] = XXXX.
// A missing fixture, a changed control or a geometry that cannot fit is inconclusive. The pinned probes never read the
// cursor after the edit, any column the fixture does not name, other rows or any attribute.
//
// SPELLINGS ARE DISTINCT. ICH and DCH grade the EXPLICIT count 1 spelling (`ESC[1@`, `ESC[1P`), REP the explicit count 3
// (`ESC[3b`). The omitted, explicit 0, explicit 2, huge-count and REP-after-barrier spellings run as separate, labeled
// controls that are observations, never grades, and no other spelling is substituted for a pinned one.
//
// REP ADJACENCY. REP repeats the preceding graphic character, and ECMA-48 8.3.103 leaves it undefined after any intervening
// control function. The owned fence ends every write with a DA query, which is such a control function (the App parser
// clears the repeatable character on every control sequence). The pinned termless harness feeds `X` and `ESC[3b` as two
// feeds with reads between them but no bytes, so the byte stream is `... X ESC[3b`. This suite therefore sends `X` and
// `ESC[3b` in ONE fenced write (identical stream bytes, no DA between them), and takes the pinned pre-REP reads
// (cells and cursor after `X`) from a separate group whose bytes end at `X`. The pinned code runs over the recorded
// screens in its own feed order. Whether REP survives a barrier is recorded separately as the labeled `split` control.
//
// The contract adds, only ever making the verdict stricter than the pinned one: the cursor must stay at its seeded cell
// for ICH and DCH and advance to the cell after the last repeat for REP; the whole row is compared, not eight columns
// (ICH moves the entire suffix, DCH pulls it left and exposes blank cells at the right edge, REP never touches the flank
// beyond its count); every other cell of the screen stays blank with default storage (unaffected rows and columns); a wide
// three-row control; a right-edge control (ICH truncates at the screen edge, DCH exposes blanks at it); and an attribute
// control (moved cells keep their stored attributes; the blank cells ICH/DCH create, and the cells REP writes, are OBSERVED
// against a reference cell written under the same pen, not required, because no pinned criterion constrains them).
//
// Naming: the pinned ids are exactly `editing.insert-chars`, `editing.delete-chars` and `editing.repeat-char`. Ntilde's own
// ntilde-owned-cell-editing-v1 suite (scripts/terminfo/editing-cases.mjs) has experiments named
// editing.insert-characters.*, editing.delete-characters.* and editing.repeat.* with DIFFERENT bytes; those are Ntilde-owned
// observations, not these fixtures and not benchmark grades. Line editing (IL/DL), DECIC/DECDC, erase operations, scroll
// regions, Unicode, wide-cell and combining-character editing are different cases and are not substituted: this qualifies
// the ASCII fixture only.
import { planFeed, planRead, planFeeds, planReads, validateExactFeeds } from './graded-plan.mjs';
import { sgrAttrsCellAt, sgrAttrsReset, sgrAttrsClear, validateSgrAttrsScreen, sgrAttrsPredicates } from './sgr-attrs-contracts.mjs';
import { eraseLineInk } from './erase-line-contracts.mjs';

const { neutral } = sgrAttrsPredicates;

/** Contracts v1 is the first measured semantics of this batch. */
export const charEditContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const charEditSuiteId = 'ntilde-owned-char-edit-graded-v1';
/** The nonce prefix binding worker output to this suite. */
export const charEditNoncePrefix = 'NTILDE_CE_';
/** The measured reset every plan begins with: DECSCNM, DECOM, margins, IRM and SGR off, screen cleared, cursor home. */
export const charEditReset = sgrAttrsReset;
/** The attribute suite's screen validation, so attribute runs, stored details and resolved colors must agree. */
export const validateCharEditScreen = validateSgrAttrsScreen;
const clear = sgrAttrsClear;

/** The pinned fixture rejection notes, byte for byte. */
export const pinnedNotes = Object.freeze({
    'editing.insert-chars': { geometry: 'Need eight measured columns for the ICH fixture', seed: 'ICH seed was not measured before the edit',
        cursor: 'ICH cursor setup did not reach column 3', control: 'ICH prefix control changed during the edit' },
    'editing.delete-chars': { geometry: 'Need eight measured columns for the DCH fixture', seed: 'DCH seed was not measured before the edit',
        cursor: 'DCH cursor setup did not reach column 3', control: 'DCH prefix control changed during the edit' },
    'editing.repeat-char': { geometry: 'Need six measured columns for the REP fixture', seed: 'REP flank and blank seed was not measured',
        cursor: 'REP cursor setup did not reach column 2', setup: 'REP X setup was not measured', control: 'REP flank control changed during the edit' },
});
/** The pinned `expected` assertion text of each case, byte for byte. */
const pinnedExpectedText = Object.freeze({
    'editing.insert-chars': 'ICH inserts a blank cell at column 3 and shifts measured text right; prefix stays intact',
    'editing.delete-chars': 'DCH deletes the cell at column 3 and shifts measured text left; prefix stays intact',
    'editing.repeat-char': 'REP repeats the immediately preceding X into three target cells without changing flank controls',
});
/** The geometry the controls need (the pinned probes need 8, 8 and 6 columns and one row). */
export const minimum = Object.freeze({ rows: 3, cols: 12 });
export const pinnedGeometryNote = 'Measured geometry cannot fit the character-editing controls';

const seedFeed = '\x1b[1;1H\x1b[2KABCDEZQH';
const cupFeed = '\x1b[1;3H';
const cursorCol = 2;
/** The wide control: a ten-column middle row between two full rows, cursor at column 2 of the middle row. */
const wideSeed = '\x1b[1;1HABOVE-ROW!\x1b[2;1HABCDEFGHIJ\x1b[3;1HBELOW-ROW!\x1b[2;3H';
/** The right-edge control: three characters ending in the last column, cursor on the first of them. */
const edgeSeed = '\x1b[1;1H\x1b[2K\x1b[999C\x1b[2DWXY\x1b[2D';
/** The attribute control: ABCDEZQH with D and E bold, cursor at column 2, blue background pen active. */
const attrSeed = '\x1b[1;1H\x1b[2KABC\x1b[1mDE\x1b[0mZQH\x1b[1;3H\x1b[44m';
/** The reference write: one cell written under the same SGR 44 pen. */
const penReference = '\x1b[44mX';
const repSeedFeed = '\x1b[1;1H\x1b[2KAX   Z';
const repCupFeed = '\x1b[1;2H';
const repWideSeed = '\x1b[1;1HABOVE-ROW!\x1b[2;1HAX   ZQRST\x1b[3;1HBELOW-ROW!\x1b[2;2H';
const repAttrSeed = '\x1b[1;1H\x1b[2KAX   Z\x1b[1;2H\x1b[1m\x1b[44m';
const repPenReference = '\x1b[1m\x1b[44mX';

const blank = ' ';
const repeat = (count, char = blank) => Array.from({ length: count }, () => char);
/** The expected row after ICH/DCH, computed independently of the App: `cells` is the full row, `col` the cursor column. */
function applyEdit(op, cells, col, count, fill = blank) {
    const width = cells.length;
    if (op === 'ich') return [...cells.slice(0, col), ...repeat(Math.min(count, width - col), fill), ...cells.slice(col)].slice(0, width);
    return [...cells.slice(0, col), ...cells.slice(col + count), ...repeat(Math.min(count, width - col), fill)].slice(0, width);
}
/** Spell a row of `text` starting at `offset` as a width-wide cell array. */
const rowCells = (width, text, offset = 0) => Array.from({ length: width }, (_, col) => (col >= offset && col < offset + text.length ? text[col - offset] : blank));
/** Placements [row, col, char] of the non-blank cells of one row. */
const placements = (row, cells) => cells.flatMap((char, col) => (char === blank ? [] : [[row, col, char]]));
const byPosition = (a, b) => (a[0] - b[0]) || (a[1] - b[1]);

/** The spellings of the two shifting operations: `count` is the count the ECMA-48 definition assigns the spelling. */
const shiftSpellings = (letter, pinned) => Object.freeze({ pinned: { bytes: `\x1b[${pinned}${letter}`, count: pinned },
    omitted: { bytes: `\x1b[${letter}`, count: 1 }, explicit0: { bytes: `\x1b[0${letter}`, count: 1 },
    explicit2: { bytes: `\x1b[2${letter}`, count: 2 }, explicit999: { bytes: `\x1b[999${letter}`, count: 999 } });
const repSpellings = Object.freeze({ pinned: { bytes: 'X\x1b[3b', count: 3 }, omitted: { bytes: 'X\x1b[b', count: 1 },
    explicit0: { bytes: 'X\x1b[0b', count: 1 }, explicit1: { bytes: 'X\x1b[1b', count: 1 }, explicit5: { bytes: 'X\x1b[5b', count: 5 } });

const contracts = [];
const contract_ = caseId => contracts.find(item => item.caseId === caseId);

/** Build one ICH or DCH case: the pinned fixture plus its discriminating controls, in a fixed plan. */
function shiftCase({ caseId, op, letter, pinnedNotes: notes }) {
    const spellings = shiftSpellings(letter, 1);
    const others = ['omitted', 'explicit0', 'explicit2', 'explicit999'];
    const groups = [
        { role: 'initial', feeds: [] },
        { role: 'seed', feeds: [clear, seedFeed] },
        { role: 'cursor', feeds: [cupFeed] },
        { role: 'edited', feeds: [spellings.pinned.bytes] },
        ...others.map(name => ({ role: name, feeds: [clear, seedFeed, cupFeed, spellings[name].bytes] })),
        { role: 'edgeBefore', feeds: [clear, edgeSeed] },
        { role: 'edge', feeds: [spellings.pinned.bytes] },
        { role: 'wideBefore', feeds: [clear, wideSeed] },
        { role: 'wide', feeds: [spellings.pinned.bytes] },
        { role: 'attrBefore', feeds: [clear, attrSeed] },
        { role: 'attr', feeds: [spellings.pinned.bytes] },
        { role: 'pen', feeds: [clear, penReference] },
    ];
    return finish({ caseId, family: 'shift', op, spellings, others, groups, notes,
        pinnedFeeds: [seedFeed, cupFeed, spellings.pinned.bytes], feedRoles: ['seed', 'cursor', 'edited'],
        pinnedCriterion: `row 0 of ABCDEZQH with the cursor at column 2: ${op === 'ich' ? 'ICH `ESC[1@` inserts one blank at column 2 and shifts the suffix right (cells 3..7 read CDEZQ)'
            : 'DCH `ESC[1P` removes the cell at column 2 and shifts the suffix left (cells 2..6 read DEZQH)'}; prefix AB intact`,
        demand: op === 'ich' ? 'one blank inserted at the cursor, suffix shifted right, truncation at the screen edge'
            : 'cell at the cursor removed, suffix shifted left, blank exposed at the screen edge' });
}

/** Build the REP case. */
function repeatCase({ caseId, pinnedNotes: notes }) {
    const spellings = repSpellings;
    const others = ['omitted', 'explicit0', 'explicit1', 'explicit5'];
    const groups = [
        { role: 'initial', feeds: [] },
        { role: 'seed', feeds: [clear, repSeedFeed] },
        { role: 'target', feeds: [repCupFeed] },
        { role: 'before', feeds: ['X'] },
        { role: 'split', feeds: ['\x1b[3b'] },
        { role: 'repeated', feeds: [clear, repSeedFeed, repCupFeed, spellings.pinned.bytes] },
        ...others.map(name => ({ role: name, feeds: [clear, repSeedFeed, repCupFeed, spellings[name].bytes] })),
        { role: 'wideBefore', feeds: [clear, repWideSeed] },
        { role: 'wide', feeds: [spellings.pinned.bytes] },
        { role: 'attrBefore', feeds: [clear, repAttrSeed] },
        { role: 'attr', feeds: [spellings.pinned.bytes] },
        { role: 'pen', feeds: [clear, repPenReference] },
    ];
    return finish({ caseId, family: 'repeat', op: 'rep', spellings, others, groups, notes,
        pinnedFeeds: [repSeedFeed, repCupFeed, 'X', '\x1b[3b'], feedRoles: ['seed', 'target', 'before', 'repeated'],
        pinnedCriterion: 'row 0 AX   Z with the cursor at column 1: X is written and REP `ESC[3b` repeats it into cells 2..4 (cells 1..4 read XXXX); flank A and Z intact',
        demand: 'preceding X repeated exactly three times, flank cells unchanged, cursor after the last repeat' });
}

/** Common contract assembly: the fixed plan, identity and grader hooks. */
function finish({ caseId, family, op, spellings, others, groups, notes, pinnedFeeds, feedRoles, pinnedCriterion, demand }) {
    const plan = [planFeed(charEditReset)];
    for (const group of groups) {
        for (const feed of group.feeds) plan.push(planFeed(feed));
        plan.push(planRead('cells'));
    }
    const roles = Object.freeze(groups.map(group => group.role));
    return Object.freeze({
        caseId, probeId: caseId, family, op, spellings, notes, others: Object.freeze(others), roles,
        pinnedPath: 'termless', feedRoles: Object.freeze(feedRoles), pinnedFeeds: Object.freeze(pinnedFeeds),
        pinnedReads: Object.freeze(feedRoles.map(role => roles.indexOf(role))),
        pinnedExpected: pinnedExpectedText[caseId], pinnedCriterion,
        plan: Object.freeze(plan),
        terminalFixture: { demand, minimum: { rows: 1, cols: family === 'repeat' ? 6 : 5 },
            note: 'upstream pixel/query path writes different bytes and ends in a cursor-position query that never measures cells: it carries no verdict and is not qualified' },
        appearanceScope: 'stored ASCII cells only: exact edited and retained characters across the whole row, neutral stored attributes for retained and moved text, '
            + 'the cursor and unaffected rows/columns; attributes of newly created blank cells (ICH, DCH) and of repeated cells (REP) are observed against a same-pen reference, not required; '
            + 'no Unicode, wide-cell or combining-character editing and no pixels are claimed',
        grade: observation => gradeCharEdit(contract_(caseId), observation),
        observed: observation => briefCharEdit(contract_(caseId), observation),
    });
}

contracts.push(
    shiftCase({ caseId: 'editing.insert-chars', op: 'ich', letter: '@', pinnedNotes: pinnedNotes['editing.insert-chars'] }),
    shiftCase({ caseId: 'editing.delete-chars', op: 'dch', letter: 'P', pinnedNotes: pinnedNotes['editing.delete-chars'] }),
    repeatCase({ caseId: 'editing.repeat-char', pinnedNotes: pinnedNotes['editing.repeat-char'] }),
);
/** The three pinned cases, in suite order. */
export const charEditContracts = Object.freeze(contracts);
/** Contract case ids in suite order. */
export const charEditCaseIds = Object.freeze(charEditContracts.map(contract => contract.caseId));

/** The pinned getCell projection. */
export const charEditCellAt = sgrAttrsCellAt;

/** A group's observation: its ink, cursor and geometry. */
const group = screen => ({ ink: eraseLineInk(screen), cursor: [screen.cursorRow, screen.cursorCol], rows: screen.rows, cols: screen.cols,
    cursorVisible: screen.cursorVisible });
const sameCursor = (state, row, col) => state.cursor[0] === row && state.cursor[1] === col;

/** Ink equals exactly these [row, col, char] cells, all with neutral stored attributes. */
function inkIs(state, expected) {
    if (state.ink.length !== expected.length) return false;
    return expected.every(([row, col, char], index) => {
        const cell = state.ink[index];
        return cell.row === row && cell.col === col && cell.char === char && neutral(cell);
    });
}
/** Ink positions and characters only (any stored attributes), used to diagnose a mismatch. */
const inkText = (state, expected) => state.ink.length === expected.length
    && expected.every(([row, col, char], index) => state.ink[index].row === row && state.ink[index].col === col && state.ink[index].char === char);

/** The expected ink of the pinned fixture row 0 after an ICH/DCH of `count`. */
const fixtureRow = (width, op, count) => applyEdit(op, rowCells(width, 'ABCDEZQH'), cursorCol, count);
/** The expected ink of the wide control: three rows, the middle one edited at column 2. */
const wideInk = (width, middle) => [...placements(0, rowCells(width, 'ABOVE-ROW!')), ...placements(1, middle),
    ...placements(2, rowCells(width, 'BELOW-ROW!'))].sort(byPosition);
/** The expected ink of the right-edge control after an ICH/DCH of `count` with the cursor on its first character. */
const edgeRow = (width, op, count) => applyEdit(op, rowCells(width, 'WXY', width - 3), width - 3, count);

/** Bold-only stored tuple (the attribute control seeds D and E bold). */
const boldOnly = cell => cell.bold === true && !cell.dim && !cell.italic && !cell.inverse && !cell.hidden && !cell.strikethrough
    && !cell.blink && !cell.overline && cell.underlineStyle === 0 && cell.fgIndex === -1 && cell.bgIndex === -1
    && cell.foregroundArgb === null && cell.backgroundArgb === null && cell.underlineColorIndex === -1 && cell.underlineArgb === null;
/** Whether a cell is stored with some non-default background. */
const painted = cell => cell !== undefined && (cell.bgIndex >= 0 || cell.backgroundArgb !== null);

/** Compare the stored background of newly created cells with a same-pen reference: resolved color and stored representation separately. */
function backgroundObservation(created, penCell, byCol) {
    if (created.length === 0) return { applied: null, detail: { pen: null, first: null } };
    const cells = created.map(col => byCol(col));
    const penMeasured = painted(penCell);
    const sameResolved = cell => painted(cell) && JSON.stringify(cell.bg) === JSON.stringify(penCell.bg);
    const sameStored = cell => sameResolved(cell) && cell.bgIndex === penCell.bgIndex && cell.backgroundArgb === penCell.backgroundArgb;
    const applied = cells.every(cell => cell === undefined) ? 'created cells keep the default background'
        : !penMeasured ? 'unmeasured: the reference pen cell was not stored with a background'
            : cells.every(sameStored) ? 'created cells carry the current background'
                : cells.every(sameResolved) ? 'created cells carry the current background color, stored differently from the pen cell (palette index versus literal ARGB)'
                    : cells.every(painted) && !cells.some(sameResolved) ? 'created cells carry a different non-default background color than the current pen'
                        : 'mixed';
    const storedOf = cell => (cell === undefined ? null : { bgIndex: cell.bgIndex, backgroundArgb: cell.backgroundArgb, resolved: cell.bg });
    return { applied, detail: { pen: penMeasured ? storedOf(penCell) : null, first: storedOf(cells[0]) } };
}

/** The shift attribute control: moved text keeps its stored tuple, blanks appear only where the edit created them. */
function shiftAttributes(contract, before, after, pen) {
    const width = after.cols, op = contract.op;
    const seedCells = [...'ABCDEZQH'].map((char, col) => ({ char, bold: col === 3 || col === 4 }));
    const seeded = before.ink.length === 8 && before.ink.every((cell, col) => cell.row === 0 && cell.col === col && cell.char === seedCells[col].char
        && (seedCells[col].bold ? boldOnly(cell) : neutral(cell))) && sameCursor(before, 0, cursorCol);
    const empty = { char: blank, bold: false };
    const row = applyEdit(op, [...seedCells, ...repeat(width - seedCells.length, empty)], cursorCol, 1, empty);
    const created = op === 'ich' ? [cursorCol] : [width - 1];
    const text = row.flatMap((cell, col) => (cell.char === blank ? [] : [{ col, cell }]));
    const afterText = after.ink.filter(cell => cell.char !== blank);
    const textOk = afterText.length === text.length && afterText.every((cell, index) => cell.row === 0 && cell.col === text[index].col
        && cell.char === text[index].cell.char && (text[index].cell.bold ? boldOnly(cell) : neutral(cell)));
    const blanks = after.ink.filter(cell => cell.char === blank);
    const blanksOk = blanks.every(cell => cell.row === 0 && created.includes(cell.col));
    const penCell = pen.ink.find(cell => cell.char === 'X');
    const background = backgroundObservation(created, penCell, col => blanks.find(cell => cell.col === col));
    return { seeded, textOk, blanksOk, cursorOk: sameCursor(after, 0, cursorCol), background };
}

/** The REP attribute control: flanks stay neutral; the repeated cells' stored tuple is observed against the same-pen reference. */
function repeatAttributes(before, after, pen) {
    const seeded = inkIs(before, [[0, 0, 'A'], [0, 1, 'X'], [0, 5, 'Z']]) && sameCursor(before, 0, 1);
    const penCell = pen.ink.find(cell => cell.char === 'X');
    const cells = after.ink;
    const flanksOk = cells.length === 6 && cells[0].col === 0 && cells[0].char === 'A' && neutral(cells[0]) && cells[5].col === 5 && cells[5].char === 'Z' && neutral(cells[5]);
    const middle = cells.slice(1, 5);
    const textOk = flanksOk && middle.every((cell, index) => cell.row === 0 && cell.col === index + 1 && cell.char === 'X');
    const sameTuple = cell => penCell !== undefined && cell.flags === penCell.flags && cell.bgIndex === penCell.bgIndex && cell.fgIndex === penCell.fgIndex
        && cell.backgroundArgb === penCell.backgroundArgb && cell.foregroundArgb === penCell.foregroundArgb;
    const repeatedPen = !textOk ? null : middle.every(sameTuple) ? 'repeated cells carry the current pen attributes'
        : middle.slice(0, 1).every(sameTuple) ? 'the written X carries the pen but the repeated cells do not' : 'the written X does not carry the pen';
    return { seeded, textOk, cursorOk: sameCursor(after, 0, 5), repeatedPen,
        penStored: penCell === undefined ? null : { flags: penCell.flags, bgIndex: penCell.bgIndex, backgroundArgb: penCell.backgroundArgb } };
}

/** Why the pinned row did not match, as an unsupported note; the diagnosis only picks the words, never the verdict. */
function diagnose(contract, state) {
    const width = state.cols;
    const expected = cells => placements(0, cells);
    if (contract.family === 'repeat') {
        const same = cells => inkText(state, expected(cells));
        if (same(repeatRow(width, 0))) return 'The row was unchanged after REP: nothing was repeated';
        for (const count of [1, 2, 4, 5]) if (same(repeatRow(width, count))) return `REP repeated the character ${count} time${count === 1 ? '' : 's'} instead of 3`;
        if (state.ink.some(cell => cell.row !== 0)) return 'Text appeared outside the fixture row after REP';
        return 'The repeated or flank cells differ from the contracted row';
    }
    const same = cells => inkText(state, expected(cells));
    if (same(rowCells(width, 'ABCDEZQH'))) return `The row was unchanged after ${contract.op.toUpperCase()}: the edit was a no-op`;
    const other = contract.op === 'ich' ? 'dch' : 'ich';
    if (same(fixtureRow(width, other, 1))) return `The row matches ${other.toUpperCase()} instead of ${contract.op.toUpperCase()}: the suffix moved the wrong way`;
    const erased = [...rowCells(width, 'ABCDEZQH')];
    erased[cursorCol] = blank;
    if (same(erased)) return 'The cell at the cursor was blanked in place: overwrite or erase instead of a shift';
    for (const count of [2, 3]) if (same(fixtureRow(width, contract.op, count))) return `The row shifted by ${count} cells instead of 1`;
    if (state.ink.some(cell => cell.row !== 0)) return 'Text appeared outside the fixture row after the edit';
    return 'The edited or retained cells differ from the contracted row';
}

/** The row of the repeat case after the X write and a REP of `count` (0 means the X write alone): REP overwrites, so a count past
 * three reaches the Z flank. */
const repeatRow = (width, count) => rowCells(width, 'AX   Z').map((char, col) => (col >= 1 && col <= 1 + count ? 'X' : char));

/** The shared control walk, then the exact verdict. */
function gradeCharEdit(contract, observation) {
    const { initial, seed, reverseVideo } = observation;
    if (seed.rows < minimum.rows || seed.cols < minimum.cols) return { outcome: 'inconclusive', note: pinnedGeometryNote };
    if (reverseVideo !== false) return { outcome: 'inconclusive', note: 'DECSCNM reverse video was not proven off, so stored attributes cannot be separated from it' };
    if (initial.ink.length !== 0) return { outcome: 'inconclusive', note: 'Blank neutral screen control was not measured (the screen was not empty)' };
    return contract.family === 'repeat' ? gradeRepeat(contract, observation) : gradeShift(contract, observation);
}

function gradeShift(contract, observation) {
    const { seed, cursor, edited, wideBefore, wide, edgeBefore, edge, attrBefore, attr, pen } = observation;
    const width = seed.cols, notes = contract.notes;
    const seedInk = placements(0, rowCells(width, 'ABCDEZQH'));
    if (!inkIs(seed, seedInk) || !sameCursor(seed, 0, 8)) return { outcome: 'inconclusive', note: notes.seed };
    if (!inkIs(cursor, seedInk) || !sameCursor(cursor, 0, cursorCol)) return { outcome: 'inconclusive', note: notes.cursor };
    if (!inkIs(wideBefore, wideInk(width, rowCells(width, 'ABCDEFGHIJ'))) || !sameCursor(wideBefore, 1, cursorCol)) {
        return { outcome: 'inconclusive', note: 'The wide-row control seed was not measured, so unaffected rows and columns cannot be distinguished' };
    }
    const edgeSeedInk = placements(0, rowCells(width, 'WXY', width - 3));
    if (!inkIs(edgeBefore, edgeSeedInk) || !sameCursor(edgeBefore, 0, width - 3)) {
        return { outcome: 'inconclusive', note: 'The right-edge control seed was not measured, so edge behavior cannot be distinguished' };
    }
    const attrs = shiftAttributes(contract, attrBefore, attr, pen);
    if (!attrs.seeded) return { outcome: 'inconclusive', note: 'The attribute control seed was not measured, so moved attributes cannot be distinguished' };
    const prefixKept = edited.ink.length >= 2 && edited.ink[0].row === 0 && edited.ink[0].col === 0 && edited.ink[0].char === 'A'
        && edited.ink[1].row === 0 && edited.ink[1].col === 1 && edited.ink[1].char === 'B';
    const expected = placements(0, fixtureRow(width, contract.op, 1));
    if (!prefixKept) return { outcome: 'unsupported', note: `The prefix AB changed during the edit: ${diagnose(contract, edited)}` };
    if (!inkIs(edited, expected)) return { outcome: 'unsupported', note: diagnose(contract, edited) };
    if (!sameCursor(edited, 0, cursorCol)) return { outcome: 'unsupported', note: `The cursor moved during ${contract.op.toUpperCase()}: it must stay at its cell` };
    if (!inkIs(wide, wideInk(width, applyEdit(contract.op, rowCells(width, 'ABCDEFGHIJ'), cursorCol, 1))) || !sameCursor(wide, 1, cursorCol)) {
        return { outcome: 'unsupported', note: 'The wide-row control differs: unaffected rows or columns changed, or the suffix did not move across the whole row' };
    }
    if (!inkIs(edge, placements(0, edgeRow(width, contract.op, 1))) || !sameCursor(edge, 0, width - 3)) {
        return { outcome: 'unsupported', note: 'The right-edge control differs: the suffix did not truncate or expose blanks correctly at the last column' };
    }
    if (!attrs.textOk || !attrs.blanksOk || !attrs.cursorOk) {
        return { outcome: 'unsupported', note: 'Moved cells lost their stored text or attributes, an unexpected blank appeared, or the cursor moved, in the attribute control' };
    }
    return { outcome: 'supported' };
}

function gradeRepeat(contract, observation) {
    const { seed, target, before, repeated, wideBefore, wide, attrBefore, attr, pen } = observation;
    const width = seed.cols, notes = contract.notes;
    const seedInk = [[0, 0, 'A'], [0, 1, 'X'], [0, 5, 'Z']];
    if (!inkIs(seed, seedInk) || !sameCursor(seed, 0, 6)) return { outcome: 'inconclusive', note: notes.seed };
    if (!inkIs(target, seedInk) || !sameCursor(target, 0, 1)) return { outcome: 'inconclusive', note: notes.cursor };
    if (!inkIs(before, seedInk) || !sameCursor(before, 0, 2)) return { outcome: 'inconclusive', note: notes.setup };
    const wideSeedInk = [...placements(0, rowCells(width, 'ABOVE-ROW!')), ...placements(1, rowCells(width, 'AX   ZQRST')),
        ...placements(2, rowCells(width, 'BELOW-ROW!'))].sort(byPosition);
    if (!inkIs(wideBefore, wideSeedInk) || !sameCursor(wideBefore, 1, 1)) {
        return { outcome: 'inconclusive', note: 'The wide-row control seed was not measured, so unaffected rows and columns cannot be distinguished' };
    }
    const attrs = repeatAttributes(attrBefore, attr, pen);
    if (!attrs.seeded) return { outcome: 'inconclusive', note: 'The attribute control seed was not measured, so flank attributes cannot be distinguished' };
    const flankKept = repeated.ink.length > 0 && repeated.ink[0].col === 0 && repeated.ink[0].char === 'A' && repeated.ink.some(cell => cell.col === 5 && cell.char === 'Z');
    if (!inkIs(repeated, placements(0, repeatRow(width, 3)))) {
        return { outcome: 'unsupported', note: flankKept ? diagnose(contract, repeated) : `A flank cell changed during REP: ${diagnose(contract, repeated)}` };
    }
    if (!sameCursor(repeated, 0, 5)) return { outcome: 'unsupported', note: 'The cursor did not advance to the cell after the last repeat' };
    const wideExpected = [...placements(0, rowCells(width, 'ABOVE-ROW!')), ...placements(1, rowCells(width, 'AXXXXZQRST')),
        ...placements(2, rowCells(width, 'BELOW-ROW!'))].sort(byPosition);
    if (!inkIs(wide, wideExpected) || !sameCursor(wide, 1, 5)) {
        return { outcome: 'unsupported', note: 'The wide-row control differs: unaffected rows or columns changed, or the repeat count was wrong' };
    }
    if (!attrs.textOk || !attrs.cursorOk) {
        return { outcome: 'unsupported', note: 'A flank cell lost its neutral stored attributes, the repeated text was wrong, or the cursor was wrong, in the attribute control' };
    }
    return { outcome: 'supported' };
}

/** A compact stored record of one cell: a `row,col,char` string when its stored tuple is neutral, else the stored fields. */
const brief = cell => (neutral(cell) ? `${cell.row},${cell.col},${cell.char}`
    : { row: cell.row, col: cell.col, char: cell.char, flags: cell.flags, fgIndex: cell.fgIndex, bgIndex: cell.bgIndex,
        foregroundArgb: cell.foregroundArgb, backgroundArgb: cell.backgroundArgb });
/** The cells of one row inside a window of columns as text, `_` for blank. */
const window_ = (state, row, from, to) => Array.from({ length: to - from }, (_, index) => {
    const cell = state.ink.find(item => item.row === row && item.col === from + index);
    return cell === undefined || cell.char === blank ? '_' : cell.char;
}).join('');
/** One group's record: first-twelve-column text of the first `rows` rows, cursor and compact ink. */
const state_ = (item, rows) => ({ text: Array.from({ length: rows }, (_, row) => window_(item, row, 0, 12)), cursor: item.cursor,
    ink: item.ink.map(brief) });

/** The observed record: every group's text, cursor and ink, plus the labeled spelling and attribute observations. */
function briefCharEdit(contract, observation) {
    const width = observation.seed.cols;
    const repeatFamily = contract.family === 'repeat';
    const tail = item => ({ tail: window_(item, 0, width - 6, width), cursor: item.cursor, ink: item.ink.map(brief) });
    const expectedFor = (name, spelling) => (repeatFamily
        ? inkIs(observation[name], placements(0, repeatRow(width, spelling.count))) && sameCursor(observation[name], 0, 2 + spelling.count)
        : inkIs(observation[name], placements(0, fixtureRow(width, contract.op, spelling.count))) && sameCursor(observation[name], 0, cursorCol));
    const pinnedRole = repeatFamily ? 'repeated' : 'edited';
    const spellingRecord = name => ({ sequence: contract.spellings[name].bytes, count: contract.spellings[name].count,
        ...state_(observation[name], 1), matchesEcma48: expectedFor(name, contract.spellings[name]),
        sameAsPinned: JSON.stringify(observation[name].ink) === JSON.stringify(observation[pinnedRole].ink) });
    const attributes = repeatFamily ? repeatAttributes(observation.attrBefore, observation.attr, observation.pen)
        : shiftAttributes(contract, observation.attrBefore, observation.attr, observation.pen);
    const common = { geometry: { rows: observation.seed.rows, cols: width }, reverseVideo: observation.reverseVideo,
        pinnedSequence: contract.spellings.pinned.bytes, initial: { ink: observation.initial.ink.length, cursor: observation.initial.cursor },
        seed: state_(observation.seed, 1), pinned: state_(observation[pinnedRole], 1),
        spellingControls: { policy: 'observed, not graded: separate spellings of the same operation, kept distinct from the pinned spelling',
            ...Object.fromEntries(contract.others.map(name => [name, spellingRecord(name)])) },
        wideRow: { policy: 'unaffected rows and columns: ten-column middle row between two full rows', before: state_(observation.wideBefore, 3),
            after: state_(observation.wide, 3) } };
    if (repeatFamily) {
        return { ...common, setup: { target: state_(observation.target, 1), before: state_(observation.before, 1) },
            splitControl: { policy: 'observed, not graded: REP in a separate write after the fence barrier (ECMA-48 8.3.103 leaves it undefined after a control function)',
                sequence: '\x1b[3b', ...state_(observation.split, 1), repeats: inkText(observation.split, placements(0, repeatRow(width, 3))) },
            attributes: { policy: 'flank cells keep neutral attributes (required); whether repeated cells carry the current pen is observed, not required',
                before: state_(observation.attrBefore, 1), after: state_(observation.attr, 1), penReference: state_(observation.pen, 1),
                flanksNeutral: attributes.textOk, repeatedCells: attributes.repeatedPen, penStored: attributes.penStored } };
    }
    return { ...common, setup: { cursor: state_(observation.cursor, 1) },
        rightEdge: { policy: 'the last three columns hold WXY with the cursor on W: ICH truncates at the edge, DCH exposes a blank at it',
            before: tail(observation.edgeBefore), after: tail(observation.edge) },
        attributes: { policy: 'moved cells keep their stored tuple (D and E bold; required); blank cells created by the edit are observed against a same-pen reference, not required',
            before: state_(observation.attrBefore, 1), after: state_(observation.attr, 1), penReference: state_(observation.pen, 1),
            movedKeepAttributes: attributes.textOk && attributes.blanksOk, createdBlankBackground: attributes.background.applied,
            createdBlankBackgroundDetail: attributes.background.detail } };
}

/** Group the read screens of one case by role. */
export function charEditObservation(contract, screens) {
    if (!Array.isArray(screens) || screens.length !== contract.roles.length) throw new Error('Missing character-editing read captures.');
    const read = role => group(screens[contract.roles.indexOf(role)]);
    const observation = Object.fromEntries(contract.roles.map(role => [role, read(role)]));
    const binding = screens.map(screen => screen.colorBinding.reverseVideo);
    observation.reverseVideo = binding.every(value => value === false) ? false : binding.some(value => value === true) ? true : null;
    return observation;
}

/** Independent contract grader entry: mirrors the pinned criteria without the bundle. */
export function gradeCharEditCase(contract, observation, revision = charEditContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown character-editing contracts revision ${revision}.`);
    if (!observation || contract.roles.some(role => !observation[role] || !Array.isArray(observation[role].ink))) {
        throw new Error(`Missing ${contract.caseId} read captures.`);
    }
    return contract.grade(observation);
}

export { planFeeds as charEditFeeds, planReads as charEditReads } from './graded-plan.mjs';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateCharEditFeeds(contract, feeds) {
    validateExactFeeds(contract, feeds, 'contract');
}
