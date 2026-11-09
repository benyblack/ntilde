// Versioned probe contracts for the charset qualification batch:
// charsets.utf8, charsets.dec-special, charsets.dec-line-drawing and charsets.g0-g1-switching.
//
// Pinned terminfo.dev@4.0.0 definitions (bundle SHA-256
// aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6, probeHash 16a690f34c0e). Every termless path
// feeds the exact bytes below, reads stored cells with getCell(0, col).char and the cursor with getCursor(), and runs
// its restoration bytes in a `finally` (SI, ESC ( B, ESC ) B) except charsets.utf8, which has none:
//   charsets.dec-special       feed  SI ESC(B ESC)B ESC[1;1H ESC[2K qqq        read cells 0-2 = q q q
//                              feed  ESC[1;2H                                   read cursor = (1,0)
//                              feed  ESC(0 q ESC(B q                            read cells 0-2 = q U+2500 q
//   charsets.dec-line-drawing  feed  SI ESC(B ESC)B ESC[1;1H ESC[2K jklmqxj     read cells 0-6 = jklmqxj
//                              feed  ESC[1;1H                                   read cursor = (0,0)
//                              feed  ESC(0 jklmqx ESC(B                         read cells 0-6 = U+2518 U+2510 U+250C U+2514 U+2500 U+2502 j
//   charsets.g0-g1-switching   feed  SI ESC(B ESC)B ESC[1;1H ESC[2K AlllZ      read cells 0-4 = A l l l Z
//                              feed  ESC[1;2H                                   read cursor = (1,0)
//                              feed  ESC(B ESC)0 l SO l SI l                    read cells 0-4 = A l U+250C l Z
//   charsets.utf8              feed  ESC[1;1H ESC[2K Axy?Z                      read cells 0-4 = A x y ? Z
//                              feed  ESC[1;2H, then U+00E9                      read cursor = (1,0)
//                              feed  ESC[1;3H                                   read cursor = (2,0)
//                              feed  U+4E16                                     read cells 0-4: [0]=A [1]=U+00E9 [2]=U+4E16 [4]=Z
// A decided verdict needs the control cells (first and last or trailing) to read as seeded; otherwise the probe is
// inconclusive. It never reads the cursor after the characters are written, the continuation cell of the wide
// sample, attributes, other rows or other columns, and it never feeds anything between the setup read and the
// target write that could discriminate designation from invocation.
//
// The pinned terminal pixel/consumed paths for the same ids (capture branch of charsets.dec-special and the
// cursor-position query path of every id) are different code paths: whatever they observe they return pass:false /
// inconclusive ("insufficient-evidence", evidence pixels or consumed); they never carry a verdict. This batch grades ONLY
// the termless criteria and pins the three registrations verbatim, so an upstream change that gave those paths a
// verdict would be refused. Cursor movement or consumed bytes alone never establish a charset mapping here either.
//
// OVERLAP, EXPLICITLY. The three DEC cases share the preamble `SI ESC(B ESC)B`, the DEC Special Graphics designation
// ESC ( 0 and some source characters: `q` (U+2500) appears in dec-special and dec-line-drawing; `l` (U+250C) appears in
// dec-line-drawing and g0-g1-switching. They are distinct registered cases with distinct fixtures, expected cells and
// verdicts. Each plan replays only its own pinned bytes; no case's reads grade another, and no registered case is
// substituted for another. The shared witness rows (see below) are additional controls that every plan carries
// independently, not evidence borrowed across plans.
//
// The contract adds, only ever making the verdict stricter than the pinned one: every non-listed cell of the screen
// stays blank with default storage; the cursor lands exactly where the bytes put it; stored attributes stay neutral;
// the decoded characters and the physical-cell structure (narrow, wide, wide continuation) are exact; and the
// restoration is proven through LOWERCASE source characters that would map to box drawing if DEC graphics stayed
// active. Uppercase markers (`A`, `Z`) cannot prove ASCII restoration because uppercase letters never map under DEC
// Special Graphics, so they are never counted as a restoration witness (a dedicated uppercase control shows exactly that).
//
// PHYSICAL CELLS. readScreen `lines` skips wide-continuation cells and trims the row, so a UTF-16 offset in a line is
// NOT a terminal column once a wide character is present. Cells are therefore derived from the complete physical
// stored-cell-detail runs (the existing opt-in includeCellDetails projection, which covers every physical cell including
// wide continuations and carries the Wide, WideContinuation and HasExtendedText storage flags): each column is a
// continuation (no text), or the next text unit of the row. A cell flagged as extended text makes the rest of its row
// unsegmentable and is reported as an ambiguous cell (null text); an incoherent row (orphan continuation, wide cell
// without its continuation, leftover or missing text) is refused. Attribute strings are not requested: they omit
// continuation cells and cannot carry physical evidence. No normalization is applied anywhere.
//
// SCOPE. Stored, decoded cell contents only: the characters (code points) held in physical cells, the wide/continuation
// structure, the cursor and neutral stored attributes. Not font glyphs or pixels, fallback fonts, keyboard encoding,
// locale behavior, grapheme/width completeness beyond the fixtures' two Unicode samples, or platform equivalence.
import { planFeed, planRead, planFeeds, planReads, validateExactFeeds } from './graded-plan.mjs';
import { expandCellDetails } from './cell-details.mjs';

