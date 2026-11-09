// Versioned probe contracts for the rectangle-editing qualification batch:
// editing.decfra (fill rectangular area), editing.decera (erase rectangular area) and editing.deccra (copy rectangular area).
//
// Pinned terminfo.dev@4.0.0 definitions (bundle SHA-256
// aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6, probeHash 16a690f34c0e). All three termless probes
// read the geometry first (`ctx.cols`, `getScrollback().screenLines`), feed ONE seed string, read the cells (`before`), feed
// ESC[1;1H and read the cursor (`setup`, must be x=0,y=0), feed the edit and read the cells again (`after`):
//   editing.decfra  needs cols >= 6, rows >= 3. Seed ESC[1;1HaaaaaZ ESC[2;1HbbbbbY ESC[3;1HcccccW (rows read as 6-char strings,
//                   must be aaaaaZ|bbbbbY|cccccW). Edit ESC[88;1;1;3;5$x. Controls: column 6 of every row unchanged. Supported iff
//                   columns 1-5 of every row read "XXXXX".
//   editing.decera  needs cols >= 6, rows >= 3. Seed aaaaaZ / bbbbbY / cccccX (rows read as arrays of single characters). Edit
//                   ESC[1;1;3;5$z. Controls: column 6 of every row unchanged. Supported iff columns 1-5 of every row are blank
//                   (every cell "" or " ").
//   editing.deccra  needs cols >= 14, rows >= 6. Seed six 14-column rows (ABCDE......... / FGHIJ......... / two blank dot rows /
//                   .........12345 / .........67890). Edit ESC[1;1;2;5;1;5;10$v (source rows 1-2, columns 1-5, page 1; destination
//                   row 5, column 10, page omitted). Controls: rows 1-4 unchanged and columns 1-9 of rows 5-6 unchanged. Supported
//                   iff row 5 columns 10-14 read "ABCDE" and row 6 columns 10-14 read "FGHIJ".
// A seed that did not read back, or a wrong setup cursor, is inconclusive in every case. The pinned probes never read the
// cursor after the edit, any cell outside the seeded block, attributes, margins, origin mode or the pen.
//
// The pinned terminal pixel path of every case (the second function of each registration) writes ESC[1;1H then the same edit,
// asks for a cursor position report and returns unmeasuredCellResult: pass:false, outcome inconclusive, reason
// insufficient-evidence (or no-response). It never carries a verdict and does not even seed cells, so there is no pixel
// criterion to qualify. This batch grades ONLY the termless criterion; the derivation pins each registration verbatim so a
// future upstream change that gave the pixel path a verdict would be refused.
//
// The contract adds, only ever making the verdict stricter than the pinned one (full-screen ink comparison with an independent
// rectangle model written for this suite): every cell of the whole screen is compared with the model, so cells outside the
// rectangle, an off-by-one border, a wrong coordinate or a smeared overlap are all observed; the cursor must not move; and the
// following controls must hold, each as a separate read group whose seed is measured first:
//   fill:  the single-cell, inclusive-border, omitted, zero and oversized (clipped) rectangles; a combined no-op group (invalid
//          character codes 31, 127, omitted and 0, reversed borders and an extra parameter all change nothing); the rectangle
//          under origin mode (DECOM) and, with DECOM off, across the scrolling margins (margins never clip); insert mode (IRM)
//          on (a fill writes cells, it never shifts them); the live pen: the filled cells' stored tuple equals the tuple of a
//          reference cell printed afterwards under the same pen (so the pen was preserved, the cursor did not move and the
//          fill carried bold/italic, palette-index and literal colors), over attributed seed cells that outside the rectangle
//          keep their stored tuples; a literal-RGB pen.
//   erase: the same bounds, defaults, clip, no-op (reversed borders, an extra parameter), origin and margin controls; erased cells
//          read blank with no stored attribute but a background color (the pen's background fill is OBSERVED, not required);
//          unrelated attributed cells keep their stored tuples; ordinary rectangular erasure ignores DECSCA protection (it is
//          the unconditional erase; DECSERA, the selective erase, is a different case and is not exercised here).
//   copy:  a non-overlapping copy leaves the source intact; overlap in every direction (right, left, down, up and diagonal) behaves
//          as if the whole source were read first (an incremental copy smears and is observed as a mismatch); a full-page copy of
//          the page onto itself and zero defaults change nothing; a destination origin clamped to the last row or column clips the
//          copy; reversed source borders and an extra parameter change nothing; origin mode and margin controls; attributed cells
//          arrive with their stored tuples while a different pen is active and is preserved.
// Margins, origin mode and insert mode are read from the App's own stored buffer state in every read; they are never inferred
// from cursor replies. History is observed only (the reads run on the alternate screen).
//
// PAGES. DECCRA's page parameters (Pps, Ppd) name pages Ntilde does not have. Ntilde accepts page 0/1 (or omitted) and ignores the
// whole command for any other page instead of treating it as the active page. That is an Ntilde policy, not a pinned or DEC
// criterion (the DEC page says a page past the arrangement means the last page); the pages control is therefore OBSERVED and
// recorded, never graded, and no other terminal is judged against it.
//
// Character scope: the pinned fixtures are ASCII single-width cells. Nothing here qualifies wide, combining or Unicode text,
// Latin-1 fill characters, scaled text, the inline-image plane, protected-cell storage or rendered appearance. editing.decsera,
// editing.deccara, editing.decrara, DECSACE and rectangle checksums are different cases and are not substituted.
//
// Naming: the pinned ids are exactly `editing.decfra`, `editing.decera` and `editing.deccra`. Ntilde's own
// ntilde-owned-cell-editing-v1 suite may carry identically named experiments with different bytes; those are Ntilde-owned
// observations, not these fixtures and not benchmark grades.
import { planFeed, planRead, validateExactFeeds } from './graded-plan.mjs';
import { sgrAttrsReset, sgrAttrsPredicates } from './sgr-attrs-contracts.mjs';
import { eraseLineInk } from './erase-line-contracts.mjs';

