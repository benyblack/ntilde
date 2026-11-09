// Versioned probe contracts for the scrolling/history qualification batch. Exactly nine pinned terminfo.dev@4.0.0
// cases (bundle SHA-256 aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6, probeHash 16a690f34c0e):
//
//   scrollback.accumulate      rows+10 lines `line N\r\n`; supported iff measured totalLines grows
//   scrollback.total-lines     the same bytes; supported iff measured totalLines grows
//   scrollback.scroll-up       A..E on rows 1-5, DECSTBM 2;4, SU: A,C,D,blank,E, history unchanged, empty history first
//   scrollback.reverse-index   `A\r\nB\r\nC`, home, RI: row 0 blank
//   scrollback.scroll-down     `LINE1\r\nLINE2\r\nLINE3`, SD: row 0 blank
//   scrollback.set-region      DECSTBM 5;10 then reset: pinned verdict is ALWAYS inconclusive (cursor only)
//   scrollback.alt-screen      `NORMAL` then ?1049h: pinned verdict is ALWAYS inconclusive (mode flag only)
//   scrollback.decstbm         FIXED_TOP, DECSTBM 3;10, INNER, `Z\r\n` at the bottom margin: FIXED_TOP survives, INNER scrolls
//   scrollback.decstbm-reset   DECSTBM 5;10, reset, home, rows+10 lines `line-N\r\n`: pinned verdict is ALWAYS inconclusive
//
// The pinned termless probes read single cells, the cursor, a mode flag and the measured total line count; three of
// them (set-region, alt-screen, decstbm-reset) return a null verdict by construction, saying so in their own note
// ("Cursor position alone does not measure the scrolling region", "Mode metadata does not measure preserved
// scrollback", "Scrollback growth does not prove the earlier region was active before reset"). The pinned code is
// executed over every retained observation and must agree wherever it decides. Where it cannot decide, the contract
// supplies the evidence the pinned note says is missing and the report records the divergence explicitly
// (kind contract-measures-beyond-pinned); the stock grade stays inconclusive.
//
// This contract grades the pinned bytes against independently observed state: the complete viewport text of every
// row, the actual ordered local scrollback lines and count, the margins, the active screen, origin/insert/auto-wrap
// state, the cursor, and that no cell stores a non-default attribute. Every expected state is derived by an independent
// byte-interpreting model (scrollback-model.mjs) over the exact bytes the plan feeds, never from terminal readback.
// Unique source-known markers (line N, ROW-N, A..E) make omissions, duplicates and reordering detectable.
//
// Totals: the pinned `totalLines` is screen rows plus retained history lines; this batch observes the history lines
// themselves (readScrollback) and requires them to equal the independent expectation.
//
// SCOPE. ASCII fixtures at the measured geometry only. They do not establish Unicode, wide-cell, wrapped-line,
// reflow or resize behavior, and they do not qualify the Ntilde-owned history suite's other scrolling operations.
import { planFeed, planRead, planFeeds, planReads, validateExactFeeds } from './graded-plan.mjs';
import { sgrAttrsCellAt, validateSgrAttrsScreen } from './sgr-attrs-contracts.mjs';
import { expandCellDetails } from './cell-details.mjs';
import { expandScreenFlags } from './ascii-sgr-contracts.mjs';
import { modelPlanStates } from './scrollback-model.mjs';

/** Contracts v1 is the first measured semantics of this batch. */
export const scrollbackContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const scrollbackSuiteId = 'ntilde-owned-scrollback-graded-v1';
/** The nonce prefix binding worker output to this suite. */
export const scrollbackNoncePrefix = 'NTILDE_SB_';
/** The measured reset every plan begins with: main screen, DECSCNM, DECOM, margins, IRM and SGR off, screen and history cleared, cursor home. */
export const scrollbackReset = '\x1b[?1049l\x1b[?5l\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[3J\x1b[H';
/** The exact pinned case ids, in suite order. */
export const scrollbackCaseIds = Object.freeze(['scrollback.accumulate', 'scrollback.total-lines', 'scrollback.scroll-up',
    'scrollback.reverse-index', 'scrollback.scroll-down', 'scrollback.set-region', 'scrollback.alt-screen',
    'scrollback.decstbm', 'scrollback.decstbm-reset']);
