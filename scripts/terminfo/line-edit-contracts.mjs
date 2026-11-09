// Versioned probe contracts for the line-editing qualification batch:
// editing.insert-lines (IL) and editing.delete-lines (DL).
//
// Pinned terminfo.dev@4.0.0 definitions (bundle SHA-256
// aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6, probeHash 16a690f34c0e). Both termless probes:
//   need cols >= 6 and screenLines >= 4 (else inconclusive);
//   feed four seed rows, each ESC[<row>;1H ESC[2K <text>, with AAAAA, BBBBB, CCCCC, DDDDD on rows 1-4, and read the
//   first five cells of rows 1-4 (`before`); feed ESC[2;1H and read the cursor (`setup`, must be x=0,y=1); feed the edit
//   (IL: ESC[1L, DL: ESC[1M) and read the first five cells of rows 1-4 (IL) or rows 1-3 (DL) (`after`).
//   `controlsValid` is: the row-1 text is still AAAAA. IL is supported iff the controls are valid and after[1] is blank
//   (every cell "" or " "), after[2] is BBBBB and after[3] is CCCCC. DL is supported iff the controls are valid and
//   after[1] is CCCCC and after[2] is DDDDD. A seed that did not read back, or a wrong setup cursor, is inconclusive.
// The pinned probes never read the cursor after the edit, any cell past column 5, any row past the fourth (IL) or third
// (DL), cell attributes, margins, origin mode or history. On a 40-row screen the pinned IL never sees the row that a
// full-height insert discards, and the pinned DL never reads the blank rows that appear at the bottom.
//
// The pinned terminal pixel path of both cases (the second function of each registration) writes ESC[3;5H then the same
// edit, asks for a cursor position report and returns unmeasuredCellResult: pass:false, outcome inconclusive,
// reason insufficient-evidence (or no-response). It never carries a verdict and does not even seed cells, so there is no
// pixel criterion to qualify. This batch grades ONLY the termless criterion; the pixel path is not qualified, and the
// derivation pins its registration verbatim so a future upstream change that gave it a verdict would be refused.
//
// SPELLINGS ARE DISTINCT. The pinned sequences are the explicit-count one: ESC[1L and ESC[1M. The omitted (ESC[L),
// explicit-zero (ESC[0L), explicit-two (ESC[2L) and oversized (ESC[999L) spellings run as separate labeled controls
// that are observations, never grades: a sibling spelling that misbehaves is reported and cannot change another
// case's grade.
//
// The contract adds, only ever making the verdict stricter than the pinned one (full-screen ink comparison): every cell of
// the whole screen is compared with an independent line-editing model, so the discarded or blank rows, retained order,
// unaffected rows and columns, and character-level drift (ICH/DCH-like shifts) are all observed; the cursor must end at
// row 2, column 1 after the pinned edit; and these graded controls must hold: a full-height edit at the second-to-last
// row (the discard boundary for IL and the blank-row boundary for DL, with the top row unaffected), a three-row scroll
// region (DECSTBM 3;5) with the cursor inside it (boundary discard / blank row at the region bottom, rows outside the
// region unchanged), the same region with the cursor above and below it (no change), and the same region with origin
// mode (DECOM) on. Margins, origin mode and insert mode are read from the App's own stored buffer state in every read;
// they are never inferred from cursor replies. A moved cell must keep its stored attributes and colors; newly created
// blank lines must carry no attribute but a background color. Whether those blank lines take the current background
// (BCE) is OBSERVED, not required. History is observed only: IL/DL scrollback accumulation is not part of any pinned
// criterion, and the reads run on the alternate screen, where no scrollback accumulates.
//
// Character scope: the pinned fixtures are ASCII single-width cells. Nothing here qualifies wide, combining or Unicode
// line editing.
//
// Naming: the pinned ids are exactly `editing.insert-lines` and `editing.delete-lines`. Ntilde's own
// ntilde-owned-cell-editing-v1 suite has identically named experiments with different bytes; those are Ntilde-owned
// observations, not these fixtures and not benchmark grades. editing.insert-chars, editing.delete-chars, scrolling
// (SU/SD, SL/SR), erase and rectangle operations are different cases and are not substituted.
import { planFeed, planRead, planFeeds, validateExactFeeds } from './graded-plan.mjs';
import { sgrAttrsReset, sgrAttrsPredicates } from './sgr-attrs-contracts.mjs';
import { eraseLineInk, eraseLineCellAt, validateEraseLineScreen } from './erase-line-contracts.mjs';

