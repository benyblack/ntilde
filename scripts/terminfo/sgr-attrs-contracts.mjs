// Versioned probe contracts for the SGR attribute qualification batch:
// sgr.underline.single, sgr.inverse, sgr.faint, sgr.hidden, sgr.strikethrough.
//
// Pinned terminfo.dev@4.0.0 definitions (bundle SHA-256
// aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6), all built
// by the same sgrProbe helper:
//   sgrProbe("sgr.faint", "\x1B[2m", cell => cell.dim === true)
//   sgrProbe("sgr.underline.single", "\x1B[4m", cell => !!cell.underline)
//   sgrProbe("sgr.inverse", "\x1B[7m", cell => cell.inverse === true)
//   sgrProbe("sgr.hidden", "\x1B[8m", cell => cell.hidden === true)
//   sgrProbe("sgr.strikethrough", "\x1B[9m", cell => cell.strikethrough === true)
// Each termless probe feeds `<sequence>X` and reads cell (0,0). The pinned
// underline check accepts ANY truthy underline, so it cannot tell single from
// double/curly/dotted/dashed; this contract additionally requires the stored
// underline style to be exactly single (a documented refinement, never a
// silent substitution). The pinned probes never return unsupported: a failed
// check is the pinned inconclusive ("a negative needs rendering review").
// This contract grader may grade unsupported, but only after valid controls
// establish an observed mismatch.
//
// No pinned criterion requires theme-resolved or rendered appearance, so the
// published scope is the STORED state: attribute flags, the stored underline
// style, stored text and default color storage. Resolved colors are observed
// only to bind DECSCNM (global reverse video) off and to show the stored
// inverse flag did not alter stored color resolution; the inverse-cell swap,
// contrast, compositing and rasterisation are explicitly not claimed.
import { planFeed, planRead, planFeeds, planReads, validateExactFeeds } from './graded-plan.mjs';
import { expandScreenFlags } from './ascii-sgr-contracts.mjs';
import { expandResolvedColors, exposedRgb, checkStoredColorCoherence } from './resolved-colors.mjs';
import { expandCellDetails } from './cell-details.mjs';

/** Contracts v1 is the first measured semantics of this batch. */
export const sgrAttrsContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const sgrAttrsSuiteId = 'ntilde-owned-sgr-attributes-graded-v1';
/** The nonce prefix binding worker output to this suite. */
export const sgrAttrsNoncePrefix = 'NTILDE_SA_';
/** The measured reset every plan begins with: DECSCNM, DECOM, margins, IRM and
 * SGR off, screen cleared, cursor home. */
export const sgrAttrsReset = '\x1b[?5l\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[H';
/** Between the control cell and the pinned fixture: SGR off, clear, home. */
export const sgrAttrsClear = '\x1b[0m\x1b[2J\x1b[H';

/** TerminalCellFlags bits this suite reads. */
export const sgrAttrsFlagBits = Object.freeze({
    bold: 0x0001, inverse: 0x0002, italic: 0x0004, underline: 0x0008, strikethrough: 0x0010,
    blink: 0x0020, faint: 0x0040, hidden: 0x0080,
});
/** UnderlineStyle storage names (0 = none). */
const underlineNames = Object.freeze([false, 'single', 'double', 'curly', 'dotted', 'dashed']);
/** Every boolean attribute the controls hold off unless a case sets it. */
const attributeNames = Object.freeze(['bold', 'dim', 'italic', 'underline', 'inverse', 'hidden', 'strikethrough', 'blink', 'overline']);

/** Project one observed cell to the pinned getCell shape plus the storage the
 * attribute came from. `underline` is the stored style name or false, so the
 * pinned truthy check and this suite's single-style check read one field. */
