// Versioned probe contract for the xterm pointer-color qualification batch: extensions.osc113-reset-pointer-fg (OSC 13 / OSC 113) and extensions.osc114-reset-pointer-bg (OSC 14 / OSC 114). This is not a
// publication or a measurement. The batch's first integrated graph is v71; its receipt schema is 78.
//
// Pinned terminfo.dev@4.0.0 definition (bundle SHA-256 aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6), read from the bundle, never inferred from the name. Each case is registered as
// `probe(id, colorResetProbe(setCode, resetCode), terminalAsync)`:
//   termless  colorResetProbe(13, 113) / (14, 114): feedCapture(OSC <set> ; ? BEL); when the captured text holds no complete `OSC <set> ; rgb:<r>/<g>/<b>` reply the probe returns oscReplyResult(raw, null,
//             ...): inconclusive, reason `no-response` (or `invalid-reply` when only the reply prefix arrived), evidence `query`. Only a complete reply makes it continue (set, query, reset, query), which
//             this owned run never serves: a reply would end the suite's unsupported contract (see below).
//   terminal  writes CUP 1;1 and EL 2, then `OSC <reset> BEL` (113 or 114), asks the cursor position and returns unverifiedEffect: inconclusive, reason `insufficient-evidence`, evidence `consumed`,
//             note "Cursor answered; pointer foreground/background restoration was not measured". It never measures a pointer colour.
// While the query stays unanswered neither pinned path can return unsupported (the termless code returns inconclusive without a complete reply and the terminal path is unverifiedEffect), so the pinned verdict
// stays inconclusive; this contract never promotes or demotes it. A complete reply would let the pinned code continue and can end in unsupported when a mutation succeeds but is not restored: that run is refused, not graded.
//
// The authoritative contract is xterm's: OSC 13 and OSC 14 set or query the pointer (mouse) foreground and background colors and OSC 113 / OSC 114 reset them, continuing a chained
// `OSC 10 ; a ; b ; ...` through 11, 12, 13 (pointer foreground), 14 (pointer background), 15, 16, 17 (highlight background), 18 and 19 (highlight foreground). Ntilde's deliberate policy (#991) is
// that the App draws the operating system's pointer and cannot recolor it, so all four codes are ignored whole: a query is never answered and a set or reset changes nothing, while a chain still
// consumes the pointer slots' values so the later slots (17, 19) do not shift. The graded effect is therefore `unsupported` when every protocol group is silent and leaves the screen, the cursor,
// the rendition, the margins and the modes unchanged, and the five colors the pointer sequences could be mistaken for (10, 11, 12, 17, 19) read back exactly as seeded (independent literals written
// from the seeded values and the xterm chain order, not read from the parser). A reply to a pointer request, or any other change, makes the effect inconclusive: a terminal that started answering
// needs its own supported contract rather than being graded as unsupported here.
//
// Limits: raw replies and stored terminal state only; the native pointer image, the OSC 22 shape and any presentation are not claimed; no remote/mux, other-platform, common-harness or score claim.
import { planFeed, planRead, validateExactFeeds } from './graded-plan.mjs';
import { selectiveEraseInk } from './selective-erase-contracts.mjs';
import { opaqueArgb } from './color-reset-model.mjs';
import { devqColorReset, devqFixture, devqMinimum, devqReseed, devqReset, devqSeed } from './devq-contracts.mjs';
export { validateDevqScreen as validatePclScreen } from './devq-contracts.mjs';

