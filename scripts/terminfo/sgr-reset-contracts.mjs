// Versioned probe contracts for the SGR selective-reset qualification batch:
// sgr.selective-reset.underline (SGR 24) and sgr.selective-reset.inverse (SGR 27).
//
// Pinned terminfo.dev@4.0.0 definitions (bundle SHA-256
// aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6, probeHash 16a690f34c0e):
//   resetProbe("sgr.selective-reset.underline", "\x1B[1;4m", "\x1B[24m", ["underline"], ["bold"])
//   resetProbe("sgr.selective-reset.inverse",   "\x1B[1;7m", "\x1B[27m", ["inverse"],   ["bold"])
// resetProbe feeds ONE string, `C${setup}X${reset}Y`, reads cells (0,0), (0,1) and (0,2) and
// runs measuredReset: the baseline C must measure every contracted attribute false, X every
// attribute true, and Y must expose every attribute as a boolean (underline: not false/null/"none");
// the case is supported iff every `set` attribute is false and every `preserve` attribute is true on Y.
// The pinned probes never read stored colors, overline, the underline style code or underline
// color; this contract adds them as controls that only make the verdict stricter. The pinned
// terminal path ("Cursor advance does not verify SGR reset") is never a grade.
//
// Naming: the pinned feature ids are exactly `sgr.selective-reset.underline` and
// `sgr.selective-reset.inverse`. Ntilde's own earlier ntilde-owned-sgr-indices-v1 suite
// (scripts/terminfo/sgr.mjs) carries two identically named experiments with DIFFERENT bytes (a
// combined colored cell, flag/index readback); those are Ntilde-owned observations, not these
// pinned fixtures and not benchmark grades. The pinned SGR 24 case is not sgr.underline.single,
// not sgr.underline-color-reset (SGR 59) and not sgr.reset (SGR 0); the pinned SGR 27 case is not
// sgr.inverse (SGR 7) and not DECSCNM. No variant is substituted.
//
// Scope is the STORED state: attribute flags, the exact stored underline style (disabled
// representation: flag clear and style code 0), stored text and stored colors. Underline color is
// OBSERVED (documented) but never required: SGR 24 vs SGR 59 behavior is recorded without a new
// color policy. Resolved colors only bind DECSCNM; no pixel or rendered-appearance claim.
import { planFeed, planRead, planFeeds, planReads, validateExactFeeds } from './graded-plan.mjs';
import { sgrAttrsCellAt, sgrAttrsReset, sgrAttrsClear, validateSgrAttrsScreen, sgrAttrsPredicates } from './sgr-attrs-contracts.mjs';

const { neutral, othersClear, exactly } = sgrAttrsPredicates;

/** Contracts v1 is the first measured semantics of this batch. */
export const sgrResetContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const sgrResetSuiteId = 'ntilde-owned-sgr-selective-reset-graded-v1';
/** The nonce prefix binding worker output to this suite. */
export const sgrResetNoncePrefix = 'NTILDE_SR_';
/** The measured reset every plan begins with: DECSCNM, DECOM, margins, IRM and SGR off, screen cleared, cursor home. */
export const sgrResetReset = sgrAttrsReset;
/** The attribute suite's screen validation, so attribute runs, stored details and resolved colors must agree. */
export const validateSgrResetScreen = validateSgrAttrsScreen;
/** The stored cell projection is the attribute suite's: flags, exact style, neutral-state fields. */
export const sgrResetCellAt = sgrAttrsCellAt;
const clear = sgrAttrsClear;

/** The pinned measuredReset notes, byte for byte. */
export const pinnedNotExposedNote = 'Baseline, styled, or reset cell was not exposed';
export const pinnedControlNote = 'Neutral baseline, styled setup, or reset readback was not measured';
const fullReset = '\x1b[0m';
const colorSetup = '\x1b[58;2;255;0;128m';

/** The stored tuple compared between cells (everything the projection keeps except the character). */
const tuple = ['flags', 'underlineStyle', 'overline', 'fgSource', 'bgSource', 'fgIndex', 'bgIndex',
    'foregroundArgb', 'backgroundArgb', 'underlineColorIndex', 'underlineArgb'];
const sameTuple = (a, b) => tuple.every(field => a[field] === b[field]);

