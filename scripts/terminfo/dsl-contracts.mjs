// Versioned probe contract for the DECSCLM qualification batch: modes.decsclm (DEC private mode 4, smooth scrolling). This is not a publication or a measurement. The batch's first integrated graph is
// v80; its receipt schema is 82.
//
// Pinned terminfo.dev@4.0.0 definition (bundle SHA-256 aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6), read from the bundle, never inferred from the name. The case is registered as
// `decrpmModeProbe("modes.decsclm", 4, termlessFn)`:
//   termless  feed CSI ? 4 h, feedCapture CSI ? 4 $ p, feed CSI ? 4 l, then ALWAYS parserStateResult(null, "DECSCLM changes scroll timing", { response }, "DECRPM state does not measure scroll timing")
//             (inconclusive, insufficient-evidence, evidence parser-state), whatever the reply was. It can never be supported or unsupported.
//   terminal  decrpmResult(await ctx.queryMode(4), 4): the DA-sentinel query CSI ? 4 $ p; a recognised state (set or reset) is supported, "not recognized" (Pm 0) is inconclusive
//             (insufficient-evidence, evidence query, "mode behavior unmeasured"), no reply is inconclusive (no-response).
// Neither path measures any scroll timing, so a DECRPM acknowledgement alone would score without behavior; teaching DECRQM about mode 4 without any pacing is exactly what the batch must not do.
// Ntilde does not track mode 4 (DECRQM answers Pm 0; ANSI mode 4 is the unrelated IRM, and the App's wheel smooth-scroll setting is a separate user-interface feature), so the pinned verdict is
// inconclusive on both paths and this contract never promotes or demotes it.
//
// The authoritative contract is DEC's: DECSCLM (CSI ? 4 h) selects smooth scrolling, at most about six lines per second for terminal output that scrolls the screen (CSI ? 4 l is jump scrolling: output
// scrolls as fast as it arrives). Ntilde's deliberate policy (#1001) is that terminal output always jump-scrolls: mode 4 is ignored whole, with no pacing, no state change and no DECRPM recognition.
// The graded effect is `unsupported` when, with the mode set, held, reset and never touched, a burst of forty line feeds that scrolls the margin band ends in exactly the same deterministic state
// (the band blank, the cursor and rendition as contracted) AND every exchange finishes inside the timing bound while a control proves the same timing reader can see a slower exchange, with mode 4 still
// answered "not recognized" beside a recognised-mode control and a clear/home control that proves the state reader sees a change. Any other observation makes the effect inconclusive: a terminal that
// started pacing its scrolling needs its own supported contract rather than being graded as unsupported here.
//
// Timing: forty line feeds at the DEC smooth rate take at least 6.6 seconds; the bound is two seconds, so pacing at or below that rate cannot hide. The reading is the parent's monotonic clock: the time from
// the end of the previous journaled observation to the arrival of this read request, which spans exactly the fenced exchange (and the seeding that precedes it) and so is an upper bound of the time the
// terminal needed (the worker cannot shorten it). The instrument control holds the worker for 2.5 seconds after a fenced exchange; its reading must reach that pause, so a slow exchange is visible in
// the same field. Wall-clock measurement is noisy: a run whose fast exchanges miss the bound is inconclusive, never unsupported, and is measured again.
//
// Masking: the seed leaves the cursor away from home (row 3, column 6, over a plain witness character written by the plan) and every exchange ends by rewriting that witness at the cursor and stepping
// back onto it, so a pen (bold, color, underline, inverse, ...) or the ANSI insert mode a misparsed `CSI ? 4 h` left changed shows in the stored cell. Each group is preceded by a reseed (the held-mode
// burst alone excepted, so that the mode set by the group before it is still in force).
//
// Limits: raw replies, stored terminal state and the parent's monotonic timing only; no rendered frame, repaint cadence, window, remote/mux, other-platform, common-harness or score claim.
import { planFeed, planRead, validateExactFeeds } from './graded-plan.mjs';
import { selectiveEraseInk } from './selective-erase-contracts.mjs';
import { sgrAttrsPredicates } from './sgr-attrs-contracts.mjs';
import { devqFixture, devqMinimum, devqReseed, devqReset, devqSeed } from './devq-contracts.mjs';
export { validateDevqScreen as validateDslScreen } from './devq-contracts.mjs';