/** Contracts v1 is the first measured semantics of this batch. */
export const charsetsContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const charsetsSuiteId = 'ntilde-owned-charsets-graded-v1';
/** The nonce prefix binding worker output to this suite. */
export const charsetsNoncePrefix = 'NTILDE_CS_';
/** The measured reset every plan begins with: DECSCNM, DECOM, margins, IRM and SGR off, G0/G1 ASCII with G0 invoked, screen cleared, cursor home. */
export const charsetsReset = '\x1b[?5l\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x0f\x1b(B\x1b)B\x1b[2J\x1b[H';
/** Between controls: charsets restored, SGR off, clear, home. */
export const charsetsClear = '\x0f\x1b(B\x1b)B\x1b[0m\x1b[2J\x1b[H';
/** The pinned restoration bytes (the termless `finally` of the three DEC/switching cases). */
export const charsetsPinnedFinally = '\x0F\x1B(B\x1B)B';

/** The measured geometry the plans need (rows 0-3 carry the fixture and witness rows, so four rows suffice; the pinned probes need far less). */
export const charsetsMinimum = Object.freeze({ rows: 4, cols: 12 });

/** TerminalCellFlags bits this suite reads from the stored cell details. */
export const charsetsFlagBits = Object.freeze({ defaultColors: 0x300, wide: 0x800, continuation: 0x1000, extended: 0x2000 });
const structural = charsetsFlagBits.wide | charsetsFlagBits.continuation | charsetsFlagBits.extended;

/** Validate one observation screen: only stored cell details (no attribute strings, no resolved colors) and coherent rows. */
export function validateCharsetsScreen(screen) {
    if (screen.attributeRuns !== undefined || screen.resolvedColors !== undefined) {
        throw new Error('Charset observations must not carry attribute strings or resolved colors (they omit physical cells).');
    }
    for (let row = 0; row < screen.rows; row++) charsetsRow(screen, row);
}

const detailCache = new WeakMap();
const rowCache = new WeakMap();
/** The expanded physical stored-cell details of one screen, validated once. */
function detailsOf(screen) {
    let value = detailCache.get(screen);
    if (value === undefined) {
        value = expandCellDetails(screen.cellDetails, screen.rows * screen.cols, false);
        detailCache.set(screen, value);
    }
    return value;
}

/** Whether a stored detail has neutral attributes: no attribute flag, default colors, no underline (structure bits ignored). */
const neutralDetail = detail => (detail.flags & ~structural) === charsetsFlagBits.defaultColors && detail.fg === -1 && detail.bg === -1
    && detail.foregroundArgb == null && detail.backgroundArgb == null && detail.underlineStyle === 0
    && detail.underlineColorIndex === -1 && detail.underlineArgb == null;

/** The physical cells of one row, derived from the complete stored cell details and the text of the row. */
export function charsetsRow(screen, row) {
    let rows = rowCache.get(screen);
    if (rows === undefined) { rows = new Map(); rowCache.set(screen, rows); }
    const cached = rows.get(row);
    if (cached !== undefined) return cached;
    const line = screen.lines[row];
    if (typeof line !== 'string') throw new Error(`Missing observed row ${row}.`);
    const details = detailsOf(screen);
    const cells = [];
    let offset = 0, ambiguous = false;
    for (let col = 0; col < screen.cols; col++) {
        const detail = details[row * screen.cols + col];
        const wide = (detail.flags & charsetsFlagBits.wide) !== 0, continuation = (detail.flags & charsetsFlagBits.continuation) !== 0;
        const extended = (detail.flags & charsetsFlagBits.extended) !== 0;
        if (wide && continuation) throw new Error(`Cell ${row},${col} is both wide and a continuation.`);
        if (continuation) {
            if (col === 0 || !cells[col - 1].wide) throw new Error(`Orphan wide continuation at ${row},${col}.`);
            cells.push({ char: '', wide, continuation, extended, neutral: neutralDetail(detail), flags: detail.flags });
            continue;
        }
        if (wide) {
            const next = col + 1 < screen.cols ? details[row * screen.cols + col + 1] : undefined;
            if (next === undefined || (next.flags & charsetsFlagBits.continuation) === 0) throw new Error(`Wide cell ${row},${col} has no continuation.`);
        }
        if (ambiguous || extended) {
            ambiguous = true;
            cells.push({ char: null, wide, continuation, extended, neutral: neutralDetail(detail), flags: detail.flags });
            continue;
        }
        const char = offset < line.length ? line[offset++] : ' ';
        cells.push({ char, wide, continuation, extended, neutral: neutralDetail(detail), flags: detail.flags });
    }
    if (!ambiguous && offset !== line.length) throw new Error(`Observed row ${row} text does not match its physical cells.`);
    rows.set(row, cells);
    return cells;
}

