// Versioned probe contracts for the SGR underline-style qualification batch:
// sgr.underline.double, sgr.underline.curly, sgr.underline.dotted and
// sgr.underline.dashed.
//
// Pinned terminfo.dev@4.0.0 definitions (bundle SHA-256
// aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6), all built
// by the same sgrProbe helper that the SGR-attribute batch mirrors:
//   sgrProbe("sgr.underline.double", "\x1B[21m",  cell => cell.underline === "double")
//   sgrProbe("sgr.underline.curly",  "\x1B[4:3m", cell => cell.underline === "curly")
//   sgrProbe("sgr.underline.dotted", "\x1B[4:4m", cell => cell.underline === "dotted")
//   sgrProbe("sgr.underline.dashed", "\x1B[4:5m", cell => cell.underline === "dashed")
// Each termless probe feeds `<sequence>X` and reads cell (0,0). Unlike
// sgr.underline.single (whose pinned check accepts ANY truthy underline), each of
// these pinned criteria already names one exact style, so the contract's exact
// stored-style requirement and the pinned check coincide; the contract adds only
// controls. The pinned spellings are qualified exactly: SGR 21 for double and the
// colon forms 4:3, 4:4 and 4:5 for the other three. SGR 4:2 (the colon spelling of
// double), SGR 4 and SGR 4:1 are labeled controls that receive no benchmark grade.
// The pinned probes never return unsupported: a failed check is the pinned
// inconclusive ("a negative needs rendering review"). This contract grader may
// grade unsupported, but only after valid controls establish an observed mismatch
// and show the readback can express more than one stored style.
//
// No pinned criterion requires rendered appearance, so the published scope is the
// STORED state: the underline flag, the exact stored underline-style value, stored
// text and default color storage. Dash, dot and wave geometry, stroke thickness,
// underline color and rasterisation are explicitly not claimed.
import { planFeed, planRead, planFeeds, planReads, validateExactFeeds } from './graded-plan.mjs';
import { sgrAttrsCellAt, sgrAttrsReset, sgrAttrsClear, validateSgrAttrsScreen,
    pinnedNoRenderedCellNote, pinnedUnreportedNote, sgrAttrsPredicates } from './sgr-attrs-contracts.mjs';

const { neutral, othersClear, keepsTarget } = sgrAttrsPredicates;

/** Contracts v1 is the first measured semantics of this batch. */
export const sgrUnderlineContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const sgrUnderlineSuiteId = 'ntilde-owned-sgr-underline-styles-graded-v1';
/** The nonce prefix binding worker output to this suite. */
export const sgrUnderlineNoncePrefix = 'NTILDE_SU_';
/** The measured reset every plan begins with: DECSCNM, DECOM, margins, IRM and SGR off, screen cleared, cursor home. */
export const sgrUnderlineReset = sgrAttrsReset;
/** The attribute suite's screen validation, so attribute runs, stored details and resolved colors must agree. */
export const validateSgrUnderlineScreen = validateSgrAttrsScreen;

/** UnderlineStyle storage names and codes this suite distinguishes (0 = none). */
const styleCodes = Object.freeze({ single: 1, double: 2, curly: 3, dotted: 4, dashed: 5 });

/** The labeled control ladder: every spelling the batch distinguishes, each on its own cell separated by SGR 0, then a
 * neutral cell. Only the pinned spellings are graded; the rest calibrate that the readback can tell styles apart. */
export const sgrUnderlineLadder = Object.freeze([
    { spelling: 'SGR 4', sequence: '\x1b[4m', char: 'P', style: 'single' },
    { spelling: 'SGR 4:1', sequence: '\x1b[4:1m', char: 'Q', style: 'single' },
    { spelling: 'SGR 21', sequence: '\x1b[21m', char: 'R', style: 'double' },
    { spelling: 'SGR 4:2', sequence: '\x1b[4:2m', char: 'S', style: 'double' },
    { spelling: 'SGR 4:3', sequence: '\x1b[4:3m', char: 'T', style: 'curly' },
    { spelling: 'SGR 4:4', sequence: '\x1b[4:4m', char: 'U', style: 'dotted' },
    { spelling: 'SGR 4:5', sequence: '\x1b[4:5m', char: 'V', style: 'dashed' },
]);
/** The final neutral cell of the ladder, written after SGR 0. */
const ladderNeutralChar = 'N';
const ladderFeed = sgrUnderlineLadder.map(entry => entry.sequence + entry.char + '\x1b[0m').join('') + ladderNeutralChar;
/** The clear between groups: SGR off, screen cleared, cursor home. */
const clear = sgrAttrsClear;

