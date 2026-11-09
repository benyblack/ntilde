// Versioned probe contracts for the attribute-rectangle qualification batch:
// editing.deccara (DECCARA), editing.decrara (DECRARA) and editing.decsace (DECSACE).
//
// Pinned terminfo.dev@4.0.0 definitions (bundle SHA-256
// aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6, probeHash 16a690f34c0e). Only the termless paths carry a verdict;
// the pinned `term` paths write `ESC[1;1H` and the operation and end in a cursor-position query (the fixed "Cursor response does not
// measure edited cells or attributes" inconclusive), so they never carry a verdict and are not qualified.
//   editing.deccara  feeds `ESC[1;1HaaaaaZ ESC[2;1HbbbbbY ESC[3;1HcccccX` and reads the 3x6 cells (text must be aaaaaZ|bbbbbY|cccccX and no
//                    cell inverse); feeds `ESC[1;1H` and reads the cursor (must be 0,0); feeds `ESC[1;1;3;5;7$r` and reads the cells again.
//                    The control is "every character unchanged and column 6 NOT inverse"; only then is it supported iff columns 1..5 of every
//                    row are inverse. Otherwise inconclusive ("DECCARA character or outside attribute control changed").
//   editing.decrara  the same shape with the seed `ESC[7m aaaaa ESC[0m Z` per row (columns 1..5 inverse, column 6 not), the cursor home and
//                    `ESC[1;1;3;5;7$t`; control "characters unchanged and column 6 NOT inverse", supported iff columns 1..5 are NOT inverse.
//   editing.decsace  feeds `ESC[1;1H ESC[2*x`, reads the text and ALWAYS returns the pinned inconclusive ("Sequence consumption does not
//                    measure attribute-change extent", evidence `consumed`). The pinned probe never reads a cell after the extent change and
//                    never applies DECCARA/DECRARA, so it cannot return a verdict whatever the terminal does.
//
// DEFAULT EXTENT. The pinned DECCARA/DECRARA fixtures never select an extent. The VT510 reference makes the DEFAULT the STREAM (Ps 0 or 1 of
// DECSACE): the stream runs from the first named position to the second, so with Pt=1, Pl=1, Pb=3, Pr=5 it covers row 1 through its last
// column (column 6 included), all of row 2, and row 3 through column 5. A terminal that follows the reference therefore changes the "right
// flank" cells the pinned control requires to stay untouched, and the pinned probe returns its inconclusive control note. THE PINNED VERDICT IS
// PRESERVED: it is never promoted. Whether the pinned criterion holds once the rectangle is selected (`ESC[2*x` first) is a separately labeled
// experiment (the same pinned code over the same pinned bytes with that one prefix), recorded beside the pinned result, not a pinned grade.
//
// PINNED VERDICT AND EFFECT CONTRACT ARE DIFFERENT THINGS. The effect contract (ntilde.attr-rect.effect/v1) is an Ntilde observation graded by
// an independent oracle written from the VT510 definitions (DECCARA sets/clears bold, underline, blink and negative image for selectors 0, 1,
// 4, 5, 7, 22, 24, 25, 27 and ignores every other value; DECRARA toggles for 0, 1, 4, 5, 7 and ignores the rest; neither moves the cursor, changes
// the SGR pen, depends on the page margins or changes characters; DECOM offsets the rectangle; the stream is the default extent). It never
// returns supported on stored cells it did not see: every seed is read back first and a seed, cursor, geometry or restoration mismatch is
// inconclusive, an operation mismatch with valid controls is unsupported.
//
// EXTENT IS NOT DIRECTLY OBSERVABLE. No observer API reports the DECSACE setting, so it is inferred from the cells a DECCARA/DECRARA changes
// (stream versus rectangle across first, intermediate and last rows of a multirow fixture). The setting is restored to the stream after every
// rectangle control, and each case ends with a restoration witness that re-applies the stream fixture: a witness that does not match is
// inconclusive. The extent state itself is never read.
//
// What is stored: bold, underline (flag; style and color detail), blink and inverse are stored per cell; italic, faint, hidden, strikethrough,
// overline and the foreground/background colors must survive unchanged. Stored state only: no rendered appearance, no resolved-color claim.
// Not claimed: occupied/unoccupied cells beyond what is observed, DECSCA protection, wide-cell attribute atomicity (unit-tested), xterm's 8/28
// selector extension, selectors outside the VT510 tables, DECRQSS for the extent, and pixels.
import { planFeed, planRead, planFeeds, planReads, validateExactFeeds } from './graded-plan.mjs';
import { sgrAttrsReset } from './sgr-attrs-contracts.mjs';
import { projectSavedResetCells, savedResetInk, validateSavedResetScreen } from './saved-reset-contracts.mjs';

/** Contracts v1 is the first measured semantics of this batch. */
export const attrRectContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const attrRectSuiteId = 'ntilde-owned-attr-rect-graded-v1';
/** The nonce prefix binding worker output to this suite. */
export const attrRectNoncePrefix = 'NTILDE_AR_';
/** Identity of the separately graded effect contract (an Ntilde observation, never a pinned grade). */
export const attrRectEffectContractId = 'ntilde.attr-rect.effect/v1';
/** The measured reset every group starts from: DECSCNM, DECOM, margins, IRM and SGR off, screen cleared, cursor home. It never touches DECSACE. */
export const attrRectReset = sgrAttrsReset;
/** The cell validation the saved-state suite uses: attributes, stored details, resolved colors, buffer state and history must agree. */
export const validateAttrRectScreen = validateSavedResetScreen;
/** The pinned getCell of one cell. */
export const attrRectCellAt = (screen, row, col) => projectSavedResetCells(screen)(row, col);
/** The pinned note texts, byte for byte. */
export const pinnedNotes = Object.freeze({
    'editing.deccara': { geometry: 'Need a measured 3x6 fixture', seed: 'DECCARA character and attribute seed was not measured',
        cursor: 'DECCARA cursor setup failed', control: 'DECCARA character or outside attribute control changed' },
    'editing.decrara': { geometry: 'Need a measured 3x6 fixture', seed: 'DECRARA inverse seed was not measured',
        cursor: 'DECRARA cursor setup failed', control: 'DECRARA character or outside attribute control changed' },
});
export const pinnedDecsaceNote = 'Sequence consumption does not measure attribute-change extent';
const pinnedExpectedText = Object.freeze({
    'editing.deccara': 'DECCARA sets inverse on measured 3x5 cells, preserving text and right flank',
    'editing.decrara': 'DECRARA clears inverse on measured 3x5 inverse cells, preserving text and right flank',
});
/** The geometry the controls need (the pinned probes need a 3x6 fixture). */
const minimum = Object.freeze({ rows: 8, cols: 24 });
export const pinnedGeometryNote = 'Measured geometry cannot fit the attribute-rectangle controls';

