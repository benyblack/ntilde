// Versioned probe contract for the rectangle-checksum qualification batch: editing.decrqcra (DECRQCRA, request checksum of
// rectangular area).
//
// Pinned terminfo.dev@4.0.0 definition (bundle SHA-256 aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6,
// probeHash 16a690f34c0e). The termless probe feeds ONE seed string (ABCDE ESC[1;1H), then feeds ESC[1;1;1;1;1;5*y and captures
// what the terminal sends back. checksumResult grades it: supported iff the reply contains a framed DCS 1 ! ~ hhhh ST (four hex
// digits, request id 1); otherwise inconclusive (no-response or invalid-reply). The pinned note says it itself: the checksum
// arithmetic is NOT verified. The pinned terminal path asks ESC[1;1;1;1;1;1*y through queryOutcome with the same regex; that path
// belongs to the stock collector, and this suite measures the same spelling as a graded control.
//
// Ntilde answers DECRQCRA only while the AllowRectangleChecksumReport setting is on (off by default, like xterm's window-ops
// gate, because a one-cell checksum names that cell's character). The measured run turns it on in its initial settings; the
// receipt binds that, and the default-off silence stays witnessed by the exact-query suite of every run without the setting.
//
// The effect contract (docs/qa/terminfo/rectangle-checksum.md) is only ever stricter than the pinned criterion. An independent
// checksum model written for this suite recomputes the exact reply of every admitted request from the parent-journaled stored
// cells of the screen the request saw: each cell's character (a blank is a space) plus bold 0x80, blink 0x40, reverse 0x20 and
// underline 0x10, summed over the rectangle, negated modulo 2^16, as four uppercase hex digits after the echoed request id
// (omitted is 0). The rectangle takes DECERA's defaults, DECOM's row offset and page clipping, and an inverted rectangle sums
// nothing. Every request is one fenced exchange followed by one read, so the following controls are each one exact reply:
//   pinned:     the pinned 1x5 request over the pinned seed;
//   terminal:   the pinned terminal-path 1x1 spelling;
//   attributes: one-cell requests over cells written bold, blink, reverse, underline (single, curly and double) and all four,
//               a space written in reverse, and two row requests whose italic, faint, hidden, strikethrough, overline and colored
//               cells must add nothing;
//   rectangle:  the whole page (omitted borders, zero borders), partly omitted borders, oversized borders (clipped to the page),
//               an ignored page parameter, the largest request id, an omitted request id and two inverted rectangles (0000);
//   identity:   a private leader, a seventh parameter, a colon subparameter, the $ and SP * intermediates and a > leader must
//               all stay unanswered;
//   origin:     DECOM with margins 3-6 offsets the rows; borders past the region still clip to the page, not the margins.
// A request must never change the screen, the cursor, the margins or the modes: every report read must equal the read before it.
//
// Like every graded suite, the plan runs on the alternate screen the shared bind enters; which page answers is not a control here
// (unit tests cover the alternate screen). Character scope: stored ASCII single-width cells only. Wide, combining and supplementary characters are covered by unit tests,
// not qualified here. Rendered appearance, the persistent (mux) path and the default-off gate itself are not claimed by this
// suite. modes.left-right-margin is a different case and is not substituted.
import { planFeed, planRead, validateExactFeeds } from './graded-plan.mjs';
import { sgrAttrsReset, sgrAttrsCellAt, sgrAttrsExpand } from './sgr-attrs-contracts.mjs';
import { selectiveEraseInk, validateSelectiveEraseScreen } from './selective-erase-contracts.mjs';

/** Contracts v1 is the first measured semantics of this batch. */
export const rectChecksumContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const rectChecksumSuiteId = 'ntilde-owned-rectangle-checksum-graded-v1';
/** The nonce prefix binding worker output to this suite. */
export const rectChecksumNoncePrefix = 'NTILDE_CK_';
/** The measured reset every plan begins with: protection, DECSCNM, DECOM, margins, IRM and SGR off, screen cleared, cursor home. */
export const rectChecksumReset = '\x1b[0"q' + sgrAttrsReset;
/** Checksum screens carry stored attributes, per-cell protection, buffer state and history (the selective-erasure screen). */
export const validateRectChecksumScreen = validateSelectiveEraseScreen;
/** The stored projection of one cell. */
export const rectChecksumCellAt = sgrAttrsCellAt;