const { neutral } = sgrAttrsPredicates;

/** Contracts v1 is the first measured semantics of this batch. */
export const rectEditContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const rectEditSuiteId = 'ntilde-owned-rectangle-editing-graded-v1';
/** The nonce prefix binding worker output to this suite. */
export const rectEditNoncePrefix = 'NTILDE_RC_';
/** The measured reset every plan begins with: DECSCNM, DECOM, margins, IRM and SGR off, screen cleared, cursor home. */
export const rectEditReset = sgrAttrsReset;
/** The attribute suite's screen validation, so attribute runs, stored details and resolved colors must agree. */
export { validateEraseLineScreen as validateRectEditScreen } from './erase-line-contracts.mjs';
/** Between groups: margins, origin mode, insert mode, SGR and character protection off, screen cleared, cursor home. */
const clear = '\x1b[r\x1b[?6l\x1b[4l\x1b[0m\x1b[0"q\x1b[2J\x1b[H';

/** The pinned note texts, byte for byte, per mode. */
export const pinnedNotes = Object.freeze({
    fill: Object.freeze({ geometry: 'Need a measured 3x6 fixture', seed: 'DECFRA seed was not measured', cursor: 'DECFRA cursor setup failed',
        controls: 'DECFRA outside control changed', expected: 'DECFRA fills the measured 3x5 area and preserves its measured right flank' }),
    erase: Object.freeze({ geometry: 'Need a measured 3x6 fixture', seed: 'DECERA seed was not measured', cursor: 'DECERA cursor setup failed',
        controls: 'DECERA outside control changed', expected: 'DECERA blanks measured 3x5 cells and preserves their measured right flank' }),
    copy: Object.freeze({ geometry: 'Need a measured 6x14 fixture', seed: 'DECCRA source and destination were not measured', cursor: 'DECCRA cursor setup failed',
        controls: 'DECCRA source or outside control changed', expected: 'DECCRA copies measured 2x5 source cells to row 5 column 10 without changing the source' }),
});

/** The contract's own geometry note. */
export const pinnedGeometryNote = 'Measured geometry cannot fit the rectangle-edit fixture';
/** The measured geometry the controls need (the pinned probes need at most 14 columns and 6 rows). */
const minimum = Object.freeze({ rows: 12, cols: 16 });

/** The pinned seeds, cursor feed and edits, per mode, byte for byte. */
const pinned = Object.freeze({
    fill: Object.freeze({ seed: '\x1b[1;1HaaaaaZ\x1b[2;1HbbbbbY\x1b[3;1HcccccW', edit: '\x1b[88;1;1;3;5$x',
        rows: Object.freeze(['aaaaaZ', 'bbbbbY', 'cccccW']), cursor: [2, 6], after: Object.freeze(['XXXXXZ', 'XXXXXY', 'XXXXXW']) }),
    erase: Object.freeze({ seed: '\x1b[1;1HaaaaaZ\x1b[2;1HbbbbbY\x1b[3;1HcccccX', edit: '\x1b[1;1;3;5$z',
        rows: Object.freeze(['aaaaaZ', 'bbbbbY', 'cccccX']), cursor: [2, 6], after: Object.freeze(['     Z', '     Y', '     X']) }),
    copy: Object.freeze({ seed: '\x1b[1;1HABCDE.........\x1b[2;1HFGHIJ.........\x1b[3;1H..............\x1b[4;1H..............\x1b[5;1H.........12345\x1b[6;1H.........67890',
        edit: '\x1b[1;1;2;5;1;5;10$v',
        rows: Object.freeze(['ABCDE.........', 'FGHIJ.........', '..............', '..............', '.........12345', '.........67890']), cursor: [5, 14],
        after: Object.freeze(['ABCDE.........', 'FGHIJ.........', '..............', '..............', '.........ABCDE', '.........FGHIJ']) }),
});
const cursorFeed = '\x1b[1;1H';

/** The block seed of the controls: seven ten-column rows of distinct characters, so any moved, smeared or misplaced cell is visible. */
const blockTexts = Object.freeze(['ABCDEFGHIJ', 'KLMNOPQRST', 'UVWXYZabcd', 'efghijklmn', 'opqrstuvwx', 'yz01234567', '89!@#$%^&*']);
const blockFeed = blockTexts.map((text, row) => `\x1b[${row + 1};1H${text}`).join('');
/** The scroll-region margins of the origin and margin controls (rows 3-5, zero-based 2-4). */
const regionMargins = '\x1b[3;5r';
const regionTop = 2, regionBottom = 4;
/** The attribute control rows: plain, bold, red-on-blue and plain again. */
const attrSeed = '\x1b[1;1HKEEP-TOP--\x1b[2;1H\x1b[1mBOLD-ROW--\x1b[0m\x1b[3;1H\x1b[31;44mRED-ON-BLU\x1b[0m\x1b[4;1HPLAIN-ROW-';
const attrTexts = Object.freeze(['KEEP-TOP--', 'BOLD-ROW--', 'RED-ON-BLU', 'PLAIN-ROW-']);
/** The pens of the attribute controls and the reference cell character printed under them. */
const pens = Object.freeze({ italicYellowOnGreen: '\x1b[3;33;42m', backgroundBlue: '\x1b[44m', literal: '\x1b[38;2;10;20;30;48;2;40;50;60m' });
const referenceChar = 'P';
/** The parked cursor of the plain controls (row 4, column 6) and of the attribute controls (row 12, column 1). */
const plainPark = Object.freeze({ row: 4, col: 6 });
const attrPark = Object.freeze({ row: 12, col: 1 });
/** The fill character of every control (the pinned fixture fills with X). */
const fillCode = 43;
const fillChar = '+';