const { neutral } = sgrAttrsPredicates;

/** Contracts v1 is the first measured semantics of this batch. */
export const lineEditContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const lineEditSuiteId = 'ntilde-owned-line-editing-graded-v1';
/** The nonce prefix binding worker output to this suite. */
export const lineEditNoncePrefix = 'NTILDE_LE_';
/** The measured reset every plan begins with: DECSCNM, DECOM, margins, IRM and SGR off, screen cleared, cursor home. */
export const lineEditReset = sgrAttrsReset;
/** The attribute suite's screen validation, so attribute runs, stored details and resolved colors must agree. */
export const validateLineEditScreen = validateEraseLineScreen;
/** Between groups: margins, origin mode and SGR off, screen cleared, cursor home. */
const clear = '\x1b[r\x1b[?6l\x1b[0m\x1b[2J\x1b[H';

/** The pinned note texts, byte for byte, per mode. */
export const pinnedNotes = Object.freeze({
    insert: Object.freeze({ geometry: 'Need six measured columns and four measured screen lines for IL',
        seed: 'IL source rows were not measured before the edit', cursor: 'IL cursor setup did not reach row 2 column 1',
        controls: 'IL first-row control changed during the edit',
        expected: 'IL inserts a blank row 2 and shifts measured rows down while row 1 stays intact' }),
    delete: Object.freeze({ geometry: 'Need six measured columns and four measured screen lines for DL',
        seed: 'DL source rows were not measured before the edit', cursor: 'DL cursor setup did not reach row 2 column 1',
        controls: 'DL first-row control changed during the edit',
        expected: 'DL removes row 2 and shifts measured rows up while row 1 stays intact' }),
});

/** The contract's own geometry note (the pinned note names the mode and is carried in `pinnedNotes`). */
export const pinnedGeometryNote = 'Measured geometry cannot fit the line-edit fixture';
/** The measured geometry the controls need (the pinned probes need only 6 columns and 4 rows). */
const minimum = Object.freeze({ rows: 10, cols: 12 });
/** The four pinned seed rows and the byte-exact feeds that write them (one fence write each). */
const seedRows = Object.freeze(['AAAAA', 'BBBBB', 'CCCCC', 'DDDDD']);
const seedFeeds = Object.freeze(seedRows.map((text, row) => `\x1b[${row + 1};1H\x1b[2K${text}`));
const cursorFeed = '\x1b[2;1H';
/** The full-height boundary control: a top anchor and the last two rows, the cursor on the second-to-last row. */
const bottomSeed = '\x1b[1;1HTOP-ROW---\x1b[999;1HLAST-ROW--\x1b[A\rNEXT-LAST-';
const bottomRows = Object.freeze({ top: 'TOP-ROW---', next: 'NEXT-LAST-', last: 'LAST-ROW--' });
/** The scroll-region control: seven ten-column rows, DECSTBM rows 3-5 (zero-based 2-4) and the cursor on row 4, column 3. */
const regionTexts = Object.freeze(['AAAAAAAAAA', 'BBBBBBBBBB', 'CCCCCCCCCC', 'DDDDDDDDDD', 'EEEEEEEEEE', 'FFFFFFFFFF', 'GGGGGGGGGG']);
const regionRows = regionTexts.map((text, row) => `\x1b[${row + 1};1H${text}`).join('');
const regionMargins = '\x1b[3;5r';
const regionTop = 2, regionBottom = 4;
/** The attribute control rows: plain, bold, red-on-blue and plain again, the cursor on row 2 with a blue background pen active. */
const attrSeed = '\x1b[1;1HKEEP-TOP--\x1b[2;1H\x1b[1mBOLD-ROW--\x1b[0m\x1b[3;1H\x1b[31;44mRED-ON-BLU\x1b[0m\x1b[4;1HPLAIN-ROW-\x1b[2;1H\x1b[44m';
const attrTexts = Object.freeze(['KEEP-TOP--', 'BOLD-ROW--', 'RED-ON-BLU', 'PLAIN-ROW-']);
/** The reference write: one cell written under the same SGR 44 pen, so its stored background is the contracted pen background. */
const penReference = '\x1b[44mX';