/** One plan step: write exact request bytes through the owned fence and capture the reply before the ordering barrier. */
export const planCapture = bytes => ({ feed: bytes, capture: true });

/** The pinned fixture, byte for byte, and the pinned criterion texts. */
export const pinned = Object.freeze({
    seed: 'ABCDE\x1b[1;1H', request: '\x1b[1;1;1;1;1;5*y', terminalRequest: '\x1b[1;1;1;1;1;1*y',
    note: 'Checks framed reply and request id; checksum arithmetic is not verified',
    expected: 'Complete four-digit checksum reply for request 1',
    pattern: /\x1bP1!~[0-9A-Fa-f]{4}\x1b\\/,
});

/** The measured geometry the controls need. */
const minimum = Object.freeze({ rows: 13, cols: 40 });

/** The xterm attribute weights the contract adds to a cell's character. */
const weights = Object.freeze({ bold: 0x80, blink: 0x40, inverse: 0x20, underline: 0x10 });

/** The attribute seed: row 3, one character per odd column, each written under one rendition and reset after it. */
const attrRow = 3;
const attrCells = Object.freeze([
    { col: 1, char: 'P', sgr: '0', expect: { } },
    { col: 3, char: 'B', sgr: '1', expect: { bold: true } },
    { col: 5, char: 'K', sgr: '5', expect: { blink: true } },
    { col: 7, char: 'R', sgr: '7', expect: { inverse: true } },
    { col: 9, char: 'U', sgr: '4', expect: { underline: 'single' } },
    { col: 11, char: 'C', sgr: '4:3', expect: { underline: 'curly' } },
    { col: 13, char: 'D', sgr: '4:2', expect: { underline: 'double' } },
    { col: 15, char: 'X', sgr: '1;5;7;4', expect: { bold: true, blink: true, inverse: true, underline: 'single' } },
    { col: 17, char: ' ', sgr: '7', expect: { inverse: true } },
    { col: 19, char: 'I', sgr: '3', expect: { italic: true } },
    { col: 21, char: 'F', sgr: '2', expect: { dim: true } },
    { col: 23, char: 'H', sgr: '8', expect: { hidden: true } },
    { col: 25, char: 'S', sgr: '9', expect: { strikethrough: true } },
    { col: 27, char: 'O', sgr: '53', expect: { overline: true } },
    { col: 29, char: 'r', sgr: '31', expect: { fgIndex: 1 } },
    { col: 31, char: 'g', sgr: '48;2;10;20;30', expect: { rgbBackground: true } },
    { col: 33, char: 'p', sgr: '38;5;200', expect: { fgIndex: 200 } },
].map(Object.freeze));
const attrFeed = attrCells.map(cell => `\x1b[${attrRow};${cell.col}H\x1b[${cell.sgr}m${cell.char}\x1b[0m`).join('') + '\x1b[1;1H';
/** The renditions every attribute seed cell must read back with; everything not named must be off. */
const flagNames = Object.freeze(['bold', 'dim', 'italic', 'inverse', 'hidden', 'strikethrough', 'blink', 'overline']);

/** The margins and origin mode of the origin control (rows 3-6, zero-based 2-5). */
const originFeed = '\x1b[3;6r\x1b[?6h';
const originTop = 2, originBottom = 5;

/** One checksum request: its parameters (undefined omits one) and bytes, or the exact bytes of a non-admitted spelling. */
const ask = (role, params, spelling = null) => Object.freeze({ role, params: params ? Object.freeze(params) : null,
    admitted: spelling === null, bytes: spelling ?? `\x1b[${params.map(value => value ?? '').join(';')}*y` });