// ---------------------------------------------------------------------------------------------------------------------------
// The independent rectangle model. Written for this suite from the DEC definitions; it shares no code with the parser.

/** A one-based inclusive border to a clamped zero-based coordinate: omitted and zero take the default, values past the page are the page edge. */
const border = (value, fallback, extent, offset) => Math.min(Math.max((value > 0 ? value : fallback) - 1 + offset, 0), extent - 1);
/** The rectangle of four raw borders on a page, or null when its clipped borders are reversed. */
function rectOf(params, { rows, cols, origin }, from = 0) {
    const top = border(params[from], 1, rows, origin), left = border(params[from + 1], 1, cols, 0);
    const bottom = border(params[from + 2], rows, rows, origin), right = border(params[from + 3], cols, cols, 0);
    return top <= bottom && left <= right ? { top, left, bottom, right } : null;
}
const validFill = code => (code >= 32 && code <= 126) || (code >= 160 && code <= 255);

/** DECFRA: a code and up to four borders. */
const fillOp = (params, code = fillCode) => Object.freeze({ kind: 'fill', bytes: `\x1b[${[code, ...params].join(';')}$x`,
    apply(grid, page) {
        if (params.length > 4 || !validFill(code)) return;
        const rect = rectOf(params, page);
        if (rect === null) return;
        for (let row = rect.top; row <= rect.bottom; row++) for (let col = rect.left; col <= rect.right; col++) grid[row][col] = String.fromCodePoint(code);
    } });
/** DECERA: up to four borders. */
const eraseOp = params => Object.freeze({ kind: 'erase', bytes: `\x1b[${params.join(';')}$z`,
    apply(grid, page) {
        if (params.length > 4) return;
        const rect = rectOf(params, page);
        if (rect === null) return;
        for (let row = rect.top; row <= rect.bottom; row++) for (let col = rect.left; col <= rect.right; col++) grid[row][col] = ' ';
    } });
/** DECCRA: source borders, source page, destination row and column, destination page. The source is read completely before the destination is written. */
const copyOp = params => Object.freeze({ kind: 'copy', bytes: `\x1b[${params.join(';')}$v`,
    apply(grid, page) {
        if (params.length > 8 || (params[4] ?? 0) > 1 || (params[7] ?? 0) > 1) return;
        const source = rectOf(params, page);
        if (source === null) return;
        const top = border(params[5], 1, page.rows, page.origin), left = border(params[6], 1, page.cols, 0);
        const snapshot = grid.map(row => [...row]);
        for (let row = source.top; row <= source.bottom && top + row - source.top < page.rows; row++) {
            for (let col = source.left; col <= source.right && left + col - source.left < page.cols; col++) {
                grid[top + row - source.top][left + col - source.left] = snapshot[row][col];
            }
        }
    } });

/** Rows of text on a `rows` x `cols` page as a grid of one-character cells. */
const gridOf = (texts, rows, cols) => Array.from({ length: rows }, (_, row) => Array.from({ length: cols }, (_, col) => texts[row]?.[col] ?? ' '));
const textsOf = grid => grid.map(row => row.join(''));

// ---------------------------------------------------------------------------------------------------------------------------
// The control groups. Every group is one fenced write (clear, seed, setup, operations) followed by one read.

/** One block-seed control: the operations run over the seven-row block, optionally under margins, origin mode or insert mode. */
const control = (role, ops, { margins = false, origin = false, insert = false, park = plainPark } = {}) => Object.freeze({
    role, ops, margins, origin, insert, park,
    feed: clear + blockFeed + (margins ? regionMargins : '') + (origin ? '\x1b[?6h' : '') + (insert ? '\x1b[4h' : '')
        + `\x1b[${park.row};${park.col}H` + ops.map(op => op.bytes).join(''),
    cursor: [origin ? regionTop + park.row - 1 : park.row - 1, park.col - 1],
});