/** The labeled count spellings of the pinned edit: a count of 1, the omitted and zero defaults, two and oversized. */
const countSpellings = Object.freeze({ omitted: { count: 1, parameter: '' }, explicit0: { count: 1, parameter: '0' },
    explicit2: { count: 2, parameter: '2' }, oversized: { count: Infinity, parameter: '999' } });

/** Every read group, in plan order. */
const roleNames = Object.freeze(['initial', 'before', 'setup', 'edited', ...Object.keys(countSpellings), 'bottomBefore', 'bottom',
    'regionBefore', 'region', 'regionBig', 'regionAbove', 'regionBelow', 'originBefore', 'origin', 'attrBefore', 'attr', 'pen']);
const regionRoles = new Set(['regionBefore', 'region', 'regionBig', 'regionAbove', 'regionBelow', 'originBefore', 'origin']);
const originRoles = new Set(['originBefore', 'origin']);

/** The independent line-editing model. `rows` is indexed by screen row; the margins are zero-based and inclusive. An edit with the
 * cursor row outside the margins changes nothing; a count larger than the rows left in the region is clamped to them. */
export function lineEditModel(rows, mode, at, count, top, bottom, blank = '') {
    const out = [...rows];
    if (at < top || at > bottom) return out;
    const moved = Math.min(count, bottom - at + 1);
    const region = out.slice(at, bottom + 1);
    const next = mode === 'insert'
        ? [...Array.from({ length: moved }, () => blank), ...region.slice(0, region.length - moved)]
        : [...region.slice(moved), ...Array.from({ length: moved }, () => blank)];
    out.splice(at, region.length, ...next);
    return out;
}

/** One case: the pinned fixture plus its discriminating controls, in a fixed plan. */
const case_ = ({ caseId, mode, operator }) => {
    const edit = parameter => `\x1b[${parameter}${operator}`;
    const sequence = edit('1');
    const joined = `${seedFeeds.join('')}${cursorFeed}`;
    const groups = [
        { role: 'initial', feeds: [] },
        { role: 'before', feeds: [...seedFeeds] },
        { role: 'setup', feeds: [cursorFeed] },
        { role: 'edited', feeds: [sequence] },
        ...Object.entries(countSpellings).map(([role, { parameter }]) => ({ role, feeds: [clear + joined + edit(parameter)] })),
        { role: 'bottomBefore', feeds: [clear + bottomSeed] },
        { role: 'bottom', feeds: [sequence] },
        { role: 'regionBefore', feeds: [clear + regionRows + regionMargins + '\x1b[4;3H'] },
        { role: 'region', feeds: [sequence] },
        { role: 'regionBig', feeds: [clear + regionRows + regionMargins + '\x1b[4;3H' + edit('99')] },
        { role: 'regionAbove', feeds: [clear + regionRows + regionMargins + '\x1b[2;3H' + sequence] },
        { role: 'regionBelow', feeds: [clear + regionRows + regionMargins + '\x1b[6;3H' + sequence] },
        { role: 'originBefore', feeds: [clear + regionRows + regionMargins + '\x1b[?6h\x1b[2;3H'] },
        { role: 'origin', feeds: [sequence] },
        { role: 'attrBefore', feeds: [clear + attrSeed] },
        { role: 'attr', feeds: [sequence] },
        { role: 'pen', feeds: [clear + penReference] },
    ];
    const plan = [planFeed(lineEditReset)];
    for (const group of groups) {
        for (const feed of group.feeds) plan.push(planFeed(feed));
        plan.push(planRead('cells'));
    }
    const notes = pinnedNotes[mode];
    const pinnedFeeds = Object.freeze([...seedFeeds, cursorFeed, sequence]);
    return Object.freeze({
        caseId, probeId: caseId, mode, operator, sequence,
        spellings: Object.freeze(Object.fromEntries(Object.entries(countSpellings).map(([name, { count, parameter }]) => [name, { count, bytes: edit(parameter) }]))),
        roles: Object.freeze(groups.map(group => group.role)), others: Object.freeze(Object.keys(countSpellings)),
        pinnedPath: 'termless', beforeRead: 1, setupRead: 2, pinnedRead: 3,
        pinnedRows: mode === 'insert' ? 4 : 3,
        pinnedExpected: notes.expected,
        pinnedCriterion: mode === 'insert'
            ? 'IL (ESC[1L) at row 2, column 1 leaves AAAAA on row 1, a blank row 2, BBBBB on row 3 and CCCCC on row 4, after a setup cursor of 0,1'
            : 'DL (ESC[1M) at row 2, column 1 leaves AAAAA on row 1, CCCCC on row 2 and DDDDD on row 3, after a setup cursor of 0,1',
        pinnedFeeds, plan: Object.freeze(plan),
        terminalFixture: { writes: [...pinnedFeeds], demand: mode === 'insert'
            ? 'a blank line at the cursor row, lines below shifted down' : 'the cursor line removed, lines below shifted up and a blank line at the bottom',
        minimum: { rows: 4, cols: 6 },
        note: 'upstream pixel path (ESC[3;5H then the edit, then a cursor position report) is never a verdict and is not qualified' },
        appearanceScope: 'stored ASCII cells only: exact line contents of the whole screen, the cursor, observed margins, origin and insert mode, '
            + 'moved-cell attributes and neutral new blank lines; background-color fill of new lines is observed, not required; the pinned '
            + 'pixel path is capture-only and not claimed; no Unicode, wide-cell or history qualification',
        grade: observation => gradeLineEdit(contract_(caseId), observation),
        observed: observation => briefLineEdit(contract_(caseId), observation),
    });
};

