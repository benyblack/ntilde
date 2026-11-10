// Versioned probe contracts for the column-editing qualification batch:
// editing.sl (SL, scroll left), editing.sr (SR, scroll right), editing.decic (DECIC, insert column) and editing.decdc (DECDC,
// delete column).
//
// Pinned terminfo.dev@4.0.0 definitions (bundle SHA-256
// aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6, probeHash 16a690f34c0e). All four termless probes read the
// geometry first (`ctx.cols`, `getScrollback().screenLines`), feed ONE seed string, read the cells (`before`, arrays of single
// characters), feed a cursor move and read the cursor (`setup`), feed the edit and read the cells again (`after`):
//   editing.sl     needs cols >= 9, rows >= 2. Seed ESC[1;1HABCDEFGHIJK ESC[2;1HJKLMNOPQRST (both clipped to min(cols, 11), so 11
//                  columns on any screen this suite accepts). Setup ESC[1;1H, must read x=0,y=0. Edit ESC[2 @ (CSI 2 SP @). Supported
//                  iff each of the first nine cells of both rows holds the seed cell two columns to its right (a blank past the page).
//   editing.sr     needs cols >= 9, rows >= 2. Seed ESC[1;1HABCDEFGHI ESC[2;1HJKLMNOPQR. Setup ESC[1;1H (x=0,y=0). Edit ESC[2 A
//                  (CSI 2 SP A). Supported iff columns 1-2 of both rows are blank and columns 3-9 hold the seed shifted right by two.
//   editing.decic  needs cols >= 8, rows >= 3. Seed ESC[1;1HABCDEFGH ESC[2;1HIJKLMNOP ESC[3;1HQRSTUVWX. Setup ESC[2;3H, must read
//                  x=2,y=1. Edit ESC[2'} (CSI 2 ' }). Control: columns 1-2 of the three rows unchanged (else inconclusive).
//                  Supported iff columns 3-4 are blank and columns 5-8 hold the seed shifted right by two, on all three rows.
//   editing.decdc  needs cols >= 8, rows >= 3. Seed ESC[1;1HABCDEFGHIJ ESC[2;1HIJKLMNOPQR ESC[3;1HQRSTUVWXab (clipped to
//                  min(cols, 10), so 10 columns here). Setup ESC[2;3H (x=2,y=1). Edit ESC[2'~ (CSI 2 ' ~). Control: columns 1-2
//                  unchanged (else inconclusive). Supported iff columns 3-8 hold the seed cell two columns to the right.
// A seed that did not read back, or a wrong setup cursor, is inconclusive in every case. The pinned probes never read the cursor
// after the edit, any row past the seeded ones, attributes, margins, origin mode, insert mode or the pen.
//
// The pinned terminal pixel path of every case (the second function of each registration) writes a cursor move and the edit
// (SL/SR first erase row 1 and print 1234567), asks for a cursor position report and returns unmeasuredCellResult: pass:false,
// outcome inconclusive. It never carries a verdict, so there is no pixel criterion to qualify. This batch grades ONLY the termless
// criterion; the derivation pins each registration verbatim so a future upstream change that gave the pixel path a verdict would
// be refused.
//
// The effect contract is defined from DEC DECIC/DECDC (VT510: "no effect outside the scrolling margins", inserted or deleted at
// the cursor column, blank columns enter at the right margin) and from xterm's SL/SR (ECMA-48 SL/SR limited to the scrolling
// region and applied from the left margin while the cursor row is inside the region). It only ever makes the verdict stricter
// than the pinned one: every cell of the whole screen is compared with an independent column model written for this suite, so
// rows outside the region, the right edge, an off-by-one count or a wrong start column are all observed; the cursor must not
// move; and the following controls must hold. Each is one read group over a block seed measured first; a group may run several
// operations in sequence, and the model composes them, so a wrong count, start column or region in any one of them still changes
// the final screen. (The shared observer allows 64 captures per suite, so related controls share a read.) The block seed puts ten
// distinct characters at the left edge AND ten at the right edge of seven rows, so columns pushed past the right edge and blank
// columns entering there are both visible:
//   counts:   one, omitted and zero (each one), then three, composed; and separately an oversized count that clamps at the right edge;
//   region:   scroll margins with the cursor inside them (only the region rows move), then the cursor row above and below the
//             margins (both no-ops), composed; and separately origin mode (DECOM) with the margins;
//   modes:    insert mode (IRM) on, an extra parameter, a colon subparameter and a private leader (all three no-ops), then an
//             admitted edit that IRM must not change, composed;
//   columns:  DECIC/DECDC at the first column, then at the last column, composed (SL/SR start at the left margin, which every SL/SR
//             control already shows with the cursor elsewhere);
//   attributes and protection: attributed seed cells and characters written under DECSCA 1 (including protected text at the right
//             edge) keep their stored tuples and protection when they move while a different pen is active; introduced blank columns
//             carry no visual attribute and no protection (their colors are OBSERVED, not required); protected text pushed past the
//             edge or deleted disappears like any other; a reference cell printed afterwards proves the pen and cursor were kept.
//             Per-cell protection is read from the App's own stored state, never inferred.
// Margins, origin mode and insert mode are read from the App's own stored buffer state in every read.
//
// OBSERVED, NOT GRADED: whether the pending-wrap state survives a column edit at the last column (a character printed afterwards
// either wraps to the next row or overwrites the last column). DEC does not define it; it is recorded, never required.
//
// Left/right margins (DECLRMM/DECSLRM) are not implemented by Ntilde and are not exercised; nothing here claims margin-bounded
// behavior. Character scope: the pinned fixtures are ASCII single-width cells; nothing here qualifies wide, combining or Unicode
// text, scaled text, the inline-image plane or rendered appearance. modes.left-right-margin, editing.decrqcra and every rectangle
// case are different cases and are not substituted.
import { planFeed, planRead, validateExactFeeds } from './graded-plan.mjs';
import { sgrAttrsReset, sgrAttrsPredicates, sgrAttrsCellAt } from './sgr-attrs-contracts.mjs';
import { selectiveEraseInk, validateSelectiveEraseScreen } from './selective-erase-contracts.mjs';