const controlsFor = Object.freeze({
    fill: Object.freeze([
        control('one', [fillOp([2, 2, 2, 2])]),
        control('box', [fillOp([2, 3, 4, 6])]),
        control('defaults', [fillOp([])]),
        control('zero', [fillOp([0, 0, 0, 0])]),
        control('clip', [fillOp([5, 7, 999, 999])]),
        control('noop', [fillOp([1, 1, 7, 10], 31), fillOp([1, 1, 7, 10], 127), fillOp([1, 1, 7, 10], 0), fillOp([1, 1, 7, 10], 255 + 1),
            fillOp([4, 3, 2, 6]), fillOp([2, 6, 4, 3]), fillOp([1, 1, 7, 10, 1])]),
        control('origin', [fillOp([1, 2, 2, 4])], { margins: true, origin: true, park: { row: 2, col: 3 } }),
        control('margins', [fillOp([2, 2, 6, 4])], { margins: true, park: { row: 6, col: 3 } }),
        control('insertMode', [fillOp([2, 3, 4, 6])], { insert: true }),
    ]),
    erase: Object.freeze([
        control('one', [eraseOp([2, 2, 2, 2])]),
        control('box', [eraseOp([2, 3, 4, 6])]),
        control('defaults', [eraseOp([])]),
        control('zero', [eraseOp([0, 0, 0, 0])]),
        control('clip', [eraseOp([5, 7, 999, 999])]),
        control('noop', [eraseOp([4, 3, 2, 6]), eraseOp([2, 6, 4, 3]), eraseOp([1, 1, 7, 10, 1])]),
        control('origin', [eraseOp([1, 2, 2, 4])], { margins: true, origin: true, park: { row: 2, col: 3 } }),
        control('margins', [eraseOp([2, 2, 6, 4])], { margins: true, park: { row: 6, col: 3 } }),
    ]),
    copy: Object.freeze([
        control('box', [copyOp([1, 1, 2, 4, 1, 5, 6])]),
        control('right', [copyOp([1, 1, 1, 6, 1, 1, 3])]),
        control('left', [copyOp([2, 3, 2, 8, 1, 2, 1])]),
        control('down', [copyOp([1, 1, 4, 5, 1, 2, 1])]),
        control('up', [copyOp([3, 1, 6, 5, 1, 2, 1])]),
        control('diagonal', [copyOp([1, 1, 3, 5, 1, 2, 2])]),
        control('self', [copyOp([]), copyOp([0, 0, 0, 0, 0, 0, 0, 0]), copyOp([1, 1, 999, 999, 1, 1, 1, 1])]),
        control('cornerRow', [copyOp([1, 1, 2, 3, 1, 999, 1])]),
        control('cornerCol', [copyOp([1, 1, 2, 3, 1, 1, 999])]),
        control('noop', [copyOp([3, 1, 2, 5, 1, 1, 1]), copyOp([1, 5, 2, 4, 1, 1, 1]), copyOp([1, 1, 2, 5, 1, 5, 10, 1, 1])]),
        control('pages', [copyOp([1, 1, 2, 5, 2, 5, 6]), copyOp([1, 1, 2, 5, 1, 5, 6, 2])]),
        control('origin', [copyOp([1, 1, 2, 3, 1, 3, 5])], { margins: true, origin: true, park: { row: 2, col: 3 } }),
        control('margins', [copyOp([2, 2, 6, 4, 1, 3, 1])], { margins: true, park: { row: 6, col: 3 } }),
    ]),
});
/** The protected-text erase control: DECSCA 1 marks the seed, DECSCA 0 ends it; DECERA must still erase the whole rectangle. */
const protectedControl = Object.freeze({ role: 'protected', ops: [eraseOp([2, 3, 4, 6])], margins: false, origin: false, insert: false, park: plainPark,
    feed: clear + '\x1b[1"q' + blockFeed + '\x1b[0"q' + `\x1b[${plainPark.row};${plainPark.col}H` + eraseOp([2, 3, 4, 6]).bytes,
    cursor: [plainPark.row - 1, plainPark.col - 1] });
/** The attribute-control operations, per mode: the pen active during the operation and its bytes. */
const attrOps = Object.freeze({
    fill: Object.freeze({ pen: pens.italicYellowOnGreen, op: fillOp([2, 3, 3, 8]), rect: { top: 1, left: 2, bottom: 2, right: 7 } }),
    erase: Object.freeze({ pen: pens.backgroundBlue, op: eraseOp([2, 3, 3, 8]), rect: { top: 1, left: 2, bottom: 2, right: 7 } }),
    copy: Object.freeze({ pen: pens.italicYellowOnGreen, op: copyOp([1, 1, 4, 10, 1, 7, 1]), rect: { top: 6, left: 0, bottom: 9, right: 9 } }),
});
/** The literal-pen fill control. */
const literalFill = fillOp([2, 3, 4, 6]);

/** Every read group of one mode, in plan order, with the bytes that precede its read. */
function groupsFor(mode) {
    const spec = pinned[mode];
    const groups = [
        { role: 'initial', feeds: [] },
        { role: 'before', feeds: [spec.seed] },
        { role: 'setup', feeds: [cursorFeed] },
        { role: 'edited', feeds: [spec.edit] },
        { role: 'blockBefore', feeds: [clear + blockFeed] },
        ...controlsFor[mode].map(item => ({ role: item.role, feeds: [item.feed] })),
    ];
    if (mode === 'erase') groups.push({ role: protectedControl.role, feeds: [protectedControl.feed] });
    groups.push({ role: 'attrBefore', feeds: [clear + attrSeed] }, { role: 'attr', feeds: [clear + attrSeed + `\x1b[${attrPark.row};${attrPark.col}H` + attrOps[mode].pen + attrOps[mode].op.bytes + referenceChar] });
    if (mode === 'fill') {
        groups.push({ role: 'literal', feeds: [clear + blockFeed + `\x1b[${attrPark.row};${attrPark.col}H` + pens.literal + literalFill.bytes + referenceChar] });
    }
    return groups;
}
const controlOf = (mode, role) => (role === protectedControl.role ? protectedControl : controlsFor[mode].find(item => item.role === role));

