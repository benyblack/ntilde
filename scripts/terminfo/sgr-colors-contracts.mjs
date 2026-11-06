// Versioned probe contracts for the SGR reset/standard-color qualification
// batch. Every fixture byte below mirrors the pinned terminfo.dev@4.0.0 probe
// definitions (bundle SHA-256 aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6);
// scripts/terminfo/sgr-colors-grading-collector.mjs refuses derivation when
// the pinned bundle no longer contains these exact definitions. Plan helpers
// and the shared flag projection come from graded-plan.mjs and the first
// batch's contracts so this module only declares case-specific facts.
//
// Flag bits of the App readScreen attribute projection (TerminalCellFlags):
// bold 1, italic 4, underline 8, faint 64, default-fg 256, default-bg 512,
// palette-fg 16384, palette-bg 32768.
import { planFeed, planRead, planFeeds, planReads, validateExactFeeds } from './graded-plan.mjs';
import { expandScreenFlags, asciiSgrReset } from './ascii-sgr-contracts.mjs';

export const sgrColorsContractsVersion = 1;
export const sgrColorsSuiteId = 'ntilde-owned-sgr-colors-graded-v1';
export const sgrColorsNoncePrefix = 'NTILDE_SC_';
const sgrColorsReset = asciiSgrReset;
export { sgrColorsReset };

export const sgrColorsFlagBits = Object.freeze({
    bold: 0x0001, italic: 0x0004, underline: 0x0008, faint: 0x0040,
    defaultFg: 0x0100, defaultBg: 0x0200, paletteFg: 0x4000, paletteBg: 0x8000,
});

const runAt = (screen, row, col) => {
    const target = row * screen.cols + col;
    let offset = 0;
    for (const run of screen.attributeRuns ?? []) {
        if (target < offset + run.count) return run;
        offset += run.count;
    }
    return undefined;
};

/** Project one observed cell for the v2 probes: boolean style attributes plus
 * the stored color state. Colors expose no RGB here: a palette index leaves
 * the channel undefined (index set, no RGB exposed), the default marker is
 * null, and literal RGB storage — equally unexposed by this projection — is
 * undefined as well. The pinned named-color criteria therefore treat stored
 * observations as indistinguishable, never as a measured mismatch. */
export const sgrColorsCellAt = (screen, row, col) => {
    const flags = expandScreenFlags(screen)[row * screen.cols + col] ?? 0;
    const paletteFg = (flags & sgrColorsFlagBits.paletteFg) !== 0;
    const paletteBg = (flags & sgrColorsFlagBits.paletteBg) !== 0;
    const defaultFg = (flags & sgrColorsFlagBits.defaultFg) !== 0;
    const defaultBg = (flags & sgrColorsFlagBits.defaultBg) !== 0;
    const run = paletteFg || paletteBg ? runAt(screen, row, col) : undefined;
    return {
        char: (screen.lines[row] ?? ' ')[col] ?? ' ', flags,
        bold: (flags & sgrColorsFlagBits.bold) !== 0,
        dim: (flags & sgrColorsFlagBits.faint) !== 0,
        italic: (flags & sgrColorsFlagBits.italic) !== 0,
        underline: (flags & sgrColorsFlagBits.underline) !== 0,
        fg: paletteFg || !defaultFg ? undefined : null,
        bg: paletteBg || !defaultBg ? undefined : null,
        fgIndex: paletteFg ? (run?.fg ?? -1) : -1,
        bgIndex: paletteBg ? (run?.bg ?? -1) : -1,
    };
};

/** Default-marker predicates per channel: the marker bit set and no palette index. */
export const fgDefaultMarker = cell => (cell.flags & sgrColorsFlagBits.defaultFg) !== 0 && cell.fgIndex === -1;
export const bgDefaultMarker = cell => (cell.flags & sgrColorsFlagBits.defaultBg) !== 0 && cell.bgIndex === -1;
export const defaultColors = cell => fgDefaultMarker(cell) && bgDefaultMarker(cell);

/** No style flag set on the cell. */
const plainStyles = cell => !cell.bold && !cell.dim && !cell.italic && !cell.underline;

/** A measuredReset-style control: chars exposed, baseline unset, setup active. */
function resetControl(baseline, before, active, untouched) {
    return plainStyles(baseline) && defaultColors(baseline)
        && active.every(name => before[name] === true)
        && untouched.every(name => before[name] === false);
}

/** The five pinned cases; each performs one feed and one three-cell read. */
const case_ = (fields) => Object.freeze({
    pinnedExpectation: 'measured',
    plan: Object.freeze([planFeed(sgrColorsReset), planFeed(fields.fixture), planRead('cells')]),
    ...fields,
});