/** Contracts v1 is the first measured semantics of this batch. */
export const dslContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const dslSuiteId = 'ntilde-owned-decsclm-graded-v1';
/** The separately identified effect contract. */
export const dslEffectContractId = 'ntilde-decsclm-unsupported-effect-v1';
/** The nonce prefix binding worker output to this suite. */
export const dslNoncePrefix = 'NTILDE_DSL_';
/** Owner-protocol prefix of this suite's observation requests. */
export const dslPrefix = 'dsl';
/** The pinned case. */
export const dslCaseIds = Object.freeze(['modes.decsclm']);
/** The measured geometry the controls need (the shared device-query seed: margins 2..6, a nonce row and an attributed seed row). */
export const dslMinimum = devqMinimum;
export const dslReset = devqReset;
export const dslReseed = devqReseed;
export const dslSeed = devqSeed;
/** The run's baseline title, written by the finish so the pane never keeps a test label. */
export const dslBaseTitle = runId => `DSL-base-${runId.slice(0, 8)}`;
/** The timing bound of every ordinary exchange, in milliseconds: well under the 6.6 seconds forty line feeds need at the DEC smooth-scroll rate of six lines per second. */
export const dslTimingBoundMs = 2000;
/** The instrument control holds the worker this long after its exchange; the paced reading must reach it. It is longer than the bound, so a pause is a reading no ordinary exchange may have. */
export const dslPauseMs = 2500;
/** Line feeds of one scroll burst. */
export const dslBurstLines = 40;
/** The rate at which DEC smooth scrolling paces output, in lines per second (the floor used to size the burst and the bound). */
export const dslSmoothLinesPerSecond = 6;

const esc = '\x1b';
const csi = body => `${esc}[${body}`;
const setFour = csi('?4h'), resetFour = csi('?4l'), queryFour = csi('?4$p');
const reply = (mode, pm) => csi(`?${mode};${pm}$y`);
/** The cursor the seed leaves the pane at (one-based CUP 4;7, zero-based row 3 column 6), inside the margins and away from home. */
export const dslCursorAt = Object.freeze([3, 6]);
const placeCursor = csi('4;7H');
/** The witness every exchange ends with: a plain character written at the cursor with whatever pen is current, then the cursor stepped back onto it. It rewrites the same cell, so the cell shows the
 *  pen (bold, color, underline, inverse and the rest) in force after that exchange, and an insert mode (the ANSI mode a misparsed CSI ? 4 h would set) would shift the row. */
const witnessChar = 'W';
const witness = witnessChar + csi('D');
/** Ntilde does not track mode 4: DECRQM answers Pm 0 (not recognized). */
const notRecognizedFour = reply(4, 0);
/** The scroll burst: the cursor at the bottom margin row (one-based row 6), then forty line feeds, each scrolling the margin band by one line. */
const toBottomMargin = csi('6;1H');
const burst = '\n'.repeat(dslBurstLines);
/** The cursor a burst leaves: the bottom margin row, column 0 (the witness steps back onto its own cell). */
export const dslBurstCursor = Object.freeze([5, 0]);
/** Clear the margin band, then lay the plan's own seed again over the nonce row the run bound: every group starts from the same fixture. */
const clearBand = [2, 3, 4, 5, 6].map(row => csi(`${row};1H`) + csi('2K')).join('');
const reseedFeed = clearBand + devqReseed + placeCursor + witness;

// ---------- the pinned probe (read from the bundle) ----------

/** The exact calls each pinned path makes, in order. `replies` names which journaled bytes the captured call returns. */
export const dslPinnedScripts = Object.freeze({
    'modes.decsclm': Object.freeze({
        termless: Object.freeze([['feed', setFour], ['feedCapture', queryFour, 'replies'], ['feed', resetFour]]),
        terminal: Object.freeze([['queryMode', 4]]),
        expected: 'DECSCLM changes scroll timing',
    }),
});
const pinnedCriterion = 'decrpmModeProbe("modes.decsclm", 4, termlessFn): the termless path feeds CSI ? 4 h, captures the CSI ? 4 $ p reply and feeds CSI ? 4 l, then always returns parserStateResult(null, ...) '
    + '(inconclusive, insufficient-evidence, parser-state, "DECRPM state does not measure scroll timing") whatever the reply was; the terminal path asks queryMode(4) and returns decrpmResult: supported only for a '
    + 'recognised set/reset state, inconclusive for Pm 0 (not recognized) or no reply; neither path measures any scroll timing';