/** One case: the pinned fixture plus its discriminating controls, in a fixed plan. */
const case_ = ({ caseId, mode, id }) => {
    const spec = pinned[mode];
    const groups = groupsFor(mode);
    const plan = [planFeed(rectEditReset)];
    for (const group of groups) {
        for (const feed of group.feeds) plan.push(planFeed(feed));
        plan.push(planRead('cells'));
    }
    const notes = pinnedNotes[mode];
    const pinnedFeeds = Object.freeze([spec.seed, cursorFeed, spec.edit]);
    const label = { fill: 'DECFRA (ESC[88;1;1;3;5$x) fills columns 1-5 of rows 1-3 with X and leaves column 6 unchanged, after a setup cursor of 0,0',
        erase: 'DECERA (ESC[1;1;3;5$z) blanks columns 1-5 of rows 1-3 and leaves column 6 unchanged, after a setup cursor of 0,0',
        copy: 'DECCRA (ESC[1;1;2;5;1;5;10$v) copies rows 1-2, columns 1-5 to row 5, column 10 leaving the source and the outside cells unchanged, after a setup cursor of 0,0' }[mode];
    return Object.freeze({
        caseId, probeId: caseId, mode, id, sequence: spec.edit, seedRows: spec.rows,
        roles: Object.freeze(groups.map(group => group.role)), others: Object.freeze(controlsFor[mode].map(item => item.role)),
        pinnedPath: 'termless', beforeRead: 1, setupRead: 2, pinnedRead: 3,
        pinnedExpected: notes.expected, pinnedCriterion: label, pinnedFeeds, plan: Object.freeze(plan),
        terminalFixture: { writes: [...pinnedFeeds], demand: { fill: 'every cell of the rectangle filled with the character; everything else unchanged',
            erase: 'every cell of the rectangle blank; everything else unchanged', copy: 'the source rectangle repeated at the destination; the source unchanged' }[mode],
        minimum: { fill: { rows: 3, cols: 6 }, erase: { rows: 3, cols: 6 }, copy: { rows: 6, cols: 14 } }[mode],
        note: 'upstream pixel path (ESC[1;1H then the edit, then a cursor position report) is never a verdict and is not qualified' },
        appearanceScope: 'stored ASCII cells only: exact contents of the whole screen, the cursor, observed margins, origin and insert mode, stored attribute tuples '
            + 'of the rectangle and the cells around it; the pen, the pinned pixel path, Unicode and wide cells, the image plane, protected-cell storage, '
            + 'other pages and rendered appearance are not claimed',
        grade: observation => gradeRectEdit(contract_(caseId), observation),
        observed: observation => briefRectEdit(contract_(caseId), observation),
    });
};

const contracts = [];
const contract_ = caseId => contracts.find(item => item.caseId === caseId);
contracts.push(
    case_({ caseId: 'editing.decfra', mode: 'fill', id: 'DECFRA' }),
    case_({ caseId: 'editing.decera', mode: 'erase', id: 'DECERA' }),
    case_({ caseId: 'editing.deccra', mode: 'copy', id: 'DECCRA' }),
);
/** The three pinned cases, in suite order. */
export const rectEditContracts = Object.freeze(contracts);
/** Contract case ids in suite order. */
export const rectEditCaseIds = Object.freeze(rectEditContracts.map(contract => contract.caseId));

/** The pinned getCell projection: the stored character of one cell. */
export { eraseLineCellAt as rectEditCellAt } from './erase-line-contracts.mjs';

/** A group's observation: its ink, cursor, geometry and the App's stored buffer state. */
const group = screen => ({ ink: eraseLineInk(screen), cursor: [screen.cursorRow, screen.cursorCol], rows: screen.rows, cols: screen.cols,
    margins: [screen.bufferState.scrollTop, screen.bufferState.scrollBottom], origin: screen.bufferState.originMode,
    insert: screen.bufferState.insertMode, scrollback: screen.bufferState.scrollbackLines, history: screen.history.lines });
const sameCursor = (state, [row, col]) => state.cursor[0] === row && state.cursor[1] === col;

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

/** The expected rows of a control: the block seed with every operation applied in order by the independent model. */
function expectedRows(item, rows, cols) {
    const grid = gridOf(blockTexts, rows, cols);
    const page = { rows, cols, origin: item.origin ? regionTop : 0 };
    for (const op of item.ops) op.apply(grid, page);
    return textsOf(grid);
}
/** The expected rows of the pinned seed on a screen of `rows` x `cols`, and after its edit. */
const pinnedBase = (mode, rows, cols) => textsOf(gridOf(pinned[mode].rows, rows, cols));
const pinnedEdited = (mode, rows, cols) => textsOf(gridOf(pinned[mode].after, rows, cols));

/** Whether a state's margins, origin mode and insert mode match what its group's setup contracts. */
function stateIsContracted(state, item) {
    const margins = item?.margins ? [regionTop, regionBottom] : [0, state.rows - 1];
    return state.margins[0] === margins[0] && state.margins[1] === margins[1] && state.origin === (item?.origin ?? false) && state.insert === (item?.insert ?? false);
}

/** The stored fields of one cell, without its character. */
const tupleKey = cell => JSON.stringify([cell.flags, cell.fgIndex, cell.bgIndex, cell.foregroundArgb, cell.backgroundArgb,
    cell.underlineStyle, cell.underlineColorIndex, cell.underlineArgb, cell.overline, cell.fg, cell.bg]);
const storedKey = cell => JSON.stringify([cell.char, tupleKey(cell)]);
/** A cell whose only stored attribute is a background color (an erased cell). */
const backgroundOnly = cell => cell.char === ' ' && cell.bold === false && cell.dim === false && cell.italic === false && cell.underline === false
    && cell.inverse === false && cell.hidden === false && cell.strikethrough === false && cell.blink === false && cell.overline === false
    && cell.underlineStyle === 0 && cell.fgIndex === -1 && cell.foregroundArgb === null && cell.underlineColorIndex === -1 && cell.underlineArgb === null;
const within = (cell, rect) => cell.row >= rect.top && cell.row <= rect.bottom && cell.col >= rect.left && cell.col <= rect.right;
const byPosition = (a, b) => (a.row - b.row) || (a.col - b.col);

/** Whether the attribute seed read holds the contracted rows, bold row and red-on-blue row. */
function attrSeedOk(before) {
    const byRow = new Map();
    for (const cell of before.ink) byRow.set(cell.row, [...byRow.get(cell.row) ?? [], cell]);
    return before.ink.length === attrTexts.join('').length
        && attrTexts.every((text, row) => (byRow.get(row) ?? []).map(cell => cell.char).join('') === text && (byRow.get(row) ?? []).every((cell, col) => cell.col === col))
        && (byRow.get(1) ?? []).every(cell => cell.bold === true && cell.bgIndex === -1 && cell.fgIndex === -1)
        && (byRow.get(2) ?? []).every(cell => cell.bold === false && cell.fgIndex === 1 && cell.bgIndex === 4)
        && [0, 3].every(row => (byRow.get(row) ?? []).every(neutral)) && sameCursor(before, [3, 10]);
}

