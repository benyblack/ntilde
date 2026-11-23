// Versioned probe contract for the DECCOLM qualification batch: modes.deccolm (DEC private mode 3, the 80/132-column switch). This is not a publication or a measurement. The batch's first integrated
// graph is v72; its receipt schema is 79.
//
// Pinned terminfo.dev@4.0.0 definition (bundle SHA-256 aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6), read from the bundle, never inferred from the name. The case is registered as
// `decrpmModeProbe("modes.deccolm", 3, termlessFn)`:
//   termless  feed CSI ? 3 h, feedCapture CSI ? 3 $ p, feed CSI ? 3 l; when the captured reply holds `$y` the probe returns parserStateResult(null, ..., { response, cols }) (inconclusive,
//             insufficient-evidence, evidence parser-state: "DECRPM state does not establish a width transition"); without it the probe feeds X, reads the text and returns the same inconclusive verdict
//             ("Parser responsiveness does not measure a width transition"). It can never be supported or unsupported.
//   terminal  decrpmResult(await ctx.queryMode(3), 3): the DA-sentinel query CSI ? 3 $ p; a recognised state (set or reset) is supported, "not recognized" (Pm 0) is inconclusive
//             (insufficient-evidence, evidence query, "mode behavior unmeasured"), no reply is inconclusive (no-response).
// Neither path measures a width, so a DECRPM acknowledgement alone would score without behavior; teaching DECRQM about mode 3 without a width transition is exactly what the batch must not do. Ntilde does
// not track mode 3 (DECRQM answers Pm 0), so the pinned verdict is inconclusive on both paths and this contract never promotes or demotes it.
//
// The authoritative contract is xterm's: DECCOLM (CSI ? 3 h / l) switches the screen between 132 and 80 columns, clears the screen, homes the cursor and resets the margins, and is honoured only
// while DECCOLM switching is allowed (CSI ? 40 h). Ntilde's deliberate policy (#1000 option A) is that the App has no program-initiated resize path (pane size comes from layout), so mode 3 and mode 40 are
// ignored whole: no width change, no clear, no home, no margin change, no DECRPM recognition. The graded effect is `unsupported` when every protocol group leaves the complete screen, cursor, rendition,
// margins, modes and active screen unchanged AND the App grid and the independently read kernel PTY grid both keep their baseline size at every read, with mode 3 and mode 40 still answered "not recognized"
// beside control groups proving that the reply channel answers recognised modes and that the state reader sees a clear and a home. Any other observation makes the effect inconclusive: a terminal that
// started honouring mode 3 needs its own supported contract rather than being graded as unsupported here.
//
// Geometry: every read frame carries the App's grid (the paired screen's rows and columns, settled after a DA barrier) and the kernel's grid read by the parent with the system `/bin/stty` against the proved
// private PTY. They must agree with each other and with the baseline at every read. The baseline width must be neither 80 nor 132, so that both a set and a reset would be visible. An App grid that did
// change is not even gradable: the observer's fixed-size guard and the journal refuse a frame of another grid, so such a run is refused rather than judged (the contract grader still states that case);
// the kernel grid changing alone is a graded disagreement. The sensitivity of this reader pair to a real width change is not re-measured here: it is the reader pair of the retained owned resize
// measurement (docs/qa/terminfo/2026-11-03-resize.md), where parent-selected App resizes were followed by both readers. A transition completed after a barrier would still be caught: every later read re-reads
// both grids.
//
// Limits: raw replies, stored terminal state and PTY geometry only; no window-manager size, remote/mux, other-platform, common-harness or score claim.
import { planFeed, planRead, validateExactFeeds } from './graded-plan.mjs';
import { selectiveEraseInk } from './selective-erase-contracts.mjs';
import { devqFixture, devqMinimum, devqReseed, devqReset, devqSeed } from './devq-contracts.mjs';
export { validateDevqScreen as validateDclScreen } from './devq-contracts.mjs';