const { neutral } = sgrAttrsPredicates;

/** Contracts v1 is the first measured semantics of this batch. */
export const colEditContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const colEditSuiteId = 'ntilde-owned-column-editing-graded-v1';
/** The nonce prefix binding worker output to this suite. */
export const colEditNoncePrefix = 'NTILDE_CE_';
/** The measured reset every plan begins with: protection, DECSCNM, DECOM, margins, IRM and SGR off, screen cleared, cursor home. */
export const colEditReset = '\x1b[0"q' + sgrAttrsReset;
/** Column-edit screens carry stored attributes, per-cell protection, buffer state and history (the selective-erasure screen). */
export const validateColEditScreen = validateSelectiveEraseScreen;
/** The pinned getCell projection: the stored character of one cell. */
export const colEditCellAt = sgrAttrsCellAt;
/** Between groups: margins, origin mode, insert mode, SGR and character protection off, screen cleared, cursor home. */
const clear = '\x1b[r\x1b[?6l\x1b[4l\x1b[0m\x1b[0"q\x1b[2J\x1b[H';

/** The pinned note texts, byte for byte, per mode. */
export const pinnedNotes = Object.freeze({
    left: Object.freeze({ geometry: 'Need a measured 2x9 fixture', seed: 'SL seed and incoming sources were not measured', cursor: 'SL cursor setup failed',
        controls: null, expected: 'SL shifts the first nine observed cells of two rows left by two columns' }),
    right: Object.freeze({ geometry: 'Need a measured 2x9 fixture', seed: 'SR seed was not measured', cursor: 'SR cursor setup failed',
        controls: null, expected: 'SR shifts the first nine measured cells of two rows right by two columns' }),
    insert: Object.freeze({ geometry: 'Need a measured 3x8 fixture', seed: 'DECIC seed was not measured', cursor: 'DECIC cursor setup failed',
        controls: 'DECIC unchanged-prefix control changed', expected: 'DECIC inserts two blank columns at column 3 on three measured rows' }),
    delete: Object.freeze({ geometry: 'Need a measured 3x8 fixture', seed: 'DECDC seed and incoming sources were not measured', cursor: 'DECDC cursor setup failed',
        controls: 'DECDC unchanged-prefix control changed', expected: 'DECDC deletes two columns at column 3 on three measured rows' }),
});

/** The contract's own geometry note. */
export const pinnedGeometryNote = 'Measured geometry cannot fit the column-edit fixture';
/** The measured geometry the controls need (the pinned probes need at most 11 columns and 3 rows). */
const minimum = Object.freeze({ rows: 13, cols: 32 });

/** The pinned seeds, cursor feeds and edits, per mode, byte for byte. `setup` is the zero-based [row, col] the cursor feed must reach. */
const pinned = Object.freeze({
    left: Object.freeze({ seed: '\x1b[1;1HABCDEFGHIJK\x1b[2;1HJKLMNOPQRST', cursorFeed: '\x1b[1;1H', edit: '\x1b[2 @',
        rows: Object.freeze(['ABCDEFGHIJK', 'JKLMNOPQRST']), cursor: [1, 11], setup: [0, 0], width: 11, afterWidth: 9 }),
    right: Object.freeze({ seed: '\x1b[1;1HABCDEFGHI\x1b[2;1HJKLMNOPQR', cursorFeed: '\x1b[1;1H', edit: '\x1b[2 A',
        rows: Object.freeze(['ABCDEFGHI', 'JKLMNOPQR']), cursor: [1, 9], setup: [0, 0], width: 9, afterWidth: 9 }),
    insert: Object.freeze({ seed: '\x1b[1;1HABCDEFGH\x1b[2;1HIJKLMNOP\x1b[3;1HQRSTUVWX', cursorFeed: '\x1b[2;3H', edit: "\x1b[2'}",
        rows: Object.freeze(['ABCDEFGH', 'IJKLMNOP', 'QRSTUVWX']), cursor: [2, 8], setup: [1, 2], width: 8, afterWidth: 8 }),
    delete: Object.freeze({ seed: '\x1b[1;1HABCDEFGHIJ\x1b[2;1HIJKLMNOPQR\x1b[3;1HQRSTUVWXab', cursorFeed: '\x1b[2;3H', edit: "\x1b[2'~",
        rows: Object.freeze(['ABCDEFGHIJ', 'IJKLMNOPQR', 'QRSTUVWXab']), cursor: [2, 10], setup: [1, 2], width: 10, afterWidth: 8 }),
});