/** The pinned cases whose own code can return a verdict; the others are null by construction. */
export const scrollbackDecidingIds = Object.freeze(['scrollback.accumulate', 'scrollback.total-lines', 'scrollback.scroll-up',
    'scrollback.reverse-index', 'scrollback.scroll-down', 'scrollback.decstbm']);
/** The smallest measured geometry the contracts accept: the pinned decstbm probe needs 10 rows; the controls use 12. */
export const scrollbackMinimum = Object.freeze({ rows: 12, cols: 48 });
/** The pinned note texts, byte for byte. */
export const pinnedScrollbackNotes = Object.freeze({
    region: 'Cursor position alone does not measure the scrolling region',
    alt: 'Mode metadata does not measure preserved scrollback',
    reset: 'Scrollback growth does not prove the earlier region was active before reset',
});

/** The pinned probes with a `finally` block feed this reset even when they return early; other probes have no cleanup. */
const scrollbackCleanupFeeds = Object.freeze({ 'scrollback.scroll-up': '\x1b[r', 'scrollback.decstbm': '\x1b[r' });

const at = (row, col = 1) => `\x1b[${row};${col}H`;
/** One distinct marker per physical row, so any displaced row is identifiable. */
const rowMark = index => `ROW-${index}`;
const wideSeed = rows => Array.from({ length: rows }, (_, index) => at(index + 1) + rowMark(index)).join('');
const letterSeed = '\x1b[1;1HA\x1b[2;1HB\x1b[3;1HC\x1b[4;1HD\x1b[5;1HE';

/** The shared state-read screen: attributes, stored details, resolved colors, buffer state and actual history. */
export const validateScrollbackScreen = screen => {
    validateSgrAttrsScreen(screen);
    // The attribute runs and the stored cell details are two projections of every cell: any disagreement voids the evidence.
    const flags = expandScreenFlags(screen);
    const details = expandCellDetails(screen.cellDetails, screen.rows * screen.cols, true);
    if (flags.length !== details.length || details.some((detail, position) => detail.flags !== flags[position])) {
        throw new Error('Contradictory stored cell projections in scrollback evidence.');
    }
    if (!screen.bufferState || !screen.history || screen.history.lines.length !== screen.bufferState.scrollbackLines) {
        throw new Error('Scrollback evidence is missing buffer state or actual history.');
    }
};
/** The pinned getCell projection over a stored screen. */
export const scrollbackCellAt = sgrAttrsCellAt;

/** Whether one stored cell detail is the default tuple (no attribute, default colors, no underline or overline color). */
const defaultDetail = detail => detail.flags === 0x300 && detail.fg === -1 && detail.bg === -1 && detail.foregroundArgb == null
    && detail.backgroundArgb == null && detail.overline === false && detail.underlineStyle === 0
    && detail.underlineColorIndex === -1 && detail.underlineArgb == null;

/** What one state read shows, projected for comparison with a modeled state. */
export function scrollbackView(screen) {
    validateScrollbackScreen(screen);
    const state = screen.bufferState;
    return { rows: screen.rows, cols: screen.cols, lines: screen.lines.map(line => line.trimEnd()),
        cursor: [screen.cursorRow, screen.cursorCol], cursorVisible: screen.cursorVisible,
        top: state.scrollTop, bottom: state.scrollBottom, alternate: state.alternateScreen, origin: state.originMode,
        autoWrap: state.autoWrap, insert: state.insertMode, pending: state.pendingWrap,
        history: screen.history.lines.map(line => line.trimEnd()), count: state.scrollbackLines,
        storedDefault: expandCellDetails(screen.cellDetails, screen.rows * screen.cols, true).every(defaultDetail) };
}

const sameList = (left, right) => left.length === right.length && left.every((value, index) => value === right[index]);

/** The first way an observed state differs from a modeled one, or null. `options.history` lists the acceptable histories
 * (default: exactly the modeled one; `main-or-empty` accepts the main history or none, for the alternate screen, which has none of its own);
 * `options.cursor === false` records the cursor without requiring it. */
