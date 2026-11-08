// Versioned contracts for the App-observed underline-color RESOLUTION suite. These are NOT terminfo.dev
// cases: there is no pinned probe, no stock grade and no `sgr.*` id. Each case is an Ntilde-specific
// extension control recorded beside (never replacing) the three pinned qualifications
// sgr.underline-color-indexed, -rgb and -reset, whose evidence stays parent-derived and unchanged.
//
// The App's opt-in `includeResolvedUnderlineColors` projection reports, per cell, the underline color the
// draw path's own underline stage resolves: none, an explicit palette index through the recorded palette, an
// explicit literal, or the inherited text color of a default marker (foreground, or background under inverse).
// Four things must agree for every cell of every read, and the suite never lets one stand in for another:
//   stored representation   parent-journaled readScreen cell details (style, palette index, literal bytes, default marker);
//   captured binding        the per-read palette/theme state the App reports (mutation and restoration are visible in it);
//   independent expectation computed here from the STORED representation, the run's start binding and the
//                           contracted OSC 4 constants - never from the App's reported underline value;
//   App-observed resolution the App's own `ul` projection, retained separately as `appObserved`.
// A parent calculation is therefore never labelled an App observation: `parentExpected` and `appObserved`
// are distinct fields and the verdict is their comparison.
//
// Stage: the theme-resolution stage shared with the foreground/background projection. Excluded: minimum contrast,
// the faint blend, the foreground override, whether the stroke is painted (hidden, blink phase, overlays), opacity
// compositing and rasterisation. Rendered appearance is never claimed.
//
// Kind of every check here: Ntilde-specific extension contract (not a benchmark criterion).
import { planFeed, planRead, planFeeds, planReads, validateExactFeeds } from './graded-plan.mjs';
import { sgrAttrsReset } from './sgr-attrs-contracts.mjs';
import { sgrColorCellAt, isDefault, isPalette, isLiteral, argbRgb, inconclusive } from './sgr-color-core.mjs';
import { expandResolvedColors, checkStoredColorCoherence, checkUnderlineCoherence } from './resolved-colors.mjs';

/** Contracts v1 is the first measured semantics of this suite. */
export const underlineResolvedContractsVersion = 1;
/** Identity of this suite. */
export const underlineResolvedSuiteId = 'ntilde-owned-underline-color-resolved-v1';
/** The nonce prefix binding worker output to this suite. */
export const underlineResolvedNoncePrefix = 'NTILDE_UR_';
/** The measured reset: the attribute suite's, then every palette override cleared (OSC 104). */
export const underlineResolvedReset = sgrAttrsReset + '\x1b]104\x07';

/** OSC 4 mutation: the two entries the fixtures bind to, and the exact opaque colors they are set to. */
export const underlineResolvedMutation = Object.freeze({ 4: 0xff112233, 5: 0xff445566 });
const mutationIndices = Object.freeze(Object.keys(underlineResolvedMutation).map(Number));
const mutateFeed = '\x1b]4;4;rgb:11/22/33\x07\x1b]4;5;rgb:44/55/66\x07';
const restoreFeed = '\x1b]104;4\x07\x1b]104;5\x07';
/** The default-theme entry 5 the literal-aliasing control relies on (205,0,205). */
const aliasedEntry5 = 0xffcd00cd;

/** Screen validation: stored coherence of the resolved channels, then the underline projection's coherence. */
export function validateUnderlineResolvedScreen(screen) {
    checkStoredColorCoherence(screen, true);
    checkUnderlineCoherence(screen);
}

/** One cell: the stored/resolved-channel projection plus the App's own underline observation, kept separate. */
export function underlineResolvedCellAt(screen, row, col) {
    const cell = sgrColorCellAt(screen, row, col);
    const appObserved = expandResolvedColors(screen.resolvedColors, screen.rows * screen.cols, screen.colorBinding)[row * screen.cols + col].ul;
    if (!appObserved) throw new Error('Missing App-observed underline resolution.');
    return { ...cell, appObserved: { ...appObserved } };
}

const opaqueRgb = value => (0xff000000 | (value & 0xffffff)) >>> 0;
const rgbArgb = ({ r, g, b }) => (0xff000000 | (r << 16) | (g << 8) | b) >>> 0;
const sameRecord = (a, b) => a.source === b.source && a.index === b.index && a.inheritsFrom === b.inheritsFrom && a.argb === b.argb;