// ---- fixture bytes -------------------------------------------------------------------------------------------------------------------
const at = (row, col) => `\x1b[${row + 1};${col + 1}H`;
const ccara = (top, left, bottom, right, ...selectors) => `\x1b[${[top, left, bottom, right, ...selectors].join(';')}$r`;
const rara = (top, left, bottom, right, ...selectors) => `\x1b[${[top, left, bottom, right, ...selectors].join(';')}$t`;
const rect = '\x1b[2*x', stream = '\x1b[0*x';
const pinnedCcaraSeed = '\x1b[1;1HaaaaaZ\x1b[2;1HbbbbbY\x1b[3;1HcccccX';
const pinnedRaraSeed = '\x1b[1;1H\x1b[7maaaaa\x1b[0mZ\x1b[2;1H\x1b[7mbbbbb\x1b[0mY\x1b[3;1H\x1b[7mccccc\x1b[0mX';
const pinnedHome = '\x1b[1;1H';
const pinnedCcaraOp = '\x1b[1;1;3;5;7$r';
const pinnedRaraOp = '\x1b[1;1;3;5;7$t';
const pinnedDecsaceFeed = '\x1b[1;1H\x1b[2*x';

// ---- the independent oracle ----------------------------------------------------------------------------------------------------------
const nine = ['bold', 'dim', 'italic', 'underline', 'inverse', 'hidden', 'strikethrough', 'blink', 'overline'];
const four = ['bold', 'underline', 'blink', 'inverse'];
const selectorBit = Object.freeze({ 1: 'bold', 4: 'underline', 5: 'blink', 7: 'inverse' });
const clearBit = Object.freeze({ 22: 'bold', 24: 'underline', 25: 'blink', 27: 'inverse' });
const key = (row, col) => `${row},${col}`;
const parseKey = text => text.split(',').map(Number);

/** One seeded run: text laid from a position under one SGR string, every cell carrying the named attributes and palette colors. */
const run = (row, col, text, { sgr = '', attrs = [], fg, bg } = {}) => ({ row, col, text, sgr, attrs, fg, bg });
/** Bytes and the independent oracle grid of a set of runs, ending with the cursor parked at `park`. */
function seed(runs, park = [0, 0]) {
    const grid = new Map();
    let bytes = '';
    for (const item of runs) {
        bytes += at(item.row, item.col) + item.sgr + item.text + (item.sgr ? '\x1b[0m' : '');
        for (const [offset, char] of [...item.text].entries()) grid.set(key(item.row, item.col + offset), { char, attrs: [...item.attrs], fg: item.fg, bg: item.bg });
    }
    return { bytes: bytes + at(park[0], park[1]), grid };
}
/** Plain rows from column 0. */
const plainRows = rows => rows.map((text, row) => run(row, 0, text));

/** The expected grid after one DECCARA/DECRARA, from the VT510 definition: `rectangle` selects the extent, selectors fold in order. */
function applyOp(grid, op) {
    const written = new Map();
    for (const place of grid.keys()) {
        const [row, col] = parseKey(place);
        written.set(row, Math.max(written.get(row) ?? 0, col + 1));
    }
    const out = new Map([...grid].map(([place, cell]) => [place, { ...cell, attrs: [...cell.attrs] }]));
    const selectors = op.selectors.length === 0 ? [0] : op.selectors;
    for (let row = op.top - 1; row <= op.bottom - 1; row++) {
        const first = op.rectangle || row === op.top - 1 ? op.left - 1 : 0;
        const last = op.rectangle || row === op.bottom - 1 ? op.right - 1 : Infinity;
        const limit = op.rectangle ? last : Math.min(last, (written.get(row) ?? 0) - 1);
        for (let col = first; col <= limit; col++) {
            const place = key(row, col);
            const cell = out.get(place);
            if (!cell) {
                if (op.rectangle) throw new Error(`Contract authoring error: the rectangle reaches the unoccupied cell ${place}.`);
                out.set(place, { char: ' ', attrs: foldSelectors([], selectors, op.reverse) });
                continue;
            }
            cell.attrs = foldSelectors(cell.attrs, selectors, op.reverse);
        }
    }
    for (const [place, cell] of out) if (cell.char === ' ' && cell.attrs.length === 0 && cell.fg === undefined && cell.bg === undefined) out.delete(place);
    return out;
}
/** Fold a selector list into an attribute list, in order, as DECCARA (set/clear) or DECRARA (toggle). */
function foldSelectors(attrs, selectors, reverse) {
    const set = new Set(attrs);
    for (const selector of selectors) {
        if (selector === 0) {
            for (const name of four) { if (!reverse) set.delete(name); else if (set.has(name)) set.delete(name); else set.add(name); }
        } else if (selectorBit[selector]) {
            const name = selectorBit[selector];
            if (reverse) { if (set.has(name)) set.delete(name); else set.add(name); } else set.add(name);
        } else if (!reverse && clearBit[selector]) set.delete(clearBit[selector]);
    }
    return nine.filter(name => set.has(name));
}