/** The block seed: seven rows with ten distinct characters at the left edge and ten at the right edge (written backwards from the last column). */
const leftTexts = Object.freeze(['ABCDEFGHIJ', 'KLMNOPQRST', 'UVWXYZabcd', 'efghijklmn', 'opqrstuvwx', 'yz01234567', '89!#%&*+-=']);
const rightTexts = Object.freeze(['0123456789', '9876543210', 'zyxwvutsrq', 'ponmlkjihg', 'fedcbaZYXW', 'VUTSRQPONM', 'LKJIHGFEDC']);
const edgeFeed = (row, text) => `\x1b[${row};999H\x1b[${text.length - 1}D${text}`;
const blockFeed = leftTexts.map((text, row) => `\x1b[${row + 1};1H${text}${edgeFeed(row + 1, rightTexts[row])}`).join('');
/** The scroll-region margins of the region controls (rows 3-5, zero-based 2-4). */
const regionMargins = '\x1b[3;5r';
const regionTop = 2, regionBottom = 4;
/** The attribute and protection control seed: a protected XYZ at the right edge of row 1, then rows of plain, bold, red-on-blue and
 * plain text whose ROW (columns 7-9 of row 4) is written under DECSCA 1. The edge is written first so the seed ends at row 4, column 11. */
const attrSeed = '\x1b[1;999H\x1b[2D\x1b[1"qXYZ\x1b[0"q\x1b[1;1HKEEP-TOP--\x1b[2;1H\x1b[1mBOLD-ROW--\x1b[0m\x1b[3;1H\x1b[31;44mRED-ON-BLU\x1b[0m'
    + '\x1b[4;1HPLAIN-\x1b[1"qROW\x1b[0"q-';
const attrTexts = Object.freeze(['KEEP-TOP--', 'BOLD-ROW--', 'RED-ON-BLU', 'PLAIN-ROW-']);
/** The protected cells of the attribute seed on a page: ROW on row 4 and XYZ at the right edge of row 1, zero-based. */
const attrProtected = cols => [[3, 6], [3, 7], [3, 8], [0, cols - 3], [0, cols - 2], [0, cols - 1]];
/** The pen of the attribute control and the reference cell character printed under it. */
const attrPen = '\x1b[3;33;42m';
const referenceChar = 'P';
/** The parked cursor of the plain controls (row 4, column 6) and of the attribute control (row 12, column 3). */
const plainPark = Object.freeze({ row: 4, col: 6 });
const attrPark = Object.freeze({ row: 12, col: 3 });
/** The character the pending-wrap observation prints after the edit. */
const wrapProbe = 'w';

// ---------------------------------------------------------------------------------------------------------------------------
// The independent column model. Written for this suite from the DEC and xterm definitions; it shares no code with the parser.

/** The four column operations: their final bytes, where they start and whether they insert. */
const kinds = Object.freeze({
    left: Object.freeze({ final: ' @', atCursor: false, insert: false }),
    right: Object.freeze({ final: ' A', atCursor: false, insert: true }),
    insert: Object.freeze({ final: "'}", atCursor: true, insert: true }),
    delete: Object.freeze({ final: "'~", atCursor: true, insert: false }),
});

/** Shift one row in place at column `start`: insert `n` blanks there, or delete `n` cells there and refill at the right edge. */
function shiftRow(line, start, n, insert, blank) {
    const old = [...line];
    for (let col = start; col < line.length; col++) {
        if (insert) line[col] = col < start + n ? blank : old[col - n];
        else line[col] = col + n < line.length ? old[col + n] : blank;
    }
}


/** One column operation. `count` undefined omits the parameter; `form.leader` / `form.extra` spell a non-admitted identity. */
const columnOp = (mode, count, form = {}) => {
    const { leader = '', extra = '' } = form;
    const kind = kinds[mode];
    const admitted = leader === '' && extra === '';
    return Object.freeze({ mode, bytes: `\x1b[${leader}${count ?? ''}${extra}${kind.final}`, admitted,
        /** The region rows, start column and count this operation acts on with the cursor at `cursor`, or null when it does nothing. */
        span(page, [row, col]) {
            if (!admitted || row < page.top || row > page.bottom) return null;
            const start = kind.atCursor ? Math.min(col, page.cols - 1) : 0;
            return { start, n: Math.min((count ?? 0) > 0 ? count : 1, page.cols - start), insert: kind.insert };
        },
        /** Apply to parallel grids (character, protection, ...) with their own blank values. */
        apply(grids, blanks, page, cursor) {
            const span = this.span(page, cursor);
            if (span === null) return;
            for (let row = page.top; row <= page.bottom; row++) grids.forEach((grid, index) => shiftRow(grid[row], span.start, span.n, span.insert, blanks[index]));
        } });
};

/** Rows of text on a `rows` x `cols` page as a grid of one-character cells. */
const gridOf = (texts, rows, cols) => Array.from({ length: rows }, (_, row) => Array.from({ length: cols }, (_, col) => texts[row]?.[col] ?? ' '));
const textsOf = grid => grid.map(row => row.join(''));
/** The block seed on a page: the left texts from column 0 and the right texts ending at the last column. */
function blockGrid(rows, cols) {
    const grid = gridOf(leftTexts, rows, cols);
    rightTexts.forEach((text, row) => [...text].forEach((char, index) => { grid[row][cols - text.length + index] = char; }));
    return grid;
}