/** The independent expectation of one cell: derived from stored fields, a palette state and the start defaults only. */
export function expectedUnderline(cell, palette, defaults) {
    if (cell.underlineStyle === 0) return { source: 'none', index: -1, inheritsFrom: undefined, argb: undefined };
    if (cell.underlineColorIndex >= 0) {
        return { source: 'palette', index: cell.underlineColorIndex, inheritsFrom: undefined, argb: palette[cell.underlineColorIndex] };
    }
    if (cell.underlineArgb !== null) return { source: 'literal', index: -1, inheritsFrom: undefined, argb: opaqueRgb(cell.underlineArgb) };
    // A default marker takes the presented text color: the stored foreground, or the stored background under inverse.
    const channel = cell.inverse ? 'bg' : 'fg';
    const source = cell[channel + 'Source'], index = cell[channel + 'Index'];
    const literal = channel === 'fg' ? cell.foregroundArgb : cell.backgroundArgb;
    const argb = source === 'palette' ? palette[index] : source === 'literal' ? opaqueRgb(literal) : defaults[channel];
    return { source: 'inherit', index: -1, inheritsFrom: cell.inverse ? 'background' : 'foreground', argb };
}

/** A setup cell: stored underline, foreground, background and inverse state, all other attributes clear. */
const spec = (char, ul, { fg = { kind: 'default' }, bg = { kind: 'default' }, inverse = false } = {}) => ({ char, ul, fg, bg, inverse });
const channelOk = (cell, channel, want) => want.kind === 'default' ? isDefault(cell, channel)
    : want.kind === 'palette' ? isPalette(cell, channel, want.index) : isLiteral(cell, channel, want.rgb);
const ulOk = (cell, want) => want.kind === 'none' ? cell.underlineStyle === 0 && cell.underlineColorIndex === -1 && cell.underlineArgb === null
    : cell.underlineStyle === 1 && (want.kind === 'palette' ? cell.underlineColorIndex === want.index && cell.underlineArgb === null
        : want.kind === 'literal' ? cell.underlineColorIndex === -1 && cell.underlineArgb !== null && (cell.underlineArgb & 0xffffff) === ((want.rgb.r << 16) | (want.rgb.g << 8) | want.rgb.b)
            : cell.underlineColorIndex === -1 && cell.underlineArgb === null);
/** Only the contracted attributes: bold, faint, italic, hidden, strikethrough, blink and overline stay clear. */
const quiet = cell => ['bold', 'dim', 'italic', 'hidden', 'strikethrough', 'blink', 'overline'].every(name => cell[name] === false);
const setupValid = (cell, want) => cell.char === want.char && quiet(cell) && Boolean(cell.inverse) === want.inverse
    && ulOk(cell, want.ul) && channelOk(cell, 'fg', want.fg) && channelOk(cell, 'bg', want.bg);

const none = { kind: 'none' }, dflt = { kind: 'default' };
const pal = index => ({ kind: 'palette', index });
const lit = (r, g, b) => ({ kind: 'literal', rgb: { r, g, b } });