export function scrollbackMismatch(observed, expected, options = {}) {
    if (observed.alternate !== expected.alternate) {
        return { part: 'screen', note: expected.alternate ? 'The alternate screen was not active' : 'The main screen was not active' };
    }
    if (observed.top !== expected.top || observed.bottom !== expected.bottom) {
        return { part: 'margins', note: `Margins ${observed.top + 1};${observed.bottom + 1} differ from the contracted ${expected.top + 1};${expected.bottom + 1}` };
    }
    if (observed.origin || observed.insert || !observed.autoWrap || observed.pending || !observed.cursorVisible) {
        return { part: 'modes', note: 'Origin mode, insert mode, auto-wrap, pending wrap or cursor visibility drifted from the contracted state' };
    }
    const histories = options.history === 'main-or-empty' ? [expected.history, []] : (options.history ?? [expected.history]);
    if (!histories.some(history => sameList(observed.history, history))) {
        return { part: 'history', note: `The actual scrollback differs from the contracted history (${observed.history.length} lines observed, ${expected.history.length} contracted)` };
    }
    const row = expected.lines.findIndex((line, index) => observed.lines[index] !== line);
    if (row >= 0) return { part: 'viewport', row, note: `Viewport row ${row} differs from the contracted contents` };
    if (!observed.storedDefault) return { part: 'attributes', note: 'A cell stores a non-default attribute or color that no contracted operation sets' };
    if (options.cursor !== false && (observed.cursor[0] !== expected.cursor[0] || observed.cursor[1] !== expected.cursor[1])) {
        return { part: 'cursor', note: `The cursor is at ${observed.cursor.join(',')} instead of the contracted ${expected.cursor.join(',')}` };
    }
    return null;
}

/** A plan under construction: feed and read steps plus the per-read role record. */
class PlanBuilder {
    constructor() { this.steps = []; this.roles = []; this.feeds = 0; this.pinned = []; this.readAt = {}; }
    /** One exact feed; returns its index among the plan's feeds. */
    feed(bytes) { this.steps.push(planFeed(bytes)); return this.feeds++; }
    /** One exact pinned feed. */
    pin(bytes) { this.pinned.push(bytes); return this.feed(bytes); }
    /** One state read with its role: `control` states must hold for a conclusive grade, `measured` states are the verdict. */
    read(role, kind, { pinned = false, options = {} } = {}) {
        this.steps.push(planRead('state'));
        this.roles.push({ role, kind, options, alternatives: [] });
        if (pinned) this.readAt[this.pinned.length] = this.roles.length - 1;
        return this.roles.length - 1;
    }
    /** A labeled wrong behavior the model computes by replacing one feed, used only to word an unsupported diagnosis. */
    alternative(readIndex, label, feedIndex, replacement) { this.roles[readIndex].alternatives.push({ label, feedIndex, replacement }); }
}

/**
 * Split a line into its text and its trailing decimal number. Equivalent to matching /^(.*?)(\d+)$/ (the number is the longest
 * all-digit tail, and the text may not contain a line terminator, which `.` refuses) without that expression's backtracking:
 * the lazy prefix followed by a greedy digit run retries the digit run from every start, which is quadratic on long digit-rich
 * lines. This scan is linear in the line length.
 */
export function splitTrailingNumber(text) {
    let start = text.length;
    while (start > 0 && text.codePointAt(start - 1) >= 48 && text.codePointAt(start - 1) <= 57) start--;
    if (start === text.length) return null;
    const prefix = text.slice(0, start);
    if (/[\n\r\u2028\u2029]/.test(prefix)) return null;
    return { prefix, digits: text.slice(start) };
}

/** Compact a list of rows or history lines: runs of consecutive numbered lines become one entry. */
export function compactLines(lines, firstRow = 0) {
    const out = [];
    for (let index = 0; index < lines.length; index++) {
        const text = lines[index];
        if (text === '') continue;
        const first = splitTrailingNumber(text);
        let end = index, last = first;
        if (first) {
            while (end + 1 < lines.length) {
                const next = splitTrailingNumber(lines[end + 1]);
                if (!next || next.prefix !== first.prefix || Number(next.digits) !== Number(last.digits) + 1) break;
                end++;
                last = next;
            }
        }
        out.push(end === index ? `${firstRow + index}: ${text}`
            : `${firstRow + index}-${firstRow + end}: ${text}..${last.digits}`);
        index = end;
    }
    return out;
}

/** Group the read screens of one case by role. */
export function scrollbackObservation(contract, screens) {
    if (!Array.isArray(screens) || screens.length !== contract.roles.length) throw new Error('Missing scrollback read captures.');
    const observation = Object.fromEntries(contract.roles.map((entry, index) => [entry.role, scrollbackView(screens[index])]));
    const binding = screens.map(screen => screen.colorBinding.reverseVideo);
    observation.reverseVideo = binding.every(value => value === false) ? false : binding.some(value => value === true) ? true : null;
    return observation;
}