// ---- observation ---------------------------------------------------------------------------------------------------------------------
/** A group's observation: stored ink, cursor, geometry, text lines and the buffer facts a DECCARA could disturb. */
const state = screen => {
    const buffer = screen.bufferState;
    return { ink: savedResetInk(screen), cursor: [screen.cursorRow, screen.cursorCol], rows: screen.rows, cols: screen.cols, text: [...screen.lines],
        alt: buffer.alternateScreen, top: buffer.scrollTop, bottom: buffer.scrollBottom, origin: buffer.originMode, wrap: buffer.autoWrap,
        insert: buffer.insertMode, pending: buffer.pendingWrap, scrollback: buffer.scrollbackLines };
};
const sameCursor = (item, row, col) => item.cursor[0] === row && item.cursor[1] === col;
const inkMap = item => new Map(item.ink.map(cell => [key(cell.row, cell.col), cell]));
const present = cell => nine.filter(name => (name === 'underline' ? cell.underline !== false : cell[name] === true));
/** Whether a stored cell is exactly the oracle cell: text, the nine attributes, palette colors, and no underline or overline color. */
function cellMatches(cell, expected) {
    if (cell.char !== expected.char) return false;
    if (JSON.stringify(present(cell)) !== JSON.stringify([...expected.attrs].sort((a, b) => nine.indexOf(a) - nine.indexOf(b)))) return false;
    const channel = (source, index, argb, wanted) => (wanted === undefined ? source === 'default' && index === -1 && argb === null
        : source === 'palette' && index === wanted && argb === null);
    return channel(cell.fgSource, cell.fgIndex, cell.foregroundArgb, expected.fg) && channel(cell.bgSource, cell.bgIndex, cell.backgroundArgb, expected.bg)
        && cell.underlineColorIndex === -1 && cell.underlineArgb === null;
}
/** Ink equals exactly the oracle grid: the same cells, no other ink anywhere on the screen. */
function inkIs(item, grid) {
    const actual = inkMap(item);
    if (actual.size !== grid.size) return false;
    return [...grid].every(([place, expected]) => actual.has(place) && cellMatches(actual.get(place), expected));
}
/** Stored tuple identity of one cell, for exact restoration comparisons. */
const tuple = cell => JSON.stringify([cell.row, cell.col, cell.char, cell.flags, cell.fgIndex, cell.bgIndex, cell.foregroundArgb, cell.backgroundArgb,
    cell.underlineStyle, cell.underlineColorIndex, cell.underlineArgb, cell.overline]);
const sameStored = (left, right) => JSON.stringify(left.ink.map(tuple)) === JSON.stringify(right.ink.map(tuple));
/** Margins, origin, wrap, insert, pending wrap, scrollback and active screen did not move between two groups. */
const sameBuffer = (left, right) => ['alt', 'top', 'bottom', 'origin', 'wrap', 'insert', 'pending', 'scrollback'].every(field => left[field] === right[field]);
/** The default buffer facts of a reset alternate screen. */
const quiet = item => item.alt && item.top === 0 && item.bottom === item.rows - 1 && !item.origin && item.wrap && !item.insert && !item.pending;
/** The attribute marks of one row as text: `.` none of the four, a letter per attribute (I inverse, B bold, U underline, K blink). */
function marks(item, row, width) {
    const cells = inkMap(item);
    return Array.from({ length: width }, (_, col) => {
        const cell = cells.get(key(row, col));
        const letters = !cell ? '' : (cell.inverse ? 'I' : '') + (cell.bold ? 'B' : '') + (cell.underline !== false ? 'U' : '') + (cell.blink ? 'K' : '');
        return letters.length === 0 ? '.' : letters.length === 1 ? letters : `[${letters}]`;
    }).join('');
}
/** The first `width` characters of one row, `_` for blank. */
const rowText = (item, row, width) => Array.from({ length: width }, (_, col) => {
    const cell = inkMap(item).get(key(row, col));
    return cell === undefined || cell.char === ' ' ? '_' : cell.char;
}).join('');

const inconclusive = note => ({ outcome: 'inconclusive', note });
const unsupported = note => ({ outcome: 'unsupported', note });

/** The shared validity walk: geometry, DECSCNM and the blank neutral screen every case starts from. */
function prelude(observation) {
    const { initial: start, reverseVideo } = observation;
    if (start.rows < minimum.rows || start.cols < minimum.cols) return inconclusive(pinnedGeometryNote);
    if (reverseVideo !== false) return inconclusive('DECSCNM reverse video was not proven off, so stored attributes cannot be separated from it');
    if (start.ink.length !== 0 || !sameCursor(start, 0, 0) || !quiet(start)) {
        return inconclusive('Blank neutral screen control was not measured (the screen was not empty, quiet and homed)');
    }
    return null;
}

// ---- plans ---------------------------------------------------------------------------------------------------------------------------
/** Compile groups (a role and its feeds followed by exactly one read) into one fixed plan; `pinned` maps pinned feed order to roles. */
function compile(groups, { pinnedFeeds, pinnedRoles, rectangleRoles = null }) {
    const plan = [planFeed(attrRectReset)];
    plan.push(planRead('cells')); // the initial group: the reset alone
    for (const group of groups) {
        for (const feed of group.feeds) plan.push(planFeed(feed));
        plan.push(planRead('cells'));
    }
    const roles = ['initial', ...groups.map(group => group.role)];
    if (new Set(roles).size !== roles.length) throw new Error('Invalid attribute-rectangle plan: duplicate roles.');
    const at_ = role => roles.indexOf(role);
    return { plan: Object.freeze(plan), roles: Object.freeze(roles), pinnedFeeds: Object.freeze([...pinnedFeeds]),
        pinnedReads: Object.freeze(pinnedRoles.map(at_)), rectanglePinnedReads: rectangleRoles ? Object.freeze(rectangleRoles.map(at_)) : null };
}
const group = (role, ...feeds) => ({ role, feeds });
/** The oracle op of one scenario, one-based page coordinates. */
const op = (top, left, bottom, right, selectors, { reverse = false, rectangle = false } = {}) => ({ top, left, bottom, right, selectors, reverse, rectangle });
/** The feeds of an operation group: extent prelude, the operation(s), restoration to the stream. */
const opFeeds = (ops, { rectangle }) => [...(rectangle ? [rect] : []), ...ops.map(item => (item.reverse ? rara : ccara)(item.top, item.left, item.bottom, item.right, ...item.selectors)),
    ...(rectangle ? [stream] : [])];