export const sgrColorsContracts = Object.freeze([
    case_({
        caseId: 'sgr.selective-reset.bold', probeId: 'sgr.selective-reset.bold',
        pinnedCriterion: 'measured setup attributes clear on Y and unrelated attributes remain set',
        pinnedControlNote: 'Neutral baseline, styled setup, or reset readback was not measured',
        fixture: 'C\x1b[1;2;3mX\x1b[22mY',
        set: ['bold', 'dim'], preserve: ['italic'],
        terminalFixture: { writes: ['\x1b[1;2;3mX\x1b[22mY'], demand: 'consumed', minimum: { rows: 1, cols: 3 } },
        grade(cells) {
            const [baseline, before, after] = cells;
            if (baseline.char !== 'C' || before.char !== 'X' || after.char !== 'Y') {
                return { outcome: 'inconclusive', note: 'Baseline, styled, or reset cell was not exposed' };
            }
            // Initially-unset control; bold, dim and italic proven active on X
            // (italic is the attribute SGR 22 must preserve).
            if (!resetControl(baseline, before, ['bold', 'dim', 'italic'], ['underline'])) {
                return { outcome: 'inconclusive', note: 'Neutral baseline, styled setup, or reset readback was not measured' };
            }
            const cleared = after.bold === false && after.dim === false;
            const retained = after.italic === true && after.underline === false && defaultColors(after);
            return { outcome: cleared && retained ? 'supported' : 'unsupported' };
        },
        observed(cells) {
            const brief = cell => ({ char: cell.char, bold: cell.bold, dim: cell.dim, italic: cell.italic, flags: cell.flags });
            return { baseline: brief(cells[0]), before: brief(cells[1]), after: brief(cells[2]) };
        },
    }),
    case_({
        caseId: 'sgr.selective-reset.italic', probeId: 'sgr.selective-reset.italic',
        pinnedCriterion: 'measured setup attributes clear on Y and unrelated attributes remain set',
        pinnedControlNote: 'Neutral baseline, styled setup, or reset readback was not measured',
        fixture: 'C\x1b[1;3mX\x1b[23mY',
        set: ['italic'], preserve: ['bold'],
        terminalFixture: { writes: ['\x1b[1;3mX\x1b[23mY'], demand: 'consumed', minimum: { rows: 1, cols: 3 } },
        grade(cells) {
            const [baseline, before, after] = cells;
            if (baseline.char !== 'C' || before.char !== 'X' || after.char !== 'Y') {
                return { outcome: 'inconclusive', note: 'Baseline, styled, or reset cell was not exposed' };
            }
            // Bold is the attribute SGR 23 must preserve here.
            if (!resetControl(baseline, before, ['bold', 'italic'], ['dim', 'underline'])) {
                return { outcome: 'inconclusive', note: 'Neutral baseline, styled setup, or reset readback was not measured' };
            }
            const cleared = after.italic === false;
            const retained = after.bold === true && after.dim === false && after.underline === false && defaultColors(after);
            return { outcome: cleared && retained ? 'supported' : 'unsupported' };
        },
        observed(cells) {
            const brief = cell => ({ char: cell.char, bold: cell.bold, italic: cell.italic, flags: cell.flags });
            return { baseline: brief(cells[0]), before: brief(cells[1]), after: brief(cells[2]) };
        },
    }),
    case_({
        caseId: 'sgr.reset', probeId: 'sgr.reset',
        pinnedCriterion: 'measured setup attributes clear on Y and unrelated attributes remain set',
        pinnedControlNote: 'Neutral baseline, styled setup, or reset readback was not measured',
        fixture: 'C\x1b[1;3;4mX\x1b[0mY',
        set: ['bold', 'italic', 'underline'], preserve: [],
        terminalFixture: { writes: ['\x1b[1;3;4mX\x1b[0mY'], demand: 'consumed', minimum: { rows: 1, cols: 3 } },
        grade(cells) {
            const [baseline, before, after] = cells;
            if (baseline.char !== 'C' || before.char !== 'X' || after.char !== 'Y') {
                return { outcome: 'inconclusive', note: 'Baseline, styled, or reset cell was not exposed' };
            }
            if (!resetControl(baseline, before, ['bold', 'italic', 'underline'], ['dim'])) {
                return { outcome: 'inconclusive', note: 'Neutral baseline, styled setup, or reset readback was not measured' };
            }
            // SGR 0 must clear every contracted attribute and restore the
            // default color markers — not palette index 0 and not literal RGB:
            // default markers, palette indices and literal RGB are distinct
            // storage states.
            const cleared = !after.bold && !after.italic && !after.underline && !after.dim;
            const colorsDefault = defaultColors(after);
            return { outcome: cleared && colorsDefault ? 'supported' : 'unsupported' };
        },
        observed(cells) {
            const brief = cell => ({ char: cell.char, bold: cell.bold, italic: cell.italic, underline: cell.underline,
                dim: cell.dim, defaultColors: defaultColors(cell), flags: cell.flags });
            return { baseline: brief(cells[0]), before: brief(cells[1]), after: brief(cells[2]) };
        },
    }),
    case_({
        caseId: 'sgr.fg.standard', probeId: 'sgr.fg.standard',
        pinnedCriterion: 'the two named colors select distinct exposed fg RGB values',
        pinnedControlNote: 'Baseline, theme colors, or color readback were indistinguishable',
        fixture: 'C\x1b[31mX\x1b[34mY',
        terminalFixture: { writes: ['\x1b[31mX'], demand: 'consumed', minimum: { rows: 1, cols: 2 } },
        // The pinned criterion compares theme-resolved RGB values, which the
        // stored projection deliberately does not expose; executing the pinned
        // code over these observations must yield its own indistinguishable
        // inconclusive, never an unsupported.
        pinnedExpectation: 'inapplicable',
        grade(cells) {
            return gradeNamedColor(cells, 'fg', 'fgIndex', 1, 4, bgDefaultMarker);
        },
        observed(cells) {
            const brief = cell => ({ char: cell.char, fgIndex: cell.fgIndex, bgDefault: bgDefaultMarker(cell), flags: cell.flags });
            return { baseline: brief(cells[0]), first: brief(cells[1]), second: brief(cells[2]) };
        },
        appearanceScope: 'stored palette indices and default markers only; the pinned RGB-appearance criterion is not observable from storage and stays inconclusive',
    }),
    case_({
        caseId: 'sgr.bg.standard', probeId: 'sgr.bg.standard',
        pinnedCriterion: 'the two named colors select distinct exposed bg RGB values',
        pinnedControlNote: 'Baseline, theme colors, or color readback were indistinguishable',
        fixture: 'C\x1b[41mX\x1b[44mY',
        terminalFixture: { writes: ['\x1b[41mX'], demand: 'consumed', minimum: { rows: 1, cols: 2 } },
        pinnedExpectation: 'inapplicable',
        grade(cells) {
            return gradeNamedColor(cells, 'bg', 'bgIndex', 1, 4, fgDefaultMarker);
        },
        observed(cells) {
            const brief = cell => ({ char: cell.char, bgIndex: cell.bgIndex, fgDefault: fgDefaultMarker(cell), flags: cell.flags });
            return { baseline: brief(cells[0]), first: brief(cells[1]), second: brief(cells[2]) };
        },
        appearanceScope: 'stored palette indices and default markers only; the pinned RGB-appearance criterion is not observable from storage and stays inconclusive',
    }),
]);

