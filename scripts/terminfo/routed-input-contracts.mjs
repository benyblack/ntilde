// Independent contracts for the routed-input benchmark layer (input.x10-mouse, input.button-event-mouse, modes.application-keypad).
// The oracle below is written from xterm's control-sequence documentation and the VT100/VT220 keypad tables; it never calls the App's
// encoder. Each plan case carries a literal expectation that must equal the oracle, and the child's raw bytes must equal the literal.
import { benchmarkIds, makeRoutedInputCases } from './routed-input-cases.mjs';

export const routedInputGradedSuiteId = 'ntilde-owned-routed-input-benchmark-graded-v1';
export const routedInputContractsVersion = 1;
export const routedInputCaseIds = Object.freeze([...benchmarkIds]);
export const routedInputEffectContracts = Object.freeze({
    'input.x10-mouse': 'ntilde-x10-mouse-tracking-effect-v1',
    'input.button-event-mouse': 'ntilde-button-event-mouse-tracking-effect-v1',
    'modes.application-keypad': 'ntilde-application-keypad-effect-v1',
});

const hex = value => Buffer.from(value, 'latin1').toString('hex');

/** Legacy report value: one octet 32 + v for v in 0..223; a larger value is not representable and never produced here. */
const octet = value => String.fromCharCode(32 + value);

const buttonIndex = Object.freeze({ left: 0, middle: 1, right: 2 });
/** The button code each event kind reports under a tracking protocol before modifiers, or null when the protocol does not report it. */
const codeByKind = Object.freeze({
    mousePress: ({ button }) => buttonIndex[button] ?? null,
    mouseRelease: ({ tracking, sgr, button }) => (tracking === 9 || buttonIndex[button] === undefined ? null : (sgr ? buttonIndex[button] : 3)),
    mouseWheel: ({ tracking, wheelDelta }) => (tracking === 9 ? null : (wheelDelta > 0 ? 64 : 65)),
    mouseMove: ({ tracking, button }) => {
        const reports = tracking === 1003 || (tracking === 1002 && button !== 'none');
        return reports ? (buttonIndex[button] ?? 3) + 32 : null;
    },
});
/**
 * xterm mouse reports. `tracking` is the one selected protocol (9 X10 press-only, 1000 normal, 1002 button-event, 1003 any-event);
 * `sgr` selects coordinate encoding only. Button codes: left 0, middle 1, right 2, release 3 (legacy), wheel 64/65, motion +32,
 * modifiers shift 4, alt 8, ctrl 16. Returns the exact report bytes (latin1) or '' when the protocol does not report the event.
 * The generated-event bit mask is shift=1, ctrl=2, alt=4, meta=8.
 */
export function xtermMouseReport(event) {
    const { tracking, sgr, kind, modifiers = 0, column, row } = event;
    const base = Object.hasOwn(codeByKind, kind) && [9, 1000, 1002, 1003].includes(tracking) ? codeByKind[kind](event) : null;
    if (base === null) return '';
    // X10 compatibility mode never carries modifier information; xterm's meta bit is alt's 8, so alt or meta adds 8 once.
    const code = base + (tracking === 9 ? 0 : (modifiers & 1 ? 4 : 0) + (modifiers & 2 ? 16 : 0) + (modifiers & 12 ? 8 : 0));
    if (sgr) return `\x1b[<${code};${column};${row}${kind === 'mouseRelease' ? 'm' : 'M'}`;
    if (column > 223 || row > 223) throw new Error('Legacy mouse coordinates above 223 are not representable.');
    return '\x1b[M' + octet(code) + octet(column) + octet(row);
}

/** VT100 table 3-8 / xterm VT220-style keypad: application mode sends SS3 + final; numeric mode sends nothing on key-down except Enter (CR). */
const keypadFinals = Object.freeze({ '0': 'p', '1': 'q', '2': 'r', '3': 's', '4': 't', '5': 'u', '6': 'v', '7': 'w', '8': 'x', '9': 'y',
    decimal: 'n', separator: 'l', add: 'k', subtract: 'm', multiply: 'j', divide: 'o', enter: 'M' });