/** Apply a list of ops (all in one extent) to a seed grid. */
const applyAll = (grid, ops, rectangle) => ops.reduce((current, item) => applyOp(current, { ...item, rectangle }), grid);

// Shared seeds.
const plain4 = seed(plainRows(['abcdef', 'ghijkl', 'mnopqr', 'stuvwx']));
const plain6 = seed(plainRows(['abcdef', 'ghijkl', 'mnopqr', 'stuvwx', 'yz0123', '456789']));
const fix4x8 = seed(plainRows(['AAAAAAAA', 'BBBBBBBB', 'CCCCCCCC', 'DDDDDDDD']));
const pinnedC = seed(plainRows(['aaaaaZ', 'bbbbbY', 'cccccX']), [2, 6]);
const pinnedR = seed([...[0, 1, 2].flatMap(row => [run(row, 0, ['aaaaa', 'bbbbb', 'ccccc'][row], { sgr: '\x1b[7m', attrs: ['inverse'] }),
    run(row, 5, ['Z', 'Y', 'X'][row])])], [2, 6]);
const withPen = { sgr: '\x1b[1;44m', attrs: ['bold'], bg: 4 };

// ---- the two operation cases (DECCARA, DECRARA) --------------------------------------------------------------------------------------
/** The pinned effect over the pinned fixture: stream (the DEC default) and rectangle outcomes, seed and cursor. */
function pinnedFixtureResults(contract, observation) {
    const initialGrid = contract.family === 'set' ? pinnedC.grid : pinnedR.grid;
    const reverse = contract.family === 'reverse';
    const pinnedOp = op(1, 1, 3, 5, [7], { reverse });
    return { seedOk: inkIs(observation.seed, initialGrid) && sameCursor(observation.seed, 2, 6) && quiet(observation.seed),
        cursorOk: inkIs(observation.cursor, initialGrid) && sameCursor(observation.cursor, 0, 0),
        streamOk: inkIs(observation.edited, applyOp(initialGrid, { ...pinnedOp, rectangle: false })) && sameCursor(observation.edited, 0, 0) && sameBuffer(observation.edited, observation.seed),
        rectangleOk: inkIs(observation.rect, applyOp(initialGrid, { ...pinnedOp, rectangle: true })) && sameCursor(observation.rect, 0, 0) && sameBuffer(observation.rect, observation.seed),
        witnessOk: inkIs(observation.witness, applyOp(initialGrid, { ...pinnedOp, rectangle: false })) && sameCursor(observation.witness, 0, 0) };
}

/** One generic scenario: a seeded Before read and an operation group read, both graded against the oracle. */
function scenario(spec) {
    return Object.freeze({ ...spec });
}
const setScenarios = Object.freeze([
    scenario({ name: 'pre', before: 'preBefore', after: 'pre', title: 'bold and inverse set over mixed pre-attributed cells',
        seed: seed([run(0, 0, 'a'), run(0, 1, 'b', { sgr: '\x1b[1m', attrs: ['bold'] }), run(0, 2, 'c', { sgr: '\x1b[3;31m', attrs: ['italic'], fg: 1 }),
            run(0, 3, 'd', { sgr: '\x1b[4m', attrs: ['underline'] }), run(0, 4, 'e', { sgr: '\x1b[5m', attrs: ['blink'] }),
            run(0, 5, 'f', { sgr: '\x1b[7;44m', attrs: ['inverse'], bg: 4 }), run(1, 0, 'ghijkl')], [5, 10]),
        ops: [op(1, 1, 2, 6, [1, 7])], rectangle: true, cursor: [5, 10] }),
    scenario({ name: 'clear', before: 'clearBefore', after: 'clear', title: 'selectors 22;24 clear bold and underline only; selector 0 clears the four attributes only',
        seed: seed([0, 1].map(row => run(row, 0, row === 0 ? 'abcdef' : 'ghijkl', { sgr: '\x1b[1;3;4;5;7;31m', attrs: ['bold', 'italic', 'underline', 'blink', 'inverse'], fg: 1 })), [5, 10]),
        ops: [op(1, 1, 1, 3, [22, 24]), op(2, 1, 2, 3, [0])], rectangle: true, cursor: [5, 10] }),
    scenario({ name: 'origin', before: 'originBefore', after: 'origin', title: 'DECOM offsets the rows and the margins do not clip the rectangle',
        seed: plain6, setup: ['\x1b[2;4r', '\x1b[?6h'], ops: [op(2, 2, 5, 4, [7])], absolute: [op(3, 2, 6, 4, [7])], rectangle: true, margins: [1, 3], cursor: [1, 0] }),
]);