/** Contracts v1 is the first measured semantics of this batch. */
export const dclContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const dclSuiteId = 'ntilde-owned-deccolm-graded-v1';
/** The separately identified effect contract. */
export const dclEffectContractId = 'ntilde-deccolm-unsupported-effect-v1';
/** The nonce prefix binding worker output to this suite. */
export const dclNoncePrefix = 'NTILDE_DCL_';
/** Owner-protocol prefix of this suite's observation requests. */
export const dclPrefix = 'dcl';
/** The pinned case. */
export const dclCaseIds = Object.freeze(['modes.deccolm']);
/** The measured geometry the controls need (the shared device-query seed: margins 2..6, a nonce row and an attributed seed row). */
export const dclMinimum = devqMinimum;
export const dclReset = devqReset;
export const dclReseed = devqReseed;
export const dclSeed = devqSeed;
/** The run's baseline title, written by the finish so the pane never keeps a test label. */
export const dclBaseTitle = runId => `DCL-base-${runId.slice(0, 8)}`;
/** The two widths a real DECCOLM switches between: a baseline on either would hide one direction. */
export const deccolmWidths = Object.freeze([80, 132]);

const esc = '\x1b';
const csi = body => `${esc}[${body}`;
const setThree = csi('?3h'), resetThree = csi('?3l'), queryThree = csi('?3$p');
const allow = csi('?40h'), disallow = csi('?40l'), queryForty = csi('?40$p');
const reply = (mode, pm) => csi(`?${mode};${pm}$y`);
/** Ntilde does not track mode 3 or mode 40: DECRQM answers Pm 0 (not recognized). */
const notRecognizedThree = reply(3, 0), notRecognizedForty = reply(40, 0);

// ---------- the pinned probe (read from the bundle) ----------

/** The exact calls each pinned path makes, in order. `replies` names which journaled bytes the captured call returns. */
export const dclPinnedScripts = Object.freeze({
    'modes.deccolm': Object.freeze({
        termless: Object.freeze([['feed', setThree], ['feedCapture', queryThree, 'replies'], ['feed', resetThree]]),
        terminal: Object.freeze([['queryMode', 3]]),
        expected: 'DECCOLM switches the measured grid width',
    }),
});
const pinnedCriterion = 'decrpmModeProbe("modes.deccolm", 3, termlessFn): the termless path feeds CSI ? 3 h, captures the CSI ? 3 $ p reply and feeds CSI ? 3 l, then returns parserStateResult(null, ...) '
    + '(inconclusive, insufficient-evidence, parser-state) whether or not the reply holds $y; the terminal path asks queryMode(3) and returns decrpmResult: supported only for a recognised set/reset state, '
    + 'inconclusive for Pm 0 (not recognized) or no reply; neither path measures a width';

const decrpmPattern = /\x1b\[\?3;([0-4])\$y/;
/** The state the pinned queryMode derives from a reply list: null (no matching reply), "set", "reset" or "unknown". */
export function pinnedModeState(replies) {
    const match = decrpmPattern.exec(replies.join(''));
    if (match === null) return null;
    return { 0: 'unknown', 1: 'set', 2: 'reset', 3: 'set', 4: 'reset' }[match[1]];
}

/** The pinned termless result over the journaled reply text and the App grid width of the case's own exchange. A reply without `$y` would make the pinned code feed X and read the text, which this owned
 *  run never serves, so the replay refuses it instead of inventing the rest of the fixture. */
export function replayDclTermless(caseId, replies, cols) {
    if (!(caseId in dclPinnedScripts)) throw new Error(`Unknown deccolm case ${caseId}.`);
    if (typeof replies !== 'string') throw new Error('Missing deccolm reply text.');
    if (!Number.isSafeInteger(cols) || cols <= 0) throw new Error('Missing deccolm grid width.');
    if (!replies.includes('$y')) throw new Error(`The ${caseId} DECRPM query was not answered: the pinned code would continue to a text probe this owned run never serves.`);
    const note = 'DECRPM state does not establish a width transition';
    return { pass: false, response: JSON.stringify({ response: replies, cols }), note, observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'parser-state', note } };
}