export function sgrAttrsCellAt(screen, row, col) {
    const position = row * screen.cols + col;
    const flags = expandScreenFlags(screen)[position] ?? 0;
    const detail = expandCellDetails(screen.cellDetails, screen.rows * screen.cols, true)[position];
    const resolved = expandResolvedColors(screen.resolvedColors, screen.rows * screen.cols, screen.colorBinding)[position];
    if (!detail || !resolved) throw new Error(`Missing stored cell evidence at ${row},${col}.`);
    // The attribute runs and the stored cell details are two projections of one cell: every
    // flag must agree and the underline flag must match the stored style, or the evidence
    // contradicts itself and nothing may be graded from it.
    if (detail.flags !== flags || Boolean(flags & sgrAttrsFlagBits.underline) !== Boolean(detail.underlineStyle)) {
        throw new Error(`Contradictory stored cell projections at ${row},${col}.`);
    }
    return {
        char: (screen.lines[row] ?? ' ')[col] ?? ' ', flags,
        bold: (flags & sgrAttrsFlagBits.bold) !== 0,
        dim: (flags & sgrAttrsFlagBits.faint) !== 0,
        italic: (flags & sgrAttrsFlagBits.italic) !== 0,
        underline: underlineNames[detail.underlineStyle] ?? false,
        inverse: (flags & sgrAttrsFlagBits.inverse) !== 0,
        hidden: (flags & sgrAttrsFlagBits.hidden) !== 0,
        strikethrough: (flags & sgrAttrsFlagBits.strikethrough) !== 0,
        blink: (flags & sgrAttrsFlagBits.blink) !== 0,
        overline: detail.overline,
        fg: exposedRgb(resolved.fg), bg: exposedRgb(resolved.bg),
        fgSource: resolved.fg.source, bgSource: resolved.bg.source,
        underlineStyle: detail.underlineStyle,
        // The rest of the stored tuple, so no control can miss drift in a field the projection dropped.
        underlineColorIndex: detail.underlineColorIndex, underlineArgb: detail.underlineArgb ?? null,
        fgIndex: detail.fg, bgIndex: detail.bg,
        foregroundArgb: detail.foregroundArgb ?? null, backgroundArgb: detail.backgroundArgb ?? null,
    };
}

/** Validate one observation screen: attributes, stored details and resolved colors coherent. */
export function validateSgrAttrsScreen(screen) {
    checkStoredColorCoherence(screen, true);
}

/** No attribute set and both channels on default storage. */
const storedNeutral = cell => cell.fgSource === 'default' && cell.bgSource === 'default'
    && cell.fgIndex === -1 && cell.bgIndex === -1 && cell.foregroundArgb === null && cell.backgroundArgb === null;
/** No underline color stored either. */
const underlineColorNeutral = cell => cell.underlineColorIndex === -1 && cell.underlineArgb === null;
const neutral = cell => attributeNames.every(name => cell[name] === false)
    && storedNeutral(cell) && cell.underlineStyle === 0 && underlineColorNeutral(cell);
/** Exactly these attributes set (underline by style), no others, default colors. */
const exactly = (cell, set) => attributeNames.every(name => (name === 'underline'
        ? (cell.underline !== false) === set.includes(name)
        : cell[name] === set.includes(name)))
    && storedNeutral(cell) && underlineColorNeutral(cell);
/** Every attribute OUTSIDE `own` is clear and colors are default: the unrelated-drift control.
 * The case's own attributes are the measured quantity and are deliberately not constrained. */
const othersClear = (cell, own) => attributeNames.every(name => own.includes(name)
        || (name === 'underline' ? cell.underline === false : cell[name] === false))
    && storedNeutral(cell) && underlineColorNeutral(cell)
    && (own.includes('underline') || cell.underlineStyle === 0);

/** The pinned sgrProbe note texts, byte for byte. */
export const pinnedNoRenderedCellNote = 'No rendered X cell to evaluate';
export const pinnedUnreportedNote = 'Cell readback cannot distinguish unsupported styling from unreported attributes or collapsed styles; a negative needs rendering review';

/** Case builder: fixed plan shape control -> clear -> pinned fixture -> trailing control. */
const case_ = (fields) => Object.freeze({
    pinnedPath: 'termless', pinnedRead: 1,
    pinnedExpected: `${fields.caseId}: attribute applied to the rendered X cell`,
    pinnedCriterion: 'attribute applied to the rendered X cell',
    pinnedFeeds: Object.freeze([fields.sequence + 'X']),
    plan: Object.freeze([planFeed(sgrAttrsReset), planFeed('C'), planRead('cells'),
        planFeed(sgrAttrsClear), planFeed(fields.sequence + 'X'), planRead('cells'),
        planFeed(fields.trailing), planRead('cells')]),
    ...fields,
});

/** Shared control walk; `rules` supplies the case-specific attribute expectations. */
function gradeWithControls(observation, rules) {
    const { baseline, target, trailing, reverseVideo } = observation;
    if (target.char !== 'X') return { outcome: 'inconclusive', note: pinnedNoRenderedCellNote };
    if (reverseVideo !== false) {
        return { outcome: 'inconclusive', note: 'DECSCNM reverse video was not proven off, so stored attributes cannot be separated from it' };
    }
    if (baseline.char !== 'C' || !neutral(baseline)) {
        return { outcome: 'inconclusive', note: 'Neutral baseline control was not measured (an attribute was initially active)' };
    }
    if (!rules.unrelatedClear(target)) {
        return { outcome: 'inconclusive', note: 'Unrelated attributes or colors drifted on the target, so the setup control is invalid' };
    }
    const verdict = rules.verdict(target);
    // The reset controls must hold before ANY verdict: an unsupported grade needs the same
    // valid restoration evidence as a supported one, except the SGR 22 pair that only a
    // working attribute can demonstrate (rules.trailingWhenSet).
    const restoredBasic = rules.trailingValid(trailing, target, verdict.outcome === 'supported');
    if (!restoredBasic) {
        return { outcome: 'inconclusive', note: 'Reset/restoration control cells were not measured as contracted' };
    }
    return verdict;
}

