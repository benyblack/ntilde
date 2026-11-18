// Versioned probe contract for the iTerm2 feature-reporting qualification batch: extensions.osc1337-capabilities (OSC 1337 ; RequestCapabilities). This is not a publication or a measurement.
// The batch's first integrated graph is v70 (renumbered from v69 after the font-query batch merged first); its receipt schema is 77 and its query policy is v7.
//
// Pinned terminfo.dev@4.0.0 definition (bundle SHA-256 aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6), read from the bundle, never inferred from the name. It is a query-only probe
// registered through oscSimpleQueryProbe, so both of its paths can replay over the journaled raw bytes of their own fenced exchange:
//   termless  oscReplyResult(feedCapture(OSC 1337 ; RequestCapabilities BEL)): the complete frame ESC]1337;Capabilities=<characters without BEL or ESC> ended by BEL or ST is supported ("Complete OSC
//             1337 Capabilities reply"); a capture holding the ESC]1337;Capabilities= prefix without a complete frame is inconclusive/invalid-reply; no prefix is inconclusive/no-response.
//   terminal  queryWithSentinelOutcome(the same request, the same frame pattern): the sentinel outcome's reply goes through the same oscReplyResult (no match is inconclusive).
// It can never return unsupported, and its valid pattern accepts any feature string — so the pinned verdict alone never establishes which capabilities are honestly advertised. The graded conclusion
// belongs to the separately identified effect contract (ntilde-osc1337-capabilities-effect-v1): the reply must be exactly the specification-framed declaration whose codes each carry owning
// implementation and test evidence (docs/qa/terminfo/iterm-feature-reporting.md), transcribed here as an independent literal rather than read from the parser's constants.
//
// The authoritative contract is the iTerm2 feature-reporting specification: OSC 1337 ; RequestCapabilities is answered by OSC 1337 ; Capabilities = {FeatureString} ST, always ST-terminated whatever
// terminator the request used, changing no screen, cursor, rendition, margins, modes or active-screen state. Only the exact payload is a request: an unsolicited Capabilities= assignment, a bare
// Capabilities, a query spelling, any other case and any other 1337 payload stay silent (the image and cell-size forms keep their own handlers). The declaration is static per host-acceptance state:
// the measured run uses default initial settings, so the direct pane's host accepts OSC 52 writes and the Cw code is declared; a session whose host cannot answer omits Cw (recorded policy, unit-tested,
// not measured here).
//
// Limits: the reply's per-code behavior continues to be owned by each capability's own implementations and tests — this suite measures the declaration, its framing, its correlation and its silence
// boundaries; no rendering, painted-pixel, remote/mux, common-harness or score claim.
import { planFeed, planRead, validateExactFeeds } from './graded-plan.mjs';
import { selectiveEraseInk } from './selective-erase-contracts.mjs';
import { pinnedOscReply, replayPinnedPath } from './probe-suite-shared.mjs';
import { devqColorReset, devqFixture, devqMinimum, devqReseed, devqReset, devqSeed } from './devq-contracts.mjs';
export { validateDevqScreen as validateCapsScreen } from './devq-contracts.mjs';

/** Contracts v1 is the first measured semantics of this batch. */
export const capsContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const capsSuiteId = 'ntilde-owned-osc1337-capabilities-graded-v1';
/** The separately identified effect contract. */
export const capsEffectContractId = 'ntilde-osc1337-capabilities-effect-v1';
/** The nonce prefix binding worker output to this suite. */
export const capsNoncePrefix = 'NTILDE_CAP_';
/** Owner-protocol prefix of this suite's observation requests. */
export const capsPrefix = 'caps';
/** The pinned case, in suite order. */
export const capsCaseIds = Object.freeze(['extensions.osc1337-capabilities']);
/** The measured geometry the controls need (the shared device-query seed: margins 2..6, a nonce row and a coloured/bold/underlined seed row). */
export const capsMinimum = devqMinimum;
export const capsReset = devqReset;
export const capsReseed = devqReseed;
export const capsSeed = devqSeed;
/** The reset every phase ends with: every palette entry and the five dynamic colors back to the host's (this suite changes no color, and keeps the shared finish shape). */
export const capsColorReset = devqColorReset;
/** The run's baseline title, written by the finish so the pane never keeps a test label. */
export const capsBaseTitle = runId => `CAP-base-${runId.slice(0, 8)}`;