/** One physical cell as the pinned getCell reads it: `char` is the decoded text, '' for a continuation, null when ambiguous. */
export function charsetsCellAt(screen, row, col) {
    if (!Number.isSafeInteger(row) || !Number.isSafeInteger(col) || row < 0 || col < 0 || row >= screen.rows || col >= screen.cols) {
        throw new Error(`Cell ${row},${col} is outside the observed screen.`);
    }
    return charsetsRow(screen, row)[col];
}

/** The storage kind of one cell. */
const kindOf = cell => (cell.continuation ? 'continuation' : cell.wide ? 'wide' : 'narrow');

/** Every cell of the screen that carries text, structure or a non-default stored tuple, in row-major order. */
export function charsetsInk(screen) {
    validateCharsetsScreen(screen);
    const ink = [];
    for (let row = 0; row < screen.rows; row++) {
        for (const [col, cell] of charsetsRow(screen, row).entries()) {
            const blank = cell.char === ' ' && !cell.wide && !cell.continuation && !cell.extended && cell.neutral && cell.flags === charsetsFlagBits.defaultColors;
            if (!blank) ink.push({ row, col, char: cell.char, kind: kindOf(cell), extended: cell.extended, neutral: cell.neutral });
        }
    }
    return ink;
}

/** A group's observation: ink, cursor, geometry and whether any physical cell was ambiguous. */
const group = screen => {
    const ink = charsetsInk(screen);
    return { ink, cursor: [screen.cursorRow, screen.cursorCol], rows: screen.rows, cols: screen.cols,
        ambiguous: ink.some(cell => cell.char === null) };
};
const sameCursor = (state, cursor) => state.cursor[0] === cursor[0] && state.cursor[1] === cursor[1];

/** Placement [row, col, char, kind]. */
const at = (row, col, char, kind = 'narrow') => [row, col, char, kind];
/** The placements of one text laid on a row starting at column `start`; blanks omitted. */
const textAt = (row, text, start = 0) => [...text].flatMap((char, index) => (char === ' ' ? [] : [at(row, start + index, char)]));
const byPosition = (a, b) => (a[0] - b[0]) || (a[1] - b[1]);
const ordered = list => [...list].sort(byPosition);

/** Ink equals exactly these placements: text, storage kind (narrow, wide, continuation), no extended text, neutral attributes. */
function inkIs(state, placements) {
    if (state.ambiguous || state.ink.length !== placements.length) return false;
    return placements.every(([row, col, char, kind], index) => {
        const cell = state.ink[index];
        return cell.row === row && cell.col === col && cell.char === char && cell.kind === kind && !cell.extended && cell.neutral;
    });
}
const stateIs = (state, spec) => inkIs(state, spec.ink) && sameCursor(state, spec.cursor);

/** The shared restoration witness: the source characters of every DEC case, written in lowercase after the fixture. Under DEC
 * Special Graphics each of these maps to box drawing, so ASCII output proves the ASCII sets are active again. */
const witnessSource = 'jklmqxl';
const witnessMapped = '┘┐┌└─│┌';
const witnessAsciiFeed = `\x1b[2;1H${witnessSource}`;
const witnessDecFeed = `\x1b[3;1H\x1b(0${witnessSource}\x1b(B`;
const witnessAfterFeed = `\x1b[4;1H${witnessSource}`;
const witnessRows = Object.freeze({
    witnessAscii: textAt(1, witnessSource),
    witnessDec: textAt(2, witnessMapped),
    witnessAfter: textAt(3, witnessSource),
});

/** Role classes: initial and fixture failures are inconclusive, pinned and behavior mismatches are unsupported, restoration failures are inconclusive. */
const classNote = Object.freeze({
    initial: 'Blank neutral screen control was not measured (the screen was not empty or the cursor was not home)',
});

