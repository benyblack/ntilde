// Independent contracts for the extended-mouse-encoding benchmark layer (modes.utf8-mouse-1005 and input.urxvt-mouse).
// The oracle below is written from xterm's control-sequence documentation (ctlseqs "Extended coordinates") and its button.c emitters
// (EmitMousePosition, EditorButton): it never calls the App's encoder. Each plan case carries a literal expectation that must equal the
// oracle, and the child's raw bytes must equal the literal. The button/modifier arithmetic is the routed-input contract's own (what a
// code means); this module only changes how the code and the coordinates are printed.
import { xtermMouseCode, xtermMouseReport, modeFromReplies, trackingFromReplies } from './routed-input-contracts.mjs';
import { makeMexCases, mexBenchmarkIds, utf8Octets, utf8PinnedCases, utf8PinnedFeeds, utf8PinnedQuery } from './mex-cases.mjs';

export const mexGradedSuiteId = 'ntilde-owned-mouse-extended-benchmark-graded-v1';
export const mexContractsVersion = 1;
export const mexCaseIds = Object.freeze([...mexBenchmarkIds]);
export const mexEffectContracts = Object.freeze({
    'modes.utf8-mouse-1005': 'ntilde-utf8-mouse-1005-effect-v1',
    'input.urxvt-mouse': 'ntilde-urxvt-mouse-effect-v1',
});

const hex = value => Buffer.from(value, 'latin1').toString('hex');
/** xterm's UTF-8 coordinate limit: (2047 - 32) + 1 positions; larger values are not representable and never produced here. */
const maxUtf8Position = 2015;

/**
 * Extended-encoding mouse reports. `tracking` is the one selected protocol (9, 1000, 1002, 1003); `encoding` is the single coordinate
 * encoding in force: '' legacy, 'sgr' (1006), 'utf8' (1005) or 'urxvt' (1015). UTF-8 prints the button byte and both positions as the
 * UTF-8 encoding of (value + 32); urxvt prints `CSI Cb ; Cx ; Cy M` with Cb = code + 32 in decimal and always final M (a release is code 3).
 * Returns the exact report bytes (latin1) or '' when the protocol does not report the event.
 */
export function xtermExtendedMouseReport(event) {
    const { encoding, tracking, kind, column, row, modifiers, button, wheelDelta } = event;
    const common = { tracking, kind, modifiers, button, wheelDelta, column, row };
    if (encoding === '' || encoding === 'sgr') return xtermMouseReport({ ...common, sgr: encoding === 'sgr' });
    const code = xtermMouseCode({ ...common, sgr: false });
    if (code === null) return '';
    if (encoding === 'urxvt') return `\x1b[${code + 32};${column};${row}M`;
    if (encoding !== 'utf8') throw new Error(`Unknown coordinate encoding ${encoding}.`);
    if (column > maxUtf8Position || row > maxUtf8Position) throw new Error('UTF-8 mouse coordinates above 2015 are not representable.');
    return '\x1b[M' + utf8Octets(code) + utf8Octets(column) + utf8Octets(row);
}

/** The one coordinate encoding a mode profile selects: the profile never carries two. */
export function encodingOfModes(modes) {
    const selected = [modes.mouseSgr && 'sgr', modes.mouseUtf8 && 'utf8', modes.mouseUrxvt && 'urxvt'].filter(Boolean);
    if (selected.length > 1) throw new Error('A mode profile selects at most one coordinate encoding.');
    return selected[0] ?? '';
}

/** The oracle's expectation for one extended-encoding plan case: the bytes its declared event must deliver to the child. */
export function oracleHex(item) {
    const { event, modes } = item;
    if (!event.kind.startsWith('mouse')) return hex('');
    return hex(xtermExtendedMouseReport({ tracking: modes.mouseTracking, encoding: encodingOfModes(modes), kind: event.kind, button: event.button,
        modifiers: event.modifiers, wheelDelta: event.wheelDelta, column: event.column, row: event.row }));
}

/** Every extended-encoding plan literal must equal the independent oracle; returns the mismatching ids (empty when coherent). */
export function oracleDisagreements(cases = makeMexCases(40, 140)) {
    return cases.filter(item => mexCaseIds.some(id => item.roles[id] !== undefined))
        .flatMap(item => oracleHex(item) === item.expectedHex ? [] : [item.id]);
}

const absentEventNote = 'No generated input event and encoded report were validated';
const utf8Note = 'encoded UTF-8 mouse events';
/** The pinned definitions (terminfo.dev@4.0.0, probe-defs/src/input.ts and modes.ts), documented for the publication. */
export const mexPinned = Object.freeze({
    'modes.utf8-mouse-1005': Object.freeze({
        criterion: 'decrpmModeProbe("modes.utf8-mouse-1005", 1005, termless): the termless function feeds ESC [ ? 1005 h, captures the reply to ESC [ ? 1005 $ p, '
            + 'feeds ESC [ ? 1005 l and returns notTestedResult("encoded UTF-8 mouse events") (no semantic observable) whether or not the reply recognises the mode '
            + '(an unrecognised mode feeds X and reads cell 0,0 first); the terminal path returns the DECRQM result of mode 1005 (supported once the mode is recognised)',
        feeds: utf8PinnedFeeds, capture: utf8PinnedQuery, note: utf8Note, evidence: 'not-tested',
    }),
    'input.urxvt-mouse': Object.freeze({
        criterion: 'mouseInputProbe("input.urxvt-mouse", "mouseTracking"): reads ctx.getMode("mouseTracking") and returns inconclusive for any value; the terminal path returns inconclusive without touching the terminal',
        feeds: Object.freeze([]), mode: 'mouseTracking', note: absentEventNote, evidence: 'legacy',
    }),
});