/** Contracts v1 is the first measured semantics of this batch. */
export const pclContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const pclSuiteId = 'ntilde-owned-pointer-color-graded-v1';
/** The separately identified effect contract. */
export const pclEffectContractId = 'ntilde-pointer-color-unsupported-effect-v1';
/** The nonce prefix binding worker output to this suite. */
export const pclNoncePrefix = 'NTILDE_PCL_';
/** Owner-protocol prefix of this suite's observation requests. */
export const pclPrefix = 'pcl';
/** The pinned cases, in suite order. */
export const pclCaseIds = Object.freeze(['extensions.osc113-reset-pointer-fg', 'extensions.osc114-reset-pointer-bg']);
/** The measured geometry the controls need (the shared device-query seed: margins 2..6, a nonce row and a coloured/bold/underlined seed row). */
export const pclMinimum = devqMinimum;
export const pclReset = devqReset;
export const pclReseed = devqReseed;
export const pclSeed = devqSeed;
/** The reset every phase ends with: every palette entry and the five dynamic colors back to the host's. */
export const pclColorReset = devqColorReset;
/** The run's baseline title, written by the finish so the pane never keeps a test label. */
export const pclBaseTitle = runId => `PCL-base-${runId.slice(0, 8)}`;

const esc = '\x1b';
const st = esc + '\\';
const bel = '\x07';
const osc = (body, terminator = bel) => `${esc}]${body}${terminator}`;

// ---------- the pinned probes (read from the bundle) ----------

/** The pinned query, the reset and the writes the terminal path makes, per case. */
const pinnedOf = (setCode, resetCode, label) => Object.freeze({
    setCode, resetCode,
    query: osc(`${setCode};?`),
    // The compiled source must stay byte-identical to the pinned pattern source: this is the reply the pinned termless code searches for.
    replySource: `\\x1b\\]${setCode};(rgb:[0-9a-f]{1,4}/[0-9a-f]{1,4}/[0-9a-f]{1,4})(?:\\x07|\\x1b\\\\)`,
    prefixSource: `\\x1b\\]${setCode};`,
    expected: `OSC ${setCode} color before reset`,
    terminalWrites: Object.freeze([`${esc}[1;1H${esc}[2K`, osc(String(resetCode))]),
    terminalNote: `Cursor answered; pointer ${label} restoration was not measured`,
    noCursorNote: `No cursor response after OSC ${resetCode}`,
});
export const pclPinned = Object.freeze({
    'extensions.osc113-reset-pointer-fg': pinnedOf(13, 113, 'foreground'),
    'extensions.osc114-reset-pointer-bg': pinnedOf(14, 114, 'background'),
});

/** The exact calls each pinned path makes, in order. `replies` names which journaled bytes the captured call returns. */
export const pclPinnedScripts = Object.freeze(Object.fromEntries(pclCaseIds.map(caseId => [caseId, Object.freeze({
    termless: Object.freeze([['feedCapture', pclPinned[caseId].query, 'replies']]),
    terminal: Object.freeze([['write', pclPinned[caseId].terminalWrites[0]], ['write', pclPinned[caseId].terminalWrites[1]], ['queryCursorPosition']]),
    expected: pclPinned[caseId].expected,
})])));
const pinnedCriteria = Object.freeze(Object.fromEntries(pclCaseIds.map(caseId => [caseId,
    `colorResetProbe(${pclPinned[caseId].setCode}, ${pclPinned[caseId].resetCode}) feeds the BEL-spelled query OSC ${pclPinned[caseId].setCode} ; ? and returns oscReplyResult(raw, null, ...) when the captured text holds `
    + `no complete rgb reply (inconclusive, no-response); the terminal path writes CUP, EL and OSC ${pclPinned[caseId].resetCode}, asks the cursor position and returns unverifiedEffect (inconclusive, consumed); `
    + 'neither path can be unsupported while the query stays unanswered'])));

/** The pinned termless result over the journaled reply text of the case's own exchange. A complete reply would make the pinned code continue (set, query, reset, query), which this owned run never
 *  serves, so the replay refuses it instead of inventing the rest of the fixture. */
export function replayPclTermless(caseId, replies) {
    const pinned = pclPinned[caseId];
    if (pinned === undefined) throw new Error(`Unknown pointer-color case ${caseId}.`);
    if (typeof replies !== 'string') throw new Error('Missing pointer-color reply text.');
    if (new RegExp(pinned.replySource, 'i').test(replies)) throw new Error(`The ${caseId} query was answered with a color: the pinned code would continue past the unsupported contract.`);
    const prefixed = new RegExp(pinned.prefixSource).test(replies);
    return { pass: false, response: replies, observation: { outcome: 'inconclusive', reason: prefixed ? 'invalid-reply' : 'no-response', evidence: 'query' } };
}