/** One case: the pinned fixture bytes, then discriminating controls, in a fixed plan. */
function build({ caseId, pinnedFeeds, pinnedFinally, pinnedRoles, pinnedGeometryNote, pinnedFixtureNote, pinnedExpected, pinnedCriterion,
    pinnedReadRoles, fixtureRoles, targetInk, targetCursor, witnessClass, controls, alternatives, overlaps, appearanceScope, terminalFixture }) {
    const specs = [{ role: 'initial', klass: 'initial', feeds: [], ink: [], cursor: [0, 0] }, ...fixtureRoles];
    if (pinnedFinally !== null) specs.push({ role: 'restored', klass: 'restoration', feeds: [pinnedFinally], ink: targetInk, cursor: targetCursor,
        note: 'The pinned restoration bytes changed cells or the cursor' });
    specs.push(
        { role: 'witnessAscii', klass: 'restoration', feeds: [witnessAsciiFeed], ink: ordered([...targetInk, ...witnessRows.witnessAscii]), cursor: [1, 7],
            note: 'Lowercase source characters written after the fixture did not stay ASCII: charset restoration is incomplete' },
        { role: 'witnessDec', klass: witnessClass, feeds: [witnessDecFeed], ink: ordered([...targetInk, ...witnessRows.witnessAscii, ...witnessRows.witnessDec]), cursor: [2, 7],
            note: 'The same lowercase source characters did not map to box drawing under an explicit DEC designation: the restoration witness cannot discriminate' },
        { role: 'witnessAfter', klass: 'restoration', feeds: [witnessAfterFeed], ink: ordered([...targetInk, ...witnessRows.witnessAscii, ...witnessRows.witnessDec, ...witnessRows.witnessAfter]),
            cursor: [3, 7], note: 'Lowercase source characters written after returning from DEC graphics did not stay ASCII: charset restoration is incomplete' },
        ...controls);
    const plan = [planFeed(charsetsReset)];
    for (const spec of specs) {
        for (const feed of spec.feeds) plan.push(planFeed(feed));
        plan.push(planRead('cells'));
    }
    const contract = {
        caseId, probeId: caseId, pinnedPath: 'termless', pinnedFeeds: Object.freeze([...pinnedFeeds]), pinnedFinally, pinnedReadRoles: Object.freeze([...pinnedReadRoles]),
        pinnedGeometryNote, pinnedFixtureNote, pinnedExpected, pinnedCriterion, pinnedRoles: Object.freeze([...pinnedRoles]),
        roles: Object.freeze(specs.map(spec => spec.role)), specs: Object.freeze(specs.map(spec => Object.freeze(spec))),
        alternatives: Object.freeze(alternatives), overlaps: Object.freeze(overlaps), appearanceScope, terminalFixture,
        plan: Object.freeze(plan),
        grade: observation => gradeCharsets(contractOf(caseId), observation),
        observed: observation => briefCharsets(contractOf(caseId), observation),
    };
    return Object.freeze(contract);
}

const contracts = [];
const contractOf = caseId => contracts.find(item => item.caseId === caseId);

const decSpecial = (() => {
    const feeds = ['\x0F\x1B(B\x1B)B\x1B[1;1H\x1B[2Kqqq', '\x1B[1;2H', '\x1B(0q\x1B(Bq'];
    const seed = textAt(0, 'qqq');
    const target = [at(0, 0, 'q'), at(0, 1, '─'), at(0, 2, 'q')];
    return build({
        caseId: 'charsets.dec-special', pinnedFeeds: feeds, pinnedFinally: charsetsPinnedFinally,
        pinnedReadRoles: ['seed', 'cup', 'target'], pinnedRoles: ['target'],
        pinnedGeometryNote: 'Need at least three measured columns for charset controls',
        pinnedFixtureNote: 'ASCII seed was not measured before DEC designation',
        pinnedExpected: 'ASCII q controls flank DEC Special Graphics q mapped to U+2500',
        pinnedCriterion: 'ASCII q cells at columns 0 and 2 flank the cell at column 1, which holds U+2500 after ESC ( 0 q ESC ( B q',
        fixtureRoles: [
            { role: 'seed', klass: 'fixture', feeds: [feeds[0]], ink: seed, cursor: [0, 3], note: 'ASCII seed was not measured before DEC designation' },
            { role: 'cup', klass: 'fixture', feeds: [feeds[1]], ink: seed, cursor: [0, 1], note: 'Target CUP did not reach the DEC graphics cell' },
            { role: 'target', klass: 'pinned', feeds: [feeds[2]], ink: target, cursor: [0, 3] },
        ],
        targetInk: target, targetCursor: [0, 3], witnessClass: 'behavior',
        controls: [
            { role: 'designateG1', klass: 'behavior', feeds: [charsetsClear, '\x1b(B\x1b)0qqq'], ink: textAt(0, 'qqq'), cursor: [0, 3],
                note: 'Designating DEC graphics into G1 changed G0 output without a shift: a bank was confused with a designation' },
            { role: 'upper', klass: 'behavior', feeds: [charsetsClear, '\x1b(0QQQ\x1b(B'], ink: textAt(0, 'QQQ'), cursor: [0, 3],
                note: 'Uppercase source characters changed under DEC graphics: the mapping is not character-specific' },
            { role: 'persist', klass: 'behavior', feeds: [charsetsClear, '\x1b(0qqq'], ink: [at(0, 0, '─'), at(0, 1, '─'), at(0, 2, '─')], cursor: [0, 3],
                note: 'DEC graphics did not stay active across consecutive source characters' },
            { role: 'persistRestore', klass: 'behavior', feeds: ['\x1b(Bqqq'], ink: [at(0, 0, '─'), at(0, 1, '─'), at(0, 2, '─'), ...textAt(0, 'qqq', 3)], cursor: [0, 6],
                note: 'Lowercase q written after ESC ( B did not stay ASCII: restoration through output failed' },
        ],
        alternatives: [
            { name: 'The cells matched the unmodified ASCII seed: the DEC designation had no effect on output', ink: seed },
            { name: 'The cell after ESC ( B mapped too: DEC graphics stayed active (stale charset state)', ink: [at(0, 0, 'q'), at(0, 1, '─'), at(0, 2, '─')] },
            { name: 'Every cell mapped: the ASCII flank controls were overwritten', ink: [at(0, 0, '─'), at(0, 1, '─'), at(0, 2, '─')] },
        ],
        overlaps: [
            { with: 'charsets.dec-line-drawing', shared: 'DEC Special Graphics designation ESC ( 0, restoration ESC ( B and the source character q (U+2500)' },
        ],
        appearanceScope: 'stored decoded cell contents only: the exact ASCII q, U+2500 and ASCII q code points at columns 0-2, narrow storage, the cursor, neutral stored attributes, '
            + 'unaffected cells and lowercase restoration witnesses; not font glyphs, pixels or the pinned pixel capture path',
        terminalFixture: { writes: feeds, restore: charsetsPinnedFinally, demand: 'q U+2500 q at columns 0-2 with ASCII restored', minimum: { rows: 1, cols: 3 },
            note: 'upstream pixel capture (rows 1-4) and cursor-query paths are never verdicts and are not claimed' },
    });
})();