/** Whether the contract's target attribute is stored active / in its disabled representation. */
const on = (contract, cell) => (contract.attr === 'underline' ? cell.underline === 'single' && cell.underlineStyle === 1 : cell.inverse === true);
const off = (contract, cell) => (contract.attr === 'underline'
    ? cell.underline === false && cell.underlineStyle === 0 && (cell.flags & 0x0008) === 0
    : cell.inverse === false && (cell.flags & 0x0002) === 0);

/** One case: the pinned fixture plus its discriminating controls, in a fixed plan. */
const case_ = ({ caseId, attr, setup, reset, wrongReset, wrongLabel, extras }) => {
    const pinnedFixture = `C${setup}X${reset}Y`;
    const groups = [
        { role: 'initial', feeds: [] },
        { role: 'pinned', feeds: [clear, pinnedFixture] },
        { role: 'noop', feeds: [clear, `C${setup}XZ`] },
        { role: 'full', feeds: [clear, `C${setup}X${fullReset}Y`] },
        { role: 'wrong', feeds: [clear, `C${setup}X${wrongReset}Y`] },
        ...extras,
    ];
    const plan = [planFeed(sgrResetReset)];
    for (const group of groups) {
        for (const feed of group.feeds) plan.push(planFeed(feed));
        plan.push(planRead('cells'));
        if (group.after) plan.push(planFeed(group.after));
    }
    return Object.freeze({
        caseId, probeId: caseId, attr, setup, reset, wrongReset, wrongLabel,
        roles: Object.freeze(groups.map(group => group.role)),
        pinnedPath: 'termless', pinnedRead: 1, pinnedFixtureCells: 3,
        pinnedExpected: `${caseId}: measured setup attributes clear on Y and unrelated attributes remain set`,
        pinnedCriterion: 'measured setup attributes clear on Y and unrelated attributes remain set',
        set: Object.freeze([attr]), preserve: Object.freeze(['bold']),
        pinnedFeeds: Object.freeze([pinnedFixture]),
        reverseControlRead: attr === 'inverse' ? groups.findIndex(group => group.role === 'decscnm') : undefined,
        plan: Object.freeze(plan),
        terminalFixture: { writes: [`${setup}X${reset}Y`], demand: 'consumed', minimum: { rows: 1, cols: 3 } },
        appearanceScope: attr === 'underline'
            ? 'stored underline flag and exact stored single style before SGR 24, the disabled representation (flag clear, style 0) after it, bold preserved, stored colors and underline color untouched by the pinned fixture; SGR 59 and underline-color behavior observed not required; stroke appearance is not claimed'
            : 'stored per-cell inverse flag before and after SGR 27 with DECSCNM proven off, bold preserved, stored foreground/background untouched, per-cell inverse kept distinct from a DECSCNM-on control; the foreground/background swap and rendered appearance are not claimed',
        grade: observation => gradeReset(contract_(caseId), observation),
        observed: observation => briefReset(contract_(caseId), observation),
    });
};

const contracts = [];
const contract_ = caseId => contracts.find(item => item.caseId === caseId);

const underlineExtras = [
    { role: 'double', feeds: [clear, `C\x1b[1;21mX\x1b[24mY`] },
    { role: 'color', feeds: [clear, `C\x1b[1;4m${colorSetup}X\x1b[24mY\x1b[4mZ`] },
    { role: 'sgr59', feeds: [clear, `C\x1b[1;4m${colorSetup}X\x1b[59mY`] },
];
const inverseExtras = [
    { role: 'decscnm', feeds: [clear, '\x1b[?5h' + 'C\x1b[1;7mX\x1b[27mY'], after: '\x1b[?5l' + clear },
];

contracts.push(
    case_({ caseId: 'sgr.selective-reset.underline', attr: 'underline', setup: '\x1b[1;4m', reset: '\x1b[24m',
        wrongReset: '\x1b[22m', wrongLabel: 'SGR 22', extras: underlineExtras }),
    case_({ caseId: 'sgr.selective-reset.inverse', attr: 'inverse', setup: '\x1b[1;7m', reset: '\x1b[27m',
        wrongReset: '\x1b[24m', wrongLabel: 'SGR 24', extras: inverseExtras }),
);
/** The two pinned cases, in suite order. */
export const sgrResetContracts = Object.freeze(contracts);
/** Contract case ids in suite order. */
export const sgrResetCaseIds = Object.freeze(sgrResetContracts.map(contract => contract.caseId));

