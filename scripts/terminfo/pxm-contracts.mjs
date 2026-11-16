// Independent contract for the SGR pixel-coordinate mouse benchmark layer (input.pixel-mouse, ?1016).
// The oracle below is written from xterm's control-sequence documentation (ctlseqs "SGR-Pixels (1016)": "the same mouse response format as the
// 1006 control, but report position in pixels rather than character cells") and its button.c emitters (EditorButton: row = event->y - OriginY,
// col = event->x - OriginX in this mode; EmitMousePosition prints value + 1; EmitButtonCode subtracts the 32 offset; a release keeps the button
// and ends in `m`): it never calls the App's encoder. Each plan case carries a literal expectation that must equal the oracle, and the child's
// raw bytes must equal the literal. The button/modifier arithmetic is the routed-input contract's own; the cell-coordinate encodings used by the
// controls and the slot cases are the extended-mouse oracle's.
import { modeFromReplies, xtermMouseCode } from './routed-input-contracts.mjs';
import { xtermExtendedMouseReport } from './mex-contracts.mjs';
import { makePxmCases, pxmBenchmarkIds } from './pxm-cases.mjs';

export const pxmGradedSuiteId = 'ntilde-owned-pixel-mouse-benchmark-graded-v1';
export const pxmContractsVersion = 1;
export const pxmCaseIds = Object.freeze([...pxmBenchmarkIds]);
export const pxmEffectContracts = Object.freeze({ 'input.pixel-mouse': 'ntilde-sgr-pixel-mouse-1016-effect-v1' });
export const pxmAloneCase = 'input.pxm.alone.set';

const hex = value => Buffer.from(value, 'latin1').toString('hex');

/**
 * The SGR-Pixels report for one declared pixel event: `CSI < Cb ; Px ; Py M` with Cb the SGR code (no 32 offset) and the zero-based
 * pixel offsets plus one; a release keeps the button and ends in `m`. Returns '' when the tracking protocol does not report the event.
 */
export function xtermPixelMouseReport(event) {
    const { tracking, kind, modifiers, button, wheelDelta, pixelX, pixelY } = event;
    const code = xtermMouseCode({ tracking, kind, modifiers, button, wheelDelta, sgr: true });
    if (code === null) return '';
    if (!Number.isSafeInteger(pixelX) || !Number.isSafeInteger(pixelY) || pixelX < 0 || pixelY < 0) throw new Error('A pixel report needs non-negative integer pixels.');
    return `\x1b[<${code};${pixelX + 1};${pixelY + 1}${kind === 'mouseRelease' ? 'm' : 'M'}`;
}

/** The one coordinate encoding a mode profile selects: the profile never carries two. */
export function encodingOfModes(modes) {
    const selected = [modes.mouseSgr && 'sgr', modes.mouseUtf8 && 'utf8', modes.mouseUrxvt && 'urxvt', modes.mousePixel && 'pixel'].filter(Boolean);
    if (selected.length > 1) throw new Error('A mode profile selects at most one coordinate encoding.');
    return selected[0] ?? '';
}

/**
 * xterm reports a motion only when the pointer position differs from the last reported one (button.c EditorButton, `changed = False`), and in
 * this mode the compared positions are pixels: the same pixel again under unchanged tracking sends nothing. `previous` is the plan case run
 * immediately before and already expected to have reported that motion.
 */
const repeatsReportedMotion = (item, previous) => previous !== undefined && previous.expectedHex !== '' && item.event.kind === 'mouseMove' && previous.event.kind === 'mouseMove'
    && previous.modes.mouseTracking === item.modes.mouseTracking && previous.event.pixelX === item.event.pixelX && previous.event.pixelY === item.event.pixelY;

/** The oracle's expectation for one plan case: the bytes its declared event must deliver to the child (`previous`: the preceding plan case). */
export function oracleHex(item, previous) {
    const { event, modes } = item, encoding = encodingOfModes(modes);
    if (!event.kind.startsWith('mouse')) return hex('');
    const common = { tracking: modes.mouseTracking, kind: event.kind, button: event.button, modifiers: event.modifiers, wheelDelta: event.wheelDelta };
    if (encoding !== 'pixel') return hex(xtermExtendedMouseReport({ ...common, encoding, column: event.column, row: event.row }));
    if (repeatsReportedMotion(item, previous)) return hex('');
    return hex(xtermPixelMouseReport({ ...common, pixelX: event.pixelX, pixelY: event.pixelY }));
}