const decLineDrawing = (() => {
    const feeds = ['\x0F\x1B(B\x1B)B\x1B[1;1H\x1B[2Kjklmqxj', '\x1B[1;1H', '\x1B(0jklmqx\x1B(B'];
    const seed = textAt(0, 'jklmqxj');
    const target = ordered([...textAt(0, '┘┐┌└─│'), at(0, 6, 'j')]);
    return build({
        caseId: 'charsets.dec-line-drawing', pinnedFeeds: feeds, pinnedFinally: charsetsPinnedFinally,
        pinnedReadRoles: ['seed', 'cup', 'target'], pinnedRoles: ['target'],
        pinnedGeometryNote: 'Need at least seven measured columns for line-drawing controls',
        pinnedFixtureNote: 'ASCII seed was not measured before line drawing',
        pinnedExpected: 'DEC jklmqx map exactly to ┘┐┌└─│ while trailing ASCII j remains unchanged',
        pinnedCriterion: 'ESC ( 0 jklmqx ESC ( B stores U+2518 U+2510 U+250C U+2514 U+2500 U+2502 at columns 0-5 while the trailing ASCII j at column 6 is unchanged',
        fixtureRoles: [
            { role: 'seed', klass: 'fixture', feeds: [feeds[0]], ink: seed, cursor: [0, 7], note: 'ASCII seed was not measured before line drawing' },
            { role: 'cup', klass: 'fixture', feeds: [feeds[1]], ink: seed, cursor: [0, 0], note: 'Target CUP did not reach the line-drawing start cell' },
            { role: 'target', klass: 'pinned', feeds: [feeds[2]], ink: target, cursor: [0, 6] },
        ],
        targetInk: target, targetCursor: [0, 6], witnessClass: 'behavior',
        controls: [
            { role: 'designateG1', klass: 'behavior', feeds: [charsetsClear, '\x1b(B\x1b)0jklmqx'], ink: textAt(0, 'jklmqx'), cursor: [0, 6],
                note: 'Designating DEC graphics into G1 changed G0 output without a shift: a bank was confused with a designation' },
            { role: 'upper', klass: 'behavior', feeds: [charsetsClear, '\x1b(0JKLMQX\x1b(B'], ink: textAt(0, 'JKLMQX'), cursor: [0, 6],
                note: 'Uppercase source characters changed under DEC graphics: the mapping is not character-specific' },
            { role: 'persist', klass: 'behavior', feeds: [charsetsClear, '\x1b(0jklmqx'], ink: textAt(0, '┘┐┌└─│'), cursor: [0, 6],
                note: 'DEC graphics did not map the six source characters while designated' },
            { role: 'persistRestore', klass: 'behavior', feeds: ['\x1b(Bjklmqx'], ink: [...textAt(0, '┘┐┌└─│'), ...textAt(0, 'jklmqx', 6)], cursor: [0, 12],
                note: 'Lowercase source characters written after ESC ( B did not stay ASCII: restoration through output failed' },
        ],
        alternatives: [
            { name: 'The cells matched the unmodified ASCII seed: the DEC designation had no effect on output', ink: seed },
            { name: 'The trailing ASCII j was overwritten or mapped as well', ink: textAt(0, '┘┐┌└─│┘') },
            { name: 'The six cells carry ASCII source characters unchanged', ink: textAt(0, 'jklmqx') },
        ],
        overlaps: [
            { with: 'charsets.dec-special', shared: 'DEC Special Graphics designation ESC ( 0, restoration ESC ( B and the source character q (U+2500)' },
            { with: 'charsets.g0-g1-switching', shared: 'the source character l (U+250C) and the preamble SI ESC ( B ESC ) B' },
        ],
        appearanceScope: 'stored decoded cell contents only: the six exact DEC line-drawing code points at columns 0-5, the unchanged ASCII j at column 6, narrow storage, the cursor, '
            + 'neutral stored attributes, unaffected cells, a same-source-character ASCII control, an uppercase control and lowercase restoration witnesses; not font glyphs or pixels',
        terminalFixture: { writes: feeds, restore: charsetsPinnedFinally, demand: 'jklmqx stored as six box-drawing code points followed by ASCII j', minimum: { rows: 1, cols: 7 },
            note: 'the pinned cursor-position query path is never a verdict and is not claimed' },
    });
})();