// ---------------------------------------------------------------------------------------------------------------------------
// The control groups. Every group is one fenced write (clear, seed, setup, steps) followed by one read.

const cup = park => `\x1b[${park.row};${park.col}H`;
/** One block-seed control: its steps (a parked cursor, or null to keep the seed's cursor, then an operation) under margins, origin or insert mode. */
const control = (role, steps, { margins = false, origin = false, insert = false, graded = true } = {}) => Object.freeze({
    role, steps: Object.freeze(steps), margins, origin, insert, graded,
    feed: clear + blockFeed + (margins ? regionMargins : '') + (origin ? '\x1b[?6h' : '') + (insert ? '\x1b[4h' : '')
        + steps.map(step => (step.park ? cup(step.park) : '') + step.op.bytes + (step.after ?? '')).join(''),
});
const at = (park, op, after) => Object.freeze({ park, op, ...(after === undefined ? {} : { after }) });

/** Every mode's controls: the shared ones, then the mode's column controls and the observed pending-wrap control. */
function controlsOf(mode) {
    const op = (count, form) => columnOp(mode, count, form);
    const shared = [
        control('counts', [at(plainPark, op(1)), at(plainPark, op(undefined)), at(plainPark, op(0)), at(plainPark, op(3))]),
        control('clip', [at(plainPark, op(999))]),
        control('region', [at(plainPark, op(2)), at({ row: 1, col: 6 }, op(2)), at({ row: 7, col: 6 }, op(2))], { margins: true }),
        control('origin', [at({ row: 2, col: 3 }, op(2))], { margins: true, origin: true }),
        control('modes', [at(plainPark, op(2, { extra: ';1' })), at(plainPark, op(2, { extra: ':1' })), at(plainPark, op(2, { leader: '?' })), at(plainPark, op(2))],
            { insert: true }),
    ];
    const columns = kinds[mode].atCursor ? [control('columns', [at({ row: 4, col: 1 }, op(2)), at({ row: 4, col: 999 }, op(2))])] : [];
    const pendingWrap = control('pendingWrap', [at(null, op(1), wrapProbe)], { graded: false });
    return Object.freeze([...shared, ...columns, pendingWrap]);
}
const controlsFor = Object.freeze(Object.fromEntries(Object.keys(kinds).map(mode => [mode, controlsOf(mode)])));

/** The attribute control: the attribute seed, the control pen, one operation of the mode and the reference cell. */
const attrOpOf = mode => columnOp(mode, 2);
const attrFeedOf = mode => clear + attrSeed + cup(attrPark) + attrPen + attrOpOf(mode).bytes + referenceChar;

/** Every read group of one mode, in plan order, with the bytes that precede its read. */
function groupsFor(mode) {
    const spec = pinned[mode];
    return [
        { role: 'initial', feeds: [] },
        { role: 'before', feeds: [spec.seed] },
        { role: 'setup', feeds: [spec.cursorFeed] },
        { role: 'edited', feeds: [spec.edit] },
        { role: 'blockBefore', feeds: [clear + blockFeed] },
        ...controlsFor[mode].map(item => ({ role: item.role, feeds: [item.feed] })),
        { role: 'attrBefore', feeds: [clear + attrSeed] },
        { role: 'attr', feeds: [attrFeedOf(mode)] },
    ];
}
const controlOf = (mode, role) => controlsFor[mode].find(item => item.role === role);

/** One case: the pinned fixture plus its discriminating controls, in a fixed plan. */
const case_ = ({ caseId, mode, id, label, demand }) => {
    const spec = pinned[mode];
    const groups = groupsFor(mode);
    const plan = [planFeed(colEditReset)];
    for (const group of groups) {
        for (const feed of group.feeds) plan.push(planFeed(feed));
        plan.push(planRead('cells'));
    }
    const pinnedFeeds = Object.freeze([spec.seed, spec.cursorFeed, spec.edit]);
    return Object.freeze({
        caseId, probeId: caseId, mode, id, sequence: spec.edit, seedRows: spec.rows,
        roles: Object.freeze(groups.map(group => group.role)),
        others: Object.freeze(controlsFor[mode].filter(item => item.graded).map(item => item.role)),
        pinnedPath: 'termless', beforeRead: 1, setupRead: 2, pinnedRead: 3,
        pinnedExpected: pinnedNotes[mode].expected, pinnedCriterion: label, pinnedFeeds, plan: Object.freeze(plan),
        terminalFixture: { writes: [...pinnedFeeds], demand, minimum: { rows: spec.rows.length, cols: mode === 'insert' || mode === 'delete' ? 8 : 9 },
            note: 'upstream pixel path (a cursor move and the edit, then a cursor position report) is never a verdict and is not qualified' },
        appearanceScope: 'stored ASCII cells only: exact contents of the whole screen, the cursor, observed margins, origin and insert mode, stored attribute tuples '
            + 'and per-cell protection of moved cells and introduced blanks; left/right margins, the pinned pixel path, Unicode and wide cells, the image plane, '
            + 'the pending-wrap state and rendered appearance are not claimed',
        grade: observation => gradeColEdit(contract_(caseId), observation),
        observed: observation => briefColEdit(contract_(caseId), observation),
    });
};