const decrpmPattern = /\x1b\[\?4;([0-4])\$y/;
/** The state the pinned queryMode derives from a reply list: null (no matching reply), "set", "reset" or "unknown". */
export function pinnedModeState(replies) {
    const match = decrpmPattern.exec(replies.join(''));
    if (match === null) return null;
    return { 0: 'unknown', 1: 'set', 2: 'reset', 3: 'set', 4: 'reset' }[match[1]];
}

/** The pinned termless result over the journaled reply text of the case's own exchange: parserStateResult(null, ...) with the captured response, whatever it was. */
export function replayDslTermless(caseId, replies) {
    if (!(caseId in dslPinnedScripts)) throw new Error(`Unknown decsclm case ${caseId}.`);
    if (typeof replies !== 'string') throw new Error('Missing decsclm reply text.');
    const note = 'DECRPM state does not measure scroll timing';
    return { pass: false, response: JSON.stringify({ response: replies }), note, observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'parser-state', note } };
}

/** The pinned terminal result: decrpmResult over the state queryMode derives from the journaled reply list of the case's own mode query. */
export function replayDslTerminal(caseId, replies) {
    if (!(caseId in dslPinnedScripts)) throw new Error(`Unknown decsclm case ${caseId}.`);
    if (!Array.isArray(replies)) throw new Error('Missing decsclm reply list.');
    const state = pinnedModeState(replies);
    if (state === null) return { pass: false, note: 'No DECRPM response', observation: { outcome: 'inconclusive', reason: 'no-response', evidence: 'query', note: 'No DECRPM response' } };
    if (state === 'unknown') {
        const note = 'DECRPM mode 4: not recognized; mode behavior unmeasured';
        return { pass: false, note, response: state, observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'query', note } };
    }
    throw new Error(`The ${caseId} mode query was recognised as ${state}: the pinned terminal path would pass without any scroll timing; this owned run never serves it.`);
}

// ---------- the groups ----------

const ask = (role, kind, note, body, demand, { expect = 'unchanged', reseed = true, pauseMs = null } = {}) => {
    const request = expect === 'cleared' ? body : body + witness;
    // The owned fence refuses any exchange request over 64 characters (terminal-fence.mjs); the contract
    // self-check keeps every group inside that bound instead of discovering it at collection time.
    if (request.length > 64) throw new Error(`The ${role} request exceeds the 64-byte owned exchange limit.`);
    return Object.freeze({ role, kind, note, request, demand: Object.freeze(demand), expect, reseed, pauseMs });
};
const burstBody = modes => toBottomMargin + modes + burst + queryFour;

/** The groups, in execution order. A `control` gates the verdict (a failing control makes the effect inconclusive); a `protocol` group demands the ignored behavior. Every group is judged on its own bytes: the
 *  state, the timing and the reply list after that group's exchange. The clear/home control runs last because it destroys the fixture it is compared with. */
