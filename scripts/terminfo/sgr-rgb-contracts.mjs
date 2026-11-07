// Versioned probe contracts for the SGR standard-color RGB-appearance
// qualification. The two cases' fixture bytes mirror the pinned
// terminfo.dev@4.0.0 namedColorProbe definitions (and the stored-index batch's
// fixtures) byte for byte; the criterion here operates on the App-resolved
// per-cell RGB projection (resolved-colors.mjs) rather than stored indices, and
// mirrors the pinned code's own semantics: the exposed values must be
// distinguishable from each other and from the neutral baseline — never an
// unsupported, exactly like the pinned probe.
import { planFeed, planRead, planFeeds, planReads, validateExactFeeds } from './graded-plan.mjs';
import { expandScreenFlags, asciiSgrReset } from './ascii-sgr-contracts.mjs';
import { expandResolvedColors, exposedRgb, checkStoredColorCoherence } from './resolved-colors.mjs';

// Contracts v1 is the measured semantics of this batch: resolution-stage RGB
// over the App's live render theme, distinguishability only.
export const sgrRgbContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const sgrRgbSuiteId = 'ntilde-owned-sgr-rgb-graded-v1';
/** The nonce prefix binding worker output to this suite. */
export const sgrRgbNoncePrefix = 'NTILDE_SR_';
/** The shared measured reset every plan begins with. */
const sgrRgbReset = asciiSgrReset;
export { sgrRgbReset };

/** TerminalCellFlags bits this suite reads. */
export const sgrRgbFlagBits = Object.freeze({
    bold: 0x0001, italic: 0x0004, underline: 0x0008, faint: 0x0040,
    defaultFg: 0x0100, defaultBg: 0x0200, paletteFg: 0x4000, paletteBg: 0x8000,
});

/** Project one observed cell for the RGB probes: the pinned exposed-color shape
 * (fg/bg are resolved {r,g,b} or null for default) plus the storage state each
 * value was resolved from, so storage and appearance travel together. */
export function sgrRgbCellAt(screen, row, col) {
    const flags = expandScreenFlags(screen)[row * screen.cols + col] ?? 0;
    const cells = expandResolvedColors(screen.resolvedColors, screen.rows * screen.cols, screen.colorBinding);
    const cell = cells[row * screen.cols + col];
    if (!cell) throw new Error(`Missing resolved cell at ${row},${col}.`);
    return {
        char: (screen.lines[row] ?? ' ')[col] ?? ' ', flags,
        bold: (flags & sgrRgbFlagBits.bold) !== 0,
        dim: (flags & sgrRgbFlagBits.faint) !== 0,
        italic: (flags & sgrRgbFlagBits.italic) !== 0,
        underline: (flags & sgrRgbFlagBits.underline) !== 0,
        fg: exposedRgb(cell.fg), bg: exposedRgb(cell.bg),
        fgSource: cell.fg.source, bgSource: cell.bg.source,
        fgIndex: cell.fg.source === 'palette' ? cell.fg.index : -1,
        bgIndex: cell.bg.source === 'palette' ? cell.bg.index : -1,
    };
}

/** Validate one RGB observation screen: both projections present and coherent. */
export function validateSgrRgbScreen(screen) {
    checkStoredColorCoherence(screen);
}

/** The pinned sameRgb: exact {r,g,b} equality. */
const sameRgb = (a, b) => a.r === b.r && a.g === b.g && a.b === b.b;
/** The pinned sameCellColor: undefined-aware, null-aware color equality. */
const sameCellColor = (a, b) => {
    if (a === undefined || b === undefined) return false;
    if (a === null || b === null) return a === b;
    return sameRgb(a, b);
};

/** No style flag set on the cell. */
const plainStyles = cell => !cell.bold && !cell.dim && !cell.italic && !cell.underline;
/** Both channels on default storage. */
const bothDefault = cell => cell.fgSource === 'default' && cell.bgSource === 'default';

/** The two pinned cases; each performs one feed and one three-cell read. */
const case_ = (fields) => Object.freeze({
    pinnedExpectation: 'measured',
    plan: Object.freeze([planFeed(sgrRgbReset), planFeed(fields.fixture), planRead('cells')]),
    ...fields,
});