/** The pinned sgrProbe note texts are the attribute batch's, byte for byte. */
export { pinnedNoRenderedCellNote, pinnedUnreportedNote };

/** The stored cell projection is the attribute suite's: flags, exact style, neutral-state fields. */
export const sgrUnderlineCellAt = sgrAttrsCellAt;

/** The pinned sgrProbe check of one case, evaluated on the pinned `cell.underline` string (false when none). */
const pinnedStyle = name => cell => cell.underline === name;

/** Why an observed target did not store the contracted style. */
function mismatchNote(contract, target) {
    if (target.underline === false) return `The stored underline attribute was not set after ${contract.spelling}`;
    return `${contract.spelling} stored the ${target.underline} underline style instead of ${contract.style}`;
}

/** The ladder is a valid control when every cell holds its character with no unrelated state and the plain SGR 4
 * cell stores single. Which styles the other spellings store is observed, never required. */
function ladderValid(ladder) {
    if (!Array.isArray(ladder) || ladder.length !== sgrUnderlineLadder.length + 1) return false;
    const spellingsOk = sgrUnderlineLadder.every((entry, index) => ladder[index].char === entry.char
        && othersClear(ladder[index], ['underline']));
    const neutralOk = ladder.at(-1).char === ladderNeutralChar && neutral(ladder.at(-1));
    return spellingsOk && neutralOk && ladder[0].underline === 'single' && ladder[0].underlineStyle === styleCodes.single;
}

/** Whether the ladder shows at least two distinct stored styles, so a mismatch is not just a collapsed readback. */
const ladderDiscriminates = ladder => new Set(ladder.slice(0, -1).map(cell => cell.underlineStyle)).size >= 2;

/** Case builder: neutral baseline, the pinned fixture, an SGR 24 restoration and the labeled ladder. */
const case_ = ({ caseId, sequence, style, spelling }) => {
    const identity = { caseId, style, spelling, styleCode: styleCodes[style] };
    return Object.freeze({
    caseId, probeId: caseId, sequence, style, spelling, styleCode: styleCodes[style],
    pinnedPath: 'termless', pinnedRead: 1,
    pinnedExpected: `${caseId}: attribute applied to the rendered X cell`,
    pinnedCriterion: 'attribute applied to the rendered X cell',
    pinnedCheck: pinnedStyle(style),
    pinnedCheckSource: `cell.underline === "${style}"`,
    pinnedFeeds: Object.freeze([sequence + 'X']),
    plan: Object.freeze([planFeed(sgrUnderlineReset), planFeed('C'), planRead('cells'),
        planFeed(clear), planFeed(sequence + 'X'), planRead('cells'),
        planFeed('\x1b[24mY'), planRead('cells'),
        planFeed(clear), planFeed(ladderFeed), planRead('cells')]),
    terminalFixture: { writes: [sequence + 'X\x1b[0m'], demand: 'consumed (cursor advance does not verify SGR styling)', minimum: { rows: 1, cols: 2 } },
    appearanceScope: `stored underline flag and exact stored ${style}-underline style only; ${style} stroke geometry, thickness, color and rendered appearance are not claimed`,
    grade: observation => gradeUnderline(identity, observation),
    observed: observation => briefUnderline(observation),
    });
};