const atReference = cell => cell.row === attrPark.row - 1 && cell.col === attrPark.col - 1;
const samePlace = (left, right) => left.row === right.row && left.col === right.col;
/** Whether the observed cells equal the expected ones in order: same places and same stored tuples. */
const sameStoredCells = (actual, expected) => actual.length === expected.length
    && expected.every((cell, index) => samePlace(actual[index], cell) && storedKey(actual[index]) === storedKey(cell));
/** Whether the reference cell carries the pen the attribute control printed it under. */
const penMeasured = (mode, reference) => (mode === 'erase'
    ? reference.bgIndex === 4 && reference.italic === false
    : reference.italic === true && reference.fgIndex === 3 && reference.bgIndex === 2);
const rectCells = rect => (rect.bottom - rect.top + 1) * (rect.right - rect.left + 1);

/** The background an erase left on its cells, in words (observed, never required). */
function backgroundFillNote(inside, reference, referenceOk, rect) {
    if (inside.length === 0) return 'erased cells keep the default background';
    const painted = inside.filter(cell => cell.bgIndex >= 0 || cell.backgroundArgb !== null);
    const same = painted.filter(cell => referenceOk && JSON.stringify(cell.bg) === JSON.stringify(reference.bg));
    const full = rectCells(rect);
    if (painted.length === full && same.length === full) return 'erased cells carry the current background color';
    return painted.length === 0 ? 'erased cells carry no background color' : 'mixed or different background on erased cells';
}

/** The cells a copy must leave: the seed plus its copy shifted by the destination row. */
function copyChange(before, rest, rect) {
    const moved = before.ink.map(cell => ({ ...cell, row: cell.row + rect.top, col: cell.col }));
    return { changedOk: sameStoredCells(rest, [...before.ink, ...moved].sort(byPosition)) };
}

/** A fill or an erase: the cells outside the rectangle keep their tuples, and the cells inside change as the mode requires. */
function areaChange(mode, before, rest, rect, reference, flags) {
    const outside = before.ink.filter(cell => !within(cell, rect));
    const insideNow = rest.filter(cell => within(cell, rect));
    const outsideOk = sameStoredCells(rest.filter(cell => !within(cell, rect)), outside);
    if (mode === 'fill') {
        return { changedOk: outsideOk && flags.penOk && insideNow.length === rectCells(rect)
            && insideNow.every(cell => cell.char === fillChar && tupleKey(cell) === tupleKey(reference)) };
    }
    return { changedOk: outsideOk && insideNow.every(backgroundOnly), fill: backgroundFillNote(insideNow, reference, flags.referenceOk, rect) };
}

/** The attribute control of one mode: its reference cell, the changed rectangle and the untouched cells around it. */
function attributeState(mode, before, after) {
    const { rect } = attrOps[mode];
    const reference = after.ink.find(atReference);
    const referenceOk = reference !== undefined && reference.char === referenceChar;
    const penOk = referenceOk && penMeasured(mode, reference);
    const rest = after.ink.filter(cell => !atReference(cell));
    const { changedOk, fill } = mode === 'copy' ? copyChange(before, rest, rect) : areaChange(mode, before, rest, rect, reference, { referenceOk, penOk });
    return { referenceOk, penOk, changedOk, fill, cursorOk: sameCursor(after, [attrPark.row - 1, attrPark.col]), seedOk: attrSeedOk(before) };
}

/** The literal-pen fill control: the filled cells carry the stored tuple of the reference cell printed under the same literal pen. */
function literalState(base, after) {
    const reference = after.ink.find(cell => cell.row === attrPark.row - 1 && cell.col === attrPark.col - 1);
    const penOk = reference !== undefined && reference.char === referenceChar && reference.foregroundArgb !== null && reference.backgroundArgb !== null
        && reference.fgIndex === -1 && reference.bgIndex === -1;
    const rows = after.rows, cols = after.cols;
    const expected = gridOf(blockTexts, rows, cols);
    literalFill.apply(expected, { rows, cols, origin: 0 });
    const texts = textsOf(expected);
    const filled = [];
    const rest = [];
    for (const cell of after.ink) {
        if (cell.row === attrPark.row - 1 && cell.col === attrPark.col - 1) continue;
        (cell.char === fillChar ? filled : rest).push(cell);
    }
    const placements = place(texts).filter(([row, col]) => !(row === attrPark.row - 1 && col === attrPark.col - 1));
    const filledPlaces = placements.filter(([, , char]) => char === fillChar), restPlaces = placements.filter(([, , char]) => char !== fillChar);
    const filledOk = penOk && filled.length === filledPlaces.length && filled.every((cell, index) => cell.row === filledPlaces[index][0] && cell.col === filledPlaces[index][1]
        && tupleKey(cell) === tupleKey(reference));
    const restOk = rest.length === restPlaces.length && rest.every((cell, index) => cell.row === restPlaces[index][0] && cell.col === restPlaces[index][1]
        && cell.char === restPlaces[index][2] && neutral(cell));
    return { penOk, filledOk, restOk, cursorOk: sameCursor(after, [attrPark.row - 1, attrPark.col]) };
}