const contracts = [];
const contract_ = caseId => contracts.find(item => item.caseId === caseId);
contracts.push(
    case_({ caseId: 'editing.insert-lines', mode: 'insert', operator: 'L' }),
    case_({ caseId: 'editing.delete-lines', mode: 'delete', operator: 'M' }),
);
/** The two pinned cases, in suite order. */
export const lineEditContracts = Object.freeze(contracts);
/** Contract case ids in suite order. */
export const lineEditCaseIds = Object.freeze(lineEditContracts.map(contract => contract.caseId));

/** The pinned getCell projection: the stored character of one cell. */
export const lineEditCellAt = eraseLineCellAt;

/** A group's observation: its ink, cursor, geometry and the App's stored buffer state. */
const group = screen => ({ ink: eraseLineInk(screen), cursor: [screen.cursorRow, screen.cursorCol], rows: screen.rows, cols: screen.cols,
    margins: [screen.bufferState.scrollTop, screen.bufferState.scrollBottom], origin: screen.bufferState.originMode,
    insert: screen.bufferState.insertMode, scrollback: screen.bufferState.scrollbackLines, history: screen.history.lines });
const sameCursor = (state, row, col) => state.cursor[0] === row && state.cursor[1] === col;

/** Ink equals exactly these [row, col, char] cells, all with neutral stored attributes. */
function inkIs(state, placements) {
    if (state.ink.length !== placements.length) return false;
    return placements.every(([row, col, char], index) => {
        const cell = state.ink[index];
        return cell.row === row && cell.col === col && cell.char === char && neutral(cell);
    });
}
/** The placements of rows of text from column 0, blanks omitted, in row-major order. */
const place = rows => rows.flatMap((text, row) => [...text].flatMap((char, col) => (char === ' ' ? [] : [[row, col, char]])));

/** The expected rows of the pinned seed on a screen of `rows` rows. */
const pinnedBase = rows => Array.from({ length: rows }, (_, row) => seedRows[row] ?? '');
/** The expected rows of the full-height boundary control. */
const bottomBase = rows => Array.from({ length: rows }, (_, row) => (row === 0 ? bottomRows.top : row === rows - 2 ? bottomRows.next : row === rows - 1 ? bottomRows.last : ''));
/** The expected rows of the scroll-region control. */
const regionBase = rows => Array.from({ length: rows }, (_, row) => regionTexts[row] ?? '');
/** The rows after one edit. */
const edited = (contract, base, at, count, top = 0, bottom = base.length - 1) => lineEditModel(base, contract.mode, at, count, top, bottom);