const g01 = (() => {
    const feeds = ['\x0F\x1B(B\x1B)B\x1B[1;1H\x1B[2KAlllZ', '\x1B[1;2H', '\x1B(B\x1B)0l\x0El\x0Fl'];
    const seed = textAt(0, 'AlllZ');
    const target = [at(0, 0, 'A'), at(0, 1, 'l'), at(0, 2, '┌'), at(0, 3, 'l'), at(0, 4, 'Z')];
    const prefix = feeds[0] + feeds[1];
    const row = (second, third, fourth) => [at(0, 0, 'A'), at(0, 1, second), at(0, 2, third), at(0, 3, fourth), at(0, 4, 'Z')];
    return build({
        caseId: 'charsets.g0-g1-switching', pinnedFeeds: feeds, pinnedFinally: charsetsPinnedFinally,
        pinnedReadRoles: ['seed', 'cup', 'target'], pinnedRoles: ['target'],
        pinnedGeometryNote: 'Need at least five measured columns for G0/G1 controls',
        pinnedFixtureNote: 'ASCII seed was not measured before G0/G1 switching',
        pinnedExpected: 'G0 ASCII l, SO-selected G1 DEC l→U+250C, then SI-selected G0 ASCII l, within A/Z controls',
        pinnedCriterion: 'after ESC ( B ESC ) 0 l SO l SI l the cells at columns 1-3 hold l, U+250C, l within the A and Z controls',
        fixtureRoles: [
            { role: 'seed', klass: 'fixture', feeds: [feeds[0]], ink: seed, cursor: [0, 5], note: 'ASCII seed was not measured before G0/G1 switching' },
            { role: 'cup', klass: 'fixture', feeds: [feeds[1]], ink: seed, cursor: [0, 1], note: 'Target CUP did not reach the G0/G1 switching cells' },
            { role: 'target', klass: 'pinned', feeds: [feeds[2]], ink: target, cursor: [0, 4] },
        ],
        targetInk: target, targetCursor: [0, 4], witnessClass: 'restoration',
        controls: [
            { role: 'designateOnly', klass: 'behavior', feeds: [charsetsClear, prefix, '\x1b(B\x1b)0lll'], ink: row('l', 'l', 'l'), cursor: [0, 4],
                note: 'Designating DEC graphics into G1 without invoking it changed the output: a designation was treated as a bank selection' },
            { role: 'shiftOnly', klass: 'behavior', feeds: [charsetsClear, prefix, '\x1b(B\x1b)B\x0Elll\x0F'], ink: row('l', 'l', 'l'), cursor: [0, 4],
                note: 'Invoking G1 while it holds ASCII changed the output: a shift was treated as a designation' },
            { role: 'g0Direct', klass: 'behavior', feeds: [charsetsClear, prefix, '\x1b(0lll\x1b(B'], ink: row('┌', '┌', '┌'), cursor: [0, 4],
                note: 'Designating DEC graphics directly into G0 did not map the characters' },
            { role: 'g1Invoked', klass: 'behavior', feeds: [charsetsClear, prefix, '\x1b(B\x1b)0\x0Elll\x0F'], ink: row('┌', '┌', '┌'), cursor: [0, 4],
                note: 'SO with DEC graphics designated into G1 did not map the characters' },
            { role: 'persistFixture', klass: 'behavior', feeds: [charsetsClear, prefix, feeds[2]], ink: target, cursor: [0, 4],
                note: 'The pinned fixture bytes did not reproduce the pinned target without the restoration bytes' },
            { role: 'persistShift', klass: 'behavior', feeds: ['\x1b[2;1H\x0Ejkl\x0F'], ink: ordered([...target, ...textAt(1, '┘┐┌')]), cursor: [1, 3],
                note: 'The G1 designation did not persist after SI: selecting a bank must not undo the designation' },
        ],
        alternatives: [
            { name: 'The cells matched the unmodified ASCII seed: SO selected no mapping', ink: seed },
            { name: 'The cell after SI mapped too: SI did not return to G0 ASCII', ink: row('l', '┌', '┌') },
            { name: 'The cell before SO mapped: the G1 designation applied without SO', ink: row('┌', '┌', 'l') },
            { name: 'The SO cell stayed ASCII: SO did not select G1', ink: row('l', 'l', 'l') },
        ],
        overlaps: [
            { with: 'charsets.dec-line-drawing', shared: 'the source character l (U+250C) and the preamble SI ESC ( B ESC ) B' },
        ],
        appearanceScope: 'stored decoded cell contents only: the exact A, l, U+250C, l, Z code points at columns 0-4, narrow storage, the cursor, neutral stored attributes, unaffected cells, '
            + 'designation-only, shift-only, G0-direct and G1-invoked controls, persistence of the G1 designation past SI, and lowercase restoration witnesses; not font glyphs or pixels',
        terminalFixture: { writes: feeds, restore: charsetsPinnedFinally, demand: 'l U+250C l between A and Z', minimum: { rows: 1, cols: 5 },
            note: 'the pinned cursor-position query path is never a verdict and is not claimed' },
    });
})();