/** Build one frozen contract from a finished plan. */
function finish(rows, cols, { caseId, builder, criterion, expected, fixture }) {
    const states = modelPlanStates(builder.steps, rows, cols);
    const roles = builder.roles.map((entry, index) => {
        const alternatives = entry.alternatives.map(({ label, feedIndex, replacement }) =>
            ({ label, state: modelPlanStates(builder.steps, rows, cols, (position, bytes) => (position === feedIndex ? replacement : bytes))[index] }));
        return Object.freeze({ role: entry.role, kind: entry.kind, options: entry.options, expected: states[index], alternatives });
    });
    const contract = {
        caseId, probeId: caseId, pinnedPath: 'termless', rows, cols, roles: Object.freeze(roles),
        decides: scrollbackDecidingIds.includes(caseId), cleanupFeed: scrollbackCleanupFeeds[caseId], pinnedReadAt: Object.freeze({ ...builder.readAt }),
        pinnedFeeds: Object.freeze([...builder.pinned]), pinnedCriterion: criterion, pinnedExpected: expected,
        plan: Object.freeze(builder.steps), terminalFixture: { writes: builder.pinned.length > 12 ? ['(see pinnedFeeds: one `line N` write per row plus ten)'] : [...builder.pinned], demand: fixture, minimum: scrollbackMinimum },
        appearanceScope: 'stored cells, actual local scrollback, margins and cursor of ASCII fixtures at the measured geometry; no Unicode, wide-cell, '
            + 'wrapped-line, reflow, resize or pixel claim',
    };
    contract.grade = observation => gradeScrollback(contract, observation);
    contract.observed = observation => briefScrollback(contract, observation);
    return Object.freeze(contract);
}

/** The pinned accumulate/total-lines fixture: rows+10 lines, one feed each, with a read just before the first overflow. */
function accumulatePlan(rows, prefix, caseId, criterion) {
    const builder = new PlanBuilder();
    const lines = Array.from({ length: rows + 10 }, (_, index) => `${prefix} ${index}\r\n`);
    builder.feed(scrollbackReset);
    builder.read('initial', 'control', { pinned: true });
    for (const line of lines.slice(0, rows - 1)) builder.pin(line);
    builder.read('justFull', 'measured');
    for (const line of lines.slice(rows - 1)) builder.pin(line);
    builder.read('final', 'measured', { pinned: true });
    return { caseId, builder, criterion, expected: criterion, fixture: 'exact ordered history and viewport after overflow' };
}

/** Two seeded interior-region reads shared by the SU, SD and RI controls: A..E with DECSTBM 2;4. */
function regionControl(builder, operation, label, wrong) {
    builder.feed(scrollbackReset);
    builder.feed(letterSeed);
    const margins = builder.feed('\x1b[2;4r');
    builder.read('regionBefore', 'control');
    builder.feed(operation.move);
    const feed = builder.feed(operation.bytes);
    const after = builder.read('regionAfter', 'measured');
    builder.alternative(after, 'no-op: the region did not scroll', feed, '\x1b[0m');
    if (wrong) builder.alternative(after, wrong.label, feed, wrong.bytes);
    builder.alternative(after, `whole-screen scrolling instead of region scrolling (${label})`, margins, '\x1b[r');
}