/** Whether a state's margins, origin mode and insert mode match what its group's setup contracts. */
function stateIsContracted(state, role) {
    const margins = regionRoles.has(role) ? [regionTop, regionBottom] : [0, state.rows - 1];
    return state.margins[0] === margins[0] && state.margins[1] === margins[1] && state.origin === originRoles.has(role) && state.insert === false;
}

/** The stored fields of one cell that a move must carry along. */
const storedKey = cell => JSON.stringify([cell.char, cell.flags, cell.fgIndex, cell.bgIndex, cell.foregroundArgb, cell.backgroundArgb,
    cell.underlineStyle, cell.underlineColorIndex, cell.underlineArgb, cell.overline, cell.fg, cell.bg]);
/** A cell whose only stored attribute is a background color (a new blank line cell). */
const backgroundOnly = cell => cell.char === ' ' && cell.bold === false && cell.dim === false && cell.italic === false && cell.underline === false
    && cell.inverse === false && cell.hidden === false && cell.strikethrough === false && cell.blink === false && cell.overline === false
    && cell.underlineStyle === 0 && cell.fgIndex === -1 && cell.foregroundArgb === null && cell.underlineColorIndex === -1 && cell.underlineArgb === null;

/** The attribute control: moved cells keep their stored tuple (required), new blank lines carry no attribute but a background (required),
 * and whether the blank lines take the current background is observed against the pen reference cell. */
function attributeState(contract, before, after, pen) {
    const rows = after.rows;
    const at = 1;
    const sources = lineEditModel(Array.from({ length: rows }, (_, row) => row), contract.mode, at, 1, 0, rows - 1, null);
    const bySource = new Map();
    for (const cell of before.ink) bySource.set(cell.row, [...bySource.get(cell.row) ?? [], cell]);
    const seedOk = before.ink.length === attrTexts.join('').length && attrTexts.every((text, row) => (bySource.get(row) ?? []).map(cell => cell.char).join('') === text)
        && (bySource.get(1) ?? []).every(cell => cell.bold === true && cell.bgIndex === -1 && cell.fgIndex === -1)
        && (bySource.get(2) ?? []).every(cell => cell.bold === false && cell.fgIndex === 1 && cell.bgIndex === 4)
        && [0, 3].every(row => (bySource.get(row) ?? []).every(neutral)) && sameCursor(before, 1, 0);
    const movedCells = [], blankRows = [];
    for (const [row, source] of sources.entries()) {
        if (source === null) blankRows.push(row);
        else for (const cell of bySource.get(source) ?? []) movedCells.push({ ...cell, row });
    }
    const keep = after.ink.filter(cell => !blankRows.includes(cell.row));
    const blanks = after.ink.filter(cell => blankRows.includes(cell.row));
    const movedOk = keep.length === movedCells.length && movedCells.every((cell, index) => keep[index].row === cell.row && keep[index].col === cell.col
        && storedKey(keep[index]) === storedKey(cell));
    const blanksOk = blanks.every(cell => backgroundOnly(cell));
    const penCell = pen.ink.find(cell => cell.char === 'X');
    const penMeasured = penCell !== undefined && (penCell.bgIndex >= 0 || penCell.backgroundArgb !== null);
    const painted = blankRows.map(row => blanks.filter(cell => cell.row === row));
    const isPainted = cell => cell.bgIndex >= 0 || cell.backgroundArgb !== null;
    const sameResolved = cell => penMeasured && isPainted(cell) && JSON.stringify(cell.bg) === JSON.stringify(penCell.bg);
    const sameStored = cell => sameResolved(cell) && cell.bgIndex === penCell.bgIndex && cell.backgroundArgb === penCell.backgroundArgb;
    const everyCell = predicate => painted.length > 0 && painted.every(cells => cells.length === after.cols && cells.every(predicate));
    const fill = painted.every(cells => cells.length === 0) ? 'new blank lines keep the default background'
        : !penMeasured ? 'unmeasured: the reference pen cell was not stored with a background'
            : everyCell(sameStored) ? 'new blank lines carry the current background across the full line'
                : everyCell(sameResolved) ? 'new blank lines carry the current background color across the full line, stored differently from the pen cell (palette index versus literal ARGB)'
                    : painted.flat().every(isPainted) && !painted.flat().some(sameResolved) ? 'new blank lines carry a different non-default background color than the current pen'
                        : 'mixed or partial background fill';
    const storedOf = cell => (cell === undefined ? null : { bgIndex: cell.bgIndex, backgroundArgb: cell.backgroundArgb, resolved: cell.bg });
    return { seedOk, movedOk, blanksOk, blankRows, fill, detail: { pen: penMeasured ? storedOf(penCell) : null, firstBlank: storedOf(painted[0]?.[0]) },
        cursorOk: after.cursor[0] === 1 };
}