/** Build one operation case (DECCARA when `reverse` is false, DECRARA otherwise). */
function operationCase(caseId, reverse) {
    const pinnedSeed = reverse ? pinnedRaraSeed : pinnedCcaraSeed, pinnedOp = reverse ? pinnedRaraOp : pinnedCcaraOp;
    const groups = [group('seed', attrRectReset, pinnedSeed), group('cursor', pinnedHome), group('edited', pinnedOp),
        group('rect', attrRectReset, pinnedSeed, pinnedHome, rect, pinnedOp, stream)];
    const scenarios = [];
    const boundsOp = op(2, 2, 3, 5, [7], { reverse });
    // Shared plain seed validated once; the operation groups that start from it re-lay the same bytes.
    groups.push(group('plainBefore', attrRectReset, plain4.bytes));
    groups.push(group('bounds', attrRectReset, plain4.bytes, ...opFeeds([boundsOp], { rectangle: true })));
    groups.push(group('pen', attrRectReset, plain4.bytes, at(3, 0), '\x1b[1;44m', ...opFeeds([op(1, 1, 2, 3, [7], { reverse })], { rectangle: true }), 'P'));
    if (!reverse) {
        groups.push(group('unknown', attrRectReset, plain4.bytes, ...opFeeds([op(1, 1, 1, 3, [99]), op(2, 1, 2, 3, [1, 99, 4]), op(3, 1, 3, 3, [8])], { rectangle: true })));
        for (const item of setScenarios) {
            groups.push(group(item.before, attrRectReset, item.seed.bytes, ...(item.setup ?? [])));
            groups.push(group(item.after, ...opFeeds(item.ops, { rectangle: item.rectangle })));
            scenarios.push(item);
        }
    } else {
        groups.push(group('mixBefore', attrRectReset, reverseMix.seed.bytes));
        groups.push(group('mix', ...opFeeds([op(1, 1, 2, 6, [7], { reverse })], { rectangle: true })));
        groups.push(group('mixAgain', ...opFeeds([op(1, 1, 2, 6, [7], { reverse })], { rectangle: true })));
        groups.push(group('opsBefore', attrRectReset, operandSeed.bytes));
        groups.push(group('ops', rect, rara(1, 1, 1, 6, 1), rara(2, 1, 2, 6, 0), rara(3, 1, 3, 6), rara(4, 1, 4, 6, 22, 24, 25, 27), rara(5, 1, 5, 6, 1, 1), rara(6, 1, 6, 6, 4, 5), stream));
    }
    groups.push(group('witness', attrRectReset, pinnedSeed, pinnedHome, pinnedOp));
    const compiled = compile(groups, { pinnedFeeds: [pinnedSeed, pinnedHome, pinnedOp], pinnedRoles: ['seed', 'cursor', 'edited'], rectangleRoles: ['seed', 'cursor', 'rect'] });
    const mnemonic = reverse ? 'DECRARA' : 'DECCARA';
    return finish({ caseId, family: reverse ? 'reverse' : 'set', notes: pinnedNotes[caseId], ...compiled,
        pinnedExpected: pinnedExpectedText[caseId], pinnedAlwaysInconclusive: false, pinnedExtentSensitive: true, scenarios,
        pinnedCriterion: reverse
            ? 'three rows of aaaaa/bbbbb/ccccc with columns 1..5 inverse and one plain flank cell each (Z, Y, X): `ESC[1;1;3;5;7$t` leaves every character and the right flank column NOT inverse, and clears inverse on columns 1..5'
            : 'three plain rows aaaaaZ/bbbbbY/cccccX: `ESC[1;1;3;5;7$r` leaves every character and the right flank column NOT inverse, and sets inverse on columns 1..5',
        terminalFixture: { writes: [pinnedHome, pinnedOp, 'cursor query'], demand: 'a cursor-position reply only (always inconclusive)', minimum: { rows: 3, cols: 5 },
            note: 'upstream pixel/query path never measures cells or attributes: it carries no verdict and is not qualified' },
        appearanceScope: `stored per-cell bold, underline, blink and inverse of the ${mnemonic} area, preserved text, other attributes, palette colors and side details, the cursor, the SGR pen and `
            + 'the buffer facts (margins, origin, wrap); the DECSACE extent is inferred behaviorally, never read; no rendered appearance, no resolved-color claim' });
}

/** Common contract assembly: identity and grader hooks. */
function finish(fields) {
    const contract = Object.freeze({ probeId: fields.caseId, pinnedPath: 'termless', effectContract: attrRectEffectContractId, ...fields,
        grade: observation => gradeAttrRect(contract, observation), observed: observation => observedAttrRect(contract, observation) });
    return contract;
}

// ---- grading -------------------------------------------------------------------------------------------------------------------------
/** The extent the pinned fixture revealed: stream (the VT510 default), rectangle, or neither pattern. */
function defaultExtentOf(results) {
    if (results.streamOk) return 'stream';
    return results.seedOk ? 'other' : 'unmeasured';
}

/** A scenario before/after pair graded against the oracle. Returns null when it passes, else an outcome object. */
function gradeScenario(item, observation) {
    const before = observation[item.before], after = observation[item.after];
    if (!inkIs(before, item.seed.grid) || !sameCursor(before, ...item.cursor)) return inconclusive(`The ${item.name} control seed was not measured (${item.title})`);
    if (item.margins && (before.top !== item.margins[0] || before.bottom !== item.margins[1] || !before.origin)) {
        return inconclusive('The origin-mode control margins or DECOM were not measured');
    }
    const expected = applyAll(item.seed.grid, item.absolute ?? item.ops, item.rectangle);
    if (!inkIs(after, expected)) return unsupported(`The ${item.name} control differs from the VT510 definition: ${item.title}`);
    if (!sameCursor(after, ...item.cursor)) return unsupported(`The cursor moved during the ${item.name} control; the operation must not move it`);
    if (!sameBuffer(after, before)) return unsupported(`The ${item.name} control changed the margins, origin or another buffer fact`);
    return null;
}