/** Every read group, in plan order. A cells group writes feeds and reads; a report group asks one request and reads. */
const groups = Object.freeze([
    { role: 'initial', feeds: [] },
    { role: 'seed', feeds: [pinned.seed] },
    { role: 'pinned', request: ask('pinned', [1, 1, 1, 1, 1, 5]) },
    { role: 'terminal', request: ask('terminal', [1, 1, 1, 1, 1, 1]) },
    { role: 'attrSeed', feeds: [attrFeed] },
    ...[['bold', 3], ['blink', 5], ['reverse', 7], ['underline', 9], ['curly', 11], ['double', 13], ['allFour', 15], ['reverseSpace', 17]]
        .map(([name, col]) => ({ role: `attr-${name}`, request: ask(`attr-${name}`, [2, 1, attrRow, col, attrRow, col]) })),
    { role: 'attr-row', request: ask('attr-row', [2, 1, attrRow, 1, attrRow, 999]) },
    { role: 'attr-unweighted', request: ask('attr-unweighted', [2, 1, attrRow, 19, attrRow, 33]) },
    { role: 'page-omitted', request: ask('page-omitted', [7]) },
    { role: 'page-zero', request: ask('page-zero', [7, 0, 0, 0, 0, 0]) },
    { role: 'page-partial', request: ask('page-partial', [7, undefined, 3, 5]) },
    { role: 'page-oversized', request: ask('page-oversized', [7, 1, 1, 1, 999, 999]) },
    { role: 'page-parameter', request: ask('page-parameter', [7, 9, 1, 1, 1, 5]) },
    { role: 'id-largest', request: ask('id-largest', [65535, 1, attrRow, 1, attrRow, 40]) },
    { role: 'id-omitted', request: ask('id-omitted', [undefined, 1, 1, 1, 1, 5]) },
    { role: 'inverted-rows', request: ask('inverted-rows', [7, 1, 3, 1, 2, 8]) },
    { role: 'inverted-cols', request: ask('inverted-cols', [7, 1, 1, 5, 1, 1]) },
    { role: 'identity-private', request: ask('identity-private', null, '\x1b[?1;1;1;1;1;5*y') },
    { role: 'identity-greater', request: ask('identity-greater', null, '\x1b[>1;1;1;1;1;5*y') },
    { role: 'identity-seventh', request: ask('identity-seventh', null, '\x1b[1;1;1;1;1;5;1*y') },
    { role: 'identity-colon', request: ask('identity-colon', null, '\x1b[1;1;1:2;1;1;5*y') },
    { role: 'identity-dollar', request: ask('identity-dollar', null, '\x1b[1;1;1;1;1;5$y') },
    { role: 'identity-space', request: ask('identity-space', null, '\x1b[1;1;1;1;1;5 *y') },
    { role: 'originSeed', feeds: [originFeed] },
    { role: 'origin-region', request: ask('origin-region', [7, 1, 1, 1, 2, 12]) },
    { role: 'origin-clip', request: ask('origin-clip', [7, 1, 1, 1, 99, 999]) },
    { role: 'origin-defaults', request: ask('origin-defaults', [7, 1, 0, 0, 0, 0]) },
].map(Object.freeze));

/** The margins, origin mode and screen each group's setup contracts (report groups inherit the cells group before them). */
const stateOf = role => {
    const index = groups.findIndex(item => item.role === role);
    const setup = groups.slice(0, index + 1).filter(item => item.feeds).at(-1).role;
    if (setup === 'originSeed') return { margins: [originTop, originBottom], origin: true, alternate: true };
    return { margins: null, origin: false, alternate: true };
};

/** The one contracted case: the pinned fixture plus its controls, in a fixed plan. */
function makeContract() {
    const plan = [planFeed(rectChecksumReset)];
    for (const item of groups) {
        if (item.request) {
            plan.push(planCapture(item.request.bytes));
            plan.push(planRead('report'));
        } else {
            for (const feed of item.feeds) plan.push(planFeed(feed));
            plan.push(planRead('cells'));
        }
    }
    const roles = Object.freeze(groups.map(item => item.role));
    return Object.freeze({
        caseId: 'editing.decrqcra', probeId: 'editing.decrqcra', id: 'DECRQCRA', sequence: pinned.request,
        roles, requests: Object.freeze(groups.filter(item => item.request).map(item => item.request)),
        pinnedPath: 'termless', seedRead: roles.indexOf('seed'), pinnedRead: roles.indexOf('pinned'),
        pinnedExpected: pinned.expected,
        pinnedCriterion: 'DECRQCRA (ESC[1;1;1;1;1;5*y) after the seed ABCDE ESC[1;1H answers a framed DCS 1 ! ~ hhhh ST (the checksum arithmetic is not verified)',
        pinnedFeeds: Object.freeze([pinned.seed, pinned.request]), plan: Object.freeze(plan),
        terminalFixture: { writes: [pinned.terminalRequest], demand: 'a framed DCS 1 ! ~ hhhh ST reply within 2000 ms', minimum: { rows: 1, cols: 1 },
            note: 'the stock collector\'s queryOutcome path; this suite measures the same spelling as the graded terminal control' },
        appearanceScope: 'exact replies to stored ASCII cells: the checksum of the character and the bold, blink, reverse and underline weights over the '
            + 'requested rectangle, its defaults, origin offset and page clipping, the request id, the non-admitted spellings, and unchanged screen, cursor, margins '
            + 'and modes; wide, combining and supplementary characters, the persistent-session path, the default-off gate and rendered appearance are not claimed',
        evidence: 'owned-stored-cells-and-exact-replies',
        grade: observation => gradeRectChecksum(observation),
        observed: observation => briefRectChecksum(observation),
    });
}