/** Why the pinned rows did not match, as an unsupported note; the diagnosis only picks the words, never the verdict. */
function diagnose(contract, state) {
    const rows = state.rows;
    const base = pinnedBase(rows);
    const same = rowsExpected => inkIs(state, place(rowsExpected));
    if (same(base)) return 'The rows were unchanged after the edit: the line edit was a no-op';
    const other = contract.mode === 'insert' ? 'delete' : 'insert';
    if (same(lineEditModel(base, other, 1, 1, 0, rows - 1))) return `The rows match ${other === 'insert' ? 'IL' : 'DL'} instead: lines moved the wrong way`;
    for (const count of [2, Infinity]) {
        if (same(edited(contract, base, 1, count))) return 'The rows match a larger count than the pinned edit asked for';
    }
    const shifted = [...base];
    shifted[1] = contract.mode === 'insert' ? ` ${seedRows[1]}` : seedRows[1].slice(1);
    if (inkIs(state, place(shifted))) return 'Only the cursor row changed, by a single cell: characters were edited instead of lines';
    if (state.ink.length > 0 && state.ink[0].row > 0) return 'The first row changed: the screen scrolled instead of editing lines at the cursor row';
    return 'The edited rows differ from the contracted line edit';
}

/** The shared control walk, then the exact line-edit verdict. */
function gradeLineEdit(contract, observation) {
    const { before, setup, reverseVideo, initial } = observation;
    const inconclusive = note => ({ outcome: 'inconclusive', note });
    if (setup.rows < minimum.rows || setup.cols < minimum.cols) return inconclusive(pinnedGeometryNote);
    if (reverseVideo !== false) return inconclusive('DECSCNM reverse video was not proven off, so stored attributes cannot be separated from it');
    const rows = setup.rows;
    const badState = contract.roles.find(role => !stateIsContracted(observation[role], role));
    if (badState !== undefined) {
        return inconclusive(`The observed margins, origin mode or insert mode differ from the contracted setup in the ${badState} read, so the edit cannot be attributed`);
    }
    if (initial.ink.length !== 0) return inconclusive('Blank neutral screen control was not measured (the screen was not empty)');
    const base = pinnedBase(rows);
    if (!inkIs(before, place(base)) || !sameCursor(before, 3, 5)) return inconclusive('The pinned seed rows were not measured before the edit');
    if (!inkIs(setup, place(base)) || !sameCursor(setup, 1, 0)) return inconclusive('The pinned cursor setup did not reach row 2 column 1');
    const bottomStart = bottomBase(rows);
    if (!inkIs(observation.bottomBefore, place(bottomStart)) || observation.bottomBefore.cursor[0] !== rows - 2) {
        return inconclusive('The full-height boundary control seed was not measured, so the discard or blank-row boundary cannot be distinguished');
    }
    const regionStart = regionBase(rows);
    for (const [role, row] of [['regionBefore', 3], ['originBefore', 3]]) {
        if (!inkIs(observation[role], place(regionStart)) || observation[role].cursor[0] !== row) {
            return inconclusive(`The scroll-region control seed was not measured (${role}), so the region boundary cannot be distinguished`);
        }
    }
    const attributes = attributeState(contract, observation.attrBefore, observation.attr, observation.pen);
    if (!attributes.seedOk) return inconclusive('The attribute control seed was not measured, so moved-cell attributes cannot be distinguished');
    const expectedEdited = edited(contract, base, 1, 1);
    if (!inkIs(observation.edited, place(expectedEdited))) return { outcome: 'unsupported', note: diagnose(contract, observation.edited) };
    if (!sameCursor(observation.edited, 1, 0)) return { outcome: 'unsupported', note: 'The cursor drifted during the edit: it must end at row 2, column 1' };
    const bottomExpected = edited(contract, bottomStart, rows - 2, 1);
    if (!inkIs(observation.bottom, place(bottomExpected)) || observation.bottom.cursor[0] !== rows - 2) {
        return { outcome: 'unsupported', note: 'The full-height boundary control differs: the discarded or blank row, the top row or the cursor row is wrong' };
    }
    const regionChecks = [['region', 3, 1], ['regionAbove', 1, 1], ['regionBelow', 5, 1]];
    for (const [role, row, count] of regionChecks) {
        const expected = edited(contract, regionStart, row, count, regionTop, regionBottom);
        if (!inkIs(observation[role], place(expected)) || observation[role].cursor[0] !== row) {
            return { outcome: 'unsupported', note: `The scroll-region control differs (${role}): the region margins or the rows outside them were not respected` };
        }
    }
    const originExpected = edited(contract, regionStart, 3, 1, regionTop, regionBottom);
    if (!inkIs(observation.origin, place(originExpected)) || observation.origin.cursor[0] !== 3) {
        return { outcome: 'unsupported', note: 'The origin-mode scroll-region control differs: the edit did not act on the cursor row inside the margins' };
    }
    if (!attributes.movedOk || !attributes.blanksOk || !attributes.cursorOk) {
        return { outcome: 'unsupported', note: 'Moved cells lost their stored attributes, new blank lines carried attributes, or the cursor row changed, in the attribute control' };
    }
    return { outcome: 'supported' };
}