/** Build the pinned contracts for the measured geometry. */
export function makeScrollbackContracts(rows, cols) {
    if (!Number.isSafeInteger(rows) || rows < scrollbackMinimum.rows || rows > 128 || !Number.isSafeInteger(cols) || cols < scrollbackMinimum.cols || cols > 256) {
        throw new Error('Scrollback contracts need at least 12 rows and 48 columns, and at most 128 rows and 256 columns (the observer limits).');
    }
    const contracts = [];
    contracts.push(finish(rows, cols, accumulatePlan(rows, 'line', 'scrollback.accumulate',
        'rows+10 lines `line N` grow the measured scrollback; observed as the exact ordered history and viewport')));
    contracts.push(finish(rows, cols, accumulatePlan(rows, 'line', 'scrollback.total-lines',
        'rows+10 lines `line N` raise the measured total line count above its baseline; observed as rows plus the exact history lines')));

    { // scroll-up
        const builder = new PlanBuilder();
        builder.feed(scrollbackReset);
        builder.read('initial', 'control', { pinned: true });
        builder.pin('\x1b[2J');
        for (const [row, marker] of ['A', 'B', 'C', 'D', 'E'].entries()) builder.pin(`${at(row + 1)}${marker}`);
        const margins = builder.pin('\x1b[2;4r');
        builder.read('seed', 'control', { pinned: true });
        const feed = builder.pin('\x1b[S');
        const after = builder.read('after', 'measured', { pinned: true });
        builder.alternative(after, 'wrong direction: the region scrolled down', feed, '\x1b[T');
        builder.alternative(after, 'no-op: the region did not scroll', feed, '\x1b[0m');
        builder.alternative(after, 'whole-screen scrolling instead of region scrolling', margins, '\x1b[r');
        builder.pin('\x1b[r');
        builder.read('restored', 'measured');
        builder.feed(scrollbackReset);
        builder.feed(wideSeed(rows));
        const wideMargins = builder.feed('\x1b[2;4r');
        builder.read('wideBefore', 'control');
        const wideFeed = builder.feed('\x1b[S');
        const wideAfter = builder.read('wideAfter', 'measured');
        builder.alternative(wideAfter, 'whole-screen scrolling instead of region scrolling', wideMargins, '\x1b[r');
        builder.alternative(wideAfter, 'wrong direction: the region scrolled down', wideFeed, '\x1b[T');
        contracts.push(finish(rows, cols, { caseId: 'scrollback.scroll-up', builder, fixture: 'region rows shifted up with the bottom row blank, outside rows and history intact',
            criterion: 'Interior-region SU shifts the measured middle and bottom rows up, clearing the region bottom, with no history growth',
            expected: 'rows A,C,D,blank,E and an unchanged, initially empty history' }));
    }

    { // reverse-index
        const builder = new PlanBuilder();
        builder.feed(scrollbackReset);
        builder.pin('A\r\nB\r\nC');
        builder.read('before', 'control', { pinned: true });
        const feed = builder.pin('\x1b[H\x1bM');
        const after = builder.read('after', 'measured', { pinned: true });
        builder.alternative(after, 'no-op: no blank row was inserted', feed, '\x1b[H');
        builder.feed(scrollbackReset);
        builder.feed('A\r\nB\r\nC');
        const move = builder.feed('\x1b[2;1H\x1bM');
        const moved = builder.read('moveOnly', 'measured');
        builder.alternative(moved, 'a blank row was inserted although the cursor was below the top margin', move, '\x1b[1;1H\x1bM');
        regionControl(builder, { move: '\x1b[2;1H', bytes: '\x1bM' }, 'RI', { label: 'wrong direction: the region scrolled up', bytes: '\x1b[S' });
        contracts.push(finish(rows, cols, { caseId: 'scrollback.reverse-index', builder, fixture: 'blank row inserted at the top margin, rows below shifted down, no history growth',
            criterion: 'RI at the top margin inserts a blank row above the measured A marker', expected: 'row 0 blank and A,B,C on rows 1-3' }));
    }

    { // scroll-down
        const builder = new PlanBuilder();
        builder.feed(scrollbackReset);
        builder.pin('LINE1\r\nLINE2\r\nLINE3');
        builder.read('before', 'control', { pinned: true });
        const feed = builder.pin('\x1b[T');
        const after = builder.read('after', 'measured', { pinned: true });
        builder.alternative(after, 'wrong direction: the screen scrolled up', feed, '\x1b[S');
        builder.alternative(after, 'no-op: no blank row was inserted', feed, '\x1b[0m');
        regionControl(builder, { move: '\x1b[1;1H', bytes: '\x1b[T' }, 'SD', { label: 'wrong direction: the region scrolled up', bytes: '\x1b[S' });
        builder.feed(scrollbackReset);
        builder.feed(wideSeed(rows));
        builder.read('fullBefore', 'control');
        const full = builder.feed('\x1b[T');
        const fullAfter = builder.read('fullAfter', 'measured');
        builder.alternative(fullAfter, 'wrong direction: the screen scrolled up', full, '\x1b[S');
        contracts.push(finish(rows, cols, { caseId: 'scrollback.scroll-down', builder, fixture: 'blank row inserted at the top, the bottom row discarded without history, no history growth',
            criterion: 'SD inserts a blank row above the measured LINE marker', expected: 'row 0 blank and LINE1,LINE2,LINE3 on rows 1-3' }));
    }

    { // set-region
        const builder = new PlanBuilder();
        builder.feed(scrollbackReset);
        const set = builder.pin('\x1b[5;10r');
        const setRead = builder.read('set', 'measured', { pinned: true });
        builder.alternative(setRead, 'no-op: the margins were not set', set, '\x1b[0m');
        builder.pin('\x1b[r');
        builder.read('reset', 'measured');
        builder.feed(scrollbackReset);
        builder.feed(wideSeed(rows));
        const ctlMargins = builder.feed('\x1b[5;10r');
        builder.read('ctlSet', 'control');
        builder.feed(`${at(10)}\n`);
        const scrolled = builder.read('ctlScrolled', 'measured');
        builder.alternative(scrolled, 'whole-screen scrolling instead of region scrolling', ctlMargins, '\x1b[r');
        builder.feed('\x1b[r');
        builder.feed(`${at(rows)}\n`);
        builder.read('ctlFull', 'measured');
        contracts.push(finish(rows, cols, { caseId: 'scrollback.set-region', builder, fixture: 'margins observed, scrolling confined to the region, boundaries restored by the reset',
            criterion: 'DECSTBM constrains scrolling to the requested region', expected: pinnedScrollbackNotes.region }));
    }

    { // alt-screen
        const builder = new PlanBuilder();
        builder.feed(scrollbackReset);
        builder.feed(`${wideSeed(rows)}${at(rows)}\n\n\n`);
        builder.read('seeded', 'control');
        builder.pin('NORMAL');
        builder.read('normal', 'control');
        const enter = builder.pin('\x1b[?1049h');
        const alt = builder.read('alt', 'measured', { pinned: true, options: { cursor: false, history: 'main-or-empty' } });
        builder.alternative(alt, 'no-op: the alternate screen was not entered', enter, '\x1b[0m');
        builder.feed(`\x1b[H${Array.from({ length: rows + 5 }, (_, index) => `ALT-${index}\r\n`).join('')}`);
        builder.read('altOverflow', 'measured', { options: { history: 'main-or-empty' } });
        const exit = builder.feed('\x1b[?1049l');
        const restored = builder.read('restored', 'measured');
        builder.alternative(restored, 'no-op: the main screen was not restored', exit, '\x1b[0m');
        contracts.push(finish(rows, cols, { caseId: 'scrollback.alt-screen', builder, fixture: 'alternate screen isolated from main contents and history, main exactly restored',
            criterion: 'The alternate screen preserves normal scrollback', expected: pinnedScrollbackNotes.alt }));
    }

    { // decstbm
        const builder = new PlanBuilder();
        builder.feed(scrollbackReset);
        builder.read('initial', 'control', { pinned: true });
        builder.pin('FIXED_TOP\r\n');
        builder.read('before', 'control', { pinned: true });
        const margins = builder.pin('\x1b[3;10r');
        builder.pin('\x1b[3;1HINNER');
        builder.read('innerBefore', 'control', { pinned: true });
        builder.pin('\x1b[10;1HZ\r\n');
        const after = builder.read('after', 'measured', { pinned: true });
        builder.alternative(after, 'whole-screen scrolling instead of region scrolling', margins, '\x1b[r');
        builder.pin('\x1b[r');
        builder.read('restored', 'measured');
        builder.feed(scrollbackReset);
        builder.feed(wideSeed(rows));
        const wideMargins = builder.feed('\x1b[3;10r');
        builder.read('ctlBefore', 'control');
        builder.feed(`${at(10)}Z\r\n`);
        const ctlAfter = builder.read('ctlAfter', 'measured');
        builder.alternative(ctlAfter, 'whole-screen scrolling instead of region scrolling', wideMargins, '\x1b[r');
        contracts.push(finish(rows, cols, { caseId: 'scrollback.decstbm', builder, fixture: 'inner rows scroll, FIXED_TOP and every row outside the region stay intact, no history growth',
            criterion: 'DECSTBM scrolls the inner marker while preserving FIXED_TOP outside the region', expected: 'FIXED_TOP on row 0 after the inner scroll' }));
    }

    { // decstbm-reset
        const builder = new PlanBuilder();
        const lines = Array.from({ length: rows + 10 }, (_, index) => `line-${index}\r\n`);
        builder.feed(scrollbackReset);
        builder.read('initial', 'control', { pinned: true });
        builder.pin('\x1b[5;10r');
        builder.read('set', 'measured');
        const resetFeed = builder.pin('\x1b[r');
        builder.read('reset', 'measured');
        builder.pin('\x1b[H');
        for (const line of lines) builder.pin(line);
        const final = builder.read('final', 'measured', { pinned: true });
        builder.alternative(final, 'the margins stayed active after the reset: the lines never reached history', resetFeed, '\x1b[5;10r');
        builder.feed(scrollbackReset);
        builder.feed('\x1b[5;10r');
        builder.feed('\x1b[H');
        builder.feed(lines.join(''));
        builder.read('ctlRegion', 'measured');
        contracts.push(finish(rows, cols, { caseId: 'scrollback.decstbm-reset', builder, fixture: 'reset restores full-screen scrolling into history; an unreset region keeps history empty and rows 1-4 fixed',
            criterion: 'Reset DECSTBM permits full-screen scrolling into history', expected: pinnedScrollbackNotes.reset }));
    }
    return Object.freeze(contracts);
}