/** Named-color grading shared by the fg and bg variants: exact stored palette
 * indices, the other channel's default marker preserved, no style flag moved. */
function gradeNamedColor(cells, channel, indexField, firstIndex, secondIndex, otherDefault) {
    const [baseline, first, second] = cells;
    const exposed = channel === 'fg' ? ['C', 'X', 'Y'] : ['C', 'X', 'Y'];
    if (![baseline, first, second].every((cell, index) => cell.char === exposed[index])) {
        return { outcome: 'inconclusive', note: 'Neutral control or named-color cells were not exposed' };
    }
    if (!(defaultColors(baseline) && plainStyles(baseline))) {
        return { outcome: 'inconclusive', note: 'Neutral control cell did not carry default state' };
    }
    // Exact stored palette indices (30+n -> n; 31/41 -> 1, 34/44 -> 4), never
    // inferred from theme-dependent RGB appearance.
    const indices = first[indexField] === firstIndex && second[indexField] === secondIndex;
    const unaffected = otherDefault(first) && otherDefault(second)
        && plainStyles(first) && plainStyles(second);
    return { outcome: indices && unaffected ? 'supported' : 'unsupported' };
}

export const sgrColorsCaseIds = Object.freeze(sgrColorsContracts.map(contract => contract.caseId));

export { planFeeds as sgrColorsFeeds, planReads as sgrColorsReads };

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateSgrColorsFeeds(contract, feeds) {
    validateExactFeeds(contract, feeds, 'contract');
}

/** Independent contract grader: mirrors the pinned criteria without the bundle.
 * Every case reads one screen group; the grader receives its three cells. */
export function gradeSgrColorsCase(contract, cells) {
    if (!Array.isArray(cells) || cells.length !== 3 * planReads(contract)) {
        throw new Error(`Missing ${contract.caseId} read captures.`);
    }
    return contract.grade(cells);
}
