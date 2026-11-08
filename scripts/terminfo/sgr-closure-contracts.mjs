// Versioned probe contracts for the SGR closure qualification batch: sgr.blink, sgr.overline and
// sgr.underline.color.
//
// Pinned terminfo.dev@4.0.0 definitions (bundle SHA-256
// aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6, probeHash 16a690f34c0e):
//   sgrProbe("sgr.blink",    "\x1B[5m",  cell => cell.blink === true)
//   sgrProbe("sgr.overline", "\x1B[53m", cell => cell.overline === undefined ? null : cell.overline === true,
//                                        cell => cell.overline === undefined ? "cell.overline field not exposed" : null)
//   rgbUnderlineProbe("sgr.underline.color")
// sgrProbe feeds `<sequence>X`, reads cell (0,0) and supports only when the X cell passes the check. The overline
// registration adds a no-semantic-observable guard: an UNAVAILABLE overline field (undefined) is a not-tested result,
// while an exposed false value is the generic inconclusive ("a negative needs rendering review"); the pinned probes
// never return unsupported. This contract grader may grade unsupported, but only after valid controls establish an
// observed mismatch.
//
// sgr.underline.color is a separate registered id built by the SAME rgbUnderlineProbe helper as the already-qualified
// sgr.underline-color-rgb. The two share fixture bytes and criteria and differ only in the id interpolated into the
// pinned `expected` text. Each is executed and graded from its own journaled reads; this batch never copies the
// sibling's verdict and the two are counted as two registered ids.
//
// Scope is the STORED state: attribute flags (blink bit 0x20), the stored overline boolean (an explicit, always
// exposed field in the owned projection), underline style and stored underline color (index, literal bytes or the null
// default marker), with parent-derived RGB for the color case. No animation, blink timing, overline stroke or rendered
// underline appearance is claimed, and no App-resolved color is used.
import { planFeed, planRead, planFeeds, planReads, validateExactFeeds } from './graded-plan.mjs';
import { sgrAttrsCellAt, sgrAttrsClear, validateSgrAttrsScreen, sgrAttrsPredicates, pinnedNoRenderedCellNote } from './sgr-attrs-contracts.mjs';
import { sgrUnderlineColorContracts, sgrUnderlineColorReset, sgrUnderlineColorObservation, sgrUnderlineColorCellAt,
    gradeSgrUnderlineColorCase } from './sgr-underline-color-contracts.mjs';

const { neutral, othersClear, keepsTarget, storedNeutral } = sgrAttrsPredicates;

/** Contracts v1 is the first measured semantics of this batch. */
export const sgrClosureContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const sgrClosureSuiteId = 'ntilde-owned-sgr-closure-graded-v1';
/** The nonce prefix binding worker output to this suite. */
export const sgrClosureNoncePrefix = 'NTILDE_CL_';
/** The measured reset every plan begins with (DECSCNM, DECOM, margins, IRM, SGR off, clear, home, palette restore). */
export const sgrClosureReset = sgrUnderlineColorReset;
/** The attribute suite's screen validation, so attribute runs, stored details and resolved colors must agree. */
export const validateSgrClosureScreen = validateSgrAttrsScreen;
/** The pinned cell shape of blink and overline: the attribute suite's stored projection. */
export const sgrClosureCellAt = sgrAttrsCellAt;
/** The pinned no-semantic-observable note of the overline registration, byte for byte. */
export const pinnedOverlineUnavailableNote = 'cell.overline field not exposed';
/** The pinned sgrProbe inconclusive note, byte for byte. */
export const pinnedUnreportedNote = 'Cell readback cannot distinguish unsupported styling from unreported attributes or collapsed styles; a negative needs rendering review';
export { pinnedNoRenderedCellNote };

const clear = sgrAttrsClear;
const attributeNames = ['bold', 'dim', 'italic', 'underline', 'inverse', 'hidden', 'strikethrough', 'blink', 'overline'];
/** The stored underline color of the overline decoy: the literal requested by SGR 58;2;255;0;128, no palette index. */
const decoyLiteral = 0xffff0080;
/** Whether a cell has exactly the named attributes set (underline: single style), default fg/bg, and the contracted
 * stored underline color: the literal for the overline decoy, none otherwise. */
const decoyExactly = (cell, set, { color = false } = {}) => attributeNames.every(name => (name === 'underline' ? (cell.underline !== false) : cell[name]) === set.includes(name))
    && storedNeutral(cell) && (!set.includes('underline') || cell.underlineStyle === 1)
    && cell.underlineColorIndex === -1 && (cell.underlineArgb === null ? null : cell.underlineArgb >>> 0) === (color ? decoyLiteral : null);