const groups = Object.freeze([
    ask('recognized', 'control', 'the reply channel answers recognised modes (DECAWM set, DECANM set), so a Pm 0 answer for mode 4 is a real "not recognized" and silence is not a dead channel',
        csi('?7$p') + csi('?2$p'), [reply(7, 1), reply(2, 1)]),
    ask('pinned', 'protocol', 'the exact pinned termless exchange (CSI ? 4 h, the CSI ? 4 $ p query, CSI ? 4 l) leaves everything unchanged and mode 4 is answered not recognized',
        setFour + queryFour + resetFour, [notRecognizedFour]),
    ask('pinned-query', 'protocol', 'the exact pinned terminal-path query alone (CSI ? 4 $ p) is answered not recognized', queryFour, [notRecognizedFour]),
    ask('set', 'protocol', 'CSI ? 4 h alone changes nothing (the screen, the cursor, the rendition, the ANSI insert mode) and mode 4 stays not recognized', setFour + queryFour, [notRecognizedFour]),
    ask('reset', 'protocol', 'CSI ? 4 l alone changes nothing and mode 4 stays not recognized', resetFour + queryFour, [notRecognizedFour]),
    ask('burst-off', 'control', 'the reference burst, with jump scrolling selected (CSI ? 4 l): forty line feeds scroll the margin band blank, the cursor ends on the bottom margin row, inside the timing bound',
        burstBody(resetFour), [notRecognizedFour], { expect: 'burst' }),
    ask('burst-set', 'protocol', 'with smooth scrolling selected (CSI ? 4 h) the same burst ends in the identical state, inside the timing bound that smooth scrolling at six lines per second could not meet',
        burstBody(setFour), [notRecognizedFour], { expect: 'burst' }),
    ask('burst-held', 'protocol', 'with the mode left set by the group before and not mentioned, the same burst again ends in the identical state inside the bound',
        burstBody(''), [notRecognizedFour], { expect: 'burst', reseed: false }),
    ask('burst-reset', 'protocol', 'returning to jump scrolling (CSI ? 4 l) the burst ends in the identical state inside the bound', burstBody(resetFour), [notRecognizedFour], { expect: 'burst' }),
    ask('restored', 'protocol', 'after all of them the mode is still not recognized and a plain exchange leaves everything unchanged', resetFour + queryFour, [notRecognizedFour]),
    ask('paced', 'control', 'the timing instrument control: the worker holds 2.5 seconds after the burst exchange, and the same timing reading must reach that pause (it is longer than the bound), so a slow exchange is visible in that field',
        burstBody(setFour), [notRecognizedFour], { expect: 'burst', pauseMs: dslPauseMs }),
    ask('control-clear', 'control', 'ED 2 and CUP 1;1 visibly clear the screen and home the cursor in the same state reader, so it can see what a changed state looks like', csi('2J') + csi('H'), [], { expect: 'cleared' }),
]);

/** The plan: the seed's cursor placed away from home with the plain witness written, an initial cells read, then for each group the reseed (where contracted), the captured request and its report read. */
function planOf() {
    const plan = [planFeed(reseedFeed), planRead('cells')];
    for (const item of groups) {
        if (item.reseed && item.expect !== 'cleared') plan.push(planFeed(reseedFeed));
        plan.push({ feed: item.request, capture: true }, planRead('report'));
    }
    return plan;
}

const appearanceScope = 'DEC private mode 4 (DECSCLM) only: the exact pinned termless exchange, the exact pinned mode query, set and reset alone, and a forty-line scroll burst of the margin band with smooth scrolling selected, held, '
    + 'reset and never touched, each leaving the complete screen, the cursor, the current rendition (witnessed by a plain character rewritten after every exchange), margins, modes and active screen as contracted '
    + '(identical across the burst groups) and every exchange inside a two-second bound that smooth scrolling at six lines per second could not meet, beside a timing instrument control that reads a deliberate '
    + '2.5-second hold, a recognised-mode control and a clear/home control; raw replies, stored terminal state and the parent\'s monotonic timing, not a rendered frame or painted pixels; '
    + 'no remote/mux, other-platform, common-harness or score claim; the pinned verdict stays inconclusive on both paths';

// ---------- state, seed and grading ----------

const printable = raw => raw.replaceAll(esc, String.raw`\e`);