const contracts = [];
const contract_ = caseId => contracts.find(item => item.caseId === caseId);
contracts.push(
    case_({ caseId: 'editing.sl', mode: 'left', id: 'SL',
        label: 'SL (ESC[2 @) shifts the first nine cells of rows 1-2 left by two columns, after a setup cursor of 0,0',
        demand: 'every row of the scrolling region shifted left by two from the left margin; blank columns at the right edge' }),
    case_({ caseId: 'editing.sr', mode: 'right', id: 'SR',
        label: 'SR (ESC[2 A) blanks columns 1-2 and shifts columns 3-9 of rows 1-2 right by two, after a setup cursor of 0,0',
        demand: 'every row of the scrolling region shifted right by two from the left margin; columns past the right edge lost' }),
    case_({ caseId: 'editing.decic', mode: 'insert', id: 'DECIC',
        label: "DECIC (ESC[2'}) keeps columns 1-2, blanks columns 3-4 and shifts columns 5-8 of rows 1-3 right by two, after a setup cursor of 1,2",
        demand: 'two blank columns inserted at the cursor column on every row of the scrolling region; columns past the right edge lost' }),
    case_({ caseId: 'editing.decdc', mode: 'delete', id: 'DECDC',
        label: "DECDC (ESC[2'~) keeps columns 1-2 and shifts columns 3-8 of rows 1-3 left by two, after a setup cursor of 1,2",
        demand: 'two columns deleted at the cursor column on every row of the scrolling region; blank columns at the right edge' }),
);
/** The four pinned cases, in suite order. */
export const colEditContracts = Object.freeze(contracts);
/** Contract case ids in suite order. */
export const colEditCaseIds = Object.freeze(colEditContracts.map(contract => contract.caseId));

// ---------------------------------------------------------------------------------------------------------------------------
// Grading.

/** A group's observation: its ink (with protection), cursor, geometry and the App's stored buffer state. */
const group = screen => ({ ink: selectiveEraseInk(screen), cursor: [screen.cursorRow, screen.cursorCol], rows: screen.rows, cols: screen.cols,
    margins: [screen.bufferState.scrollTop, screen.bufferState.scrollBottom], origin: screen.bufferState.originMode,
    insert: screen.bufferState.insertMode, scrollback: screen.bufferState.scrollbackLines, history: screen.history.lines });
const sameCursor = (state, [row, col]) => state.cursor[0] === row && state.cursor[1] === col;

/** Ink equals exactly these [row, col, char, protected?] cells, all with neutral stored attributes. */
function inkIs(state, placements) {
    if (state.ink.length !== placements.length) return false;
    return placements.every(([row, col, char, guarded = false], index) => {
        const cell = state.ink[index];
        return cell.row === row && cell.col === col && cell.char === char && cell.protected === guarded && neutral(cell);
    });
}
/** The placements of a character grid (and an optional protection grid), blanks omitted, in row-major order. */
const place = (chars, guarded = null) => chars.flatMap((line, row) => line.flatMap((char, col) => {
    const isProtected = guarded?.[row][col] ?? false;
    return char === ' ' && !isProtected ? [] : [[row, col, char, isProtected]];
}));

/** The page of a group: its geometry and the scrolling region its setup contracts. */
const pageOf = (item, rows, cols) => ({ rows, cols, top: item?.margins ? regionTop : 0, bottom: item?.margins ? regionBottom : rows - 1 });
/** The zero-based cursor a parked step reaches on a page (origin mode is relative to the region top; columns past the page clamp). */
function parkedCursor(item, park, rows, cols) {
    const row = item.origin ? Math.min(regionTop + park.row - 1, regionBottom) : Math.min(park.row, rows) - 1;
    return [row, Math.min(park.col, cols) - 1];
}
/** Run a control's steps over grids, returning the cursor after the last step (the seed's cursor when a step keeps it). */
function runSteps(item, grids, blanks, rows, cols, seedCursor) {
    const page = pageOf(item, rows, cols);
    let cursor = seedCursor;
    for (const step of item.steps) {
        if (step.park) cursor = parkedCursor(item, step.park, rows, cols);
        step.op.apply(grids, blanks, page, cursor);
    }
    return cursor;
}
/** The block seed's cursor after its last right-edge write: the last column of row 7 (pending wrap). */
const blockCursor = cols => [leftTexts.length - 1, cols - 1];
/** The expected character grid and cursor of a graded block control. */
function expectedControl(item, rows, cols) {
    const grid = blockGrid(rows, cols);
    const cursor = runSteps(item, [grid], [' '], rows, cols, blockCursor(cols));
    return { texts: grid, cursor };
}

/** Whether a state's margins, origin mode and insert mode match what its group's setup contracts. */
function stateIsContracted(state, item) {
    const margins = item?.margins ? [regionTop, regionBottom] : [0, state.rows - 1];
    return state.margins[0] === margins[0] && state.margins[1] === margins[1] && state.origin === (item?.origin ?? false) && state.insert === (item?.insert ?? false);
}

/** The stored fields of one cell, without its character. */
const tupleKey = cell => JSON.stringify([cell.flags, cell.fgIndex, cell.bgIndex, cell.foregroundArgb, cell.backgroundArgb,
    cell.underlineStyle, cell.underlineColorIndex, cell.underlineArgb, cell.overline, cell.fg, cell.bg, cell.protected]);