/** The pinned terminal result: the cursor position the stock collector would have asked for, from the journaled cursor of the pinned exchange's own read. */
export function replayPclTerminal(caseId, cursor) {
    const pinned = pclPinned[caseId];
    if (pinned === undefined) throw new Error(`Unknown pointer-color case ${caseId}.`);
    if (cursor !== null && !(Array.isArray(cursor) && cursor.length === 2 && cursor.every(value => Number.isSafeInteger(value) && value >= 0))) throw new Error('Invalid pointer-color cursor.');
    const note = cursor === null ? pinned.noCursorNote : pinned.terminalNote;
    return { pass: false, note, observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'consumed', note } };
}

// ---------- the independent oracle ----------

/** The five colors a pointer sequence could be mistaken for, seeded to distinguishable values. The seed is plain OSC 10/11/12/17/19 sets, ST-terminated. */
export const pclSeedColors = Object.freeze([['10', '#102030'], ['11', '#405060'], ['12', '#708090'], ['17', '#a0b0c0'], ['19', '#d0e0f0']]);
export const pclColorSeed = pclSeedColors.map(([code, value]) => osc(`${code};${value}`, st)).join('');
/** The App's resolved theme binding while the seed is in force: only OSC 10 and OSC 11 reach it (the foreground and background defaults and their override flags); the cursor, the selection colors and
 *  everything else about the theme are not part of it. Derived from the binding the run starts on, so the palette, theme name, contrast and reverse video stay exactly the host's. */
export const pclSeededBinding = edge => ({ ...edge, foregroundArgb: opaqueArgb(0x102030), backgroundArgb: opaqueArgb(0x405060), foregroundOverride: true, backgroundOverride: true });
/** The exact replies of a color read at the seed: ST-terminated, 16-bit channels (each byte doubled). */
const reply = (code, [r, g, b]) => osc(`${code};rgb:${r}${r}/${g}${g}/${b}${b}`, st);
const seededReplies = Object.freeze([
    reply('10', ['10', '20', '30']), reply('11', ['40', '50', '60']), reply('12', ['70', '80', '90']), reply('17', ['a0', 'b0', 'c0']), reply('19', ['d0', 'e0', 'f0']),
]);
/** The color read: one BEL-spelled query for each of the five colors, in slot order. */
const colorRead = pclSeedColors.map(([code]) => osc(`${code};?`)).join('');
const movedCursor = reply('12', ['e0', 'a0', '20']);
// OSC 12 ; a ; b ; c ; d ; e ; f continues through 13, 14, 15, 16 to 17: the sixth value is the highlight background and the pointer slots in between take their values silently.
const chainSet = osc('12;#010203;#040506;#070809;#0a0b0c;#0d0e0f;#101112');
const afterChain = Object.freeze([seededReplies[0], seededReplies[1], reply('12', ['01', '02', '03']), reply('17', ['10', '11', '12']), seededReplies[4]]);

// ---------- the groups ----------

const ask = (role, kind, note, request, demand) => {
    // The owned fence refuses any exchange request over 64 characters (terminal-fence.mjs); the contract
    // self-check keeps every group inside that bound instead of discovering it at collection time.
    if (request.length > 64) throw new Error(`The ${role} request exceeds the 64-byte owned exchange limit.`);
    return Object.freeze({ role, kind, note, request, demand: Object.freeze(demand) });
};