/** Grade one operation case: preludes and seeds first (inconclusive), then the pinned fixture under both extents and every control (unsupported on a measured mismatch). */
function gradeAttrRect(contract, observation) {
    const early = prelude(observation);
    if (early) return early;
    if (contract.family === 'extent') return gradeExtent(contract, observation);
    const reverse = contract.family === 'reverse';
    const results = pinnedFixtureResults(contract, observation);
    if (!results.seedOk) return inconclusive(contract.notes.seed);
    if (!results.cursorOk) return inconclusive(contract.notes.cursor);
    if (!inkIs(observation.plainBefore, plain4.grid) || !sameCursor(observation.plainBefore, 0, 0)) {
        return inconclusive('The plain-text control seed was not measured, so unaffected cells cannot be distinguished');
    }
    const extent = defaultExtentOf(results);
    const base = { defaultExtent: extent };
    if (!results.streamOk) {
        // The default-extent result differs from the VT510 stream: either the rectangle is the default, or the edit is wrong.
        const pinnedOp = op(1, 1, 3, 5, [7], { reverse });
        const initialGrid = reverse ? pinnedR.grid : pinnedC.grid;
        if (inkIs(observation.edited, applyOp(initialGrid, { ...pinnedOp, rectangle: true }))) {
            return { ...unsupported('The default extent is the rectangle; the VT510 reference makes the stream the default (DECSACE 0 or 1)'), ...base, defaultExtent: 'rectangle' };
        }
        return { ...unsupported(`The pinned fixture changed the wrong cells: ${diagnose(contract, observation.edited, initialGrid, pinnedOp)}`), ...base };
    }
    if (!results.rectangleOk) return { ...unsupported('With the rectangle extent selected the pinned fixture did not change exactly the 3x5 area'), ...base };
    if (!results.witnessOk) return { ...inconclusive('The stream extent was not restored after the rectangle controls (restoration witness differs)'), ...base };
    const bounds = applyOp(plain4.grid, { ...op(2, 2, 3, 5, [7], { reverse }), rectangle: true });
    if (!inkIs(observation.bounds, bounds) || !sameCursor(observation.bounds, 0, 0)) {
        return { ...unsupported('The inclusive-bounds control differs: the rectangle did not cover exactly rows 2..3 and columns 2..5'), ...base };
    }
    const penGrid = applyOp(plain4.grid, { ...op(1, 1, 2, 3, [7], { reverse }), rectangle: true });
    penGrid.set(key(3, 0), { char: 'P', attrs: ['bold'], bg: 4 });
    if (!inkIs(observation.pen, penGrid) || !sameCursor(observation.pen, 3, 1)) {
        return { ...unsupported('The pen/cursor control differs: the operation moved the cursor, changed the current SGR pen, or altered a cell outside the area'), ...base };
    }
    if (!reverse) {
        const unknownGrid = applyAll(plain4.grid, [op(1, 1, 1, 3, [99]), op(2, 1, 2, 3, [1, 99, 4]), op(3, 1, 3, 3, [8])], true);
        if (!inkIs(observation.unknown, unknownGrid) || !sameCursor(observation.unknown, 0, 0)) {
            return { ...unsupported('Selectors outside the VT510 table were not ignored while the defined selectors in the same list applied'), ...base };
        }
    } else {
        const outcome = gradeReverseControls(observation);
        if (outcome) return { ...outcome, ...base };
    }
    for (const item of reverse ? [] : setScenarios) {
        const outcome = gradeScenario(item, observation);
        if (outcome) return { ...outcome, ...base };
    }
    return { outcome: 'supported', ...base };
}

/** The DECRARA-only controls: toggle with its restoration, and the operand table. */
function gradeReverseControls(observation) {
    const { mixBefore, mix, mixAgain, opsBefore, ops } = observation;
    const mixSeed = reverseMix.seed.grid;
    if (!inkIs(mixBefore, mixSeed) || !sameCursor(mixBefore, 5, 10)) return inconclusive('The toggle control seed was not measured, so toggling cannot be distinguished from setting or clearing');
    const toggled = applyOp(mixSeed, { ...op(1, 1, 2, 6, [7], { reverse: true }), rectangle: true });
    if (!inkIs(mix, toggled) || !sameCursor(mix, 5, 10)) return unsupported('DECRARA did not toggle: inverse cells must clear and plain cells must become inverse, with other attributes untouched');
    if (!sameStored(mixAgain, mixBefore) || !sameCursor(mixAgain, 5, 10)) return unsupported('A second identical DECRARA did not restore the area exactly');
    if (!inkIs(opsBefore, operandSeed.grid) || !sameCursor(opsBefore, 5, 10)) return inconclusive('The operand control seed was not measured');
    const expected = [rows => op(1, 1, 1, 6, [1], { reverse: true }), rows => op(2, 1, 2, 6, [0], { reverse: true }), rows => op(3, 1, 3, 6, [], { reverse: true }),
        rows => op(4, 1, 4, 6, [22, 24, 25, 27], { reverse: true }), rows => op(5, 1, 5, 6, [1, 1], { reverse: true }), rows => op(6, 1, 6, 6, [4, 5], { reverse: true })]
        .map(make => make());
    if (!inkIs(ops, applyAll(operandSeed.grid, expected, true)) || !sameCursor(ops, 5, 10)) {
        return unsupported('The DECRARA operand table differs: selector 1 toggles bold, 0 or none toggles all four, 22/24/25/27 are ignored, a repeated selector cancels, 4;5 toggle underline and blink');
    }
    return null;
}

/** Why a pinned fixture result did not match the oracle; the diagnosis only picks the words, never the verdict. */
function diagnose(contract, item, initialGrid, pinnedOp) {
    if (inkIs(item, initialGrid)) return 'nothing changed';
    const opposite = applyOp(initialGrid, { ...pinnedOp, reverse: !pinnedOp.reverse, rectangle: false });
    if (inkIs(item, opposite)) return `the ${contract.family === 'set' ? 'toggle' : 'set'} semantics were applied instead`;
    return 'the changed cells differ from the stream area';
}

// The DECRARA control seeds, built once so the grader and the plan agree byte for byte.
const reverseMix = { seed: seed([run(0, 0, 'a', { sgr: '\x1b[7m', attrs: ['inverse'] }), run(0, 1, 'b'), run(0, 2, 'c', { sgr: '\x1b[7m', attrs: ['inverse'] }), run(0, 3, 'd'),
    run(0, 4, 'e', { sgr: '\x1b[7m', attrs: ['inverse'] }), run(0, 5, 'f'),
    run(1, 0, 'g', { sgr: '\x1b[1m', attrs: ['bold'] }), run(1, 1, 'h', { sgr: '\x1b[3;31m', attrs: ['italic'], fg: 1 }), run(1, 2, 'i', { sgr: '\x1b[4m', attrs: ['underline'] }),
    run(1, 3, 'j', { sgr: '\x1b[5m', attrs: ['blink'] }), run(1, 4, 'k', { sgr: '\x1b[1;7m', attrs: ['bold', 'inverse'] }), run(1, 5, 'l')], [5, 10]) };