/** One toggle case (blink or overline): neutral control, the pinned fixture, a reset control and independence controls. */
const toggle = ({ caseId, attr, sequence, resetSequence, decoy, decoySet, independence }) => Object.freeze({
    kind: 'toggle', caseId, probeId: caseId, attr, sequence, resetSequence, decoy, decoySet,
    pinnedPath: 'termless', pinnedRead: 1,
    pinnedExpected: `${caseId}: attribute applied to the rendered X cell`,
    pinnedCriterion: 'attribute applied to the rendered X cell',
    pinnedFeeds: Object.freeze([sequence + 'X']),
    plan: Object.freeze([planFeed(sgrClosureReset), planFeed('C'), planRead('cells'),
        planFeed(clear), planFeed(sequence + 'X'), planRead('cells'),
        planFeed(resetSequence + 'Y'), planRead('cells'),
        planFeed(clear), planFeed(decoy), planRead('cells'),
        ...(independence ? [planFeed(clear), planFeed(independence), planRead('cells')] : [])]),
    reads: independence ? 5 : 4,
    terminalFixture: { writes: [sequence + 'X\x1b[0m'], demand: 'consumed (cursor advance does not verify SGR styling)', minimum: { rows: 1, cols: 2 } },
    appearanceScope: attr === 'blink'
        ? 'stored blink flag only (bit 0x20), distinct from every other attribute; animation, blink timing and rendered blinking are not claimed'
        : 'stored overline boolean (an explicit exposed field), independent of the underline flag, style and color; overline stroke appearance is not claimed',
    grade: observation => gradeToggle(attr, observation, { sequence, resetSequence, decoySet, decoyColor: attr === 'overline' }),
    observed: observation => briefToggle(attr, observation),
});

const blinkCase = toggle({ caseId: 'sgr.blink', attr: 'blink', sequence: '\x1b[5m', resetSequence: '\x1b[25m',
    decoy: '\x1b[1;2;3;4;7;8;9;53mW', decoySet: ['bold', 'dim', 'italic', 'underline', 'inverse', 'hidden', 'strikethrough', 'overline'] });
const overlineCase = toggle({ caseId: 'sgr.overline', attr: 'overline', sequence: '\x1b[53m', resetSequence: '\x1b[55m',
    decoy: '\x1b[1;2;3;4;5;7;8;9m\x1b[58;2;255;0;128mW', decoySet: ['bold', 'dim', 'italic', 'underline', 'blink', 'inverse', 'hidden', 'strikethrough'],
    independence: '\x1b[4;53mV\x1b[24mU\x1b[55mT' });

/** sgr.underline.color: the sibling rgb contract's plan, controls and grader, under its own registered id and pinned text. */
const rgbSibling = sgrUnderlineColorContracts.find(contract => contract.caseId === 'sgr.underline-color-rgb');
const underlineColorCase = Object.freeze({ ...rgbSibling, kind: 'underline-color', caseId: 'sgr.underline.color', probeId: 'sgr.underline.color',
    siblingCaseId: 'sgr.underline-color-rgb',
    pinnedExpected: 'sgr.underline.color: X has explicit rgb(255,0,128) underline distinct from the reset/default B cell',
    reads: 2 });

/** The three pinned cases, in suite order. */
export const sgrClosureContracts = Object.freeze([blinkCase, overlineCase, underlineColorCase]);
/** Contract case ids in suite order. */
export const sgrClosureCaseIds = Object.freeze(sgrClosureContracts.map(contract => contract.caseId));

/** A compact stored record of one toggle cell. */
const brief = cell => ({ char: cell.char, flags: cell.flags, blink: cell.blink, overline: cell.overline, underline: cell.underline,
    underlineStyle: cell.underlineStyle, underlineColorIndex: cell.underlineColorIndex, underlineArgb: cell.underlineArgb,
    fgIndex: cell.fgIndex, bgIndex: cell.bgIndex, foregroundArgb: cell.foregroundArgb, backgroundArgb: cell.backgroundArgb });
function briefToggle(attr, observation) {
    return { baseline: brief(observation.baseline), target: brief(observation.target), trailing: observation.trailing.map(brief),
        controls: { decoy: brief(observation.decoy), ...(observation.independence ? { independence: observation.independence.map(brief) } : {}) },
        reverseVideo: observation.reverseVideo };
}

/** Every attribute except overline equals the expected set, with default stored colors and no underline color. */
const attributesOnly = (cell, set) => attributeNames.every(name => name === 'overline'
    || (name === 'underline' ? (cell.underline !== false) : cell[name]) === set.includes(name))
    && storedNeutral(cell) && cell.underlineColorIndex === -1 && cell.underlineArgb === null;