const esc = '\x1b';
const st = esc + '\\';
const bel = '\x07';
/** Build a regular expression from a source written with <ESC>, so no control character sits in a pattern literal. */
const re = (source, flags = '') => new RegExp(source.replaceAll('<ESC>', String.fromCodePoint(27)), flags);

// ---------- the pinned probe (read from the bundle) ----------

/** The pinned request (BEL-terminated, exactly as registered), and the frame pattern the pinned code searches the captured text for (text escapes, like the bundle spells them). */
export const capsPinnedQuery = `${esc}]1337;RequestCapabilities${bel}`;
// The compiled source must stay byte-identical to the pinned bundle's pattern source (the served recording
// context compares them), so the escape spellings are written as escaped backslashes rather than \xNN escapes.
const prefixSource = '\\x1b\\]1337;Capabilities=';
const fullSource = `${prefixSource}[^\\x07\\x1b]*(?:\\x07|\\x1b\\\\)`;
export const capsPinnedPattern = new RegExp(fullSource);
const pinnedPrefix = new RegExp(prefixSource);
const pinnedExpected = 'Complete OSC 1337 Capabilities reply';

/** The exact calls each pinned path makes, in order. `replies` names which journaled bytes the captured call returns. */
export const capsPinnedScripts = Object.freeze({
    'extensions.osc1337-capabilities': Object.freeze({
        termless: Object.freeze([['feedCapture', capsPinnedQuery, 'replies']]),
        terminal: Object.freeze([['queryWithSentinelOutcome', capsPinnedQuery, capsPinnedPattern.source]]),
        expected: pinnedExpected,
    }),
});
const pinnedCriteria = Object.freeze({
    'extensions.osc1337-capabilities': 'OSC 1337 ; RequestCapabilities BEL answered by one complete ESC]1337;Capabilities=<characters without BEL or ESC> frame ended by BEL or ST; a prefixed capture '
        + 'without a complete frame is inconclusive (invalid-reply) and no frame is inconclusive (no-response); it can never be unsupported',
});

/** The pinned result of one path over the journaled bytes of the case's own exchange: `replies` (the captured reply text before the barrier) and `barrier`. The criterion is the shared pinned
 *  oscReplyResult bound to this case's pattern, prefix and expectation. */
export function replayCapsPinned(caseId, path, bytes) {
    if (capsPinnedScripts[caseId] === undefined) throw new Error(`Unknown feature-reporting case ${caseId}.`);
    return replayPinnedPath(path, bytes, response => pinnedOscReply({ pattern: capsPinnedPattern, prefix: pinnedPrefix, expected: pinnedExpected }, response));
}

// ---------- the independent reply oracle ----------

/** The declared codes the measured run must advertise, transcribed from the specification's code table over this terminal's implemented, tested capabilities (iterm-feature-reporting.md). The direct
 * pane's host accepts OSC 52 writes under the run's default initial settings, so Cw is declared; a host that cannot answer omits it (policy, not measured here). */
export const capsDeclaredFeatures = 'T3CwLrMSc7UTs3BFGsGoSyHNoP';
/** The exact reply the contract demands: one OSC 1337 Capabilities frame, always ST-terminated. */
export const capsExpectedReply = `${esc}]1337;Capabilities=${capsDeclaredFeatures}${st}`;
/** Unsolicited or misspelled payloads that must stay silent, exactly as fed. */
export const capsSilentPayloads = Object.freeze(['1337;Capabilities', '1337;Capabilities?', '1337;Capabilities=TSGoUwTsBFSyHNoSxP', '1337;RequestCapabilitiesX', '1337;requestcapabilities', '1337;REQUESTCAPABILITIES']);