const storedKey = cell => JSON.stringify([cell.char, tupleKey(cell)]);
/** An introduced blank: a space with no visual attribute and no protection (its colors are observed, not required). */
const plainBlank = cell => cell.char === ' ' && cell.bold === false && cell.dim === false && cell.italic === false && cell.underline === false
    && cell.inverse === false && cell.hidden === false && cell.strikethrough === false && cell.blink === false && cell.overline === false
    && cell.underlineStyle === 0 && cell.underlineColorIndex === -1 && cell.underlineArgb === null && cell.protected === false;
const colored = cell => cell.fgIndex >= 0 || cell.bgIndex >= 0 || cell.foregroundArgb !== null || cell.backgroundArgb !== null;
const byPosition = (a, b) => (a.row - b.row) || (a.col - b.col);

/** The stored rendition each attribute seed row must read: plain, bold, red (1) on blue (4), plain. */
const attrRowRenditions = Object.freeze([
    cell => neutral(cell),
    cell => cell.bold === true && cell.italic === false && cell.bgIndex === -1 && cell.fgIndex === -1,
    cell => cell.bold === false && cell.italic === false && cell.fgIndex === 1 && cell.bgIndex === 4,
    cell => neutral(cell),
]);
/** Whether the attribute seed read holds the contracted text, renditions and protection, and the cursor after its last row. */
function attrSeedOk(before) {
    const guarded = new Set(attrProtected(before.cols).map(([row, col]) => `${row},${col}`));
    const expected = [...attrTexts.flatMap((text, row) => [...text].map((char, col) => ({ row, col, char }))),
        ...[...'XYZ'].map((char, index) => ({ row: 0, col: before.cols - 3 + index, char }))].sort(byPosition);
    return before.ink.length === expected.length && sameCursor(before, [3, 10]) && expected.every((cell, index) => {
        const seen = before.ink[index];
        const rendition = cell.row === 0 && cell.col >= before.cols - 3 ? neutral : attrRowRenditions[cell.row];
        return seen.row === cell.row && seen.col === cell.col && seen.char === cell.char && rendition(seen) && seen.protected === guarded.has(`${cell.row},${cell.col}`);
    });
}

const atReference = cell => cell.row === attrPark.row - 1 && cell.col === attrPark.col - 1;
/** Whether the reference cell carries the control pen: italic, yellow (3) on green (2), unprotected. */
const penMeasured = reference => reference.italic === true && reference.fgIndex === 3 && reference.bgIndex === 2 && reference.protected === false;

/** The attribute control: moved seed cells keep their stored tuples, introduced blanks carry no attribute, the pen and cursor are kept. */
function attributeState(mode, before, after) {
    const { rows, cols } = after;
    const reference = after.ink.find(atReference);
    const referenceOk = reference?.char === referenceChar;
    const penOk = referenceOk && penMeasured(reference);
    // Move the measured seed cells (and a marker of introduced columns) through the model.
    const cells = Array.from({ length: rows }, () => Array.from({ length: cols }, () => null));
    for (const cell of before.ink) cells[cell.row][cell.col] = cell;
    const introduced = Array.from({ length: rows }, () => Array.from({ length: cols }, () => false));
    attrOpOf(mode).apply([cells, introduced], [null, true], pageOf(null, rows, cols), [attrPark.row - 1, attrPark.col - 1]);
    const expected = cells.flatMap((line, row) => line.flatMap((cell, col) => (cell === null ? [] : [{ ...cell, row, col }])));
    const rest = after.ink.filter(cell => !atReference(cell));
    const moved = rest.filter(cell => !introduced[cell.row][cell.col]);
    const blanks = rest.filter(cell => introduced[cell.row][cell.col]);
    const expectedKept = expected.filter(cell => !atReference(cell)).sort(byPosition);
    const movedOk = moved.length === expectedKept.length && expectedKept.every((cell, index) => moved[index].row === cell.row && moved[index].col === cell.col
        && storedKey(moved[index]) === storedKey(cell));
    const blanksOk = blanks.every(plainBlank);
    const introducedCount = introduced.reduce((sum, line, row) => sum + line.filter((value, col) => value && !(row === attrPark.row - 1 && col === attrPark.col - 1)).length, 0);
    const coloredBlanks = blanks.filter(colored).length;
    let fill = 'introduced blanks carry no color';
    if (coloredBlanks === introducedCount && introducedCount > 0) fill = 'introduced blanks carry the current colors';
    else if (coloredBlanks > 0) fill = 'some introduced blanks carry colors';
    return { referenceOk, penOk, changedOk: movedOk && blanksOk, fill, cursorOk: sameCursor(after, [attrPark.row - 1, attrPark.col]), seedOk: attrSeedOk(before) };
}


/** Why the pinned rows did not match, as an unsupported note; the diagnosis only picks the words, never the verdict. */
function diagnose(contract, state) {
    const { rows, cols } = state;
    const spec = pinned[contract.mode];
    const same = grid => inkIs(state, place(grid));
    const seed = gridOf(spec.rows, rows, cols);
    if (same(seed)) return 'The rows were unchanged after the edit: the column operation was a no-op';
    const cursorRowOnly = gridOf(spec.rows, rows, cols);
    const page = { rows, cols, top: spec.setup[0], bottom: spec.setup[0] };
    columnOp(contract.mode, 2).apply([cursorRowOnly], [' '], page, spec.setup);
    if (same(cursorRowOnly)) return 'Only the cursor row changed: the operation did not act on every row of the scrolling region';
    return 'The edited rows differ from the contracted column operation';
}