const utf8 = (() => {
    const feeds = ['\x1B[1;1H\x1B[2KAxy?Z', '\x1B[1;2H', 'é', '\x1B[1;3H', '世'];
    const seed = textAt(0, 'Axy?Z');
    const afterE = [at(0, 0, 'A'), at(0, 1, 'é'), at(0, 2, 'y'), at(0, 3, '?'), at(0, 4, 'Z')];
    const target = [at(0, 0, 'A'), at(0, 1, 'é'), at(0, 2, '世', 'wide'), at(0, 3, '', 'continuation'), at(0, 4, 'Z')];
    return build({
        caseId: 'charsets.utf8', pinnedFeeds: feeds, pinnedFinally: null,
        pinnedReadRoles: ['seed', 'cup1', 'afterE', 'cup2', 'target'], pinnedRoles: ['target'],
        pinnedGeometryNote: 'Need at least five measured columns for Unicode controls',
        pinnedFixtureNote: 'ASCII seed was not measured before Unicode samples',
        pinnedExpected: 'Sampled Unicode characters é and 世 occupy their target cells with ASCII controls intact',
        pinnedCriterion: 'U+00E9 at column 1 and U+4E16 at column 2 with ASCII A at column 0 and Z at column 4',
        fixtureRoles: [
            { role: 'seed', klass: 'fixture', feeds: [feeds[0]], ink: seed, cursor: [0, 5], note: 'ASCII seed was not measured before Unicode samples' },
            { role: 'cup1', klass: 'fixture', feeds: [feeds[1]], ink: seed, cursor: [0, 1], note: 'First target CUP did not reach the Unicode sample cell' },
            { role: 'afterE', klass: 'behavior', feeds: [feeds[2]], ink: afterE, cursor: [0, 2],
                note: 'The two UTF-8 bytes of U+00E9 did not decode to one narrow cell holding U+00E9' },
            { role: 'cup2', klass: 'fixture', feeds: [feeds[3]], ink: afterE, cursor: [0, 2], note: 'Second target CUP did not reach the Unicode sample cell' },
            { role: 'target', klass: 'pinned', feeds: [feeds[4]], ink: target, cursor: [0, 4] },
        ],
        targetInk: target, targetCursor: [0, 4], witnessClass: 'restoration',
        controls: [
            { role: 'wideAlone', klass: 'behavior', feeds: [charsetsClear, 'A世Z'], ink: [at(0, 0, 'A'), at(0, 1, '世', 'wide'), at(0, 2, '', 'continuation'), at(0, 3, 'Z')], cursor: [0, 4],
                note: 'A wide character written in sequence did not occupy a wide cell plus its continuation before the next cell' },
            { role: 'precomposed', klass: 'behavior', feeds: [charsetsClear, 'éZ'], ink: [at(0, 0, 'é'), at(0, 1, 'Z')], cursor: [0, 2],
                note: 'Precomposed U+00E9 was not stored as one narrow code point (it was split, extended or normalized)' },
            { role: 'decomposed', klass: 'observed', feeds: [charsetsClear, 'éZ'], ink: [], cursor: [0, 0],
                note: 'Observed only: a decomposed sequence is recorded exactly as stored, never normalized' },
        ],
        alternatives: [
            { name: 'The cells matched the unmodified ASCII seed: the Unicode characters were not stored', ink: seed },
            { name: 'The wide character did not claim a continuation cell (it was stored narrow)', ink: [at(0, 0, 'A'), at(0, 1, 'é'), at(0, 2, '世'), at(0, 3, '?'), at(0, 4, 'Z')] },
            { name: 'The wide character was stored but the following ASCII cell was overwritten', ink: [at(0, 0, 'A'), at(0, 1, 'é'), at(0, 2, '世', 'wide'), at(0, 3, '', 'continuation')] },
        ],
        overlaps: [],
        appearanceScope: 'stored decoded cell contents only for this fixture\'s two Unicode samples: U+00E9 (one narrow cell) and U+4E16 (a wide cell plus its continuation) with the ASCII A and Z controls, '
            + 'the cursor, neutral stored attributes, unaffected cells and lowercase charset witnesses; physical cells from complete stored cell details, never UTF-16 offsets; '
            + 'not general UTF-8, grapheme, normalization or width completeness, not glyphs or pixels',
        terminalFixture: { writes: feeds, demand: 'U+00E9 at column 1 and U+4E16 at columns 2-3 with A and Z intact', minimum: { rows: 1, cols: 5 },
            note: 'the pinned cursor-position query path is never a verdict and is not claimed' },
    });
})();

contracts.push(utf8, decSpecial, decLineDrawing, g01);
/** The four pinned cases, in suite order: utf8, dec-special, dec-line-drawing, g0-g1-switching. */
export const charsetsContracts = Object.freeze(contracts);
/** Contract case ids in suite order. */
export const charsetsCaseIds = Object.freeze(charsetsContracts.map(contract => contract.caseId));