/** Why the pinned rows did not match, as an unsupported note; the diagnosis only picks the words, never the verdict. */
function diagnose(contract, state) {
    const { rows, cols } = state;
    const mode = contract.mode;
    const same = texts => inkIs(state, place(textsOf(gridOf(texts, rows, cols))));
    if (same(pinned[mode].rows)) return 'The rows were unchanged after the edit: the rectangle operation was a no-op';
    if (mode === 'fill' && same(['     Z', '     Y', '     W'])) return 'The rectangle was blanked instead of filled';
    if (mode === 'erase' && same([])) return 'The rectangle and its flank were all blanked';
    if (mode === 'copy' && same(['', '', '..............', '..............', '.........ABCDE', '.........FGHIJ'])) return 'The source was cleared: the rectangle was moved instead of copied';
    return 'The edited rows differ from the contracted rectangle operation';
}

const inconclusive = note => ({ outcome: 'inconclusive', note });
const unsupported = note => ({ outcome: 'unsupported', note });
const notControlRoles = ['initial', 'before', 'setup', 'edited', 'blockBefore', 'attrBefore', 'attr', 'literal', 'pages'];
/** What the unsupported attribute control says, by mode. */
const attributeMismatchNotes = Object.freeze({
    fill: 'The filled cells did not carry the live pen, cells outside the rectangle changed, or the cursor or pen drifted, in the attribute control',
    erase: 'Erased cells carried attributes, cells outside the rectangle changed, or the cursor or pen drifted, in the attribute control',
    copy: 'Copied cells lost their stored attributes, the source changed, or the cursor or pen drifted, in the attribute control',
});

/** The first reason the seed, geometry or contracted state was not measured, or null. */
function seedGap(contract, observation) {
    const { before, setup, reverseVideo, initial } = observation;
    const mode = contract.mode;
    if (setup.rows < minimum.rows || setup.cols < minimum.cols) return pinnedGeometryNote;
    if (reverseVideo !== false) return 'DECSCNM reverse video was not proven off, so stored attributes cannot be separated from it';
    const { rows, cols } = setup;
    const badState = contract.roles.find(role => !stateIsContracted(observation[role], controlOf(mode, role)));
    if (badState !== undefined) {
        return `The observed margins, origin mode or insert mode differ from the contracted setup in the ${badState} read, so the edit cannot be attributed`;
    }
    if (initial.ink.length !== 0) return 'Blank neutral screen control was not measured (the screen was not empty)';
    const seeded = inkIs(before, place(pinnedBase(mode, rows, cols))) && sameCursor(before, pinned[mode].cursor);
    if (!seeded) return 'The pinned seed rows were not measured before the edit';
    if (!inkIs(setup, place(pinnedBase(mode, rows, cols))) || !sameCursor(setup, [0, 0])) return 'The pinned cursor setup did not reach row 1 column 1';
    const blockMeasured = inkIs(observation.blockBefore, place(textsOf(gridOf(blockTexts, rows, cols))))
        && sameCursor(observation.blockBefore, [blockTexts.length - 1, blockTexts[0].length]);
    return blockMeasured ? null : 'The block control seed was not measured, so a misplaced or smeared cell cannot be distinguished';
}

/** The first reason a pen reference was not measured, or null. */
function penGap(contract, observation) {
    if (!attrSeedOk(observation.attrBefore)) return 'The attribute control seed was not measured, so stored attributes cannot be distinguished';
    const attributes = attributeState(contract.mode, observation.attrBefore, observation.attr);
    if (!attributes.referenceOk || !attributes.penOk) return 'The reference cell printed under the control pen was not measured, so the pen cannot be attributed';
    if (contract.mode !== 'fill') return null;
    return literalState(null, observation.literal).penOk ? null : 'The literal-color reference cell was not measured, so the literal pen cannot be attributed';
}

/** The first way a measured edit differs from the rectangle model, or null. */
function rectangleMismatch(contract, observation) {
    const mode = contract.mode;
    const { rows, cols } = observation.setup;
    if (!inkIs(observation.edited, place(pinnedEdited(mode, rows, cols)))) return diagnose(contract, observation.edited);
    if (!sameCursor(observation.edited, [0, 0])) return 'The cursor drifted during the edit: it must stay at row 1, column 1';
    for (const role of contract.roles.filter(item => !notControlRoles.includes(item))) {
        const item = controlOf(mode, role);
        if (!inkIs(observation[role], place(expectedRows(item, rows, cols))) || !sameCursor(observation[role], item.cursor)) {
            return `The ${role} control differs from the rectangle model: a border, default, clip, margin, mode, overlap or the cursor was wrong`;
        }
    }
    const attributes = attributeState(mode, observation.attrBefore, observation.attr);
    if (!attributes.changedOk || !attributes.cursorOk) return attributeMismatchNotes[mode];
    const literal = mode === 'fill' ? literalState(null, observation.literal) : null;
    if (literal !== null && (!literal.filledOk || !literal.restOk || !literal.cursorOk)) {
        return 'The filled cells did not carry the literal-color pen, or cells outside the rectangle changed, in the literal control';
    }
    return null;
}

/** The shared control walk, then the exact rectangle-edit verdict. */
function gradeRectEdit(contract, observation) {
    const gap = seedGap(contract, observation) ?? penGap(contract, observation);
    if (gap !== null) return inconclusive(gap);
    const mismatch = rectangleMismatch(contract, observation);
    return mismatch === null ? { outcome: 'supported' } : unsupported(mismatch);
}