const operandStyles = [{ sgr: '\x1b[1;3;31m', attrs: ['bold', 'italic'] }, { sgr: '\x1b[3;4;31m', attrs: ['italic', 'underline'] }, { sgr: '\x1b[3;5;31m', attrs: ['italic', 'blink'] },
    { sgr: '\x1b[3;7;31m', attrs: ['italic', 'inverse'] }, { sgr: '\x1b[3;31m', attrs: ['italic'] }, { sgr: '\x1b[1;3;4;5;7;31m', attrs: ['bold', 'italic', 'underline', 'blink', 'inverse'] }];
const operandSeed = seed([0, 1, 2, 3, 4, 5].flatMap(row => operandStyles.map((style, col) => run(row, col, 'abcdef'[col], { ...style, fg: 1 }))), [5, 10]);

// ---- the DECSACE case ----------------------------------------------------------------------------------------------------------------
const extentOp = (reverse = false) => op(1, 3, 4, 6, [7], { reverse });
function extentCase() {
    const ccaraOp = (...extra) => ccara(1, 3, 4, 6, 7, ...extra), raraOp = rara(1, 3, 4, 6, 7);
    const groups = [group('pinned', pinnedDecsaceFeed), group('fixBefore', stream, attrRectReset, fix4x8.bytes),
        group('stream0', stream, attrRectReset, fix4x8.bytes, ccaraOp()), group('stream1', '\x1b[1*x', attrRectReset, fix4x8.bytes, ccaraOp()),
        group('omitted', rect, '\x1b[*x', attrRectReset, fix4x8.bytes, ccaraOp()), group('rectangle', rect, attrRectReset, fix4x8.bytes, ccaraOp()),
        group('ignored', rect, '\x1b[3*x', attrRectReset, fix4x8.bytes, ccaraOp()),
        group('rectReverse', rect, attrRectReset, fix4x8.bytes, raraOp), group('streamReverse', stream, attrRectReset, fix4x8.bytes, raraOp),
        group('isolation', stream, attrRectReset, fix4x8.bytes, '\x1b[2;3r', '\x1b[?6h', '\x1b[2;3H', '\x1b[1;44m', rect),
        group('isolationPen', 'Q'), group('witness', stream, attrRectReset, fix4x8.bytes, ccaraOp())];
    const compiled = compile(groups, { pinnedFeeds: [pinnedDecsaceFeed], pinnedRoles: ['pinned'] });
    return finish({ caseId: 'editing.decsace', family: 'extent', notes: {}, ...compiled, pinnedAlwaysInconclusive: true, pinnedExtentSensitive: false, scenarios: [],
        pinnedExpected: 'DECSACE changes the attribute-change extent',
        pinnedCriterion: '`ESC[1;1H ESC[2*x` is consumed and the text read: always the pinned inconclusive (the probe never applies DECCARA/DECRARA and never reads an attribute); '
            + 'separately identified effect contract: the stream (default, 0, 1, omitted) versus rectangle (2) cells changed by the same DECCARA/DECRARA across the first, intermediate and last rows',
        terminalFixture: { writes: [], demand: 'none (always inconclusive)', minimum: { rows: 1, cols: 1 },
            note: 'the upstream terminal path is a `not attempted` placeholder that never carries a verdict' },
        appearanceScope: 'the stored cells a DECCARA/DECRARA changes under each DECSACE value across a four-row eight-column fixture (stream: first row from Pl, whole middle rows, last row through Pr; '
            + 'rectangle: Pl..Pr on every row), independence from margins, origin mode, the cursor and the SGR pen, and restoration of the stream; the extent setting itself is never read' });
}

function gradeExtent(contract, observation) {
    const { fixBefore } = observation;
    if (!inkIs(fixBefore, fix4x8.grid) || !sameCursor(fixBefore, 0, 0)) return inconclusive('The multirow extent fixture was not measured, so stream and rectangle cannot be distinguished');
    const base = { defaultExtent: 'stream' };
    const streamGrid = applyOp(fix4x8.grid, { ...extentOp(), rectangle: false }), rectGrid = applyOp(fix4x8.grid, { ...extentOp(), rectangle: true });
    const streamReverse = applyOp(fix4x8.grid, { ...extentOp(true), rectangle: false }), rectReverse = applyOp(fix4x8.grid, { ...extentOp(true), rectangle: true });
    if (inkIs(observation.stream0, rectGrid) && !inkIs(observation.stream0, streamGrid)) {
        return { ...unsupported('DECSACE 0 left the rectangle extent in force: the cells changed by DECCARA are the rectangle, not the stream'), ...base };
    }
    const checks = [['stream0', streamGrid, 'DECSACE 0 (stream) did not give the stream extent'], ['stream1', streamGrid, 'DECSACE 1 did not give the stream extent'],
        ['omitted', streamGrid, 'DECSACE with no parameter did not return to the stream extent'], ['rectangle', rectGrid, 'DECSACE 2 did not give the rectangle extent'],
        ['rectReverse', rectReverse, 'DECRARA ignored the rectangle extent'], ['streamReverse', streamReverse, 'DECRARA ignored the stream extent']];
    for (const [role, grid, note] of checks) {
        if (!inkIs(observation[role], grid) || !sameCursor(observation[role], 0, 0)) return { ...unsupported(`${note}; the changed cells differ across the first, intermediate and last rows`), ...base };
    }
    const { isolation, isolationPen } = observation;
    if (!inkIs(isolation, fix4x8.grid) || isolation.top !== 1 || isolation.bottom !== 2 || !isolation.origin || !sameCursor(isolation, 2, 2)) {
        return { ...unsupported('Selecting an extent changed the cells, margins, origin mode or cursor: the extent must be independent of them'), ...base };
    }
    const penGrid = new Map(fix4x8.grid);
    penGrid.set(key(2, 2), { char: 'Q', attrs: ['bold'], bg: 4 });
    if (!inkIs(isolationPen, penGrid) || !sameCursor(isolationPen, 2, 3)) {
        return { ...unsupported('Selecting an extent changed the current SGR pen: the next character was not written with the pen set before it'), ...base };
    }
    if (!inkIs(observation.witness, streamGrid) || !sameCursor(observation.witness, 0, 0)) {
        return { ...inconclusive('The stream extent was not restored after the rectangle controls (restoration witness differs)'), ...base };
    }
    return { outcome: 'supported', ...base };
}