// ---------- the groups ----------

const ask = (role, kind, note, request, demand, setup = null) => {
    // The owned fence refuses any exchange request over 64 characters (terminal-fence.mjs); the contract
    // self-check keeps every group inside that bound instead of discovering it at collection time.
    if (request.length > 64) throw new Error(`The ${role} request exceeds the 64-byte owned exchange limit.`);
    return Object.freeze({ role, kind, note, setup: setup === null ? [] : [setup], request, demand: Object.freeze(demand) });
};

const request = terminator => `${esc}]1337;RequestCapabilities${terminator}`;
const silentRequest = payload => `${esc}]${payload}${bel}`;

const groupList = [
    ask('pinned', 'protocol', 'the exact pinned BEL-terminated request on the untouched default is answered by the complete declared declaration, ST-terminated', request(bel), [capsExpectedReply]),
    ask('st-terminated', 'protocol', 'the same request ended by ST is answered by the same ST-terminated reply', request(st), [capsExpectedReply]),
    ask('repeat', 'protocol', 'two requests in one exchange are answered by two identical replies in request order', request(bel) + request(st), [capsExpectedReply, capsExpectedReply]),
    ask('silent-forms', 'policy', 'a bare Capabilities form and a query spelling change nothing and stay silent', capsSilentPayloads.slice(0, 2).map(silentRequest).join(''), []),
    ask('silent-assignment', 'policy', 'an unsolicited Capabilities assignment changes nothing and stays silent', silentRequest(capsSilentPayloads[2]), []),
    ask('silent-suffix', 'policy', 'a suffixed request changes nothing and stays silent', silentRequest(capsSilentPayloads[3]), []),
    ask('silent-case', 'policy', 'both wrong-case spellings change nothing and stay silent', capsSilentPayloads.slice(4).map(silentRequest).join(''), []),
    ask('recover', 'protocol', 'after the silent forms the exact request is still answered by the same reply, so silence never broke the framing', request(bel), [capsExpectedReply]),
];

/** The plan of the case: the reseed, an initial cells read, then for each group its uncaptured setup feeds, the captured request, its report read, and the shared color reset. */
function planOf(groups) {
    const plan = [planFeed(capsReseed), planRead('cells')];
    for (const item of groups) {
        plan.push(...item.setup.map(planFeed), { feed: item.request, capture: true }, planRead('report'));
    }
    plan.push(planFeed(capsColorReset));
    return plan;
}

const appearanceScope = 'the iTerm2 feature-reporting declaration (OSC 1337 RequestCapabilities/Capabilities) only: the exact pinned request, the ST-terminated reply framing, the declared code string '
    + 'with Cw (the direct pane\'s host accepts OSC 52 writes under the run\'s default initial settings), reply count and correlation, the silent boundaries for unsolicited, suffixed and wrong-case '
    + 'payloads and recovery after them; the Cw-omitted host state, the per-code behavior of every declared capability and the image and cell-size forms are owned elsewhere; raw replies and unchanged '
    + 'state, no rendering, painted-pixel, remote/mux, common-harness or score claim, and the pinned probe alone never establishes which capabilities are honestly advertised';

// ---------- state, seed and grading ----------

/** One journaled observation group: the full screen beside the ink, cursor, geometry, margins, modes and reply
 *  facts every exchange must leave unchanged. Built from the device-query seed's fixture facts. */
const groupOf = (screen, replies) => ({
    screen, replies,
    ink: selectiveEraseInk(screen),
    at: [screen.cursorRow, screen.cursorCol],
    cursorVisible, cursorMode: screen.cursorMode,
    size: [screen.rows, screen.cols],
    bands: [screen.bufferState.scrollTop, screen.bufferState.scrollBottom],
    origin: screen.bufferState.originMode,
    alternate: screen.bufferState.alternateScreen,
    buffer: screen.bufferState,
    horizontalMargins: screen.horizontalMargins,
});
const cursorVisible = screen => screen.cursorVisible;
/** Everything an exchange must leave unchanged: the ink projection, the cursor facts, the geometry, the buffer state and the horizontal margins. */
const stateKey = state => JSON.stringify([state.ink, state.at, state.cursorVisible, state.cursorMode, state.buffer, state.horizontalMargins]);
const printable = raw => (raw === null ? null : raw.replaceAll(esc, String.raw`\e`).replaceAll(bel, String.raw`\a`));