/** The pinned terminal result: decrpmResult over the state queryMode derives from the journaled reply list of the case's own mode query. */
export function replayDclTerminal(caseId, replies) {
    if (!(caseId in dclPinnedScripts)) throw new Error(`Unknown deccolm case ${caseId}.`);
    if (!Array.isArray(replies)) throw new Error('Missing deccolm reply list.');
    const state = pinnedModeState(replies);
    if (state === null) return { pass: false, note: 'No DECRPM response', observation: { outcome: 'inconclusive', reason: 'no-response', evidence: 'query', note: 'No DECRPM response' } };
    if (state === 'unknown') {
        const note = 'DECRPM mode 3: not recognized; mode behavior unmeasured';
        return { pass: false, note, response: state, observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'query', note } };
    }
    throw new Error(`The ${caseId} mode query was recognised as ${state}: the pinned terminal path would pass without a width measurement; this owned run never serves it.`);
}

// ---------- the groups ----------

const ask = (role, kind, note, request, demand, expect = 'unchanged') => {
    // The owned fence refuses any exchange request over 64 characters (terminal-fence.mjs); the contract
    // self-check keeps every group inside that bound instead of discovering it at collection time.
    if (request.length > 64) throw new Error(`The ${role} request exceeds the 64-byte owned exchange limit.`);
    return Object.freeze({ role, kind, note, request, demand: Object.freeze(demand), expect });
};

/** The groups, in execution order. A `control` gates the verdict (a failing control makes the effect inconclusive); a `protocol` group demands the ignored behavior. Every group is judged on its own bytes: the
 *  state, both geometries and the reply list after that group's exchange. The clear/home control runs last because it destroys the fixture it is compared with. */
const groups = Object.freeze([
    ask('recognized', 'control', 'the reply channel answers recognised modes (DECAWM set, DECANM set), so a Pm 0 answer for mode 3 is a real "not recognized" and silence is not a dead channel',
        csi('?7$p') + csi('?2$p'), [reply(7, 1), reply(2, 1)]),
    ask('pinned', 'protocol', 'the exact pinned termless exchange (CSI ? 3 h, the CSI ? 3 $ p query, CSI ? 3 l) leaves everything unchanged and mode 3 is answered not recognized',
        setThree + queryThree + resetThree, [notRecognizedThree]),
    ask('pinned-query', 'protocol', 'the exact pinned terminal-path query alone (CSI ? 3 $ p) is answered not recognized', queryThree, [notRecognizedThree]),
    ask('set', 'protocol', 'CSI ? 3 h alone changes no width, clears nothing, homes nothing and mode 3 stays not recognized', setThree + queryThree, [notRecognizedThree]),
    ask('reset', 'protocol', 'CSI ? 3 l alone changes nothing and mode 3 stays not recognized', resetThree + queryThree, [notRecognizedThree]),
    ask('allow', 'protocol', 'CSI ? 40 h (allow 80/132 switching) changes nothing and neither mode 3 nor mode 40 is recognized', allow + queryThree + queryForty, [notRecognizedThree, notRecognizedForty]),
    ask('allow-set', 'protocol', 'with switching allowed, CSI ? 3 h changes no width and nothing else', allow + setThree + queryThree, [notRecognizedThree]),
    ask('allow-reset', 'protocol', 'with switching allowed, CSI ? 3 l changes no width and nothing else', allow + resetThree + queryThree, [notRecognizedThree]),
    ask('allow-cycle', 'protocol', 'with switching allowed, a full set, reset, set, reset cycle changes no width and nothing else', allow + setThree + resetThree + setThree + resetThree + queryThree, [notRecognizedThree]),
    ask('disallow-set', 'protocol', 'with switching not allowed (CSI ? 40 l), CSI ? 3 h changes nothing', disallow + setThree + queryThree, [notRecognizedThree]),
    ask('restored', 'protocol', 'after all of them the mode is still not recognized, the width is still the baseline and nothing changed', resetThree + disallow + queryThree, [notRecognizedThree]),
    ask('control-clear', 'control', 'ED 2 and CUP 1;1 visibly clear the screen and home the cursor in the same readers, so the state reader can see what a DECCOLM would have done', csi('2J') + csi('H'), [], 'cleared'),
]);