/** The two pinned named-color cases. */
export const sgrRgbContracts = Object.freeze([
    case_({
        caseId: 'sgr.fg.standard', probeId: 'sgr.fg.standard', channel: 'fg', other: 'bg',
        pinnedCriterion: 'the two named colors select distinct exposed fg RGB values',
        pinnedControlNote: 'Baseline, theme colors, or color readback were indistinguishable',
        fixture: 'C\x1b[31mX\x1b[34mY',
        indices: [1, 4],
        terminalFixture: { writes: ['\x1b[31mX'], demand: 'consumed', minimum: { rows: 1, cols: 2 } },
        grade(cells, revision) {
            return gradeNamedColorRgb(cells, this, revision);
        },
        observed(cells) {
            const brief = cell => ({ char: cell.char, fg: cell.fg, fgSource: cell.fgSource, fgIndex: cell.fgIndex,
                bg: cell.bg, flags: cell.flags });
            return { baseline: brief(cells[0]), first: brief(cells[1]), second: brief(cells[2]) };
        },
    }),
    case_({
        caseId: 'sgr.bg.standard', probeId: 'sgr.bg.standard', channel: 'bg', other: 'fg',
        pinnedCriterion: 'the two named colors select distinct exposed bg RGB values',
        pinnedControlNote: 'Baseline, theme colors, or color readback were indistinguishable',
        fixture: 'C\x1b[41mX\x1b[44mY',
        indices: [1, 4],
        terminalFixture: { writes: ['\x1b[41mX'], demand: 'consumed', minimum: { rows: 1, cols: 2 } },
        grade(cells, revision) {
            return gradeNamedColorRgb(cells, this, revision);
        },
        observed(cells) {
            const brief = cell => ({ char: cell.char, bg: cell.bg, bgSource: cell.bgSource, bgIndex: cell.bgIndex,
                fg: cell.fg, flags: cell.flags });
            return { baseline: brief(cells[0]), first: brief(cells[1]), second: brief(cells[2]) };
        },
    }),
]);

/** The independent RGB contract grader: valid controls first (neutral baseline
 * on default storage, named-color cells on exact palette storage, unrelated
 * channel and styles untouched), then the pinned distinguishability criterion.
 * Mirrors the pinned semantics: indistinguishable values stay inconclusive. */
function gradeNamedColorRgb(cells, contract, revision) {
    if (![1].includes(revision)) throw new Error(`Unknown SGR-RGB contracts revision ${revision}.`);
    const [baseline, first, second] = cells;
    const channel = contract.channel;
    if (baseline.char !== 'C' || first.char !== 'X' || second.char !== 'Y') {
        return { outcome: 'inconclusive', note: 'Neutral control or named-color cells were not exposed' };
    }
    const indexField = channel + 'Index', sourceField = channel + 'Source', otherSource = contract.other + 'Source';
    const control = plainStyles(baseline) && bothDefault(baseline)
        && first[sourceField] === 'palette' && second[sourceField] === 'palette'
        && first[indexField] === contract.indices[0] && second[indexField] === contract.indices[1]
        && first[otherSource] === 'default' && second[otherSource] === 'default'
        && plainStyles(first) && plainStyles(second);
    if (!control) {
        return { outcome: 'inconclusive', note: 'Neutral control, palette storage, or resolved readback was not measured' };
    }
    // The pinned criterion over exposed values: baseline neutral (null = default,
    // defined), two resolved RGB values, each distinguishable from the neutral
    // baseline and from each other.
    const neutral = baseline[channel];
    const a = first[channel];
    const b = second[channel];
    if (neutral !== undefined && a && b && !sameCellColor(neutral, a) && !sameRgb(a, b)) {
        return { outcome: 'supported' };
    }
    return { outcome: 'inconclusive', note: contract.pinnedControlNote };
}

/** Contract case ids in suite order. */
export const sgrRgbCaseIds = Object.freeze(sgrRgbContracts.map(contract => contract.caseId));

/** Independent contract grader entry: mirrors the pinned RGB criteria without the bundle. */
export function gradeSgrRgbCase(contract, cells, revision = sgrRgbContractsVersion) {
    if (!Array.isArray(cells) || cells.length !== 3 * planReads(contract)) {
        throw new Error(`Missing ${contract.caseId} read captures.`);
    }
    if (![1].includes(revision)) throw new Error(`Unknown SGR-RGB contracts revision ${revision}.`);
    return contract.grade(cells, revision);
}

export { planFeeds as sgrRgbFeeds, planReads as sgrRgbReads } from './graded-plan.mjs';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateSgrRgbFeeds(contract, feeds) {
    validateExactFeeds(contract, feeds, 'contract');
}