const inconclusive = note => ({ outcome: 'inconclusive', note });
const unsupported = note => ({ outcome: 'unsupported', note });
const notControlRoles = new Set(['initial', 'before', 'setup', 'edited', 'blockBefore', 'attrBefore', 'attr', 'pendingWrap']);

/** Whether a group read exactly the rows of `grid` with the cursor at `cursor`. */
const readsRows = (state, grid, cursor) => inkIs(state, place(grid)) && sameCursor(state, cursor);
/** The first read whose stored margins, origin mode or insert mode differ from its group's setup. */
const badStateRole = (contract, observation) => contract.roles.find(role => !stateIsContracted(observation[role], controlOf(contract.mode, role)));
/** The reads every case measures before grading anything, in order, each with the note naming what it failed to measure.
 * Each check runs only when every earlier one held, so a later check may rely on the geometry and state the earlier ones proved. */
const seedChecks = Object.freeze([
    [(contract, observation) => observation.setup.rows >= minimum.rows && observation.setup.cols >= minimum.cols, () => pinnedGeometryNote],
    [(contract, observation) => observation.reverseVideo === false, () => 'DECSCNM reverse video was not proven off, so stored attributes cannot be separated from it'],
    [(contract, observation) => badStateRole(contract, observation) === undefined,
        (contract, observation) => `The observed margins, origin mode or insert mode differ from the contracted setup in the ${badStateRole(contract, observation)} read, so the edit cannot be attributed`],
    [(contract, observation) => observation.initial.ink.length === 0, () => 'Blank neutral screen control was not measured (the screen was not empty or held protected cells)'],
    [(contract, { before, setup }) => readsRows(before, gridOf(pinned[contract.mode].rows, setup.rows, setup.cols), pinned[contract.mode].cursor),
        () => 'The pinned seed rows were not measured before the edit'],
    [(contract, { setup }) => readsRows(setup, gridOf(pinned[contract.mode].rows, setup.rows, setup.cols), pinned[contract.mode].setup),
        () => 'The pinned cursor setup did not reach its contracted cell'],
    [(contract, { blockBefore, setup }) => readsRows(blockBefore, blockGrid(setup.rows, setup.cols), blockCursor(setup.cols)),
        () => 'The block control seed was not measured, so a misplaced, dropped or smeared column cannot be distinguished'],
    [(contract, observation) => attrSeedOk(observation.attrBefore),
        () => 'The attribute and protection control seed was not measured, so stored attributes and moved protection cannot be distinguished'],
    [(contract, observation) => {
        const attributes = attributeState(contract.mode, observation.attrBefore, observation.attr);
        return attributes.referenceOk && attributes.penOk;
    }, () => 'The reference cell printed under the control pen was not measured, so the pen cannot be attributed'],
]);

/** The first reason the seed, geometry or contracted state was not measured, or null. */
function seedGap(contract, observation) {
    const failed = seedChecks.find(([holds]) => !holds(contract, observation));
    return failed === undefined ? null : failed[1](contract, observation);
}

/** The first way a measured edit differs from the column model, or null. */
function columnMismatch(contract, observation) {
    const mode = contract.mode;
    const { rows, cols } = observation.setup;
    const spec = pinned[mode];
    const edited = gridOf(spec.rows, rows, cols);
    columnOp(mode, 2).apply([edited], [' '], { rows, cols, top: 0, bottom: rows - 1 }, spec.setup);
    if (!inkIs(observation.edited, place(edited))) return diagnose(contract, observation.edited);
    if (!sameCursor(observation.edited, spec.setup)) return 'The cursor drifted during the edit: it must stay where the setup put it';
    for (const role of contract.roles.filter(item => !notControlRoles.has(item))) {
        const expected = expectedControl(controlOf(mode, role), rows, cols);
        if (!inkIs(observation[role], place(expected.texts)) || !sameCursor(observation[role], expected.cursor)) {
            return `The ${role} control differs from the column model: a count, default, clamp, region, mode, start column, identity or the cursor was wrong`;
        }
    }
    const attributes = attributeState(mode, observation.attrBefore, observation.attr);
    if (!attributes.changedOk || !attributes.cursorOk) {
        return 'Moved cells lost their stored attributes or protection, an introduced blank carried a visual attribute or protection, protected text did not move, '
            + 'or the cursor or pen drifted, in the attribute and protection control';
    }
    return null;
}

/** The shared control walk, then the exact column-edit verdict. */
function gradeColEdit(contract, observation) {
    const gap = seedGap(contract, observation);
    if (gap !== null) return inconclusive(gap);
    const mismatch = columnMismatch(contract, observation);
    return mismatch === null ? { outcome: 'supported' } : unsupported(mismatch);
}

/** The observed pending-wrap control: where the character printed after the edit landed. */
function pendingWrapState(mode, state) {
    const { rows, cols } = state;
    const item = controlOf(mode, 'pendingWrap');
    const grid = blockGrid(rows, cols);
    runSteps(item, [grid], [' '], rows, cols, blockCursor(cols));
    const wrapped = grid.map(line => [...line]);
    wrapped[leftTexts.length][0] = wrapProbe;
    const overwritten = grid.map(line => [...line]);
    overwritten[leftTexts.length - 1][cols - 1] = wrapProbe;
    if (inkIs(state, place(wrapped)) && sameCursor(state, [leftTexts.length, 1])) return 'kept: the next character wrapped to the next row';
    if (inkIs(state, place(overwritten))) return 'cleared: the next character overwrote the last column';
    return 'neither: the edit or the printed character differs from both readings';
}