/** The trailing cells must keep X unchanged and show the case's specific reset. */
const storedTuple = ['flags', 'underlineStyle', 'overline', 'fgSource', 'bgSource', 'fgIndex', 'bgIndex',
    'foregroundArgb', 'backgroundArgb', 'underlineColorIndex', 'underlineArgb'];
const keepsTarget = (trailing, target) => trailing[0].char === 'X'
    && storedTuple.every(field => trailing[0][field] === target[field]);

export const sgrAttrsContracts = Object.freeze([
    case_({
        caseId: 'sgr.underline.single', probeId: 'sgr.underline.single',
        sequence: '\x1b[4m', trailing: '\x1b[24mY',
        pinnedCheck: cell => !!cell.underline,
        terminalFixture: { writes: ['\x1b[4mX\x1b[0m'], demand: 'consumed (cursor advance does not verify SGR styling)', minimum: { rows: 1, cols: 2 } },
        appearanceScope: 'stored underline flag and stored single-underline style only; rendered stroke appearance is not claimed',
        grade(observation) {
            return gradeWithControls(observation, {
                trailingValid: (trailing, target) => keepsTarget(trailing, target) && trailing[1].char === 'Y' && neutral(trailing[1]),
                unrelatedClear: target => othersClear(target, ['underline']),
                verdict: target => {
                    if (target.underline === 'single' && target.underlineStyle === 1) return { outcome: 'supported' };
                    return { outcome: 'unsupported', note: target.underline === false
                        ? 'The stored underline attribute was not set after SGR 4'
                        : `SGR 4 stored the ${target.underline} underline style instead of single` };
                },
            });
        },
        observed(observation) { return briefObservation(observation, ['underline', 'underlineStyle']); },
    }),
    case_({
        caseId: 'sgr.inverse', probeId: 'sgr.inverse',
        sequence: '\x1b[7m', trailing: '\x1b[27mY',
        pinnedCheck: cell => cell.inverse === true,
        terminalFixture: { writes: ['\x1b[7mX\x1b[0m'], demand: 'consumed (cursor advance does not verify SGR styling)', minimum: { rows: 1, cols: 2 } },
        appearanceScope: 'stored per-cell inverse flag with DECSCNM proven off; the foreground/background swap and rendered appearance are not claimed',
        grade(observation) {
            return gradeWithControls(observation, {
                trailingValid: (trailing, target) => keepsTarget(trailing, target) && trailing[1].char === 'Y' && neutral(trailing[1]),
                unrelatedClear: target => othersClear(target, ['inverse']),
                verdict: target => (target.inverse ? { outcome: 'supported' }
                    : { outcome: 'unsupported', note: 'The stored inverse attribute was not set after SGR 7' }),
            });
        },
        observed(observation) { return briefObservation(observation, ['inverse']); },
    }),
    case_({
        caseId: 'sgr.faint', probeId: 'sgr.faint',
        sequence: '\x1b[2m', trailing: '\x1b[1mY\x1b[22mZ',
        pinnedCheck: cell => cell.dim === true,
        terminalFixture: { writes: ['\x1b[2mX\x1b[0m'], demand: 'consumed (cursor advance does not verify SGR styling)', minimum: { rows: 1, cols: 3 } },
        appearanceScope: 'stored faint flag distinct from bold; SGR 22 clearing both is a control; rendered dimming is not claimed',
        grade(observation) {
            return gradeWithControls(observation, {
                // Y adds bold on top of faint (both stored); Z after SGR 22 has neither. The pair
                // only demonstrates SGR 22 when faint works, so an unsupported target needs only
                // the basic restoration (X unchanged, Y present, Z clear of bold and faint).
                trailingValid: (trailing, target, whenSupported) => keepsTarget(trailing, target)
                    && trailing[1].char === 'Y' && trailing[2].char === 'Z' && neutral(trailing[2])
                    && othersClear(trailing[1], ['bold', 'dim'])
                    && (!whenSupported || exactly(trailing[1], ['bold', 'dim'])),
                unrelatedClear: target => othersClear(target, ['dim']),
                verdict: target => (target.dim && !target.bold ? { outcome: 'supported' }
                    : { outcome: 'unsupported', note: 'The stored faint attribute was not set after SGR 2' }),
            });
        },
        observed(observation) { return briefObservation(observation, ['dim', 'bold'], 3); },
    }),
    case_({
        caseId: 'sgr.hidden', probeId: 'sgr.hidden',
        sequence: '\x1b[8m', trailing: '\x1b[28mY',
        pinnedCheck: cell => cell.hidden === true,
        terminalFixture: { writes: ['\x1b[8mX\x1b[0m'], demand: 'consumed (cursor advance does not verify SGR styling)', minimum: { rows: 1, cols: 2 } },
        appearanceScope: 'stored conceal flag with the stored X text still present; whether the glyph is visibly suppressed is not claimed',
        grade(observation) {
            return gradeWithControls(observation, {
                trailingValid: (trailing, target) => keepsTarget(trailing, target) && trailing[1].char === 'Y' && neutral(trailing[1]),
                unrelatedClear: target => othersClear(target, ['hidden']),
                verdict: target => (target.hidden ? { outcome: 'supported' }
                    : { outcome: 'unsupported', note: 'The stored conceal attribute was not set after SGR 8 although the X text was stored' }),
            });
        },
        observed(observation) { return briefObservation(observation, ['hidden']); },
    }),
    case_({
        caseId: 'sgr.strikethrough', probeId: 'sgr.strikethrough',
        sequence: '\x1b[9m', trailing: '\x1b[29mY',
        pinnedCheck: cell => cell.strikethrough === true,
        terminalFixture: { writes: ['\x1b[9mX\x1b[0m'], demand: 'consumed (cursor advance does not verify SGR styling)', minimum: { rows: 1, cols: 2 } },
        appearanceScope: 'stored strikethrough flag only; rendered strike line is not claimed',
        grade(observation) {
            return gradeWithControls(observation, {
                trailingValid: (trailing, target) => keepsTarget(trailing, target) && trailing[1].char === 'Y' && neutral(trailing[1]),
                unrelatedClear: target => othersClear(target, ['strikethrough']),
                verdict: target => (target.strikethrough ? { outcome: 'supported' }
                    : { outcome: 'unsupported', note: 'The stored strikethrough attribute was not set after SGR 9' }),
            });
        },
        observed(observation) { return briefObservation(observation, ['strikethrough']); },
    }),
]);