const contract = makeContract();
/** The single pinned case. */
export const rectChecksumContracts = Object.freeze([contract]);
/** Contract case ids in suite order. */
export const rectChecksumCaseIds = Object.freeze(rectChecksumContracts.map(item => item.caseId));

// ---------------------------------------------------------------------------------------------------------------------------
// The independent checksum model. Written for this suite from the contract; it shares no code with the parser.

/** One cell's value: its character code (ASCII only; a blank is a space) plus the attribute weights, or null outside the scope. */
function cellValue(cell) {
    const code = cell.char === '' ? 0x20 : cell.char.codePointAt(0);
    if (cell.char.length > 1 || code < 0x20 || code > 0x7e) return null;
    return code + (cell.bold ? weights.bold : 0) + (cell.blink ? weights.blink : 0) + (cell.inverse ? weights.inverse : 0)
        + (cell.underline !== false ? weights.underline : 0);
}

/** One border: an omitted or non-positive value takes the default; origin offsets it; the page clips it. */
const border = (value, fallback, extent, offset) => Math.min(Math.max(((value ?? 0) > 0 ? value : fallback) - 1 + offset, 0), extent - 1);

/** The zero-based inclusive rectangle a parameter list selects on a screen (rows offset by the top margin under DECOM). */
export function rectangleOf(params, screen) {
    const rect = params.slice(2);
    const offset = screen.bufferState.originMode ? screen.bufferState.scrollTop : 0;
    return { top: border(rect[0], 1, screen.rows, offset), left: border(rect[1], 1, screen.cols, 0),
        bottom: border(rect[2], screen.rows, screen.rows, offset), right: border(rect[3], screen.cols, screen.cols, 0) };
}

/** The exact reply the contract demands for an admitted request over a screen, or null when a cell is outside the ASCII scope. */
export function modelReply(params, screen) {
    const id = (params[0] ?? 0) > 0 ? params[0] : 0;
    const { top, left, bottom, right } = rectangleOf(params, screen);
    const expanded = sgrAttrsExpand(screen);
    let sum = 0;
    for (let row = top; row <= bottom; row++) {
        for (let col = left; col <= right; col++) {
            const value = cellValue(sgrAttrsCellAt(screen, row, col, expanded));
            if (value === null) return null;
            sum += value;
        }
    }
    const checksum = (0x10000 - (sum % 0x10000)) % 0x10000;
    return `\x1bP${id}!~${checksum.toString(16).toUpperCase().padStart(4, '0')}\x1b\\`;
}

// ---------------------------------------------------------------------------------------------------------------------------
// Grading.

/** A group's observation: its ink, cursor, geometry, stored buffer state and, for a report group, the raw reply or null. */
const groupOf = (screen, reply) => ({ screen, ink: selectiveEraseInk(screen), cursor: [screen.cursorRow, screen.cursorCol], rows: screen.rows, cols: screen.cols,
    margins: [screen.bufferState.scrollTop, screen.bufferState.scrollBottom], origin: screen.bufferState.originMode,
    alternate: screen.bufferState.alternateScreen, reply });
/** Everything a request must leave unchanged: ink, cursor, margins, origin and screen. */
const stateKey = state => JSON.stringify([state.ink, state.cursor, state.margins, state.origin, state.alternate]);
const sameCursor = (state, [row, col]) => state.cursor[0] === row && state.cursor[1] === col;
const inkChars = (state, cells) => state.ink.length === cells.length
    && cells.every(([row, col, char], index) => state.ink[index].row === row && state.ink[index].col === col && state.ink[index].char === char);