/** The pinned termless result, key order included, over the served values. */
export function replayMexPinned(caseId, served) {
    if (caseId === 'modes.utf8-mouse-1005') {
        return { pass: false, response: JSON.stringify({ response: served.response }), note: utf8Note,
            notTested: { reason: 'no-semantic-observable', noObservable: utf8Note } };
    }
    return { pass: false, response: JSON.stringify({ currentMode: served.currentMode === true }), note: absentEventNote,
        observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'legacy', note: absentEventNote } };
}

/** The pinned outcome a result carries: only inconclusive is ever possible for these probes. */
export function pinnedOutcome(result) {
    if (result?.observation?.outcome) return result.observation.outcome;
    return result?.notTested?.reason === 'no-semantic-observable' ? 'inconclusive' : 'invalid';
}

const requiredStatuses = Object.freeze(['required', 'control', 'label']);

/** Experiments of a benchmark in plan order, with the role that benchmark assigns. */
export function contractExperiments(caseId, cases = makeMexCases(40, 140)) {
    return cases.filter(item => requiredStatuses.includes(item.roles[caseId])).map(item => ({ id: item.id, role: item.roles[caseId] }));
}

/**
 * Grade one benchmark from its measured experiments ({id, role, matched}). A mismatching required experiment establishes unsupported
 * only when every control held; an invalid control leaves the benchmark inconclusive; labels never grade.
 */
export function gradeMexCase(caseId, experiments, version = mexContractsVersion) {
    if (version !== mexContractsVersion) throw new Error('Unknown extended-mouse contracts version.');
    const planned = contractExperiments(caseId);
    if (experiments.length !== planned.length || planned.some((item, index) => experiments[index].id !== item.id || experiments[index].role !== item.role
        || typeof experiments[index].matched !== 'boolean')) throw new Error(`Extended-mouse experiments differ from the plan for ${caseId}.`);
    const failedControls = experiments.filter(item => item.role === 'control' && !item.matched).map(item => item.id);
    const failedRequired = experiments.filter(item => item.role === 'required' && !item.matched).map(item => item.id);
    if (failedControls.length) return { outcome: 'inconclusive', reason: 'control-mismatch', failedControls, failedRequired };
    if (failedRequired.length) return { outcome: 'unsupported', failedRequired };
    return { outcome: 'supported' };
}

/** The child's own reply to the pinned DECRQM request in one control capture: the exact `ESC [ ? 1005 ; Ps $ y` bytes, or undefined. */
export function utf8ReplyOf(controlHex) {
    const text = Buffer.from(controlHex ?? '', 'hex').toString('latin1');
    return new RegExp(String.raw`\x1b\[\?1005;[0-4]\$y`).exec(text)?.[0];
}

/**
 * The values the child's own replies serve to a pinned probe: for mode 1005 the DECRQM reply after the pinned ?1005h feed (the reply
 * the probe captures) plus the set and reset states proved by both pinned feeds' controls; for urxvt the restored tracking state.
 */
export function servedMex(caseId, controls, restored) {
    if (caseId === 'modes.utf8-mouse-1005') {
        const response = utf8ReplyOf(controls[utf8PinnedCases.set]);
        return { response, on: modeFromReplies(controls[utf8PinnedCases.set], 1005), off: modeFromReplies(controls[utf8PinnedCases.reset], 1005),
            sources: [utf8PinnedCases.set, utf8PinnedCases.reset] };
    }
    return { currentMode: trackingFromReplies(restored), sources: ['restored'] };
}

/**
 * Serve the pinned code with the child's replies, refusing any feed, capture or read the contract does not name: the 1005 probe feeds
 * ?1005h, captures the reply to ?1005$p, then feeds ?1005l; the urxvt probe feeds nothing and reads only mouseTracking.
 */
export function mexRecordingContext(caseId, served) {
    const pinned = mexPinned[caseId];
    let fed = 0, captured = 0;
    const refuse = what => () => { throw new Error(`Pinned ${caseId} must not ${what}.`); };
    return {
        get fedCount() { return fed; },
        get captureCount() { return captured; },
        feed: text => {
            if (fed >= pinned.feeds.length || text !== pinned.feeds[fed] || (caseId === 'modes.utf8-mouse-1005' && fed === 1 && captured !== 1)) {
                throw new Error(`Pinned ${caseId} feed ${fed} differs from the contract.`);
            }
            fed++;
        },
        feedCapture: text => {
            if (caseId !== 'modes.utf8-mouse-1005' || text !== pinned.capture || fed !== 1 || captured !== 0 || typeof served.response !== 'string') {
                throw new Error(`Pinned ${caseId} captured outside its contract.`);
            }
            captured++;
            return served.response;
        },
        getMode: name => {
            if (caseId === 'modes.utf8-mouse-1005' || name !== pinned.mode) throw new Error(`Pinned ${caseId} reads an uncontracted mode.`);
            if (fed !== 0) throw new Error(`Pinned ${caseId} fed unexpectedly.`);
            return served.currentMode;
        },
        getCell: refuse('read cells'),
        getCursor: refuse('read the cursor'),
    };
}