/** A compact observed record: stored text, flags and the named fields per read. */
function briefObservation(observation, fields, trailingCells = 2) {
    const brief = cell => ({ char: cell.char, flags: cell.flags, ...Object.fromEntries(fields.map(name => [name, cell[name]])) });
    return { baseline: brief(observation.baseline), target: brief(observation.target),
        trailing: observation.trailing.slice(0, trailingCells).map(brief), reverseVideo: observation.reverseVideo };
}

/** Contract case ids in suite order. */
export const sgrAttrsCaseIds = Object.freeze(sgrAttrsContracts.map(contract => contract.caseId));

/** Project the three read groups of one case into the grader's observation. */
export function sgrAttrsObservation(screens) {
    if (!Array.isArray(screens) || screens.length !== 3) throw new Error('Missing SGR-attribute read captures.');
    const binding = screens.map(screen => screen.colorBinding.reverseVideo);
    return { baseline: sgrAttrsCellAt(screens[0], 0, 0), target: sgrAttrsCellAt(screens[1], 0, 0),
        trailing: [0, 1, 2].map(col => sgrAttrsCellAt(screens[2], 0, col)),
        reverseVideo: binding.every(value => value === false) ? false : binding.some(value => value === true) ? true : null };
}

/** Independent contract grader entry: mirrors the pinned criteria without the bundle. */
export function gradeSgrAttrsCase(contract, observation, revision = sgrAttrsContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown SGR-attributes contracts revision ${revision}.`);
    if (!observation?.baseline || !observation.target || !Array.isArray(observation.trailing) || observation.trailing.length !== 3) {
        throw new Error(`Missing ${contract.caseId} read captures.`);
    }
    return contract.grade(observation);
}

export { planFeeds as sgrAttrsFeeds, planReads as sgrAttrsReads } from './graded-plan.mjs';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateSgrAttrsFeeds(contract, feeds) {
    validateExactFeeds(contract, feeds, 'contract');
}

/** The stored-state predicates and projection constants the attribute contracts use, shared by later layers
 * that grade finer attribute detail (for example underline styles) with identical neutrality semantics. */
export const sgrAttrsPredicates = Object.freeze({ neutral, othersClear, exactly, keepsTarget, storedNeutral, underlineColorNeutral });