/** The shared control walk, then the exact stored-style verdict. */
function gradeUnderline(contract, observation) {
    const { baseline, target, trailing, ladder, reverseVideo } = observation;
    if (target.char !== 'X') return { outcome: 'inconclusive', note: pinnedNoRenderedCellNote };
    if (reverseVideo !== false) {
        return { outcome: 'inconclusive', note: 'DECSCNM reverse video was not proven off, so stored attributes cannot be separated from it' };
    }
    if (baseline.char !== 'C' || !neutral(baseline)) {
        return { outcome: 'inconclusive', note: 'Neutral baseline control was not measured (an attribute was initially active)' };
    }
    if (!othersClear(target, ['underline'])) {
        return { outcome: 'inconclusive', note: 'Unrelated attributes or colors drifted on the target, so the setup control is invalid' };
    }
    // SGR 24 must restore: X keeps its exact stored tuple (style included) and Y returns to neutral.
    if (!keepsTarget(trailing, target) || trailing[1].char !== 'Y' || !neutral(trailing[1])) {
        return { outcome: 'inconclusive', note: 'Reset/restoration control cells were not measured as contracted' };
    }
    if (!ladderValid(ladder)) {
        return { outcome: 'inconclusive', note: 'The labeled underline-style control ladder was not measured as contracted' };
    }
    if (target.underline === contract.style && target.underlineStyle === contract.styleCode) return { outcome: 'supported' };
    if (!ladderDiscriminates(ladder)) {
        return { outcome: 'inconclusive', note: 'The readback exposed one stored underline style for every control spelling, so a mismatch cannot be separated from collapsed styles' };
    }
    return { outcome: 'unsupported', note: mismatchNote(contract, target) };
}

/** A compact observed record: stored text, flags, style name and code per cell; the ladder carries its spellings. */
function briefUnderline(observation) {
    const brief = cell => ({ char: cell.char, flags: cell.flags, underline: cell.underline, underlineStyle: cell.underlineStyle });
    return { baseline: brief(observation.baseline), target: brief(observation.target),
        trailing: observation.trailing.map(brief),
        controlLadder: observation.ladder.map((cell, index) => ({ spelling: sgrUnderlineLadder[index]?.spelling ?? 'neutral (after SGR 0)', ...brief(cell) })),
        reverseVideo: observation.reverseVideo };
}

export const sgrUnderlineContracts = Object.freeze([
    case_({ caseId: 'sgr.underline.double', sequence: '\x1b[21m', style: 'double', spelling: 'SGR 21' }),
    case_({ caseId: 'sgr.underline.curly', sequence: '\x1b[4:3m', style: 'curly', spelling: 'SGR 4:3' }),
    case_({ caseId: 'sgr.underline.dotted', sequence: '\x1b[4:4m', style: 'dotted', spelling: 'SGR 4:4' }),
    case_({ caseId: 'sgr.underline.dashed', sequence: '\x1b[4:5m', style: 'dashed', spelling: 'SGR 4:5' }),
]);

/** Contract case ids in suite order. */
export const sgrUnderlineCaseIds = Object.freeze(sgrUnderlineContracts.map(contract => contract.caseId));

/** Project the four read groups of one case into the grader's observation. */
export function sgrUnderlineObservation(_contract, screens) {
    if (!Array.isArray(screens) || screens.length !== 4) throw new Error('Missing SGR-underline read captures.');
    const binding = screens.map(screen => screen.colorBinding.reverseVideo);
    return { baseline: sgrUnderlineCellAt(screens[0], 0, 0), target: sgrUnderlineCellAt(screens[1], 0, 0),
        trailing: [0, 1].map(col => sgrUnderlineCellAt(screens[2], 0, col)),
        ladder: Array.from({ length: sgrUnderlineLadder.length + 1 }, (_, col) => sgrUnderlineCellAt(screens[3], 0, col)),
        reverseVideo: binding.every(value => value === false) ? false : binding.some(value => value === true) ? true : null };
}

/** Independent contract grader entry: mirrors the pinned criteria without the bundle. */
export function gradeSgrUnderlineCase(contract, observation, revision = sgrUnderlineContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown SGR-underline contracts revision ${revision}.`);
    if (!observation?.baseline || !observation.target || !Array.isArray(observation.trailing) || observation.trailing.length !== 2
        || !Array.isArray(observation.ladder) || observation.ladder.length !== sgrUnderlineLadder.length + 1) {
        throw new Error(`Missing ${contract.caseId} read captures.`);
    }
    return contract.grade(observation);
}

export { planFeeds as sgrUnderlineFeeds, planReads as sgrUnderlineReads } from './graded-plan.mjs';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateSgrUnderlineFeeds(contract, feeds) {
    validateExactFeeds(contract, feeds, 'contract');
}