/** One contracted case: a stored setup, the shared mutation, and the App/expected comparison. */
function underlineCase(caseId, suffix, fixture, setupCells, criterion, scope, extraValid = () => null) {
    return Object.freeze({
        caseId, kind: 'ntilde-extension-control', check: 'ntilde-extension-contract', suffix, fixture, setup: Object.freeze(setupCells),
        fixtureCells: setupCells.length, pinnedFeeds: Object.freeze([fixture]), mutate: mutateFeed, restore: restoreFeed,
        plan: Object.freeze([planFeed(underlineResolvedReset), planFeed(fixture), planRead('cells'),
            planFeed(mutateFeed), planRead('cells'), planFeed(restoreFeed), planRead('cells')]),
        criterion, scope,
        grade(observation) {
            const { groups, bindings, baseline } = observation;
            const [before, mutated, restored] = groups;
            if (!groups.every(group => group.length === setupCells.length && group.every((cell, position) => cell.char === setupCells[position].char))) {
                return inconclusive('The underline-resolution fixture cells were not exposed');
            }
            if (bindings.some(binding => binding.reverseVideo !== false)) {
                return inconclusive('DECSCNM reverse video was not proven off, so stored colors cannot be separated from it');
            }
            if (!bindingStatesValid(bindings, baseline)) {
                return inconclusive('The OSC 4 palette-mutation control was not applied and restored as contracted, or the theme defaults changed');
            }
            const unprepared = groups.some(group => group.some((cell, position) => !setupValid(cell, setupCells[position])));
            if (unprepared) return inconclusive('The stored underline, foreground, background or inverse setup was not prepared as contracted');
            const sameStored = group => group.every((cell, position) => JSON.stringify(storedKey(cell)) === JSON.stringify(storedKey(before[position])));
            if (!sameStored(mutated) || !sameStored(restored)) {
                return inconclusive('Stored cells changed across the palette mutation reads, so storage and resolution cannot be separated');
            }
            const refused = extraValid(observation);
            if (refused) return inconclusive(refused);
            const defaults = { fg: baseline.foregroundArgb, bg: baseline.backgroundArgb };
            const statePalettes = [baseline.ansiPaletteArgb, mutatedPalette(baseline.ansiPaletteArgb), baseline.ansiPaletteArgb];
            const mismatches = [];
            groups.forEach((group, readIndex) => group.forEach((cell, position) => {
                const expected = expectedUnderline(cell, statePalettes[readIndex], defaults);
                if (!sameRecord(cell.appObserved, expected)) {
                    mismatches.push({ read: ['before', 'mutated', 'restored'][readIndex], cell: setupCells[position].char, parentExpected: expected, appObserved: cell.appObserved });
                }
            }));
            if (mismatches.length === 0) return { outcome: 'supported', layers: { stored: 'supported', appResolved: 'supported' } };
            return { outcome: 'unsupported', layers: { stored: 'supported', appResolved: 'unsupported' },
                note: 'The App-reported underline resolution differs from the independent expectation derived from stored state and the recorded palette', mismatches };
        },
        observed(observation) {
            const brief = (cell, readIndex) => ({ char: cell.char, flags: cell.flags, inverse: cell.inverse,
                underlineStyle: cell.underlineStyle, underlineColorIndex: cell.underlineColorIndex, underlineArgb: cell.underlineArgb,
                fgSource: cell.fgSource, fgIndex: cell.fgIndex, foregroundArgb: cell.foregroundArgb,
                bgSource: cell.bgSource, bgIndex: cell.bgIndex, backgroundArgb: cell.backgroundArgb,
                appObserved: cell.appObserved,
                parentExpected: expectedUnderline(cell, [observation.baseline.ansiPaletteArgb, mutatedPalette(observation.baseline.ansiPaletteArgb), observation.baseline.ansiPaletteArgb][readIndex],
                    { fg: observation.baseline.foregroundArgb, bg: observation.baseline.backgroundArgb }) });
            return { before: observation.groups[0].map(cell => brief(cell, 0)), mutated: observation.groups[1].map(cell => brief(cell, 1)),
                restored: observation.groups[2].map(cell => brief(cell, 2)) };
        },
    });
}

const storedKey = cell => [cell.char, cell.flags, cell.underlineStyle, cell.underlineColorIndex, cell.underlineArgb,
    cell.fgSource, cell.fgIndex, cell.foregroundArgb, cell.bgSource, cell.bgIndex, cell.backgroundArgb, cell.overline];
/** The contracted mutated palette: the start palette with exactly the two entries replaced. */
const mutatedPalette = start => start.map((argb, index) => mutationIndices.includes(index) ? underlineResolvedMutation[index] : argb);

/** Start/mutate/restore: every one of the 256 entries and the override list, plus the theme defaults, across all reads. */
function bindingStatesValid(bindings, baseline) {
    if (baseline.paletteOverrideIndices.length !== 0) return false;
    const [first, second, third] = bindings;
    const theme = binding => JSON.stringify({ ...binding, ansiPaletteArgb: undefined, paletteOverrideIndices: undefined });
    if (bindings.some(binding => theme(binding) !== theme(baseline))) return false;
    const startPalette = JSON.stringify(baseline.ansiPaletteArgb);
    if (JSON.stringify(first.ansiPaletteArgb) !== startPalette || JSON.stringify(third.ansiPaletteArgb) !== startPalette) return false;
    if (JSON.stringify(second.ansiPaletteArgb) !== JSON.stringify(mutatedPalette(baseline.ansiPaletteArgb))) return false;
    // The mutation must be observable: each mutated entry starts at a different color.
    if (mutationIndices.some(index => baseline.ansiPaletteArgb[index] === underlineResolvedMutation[index])) return false;
    return first.paletteOverrideIndices.length === 0 && third.paletteOverrideIndices.length === 0
        && JSON.stringify(second.paletteOverrideIndices) === JSON.stringify(mutationIndices);
}