/** The groups of one case, in execution order. A `control` gates the verdict (a failing control makes the effect inconclusive); a `protocol` group demands the ignored behavior. */
function groupsOf(caseId) {
    const { setCode, resetCode, query } = pclPinned[caseId];
    const silent = (role, note, request) => ask(role, 'protocol', note, request, []);
    // A mutating request carries its own five-color read, so a color a set changed cannot be restored by a later reset before anything looks: every spelling is judged on its own bytes.
    const unchanged = (role, note, request) => ask(role, 'protocol', note, request + colorRead, seededReplies);
    return Object.freeze([
        ask('seeded', 'control', 'the five colors read back exactly as seeded before any pointer sequence, so the color read can see a change and a silence is not a dead channel', colorRead, seededReplies),
        silent('pinned', `the exact pinned BEL-terminated OSC ${setCode} query is never answered`, query),
        silent('query-st', `the same OSC ${setCode} query ended by ST is never answered`, osc(`${setCode};?`, st)),
        unchanged('set', `an OSC ${setCode} set (rgb: spelling) is ignored and not answered: the five colors still read back as seeded`, osc(`${setCode};rgb:aa/bb/cc`)),
        silent('query-after-set', `an OSC ${setCode} query after a set is still not answered: nothing was stored to read back`, query),
        unchanged('set-hex', `an OSC ${setCode} set in the # spelling, ST-terminated, is ignored and not answered: the five colors still read back as seeded`, osc(`${setCode};#aabbcc`, st)),
        unchanged('reset', `the exact OSC ${resetCode} reset the pinned terminal path writes is ignored and not answered: the five colors still read back as seeded`, osc(String(resetCode))),
        unchanged('reset-st', `an OSC ${resetCode} reset ended by ST is ignored and not answered: the five colors still read back as seeded`, osc(String(resetCode), st)),
        unchanged('reset-argument', `an OSC ${resetCode} reset with an argument is ignored and not answered: the five colors still read back as seeded`, osc(`${resetCode};1`)),
        ask('colors-after-pointer', 'protocol', `every pointer sequence above left the five colors exactly as seeded: no OSC ${setCode} or OSC ${resetCode} reached the foreground, background, cursor or selection colors`, colorRead, seededReplies),
        ask('control-move', 'control', 'an OSC 12 change of the cursor color is read back by its own query, so the replies do follow state', osc('12;#e0a020') + osc('12;?'), [movedCursor]),
        silent('chain', 'a chained OSC 12 set through the pointer slots to the highlight background is not answered', chainSet),
        ask('colors-after-chain', 'protocol', 'the chain took effect on the colors that exist (12 and 17) and on nothing else: the pointer slots between them consumed their values, so 17 did not shift', colorRead, afterChain),
        ask('recover', 'protocol', `after all of them the OSC ${setCode} query is still not answered while the color read still is, and the colors are still the chained values`, query + osc('12;?') + query + colorRead, [afterChain[2], ...afterChain]),
    ]);
}

/** The plan of one case: the reseed, the color seed, an initial cells read, then for each group the captured request and its report read, and the shared color reset. */
function planOf(groups) {
    const plan = [planFeed(pclReseed), planFeed(pclColorSeed), planRead('cells')];
    for (const item of groups) plan.push({ feed: item.request, capture: true }, planRead('report'));
    plan.push(planFeed(pclColorReset));
    return plan;
}

const appearanceScope = 'the xterm pointer foreground and background color sequences (OSC 13 and OSC 14, reset by OSC 113 and OSC 114) only: the exact pinned query, the ST spelling, the rgb: and # set spellings, the '
    + 'resets with and without an argument, a chained OSC 12 set through the pointer slots and a chained query, each answered with nothing and leaving the complete screen, cursor, rendition, margins and modes '
    + 'unchanged, with the five colors the pointer sequences could be mistaken for (foreground, background, cursor, selection background and selection foreground) reading back exactly as seeded and a cursor-color change '
    + 'read back as a control; raw replies and stored terminal state, not the operating system pointer image, the OSC 22 shape, rendering or painted pixels; no remote/mux, other-platform, common-harness or score claim; '
    + 'the pinned verdict stays inconclusive on both paths';

// ---------- state, seed and grading ----------

const printable = raw => (raw === null ? null : raw.replaceAll(esc, String.raw`\e`).replaceAll(bel, String.raw`\a`));