/** Every pixel-layer plan literal must equal the independent oracle; returns the mismatching ids (empty when coherent). */
export function oracleDisagreements(cases = makePxmCases(40, 140)) {
    return cases.flatMap((item, index) => pxmCaseIds.some(id => item.roles[id] !== undefined) && oracleHex(item, cases[index - 1]) !== item.expectedHex ? [item.id] : []);
}

const absentEventNote = 'No generated input event and encoded report were validated';
/** The pinned definition (terminfo.dev@4.0.0, probe-defs/src/input.ts), documented for the publication. */
export const pxmPinned = Object.freeze({
    'input.pixel-mouse': Object.freeze({
        criterion: 'mouseInputProbe("input.pixel-mouse", "pixelMouse"): reads ctx.getMode("pixelMouse") and returns inconclusive for any value; the terminal path returns inconclusive without touching the terminal',
        feeds: Object.freeze([]), mode: 'pixelMouse', note: absentEventNote, evidence: 'legacy',
    }),
});

/** The pinned termless result, key order included, over the served values. */
export function replayPxmPinned(caseId, served) {
    if (caseId !== 'input.pixel-mouse') throw new Error(`Unknown pixel-mouse case ${caseId}.`);
    return { pass: false, response: JSON.stringify({ currentMode: served.currentMode === true }), note: absentEventNote,
        observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'legacy', note: absentEventNote } };
}

/** The pinned outcome a result carries: only inconclusive is ever possible for this probe. */
export const pinnedOutcome = result => result?.observation?.outcome ?? 'invalid';

const requiredStatuses = Object.freeze(['required', 'control', 'label']);

/** Experiments of a benchmark in plan order, with the role that benchmark assigns. */
export function contractExperiments(caseId, cases = makePxmCases(40, 140)) {
    return cases.filter(item => requiredStatuses.includes(item.roles[caseId])).map(item => ({ id: item.id, role: item.roles[caseId] }));
}

/**
 * Grade the benchmark from its measured experiments ({id, role, matched}). A mismatching required experiment establishes unsupported only
 * when every control held; an invalid control leaves the benchmark inconclusive; labels never grade.
 */
export function gradePxmCase(caseId, experiments, version = pxmContractsVersion) {
    if (version !== pxmContractsVersion) throw new Error('Unknown SGR-pixel contracts version.');
    const planned = contractExperiments(caseId);
    if (experiments.length !== planned.length || planned.some((item, index) => experiments[index].id !== item.id || experiments[index].role !== item.role
        || typeof experiments[index].matched !== 'boolean')) throw new Error(`SGR-pixel experiments differ from the plan for ${caseId}.`);
    const failedControls = experiments.filter(item => item.role === 'control' && !item.matched).map(item => item.id);
    const failedRequired = experiments.filter(item => item.role === 'required' && !item.matched).map(item => item.id);
    if (failedControls.length) return { outcome: 'inconclusive', reason: 'control-mismatch', failedControls, failedRequired };
    if (failedRequired.length) return { outcome: 'unsupported', failedRequired };
    return { outcome: 'supported' };
}

/**
 * The value the child's own replies serve to the pinned probe's one read: the DECRQM state of ?1016 in the restored capture (what
 * `getMode("pixelMouse")` reports once collection is over), with the set state the selector-only case proved kept beside it.
 */
export function servedPxm(caseId, controls, restored) {
    if (caseId !== 'input.pixel-mouse') throw new Error(`Unknown pixel-mouse case ${caseId}.`);
    return { currentMode: modeFromReplies(restored, 1016) === true, on: modeFromReplies(controls[pxmAloneCase] ?? '', 1016), off: modeFromReplies(restored, 1016),
        sources: [pxmAloneCase, 'restored'] };
}

/** Serve the pinned code with the child's reply, refusing any feed, capture or read the contract does not name: it feeds nothing and reads only pixelMouse. */
export function pxmRecordingContext(caseId, served) {
    const pinned = pxmPinned[caseId], refuse = what => () => { throw new Error(`Pinned ${caseId} must not ${what}.`); };
    let fed = 0;
    return {
        get fedCount() { return fed; },
        get captureCount() { return 0; },
        feed: () => { fed++; throw new Error(`Pinned ${caseId} fed unexpectedly.`); },
        feedCapture: refuse('capture a reply'),
        getMode: name => {
            if (name !== pinned.mode) throw new Error(`Pinned ${caseId} reads an uncontracted mode.`);
            return served.currentMode;
        },
        getCell: refuse('read cells'),
        getCursor: refuse('read the cursor'),
    };
}