/** One journaled observation group: the full screen beside the ink, cursor, geometry, margins, modes, timing and reply facts every exchange is judged on. */
const groupOf = (screen, timing, replies) => ({
    screen, replies,
    ink: selectiveEraseInk(screen),
    at: [screen.cursorRow, screen.cursorCol],
    cursorVisible: screen.cursorVisible, cursorMode: screen.cursorMode,
    size: [screen.rows, screen.cols],
    elapsedMs: timing.elapsedMs,
    bands: [screen.bufferState.scrollTop, screen.bufferState.scrollBottom],
    origin: screen.bufferState.originMode,
    alternate: screen.bufferState.alternateScreen,
    buffer: screen.bufferState,
    horizontalMargins: screen.horizontalMargins,
});
/** Everything an exchange must leave unchanged: the ink projection, the cursor facts, the buffer state, the horizontal margins and the geometry. */
const stateKey = state => JSON.stringify([state.ink, state.at, state.cursorVisible, state.cursorMode, state.buffer, state.horizontalMargins, state.size]);
const perRowAt = (cells, row) => cells.filter(cell => cell.row === row).length;
const sameSize = (left, right) => left[0] === right[0] && left[1] === right[1];
/** The witness cell: the plain character with the complete neutral stored tuple (every attribute clear, default colors by source, index and literal RGB, no underline color, not protected). */
const plainCell = cell => cell !== undefined && cell.char === witnessChar && sgrAttrsPredicates.neutral(cell) && !cell.protected;

/** Whether the initial read holds the contracted seed: geometry, nonce row, the cursor away from home over a plain witness, the attributed cells, margins and modes, and room for the burst. */
function seedOk(state, nonce) {
    const { seedCells, seedMargins } = devqFixture;
    const perRow = row => state.ink.filter(cell => cell.row === row).length;
    const holds = seedCells.every(spec => {
        const cell = state.ink.find(item => item.row === spec.row && item.col === spec.col);
        return cell !== undefined && cell.char === spec.char && cell.bold === (spec.bold ?? false)
            && (spec.fgIndex === undefined || cell.fgIndex === spec.fgIndex) && (spec.underline === undefined || cell.underline === spec.underline);
    });
    const witnessCell = state.ink.find(item => item.row === dslCursorAt[0] && item.col === dslCursorAt[1]);
    return state.size[0] >= dslMinimum.rows && state.size[1] >= dslMinimum.cols && state.screen.lines[0] === nonce
        && state.at[0] === dslCursorAt[0] && state.at[1] === dslCursorAt[1] && plainCell(witnessCell)
        && state.bands[0] === seedMargins[0] && state.bands[1] === seedMargins[1] && state.origin === false && state.alternate === true
        && perRow(1) === perRowAt(seedCells, 1) && perRow(3) === perRowAt(seedCells, 3) + 1 && holds;
}

/** The state a burst must leave, derived from the seed by the contract's own literals: the nonce row as it was, the whole margin band blank but for the witness the exchange rewrote on the bottom margin row,
 *  the cursor on that cell, and everything else (the modes, margins, geometry) as seeded. */
function burstMismatch(state, initial) {
    const [bottom, left] = dslBurstCursor;
    const nonceRow = cells => JSON.stringify(cells.filter(cell => cell.row === 0));
    const rest = state.ink.filter(cell => cell.row !== 0);
    if (nonceRow(state.ink) !== nonceRow(initial.ink)) return 'the nonce row changed';
    if (rest.length !== 1 || rest[0].row !== bottom || rest[0].col !== left || !plainCell(rest[0])) return 'the margin band did not end blank but for the plain witness on the bottom margin row';
    if (state.at[0] !== bottom || state.at[1] !== left) return 'the cursor did not end on the bottom margin row at column 0';
    const modes = state => JSON.stringify([state.cursorVisible, state.cursorMode, state.buffer, state.horizontalMargins, state.size]);
    return modes(state) === modes(initial) ? null : 'the cursor mode, the buffer state (margins, wrap, insert, active screen), the horizontal margins or the geometry changed';
}

const inconclusive = note => ({ outcome: 'inconclusive', note });
const unsupported = note => ({ outcome: 'unsupported', note });

/** The way one group differs from its demand, or null: timing first (a paced control must reach its pause, every other exchange must stay inside the bound), then the state or the clear, then the exact
 *  reply list. A burst is judged against the contract's own literal outcome, the same for every burst group, so no burst can differ from another and pass. */