const keypadText = Object.freeze({ '0': '0', '1': '1', '2': '2', '3': '3', '4': '4', '5': '5', '6': '6', '7': '7', '8': '8', '9': '9',
    decimal: '.', separator: ',', add: '+', subtract: '-', multiply: '*', divide: '/', enter: '\r' });
/** Bytes a declared keypad or text event delivers to the child. Releases send nothing. A text commit is ordinary text unless it
 * duplicates the keypad key just handled in application mode, which the framework reserves and suppresses. */
export function keypadReport({ applicationKeypad, kind, key, text, reserved }) {
    if (kind === 'keyUp') return '';
    if (kind === 'text') return reserved ? '' : text;
    if (kind !== 'keyDown' || !Object.hasOwn(keypadFinals, key)) return '';
    if (applicationKeypad) return '\x1bO' + keypadFinals[key];
    return key === 'enter' ? keypadText.enter : '';
}

/** The oracle's expectation for one plan case, or null when the case is not an oracle-covered mouse or keypad event. */
export function oracleHex(item, previous) {
    const { event, modes } = item;
    if (event.kind.startsWith('mouse')) {
        return hex(xtermMouseReport({ tracking: modes.mouseTracking, sgr: modes.mouseSgr, kind: event.kind, button: event.button,
            modifiers: event.modifiers, wheelDelta: event.wheelDelta, column: event.column, row: event.row }));
    }
    // Only a text commit right after a handled keypad key-down in application mode is a duplicate.
    const reserved = event.kind === 'text' && modes.applicationKeypad && previous?.event.kind === 'keyDown'
        && previous.modes.applicationKeypad && previous.event.key && keypadText[previous.event.key] === event.text;
    return hex(keypadReport({ applicationKeypad: modes.applicationKeypad, kind: event.kind, key: event.key, text: event.text, reserved }));
}

/** Every plan literal must equal the independent oracle; returns the mismatching ids (empty when the plan is coherent). */
export function oracleDisagreements(cases = makeRoutedInputCases(40, 140)) {
    return cases.flatMap((item, index) => oracleHex(item, cases[index - 1]) === item.expectedHex ? [] : [item.id]);
}

const absentEventNote = 'No generated input event and encoded report were validated';
/** The pinned definitions (terminfo.dev@4.0.0, probe-defs/src/input.ts and modes.ts), documented for the publication. */
export const routedInputPinned = Object.freeze({
    'input.x10-mouse': Object.freeze({
        criterion: 'mouseInputProbe("input.x10-mouse", "mouseTracking"): reads ctx.getMode("mouseTracking") and returns inconclusive for any value; the terminal path returns inconclusive without touching the terminal',
        feeds: Object.freeze([]), mode: 'mouseTracking', note: absentEventNote, evidence: 'legacy',
    }),
    'input.button-event-mouse': Object.freeze({
        criterion: 'mouseInputProbe("input.button-event-mouse", "mouseTracking"): reads ctx.getMode("mouseTracking") and returns inconclusive for any value; the terminal path returns inconclusive without touching the terminal',
        feeds: Object.freeze([]), mode: 'mouseTracking', note: absentEventNote, evidence: 'legacy',
    }),
    'modes.application-keypad': Object.freeze({
        criterion: 'feeds ESC =, reads ctx.getMode("applicationKeypad"), feeds ESC >, reads it again and returns notTestedResult("keypad input"): no semantic observable; the terminal path returns inconclusive without touching the terminal',
        feeds: Object.freeze(['\x1b=', '\x1b>']), mode: 'applicationKeypad', note: 'keypad input', evidence: 'not-tested',
    }),
});