/** Whether the initial read holds the contracted seed: geometry, nonce at home, the attributed cells, margins and modes. */
function seedOk(state, nonce) {
    const { seedCells, seedMargins } = devqFixture;
    const perRow = row => state.ink.filter(cell => cell.row === row).length;
    const holds = seedCells.every(spec => {
        const cell = state.ink.find(item => item.row === spec.row && item.col === spec.col);
        return cell !== undefined && cell.char === spec.char && cell.bold === (spec.bold ?? false)
            && (spec.fgIndex === undefined || cell.fgIndex === spec.fgIndex) && (spec.underline === undefined || cell.underline === spec.underline);
    });
    return state.size[0] >= capsMinimum.rows && state.size[1] >= capsMinimum.cols && state.screen.lines[0] === nonce && state.at.every(value => value === 0)
        && state.bands[0] === seedMargins[0] && state.bands[1] === seedMargins[1] && state.origin === false && state.alternate === true
        && perRow(1) === perRowAt(seedCells, 1) && perRow(3) === perRowAt(seedCells, 3) && holds;
}
const perRowAt = (cells, row) => cells.filter(cell => cell.row === row).length;

const inconclusive = note => ({ outcome: 'inconclusive', note });
const unsupported = note => ({ outcome: 'unsupported', note });

/** The way one exchange group differs from its demand, or null: unchanged state first, then the exact reply list. */
function groupMismatch(group, state, initial) {
    if (stateKey(state) !== stateKey(initial)) {
        return `The ${group.role} request changed the screen, the cursor (position, visibility or stored mode), the rendition, the margins (vertical or horizontal), the wrap/insert modes or the active screen: a feature-reporting request must change nothing`;
    }
    if (JSON.stringify(state.replies) === JSON.stringify(group.demand)) return null;
    if (state.replies.length === 0) return group.demand.length === 0 ? null : `The ${group.role} request was not answered`;
    if (state.replies.length !== group.demand.length) {
        return `The ${group.role} reply count differs from the contract: ${state.replies.length} replies where ${group.demand.length} were demanded (count, order and framing are part of the reply)`;
    }
    return `The ${group.role} reply differs from the reply oracle: the declared code string, the Capabilities framing or the ST terminator was wrong`;
}

/** The case's grade in execution order, so an earlier failure is never overridden by a later group it damaged: the seed, then each group as the plan ran it. The first failing protocol group that
 *  mismatches makes the case unsupported; policy groups never decide. */
function gradeCase(item, observation) {
    if (!seedOk(observation.initial, observation.nonce)) return inconclusive('The attributed seed (nonce, cells, margins and modes) was not measured before any request');
    for (const group of item.groups.filter(entry => entry.kind !== 'policy')) {
        const mismatch = groupMismatch(group, observation[group.role], observation.initial);
        if (mismatch !== null) return unsupported(mismatch);
    }
    return { outcome: 'supported' };
}

/** The observed record of one case: every group with its demanded and received replies and whether the state was unchanged. */
function briefCase(item, observation) {
    const groups = Object.fromEntries(item.groups.map(group => {
        const state = observation[group.role];
        return [group.role, { kind: group.kind, request: printable(group.request), demand: group.note, demanded: group.demand.map(printable),
            received: state.replies.map(printable), matches: JSON.stringify(state.replies) === JSON.stringify(group.demand),
            unchanged: stateKey(state) === stateKey(observation.initial) }];
    }));
    const [rows, cols] = observation.initial.size, script = item.pinnedScript;
    return { geometry: { rows, cols }, effectContract: capsEffectContractId, pinnedScript: { termless: script.termless.map(call => call.map(printable)), terminal: script.terminal.map(call => call.map(printable)) },
        seed: { nonce: observation.nonce, margins: observation.initial.margins, origin: observation.initial.origin, alternate: observation.initial.alternate,
            cells: seedOk(observation.initial, observation.nonce) }, groups };
}