/** The roles of report groups and the cells group each follows. */
const reportRoles = contract.roles.filter((role, index) => groups[index].request);
const previousCells = role => groups.slice(0, contract.roles.indexOf(role)).filter(item => item.feeds).at(-1).role;
/** Whether a state's margins, origin mode and screen match what its group's setup contracts. */
function stateIsContracted(state, role) {
    const expected = stateOf(role);
    const margins = expected.margins ?? [0, state.rows - 1];
    return state.margins[0] === margins[0] && state.margins[1] === margins[1] && state.origin === expected.origin && state.alternate === expected.alternate;
}

/** Whether every attribute seed cell reads back its character and exactly its rendition, and nothing else is written on its row. */
function attrSeedOk(state) {
    const row = attrRow - 1;
    const onRow = state.ink.filter(cell => cell.row === row);
    return attrCells.every(spec => {
        const cell = onRow.find(item => item.col === spec.col - 1);
        if (!cell || cell.char !== spec.char) return false;
        const flagsOk = flagNames.every(name => cell[name] === (spec.expect[name] ?? false));
        const underlineOk = cell.underline === (spec.expect.underline ?? false);
        const fgOk = cell.fgIndex === (spec.expect.fgIndex ?? -1);
        const bgOk = spec.expect.rgbBackground ? cell.bgIndex === -1 && cell.backgroundArgb !== null : cell.bgIndex === -1 && cell.backgroundArgb === null;
        return flagsOk && underlineOk && fgOk && bgOk;
    }) && onRow.length === attrCells.length;
}

const inconclusive = note => ({ outcome: 'inconclusive', note });
const unsupported = note => ({ outcome: 'unsupported', note });
const pinnedCells = [...'ABCDE'].map((char, col) => [0, col, char]);

/** The reads every case measures before grading anything, in order, each with the note naming what it failed to measure. */
const seedChecks = Object.freeze([
    [observation => observation.seed.rows >= minimum.rows && observation.seed.cols >= minimum.cols, 'Measured geometry cannot fit the rectangle-checksum controls'],
    [observation => observation.reverseVideo === false, 'DECSCNM reverse video was not proven off, so stored attributes cannot be separated from it'],
    [observation => observation.initial.ink.length === 0 && stateIsContracted(observation.initial, 'initial'), 'Blank neutral screen control was not measured'],
    [observation => inkChars(observation.seed, pinnedCells) && sameCursor(observation.seed, [0, 0]) && stateIsContracted(observation.seed, 'seed'),
        'The pinned seed ABCDE was not measured with the cursor home before the request'],
    [observation => attrSeedOk(observation.attrSeed) && observation.attrSeed.ink.filter(cell => cell.row === 0).length === 5 && sameCursor(observation.attrSeed, [0, 0]),
        'The attribute seed was not measured, so attribute weights cannot be attributed'],
    [observation => stateIsContracted(observation.originSeed, 'originSeed') && sameCursor(observation.originSeed, [originTop, 0])
        && JSON.stringify(observation.originSeed.ink) === JSON.stringify(observation.attrSeed.ink),
        'The origin-mode margins were not measured on the unchanged seed screen'],
]);

/** The first reason the seed, geometry or contracted state was not measured, or null. */
function seedGap(observation) {
    const failed = seedChecks.find(([holds]) => !holds(observation));
    return failed === undefined ? null : failed[1];
}

/** The reply each report role demands over the screen it saw: the model reply, or null for silence. Undefined when out of scope. */
function demandedReply(observation, role) {
    const request = contract.requests.find(item => item.role === role);
    if (!request.admitted) return null;
    const reply = modelReply(request.params, observation[previousCells(role)].screen);
    return reply === null ? undefined : reply;
}

/** The first way a measured exchange differs from the contract, or null. */
function replyMismatch(observation) {
    for (const role of reportRoles) {
        const state = observation[role], before = observation[previousCells(role)];
        if (!stateIsContracted(state, role) || stateKey(state) !== stateKey(before)) {
            return `The ${role} request changed the screen, the cursor, the margins or the modes: a checksum request must change nothing`;
        }
        const demanded = demandedReply(observation, role);
        if (demanded === undefined) return null;
        if (state.reply !== demanded) {
            if (demanded === null) return `The ${role} request is not the admitted DECRQCRA identity but was answered`;
            if (state.reply === null) return `The ${role} request was not answered`;
            return `The ${role} reply differs from the checksum model: the character sum, an attribute weight, a default, the clipping, the origin offset or the request id was wrong`;
        }
    }
    return null;
}