/** The pinned termless result, key order included, over the served mode value(s). */
export function replayRoutedInputPinned(caseId, served) {
    if (caseId === 'modes.application-keypad') {
        return { pass: false, response: JSON.stringify({ on: served.on === true, off: served.off === false }), note: 'keypad input',
            notTested: { reason: 'no-semantic-observable', noObservable: 'keypad input' } };
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
export function contractExperiments(caseId, cases = makeRoutedInputCases(40, 140)) {
    return cases.filter(item => requiredStatuses.includes(item.roles[caseId])).map(item => ({ id: item.id, role: item.roles[caseId] }));
}

/**
 * Grade one benchmark from its measured experiments ({id, role, matched}). A mismatching required experiment establishes unsupported
 * only when every control held; an invalid control leaves the benchmark inconclusive; labels never grade.
 */
export function gradeRoutedInputCase(caseId, experiments, version = routedInputContractsVersion) {
    if (version !== routedInputContractsVersion) throw new Error('Unknown routed-input contracts version.');
    const planned = contractExperiments(caseId);
    if (experiments.length !== planned.length || planned.some((item, index) => experiments[index].id !== item.id || experiments[index].role !== item.role
        || typeof experiments[index].matched !== 'boolean')) throw new Error(`Routed-input experiments differ from the plan for ${caseId}.`);
    const failedControls = experiments.filter(item => item.role === 'control' && !item.matched).map(item => item.id);
    const failedRequired = experiments.filter(item => item.role === 'required' && !item.matched).map(item => item.id);
    if (failedControls.length) return { outcome: 'inconclusive', reason: 'control-mismatch', failedControls, failedRequired };
    if (failedRequired.length) return { outcome: 'unsupported', failedRequired };
    return { outcome: 'supported' };
}

/** The child's DECRQM reply value for one mode in a control or restoration capture: true set, false reset, undefined absent. */
export function modeFromReplies(replyHex, number) {
    const text = Buffer.from(replyHex, 'hex').toString('latin1'), prefix = `\x1b[?${number};`, at = text.indexOf(prefix);
    if (at < 0 || text.slice(at + prefix.length + 1, at + prefix.length + 3) !== '$y') return undefined;
    const value = text[at + prefix.length];
    if (value === '1') return true;
    return value === '2' ? false : undefined;
}
/** Whether any of the four tracking selectors is set in the replies (the pinned probe's "mouseTracking"). */
export const trackingFromReplies = replyHex => [9, 1000, 1002, 1003].some(number => modeFromReplies(replyHex, number) === true);

/** The plan cases whose setup is exactly a pinned feed of the keypad probe (the pinned fixture bytes, fed unchanged). */
export function pinnedFeedCases(caseId, cases = makeRoutedInputCases(40, 140)) {
    return routedInputPinned[caseId].feeds.map(feed => {
        const matches = cases.filter(item => item.setup === feed && item.roles[caseId]);
        if (matches.length !== 1) throw new Error(`Pinned feed for ${caseId} is not one plan case.`);
        return matches[0];
    });
}

/** The values the child's own replies serve to a pinned probe: control replies after each pinned feed, or the restored defaults. */
export function servedModes(caseId, controls, restored, cases = makeRoutedInputCases(40, 140)) {
    if (caseId === 'modes.application-keypad') {
        const [select, restore] = pinnedFeedCases(caseId, cases);
        return { on: modeFromReplies(controls[select.id], 66), off: modeFromReplies(controls[restore.id], 66),
            sources: [select.id, restore.id] };
    }
    return { currentMode: trackingFromReplies(restored), sources: ['restored'] };
}

/**
 * Serve the pinned code with the child's replies, refusing any feed or read the contract does not name: the pinned keypad probe feeds
 * exactly ESC = then ESC > and reads only applicationKeypad between and after; the mouse probes feed nothing and read only mouseTracking.
 */
export function routedInputRecordingContext(caseId, served) {
    const pinned = routedInputPinned[caseId];
    let fed = 0;
    return {
        get fedCount() { return fed; },
        feed: text => {
            if (fed >= pinned.feeds.length || text !== pinned.feeds[fed]) throw new Error(`Pinned ${caseId} feed ${fed} differs from the contract.`);
            fed++;
        },
        getMode: name => {
            if (name !== pinned.mode) throw new Error(`Pinned ${caseId} reads an uncontracted mode.`);
            if (caseId === 'modes.application-keypad') {
                if (fed === 1) return served.on;
                if (fed === 2) return served.off;
                throw new Error(`Pinned ${caseId} read the mode outside its feeds.`);
            }
            if (fed !== 0) throw new Error(`Pinned ${caseId} fed unexpectedly.`);
            return served.currentMode;
        },
        getCell: () => { throw new Error(`Pinned ${caseId} probe must not read cells.`); },
        getCursor: () => { throw new Error(`Pinned ${caseId} probe must not read the cursor.`); },
    };
}