/** The wording for one failed measured role: a labeled wrong behavior when the observation equals it, else the generic difference. */
function diagnose(role, view, mismatch) {
    for (const alternative of role.alternatives) {
        if (scrollbackMismatch(view, alternative.state, role.options) === null) return `${role.role}: ${alternative.label}`;
    }
    return `${role.role}: ${mismatch.note}`;
}

/** Independent grading: every control must hold, then every measured role must equal its modeled state. */
function gradeScrollback(contract, observation) {
    const { rows, cols } = contract;
    const first = observation[contract.roles[0].role];
    if (first.rows < scrollbackMinimum.rows || first.cols < scrollbackMinimum.cols || first.rows !== rows || first.cols !== cols) {
        return { outcome: 'inconclusive', note: `Measured geometry ${first.rows}x${first.cols} cannot fit the scrollback fixtures` };
    }
    if (observation.reverseVideo !== false) {
        return { outcome: 'inconclusive', note: 'DECSCNM reverse video was not proven off, so stored attributes cannot be separated from it' };
    }
    for (const role of contract.roles.filter(entry => entry.kind === 'control')) {
        const mismatch = scrollbackMismatch(observation[role.role], role.expected, role.options);
        if (mismatch) return { outcome: 'inconclusive', note: `Control ${role.role} was not established (${mismatch.note}), so the operation cannot be graded` };
    }
    for (const role of contract.roles.filter(entry => entry.kind === 'measured')) {
        const mismatch = scrollbackMismatch(observation[role.role], role.expected, role.options);
        if (mismatch) return { outcome: 'unsupported', note: diagnose(role, observation[role.role], mismatch) };
    }
    return { outcome: 'supported', layers: ['stored cells', 'actual scrollback', 'margins and modes', 'cursor'] };
}