/** The first `count` rows of a group's ink as ten-column text, `_` for blank. */
const render = (state, indexes) => indexes.map(row => Array.from({ length: 10 }, (_, col) => {
    const cell = state.ink.find(item => item.row === row && item.col === col);
    return cell === undefined || cell.char === ' ' ? '_' : cell.char;
}).join(''));
/** A compact stored record of one cell. */
const brief = cell => (neutral(cell) ? `${cell.row},${cell.col},${cell.char}`
    : { row: cell.row, col: cell.col, char: cell.char, flags: cell.flags, fgIndex: cell.fgIndex, bgIndex: cell.bgIndex,
        foregroundArgb: cell.foregroundArgb, backgroundArgb: cell.backgroundArgb });
/** Identical adjacent non-neutral cells collapse into one range entry; neutral cells stay one string each. */
function briefInk(ink) {
    const key = cell => JSON.stringify([cell.row, cell.char, cell.flags, cell.fgIndex, cell.bgIndex, cell.foregroundArgb, cell.backgroundArgb]);
    const out = [];
    let last = null;
    for (const cell of ink) {
        if (neutral(cell)) { out.push(brief(cell)); last = null; continue; }
        if (last !== null && last.row === cell.row && last.col + 1 === cell.col && key(last) === key(cell)) {
            const entry = out.at(-1);
            if (entry.cols === undefined) { entry.cols = [entry.col, cell.col]; delete entry.col; } else entry.cols[1] = cell.col;
        } else out.push(brief(cell));
        last = cell;
    }
    return out;
}