/** The plan: the reseed, an initial cells read, then for each group the captured request and its report read. */
function planOf() {
    const plan = [planFeed(dclReseed), planRead('cells')];
    for (const item of groups) plan.push({ feed: item.request, capture: true }, planRead('report'));
    return plan;
}

const appearanceScope = 'DEC private mode 3 (DECCOLM) and mode 40 (allow 80/132 switching) only: the exact pinned termless exchange, the exact pinned mode query, set and reset alone, with switching allowed and not allowed, '
    + 'and a set/reset cycle, each leaving the complete screen, cursor, rendition, margins, modes and active screen unchanged and the App grid and the independently read kernel PTY grid at their baseline size, with '
    + 'mode 3 and mode 40 answered not recognized beside a recognised-mode control and a clear/home control; raw replies, stored terminal state and PTY geometry, not a window size, rendering or painted pixels; '
    + 'no remote/mux, other-platform, common-harness or score claim; the pinned verdict stays inconclusive on both paths';

// ---------- state, seed and grading ----------

const printable = raw => raw.replaceAll(esc, String.raw`\e`);

/** One journaled observation group: the full screen beside the ink, cursor, App and kernel geometry, margins, modes and reply facts every exchange must leave unchanged. */
const groupOf = (screen, kernel, replies) => ({
    screen, replies,
    ink: selectiveEraseInk(screen),
    at: [screen.cursorRow, screen.cursorCol],
    cursorVisible: screen.cursorVisible, cursorMode: screen.cursorMode,
    size: [screen.rows, screen.cols],
    kernel: [kernel.rows, kernel.cols],
    bands: [screen.bufferState.scrollTop, screen.bufferState.scrollBottom],
    origin: screen.bufferState.originMode,
    alternate: screen.bufferState.alternateScreen,
    buffer: screen.bufferState,
    horizontalMargins: screen.horizontalMargins,
});
/** Everything an exchange must leave unchanged: the ink projection, the cursor facts, the buffer state, the horizontal margins and both geometries. */
const stateKey = state => JSON.stringify([state.ink, state.at, state.cursorVisible, state.cursorMode, state.buffer, state.horizontalMargins, state.size, state.kernel]);
const perRowAt = (cells, row) => cells.filter(cell => cell.row === row).length;
const sameSize = (left, right) => left[0] === right[0] && left[1] === right[1];

/** Whether the initial read holds the contracted seed: geometry, nonce at home, the attributed cells, margins and modes, and a width a DECCOLM would visibly change in either direction. */
function seedOk(state, nonce) {
    const { seedCells, seedMargins } = devqFixture;
    const perRow = row => state.ink.filter(cell => cell.row === row).length;
    const holds = seedCells.every(spec => {
        const cell = state.ink.find(item => item.row === spec.row && item.col === spec.col);
        return cell !== undefined && cell.char === spec.char && cell.bold === (spec.bold ?? false)
            && (spec.fgIndex === undefined || cell.fgIndex === spec.fgIndex) && (spec.underline === undefined || cell.underline === spec.underline);
    });
    return state.size[0] >= dclMinimum.rows && state.size[1] >= dclMinimum.cols && !deccolmWidths.includes(state.size[1]) && state.screen.lines[0] === nonce && state.at.every(value => value === 0)
        && state.bands[0] === seedMargins[0] && state.bands[1] === seedMargins[1] && state.origin === false && state.alternate === true
        && perRow(1) === perRowAt(seedCells, 1) && perRow(3) === perRowAt(seedCells, 3) && holds;
}

const inconclusive = note => ({ outcome: 'inconclusive', note });
const unsupported = note => ({ outcome: 'unsupported', note });