/** One journaled observation group: the full screen beside the ink, cursor, geometry, margins, modes and reply facts every exchange must leave unchanged. */
const groupOf = (screen, replies) => ({
    screen, replies,
    ink: selectiveEraseInk(screen),
    at: [screen.cursorRow, screen.cursorCol],
    cursorVisible: screen.cursorVisible, cursorMode: screen.cursorMode,
    size: [screen.rows, screen.cols],
    bands: [screen.bufferState.scrollTop, screen.bufferState.scrollBottom],
    origin: screen.bufferState.originMode,
    alternate: screen.bufferState.alternateScreen,
    buffer: screen.bufferState,
    horizontalMargins: screen.horizontalMargins,
});
/** Everything an exchange must leave unchanged: the ink projection, the cursor facts, the buffer state and the horizontal margins. */
const stateKey = state => JSON.stringify([state.ink, state.at, state.cursorVisible, state.cursorMode, state.buffer, state.horizontalMargins]);
const perRowAt = (cells, row) => cells.filter(cell => cell.row === row).length;

/** Whether the initial read holds the contracted seed: geometry, nonce at home, the attributed cells, margins and modes. */
function seedOk(state, nonce) {
    const { seedCells, seedMargins } = devqFixture;
    const perRow = row => state.ink.filter(cell => cell.row === row).length;
    const holds = seedCells.every(spec => {
        const cell = state.ink.find(item => item.row === spec.row && item.col === spec.col);
        return cell !== undefined && cell.char === spec.char && cell.bold === (spec.bold ?? false)
            && (spec.fgIndex === undefined || cell.fgIndex === spec.fgIndex) && (spec.underline === undefined || cell.underline === spec.underline);
    });
    return state.size[0] >= pclMinimum.rows && state.size[1] >= pclMinimum.cols && state.screen.lines[0] === nonce && state.at.every(value => value === 0)
        && state.bands[0] === seedMargins[0] && state.bands[1] === seedMargins[1] && state.origin === false && state.alternate === true
        && perRow(1) === perRowAt(seedCells, 1) && perRow(3) === perRowAt(seedCells, 3) && holds;
}

const inconclusive = note => ({ outcome: 'inconclusive', note });
const unsupported = note => ({ outcome: 'unsupported', note });

/** The way one exchange group differs from its demand, or null: unchanged state first, then the exact reply list. */
function groupMismatch(group, state, initial) {
    if (stateKey(state) !== stateKey(initial)) {
        return `The ${group.role} request changed the screen, the cursor (position, visibility or stored mode), the rendition, the margins (vertical or horizontal), the wrap/insert modes or the active screen: a pointer-color sequence must change nothing`;
    }
    if (JSON.stringify(state.replies) === JSON.stringify(group.demand)) return null;
    if (group.demand.length === 0) return `The ${group.role} request was answered with ${state.replies.length} repl${state.replies.length === 1 ? 'y' : 'ies'} where the ignored contract demands none`;
    return `The ${group.role} replies differ from the oracle: ${state.replies.length} received where ${group.demand.length} were demanded, or a color differs from the seeded or chained value`;
}

/** The case's grade in execution order, so an earlier failure is never overridden by a later group it damaged: the seed, then each group as the plan ran it. Every group holding (the controls
 *  included) makes the effect `unsupported`: the sequences are ignored and the measured quantities could move. Any mismatch is inconclusive: this contract defines only the ignored behavior. */
function gradeCase(item, observation) {
    if (!seedOk(observation.initial, observation.nonce)) return inconclusive('The attributed seed (nonce, cells, margins and modes) was not measured before any request');
    for (const group of item.groups) {
        const mismatch = groupMismatch(group, observation[group.role], observation.initial);
        if (mismatch !== null) return inconclusive(group.kind === 'control' ? `Setup control failed before the effect could be judged: ${mismatch}` : `${mismatch}; behavior that differs from the ignored contract needs its own supported contract`);
    }
    return unsupported('Every pointer-color sequence was ignored: never answered, no screen, cursor, margin or mode change, and the five colors read back exactly as seeded');
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
    return { geometry: { rows, cols }, effectContract: pclEffectContractId, pinnedScript: { termless: script.termless.map(call => call.map(printable)), terminal: script.terminal.map(call => call.map(printable)) },
        seed: { nonce: observation.nonce, margins: observation.initial.bands, origin: observation.initial.origin, alternate: observation.initial.alternate,
            cells: seedOk(observation.initial, observation.nonce) }, groups };
}