/** The overline independence group: V has underline (and overline), U has only overline once the underline ends, T has
 * nothing after SGR 55; no other attribute or color anywhere. Overline-state checks only bind when the target worked, so an
 * unsupported overline stays gradeable. */
function independenceValid(group, whenSupported) {
    const [v, u, t] = group;
    const shape = v.char === 'V' && u.char === 'U' && t.char === 'T'
        && attributesOnly(v, ['underline']) && v.underlineStyle === 1 && attributesOnly(u, []) && u.underlineStyle === 0
        && attributesOnly(t, []) && t.underlineStyle === 0;
    return shape && (!whenSupported || (v.overline === true && u.overline === true && t.overline === false));
}

/** The shared control walk, then the exact stored-attribute verdict. */
function gradeToggle(attr, observation, { decoySet, decoyColor }) {
    const { baseline, target, trailing, decoy, independence, reverseVideo } = observation;
    if (target.char !== 'X') return { outcome: 'inconclusive', note: pinnedNoRenderedCellNote };
    if (reverseVideo !== false) return { outcome: 'inconclusive', note: 'DECSCNM reverse video was not proven off, so stored attributes cannot be separated from it' };
    if (baseline.char !== 'C' || !neutral(baseline)) return { outcome: 'inconclusive', note: 'Neutral baseline control was not measured (an attribute was initially active)' };
    if (typeof target[attr] !== 'boolean') return { outcome: 'inconclusive', note: `The ${attr} field was not exposed as a boolean` };
    if (!othersClear(target, [attr])) return { outcome: 'inconclusive', note: 'Unrelated attributes or colors drifted on the target, so the setup control is invalid' };
    const worked = target[attr] === true;
    // The reset control: X keeps its exact stored tuple and Y returns to neutral.
    if (!keepsTarget(trailing, target) || trailing[1].char !== 'Y' || !neutral(trailing[1])) {
        return { outcome: 'inconclusive', note: 'Reset/restoration control cells were not measured as contracted' };
    }
    // Wrong-flag control: a cell with every OTHER attribute set must not read the target attribute.
    if (decoy.char !== 'W' || !decoyExactly(decoy, decoySet, { color: decoyColor }) || decoy[attr] !== false) {
        return { outcome: 'inconclusive', note: `The decoy control did not show ${attr} independent of the other attributes` };
    }
    if (attr === 'overline' && !independenceValid(independence, worked)) {
        return { outcome: 'inconclusive', note: 'The overline independence control (underline reset, no-overline cell) was not measured as contracted' };
    }
    if (worked) return { outcome: 'supported' };
    return { outcome: 'unsupported', note: target[attr] === false
        ? `The stored ${attr} value was exposed as false after the pinned sequence (an unavailable field would not reach this verdict)`
        : `The ${attr} state was not stored as contracted` };
}

/** Project the read groups of one case into the grader's observation. */
export function sgrClosureObservation(contract, screens, startBinding) {
    if (contract.kind === 'underline-color') return sgrUnderlineColorObservation(contract, screens, startBinding);
    if (!Array.isArray(screens) || screens.length !== contract.reads) throw new Error(`Missing ${contract.caseId} read captures.`);
    const cells = (screen, count) => Array.from({ length: count }, (_, col) => sgrClosureCellAt(screen, 0, col));
    const reverse = screens.map(screen => screen.colorBinding.reverseVideo);
    return { baseline: cells(screens[0], 1)[0], target: cells(screens[1], 1)[0], trailing: cells(screens[2], 2), decoy: cells(screens[3], 1)[0],
        ...(contract.reads === 5 ? { independence: cells(screens[4], 3) } : {}),
        reverseVideo: reverse.every(value => value === false) ? false : reverse.some(value => value === true) ? true : null };
}

/** Independent contract grader entry: mirrors the pinned criteria without the bundle. */
export function gradeSgrClosureCase(contract, observation, revision = sgrClosureContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown SGR-closure contracts revision ${revision}.`);
    if (contract.kind === 'underline-color') return gradeSgrUnderlineColorCase(contract, observation);
    if (!observation?.baseline || !observation.target || !Array.isArray(observation.trailing) || observation.trailing.length !== 2 || !observation.decoy
        || (contract.reads === 5 && (!Array.isArray(observation.independence) || observation.independence.length !== 3))) {
        throw new Error(`Missing ${contract.caseId} read captures.`);
    }
    return contract.grade(observation);
}

export { planFeeds as sgrClosureFeeds, planReads as sgrClosureReads } from './graded-plan.mjs';
export { sgrUnderlineColorCellAt as sgrClosureUnderlineCellAt };

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateSgrClosureFeeds(contract, feeds) {
    validateExactFeeds(contract, feeds, 'contract');
}