/** The observed record: every group's text, cursor, margins and ink, plus the labeled count, boundary, region and background observations. */
function briefLineEdit(contract, observation) {
    const rows = observation.setup.rows;
    const head = Array.from({ length: 6 }, (_, row) => row);
    const state = (item, indexes = head, withInk = false) => ({ text: render(item, indexes), cursor: item.cursor, margins: item.margins, origin: item.origin,
        insert: item.insert, scrollback: item.scrollback, ...(withInk ? { ink: briefInk(item.ink) } : {}) });
    const base = pinnedBase(rows);
    const count = name => contract.spellings[name].count;
    const spellingRecord = name => ({ sequence: contract.spellings[name].bytes, count: count(name) === Infinity ? 'oversized' : count(name), ...state(observation[name]),
        matchesModel: inkIs(observation[name], place(edited(contract, base, 1, count(name)))),
        sameAsPinned: JSON.stringify(observation[name].ink) === JSON.stringify(observation.edited.ink) });
    const tail = [0, rows - 3, rows - 2, rows - 1];
    const regionHead = Array.from({ length: 8 }, (_, row) => row);
    const attributes = attributeState(contract, observation.attrBefore, observation.attr, observation.pen);
    const histories = contract.roles.map(role => observation[role].scrollback);
    return { geometry: { rows, cols: observation.setup.cols }, reverseVideo: observation.reverseVideo,
        pinnedSequence: contract.sequence, pinnedFeeds: contract.pinnedFeeds,
        initial: { ink: observation.initial.ink.length, cursor: observation.initial.cursor, margins: observation.initial.margins },
        before: state(observation.before), setup: state(observation.setup), edited: state(observation.edited),
        cursorColumn: { policy: 'observed, not graded: where the cursor column ends after the edit (the pinned setup starts at column 1)',
            beforeColumn: observation.bottomBefore.cursor[1], afterColumn: observation.bottom.cursor[1] },
        countControls: { policy: 'observed, not graded: separate count spellings of the same edit, kept distinct from the pinned ESC[1' + contract.operator + ' spelling',
            ...Object.fromEntries(contract.others.map(name => [name, spellingRecord(name)])) },
        bottomBoundary: { policy: 'graded control: the second-to-last row of a full-height screen; the top row is unaffected',
            before: state(observation.bottomBefore, tail), after: state(observation.bottom, tail) },
        scrollRegion: { policy: 'graded control: DECSTBM rows 3-5 read back from the stored margins; rows outside are unchanged; larger counts observed',
            margins: observation.regionBefore.margins, before: state(observation.regionBefore, regionHead), after: state(observation.region, regionHead),
            oversizedCount: state(observation.regionBig, regionHead), cursorAbove: state(observation.regionAbove, regionHead),
            cursorBelow: state(observation.regionBelow, regionHead) },
        originMode: { policy: 'graded control: DECOM on inside the region, the stored origin flag read back',
            before: state(observation.originBefore, regionHead), after: state(observation.origin, regionHead) },
        attributes: { policy: 'moved cells keep their stored tuple and new blank lines carry no attribute but a background (required); background fill is observed, not required',
            before: state(observation.attrBefore, head, true), after: state(observation.attr, head, true), penReference: state(observation.pen, [0], true),
            movedKeepTuple: attributes.movedOk, blankLinesBackgroundOnly: attributes.blanksOk, blankRows: attributes.blankRows,
            backgroundFill: attributes.fill, backgroundFillDetail: attributes.detail },
        history: { policy: 'observed, not graded: no pinned criterion requires scrollback behavior and the reads run on the alternate screen',
            scrollbackLines: [...new Set(histories)], historyLines: contract.roles.reduce((sum, role) => sum + observation[role].history.length, 0) } };
}

/** Group the read screens of one case by role. */
export function lineEditObservation(contract, screens) {
    if (!Array.isArray(screens) || screens.length !== contract.roles.length) throw new Error('Missing line-edit read captures.');
    const observation = Object.fromEntries(contract.roles.map((role, index) => [role, group(screens[index])]));
    const binding = screens.map(screen => screen.colorBinding.reverseVideo);
    observation.reverseVideo = binding.every(value => value === false) ? false : binding.some(value => value === true) ? true : null;
    return observation;
}

/** Independent contract grader entry: mirrors the pinned criteria without the bundle. */
export function gradeLineEditCase(contract, observation, revision = lineEditContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown line-edit contracts revision ${revision}.`);
    if (!observation || contract.roles.some(role => !observation[role] || !Array.isArray(observation[role].ink))) {
        throw new Error(`Missing ${contract.caseId} read captures.`);
    }
    return contract.grade(observation);
}

export { planFeeds as lineEditFeeds, planReads as lineEditReads } from './graded-plan.mjs';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateLineEditFeeds(contract, feeds) {
    validateExactFeeds(contract, feeds, 'contract');
}

/** The fixed fixture facts other modules and tests read. */
export const lineEditFixture = Object.freeze({ seedRows, seedFeeds, cursorFeed, bottomSeed, bottomRows, regionTexts, regionMargins, attrTexts, minimum, clear });