/** The way one group differs from its demand, or null: both geometries first (they must agree and keep the baseline), then the state or the clear, then the exact reply list. */
function groupMismatch(group, state, initial) {
    if (!sameSize(state.size, state.kernel)) return `The App grid ${state.size.join('x')} and the kernel PTY grid ${state.kernel.join('x')} disagree after the ${group.role} request`;
    if (!sameSize(state.size, initial.size) || !sameSize(state.kernel, initial.kernel)) {
        return `The ${group.role} request changed the grid width or height (App ${initial.size.join('x')} to ${state.size.join('x')}, kernel ${initial.kernel.join('x')} to ${state.kernel.join('x')}): DECCOLM would have switched the grid`;
    }
    if (group.expect === 'cleared') {
        if (state.ink.length !== 0 || !state.at.every(value => value === 0)) return `The ${group.role} request did not clear the screen and home the cursor the way the state reader must see`;
    } else if (stateKey(state) !== stateKey(initial)) {
        return `The ${group.role} request changed the screen, the cursor (position, visibility or stored mode), the rendition, the margins (vertical or horizontal), the wrap/insert modes or the active screen: a DECCOLM that is ignored must change nothing`;
    }
    if (JSON.stringify(state.replies) === JSON.stringify(group.demand)) return null;
    if (group.demand.length === 0) return `The ${group.role} request was answered with ${state.replies.length} repl${state.replies.length === 1 ? 'y' : 'ies'} where the contract demands none`;
    return `The ${group.role} replies differ from the oracle: ${state.replies.length} received where ${group.demand.length} were demanded, or a mode state differs from not recognized`;
}

/** The case's grade in execution order, so an earlier failure is never overridden by a later group it damaged: the seed, then each group as the plan ran it. Every group holding (the controls included)
 *  makes the effect `unsupported`: DECCOLM is ignored and the measured quantities could move. Any mismatch is inconclusive: this contract defines only the ignored behavior. */
function gradeCase(item, observation) {
    if (!seedOk(observation.initial, observation.nonce)) return inconclusive('The attributed seed (nonce, cells, margins, modes and a width other than 80 and 132) was not measured before any request');
    for (const group of item.groups) {
        const mismatch = groupMismatch(group, observation[group.role], observation.initial);
        if (mismatch !== null) return inconclusive(group.kind === 'control' ? `Setup control failed before the effect could be judged: ${mismatch}` : `${mismatch}; behavior that differs from the ignored contract needs its own supported contract`);
    }
    return unsupported('DECCOLM and the allow-switching mode were ignored: no width change in the App or the kernel PTY, no clear, home or margin change, and mode 3 and mode 40 stayed not recognized');
}

/** The observed record of one case: every group with its demanded and received replies, both grids and whether the state was unchanged (or cleared, for the clear control). */
function briefCase(item, observation) {
    const groupsOut = Object.fromEntries(item.groups.map(group => {
        const state = observation[group.role];
        return [group.role, { kind: group.kind, request: printable(group.request), demand: group.note, demanded: group.demand.map(printable),
            received: state.replies.map(printable), matches: JSON.stringify(state.replies) === JSON.stringify(group.demand),
            app: { rows: state.size[0], cols: state.size[1] }, kernel: { rows: state.kernel[0], cols: state.kernel[1] },
            unchanged: group.expect === 'cleared' ? null : stateKey(state) === stateKey(observation.initial) }];
    }));
    const [rows, cols] = observation.initial.size, script = item.pinnedScript;
    return { geometry: { rows, cols, kernelRows: observation.initial.kernel[0], kernelCols: observation.initial.kernel[1] }, effectContract: dclEffectContractId,
        pinnedScript: { termless: script.termless.map(call => call.map(part => (typeof part === 'string' ? printable(part) : part))), terminal: script.terminal.map(call => call.map(part => (typeof part === 'string' ? printable(part) : part))) },
        seed: { nonce: observation.nonce, margins: observation.initial.bands, origin: observation.initial.origin, alternate: observation.initial.alternate,
            cells: seedOk(observation.initial, observation.nonce) }, groups: groupsOut };
}