function groupMismatch(group, state, initial) {
    if (group.pauseMs === null ? state.elapsedMs > dslTimingBoundMs : state.elapsedMs < group.pauseMs) {
        return group.pauseMs === null
            ? `The ${group.role} exchange took ${state.elapsedMs} ms, over the ${dslTimingBoundMs} ms bound: output scrolling was paced`
            : `The timing instrument read ${state.elapsedMs} ms for the ${group.role} exchange that was held ${group.pauseMs} ms: it cannot see a slow exchange`;
    }
    if (!sameSize(state.size, initial.size)) return `The ${group.role} request changed the grid (${initial.size.join('x')} to ${state.size.join('x')})`;
    if (group.expect === 'cleared') {
        if (state.ink.length !== 0 || !state.at.every(value => value === 0)) return `The ${group.role} request did not clear the screen and home the cursor the way the state reader must see`;
    } else if (group.expect === 'burst') {
        const wrong = burstMismatch(state, initial);
        if (wrong !== null) return `The ${group.role} burst did not end as a jump scroll must: ${wrong}`;
    } else if (stateKey(state) !== stateKey(initial)) {
        return `The ${group.role} request changed the screen, the cursor (position, visibility or stored mode), the rendition, the margins (vertical or horizontal), the wrap/insert modes or the active screen: an ignored DECSCLM must change nothing`;
    }
    if (JSON.stringify(state.replies) === JSON.stringify(group.demand)) return null;
    if (group.demand.length === 0) return `The ${group.role} request was answered with ${state.replies.length} repl${state.replies.length === 1 ? 'y' : 'ies'} where the contract demands none`;
    return `The ${group.role} replies differ from the oracle: ${state.replies.length} received where ${group.demand.length} were demanded, or a mode state differs from not recognized`;
}

/** The case's grade in execution order, so an earlier failure is never overridden by a later group it damaged: the seed, then each group as the plan ran it. Every group holding (the controls included)
 *  makes the effect `unsupported`: DECSCLM is ignored and nothing paced the scrolling. Any mismatch is inconclusive: this contract defines only the ignored behavior. */
function gradeCase(item, observation) {
    if (!seedOk(observation.initial, observation.nonce)) return inconclusive('The attributed seed (nonce, cells, margins, modes and a witness away from home) was not measured before any request');
    for (const group of item.groups) {
        const mismatch = groupMismatch(group, observation[group.role], observation.initial);
        if (mismatch !== null) return inconclusive(group.kind === 'control' ? `Setup control failed before the effect could be judged: ${mismatch}` : `${mismatch}; behavior that differs from the ignored contract needs its own supported contract`);
    }
    return unsupported('DECSCLM was ignored: with smooth scrolling selected, held, reset and never touched the processing of a forty-line scrolling burst finished identically inside the timing bound (the stored state, read after the fence, is the final scrolled state), mode 4 stayed not recognized and nothing else changed');
}

/** The observed record of one case: every group with its demanded and received replies, its timing and whether the state was unchanged (or cleared, for the clear control). */
function briefCase(item, observation) {
    const groupsOut = Object.fromEntries(item.groups.map(group => {
        const state = observation[group.role];
        return [group.role, { kind: group.kind, request: printable(group.request), demand: group.note, demanded: group.demand.map(printable),
            received: state.replies.map(printable), matches: JSON.stringify(state.replies) === JSON.stringify(group.demand),
            app: { rows: state.size[0], cols: state.size[1] }, elapsedMs: state.elapsedMs, ...(group.pauseMs === null ? {} : { pauseMs: group.pauseMs }),
            unchanged: group.expect === 'unchanged' ? stateKey(state) === stateKey(observation.initial) : null,
            ...(group.expect === 'burst' ? { burst: burstMismatch(state, observation.initial) === null } : {}) }];
    }));
    const [rows, cols] = observation.initial.size, script = item.pinnedScript;
    return { geometry: { rows, cols }, timing: { boundMs: dslTimingBoundMs, pauseMs: dslPauseMs, burstLines: dslBurstLines, smoothLinesPerSecond: dslSmoothLinesPerSecond,
            smoothFloorMs: Math.round(dslBurstLines / dslSmoothLinesPerSecond * 1000), clock: 'parent-monotonic' }, effectContract: dslEffectContractId,
        pinnedScript: { termless: script.termless.map(call => call.map(part => (typeof part === 'string' ? printable(part) : part))), terminal: script.terminal.map(call => call.map(part => (typeof part === 'string' ? printable(part) : part))) },
        seed: { nonce: observation.nonce, margins: observation.initial.bands, origin: observation.initial.origin, alternate: observation.initial.alternate,
            cells: seedOk(observation.initial, observation.nonce) }, groups: groupsOut };
}