/** A group's rows as text over its head columns, `_` for blank. */
const render = (state, indexes, width = 16) => indexes.map(row => Array.from({ length: width }, (_, col) => {
    const cell = state.ink.find(item => item.row === row && item.col === col);
    return cell === undefined || cell.char === ' ' ? '_' : cell.char;
}).join(''));
/** A compact record of one group: its head rows and last row, last-column characters and ink count. */
const briefState = state => ({ text: render(state, Array.from({ length: Math.min(state.rows, 12) }, (_, row) => row)), lastRow: render(state, [state.rows - 1])[0],
    lastColumn: [0, 1, state.rows - 1].map(row => state.ink.find(cell => cell.row === row && cell.col === state.cols - 1)?.char ?? '_').join(''),
    ink: state.ink.length, cursor: state.cursor, margins: state.margins, origin: state.origin, insert: state.insert, scrollback: state.scrollback });

/** What each mode's attribute control requires, in words. */
const attributePolicies = Object.freeze({
    fill: 'filled cells carry the stored tuple of a reference cell printed afterwards under the same pen; cells outside keep their stored tuples (required)',
    erase: 'erased cells carry no stored attribute but a background color and cells outside keep their stored tuples (required); background fill is observed, not required',
    copy: 'copied cells carry the stored tuple of their source cells while a different pen stays active (required)',
});

/** The observed record: every group's text, cursor, margins and ink, the attribute and page observations. */
function briefRectEdit(contract, observation) {
    const mode = contract.mode;
    const { rows, cols } = observation.setup;
    const attributes = attributeState(mode, observation.attrBefore, observation.attr);
    const controls = Object.fromEntries(contract.others.map(role => {
        const item = controlOf(mode, role);
        return [role, { sequences: item.ops.map(op => op.bytes), matchesModel: inkIs(observation[role], place(expectedRows(item, rows, cols))) && sameCursor(observation[role], item.cursor),
            ...briefState(observation[role]) }];
    }));
    const histories = contract.roles.map(role => observation[role].scrollback);
    const record = { geometry: { rows, cols }, reverseVideo: observation.reverseVideo, pinnedSequence: contract.sequence, pinnedFeeds: contract.pinnedFeeds,
        initial: { ink: observation.initial.ink.length, cursor: observation.initial.cursor, margins: observation.initial.margins },
        before: briefState(observation.before), setup: briefState(observation.setup), edited: briefState(observation.edited),
        blockSeed: briefState(observation.blockBefore),
        controls: { policy: 'graded controls: each is one fenced write over the measured block seed, compared with the independent rectangle model over the whole screen', ...controls },
        attributes: { policy: attributePolicies[mode],
        before: briefState(observation.attrBefore), after: briefState(observation.attr), referenceMeasured: attributes.referenceOk, penMeasured: attributes.penOk,
        changedOk: attributes.changedOk, cursorOk: attributes.cursorOk, ...(attributes.fill ? { backgroundFill: attributes.fill } : {}) },
        history: { policy: 'observed, not graded: no pinned criterion requires scrollback behavior and the reads run on the alternate screen', scrollbackLines: [...new Set(histories)],
            historyLines: contract.roles.reduce((sum, role) => sum + observation[role].history.length, 0) } };
    if (mode === 'fill') {
        const literal = literalState(null, observation.literal);
        record.literalPen = { policy: 'graded control: a fill under a literal-RGB pen carries the reference cell tuple', ...briefState(observation.literal), penMeasured: literal.penOk, filledOk: literal.filledOk, restOk: literal.restOk };
    }
    if (mode === 'erase') record.protectedText = { policy: 'graded control: ordinary rectangular erase is unconditional; character protection is not exercised by DECERA and DECSERA is not claimed', ...briefState(observation.protected) };
    if (mode === 'copy') {
        record.pages = { policy: 'observed, not graded: Ntilde accepts pages 0/1 and ignores any other page rather than aliasing it to the active page; not a pinned or DEC criterion',
            sequences: controlsFor.copy.find(item => item.role === 'pages').ops.map(op => op.bytes), ignored: inkIs(observation.pages, place(textsOf(gridOf(blockTexts, rows, cols)))) };
    }
    return record;
}

/** The shared reverse-video reading of every screen: off, on, or not provable (mixed or unreported). */
function reverseVideoOf(binding) {
    if (binding.every(value => value === false)) return false;
    return binding.includes(true) ? true : null;
}

/** Group the read screens of one case by role. */
export function rectEditObservation(contract, screens) {
    if (!Array.isArray(screens) || screens.length !== contract.roles.length) throw new Error('Missing rectangle-edit read captures.');
    const observation = Object.fromEntries(contract.roles.map((role, index) => [role, group(screens[index])]));
    const binding = screens.map(screen => screen.colorBinding.reverseVideo);
    observation.reverseVideo = reverseVideoOf(binding);
    return observation;
}

/** Independent contract grader entry: mirrors the pinned criteria without the bundle. */
export function gradeRectEditCase(contract, observation, revision = rectEditContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown rectangle-edit contracts revision ${revision}.`);
    if (!observation || contract.roles.some(role => !observation[role] || !Array.isArray(observation[role].ink))) {
        throw new Error(`Missing ${contract.caseId} read captures.`);
    }
    return contract.grade(observation);
}

export { planFeeds as rectEditFeeds, planReads as rectEditReads } from './graded-plan.mjs';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateRectEditFeeds(contract, feeds) {
    validateExactFeeds(contract, feeds, 'contract');
}

/** The fixed fixture facts other modules and tests read. */
export const rectEditFixture = Object.freeze({ pinned, cursorFeed, blockTexts, blockFeed, regionMargins, attrTexts, attrSeed, pens, minimum, clear, attrPark, plainPark,
    fillChar, referenceChar });

/** The independent rectangle model, exposed so tests can cross-check it against hand-written expectations. */
export const rectEditModel = Object.freeze({ rectOf, border, fillOp, eraseOp, copyOp, gridOf, textsOf, expectedRows, controlsFor, protectedControl, attrOps, literalFill, groupsFor });