/** The contracted case. */
function makeContract(caseId) {
    const groups = Object.freeze(groupList), script = capsPinnedScripts[caseId];
    const roles = Object.freeze(['initial', ...groups.map(group => group.role)]);
    const pinned = groups.find(group => group.role === 'pinned');
    if (pinned.request !== capsPinnedQuery || pinned.setup.length > 0) throw new Error('The pinned group must send exactly the pinned request.');
    const contract = {
        caseId, probeId: caseId, id: 'osc1337-capabilities', roles, groups, plan: Object.freeze(planOf(groups)),
        pinnedPath: 'termless', pinnedRead: roles.indexOf('pinned'), pinnedExpected: script.expected, pinnedCriterion: pinnedCriteria[caseId], pinnedScript: script,
        pinnedFeeds: Object.freeze([capsPinnedQuery]), pinnedSetup: false,
        terminalFixture: { writes: [capsPinnedQuery], demand: 'a framed OSC 1337 Capabilities reply matching the pinned pattern within 2000 ms of the sentinel write', minimum: { rows: 1, cols: 1 },
            note: 'the stock collector\'s queryWithSentinelOutcome path; replayed over the same journaled bytes beside the termless verdict' },
        effectContractId: capsEffectContractId, appearanceScope, evidence: 'owned-raw-replies-and-unchanged-state',
        grade: observation => gradeCase(contract, observation), observed: observation => briefCase(contract, observation),
    };
    return Object.freeze(contract);
}

export const capsContracts = Object.freeze(capsCaseIds.map(makeContract));
/** Contract case ids in suite order. */
export const capsContractCaseIds = Object.freeze(capsContracts.map(item => item.caseId));
export { planFeeds as capsFeeds, planReads as capsReads } from './graded-plan.mjs';

/** The seed bytes of the plan (the reset followed by the attributed seed and nonce). */
export function capsPlanSeed(runId) {
    if (!/^[a-f\d]{32}$/.test(runId ?? '')) throw new Error('Invalid feature-reporting fixture run identity.');
    return capsReset + capsSeed(capsNoncePrefix + runId);
}
/** The finish: the screen and colors reset, the baseline title written, the alternate screen left. */
export const capsFinish = runId => capsReset + capsColorReset + `${esc}]0;${capsBaseTitle(runId)}${st}` + '\x1b[?1049l';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateCapsFeeds(item, feeds) { validateExactFeeds(item, feeds, 'contract'); }

/** Group the read screens and replies of the case by role. */
export function capsObservation(item, screens, replies, nonce) {
    const reads = item.plan.filter(step => step.read);
    if (!Array.isArray(screens) || screens.length !== reads.length || !Array.isArray(replies) || replies.length !== reads.length || typeof nonce !== 'string') {
        throw new Error('Missing feature-reporting read captures.');
    }
    const observation = Object.fromEntries(item.roles.map((role, index) => [role, groupOf(screens[index], replies[index])]));
    observation.nonce = nonce;
    return observation;
}

/** Independent contract grader entry. */
export function gradeCapsCase(item, observation, revision = capsContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown feature-reporting contracts revision ${revision}.`);
    if (!observation || item.roles.some(role => !observation[role] || !Array.isArray(observation[role].ink))) throw new Error(`Missing ${item.caseId} read captures.`);
    return item.grade(observation);
}

/** The fixed fixture facts other modules and tests read. */
export const capsFixture = Object.freeze({ minimum: capsMinimum, seedMargins: devqFixture.seedMargins, seedCells: devqFixture.seedCells, groupList, silentPayloads: capsSilentPayloads, declaredFeatures: capsDeclaredFeatures, expectedReply: capsExpectedReply });