/** The record of one cell for diagnosis and reporting: ASCII text as is, other code points with their U+ number. */
const hex = char => 'U+' + char.codePointAt(0).toString(16).toUpperCase().padStart(4, '0');
const describe = (cell) => {
    const base = cell.char === null ? `${cell.row},${cell.col},<ambiguous>` : cell.kind === 'continuation' ? `${cell.row},${cell.col},<continuation>`
        : /^[\x20-\x7e]$/.test(cell.char) ? `${cell.row},${cell.col},${cell.char}` : `${cell.row},${cell.col},${cell.char} ${hex(cell.char)}`;
    return `${base}${cell.kind === 'wide' ? ' wide' : ''}${cell.extended ? ' extended' : ''}${cell.neutral ? '' : ' non-neutral-attributes'}`;
};
const placementState = placements => ({ ambiguous: false, ink: placements.map(([row, col, char, kind]) => ({ row, col, char, kind, extended: false, neutral: true })) });

/** Why the target group did not match, as an unsupported note; the diagnosis only picks the words, never the verdict. */
function diagnose(contract, state, spec) {
    for (const alternative of contract.alternatives) if (inkIs(state, alternative.ink)) return alternative.name;
    if (!sameCursor(state, spec.cursor)) return `The cursor is at ${state.cursor.join(',')} instead of ${spec.cursor.join(',')}: the bytes advanced it differently`;
    return 'The decoded cells differ from the contracted code points or cell structure';
}

/** The class walk: fixture and initial mismatches are inconclusive, the pinned target and behavior mismatches are unsupported, restoration mismatches inconclusive. */
function gradeCharsets(contract, observation) {
    const { initial } = observation;
    const first = observation[contract.roles[0]];
    if (first.rows < charsetsMinimum.rows || first.cols < charsetsMinimum.cols) return { outcome: 'inconclusive', note: contract.pinnedGeometryNote };
    const required = contract.specs.filter(spec => spec.klass !== 'observed');
    for (const spec of required) {
        if (observation[spec.role].ambiguous) {
            return { outcome: 'inconclusive', note: `Ambiguous physical cells (extended text) in ${spec.role}: the stored characters cannot be assigned to columns` };
        }
    }
    if (initial.ink.length !== 0 || !sameCursor(initial, [0, 0])) return { outcome: 'inconclusive', note: classNote.initial };
    for (const spec of required.filter(item => item.klass === 'fixture')) {
        if (!stateIs(observation[spec.role], spec)) return { outcome: 'inconclusive', note: spec.note };
    }
    for (const spec of required.filter(item => item.klass === 'pinned')) {
        if (!stateIs(observation[spec.role], spec)) return { outcome: 'unsupported', note: diagnose(contract, observation[spec.role], spec) };
    }
    for (const spec of required.filter(item => item.klass === 'behavior')) {
        if (!stateIs(observation[spec.role], spec)) return { outcome: 'unsupported', note: spec.note };
    }
    for (const spec of required.filter(item => item.klass === 'restoration')) {
        if (!stateIs(observation[spec.role], spec)) return { outcome: 'inconclusive', note: spec.note };
    }
    return { outcome: 'supported' };
}

/** The compact observed record of one group: text rows (twelve columns, `_` blank, `>` continuation), cursor and explicit cells. */
const render = (state, rows) => Array.from({ length: rows }, (_, row) => Array.from({ length: 12 }, (__, col) => {
    const cell = state.ink.find(item => item.row === row && item.col === col);
    return cell === undefined || cell.char === ' ' ? '_' : cell.kind === 'continuation' ? '>' : cell.char === null ? '?' : cell.char;
}).join(''));

/** The observed record: every group's text, cursor and explicit cells, plus the roles that are observed rather than graded. */
function briefCharsets(contract, observation) {
    const rowsOf = state => Math.min(4, Math.max(1, ...state.ink.map(cell => cell.row + 1)));
    const state = item => ({ text: render(item, rowsOf(item)), cursor: item.cursor, cells: item.ink.map(describe) });
    return { geometry: { rows: observation.initial.rows, cols: observation.initial.cols },
        pinnedFeeds: contract.pinnedFeeds, pinnedFinally: contract.pinnedFinally,
        groups: Object.fromEntries(contract.specs.map(spec => [spec.role, { class: spec.klass, feeds: spec.feeds, ...state(observation[spec.role]) }])),
        overlaps: contract.overlaps };
}

/** Group the read screens of one case by role. */
export function charsetsObservation(contract, screens) {
    if (!Array.isArray(screens) || screens.length !== contract.roles.length) throw new Error('Missing charset read captures.');
    return Object.fromEntries(contract.roles.map(role => [role, group(screens[contract.roles.indexOf(role)])]));
}

/** Independent contract grader entry: mirrors the pinned criteria without the bundle. */
export function gradeCharsetsCase(contract, observation, revision = charsetsContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown charsets contracts revision ${revision}.`);
    if (!observation || contract.roles.some(role => !observation[role] || !Array.isArray(observation[role].ink))) {
        throw new Error(`Missing ${contract.caseId} read captures.`);
    }
    return contract.grade(observation);
}

/** Test seam: an observation group built from placements rather than a screen. */
export const charsetsPlacementState = placementState;

export { planFeeds as charsetsFeeds, planReads as charsetsReads } from './graded-plan.mjs';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateCharsetsFeeds(contract, feeds) {
    validateExactFeeds(contract, feeds, 'contract');
}