/** A group's rows as text over its head columns, `_` for blank. */
const render = (state, indexes, from = 0, width = 16) => indexes.map(row => Array.from({ length: width }, (_, offset) => {
    const cell = state.ink.find(item => item.row === row && item.col === from + offset);
    return cell === undefined || cell.char === ' ' ? '_' : cell.char;
}).join(''));
/** A compact record of one group: its head rows, their right-edge columns, protected cells, ink count and stored state. */
const briefState = state => {
    const head = Array.from({ length: Math.min(state.rows, 12) }, (_, row) => row);
    return { text: render(state, head), rightEdge: render(state, head, state.cols - 12, 12), lastRow: render(state, [state.rows - 1])[0],
        protectedCells: state.ink.filter(cell => cell.protected).map(cell => [cell.row, cell.col]), ink: state.ink.length,
        cursor: state.cursor, margins: state.margins, origin: state.origin, insert: state.insert, scrollback: state.scrollback };
};

/** The observed record: every group's text, cursor, margins and ink, the protection, attribute and pending-wrap observations. */
function briefColEdit(contract, observation) {
    const mode = contract.mode;
    const { rows, cols } = observation.setup;
    const attributes = attributeState(mode, observation.attrBefore, observation.attr);
    const controls = Object.fromEntries(contract.others.map(role => {
        const item = controlOf(mode, role);
        const expected = expectedControl(item, rows, cols);
        return [role, { sequences: item.steps.map(step => step.op.bytes),
            matchesModel: inkIs(observation[role], place(expected.texts)) && sameCursor(observation[role], expected.cursor), ...briefState(observation[role]) }];
    }));
    const histories = contract.roles.map(role => observation[role].scrollback);
    return { geometry: { rows, cols }, reverseVideo: observation.reverseVideo, pinnedSequence: contract.sequence, pinnedFeeds: contract.pinnedFeeds,
        initial: { ink: observation.initial.ink.length, cursor: observation.initial.cursor, margins: observation.initial.margins },
        before: briefState(observation.before), setup: briefState(observation.setup), edited: briefState(observation.edited),
        blockSeed: briefState(observation.blockBefore),
        controls: { policy: 'graded controls: each is one fenced write over the measured block seed (left and right edges), compared with the independent column model over the whole screen', ...controls },
        attributes: { policy: 'moved cells carry their stored tuples and DECSCA protection while a different pen is active, introduced blanks carry no visual attribute and no protection, '
            + 'and protected text pushed past the edge or deleted disappears (required); the colors of introduced blanks are observed, not required',
            before: briefState(observation.attrBefore), after: briefState(observation.attr), referenceMeasured: attributes.referenceOk, penMeasured: attributes.penOk,
            changedOk: attributes.changedOk, cursorOk: attributes.cursorOk, blankFill: attributes.fill },
        pendingWrap: { policy: 'observed, not graded: whether a column edit at the last column keeps the pending-wrap state is not defined by DEC',
            sequences: controlOf(mode, 'pendingWrap').steps.map(step => step.op.bytes), reading: pendingWrapState(mode, observation.pendingWrap), ...briefState(observation.pendingWrap) },
        history: { policy: 'observed, not graded: column edits never scroll', scrollbackLines: [...new Set(histories)],
            historyLines: contract.roles.reduce((sum, role) => sum + observation[role].history.length, 0) } };
}

/** The shared reverse-video reading of every screen: off, on, or not provable (mixed or unreported). */
function reverseVideoOf(binding) {
    if (binding.every(value => value === false)) return false;
    return binding.includes(true) ? true : null;
}

/** Group the read screens of one case by role. */
export function colEditObservation(contract, screens) {
    if (!Array.isArray(screens) || screens.length !== contract.roles.length) throw new Error('Missing column-edit read captures.');
    const observation = Object.fromEntries(contract.roles.map((role, index) => [role, group(screens[index])]));
    observation.reverseVideo = reverseVideoOf(screens.map(screen => screen.colorBinding.reverseVideo));
    return observation;
}

/** Independent contract grader entry: mirrors the pinned criteria without the bundle. */
export function gradeColEditCase(contract, observation, revision = colEditContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown column-edit contracts revision ${revision}.`);
    if (!observation || contract.roles.some(role => !observation[role] || !Array.isArray(observation[role].ink))) {
        throw new Error(`Missing ${contract.caseId} read captures.`);
    }
    return contract.grade(observation);
}

export { planFeeds as colEditFeeds, planReads as colEditReads } from './graded-plan.mjs';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateColEditFeeds(contract, feeds) {
    validateExactFeeds(contract, feeds, 'contract');
}

/** The fixed fixture facts other modules and tests read. */
export const colEditFixture = Object.freeze({ pinned, leftTexts, rightTexts, blockFeed, regionMargins, regionTop, regionBottom,
    attrTexts, attrSeed, attrProtected, attrPen, attrPark, plainPark, referenceChar, wrapProbe, minimum, clear });

/** The independent column model, exposed so tests can cross-check it against hand-written expectations. */
export const colEditModel = Object.freeze({ kinds, columnOp, shiftRow, gridOf, textsOf, blockGrid, controlsFor,
    attrOpOf, groupsFor, expectedControl, pendingWrapState, attributeState });