/** One contracted case. */
function makeContract(caseId) {
    const groups = groupsOf(caseId), script = pclPinnedScripts[caseId], pinned = pclPinned[caseId];
    const roles = Object.freeze(['initial', ...groups.map(group => group.role)]);
    const pinnedGroup = groups.find(group => group.role === 'pinned');
    if (pinnedGroup.request !== pinned.query) throw new Error('The pinned group must send exactly the pinned request.');
    const contract = {
        caseId, probeId: caseId, id: caseId.slice('extensions.'.length), roles, groups, plan: Object.freeze(planOf(groups)),
        pinnedPath: 'termless', pinnedRead: roles.indexOf('pinned'), pinnedExpected: script.expected, pinnedCriterion: pinnedCriteria[caseId], pinnedScript: script,
        pinnedFeeds: Object.freeze([pinned.query]), pinnedSetup: false,
        terminalFixture: { writes: [...pinned.terminalWrites], demand: 'none: the pinned terminal callback writes, asks the cursor position and returns unverifiedEffect', minimum: { rows: 1, cols: 1 },
            note: 'inconclusive by design (evidence consumed): the stock grade is carried unchanged; replayed over the journaled cursor of the pinned exchange\'s own read' },
        effectContractId: pclEffectContractId, appearanceScope, evidence: 'owned-raw-replies-and-unchanged-state',
        grade: observation => gradeCase(contract, observation), observed: observation => briefCase(contract, observation),
    };
    return Object.freeze(contract);
}

export const pclContracts = Object.freeze(pclCaseIds.map(makeContract));
/** Contract case ids in suite order. */
export const pclContractCaseIds = Object.freeze(pclContracts.map(item => item.caseId));
export { planFeeds as pclFeeds, planReads as pclReads } from './graded-plan.mjs';

/** The seed bytes of the plan (the reset followed by the attributed seed and nonce). */
export function pclPlanSeed(runId) {
    if (!/^[a-f\d]{32}$/.test(runId ?? '')) throw new Error('Invalid pointer-color fixture run identity.');
    return pclReset + pclSeed(pclNoncePrefix + runId);
}
/** The finish: the screen and colors reset, the baseline title written, the alternate screen left. */
export const pclFinish = runId => pclReset + pclColorReset + `${esc}]0;${pclBaseTitle(runId)}${st}` + '\x1b[?1049l';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validatePclFeeds(item, feeds) { validateExactFeeds(item, feeds, 'contract'); }

/** Group the read screens and replies of the case by role. */
export function pclObservation(item, screens, replies, nonce) {
    const reads = item.plan.filter(step => step.read);
    if (!Array.isArray(screens) || screens.length !== reads.length || !Array.isArray(replies) || replies.length !== reads.length || typeof nonce !== 'string') {
        throw new Error('Missing pointer-color read captures.');
    }
    const observation = Object.fromEntries(item.roles.map((role, index) => [role, groupOf(screens[index], replies[index])]));
    observation.nonce = nonce;
    return observation;
}

/** Independent contract grader entry. */
export function gradePclCase(item, observation, revision = pclContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown pointer-color contracts revision ${revision}.`);
    if (!observation || item.roles.some(role => !observation[role] || !Array.isArray(observation[role].ink))) throw new Error(`Missing ${item.caseId} read captures.`);
    return item.grade(observation);
}

/** The fixed fixture facts other modules and tests read. */
export const pclFixture = Object.freeze({ minimum: pclMinimum, seedMargins: devqFixture.seedMargins, seedCells: devqFixture.seedCells, seedColors: pclSeedColors, seededReplies, afterChain, movedCursor, colorRead, chainSet });