/** Whether a cell is the stored X-with-setup shape: target attribute and bold on, nothing else, default colors. */
const active = (contract, cell) => on(contract, cell) && cell.bold === true && othersClear(cell, [contract.attr, 'bold']);

/** The shared control walk, then the exact selective-reset verdict. */
function gradeReset(contract, observation) {
    const { initial, pinned, noop, full, wrong, reverseVideo } = observation;
    const [baseline, target, after] = pinned;
    if (baseline.char !== 'C' || target.char !== 'X' || after.char !== 'Y') return { outcome: 'inconclusive', note: pinnedNotExposedNote };
    if (reverseVideo !== false) {
        return { outcome: 'inconclusive', note: 'DECSCNM reverse video was not proven off, so stored attributes cannot be separated from it' };
    }
    if (!neutral(initial) || !neutral(baseline)) {
        return { outcome: 'inconclusive', note: 'Neutral baseline control was not measured (an attribute was initially active)' };
    }
    if (!active(contract, target)) {
        return { outcome: 'inconclusive', note: `The setup did not store only ${contract.attr} and bold on X with default colors, so the reset control is invalid` };
    }
    // A no-op reset would leave Y exactly like X: the same bytes without the reset must keep the setup state on Z.
    if (!sameTuple(noop[1], target) || noop[2].char !== 'Z' || !sameTuple(noop[2], target)) {
        return { outcome: 'inconclusive', note: 'The no-reset control did not show the setup state persisting, so a no-op reset cannot be distinguished' };
    }
    // An accidental full SGR 0 clears bold too: the readback must show that difference.
    if (!sameTuple(full[1], target) || full[2].char !== 'Y' || !neutral(full[2])) {
        return { outcome: 'inconclusive', note: 'The full-reset control did not return Y to the neutral state, so a full reset cannot be distinguished' };
    }
    // A reset aimed at another attribute must leave the target set (and clear what it targets).
    const wrongExpected = contract.attr === 'underline' ? ['underline'] : ['inverse', 'bold'];
    if (!sameTuple(wrong[1], target) || wrong[2].char !== 'Y' || !exactly(wrong[2], wrongExpected)) {
        return { outcome: 'inconclusive', note: `The ${contract.wrongLabel} control did not leave ${contract.attr} as contracted, so a wrong reset cannot be distinguished` };
    }
    if (contract.attr === 'inverse') {
        const { decscnm, reverseControl } = observation;
        if (reverseControl !== true) return { outcome: 'inconclusive', note: 'The DECSCNM-on control was not engaged, so per-cell inverse cannot be separated from global reverse video' };
        if (decscnm.some((cell, index) => cell.char !== 'CXY'[index] || cell.flags !== pinned[index].flags
            || !sameTuple(cell, pinned[index]))) {
            return { outcome: 'inconclusive', note: 'Per-cell inverse storage changed under DECSCNM, so it is not distinct from global reverse video' };
        }
    }
    if (!off(contract, after)) {
        return { outcome: 'unsupported', note: contract.attr === 'underline'
            ? 'The stored underline flag or style survived SGR 24 on Y' : 'The stored inverse flag survived SGR 27 on Y' };
    }
    if (after.bold !== true) return { outcome: 'unsupported', note: `The reset also cleared bold on Y (accidental full reset): ${contract.reset === '\x1b[24m' ? 'SGR 24' : 'SGR 27'} must be selective` };
    if (!othersClear(after, ['bold'])) {
        return { outcome: 'unsupported', note: 'Unrelated styles, stored colors or underline color drifted on Y after the reset' };
    }
    return { outcome: 'supported' };
}

/** A compact stored record of one cell. */
const brief = cell => ({ char: cell.char, flags: cell.flags, bold: cell.bold, underline: cell.underline, underlineStyle: cell.underlineStyle,
    inverse: cell.inverse, overline: cell.overline, fgIndex: cell.fgIndex, bgIndex: cell.bgIndex, foregroundArgb: cell.foregroundArgb,
    backgroundArgb: cell.backgroundArgb, underlineColorIndex: cell.underlineColorIndex, underlineArgb: cell.underlineArgb });