/** The out-of-scope gap: a requested rectangle held a character outside the stored ASCII scope. */
function scopeGap(observation) {
    const role = reportRoles.find(item => demandedReply(observation, item) === undefined);
    return role === undefined ? null : `The ${role} rectangle holds a character outside the stored ASCII scope`;
}

/** The shared control walk, then the exact reply verdict. */
function gradeRectChecksum(observation) {
    const gap = seedGap(observation) ?? scopeGap(observation);
    if (gap !== null) return inconclusive(gap);
    const mismatch = replyMismatch(observation);
    return mismatch === null ? { outcome: 'supported' } : unsupported(mismatch);
}

/** A printable spelling of raw bytes: ESC as \e. */
const printable = raw => (raw === null ? null : raw.replaceAll('\x1b', '\\e'));

/** The observed record: the pinned exchange, every request with its demanded and received reply, and the seeds. */
function briefRectChecksum(observation) {
    const requests = Object.fromEntries(reportRoles.map(role => {
        const request = contract.requests.find(item => item.role === role);
        const demanded = demandedReply(observation, role);
        return [role, { request: printable(request.bytes), admitted: request.admitted,
            ...(request.admitted ? { rectangle: rectangleOf(request.params, observation[previousCells(role)].screen) } : {}),
            demanded: demanded === undefined ? 'out of scope' : printable(demanded), received: printable(observation[role].reply),
            matches: demanded !== undefined && observation[role].reply === demanded,
            unchanged: stateKey(observation[role]) === stateKey(observation[previousCells(role)]) }];
    }));
    const { rows, cols } = observation.seed;
    return { geometry: { rows, cols }, reverseVideo: observation.reverseVideo, pinnedFeeds: contract.pinnedFeeds,
        pinnedReply: printable(observation.pinned.reply),
        gate: 'the measured run turns on AllowRectangleChecksumReport (off by default); the receipt binds the setting',
        seeds: { pinned: inkChars(observation.seed, pinnedCells), attributes: attrSeedOk(observation.attrSeed),
            origin: { margins: observation.originSeed.margins, origin: observation.originSeed.origin, cursor: observation.originSeed.cursor } },
        requests };
}

/** The shared reverse-video reading of every screen: off, on, or not provable (mixed or unreported). */
function reverseVideoOf(binding) {
    if (binding.every(value => value === false)) return false;
    return binding.includes(true) ? true : null;
}

/** Group the read screens and replies of the case by role. */
export function rectChecksumObservation(item, screens, replies) {
    if (!Array.isArray(screens) || screens.length !== item.roles.length || !Array.isArray(replies) || replies.length !== item.roles.length) {
        throw new Error('Missing rectangle-checksum read captures.');
    }
    const observation = Object.fromEntries(item.roles.map((role, index) => [role, groupOf(screens[index], replies[index])]));
    observation.reverseVideo = reverseVideoOf(screens.map(screen => screen.colorBinding.reverseVideo));
    return observation;
}

/** Independent contract grader entry: mirrors the pinned criterion without the bundle. */
export function gradeRectChecksumCase(item, observation, revision = rectChecksumContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown rectangle-checksum contracts revision ${revision}.`);
    if (!observation || item.roles.some(role => !observation[role] || !Array.isArray(observation[role].ink))) {
        throw new Error(`Missing ${item.caseId} read captures.`);
    }
    return item.grade(observation);
}

export { planFeeds as rectChecksumFeeds, planReads as rectChecksumReads } from './graded-plan.mjs';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateRectChecksumFeeds(item, feeds) {
    validateExactFeeds(item, feeds, 'contract');
}

/** The fixed fixture facts other modules and tests read. */
export const rectChecksumFixture = Object.freeze({ pinned, minimum, weights, attrRow, attrCells, attrFeed, originFeed, originTop, originBottom,
    groups });

/** The independent checksum model, exposed so tests can cross-check it against hand-written expectations. */
export const rectChecksumModel = Object.freeze({ cellValue, border, rectangleOf, modelReply, stateOf, previousCells, reportRoles });