const indexed = underlineCase('ntilde.underline-color-resolved.indexed', 'indexed',
    '\x1b[4mA\x1b[58;5;5mX\x1b[58;5;4mY\x1b[59mB\x1b[24mZ',
    [spec('A', dflt), spec('X', pal(5)), spec('Y', pal(4)), spec('B', dflt), spec('Z', none)],
    'indexed underline colors resolve to the App palette entry, follow an OSC 4 change of that entry and return after OSC 104; default and absent underlines are unaffected',
    'App-reported resolved underline for stored palette indices 5 and 4, default markers before and after SGR 59 and an absent underline, compared across start, OSC 4 mutation of entries 4 and 5 and OSC 104 restoration; stored cells identical across the reads; rendered appearance is not claimed');
const literal = underlineCase('ntilde.underline-color-resolved.literal', 'literal',
    '\x1b[4mA\x1b[58;2;255;0;128mX\x1b[58;2;1;2;3mY\x1b[58;2;205;0;205mP\x1b[59mB',
    [spec('A', dflt), spec('X', lit(255, 0, 128)), spec('Y', lit(1, 2, 3)), spec('P', lit(205, 0, 205)), spec('B', dflt)],
    'literal underline colors resolve to exactly their stored RGB and do not follow an OSC 4 change, including a literal equal to the default entry 5 color',
    'App-reported resolved underline for stored literal bytes 255,0,128, 1,2,3 and 205,0,205 (equal to the default palette entry 5, an aliasing control), across start, OSC 4 mutation and OSC 104 restoration; rendered appearance is not claimed',
    ({ baseline }) => baseline.ansiPaletteArgb[5] === aliasedEntry5 ? null
        : 'The literal aliasing control needs the default theme entry 5 (205,0,205), which this run did not record');
const inherit = underlineCase('ntilde.underline-color-resolved.default-inheritance', 'default-inheritance',
    '\x1b[4mA\x1b[38;5;5mB\x1b[38;2;10;20;30mC\x1b[7mD\x1b[58;5;4mE\x1b[27;59mF\x1b[0;4;38;5;5;58;5;4mH\x1b[59mI\x1b[0;4;7;38;5;4;48;5;5mJ\x1b[0mG',
    [spec('A', dflt), spec('B', dflt, { fg: pal(5) }), spec('C', dflt, { fg: lit(10, 20, 30) }),
        spec('D', dflt, { fg: lit(10, 20, 30), inverse: true }), spec('E', pal(4), { fg: lit(10, 20, 30), inverse: true }),
        spec('F', dflt, { fg: lit(10, 20, 30) }), spec('H', pal(4), { fg: pal(5) }), spec('I', dflt, { fg: pal(5) }),
        spec('J', dflt, { fg: pal(4), bg: pal(5), inverse: true }), spec('G', none)],
    'a default underline (never set, or restored by SGR 59) inherits the cell\'s presented text color: the foreground, or the background under inverse; an explicit color is untouched by inverse and by the inherited channel',
    'App-reported resolved underline and its inherited channel for default markers over default, palette and literal foregrounds, inverse over default and palette backgrounds, an explicit palette underline under inverse and SGR 59 restoring the default marker, across start, OSC 4 mutation and OSC 104 restoration; faint, minimum contrast and the foreground override are excluded stages; rendered appearance is not claimed');

/** The three cases in suite order. */
export const underlineResolvedContracts = Object.freeze([indexed, literal, inherit]);
/** Contract case ids in suite order. */
export const underlineResolvedCaseIds = Object.freeze(underlineResolvedContracts.map(contract => contract.caseId));

/** Project the three read groups of one case into the grader's observation. */
export function underlineResolvedObservation(contract, screens, baseline) {
    if (!Array.isArray(screens) || screens.length !== 3) throw new Error('Missing underline-resolution read captures.');
    return { groups: screens.map(screen => Array.from({ length: contract.fixtureCells }, (_, col) => underlineResolvedCellAt(screen, 0, col))),
        bindings: screens.map(screen => screen.colorBinding), baseline };
}

/** Independent contract grader entry. */
export function gradeUnderlineResolvedCase(contract, observation, revision = underlineResolvedContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown underline-resolution contracts revision ${revision}.`);
    if (!Array.isArray(observation?.groups) || observation.groups.length !== 3
        || !observation.groups.every(group => Array.isArray(group) && group.length === contract.fixtureCells)
        || !Array.isArray(observation.bindings) || observation.bindings.length !== 3 || !observation.baseline) {
        throw new Error(`Missing ${contract.caseId} read captures.`);
    }
    return contract.grade(observation);
}

export { planFeeds as underlineResolvedFeeds, planReads as underlineResolvedReads };

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateUnderlineResolvedFeeds(contract, feeds) {
    validateExactFeeds(contract, feeds, 'contract');
}

export { argbRgb, rgbArgb };