/** The observed record: every group, plus the documented (not required) underline-color behavior. */
function briefReset(contract, observation) {
    const record = { initial: brief(observation.initial), pinned: observation.pinned.map(brief),
        controls: { noReset: observation.noop.map(brief), fullReset: observation.full.map(brief),
            [`wrongReset(${contract.wrongLabel})`]: observation.wrong.map(brief) },
        reverseVideo: observation.reverseVideo };
    if (contract.attr === 'inverse') {
        record.controls.decscnmOn = observation.decscnm.map(brief);
        record.reverseControl = observation.reverseControl;
    } else {
        record.controls.doubleUnderlineThenSgr24 = observation.double.map(brief);
        record.controls.sgr59KeepsUnderline = observation.sgr59.map(brief);
        record.underlineColorBehavior = underlineColorBehavior(observation);
    }
    return record;
}

/** Document what SGR 24 and SGR 59 did to the stored underline color; observed only, never graded. */
function underlineColorBehavior(observation) {
    const [, x, afterReset, reEnabled] = observation.color;
    const [, x59, after59] = observation.sgr59;
    const literal = cell => cell.underlineArgb !== null;
    const state = cell => ({ underlineColorIndex: cell.underlineColorIndex, underlineArgb: cell.underlineArgb });
    return { policy: 'observed, not required: no pinned criterion constrains underline color',
        requestedLiteralStored: literal(x) && literal(x59),
        afterSgr24: { underline: afterReset.underline, ...state(afterReset) },
        afterSgr24ThenSgr4: { underline: reEnabled.underline, ...state(reEnabled) },
        afterSgr59: { underline: after59.underline, ...state(after59) },
        sgr24ClearsStoredColor: literal(x) ? !literal(afterReset) : null,
        sgr24ColorSurvivesReEnable: literal(x) ? literal(reEnabled) : null,
        sgr59ClearsStoredColor: literal(x59) ? !literal(after59) : null,
        sgr59KeepsUnderline: after59.underline !== false };
}

/** Group the read screens of one case by role and project cells. */
export function sgrResetObservation(contract, screens) {
    if (!Array.isArray(screens) || screens.length !== contract.roles.length) throw new Error('Missing SGR selective-reset read captures.');
    const binding = role => screens[contract.roles.indexOf(role)].colorBinding.reverseVideo;
    const cells = (role, count) => Array.from({ length: count }, (_, col) => sgrResetCellAt(screens[contract.roles.indexOf(role)], 0, col));
    const graded = contract.roles.filter(role => role !== 'decscnm').map(binding);
    const observation = { initial: sgrResetCellAt(screens[0], 0, 0), pinned: cells('pinned', 3), noop: cells('noop', 3),
        full: cells('full', 3), wrong: cells('wrong', 3),
        reverseVideo: graded.every(value => value === false) ? false : graded.some(value => value === true) ? true : null };
    if (contract.attr === 'inverse') {
        observation.decscnm = cells('decscnm', 3);
        observation.reverseControl = binding('decscnm');
    } else {
        observation.double = cells('double', 3);
        observation.color = cells('color', 4);
        observation.sgr59 = cells('sgr59', 3);
    }
    return observation;
}

/** Independent contract grader entry: mirrors the pinned criteria without the bundle. */
export function gradeSgrResetCase(contract, observation, revision = sgrResetContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown SGR selective-reset contracts revision ${revision}.`);
    const groups = contract.attr === 'inverse' ? ['pinned', 'noop', 'full', 'wrong', 'decscnm'] : ['pinned', 'noop', 'full', 'wrong', 'double', 'sgr59'];
    if (!observation?.initial || groups.some(group => !Array.isArray(observation[group]) || observation[group].length !== 3)
        || (contract.attr === 'underline' && (!Array.isArray(observation.color) || observation.color.length !== 4))) {
        throw new Error(`Missing ${contract.caseId} read captures.`);
    }
    return contract.grade(observation);
}

export { planFeeds as sgrResetFeeds, planReads as sgrResetReads } from './graded-plan.mjs';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateSgrResetFeeds(contract, feeds) {
    validateExactFeeds(contract, feeds, 'contract');
}