/** One contracted case. */
function makeContract(caseId) {
    const script = dclPinnedScripts[caseId];
    const roles = Object.freeze(['initial', ...groups.map(group => group.role)]);
    const termlessGroup = groups.find(group => group.role === 'pinned'), terminalGroup = groups.find(group => group.role === 'pinned-query');
    if (termlessGroup.request !== setThree + queryThree + resetThree || terminalGroup.request !== queryThree) throw new Error('The pinned groups must send exactly the pinned requests.');
    const contract = {
        caseId, probeId: caseId, id: caseId.slice('modes.'.length), roles, groups, plan: Object.freeze(planOf()),
        pinnedPath: 'termless', pinnedRead: roles.indexOf('pinned'), pinnedTerminalRead: roles.indexOf('pinned-query'), pinnedExpected: script.expected, pinnedCriterion, pinnedScript: script,
        pinnedFeeds: Object.freeze([setThree, queryThree, resetThree]), pinnedSetup: false,
        terminalFixture: { queries: [queryThree], demand: 'none: the pinned terminal callback asks queryMode(3) and returns decrpmResult', minimum: { rows: 1, cols: 1 },
            note: 'inconclusive here because mode 3 is not recognized (Pm 0): the stock grade is carried unchanged; replayed over the journaled replies of the pinned query\'s own exchange' },
        effectContractId: dclEffectContractId, appearanceScope, evidence: 'owned-raw-replies-state-and-pty-geometry',
        grade: observation => gradeCase(contract, observation), observed: observation => briefCase(contract, observation),
    };
    return Object.freeze(contract);
}

export const dclContracts = Object.freeze(dclCaseIds.map(makeContract));
/** Contract case ids in suite order. */
export const dclContractCaseIds = Object.freeze(dclContracts.map(item => item.caseId));
export { planFeeds as dclFeeds, planReads as dclReads } from './graded-plan.mjs';

/** The seed bytes of the plan (the reset followed by the attributed seed and nonce). */
export function dclPlanSeed(runId) {
    if (!/^[a-f\d]{32}$/.test(runId ?? '')) throw new Error('Invalid deccolm fixture run identity.');
    return dclReset + dclSeed(dclNoncePrefix + runId);
}
/** The finish: switching disallowed and the mode reset (in case a terminal tracked them), the screen reset, the baseline title written, the alternate screen left. */
export const dclFinish = runId => disallow + resetThree + dclReset + `${esc}]0;${dclBaseTitle(runId)}${esc}\\` + '\x1b[?1049l';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateDclFeeds(item, feeds) { validateExactFeeds(item, feeds, 'contract'); }

/** Group the read frames and replies of the case by role. */
export function dclObservation(item, frames, replies, nonce) {
    const reads = item.plan.filter(step => step.read);
    if (!Array.isArray(frames) || frames.length !== reads.length || !Array.isArray(replies) || replies.length !== reads.length || typeof nonce !== 'string') {
        throw new Error('Missing deccolm read captures.');
    }
    const observation = Object.fromEntries(item.roles.map((role, index) => [role, groupOf(frames[index].screen, frames[index].kernel, replies[index])]));
    observation.nonce = nonce;
    return observation;
}

/** Independent contract grader entry. */
export function gradeDclCase(item, observation, revision = dclContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown deccolm contracts revision ${revision}.`);
    if (!observation || item.roles.some(role => !observation[role] || !Array.isArray(observation[role].ink))) throw new Error(`Missing ${item.caseId} read captures.`);
    return item.grade(observation);
}

/** The fixed fixture facts other modules and tests read. */
export const dclFixture = Object.freeze({ minimum: dclMinimum, seedMargins: devqFixture.seedMargins, seedCells: devqFixture.seedCells, notRecognizedThree, notRecognizedForty, queryThree, setThree, resetThree });