// ---- the observed record -------------------------------------------------------------------------------------------------------------
/** The compact observed record of one group: marks of its first rows, its text, cursor and buffer facts. */
const groupRecord = (item, rows, width) => ({ text: Array.from({ length: rows }, (_, row) => rowText(item, row, width)),
    marks: Array.from({ length: rows }, (_, row) => marks(item, row, width)), cursor: item.cursor, inkCells: item.ink.length,
    buffer: { top: item.top, bottom: item.bottom, origin: item.origin, alt: item.alt } });
function observedAttrRect(contract, observation) {
    const common = { geometry: { rows: observation.initial.rows, cols: observation.initial.cols }, reverseVideo: observation.reverseVideo,
        initial: { ink: observation.initial.ink.length, cursor: observation.initial.cursor }, extentObservation: 'the DECSACE setting is not readable; it is inferred from the cells changed and restored to the stream after every rectangle control' };
    if (contract.family === 'extent') {
        const record = role => groupRecord(observation[role], 4, 8);
        return { ...common, pinned: { cursor: observation.pinned.cursor, inkCells: observation.pinned.ink.length, text: observation.pinned.text.map(line => line.trim()).filter(Boolean) },
            fixture: record('fixBefore'), stream0: record('stream0'), stream1: record('stream1'), omitted: record('omitted'), rectangle: record('rectangle'),
            ignoredValue: { policy: 'observed, not graded: an undefined DECSACE value (3) after the rectangle selection', ...record('ignored'),
                keptRectangle: inkIs(observation.ignored, applyOp(fix4x8.grid, { ...extentOp(), rectangle: true })) },
            rectReverse: record('rectReverse'), streamReverse: record('streamReverse'),
            isolation: { ...groupRecord(observation.isolation, 4, 8), pen: groupRecord(observation.isolationPen, 4, 8) }, witness: record('witness') };
    }
    const results = pinnedFixtureResults(contract, observation);
    const record = role => groupRecord(observation[role], 4, 6);
    const base = { ...common, seed: record('seed'), cursor: { cursor: observation.cursor.cursor }, pinnedDefaultExtent: { ...record('edited'), matchesStream: results.streamOk },
        rectangleExperiment: { policy: 'labeled experiment, not a pinned grade: the pinned bytes after `ESC[2*x`; the stream is restored with `ESC[0*x` afterwards', ...record('rect'), matchesRectangle: results.rectangleOk },
        plain: record('plainBefore'), bounds: record('bounds'), pen: record('pen'), witness: { ...record('witness'), matchesStream: results.witnessOk } };
    if (contract.family === 'reverse') {
        return { ...base, toggle: { before: groupRecord(observation.mixBefore, 2, 6), after: groupRecord(observation.mix, 2, 6), second: groupRecord(observation.mixAgain, 2, 6),
            restored: sameStored(observation.mixAgain, observation.mixBefore) }, operands: { before: groupRecord(observation.opsBefore, 6, 6), after: groupRecord(observation.ops, 6, 6) } };
    }
    return { ...base, unknownSelectors: record('unknown'),
        controls: Object.fromEntries(setScenarios.map(item => [item.name, { before: groupRecord(observation[item.before], 6, 6), after: groupRecord(observation[item.after], 6, 6),
            graded: gradeScenario(item, observation) === null }])) };
}

const contracts = Object.freeze([operationCase('editing.deccara', false), operationCase('editing.decrara', true), extentCase()]);
/** The three pinned cases, in suite order. */
export const attrRectContracts = contracts;
/** Contract case ids in suite order. */
export const attrRectCaseIds = Object.freeze(contracts.map(contract => contract.caseId));

/** Group the read screens of one case by role. */
export function attrRectObservation(contract, screens) {
    if (!Array.isArray(screens) || screens.length !== contract.roles.length) throw new Error('Missing attribute-rectangle read captures.');
    const observation = Object.fromEntries(contract.roles.map((role, index) => [role, state(screens[index])]));
    const binding = screens.map(screen => screen.colorBinding.reverseVideo);
    observation.reverseVideo = null;
    if (binding.every(value => value === false)) observation.reverseVideo = false;
    else if (binding.includes(true)) observation.reverseVideo = true;
    return observation;
}

/** The observed record of one case. */
export const attrRectObserved = (contract, observation) => contract.observed(observation);

/** Independent contract grader entry: mirrors the VT510 definitions without the bundle. */
export function gradeAttrRectCase(contract, observation, revision = attrRectContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown attribute-rectangle contracts revision ${revision}.`);
    if (!observation || contract.roles.some(role => !observation[role] || !Array.isArray(observation[role].ink))) {
        throw new Error(`Missing ${contract.caseId} read captures.`);
    }
    return contract.grade(observation);
}

export { planFeeds as attrRectFeeds, planReads as attrRectReads } from './graded-plan.mjs';
/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateAttrRectFeeds(contract, feeds) {
    validateExactFeeds(contract, feeds, 'contract');
}
/** The text the pinned DECSACE probe's getText sees: the journaled screen lines. */
export const attrRectScreenText = screen => screen.lines.join('\n');
/** The independent oracle, exposed so synthetic evidence can be generated from the definition rather than from the App. */
export const attrRectOracle = Object.freeze({ seed, applyOp, plain4, plain6, fix4x8, pinnedC, pinnedR, reverseMix, operandSeed, setScenarios, extentOp });