/** One contracted case. */
function makeContract(caseId) {
    const script = dslPinnedScripts[caseId];
    const roles = Object.freeze(['initial', ...groups.map(group => group.role)]);
    const termlessGroup = groups.find(group => group.role === 'pinned'), terminalGroup = groups.find(group => group.role === 'pinned-query');
    if (termlessGroup.request !== setFour + queryFour + resetFour + witness || terminalGroup.request !== queryFour + witness) throw new Error('The pinned groups must send exactly the pinned requests, then the witness.');
    if (dslBurstLines / dslSmoothLinesPerSecond * 1000 < dslTimingBoundMs * 2 || dslPauseMs <= dslTimingBoundMs) throw new Error('The burst must outlast smooth scrolling by twice the bound and the pause must exceed the bound.');
    const contract = {
        caseId, probeId: caseId, id: caseId.slice('modes.'.length), roles, groups, plan: Object.freeze(planOf()),
        pinnedPath: 'termless', pinnedRead: roles.indexOf('pinned'), pinnedTerminalRead: roles.indexOf('pinned-query'), pinnedExpected: script.expected, pinnedCriterion, pinnedScript: script,
        pinnedFeeds: Object.freeze([setFour, queryFour, resetFour]), pinnedSetup: false,
        terminalFixture: { queries: [queryFour], demand: 'none: the pinned terminal callback asks queryMode(4) and returns decrpmResult', minimum: { rows: 1, cols: 1 },
            note: 'inconclusive here because mode 4 is not recognized (Pm 0): the stock grade is carried unchanged; replayed over the journaled replies of the pinned query\'s own exchange' },
        effectContractId: dslEffectContractId, appearanceScope, evidence: 'owned-raw-replies-state-and-timing',
        grade: observation => gradeCase(contract, observation), observed: observation => briefCase(contract, observation),
    };
    return Object.freeze(contract);
}

export const dslContracts = Object.freeze(dslCaseIds.map(makeContract));
/** Contract case ids in suite order. */
export const dslContractCaseIds = Object.freeze(dslContracts.map(item => item.caseId));
export { planFeeds as dslFeeds, planReads as dslReads } from './graded-plan.mjs';

/** The seed bytes of the plan (the reset followed by the attributed seed and nonce). */
export function dslPlanSeed(runId) {
    if (!/^[a-f\d]{32}$/.test(runId ?? '')) throw new Error('Invalid decsclm fixture run identity.');
    return dslReset + dslSeed(dslNoncePrefix + runId);
}
/** The finish: smooth scrolling reset (in case a terminal tracked it), the screen reset, the baseline title written, the alternate screen left. */
export const dslFinish = runId => resetFour + dslReset + `${esc}]0;${dslBaseTitle(runId)}${esc}\\` + '\x1b[?1049l';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateDslFeeds(item, feeds) { validateExactFeeds(item, feeds, 'contract'); }

/** Group the read frames and replies of the case by role. */
export function dslObservation(item, frames, replies, nonce) {
    const reads = item.plan.filter(step => step.read);
    if (!Array.isArray(frames) || frames.length !== reads.length || !Array.isArray(replies) || replies.length !== reads.length || typeof nonce !== 'string') {
        throw new Error('Missing decsclm read captures.');
    }
    const observation = Object.fromEntries(item.roles.map((role, index) => [role, groupOf(frames[index].screen, frames[index].timing, replies[index])]));
    observation.nonce = nonce;
    return observation;
}

/** Independent contract grader entry. */
export function gradeDslCase(item, observation, revision = dslContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown decsclm contracts revision ${revision}.`);
    if (!observation || item.roles.some(role => !observation[role] || !Array.isArray(observation[role].ink))) throw new Error(`Missing ${item.caseId} read captures.`);
    return item.grade(observation);
}

/** The fixed fixture facts other modules and tests read. */
export const dslFixture = Object.freeze({ minimum: dslMinimum, seedMargins: devqFixture.seedMargins, seedCells: devqFixture.seedCells, notRecognizedFour, queryFour, setFour, resetFour, reseedFeed });