/** The observed record: every role's compact viewport, history, margins, screen and cursor, with whether it matched the model. */
function briefScrollback(contract, observation) {
    const roles = Object.fromEntries(contract.roles.map(role => {
        const view = observation[role.role];
        const mismatch = scrollbackMismatch(view, role.expected, role.options);
        return [role.role, { kind: role.kind, matchesModel: mismatch === null, ...(mismatch ? { differs: mismatch.part } : {}),
            screen: view.alternate ? 'alternate' : 'main', margins: [view.top, view.bottom], cursor: view.cursor,
            viewport: compactLines(view.lines), history: compactLines(view.history), historyCount: view.count }];
    }));
    const first = observation[contract.roles[0].role];
    return { geometry: { rows: first.rows, cols: first.cols }, reverseVideo: observation.reverseVideo, roles };
}

/** Independent contract grader entry: mirrors the pinned criteria without the bundle. */
export function gradeScrollbackCase(contract, observation, revision = scrollbackContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown scrollback contracts revision ${revision}.`);
    if (!observation || contract.roles.some(role => !observation[role.role] || !Array.isArray(observation[role.role].lines))) {
        throw new Error(`Missing ${contract.caseId} read captures.`);
    }
    return contract.grade(observation);
}

export { planFeeds as scrollbackFeeds, planReads as scrollbackReads } from './graded-plan.mjs';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateScrollbackFeeds(contract, feeds) {
    validateExactFeeds(contract, feeds, 'contract');
}
